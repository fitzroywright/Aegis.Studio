(() => {
    const breadcrumb = document.getElementById('studioBreadcrumb');
    const pageTools = document.getElementById('studioPageTools');
    const shell = document.querySelector('.shell');
    const quickLinks = [
        ['today', 'Today', '▤'],
        ['workspaces', 'Workspaces', '▰'],
        ['jobs', 'Jobs', '▥'],
        ['media', 'Media', '▧'],
        ['pipeline', 'Pipeline', '⇥'],
        ['production', 'Production', '◆'],
        ['frontdesk', 'Front Desk', '▣'],
        ['clients', 'Clients', '♧'],
        ['finance', 'Finance', '$'],
        ['messages', 'Messages', '✉']
    ];
    let currentView = document.body.dataset.studioView || 'today';
    let renderingTools = false;
    let workspaceLoadPromise = null;
    let workspaceLoadAttempted = false;
    let productionWorkspaceFilter = null;

    const escText = value => String(value ?? '').replace(/[&<>"']/g, character => ({
        '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
    })[character]);

    function labelFor(view) {
        return ({'operations-management':'Operations','studio-management':'Studio Management','job-templates':'Job Templates','work-order-templates':'Work Order Templates'})[view] || quickLinks.find(([key]) => key === view)?.[1]
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
        'operations-management': 'Monitor operational work, assignments and exceptions.',
        'studio-management': 'Configure Studio settings, staff access and workspaces.'
    };
    const taglineFor = view => pageTaglines[view] || 'Manage Studio work and related information.';

    function iconFor(view) {
        return quickLinks.find(([key]) => key === view)?.[2] || '▤';
    }

    function ensureTools() {
        pageTools?.querySelector('[data-studio-persistent-tools]')?.remove();
    }

    function ensureFooter() {
        if (!shell) return;
        let footer = document.getElementById('studioShellFooter');
        if (!footer) {
            footer = document.createElement('footer');
            footer.id = 'studioShellFooter';
            footer.className = 'studio-shell-footer';
            footer.textContent = "Brian's Photo Studios · Studio";
            shell.append(footer);
        }
    }

    function syncFooterIdentity() {
        const target = document.getElementById('studioFooterUser');
        if (!target) return;
        const label = document.getElementById('signedInUser')?.textContent?.trim();
        const avatar = document.getElementById('userAvatar')?.textContent?.trim();
        const identity = label && !/^signed in$/i.test(label) ? label : (avatar && avatar !== '--' ? avatar : 'Studio Administrator - Admin');
        target.textContent = 'Logged in User: ' + identity;
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

    const railRole=title=>title==='Navigation'?'navigation':title==='My Workspaces'?'workspaces':'actions';
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
        const jobActions=[['upload','Upload Photos','⇧','production'],['subject','Add Subject','＋','edit'],['task','Add Task','☑','production'],['edit','Edit Job','✎','edit'],['workflow','Assignments & Workflow','⇥','ready'],['template-workflow','Template Workflow','◇','ready'],['reminders','Client Reminders','◷','ready']];
        const actionItems=jobPage?jobActions.map(([action,title,icon,permission])=>'<button type="button" data-job-action="'+action+'" '+(jobPage.dataset[permission+'Enabled']==='true'?'':'disabled')+'><span>'+icon+'</span><span>'+title+'</span></button>').join(''):'<button type="button" data-studio-new-booking><span>＋</span><span>New Booking</span></button>'+(document.body.dataset.studioView==='today'?'<button type="button" data-studio-assign-job-lead><span>▣</span><span>Assign Job Lead</span></button>':'');
        const financialActions=document.body.dataset.studioView==='frontdesk'?[['TakePayment','Take Payment'],['RecordDeposit','Record Deposit'],['IssueRefund','Issue Refund'],['NewSale','New Sale']].map(([action,label])=>'<button type="button" data-studio-financial-action="'+action+'"><span>$</span><span>'+label+'</span></button>').join(''):'';
        const pipelineScheduleAction=document.body.dataset.studioView==='schedule'&&window.history.state?.studioSchedulePanel==='pipeline'?'<button type="button" data-studio-schedule-item><span>▦</span><span>Schedule item</span></button>':'';
        rail.innerHTML = `<section class="mw-list-panel studio-context-card" data-rail-card="navigation">
                <header><h2><span class="studio-card-icon" aria-hidden="true">↗</span> Navigation</h2></header>
                <div class="mw-today-rail-links">
                    <button type="button" data-studio-side="today"><span>⌂</span><span>Today</span></button>
                    <button type="button" data-studio-side="frontdesk"><span>▣</span><span>Front Desk</span></button>
                    <button type="button" data-studio-side="clients"><span>♧</span><span>Clients</span></button>
                    <button type="button" data-studio-side="jobs"><span>▥</span><span>Jobs</span></button>
                    <button type="button" data-studio-side="production"><span>◆</span><span>Production</span></button>
                    <button type="button" data-studio-side="schedule"><span>▦</span><span>Schedule</span></button>
                    <button type="button" data-studio-side="operations-management"><span>✓</span><span>Operations</span></button>
                    <button type="button" data-studio-side="media"><span>▧</span><span>Media</span></button>
                    <button type="button" data-studio-side="messages"><span>✉</span><span>Messages</span></button>
                </div>
            </section>
            <section class="mw-list-panel studio-context-card" data-rail-card="actions">
                <header><h2><span class="studio-card-icon" aria-hidden="true">✦</span> Actions</h2></header>
                <div class="mw-today-rail-links">
                    ${actionItems}${pipelineScheduleAction}${document.body.dataset.studioView==='clients'?'<button type="button" data-clients-new><span>＋</span><span>New Client</span></button>':''}${financialActions}${document.body.dataset.studioView==='frontdesk'?'<button type="button" data-studio-new-client><span>＋</span><span>New Client</span></button><button type="button" data-studio-close-register><span>▤</span><span>Close Register</span></button>':''}
                </div>
            </section>
            <section class="mw-list-panel studio-context-card" data-rail-card="workspaces">
                <header class="studio-workspaces-card-header"><h2><span class="studio-card-icon" aria-hidden="true">▣</span> Workspaces</h2><button type="button" class="studio-workspaces-view-all" data-studio-view-all-workspaces>View All →</button></header>
                <div class="mw-today-rail-links">${scheduleWorkspaceItems ?? (workspaceItems || '<p class="mw-empty">No workspaces available.</p>')}</div>
            </section>
`;
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
        rail.querySelectorAll('[data-studio-side]').forEach(button => {
            button.onclick = () => window.studioNavigate?.(button.dataset.studioSide);
        });
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

    const railStorage = 'aegis.studio.rail-collapse.v1';
    function railState() {
        try { return JSON.parse(localStorage.getItem(railStorage) || '{}'); } catch { return {}; }
    }
    function setRailState(side, collapsed) {
        const state = railState(); state[side] = collapsed;
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
        button.dataset.chevronDirection = side === 'left'
            ? (collapsed ? 'right' : 'left')
            : (collapsed ? 'left' : 'right');
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
    function syncRightRailLayout(rail, collapsed) {
        rail.classList.toggle('studio-rail-collapsed-right', collapsed);
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
        const rightTargets = document.querySelectorAll('.mw-today-right, .mw-list-right, .st-right, .sj-right, .fd-secondary, .workspace-right-rail, .job-detail-right, .studio-media-detail');
        rightTargets.forEach(rail => {
            rail.classList.toggle('studio-rail-collapsed-right', !!state.right);
            syncRightRailLayout(rail, !!state.right);
            rail.dataset.shellRailReadyRight = 'true';
            const toggle = placeRailToggle(rail, 'right', !!state.right);
            toggle.setAttribute('aria-expanded', String(!state.right));
            toggle.setAttribute('aria-label', (state.right ? 'Expand' : 'Collapse') + ' right rail');
            toggle.title = toggle.getAttribute('aria-label');
            toggle.dataset.chevronDirection = state.right ? 'left' : 'right';
        });
    }

    const railObserver = new MutationObserver(() => {
        const leftSelector = '#studioContextRail, .mw-today-left';
        const rightSelector = '.mw-today-right, .mw-list-right, .st-right, .sj-right, .fd-secondary, .workspace-right-rail, .job-detail-right, .studio-media-detail';
        const missingLeft = Array.from(document.querySelectorAll(leftSelector))
            .some(rail => rail.dataset.shellRailReadyLeft !== 'true');
        const missingRight = Array.from(document.querySelectorAll(rightSelector))
            .some(rail => rail.dataset.shellRailReadyRight !== 'true');
        if (missingLeft || missingRight) applyRailState();
    });
    railObserver.observe(document.body, { childList: true, subtree: true });
    window.addEventListener('resize', () => {
        if (railState().right) document.querySelectorAll('.mw-today-right, .mw-list-right, .st-right, .sj-right, .fd-secondary, .workspace-right-rail, .job-detail-right, .studio-media-detail').forEach(rail => syncRightRailLayout(rail, true));
    });

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

    // Production supplies its workspace filter data to the shared Workspaces card.
    // Other views continue to use the permission-scoped workspace cache.
    window.StudioShell={
        setProductionWorkspaceFilter(workspaces, selected, onSelect) {
            productionWorkspaceFilter = { workspaces: Array.isArray(workspaces) ? workspaces : [], selected: selected || '', onSelect };
            if (document.body.dataset.studioView === 'production') renderLeftRail();
        },
        sync(view=document.body.dataset.studioView||currentView){
        currentView=view;
        ensureTools();
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
        const jobTools = document.getElementById('studioFooterJobTools');
        if (jobTools) jobTools.hidden = !(view === 'workspaces' && document.querySelector('#conversationView.has-open-job .job-detail-page'));

        const main = shell?.querySelector('main');
        const masthead = document.querySelector('.topbar');
        const title = document.getElementById('studioTodayBrandTitle');
        const bell = document.getElementById('notificationButton');
        if (!main || !masthead || !bell) return;

        // Every view, including Today, uses the same shared masthead and frame sync.
        document.getElementById('studioSharedPageHeading')?.remove();
        if (breadcrumb && breadcrumb.nextElementSibling !== bell) {
            masthead.insertBefore(breadcrumb, bell);
        }
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

    function initialize() {
        currentView = document.body.dataset.studioView || currentView;
        ensureTools();
        ensureLeftRail();
        ensureWorkspaceListLoaded();
        ensureFooter();
        syncFooterIdentity();
        const identityNode = document.getElementById('signedInUser');
        if (identityNode) new MutationObserver(syncFooterIdentity).observe(identityNode, {childList:true,characterData:true,subtree:true});
        ensureBreadcrumb();
        syncPageFrame();
        syncGridFooterCount();
        window.addEventListener('studio-jobs-view-changed',()=>{renderLeftRail();syncPageFrame();});
        window.addEventListener('studio-workflow-capabilities',renderLeftRail);
        window.addEventListener('studio-workspaces-loaded',renderLeftRail);
        window.addEventListener('studio-job-view-changed',()=>{renderLeftRail();syncPageFrame();syncGridFooterCount();});
        // Job details and the workspace list share a route. Refresh the masthead
        // when the visible panel changes, not only when the route changes.
        const conversation=document.getElementById('conversationView');
        if(conversation)new MutationObserver(()=>syncPageFrame()).observe(conversation,{attributes:true,attributeFilter:['class']});


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
            }
        }).observe(document.body, { attributes: true, attributeFilter: ['data-studio-view'] });
    }

    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', initialize, { once: true });
    else initialize();
})();











