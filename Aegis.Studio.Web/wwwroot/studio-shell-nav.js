(() => {
    const breadcrumb = document.getElementById('studioBreadcrumb');
    const pageTools = document.getElementById('studioPageTools');
    const shell = document.querySelector('.shell');
    const quickLinks = [
        ['today', 'Today', '▤'],
        ['frontdesk', 'Front Desk', '▣'],
        ['clients', 'Clients', '♧'],
        ['jobs', 'Jobs', '▥'],
        ['production', 'Production', '◆'],
        ['schedule', 'Schedule', '▦'],
        ['media', 'Media', '▧']
    ];
    let currentView = document.body.dataset.studioView || 'today';
    const shellOverviews = new Map();
    const shellContexts = new Map();
    const shellAttention = new Map();
    const overviewKey = () => document.body.dataset.studioView === 'workspaces' && document.querySelector('#conversationView.has-open-job .job-detail-page') ? 'job-detail' : (document.body.dataset.studioView || 'today');
    const defaultOverview = () => `<section class="mw-list-panel studio-context-card studio-overview-rail" data-rail-card="overview"><header><h2>Overview</h2></header><p>${escText(labelFor(document.body.dataset.studioView || 'today'))}</p><p>Use this page to ${escText((pageTaglines[document.body.dataset.studioView || 'today'] || 'manage Studio work').replace(/\.$/, '').toLowerCase())}.</p></section>`;
    let renderingTools = false;
    let workspaceLoadPromise = null;
    let workspaceLoadAttempted = false;
    let productionWorkspaceFilter = null;
    const pageAttention = new Map();
    let sharedSessions = [];
    let sessionsState = 'loading';
    let sessionsRequest = null;
    const currentPage = () => overviewKey();

    const escText = value => String(value ?? '').replace(/[&<>"']/g, character => ({
        '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
    })[character]);

    function labelFor(view) {
        return ({'session-plan':'Session Plan',delivery:'Delivery','studio-management':'Studio Management','job-templates':'Job Templates','work-order-templates':'Work Order Templates'})[view] || quickLinks.find(([key]) => key === view)?.[1]
            || String(view || 'Studio').replace(/(^|\s)\S/g, match => match.toUpperCase());
    }

    // One purpose-driven tagline per page; keep all masthead descriptions here.
    const pageTaglines = {
        today: 'Review your jobs, upcoming sessions and items needing attention.',
        jobs: 'Find, filter and manage jobs across Studio workspaces.',
        tasks: 'Review assignments, deadlines and completed work across Studio.',
        workspaces: 'Manage workspace jobs, messaging, media and activity.',
        clients: 'Manage client jobs, galleries, statements and activity.',
        frontdesk: 'Review client inquiries, prepare bookings and manage arrivals.',
        messages: 'Read, reply to and organize conversations with clients and colleagues.',
        media: 'Upload, review and organize images by workspace, job and collection.',
        production: 'Track production tasks, progress and work awaiting completion.',
        schedule: 'View scheduled shoots, assignments and staff availability.',
        pipeline: 'Track jobs through their workflow stages and next actions.',
        finance: 'Review invoices, payments and outstanding balances.',
        'studio-management': 'Configure Studio settings, staff access and workspaces.'
    };
    const taglineFor = view => pageTaglines[view] || 'Manage Studio work and related information.';

    function iconFor(view) {
        return quickLinks.find(([key]) => key === view)?.[2] || '▤';
    }

    function ensureTools() {
        pageTools?.querySelector('[data-studio-persistent-tools]')?.remove();
    }

    function ensureNavigationToolstrip() {
        const banner = document.querySelector('.topbar');
        if (!banner) return;
        let host = document.getElementById('studioNavigationToolstrip');
        if (!host) {
            host = document.createElement('div');
            host.id = 'studioNavigationToolstrip';
            host.className = 'studio-navigation-toolstrip';
            const tools = document.createElement('nav');
            tools.id = 'studioNavigationTools';
            tools.className = 'studio-navigation-tools';
            tools.setAttribute('aria-label', 'Studio navigation');
            tools.innerHTML = quickLinks.map(([view,label,icon]) => '<button type="button" data-studio-side="'+view+'"><span aria-hidden="true">'+icon+'</span><span>'+label+'</span></button>').join('');
            const scroll = document.createElement('div');
            scroll.className = 'studio-navigation-scroll';
            scroll.append(tools);
            const account = document.createElement('div');
            account.className = 'studio-navigation-account';
            for (const control of [document.getElementById('notificationButton'), document.getElementById('studioMessageBellPanel'), banner.querySelector('.user-menu')]) {
                if (control) account.append(control);
            }
            host.append(scroll, account);
            banner.append(host);
            tools.addEventListener('click', event => {
                const button = event.target.closest('[data-studio-side]');
                if (!button) return;
                window.studioNavigate?.(button.dataset.studioSide);
            });
        }
        host.querySelectorAll('[data-studio-side]').forEach(button => {
            const active = button.dataset.studioSide === currentView;
            if (active) button.setAttribute('aria-current', 'page');
            else button.removeAttribute('aria-current');
        });
    }

    function ensureFooter() {
        if (!shell) return;
        let footer = document.getElementById('studioShellFooter');
        if (!footer) {
            footer = document.createElement('footer');
            footer.id = 'studioShellFooter';
            footer.className = 'studio-shell-footer';
            footer.textContent = "Bryan's Photo Studios · Studio";
            shell.append(footer);
        }
        if (!footer.querySelector('#studioFooterRate')) {
            const count = document.createElement('span');
            count.id = 'studioFooterRate';
            count.className = 'studio-grid-item-count';
            count.setAttribute('role', 'status');
            const workspaces = footer.querySelector('#studioFooterWorkspaces');
            footer.insertBefore(count, workspaces);
        }
    }

    function syncFooterIdentity() {
        const target = document.querySelector('.user-menu-trigger');
        if (!target) return;
        const label = document.getElementById('signedInUser')?.textContent?.trim();
        const avatar = document.getElementById('userAvatar')?.textContent?.trim();
        const identity = label && !/^signed in$/i.test(label) ? label : (avatar && avatar !== '--' ? avatar : 'Studio Administrator - Admin');
        target.title = 'Logged in User: ' + identity;
    }

    function ensureLeftRail() {
        if (!shell) return;
        let rail = document.getElementById('studioContextRail');
        if (!rail) {
            rail = document.createElement('aside');
            rail.id = 'studioContextRail';
            rail.className = 'studio-context-rail';
            rail.setAttribute('aria-label', 'Page navigation');
            shell.prepend(rail);
        }
        renderLeftRail();
        applyRailState();
    }

    function ensureWorkspaceListLoaded() {
        const cache = typeof workspaceCache !== 'undefined' ? workspaceCache : window.workspaceCache;
        if (Array.isArray(cache) && cache.length) return Promise.resolve();
        if (workspaceLoadPromise || workspaceLoadAttempted || typeof loadWorkspaces !== 'function') return workspaceLoadPromise || Promise.resolve();
        workspaceLoadAttempted = true;
        workspaceLoadPromise = Promise.resolve().then(() => loadWorkspaces()).catch(() => {
            workspaceLoadAttempted = false;
        }).finally(() => {
            workspaceLoadPromise = null;
            renderLeftRail();
        });
        return workspaceLoadPromise;
    }

    function renderLeftRail() {
        const rail = document.getElementById('studioContextRail');
        if (!rail) return;
        const cache=typeof workspaceCache!=='undefined'?workspaceCache:window.workspaceCache;
        const workspaces = document.body.dataset.studioView === 'production' && productionWorkspaceFilter
            ? productionWorkspaceFilter.workspaces
            : (Array.isArray(cache) ? cache : []).filter(workspace => workspace.permissions?.accessWorkspace !== false && workspace.permissions?.view !== false);
        const workspaceItems = document.body.dataset.studioView === 'production' && productionWorkspaceFilter
            ? '<button type="button" data-production-workspace="" class="' + (!productionWorkspaceFilter.selected ? 'active' : '') + '" aria-pressed="' + String(!productionWorkspaceFilter.selected) + '"><span>▣</span><span>All Workspaces</span></button>' + workspaces.map(workspace => '<button type="button" data-production-workspace="' + escText(workspace.id) + '" class="' + (String(productionWorkspaceFilter.selected) === String(workspace.id) ? 'active' : '') + '" aria-pressed="' + String(String(productionWorkspaceFilter.selected) === String(workspace.id)) + '"><span>▣</span><span>' + escText(workspace.name || workspace.title || workspace.displayName || 'Workspace') + '</span></button>').join('')
            : workspaces.map(workspace => '<button type="button" data-studio-workspace-id="' + escText(workspace.id) + '"><span>▣</span><span>' + escText(workspace.name) + '</span></button>').join('');
        const scheduleWorkspaceItems=document.body.dataset.studioView==='schedule'?window.StudioScheduleWorkspaceCard?.markup():null;
        // All views share the exact Today rail markup and presentation.
        // Job Detail retains its contextual actions in the shared Actions card.
        const jobPage=document.body.dataset.studioView==='workspaces'?document.querySelector('#conversationView.has-open-job .job-detail-page'):null;
        const jobActions=[['session-plan','Session Plan','▤','ready'],['upload','Upload Photos','⇧','production'],['subject','Add Subject','＋','edit'],['task','Add Task','☑','production'],['edit','Edit Job','✎','edit'],['workflow','Assignments & Workflow','⇥','ready'],['template-workflow','Template Workflow','◇','ready'],['reminders','Client Reminders','◷','ready']];
        const workflowActions=window.StudioWorkflowViews?.actions(document.body.dataset.studioView);
        const actionItems=workflowActions?workflowActions.map(([key,label])=>'<button type="button" data-workflow-action="'+key+'"><span>'+escText(label)+'</span></button>').join(''):jobPage?jobActions.map(([action,title,icon,permission])=>'<button type="button" data-job-action="'+action+'" '+(jobPage.dataset[permission+'Enabled']==='true'?'':'disabled')+'><span>'+icon+'</span><span>'+title+'</span></button>').join(''):'<button type="button" data-studio-new-booking><span>＋</span><span>New Booking</span></button>'+(document.body.dataset.studioView==='today'?'<button type="button" data-studio-assign-job-lead><span>▣</span><span>Assign Job Lead</span></button>':'');
        const financialActions=!workflowActions&&document.body.dataset.studioView==='frontdesk'?[['TakePayment','Take Payment'],['RecordDeposit','Record Deposit'],['IssueRefund','Issue Refund'],['NewSale','New Sale']].map(([action,label])=>'<button type="button" data-studio-financial-action="'+action+'"><span>$</span><span>'+label+'</span></button>').join(''):'';
        const pipelineScheduleAction=document.body.dataset.studioView==='schedule'&&window.history.state?.studioSchedulePanel==='pipeline'?'<button type="button" data-studio-schedule-item><span>▦</span><span>Schedule item</span></button>':'';
        ensureNavigationToolstrip();
        let overview = shellOverviews.get(overviewKey());
        if (!overview) {
            const template = document.createElement('template');
            template.innerHTML = defaultOverview();
            overview = template.content.firstElementChild;
            shellOverviews.set(overviewKey(), overview);
        }
        const context = shellContexts.get(overviewKey()) || [];
        rail.innerHTML = `
            <section class="mw-list-panel studio-context-card" data-rail-card="actions">
                <header><h2><span class="studio-card-icon" aria-hidden="true">✦</span> Actions</h2></header>
                <div class="mw-today-rail-links">
                    ${actionItems}${pipelineScheduleAction}${document.body.dataset.studioView==='clients'?'<button type="button" data-clients-new><span>＋</span><span>New Client</span></button>':''}${financialActions}${!workflowActions&&document.body.dataset.studioView==='frontdesk'?'<button type="button" data-studio-new-client><span>＋</span><span>New Client</span></button><button type="button" data-studio-close-register><span>▤</span><span>Close Register</span></button>':''}
                </div>
            </section>

`;
        rail.prepend(overview);
        window.StudioWorkflowViews?.bind(rail);
        rail.append(...context);
        if(document.body.dataset.studioView === 'frontdesk') window.studioBindFrontDeskOverview?.();
        renderFooterWorkspaceMenu(workspaces);
        if(document.body.dataset.studioView==='schedule')window.StudioScheduleWorkspaceCard?.bindRail(rail);
        rail.querySelector('[data-studio-close-register]')?.addEventListener('click',()=>launchFrontDeskPos('CloseRegister').catch(error=>alert(error.message)));
        rail.querySelector('[data-studio-new-client]')?.addEventListener('click',()=>showFrontDeskClients(true).catch(error=>alert(error.message)));
        rail.querySelectorAll('[data-studio-financial-action]').forEach(button=>button.onclick=()=>pickFrontDeskFinancialAction(button.dataset.studioFinancialAction).catch(error=>alert(error.message)));
        rail.querySelector('[data-studio-view-all-workspaces]')?.addEventListener('click',()=>window.studioNavigate?.('workspaces'));
        rail.querySelector('[data-studio-new-booking]')?.addEventListener('click',()=>document.body.dataset.studioView==='clients'?window.StudioClients?.newBooking():window.studioLaunchNewBooking?.());
        rail.querySelector('[data-studio-schedule-item]')?.addEventListener('click',()=>window.dispatchEvent(new CustomEvent('studio-schedule-item')));
        rail.querySelector('[data-clients-new]')?.addEventListener('click',()=>window.StudioClients?.newClient());
        rail.querySelector('[data-studio-assign-job-lead]')?.addEventListener('click',()=>document.getElementById('genericView')?.dispatchEvent(new CustomEvent('studio-assign-job-lead')));
        rail.querySelectorAll('[data-job-action]').forEach(button => button.onclick=()=>window.dispatchEvent(new CustomEvent('studio-job-action',{detail:{action:button.dataset.jobAction}})));
        rail.querySelectorAll('[data-production-workspace]').forEach(button => {
            button.onclick = () => productionWorkspaceFilter?.onSelect?.(button.dataset.productionWorkspace || '');
        });
        rail.querySelectorAll('[data-studio-workspace-id]').forEach(button => {
            button.onclick = () => {
                const id = button.dataset.studioWorkspaceId;
                const workspace = workspaces.find(item => String(item.id) === id);
                if (typeof window.selectWorkspace === 'function') window.selectWorkspace(id, workspace?.name || '');
                else window.studioNavigate?.('workspaces');
            };
        });

    }


    function renderFooterWorkspaceMenu(workspaces) {
        const footer = document.getElementById('studioShellFooter');
        if (!footer) return;
        let control = document.getElementById('studioFooterWorkspaces');
        if (!control || control.tagName !== 'SELECT') {
            control?.remove();
            control = document.createElement('select');
            control.id = 'studioFooterWorkspaces';
            control.className = 'studio-footer-workspaces';
            control.setAttribute('aria-label', 'Select workspace');
            footer.append(control);
        }
        const isProduction = document.body.dataset.studioView === 'production' && productionWorkspaceFilter;
        const savedSelection = window.sessionStorage.getItem('studio.footer.workspace.selection') || '';
        const selectedId = isProduction ? String(productionWorkspaceFilter.selected || '') : savedSelection;
        control.replaceChildren();
        const all = new Option('Workspaces: All', '');
        control.add(all);
        workspaces.forEach(workspace => control.add(new Option(workspace.name || workspace.title || 'Workspace', String(workspace.id))));
        control.value = workspaces.some(workspace => String(workspace.id) === selectedId) ? selectedId : '';
        control.onchange = () => {
            const id = control.value;
            if (!isProduction) window.sessionStorage.setItem('studio.footer.workspace.selection', id);
            if (isProduction) {
                productionWorkspaceFilter.onSelect?.(id);
            } else if (id) {
                const workspace = workspaces.find(item => String(item.id) === id);
                if (typeof window.selectWorkspace === 'function') window.selectWorkspace(id, workspace?.name || '');
                else window.studioNavigate?.('workspaces');
            } else {
                window.studioNavigate?.('workspaces');
            }
        };
    }

    const railStorage = 'aegis.studio.rail-collapse.v1';
    function railState() {
        try { return JSON.parse(localStorage.getItem(railStorage) || '{}'); } catch { return {}; }
    }
    function setRailState(side, collapsed) {
        const state = railState(); state.left = collapsed; delete state.right;
        try { localStorage.setItem(railStorage, JSON.stringify(state)); } catch {}
        applyRailState();
    }
    function railButton(side, collapsed) {
        const button = document.createElement('button');
        button.type = 'button'; button.className = 'studio-rail-toggle';
        button.dataset.railToggle = side;
        button.setAttribute('aria-expanded', String(!collapsed));
        button.setAttribute('aria-label', (collapsed ? 'Expand ' : 'Collapse ') + side + ' rail');
        button.title = button.getAttribute('aria-label');
        button.innerHTML = '<svg viewBox="0 0 24 24" aria-hidden="true" focusable="false"><path d="m9 18 6-6-6-6"/></svg>';
        button.dataset.chevronDirection = collapsed ? 'right' : 'left';
        button.onclick = () => setRailState(side, !railState()[side]);
        return button;
    }
    function placeRailToggle(rail, side, collapsed) {
        let toggle = shell.querySelector('[data-rail-toggle="' + side + '"]');
        if (!toggle) toggle = railButton(side, collapsed);
        toggle.classList.add('studio-shell-rail-toggle');
        if (toggle.parentElement !== shell) shell.append(toggle);
        return toggle;
    }
    function applyRailState() {
        const state = railState();
        const leftTargets = [document.getElementById('studioContextRail'), document.querySelector('.mw-today-left')].filter(Boolean);
        leftTargets.forEach(rail => {
            rail.classList.toggle('studio-rail-collapsed-left', !!state.left);
            rail.dataset.shellRailReadyLeft = 'true';
            const toggle = placeRailToggle(rail, 'left', !!state.left);
            toggle.setAttribute('aria-expanded', String(!state.left));
            toggle.setAttribute('aria-label', (state.left ? 'Expand' : 'Collapse') + ' left rail');
            toggle.title = toggle.getAttribute('aria-label');
            toggle.dataset.chevronDirection = state.left ? 'right' : 'left';
        });
    }

    const railObserver = new MutationObserver(() => {
        const leftSelector = '#studioContextRail, .mw-today-left';
        const missingLeft = Array.from(document.querySelectorAll(leftSelector))
            .some(rail => rail.dataset.shellRailReadyLeft !== 'true');
        if (missingLeft) applyRailState();
    });
    railObserver.observe(document.body, { childList: true, subtree: true });
    function ensureBreadcrumb() {
        if (!breadcrumb || breadcrumb.children.length) return;
        window.studioSetBreadcrumb?.([{ label: labelFor(currentView), view: currentView }]);
    }

    window.studioSetBreadcrumb = items => {
        if (!breadcrumb) return;
        const supplied = Array.isArray(items) ? items.filter(Boolean) : [];
        const crumbs = supplied[0]?.view === 'today'
            ? supplied
            : [{ label: 'Today', view: 'today' }, ...supplied];
        breadcrumb.innerHTML = '<span class="breadcrumb-label">Navigation History</span>' + crumbs.map((item, index) => {
            const separator = index ? '<span class="breadcrumb-separator" aria-hidden="true">›</span>' : '';
            const last = index === crumbs.length - 1;
            if (last) return separator + '<strong>' + escText(item.label) + '</strong>';
            return separator + '<button type="button" data-studio-crumb="' + escText(item.view || '') + '"' +
                (item.workspaceId ? ' data-studio-workspace="' + escText(item.workspaceId) + '"' : '') + '>' +
                escText(item.label) + '</button>';
        }).join('');
        breadcrumb.querySelectorAll('[data-studio-crumb]').forEach(button => {
            button.onclick = () => {
                if (button.dataset.studioWorkspace && typeof selectWorkspace === 'function') {
                    const cache=typeof workspaceCache!=='undefined'?workspaceCache:window.workspaceCache;
                    const workspace = (cache || []).find(item => String(item.id) === String(button.dataset.studioWorkspace));
                    return selectWorkspace(button.dataset.studioWorkspace, workspace?.name || button.textContent);
                }
                if (button.dataset.studioCrumb) window.studioNavigate?.(button.dataset.studioCrumb);
            };
        });
    };

    window.studioShellMountAttention = (content, view = overviewKey()) => {
        shellAttention.set(view, content || null);
        refreshPageDrawers();
    };

    // Production supplies its workspace filter data to the shared Workspaces card.
    // Other views continue to use the permission-scoped workspace cache.
    window.studioShellMountContext = (nodes, view = overviewKey()) => {
        const cards = Array.from(nodes || []);
        for (const card of cards) {
            card.classList.add('studio-context-card');
            card.dataset.railCard = 'context';
        }
        shellContexts.set(view, cards);
        if (view === overviewKey()) renderLeftRail();
    };

    window.StudioShell={
        setProductionWorkspaceFilter(workspaces, selected, onSelect) {
            productionWorkspaceFilter = { workspaces: Array.isArray(workspaces) ? workspaces : [], selected: selected || '', onSelect };
            if (document.body.dataset.studioView === 'production') renderLeftRail();
        },
        sync(view=document.body.dataset.studioView||currentView){
        currentView=view;
        ensureTools();
        initializeAttentionDrawer();
        refreshPageDrawers();
        ensureLeftRail();
        ensureWorkspaceListLoaded();
        syncPageFrame();
        applyRailState();
        syncGridFooterCount();
        if(view!=='today'&&!breadcrumb?.querySelector('[data-studio-crumb="today"]')&&!breadcrumb?.querySelector('[data-shell-nav="today"]')){
            window.studioSetBreadcrumb([{label:labelFor(view),view}]);
        }
    }};

    function syncGridFooterCount() {
        const view = document.body.dataset.studioView || currentView;
        if (view === 'workspaces' && document.querySelector('#conversationView.has-open-job .job-detail-page')) {
            const count=document.getElementById('studioFooterRate'),page=document.querySelector('.job-detail-page');if(count)count.textContent=page.dataset.mediaCount!==undefined?page.dataset.mediaCount+' files · '+page.dataset.subjectCount+(page.dataset.subjectCount==='1'?' subject':' subjects'):'1 Job';return;
        }
        if (view === 'today' || view === 'jobs' || view === 'frontdesk') return;
        const footer = document.getElementById('studioFooterRate');
        const root = document.getElementById('genericView');
        if (!footer || !root || root.classList.contains('hidden')) return;
        const count=root.querySelector('.st-page')?.dataset.itemCount;
        if(count!==undefined){footer.textContent=count;return;}
        const grids = [
            { selector: '[data-job-row], .mw-job-row', noun: 'job' },
            { selector: '[data-workspace-row]', noun: 'workspace' },
            { selector: '[data-task-row]', noun: 'task' },
            { selector: '[data-production-row]', noun: 'production item' },
            { selector: '[data-booking-row]', noun: 'booking' },
            { selector: '[data-message-row]', noun: 'message' },
            { selector: 'tbody tr', noun: ({workspaces:'workspace',production:'production item',frontdesk:'booking',finance:'transaction',messages:'message',admin:'record',pipeline:'job',schedule:'session',tasks:'task'})[view] || 'item' }
        ];
        const visible = node => !node.hidden && !node.closest('[hidden]') && getComputedStyle(node).display !== 'none';
        for (const grid of grids) {
            const rows = Array.from(root.querySelectorAll(grid.selector)).filter(visible);
            if (!rows.length) continue;
            const noun = typeof grid.noun === 'function' ? grid.noun() : grid.noun;
            const label = (noun + (rows.length === 1 ? '' : 's')).replace(/\b\w/g, letter => letter.toUpperCase());
            footer.textContent = rows.length + ' ' + label;
            return;
        }
        footer.textContent = '';
    }

    // All non-Today views use the same masthead and content frame as Today.
    // The Today route is deliberately excluded from these structural changes.
    function syncPageFrame() {
        const view = document.body.dataset.studioView || currentView;
        const isToday = view === 'today';

        const main = shell?.querySelector('main');
        const masthead = document.querySelector('.topbar');
        const title = document.getElementById('studioTodayBrandTitle');
        const bell = document.getElementById('notificationButton');
        if (!main || !masthead || !bell) return;

        // Every view, including Today, uses the same shared masthead and frame sync.
        document.getElementById('studioSharedPageHeading')?.remove();
        const footer = document.getElementById('studioShellFooter');
        if (footer && breadcrumb && breadcrumb.parentElement !== footer) footer.prepend(breadcrumb);
        if (title) {
            const jobOpen = view === 'workspaces' && !document.getElementById('conversationView')?.classList.contains('hidden') && !!document.querySelector('#conversationView.has-open-job .job-detail-page');
            const pageLabel = jobOpen ? 'Job Details' : labelFor(view);
            const heading = title.querySelector('strong');
            if (heading) {
                if (isToday) heading.innerHTML = 'Today <em>Your focus</em>';
                else heading.replaceChildren(document.createTextNode(pageLabel));
            }
            title.setAttribute('aria-label', pageLabel);
            const icon = title.querySelector('.mw-today-focus-icon');
            if (icon) icon.innerHTML = view === 'jobs' ? '<svg viewBox="0 0 24 24" width="25" height="25" fill="none" stroke="currentColor" stroke-width="1.7" aria-hidden="true"><path d="M4 6h16v14H4zM8 6V3h8v3M4 11h16M10 11v3h4v-3"/></svg>' : jobOpen ? '<svg viewBox="0 0 24 24" width="25" height="25" fill="none" stroke="currentColor" stroke-width="1.7" aria-hidden="true"><path d="M4 6h4l2-3h4l2 3h4v14H4z"/><circle cx="12" cy="13" r="4"/></svg>' : '✦';
            const tagline = title.querySelector('small');
            if (tagline) tagline.textContent = isToday ? taglineFor('today') : jobOpen ? 'Review job details, assignments, workflow, media and conversations.' : taglineFor(view);
        }
        main.classList.add('studio-standard-main');
    }

    function initializeAttentionDrawer() {
        ensureNavigationToolstrip();
        const toolstrip = document.getElementById('studioNavigationToolstrip');
        if (!toolstrip || document.getElementById('studioAttentionTrigger')) return;
        const trigger = document.createElement('button');
        trigger.type = 'button';
        trigger.id = 'studioAttentionTrigger';
        trigger.className = 'studio-attention-trigger';
        trigger.setAttribute('aria-controls', 'studioAttentionDrawer');
        trigger.setAttribute('aria-expanded', 'false');
        trigger.innerHTML = '<span>Needs Attention</span><strong id="studioAttentionCount" aria-label="Attention items">0</strong><span aria-hidden="true">⌄</span>';
        const sessionsTrigger = document.createElement('button');
        sessionsTrigger.type = 'button';
        sessionsTrigger.id = 'studioSessionsTrigger';
        sessionsTrigger.className = 'studio-attention-trigger studio-sessions-trigger';
        sessionsTrigger.setAttribute('aria-controls', 'studioSessionsDrawer');
        sessionsTrigger.setAttribute('aria-expanded', 'false');
        sessionsTrigger.innerHTML = '<span>Upcoming Sessions</span><strong id="studioSessionsCount">0</strong><span aria-hidden="true">⌄</span>';
        const statusTools = document.createElement('div');
        statusTools.className = 'studio-navigation-status';
        statusTools.append(sessionsTrigger, trigger);
        toolstrip.querySelector('.studio-navigation-scroll').append(statusTools);
        const backdrop = document.createElement('div');
        backdrop.className = 'studio-attention-backdrop';
        backdrop.hidden = true;
        const drawer = document.createElement('aside');
        drawer.id = 'studioAttentionDrawer';
        drawer.className = 'studio-attention-drawer';
        drawer.setAttribute('role', 'dialog');
        drawer.setAttribute('aria-modal', 'true');
        drawer.setAttribute('aria-label', 'Needs Attention');
        drawer.hidden = true;
        drawer.innerHTML = '<header><h2>Needs Attention</h2><button type="button" data-attention-close aria-label="Close Needs Attention">×</button></header><div id="studioAttentionDetails" class="studio-attention-details"><div id="studioAttentionItems"></div><div id="studioPageAttention"></div></div>';
        const sessionsDrawer = document.createElement('aside');
        sessionsDrawer.id = 'studioSessionsDrawer';
        sessionsDrawer.className = 'studio-attention-drawer';
        sessionsDrawer.setAttribute('role', 'dialog');
        sessionsDrawer.setAttribute('aria-modal', 'true');
        sessionsDrawer.setAttribute('aria-label', "Upcoming Sessions");
        sessionsDrawer.hidden = true;
        sessionsDrawer.innerHTML = '<header><h2>Upcoming Sessions</h2><button type="button" data-sessions-close aria-label="Close sessions">×</button></header><p class="studio-sessions-range">Next 30 days · Local time</p><div id="studioSessionsDetails" class="studio-attention-details"></div><button type="button" data-sessions-calendar>View Calendar →</button>';
        document.body.append(backdrop, drawer, sessionsDrawer);
        // Page-specific attention details are hosted exclusively by this shell drawer.
        const jobAttentionSlot=document.createElement('div');
        jobAttentionSlot.id='studioShellJobAttention';
        drawer.querySelector('#studioAttentionDetails').append(jobAttentionSlot);
        window.studioShellMountJobAttention=(content)=>{
            jobAttentionSlot.replaceChildren();
            if(content)jobAttentionSlot.append(content);
            refreshPageDrawers();
        };
        const attentionObserver=new MutationObserver(refreshPageDrawers);
        attentionObserver.observe(document.getElementById('studioPageAttention'),{childList:true,subtree:true,characterData:true,attributes:true});
        attentionObserver.observe(jobAttentionSlot,{childList:true,subtree:true,characterData:true,attributes:true});

        const close = () => {
            drawer.hidden = true;
            sessionsDrawer.hidden = true;
            sessionsTrigger.setAttribute('aria-expanded', 'false');
            backdrop.hidden = true;
            trigger.setAttribute('aria-expanded', 'false');
            trigger.focus();
        };
        trigger.onclick = () => {
            const opening = drawer.hidden;
            sessionsDrawer.hidden = true;
            sessionsTrigger.setAttribute('aria-expanded', 'false');
            drawer.hidden = !opening;
            backdrop.hidden = !opening;
            trigger.setAttribute('aria-expanded', String(opening));
            if (opening) drawer.querySelector('[data-attention-close]').focus();
        };
        sessionsTrigger.onclick = () => {
            const opening = sessionsDrawer.hidden;
            drawer.hidden = true;
            trigger.setAttribute('aria-expanded', 'false');
            sessionsDrawer.hidden = !opening;
            backdrop.hidden = !opening;
            sessionsTrigger.setAttribute('aria-expanded', String(opening));
            if (opening) {
                sessionsDrawer.querySelector('[data-sessions-close]').focus();
                loadSharedSessions();
            }
        };
        sessionsDrawer.querySelector('[data-sessions-close]').onclick = close;
        sessionsDrawer.querySelector('[data-sessions-calendar]').onclick = () => { close(); window.studioNavigate?.('schedule'); };
        sessionsDrawer.addEventListener('click', event => {
            const job = event.target.closest('[data-open-job]');
            if (!job) return;
            close();
            const id = job.dataset.openJob;
            if (id) window.studioNavigate?.('workspaces', {jobId:id});
        });
        backdrop.onclick = close;
        drawer.querySelector('[data-attention-close]').onclick = close;
        document.addEventListener('keydown', event => {
            if (event.key === 'Escape' && (!drawer.hidden || !sessionsDrawer.hidden)) close();
        });
        drawer.addEventListener('click', event => {
            const link = event.target.closest('[data-attention-job],[data-attention-route]');
            if (!link) return;
            close();
            if(link.dataset.attentionJob)window.studioNavigate?.('workspaces',{jobId:link.dataset.attentionJob});
            else window.studioNavigate?.(link.dataset.attentionRoute);
        });
        window.addEventListener('studio-attention-updated', event => {
            const data = event.detail || {};
            pageAttention.set(data.view || currentPage(), data);
            refreshPageDrawers();
        });

    }


    function refreshPageDrawers() {
        const key = currentPage();
        const data = pageAttention.get(key) || {};
        const items=Array.isArray(data.items)?data.items:[];
        const pageSlot=document.getElementById('studioPageAttention');
        const content=shellAttention.get(key);
        if(pageSlot&&pageSlot.firstElementChild!==(content||null))pageSlot.replaceChildren(...(content?[content]:[]));
        const jobSlot=document.getElementById('studioShellJobAttention');
        const jobContent=key==='job-detail'?jobSlot?.firstElementChild:null;
        if(jobSlot&&jobSlot.hidden!==(key!=='job-detail'))jobSlot.hidden=key!=='job-detail';
        const countContent=node=>{
            if(node.dataset.attentionCount!==undefined)return Number(node.dataset.attentionCount)||0;
            return node.querySelectorAll('[data-attention-item],button,[role=alert]').length;
        };
        const total = content?countContent(content):jobContent?countContent(jobContent):items.length;
        const count = document.getElementById('studioAttentionCount');
        if (count) count.textContent = String(total);
        document.getElementById('studioAttentionTrigger')?.classList.toggle('has-attention', total > 0);
        const details = document.getElementById('studioAttentionItems');
        if(details)details.hidden=!!content||!!jobContent;
        if (details && !details.hidden) {
            details.replaceChildren();
            if(data.error){const error=document.createElement('p');error.setAttribute('role','alert');error.textContent='Unable to load some attention items. Refresh Today to retry.';details.append(error);}
            if (!total&&!data.error) {
                const p = document.createElement('p');
                p.textContent = data.loaded?'Nothing currently needs attention.':'Attention items have not loaded for this page.';
                details.append(p);
            }
            items.forEach(item => {
                const button = document.createElement('button');
                button.type = 'button';
                button.dataset.attentionItem = item.id;
                if(item.jobId)button.dataset.attentionJob = item.jobId;
                else button.dataset.attentionRoute = 'tasks';
                const name = document.createElement('strong');name.textContent = item.title;
                const context = document.createElement('span');context.textContent = item.context || '';
                button.append(name, context);details.append(button);
            });
        }
        const sessions = sharedSessions;
        const sessionCount = document.getElementById('studioSessionsCount');
        if (sessionCount) sessionCount.textContent = sessionsState === 'ready' ? String(sessions.length) : '…';
        const sessionDetails = document.getElementById('studioSessionsDetails');
        if (sessionDetails) {
            sessionDetails.replaceChildren();
            if (sessionsState !== 'ready' || !sessions.length) {
                const empty = document.createElement('p');
                empty.textContent = sessionsState === 'loading' ? 'Loading sessions…' : sessionsState === 'error' ? 'Unable to load sessions. Close and reopen to retry.' : 'No upcoming sessions in the next 30 days.';
                sessionDetails.append(empty);
            }
            if(sessionsState==='ready'&&sessions.length){
                const table=document.createElement('table');table.className='studio-sessions-grid';table.setAttribute('aria-label','Upcoming sessions');
                table.innerHTML='<thead><tr><th scope="col">Date</th><th scope="col">Time</th><th scope="col">Session</th><th scope="col">Location</th></tr></thead><tbody></tbody>';
                const body=table.querySelector('tbody');
                for(const item of sessions){
                    const row=document.createElement('tr');row.dataset.sessionId=item.id;
                    const start=new Date(item.startsAtUtc||item.start||item.startsAt);
                    for(const value of [start.toLocaleDateString([],{month:'short',day:'numeric'}),start.toLocaleTimeString([],{hour:'numeric',minute:'2-digit'}),item.jobTitle||item.title||'Photography session',item.location||'Not set']){
                        const cell=document.createElement('td');cell.textContent=value;row.append(cell);
                    }
                    body.append(row);
                }
                sessionDetails.append(table);
            }
        }
    }

    function loadSharedSessions() {
        if (sessionsRequest) return sessionsRequest;
        sessionsState = 'loading';
        refreshPageDrawers();
        sessionsRequest = readSharedSessions().finally(() => { sessionsRequest = null; });
        return sessionsRequest;
    }

    async function readSharedSessions() {
        const from = new Date();
        const to = new Date(from);
        to.setDate(to.getDate()+30);
        try {
            const response = await fetch('/api/today/schedule?from='+encodeURIComponent(from.toISOString())+'&to='+encodeURIComponent(to.toISOString()),{credentials:'same-origin'});
            if (!response.ok) throw Error('Schedule request failed');
            const data = await response.json();
            if (!Array.isArray(data)) throw Error('Unexpected schedule response');
            sharedSessions = data.filter(item => {
                const start = new Date(item.startsAtUtc || item.start || item.startsAt);
                const end = new Date(item.endsAtUtc || item.end || item.endsAt);
                return !['Cancelled','Completed'].includes(item.status) && start >= from && start < to && end > start;
            });
            sharedSessions.sort((a,b) => new Date(a.startsAtUtc || a.start || a.startsAt) - new Date(b.startsAtUtc || b.start || b.startsAt));
            sessionsState = 'ready';
        } catch (error) {
            sessionsState = 'error';
            console.error('[Studio sessions]',error);
        }
        refreshPageDrawers();
    }

    function initialize() {
        currentView = document.body.dataset.studioView || currentView;
        ensureTools();
        ensureFooter();
        ensureLeftRail();
        ensureWorkspaceListLoaded();
        syncFooterIdentity();
        const identityNode = document.getElementById('signedInUser');
        if (identityNode) new MutationObserver(syncFooterIdentity).observe(identityNode, {childList:true,characterData:true,subtree:true});
        ensureBreadcrumb();
        syncPageFrame();
        initializeAttentionDrawer();
        refreshPageDrawers();
        loadSharedSessions();
        syncGridFooterCount();
        window.addEventListener('studio-jobs-view-changed',()=>{renderLeftRail();syncPageFrame();});
        window.addEventListener('studio-workflow-capabilities',renderLeftRail);
        window.addEventListener('studio-workspaces-loaded',renderLeftRail);
        document.querySelector('[data-studio-avatar-activity]')?.addEventListener('click', () => {
            document.querySelector('.user-menu')?.removeAttribute('open');
            if (document.body.dataset.studioView==='workspaces' && document.querySelector('#conversationView:not(.hidden).has-open-job .job-detail-page')) {
                window.dispatchEvent(new CustomEvent('studio-job-action',{detail:{action:'activity'}}));
            } else window.studioNavigate?.('notifications');
        });
        document.querySelector('[data-studio-avatar-notifications]')?.addEventListener('click', () => {
            document.querySelector('.user-menu')?.removeAttribute('open');
            window.studioNavigate?.('notifications');
        });
        // One shell contract for Overview on every route.
        window.studioShellSetOverview = (markup, view = document.body.dataset.studioView) => {
            const template = document.createElement('template');
            if (!markup || typeof markup === 'string') template.innerHTML = markup || defaultOverview();
            const overview = markup?.nodeType === 1 ? markup : template.content.firstElementChild;
            overview.classList.add('studio-context-card');
            overview.dataset.railCard = 'overview';
            shellOverviews.set(view, overview);
            if (view === overviewKey()) renderLeftRail();
        };
        window.addEventListener('studio-shell-overview', event => {
            const detail = event.detail || {};
            window.studioShellSetOverview(detail.markup, detail.view);
        });
        window.addEventListener('studio-job-view-changed',()=>{renderLeftRail();syncPageFrame();syncGridFooterCount();refreshPageDrawers();});
        // Job details and the workspace list share a route. Refresh the masthead
        // when the visible panel changes, not only when the route changes.
        const conversation=document.getElementById('conversationView');
        if(conversation)new MutationObserver(()=>{syncPageFrame();refreshPageDrawers();}).observe(conversation,{attributes:true,attributeFilter:['class']});


        if (pageTools) {
            new MutationObserver(() => {
                ensureTools();
            }).observe(pageTools, { childList: true });
        }
        if (breadcrumb) {
            new MutationObserver(() => ensureBreadcrumb()).observe(breadcrumb, { childList: true });
        }
        new MutationObserver(() => {
            const next = document.body.dataset.studioView || currentView;
            if (next !== currentView) {
                currentView = next;
                renderLeftRail();
                syncPageFrame();
                refreshPageDrawers();
            }
        }).observe(document.body, { attributes: true, attributeFilter: ['data-studio-view'] });
    }

    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', initialize, { once: true });
    else initialize();
})()

