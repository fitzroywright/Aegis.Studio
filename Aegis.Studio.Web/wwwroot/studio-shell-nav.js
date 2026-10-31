(() => {
    const breadcrumb = document.getElementById('studioBreadcrumb');
    const pageTools = document.getElementById('studioPageTools');
    const shell = document.querySelector('.shell');
    const navigationIcon = artwork => '<svg class="studio-navigation-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true" focusable="false">'+artwork+'</svg>';
    const quickLinks = [
        ['dashboard', 'Today', navigationIcon('<rect x="3" y="5" width="18" height="16" rx="2"/><path d="M7 3v4m10-4v4M3 10h18"/><path class="studio-navigation-accent" d="m8 15 3 3 5-5"/>')],
        ['frontdesk', 'Front Desk', navigationIcon('<path d="M3 12h18v9H3zM2 12h20M8 12V9a4 4 0 0 1 8 0v3"/><path class="studio-navigation-accent" d="M12 3v2M7 17h4"/>')],
        ['clients', 'Clients', navigationIcon('<circle cx="9" cy="7" r="3"/><path d="M3 21v-3a6 6 0 0 1 12 0v3"/><path class="studio-navigation-accent" d="M16 4a3 3 0 0 1 0 6m2 4a5 5 0 0 1 3 5v2"/>')],
        ['jobs', 'Jobs', navigationIcon('<rect x="3" y="7" width="18" height="14" rx="2"/><path d="M8 7V4h8v3M3 12a24 24 0 0 0 18 0"/><path class="studio-navigation-accent" d="M10 13h4v3h-4z"/>')],
        ['production', 'Production', navigationIcon('<path d="M4 6h16M4 12h16M4 18h16"/><path class="studio-navigation-accent" d="M8 3v6m8 0v6m-6 0v6"/>')],
        ['schedule', 'Schedule', navigationIcon('<rect x="3" y="5" width="18" height="16" rx="2"/><path d="M7 3v4m10-4v4M3 10h18"/><path class="studio-navigation-accent" d="M7 14h3m4 0h3M7 18h3"/>')],
        ['media', 'Media', navigationIcon('<rect x="5" y="6" width="16" height="15" rx="2"/><path d="M17 3H5a2 2 0 0 0-2 2v12m2 1 5-5 4 4 3-3 4 4"/><circle class="studio-navigation-accent" cx="16.5" cy="10.5" r="1.5"/>')]
    ];
    let currentView = document.body.dataset.studioView || 'dashboard';
    const shellAttention = new Map();
    const pageKey = () => document.body.dataset.studioView === 'workspaces' && document.querySelector('#conversationView.has-open-job .job-detail-page') ? 'job-detail' : (document.body.dataset.studioView || 'dashboard');
    let workspaceLoadPromise = null;
    let workspaceLoadAttempted = false;
    let productionWorkspaceFilter = null;
    let mediaWorkspaceFilter = null;
    const pageAttention = new Map();
    let sharedAttention={items:[],loaded:false,error:''};
    let attentionOwnerFilter='',attentionActor='';
    async function loadSharedAttention(){if(!window.StudioAttention)return;try{sharedAttention=await window.StudioAttention.load();if(window.StudioNextActions){const me=await json('/api/auth/me');attentionActor=(me.user||me).userName||(me.user||me).loginName||'';}}catch{sharedAttention={items:[],loaded:false,error:'Needs Attention could not be loaded. Reopen to retry.'};}refreshPageDrawers();}
    let sharedSessions = [];
    let sessionsState = 'loading';
    let sessionsRequest = null;
    const currentPage = () => pageKey();

    const escText = value => String(value ?? '').replace(/[&<>"']/g, character => ({
        '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
    })[character]);

    function labelFor(view) {
        if(view==='access-pending')return 'Access Pending';
        return window.StudioAdmin?.entries.find(entry=>entry.view===view)?.label || ({clients:'Client Management','workspace-management':'Workspaces','session-plan-import':'Session Plan Import','session-plan':'Session Plan',delivery:'Delivery',about:'About','staff-roles':'Staff and Roles','job-templates':'Job Templates','work-order-templates':'Work Order Templates'})[view] || quickLinks.find(([key]) => key === view)?.[1]
            || String(view || 'Studio').replace(/(^|\s)\S/g, match => match.toUpperCase());
    }

    // One purpose-driven tagline per page; keep all masthead descriptions here.
    const pageTaglines = {
        dashboard: 'Company-wide sessions, intake, production and delivery.',
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
        about: 'About Aegis.Studio, service health, support and demo data.',
        'staff-roles': 'Manage Studio staff, reusable job roles and global operational roles.'
    };
    const taglineFor = view => pageTaglines[view] || 'Manage Studio work and related information.';

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
            for (const control of [document.getElementById('notificationButton'), document.getElementById('studioMessageBellPanel'), banner.querySelector('.studio-user-identity')]) {
                if (control) account.append(control);
            }
            host.append(scroll);
            window.StudioAdmin?.mountMenu(host);
            host.append(account);
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

    function ensureWorkspaceListLoaded() {
        if(window.StudioAccess?.pending())return Promise.resolve();
        const cache = typeof workspaceCache !== 'undefined' ? workspaceCache : window.workspaceCache;
        if (Array.isArray(cache) && cache.length) return Promise.resolve();
        if (workspaceLoadPromise || workspaceLoadAttempted || typeof loadWorkspaces !== 'function') return workspaceLoadPromise || Promise.resolve();
        workspaceLoadAttempted = true;
        workspaceLoadPromise = Promise.resolve().then(() => loadWorkspaces()).catch(() => {
            workspaceLoadAttempted = false;
        }).finally(() => {
            workspaceLoadPromise = null;
            refreshWorkspaceMenu();
        });
        return workspaceLoadPromise;
    }

    function refreshWorkspaceMenu() {
        if(window.StudioAccess?.pending()){renderFooterWorkspaceMenu([]);return;}
        const cache = typeof workspaceCache !== 'undefined' ? workspaceCache : window.workspaceCache;
        const workspaces = document.body.dataset.studioView === 'production' && productionWorkspaceFilter
            ? productionWorkspaceFilter.workspaces
            : document.body.dataset.studioView === 'media' && mediaWorkspaceFilter
            ? mediaWorkspaceFilter.workspaces
            : (Array.isArray(cache) ? cache : []).filter(workspace => workspace.permissions?.accessWorkspace !== false && workspace.permissions?.view !== false);
        renderFooterWorkspaceMenu(workspaces);
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
        const isMedia = document.body.dataset.studioView === 'media' && mediaWorkspaceFilter;
        control.disabled = Boolean(isMedia && mediaWorkspaceFilter.locked);
        const savedSelection = window.sessionStorage.getItem('studio.footer.workspace.selection') || '';
        const selectedId = isProduction ? String(productionWorkspaceFilter.selected || '') : isMedia ? String(mediaWorkspaceFilter.selected || '') : savedSelection;
        control.replaceChildren();
        const all = new Option('Workspaces: All', '');
        control.add(all);
        if(window.StudioAccess?.pending()){all.textContent='No workspace assigned';control.disabled=true;control.onchange=null;return;}
        workspaces.forEach(workspace => control.add(new Option(workspace.name || workspace.title || 'Workspace', String(workspace.id))));
        control.value = workspaces.some(workspace => String(workspace.id) === selectedId) ? selectedId : '';
        control.onchange = () => {
            const id = control.value;
            if (isMedia) {
                return mediaWorkspaceFilter.onSelect?.(id);
            }
            if(document.body.dataset.studioView === 'dashboard'){
                window.sessionStorage.setItem('studio.footer.workspace.selection',id);
                window.StudioDashboard?.render();
                return;
            }
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

    function ensureBreadcrumb() {
        if (!breadcrumb || breadcrumb.children.length) return;
        window.studioSetBreadcrumb?.([{ label: labelFor(currentView), view: currentView }]);
    }

    window.studioSetBreadcrumb = items => {
        if (!breadcrumb) return;
        const supplied = Array.isArray(items) ? items.filter(Boolean) : [];
        const crumbs = window.StudioAccess?.pending() ? supplied : supplied[0]?.view === 'dashboard'
            ? supplied
            : [{ label: 'Today', view: 'dashboard' }, ...supplied];
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

    window.studioShellMountAttention = (content, view = pageKey()) => {
        shellAttention.set(view, content || null);
        refreshPageDrawers();
    };

    window.StudioShell={
        setMediaWorkspaceFilter(workspaces, selected, onSelect, locked = false) {
            mediaWorkspaceFilter = { workspaces: Array.isArray(workspaces) ? workspaces : [], selected: selected || '', onSelect, locked };
            if (document.body.dataset.studioView === 'media') refreshWorkspaceMenu();
        },
        setProductionWorkspaceFilter(workspaces, selected, onSelect) {
            productionWorkspaceFilter = { workspaces: Array.isArray(workspaces) ? workspaces : [], selected: selected || '', onSelect };
            if (document.body.dataset.studioView === 'production') refreshWorkspaceMenu();
        },
        sync(view=document.body.dataset.studioView||currentView){
        currentView=view;
        ensureTools();
        initializeAttentionDrawer();
        refreshPageDrawers();
        refreshWorkspaceMenu();
        ensureWorkspaceListLoaded();
        syncPageFrame();
        syncGridFooterCount();
        if(view!=='dashboard'&&!breadcrumb?.querySelector('[data-studio-crumb="today"]')&&!breadcrumb?.querySelector('[data-shell-nav="today"]')){
            window.studioSetBreadcrumb([{label:labelFor(view),view}]);
        }
    }};

    function syncGridFooterCount() {
        const view = document.body.dataset.studioView || currentView;
        if (view === 'workspaces' && document.querySelector('#conversationView.has-open-job .job-detail-page')) {
            const count=document.getElementById('studioFooterRate'),page=document.querySelector('.job-detail-page');if(count)count.textContent=page.dataset.mediaCount!==undefined?page.dataset.mediaCount+' files · '+page.dataset.subjectCount+(page.dataset.subjectCount==='1'?' subject':' subjects'):'1 Job';return;
        }
        if (view === 'dashboard' || view === 'jobs' || view === 'frontdesk') return;
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
        const pending=!!window.StudioAccess?.pending();
        for(const node of document.querySelectorAll('.studio-navigation-scroll,#studioAttentionTrigger,#studioSessionsTrigger,#notificationButton,#studioContentSearch,#profileButton,[data-studio-avatar-notifications]'))node.classList.toggle('hidden',pending);
        const view = document.body.dataset.studioView || currentView;
        const isToday = view === 'dashboard';
        const announcement = document.getElementById('studioLoginAnnouncements');
        if(announcement) announcement.hidden = isToday;

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
                if (isToday) heading.innerHTML = 'Today <em>Company overview</em>';
                else heading.replaceChildren(document.createTextNode(pageLabel));
            }
            title.setAttribute('aria-label', pageLabel);
            const icon = title.querySelector('.mw-today-focus-icon');
            if (icon) icon.innerHTML = view === 'jobs' ? '<svg viewBox="0 0 24 24" width="25" height="25" fill="none" stroke="currentColor" stroke-width="1.7" aria-hidden="true"><path d="M4 6h16v14H4zM8 6V3h8v3M4 11h16M10 11v3h4v-3"/></svg>' : jobOpen ? '<svg viewBox="0 0 24 24" width="25" height="25" fill="none" stroke="currentColor" stroke-width="1.7" aria-hidden="true"><path d="M4 6h4l2-3h4l2 3h4v14H4z"/><circle cx="12" cy="13" r="4"/></svg>' : '✦';
            const tagline = title.querySelector('small');
            if (tagline) tagline.textContent = isToday ? taglineFor('dashboard') : jobOpen ? 'Review job details, assignments, workflow, media and conversations.' : taglineFor(view);
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
        trigger.setAttribute('aria-label', 'Needs Attention');
        trigger.title = 'Needs Attention';
        trigger.innerHTML = '<svg class="studio-status-icon studio-attention-icon" viewBox="0 0 24 24" aria-hidden="true" focusable="false"><path d="M10.3 4.4 2.8 17.5A2 2 0 0 0 4.5 20.5h15a2 2 0 0 0 1.7-3L13.7 4.4a2 2 0 0 0-3.4 0Z"/><path d="M12 9v4.5m0 3h.01"/></svg><strong id="studioAttentionCount" aria-label="Attention items">0</strong><span aria-hidden="true">⌄</span>';
        const sessionsTrigger = document.createElement('button');
        sessionsTrigger.type = 'button';
        sessionsTrigger.id = 'studioSessionsTrigger';
        sessionsTrigger.className = 'studio-attention-trigger studio-sessions-trigger';
        sessionsTrigger.setAttribute('aria-controls', 'studioSessionsDrawer');
        sessionsTrigger.setAttribute('aria-expanded', 'false');
        sessionsTrigger.setAttribute('aria-label', 'Upcoming Sessions');
        sessionsTrigger.title = 'Upcoming Sessions';
        sessionsTrigger.innerHTML = '<svg class="studio-status-icon" viewBox="0 0 24 24" aria-hidden="true" focusable="false"><path d="M20 10V6a2 2 0 0 0-2-2H5a2 2 0 0 0-2 2v13a2 2 0 0 0 2 2h6M7 2v4m9-4v4M3 9h17M7 13h.01M11 13h.01M7 17h.01"/><g class="studio-session-clock"><circle cx="17" cy="17" r="5"/><path d="M17 14v3l2 1"/></g></svg><strong id="studioSessionsCount" aria-label="Upcoming sessions">0</strong><span aria-hidden="true">⌄</span>';
        const statusTools = document.createElement('div');
        statusTools.className = 'studio-navigation-status';
        statusTools.append(sessionsTrigger, trigger);
        toolstrip.insertBefore(statusTools,toolstrip.querySelector('.studio-navigation-account'));
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
        drawer.innerHTML = '<header><h2>Needs Attention</h2><button type="button" data-attention-close aria-label="Close Needs Attention">×</button></header><label class="studio-attention-owner">Show <select data-attention-owner aria-label="Attention responsibility"><option value="">All staff</option><option value="mine">Assigned to me</option><option value="unassigned">Unassigned</option></select></label><div id="studioAttentionDetails" class="studio-attention-details"><div id="studioAttentionItems"></div><div id="studioPageAttention"></div></div>';
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
            if (opening) {drawer.querySelector('[data-attention-close]').focus();void loadSharedAttention();}
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
        drawer.querySelector('[data-attention-owner]').onchange=event=>{attentionOwnerFilter=event.target.value;refreshPageDrawers();};
        document.addEventListener('keydown', event => {
            if (event.key === 'Escape' && (!drawer.hidden || !sessionsDrawer.hidden)) close();
        });
        drawer.addEventListener('click', event => {
            const link = event.target.closest('[data-attention-job],[data-attention-route],[data-attention-next],[data-attention-enquiry]');
            if (!link) return;
            close();
            if(link.dataset.attentionNext){const item=sharedAttention.items.find(item=>item.id===link.dataset.attentionNext);if(item?.nextAction)StudioNextActions.activate(item.nextAction).catch(error=>alert(error.message));}
            else if(link.dataset.attentionEnquiry)window.StudioNextActions?.openEnquiry(link.dataset.attentionEnquiry);
            else if(link.dataset.attentionJob)window.studioNavigate?.('workspaces',{jobId:link.dataset.attentionJob});
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
        const merged=new Map([...(Array.isArray(data.items)?data.items:[]),...sharedAttention.items].map(item=>[item.id,item]));
        let items=[...merged.values()];
        const pageSlot=document.getElementById('studioPageAttention');
        const content=shellAttention.get(key);
        if(pageSlot&&pageSlot.firstElementChild!==(content||null))pageSlot.replaceChildren(...(content?[content]:[]));
        const jobSlot=document.getElementById('studioShellJobAttention');
        const jobContent=key==='job-detail'?jobSlot?.firstElementChild:null;
        if(jobSlot&&jobSlot.hidden!==(key!=='job-detail'||!!attentionOwnerFilter))jobSlot.hidden=key!=='job-detail'||!!attentionOwnerFilter;
        const countContent=node=>{
            if(node.dataset.attentionCount!==undefined)return Number(node.dataset.attentionCount)||0;
            return node.querySelectorAll('[data-attention-item],button,[role=alert]').length;
        };
        const mountedIds=new Set();
        for(const node of [content,jobContent].filter(Boolean))for(const row of node.querySelectorAll('[data-attention-item]')){
            const id=row.dataset.attentionItem;mountedIds.add(id);if(row.dataset.sjJob&&id===row.dataset.sjJob)mountedIds.add('job:'+id);
        }
        items=items.filter(item=>!mountedIds.has(item.id));
        if(attentionOwnerFilter)items=items.filter(item=>attentionOwnerFilter==='unassigned'?!item.assignedTo:!!attentionActor&&String(item.assignedTo||'').toLowerCase()===attentionActor.toLowerCase());
        if(pageSlot&&pageSlot.hidden!==!!attentionOwnerFilter)pageSlot.hidden=!!attentionOwnerFilter;
        const total = (!attentionOwnerFilter&&content?countContent(content):!attentionOwnerFilter&&jobContent?countContent(jobContent):0)+items.length;
        const count = document.getElementById('studioAttentionCount');
        if (count) count.textContent = String(total);
        document.getElementById('studioAttentionTrigger')?.classList.toggle('has-attention', total > 0);
        const details = document.getElementById('studioAttentionItems');
        if(details)details.hidden=false;
        if (details && !details.hidden) {
            details.replaceChildren();
            if(data.error||sharedAttention.error){const error=document.createElement('p');error.setAttribute('role','alert');error.textContent=sharedAttention.error||'Unable to load some attention items. Refresh Today to retry.';details.append(error);}
            if (!total&&!data.error&&!sharedAttention.error) {
                const p = document.createElement('p');
                p.textContent = sharedAttention.loaded||data.loaded?'Nothing currently needs attention.':'Loading attention items…';
                details.append(p);
            }
            items.forEach(item => {
                const button = document.createElement('button');
                button.type = 'button';
                button.dataset.attentionItem = item.id;
                if(item.nextAction)button.dataset.attentionNext=item.id;
                else if(item.enquiryId)button.dataset.attentionEnquiry=item.enquiryId;
                else if(item.route)button.dataset.attentionRoute=item.route;
                else if(item.jobId)button.dataset.attentionJob = item.jobId;
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
        if(window.StudioAccess?.pending())return Promise.resolve();
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

    async function initialize() {
        if(window.studioSignedInUserReady){const user=await window.studioSignedInUserReady;if(!user)return;}
        document.querySelector('[data-studio-home]')?.addEventListener('click', event => {
            if (event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;
            event.preventDefault();
            window.studioNavigate?.('dashboard');
        });
        currentView = document.body.dataset.studioView || currentView;
        ensureTools();
        ensureFooter();
        refreshWorkspaceMenu();
        ensureWorkspaceListLoaded();
        syncFooterIdentity();
        const identityNode = document.getElementById('signedInUser');
        if (identityNode) new MutationObserver(syncFooterIdentity).observe(identityNode, {childList:true,characterData:true,subtree:true});
        ensureBreadcrumb();
        syncPageFrame();
        initializeAttentionDrawer();
        refreshPageDrawers();
        syncPageFrame();
        loadSharedSessions();
        if(!window.StudioAccess?.pending())void loadSharedAttention();
        syncGridFooterCount();
        window.addEventListener('studio-jobs-view-changed',()=>{refreshWorkspaceMenu();syncPageFrame();});
        window.addEventListener('studio-next-actions-changed',()=>{void loadSharedAttention();});
        document.addEventListener('studio-workflow-changed',()=>{if(!window.StudioNextActions)void loadSharedAttention();});
        window.addEventListener('studio-workspaces-loaded',refreshWorkspaceMenu);
        document.querySelector('[data-studio-avatar-notifications]')?.addEventListener('click', () => {
            document.querySelector('.user-menu')?.removeAttribute('open');
            window.studioNavigate?.('notifications');
        });
        window.addEventListener('studio-job-view-changed',()=>{refreshWorkspaceMenu();syncPageFrame();syncGridFooterCount();refreshPageDrawers();});
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
                refreshWorkspaceMenu();
                syncPageFrame();
                refreshPageDrawers();
            }
        }).observe(document.body, { attributes: true, attributeFilter: ['data-studio-view'] });
    }

    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', initialize, { once: true });
    else initialize();
})()


