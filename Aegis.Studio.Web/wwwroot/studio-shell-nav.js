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
        ['finance', 'Finance', '$'],
        ['messages', 'Messages', '✉']
    ];
    let currentView = document.body.dataset.studioView || 'today';
    let renderingTools = false;

    const escText = value => String(value ?? '').replace(/[&<>"']/g, character => ({
        '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
    })[character]);

    function labelFor(view) {
        return quickLinks.find(([key]) => key === view)?.[1]
            || String(view || 'Studio').replace(/(^|\s)\S/g, match => match.toUpperCase());
    }

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
    }

    function renderLeftRail() {
        const rail = document.getElementById('studioContextRail');
        if (!rail) return;
        const cache=typeof workspaceCache!=='undefined'?workspaceCache:window.workspaceCache;
        const workspaces = Array.isArray(cache) ? cache : [];
        const workspaceItems = workspaces
            .filter(workspace => workspace.permissions?.accessWorkspace !== false && workspace.permissions?.view !== false)
            .map(workspace => '<button type="button" data-studio-workspace-id="' + escText(workspace.id) + '"><span>▣</span><span>' + escText(workspace.name) + '</span></button>')
            .join('');
        if(document.body.dataset.studioView==='jobs'){
            const navigation=[['today','Today','⌂'],['frontdesk','Front Desk','▣'],['jobs','Jobs','▥'],['media','Media','▧'],['messages','Messages','✉']];
            const card=(title,body)=>'<section class="sj-rail-card"><header><h2>'+title+'</h2></header><div class="sj-rail-links">'+body+'</div></section>';
            const counts=document.getElementById('genericView')?.studioJobsWorkspaceCounts||{};
            const items=workspaces.filter(w=>w.permissions?.accessWorkspace!==false&&w.permissions?.view!==false).map(w=>'<button type="button" data-jobs-workspace-id="'+escText(w.id)+'"><span>▣</span><span>'+escText(w.name)+'</span>'+(counts[String(w.id)]!==undefined?'<b>'+counts[String(w.id)]+'</b>':'')+'</button>').join('');
            rail.innerHTML=card('Navigation',navigation.map(([view,title,icon])=>'<button type="button" data-studio-side="'+view+'"'+(view==='jobs'?' class="active" aria-current="page"':'')+'><span>'+icon+'</span><span>'+title+'</span></button>').join(''))+card('My Workspaces',items||'<p>No workspaces available.</p>')+card('Actions','<button type="button" data-studio-side="frontdesk"><span>＋</span><span>New Booking</span></button><button type="button" data-studio-side="schedule"><span>▦</span><span>View Calendar</span></button>');
            rail.querySelectorAll('[data-studio-side]').forEach(b=>b.onclick=()=>window.studioNavigate?.(b.dataset.studioSide));
            rail.querySelectorAll('[data-jobs-workspace-id]').forEach(b=>b.onclick=()=>window.studioOpenJobs?.(b.dataset.jobsWorkspaceId));
            return;
        }
        const jobPage=document.querySelector('#conversationView.has-open-job .job-detail-page');
        if(jobPage){
            const navigation=[['today','Today','⌂'],['frontdesk','Front Desk','▣'],['jobs','Jobs','▥'],['media','Media','▧'],['messages','Messages','✉']];
            const actions=[['upload','Upload Photos','⇧','production'],['subject','Add Subject','＋','edit'],['task','Add Task','☑','production'],['edit','Edit Job','✎','edit'],['workflow','Assignments & Workflow','⇥','ready']];
            const card=(title,body)=>'<section class="jd-rail-card"><header><h2>'+title+'</h2></header><div class="jd-rail-links">'+body+'</div></section>';
            rail.innerHTML=card('Navigation',navigation.map(([view,title,icon])=>'<button type="button" data-studio-side="'+view+'"><span>'+icon+'</span><span>'+title+'</span></button>').join(''))+
                card('My Workspaces',workspaceItems||'<p class="mw-empty">No workspaces available.</p>')+
                card('Actions',actions.map(([action,title,icon,permission])=>'<button type="button" data-job-action="'+action+'" '+(jobPage.dataset[permission+'Enabled']==='true'?'':'disabled')+'><span>'+icon+'</span><span>'+title+'</span></button>').join(''));
            rail.querySelectorAll('[data-studio-side]').forEach(button=>button.onclick=()=>window.studioNavigate?.(button.dataset.studioSide));
            rail.querySelectorAll('[data-job-action]').forEach(button=>button.onclick=()=>window.dispatchEvent(new CustomEvent('studio-job-action',{detail:{action:button.dataset.jobAction}})));
            rail.querySelectorAll('[data-studio-workspace-id]').forEach(button=>{
                button.classList.toggle('active',typeof selectedWorkspace!=='undefined'&&String(selectedWorkspace)===button.dataset.studioWorkspaceId);
                button.onclick=()=>{const workspace=workspaces.find(item=>String(item.id)===button.dataset.studioWorkspaceId);return selectWorkspace(button.dataset.studioWorkspaceId,workspace?.name||'Workspace');};
            });
            return;
        }
        rail.innerHTML = `

            <section class="mw-list-panel studio-context-card">
                <header><h2><span class="studio-card-icon" aria-hidden="true">↗</span> Navigation</h2></header>
                <div class="mw-today-rail-links">
                    <button type="button" data-studio-side="workspaces"><span>▰</span><span>Workspaces</span></button>
                    <button type="button" data-studio-side="frontdesk"><span>▣</span><span>Front Desk</span></button>
                    <button type="button" data-studio-side="production"><span>◆</span><span>Production</span></button>
                    <button type="button" data-studio-side="media"><span>▧</span><span>Media</span></button>
                    <button type="button" data-studio-side="studio-management"><span>⚙</span><span>Studio Management</span></button>
                    <button type="button" data-studio-side="schedule"><span>▦</span><span>Schedule</span></button>
                </div>
            </section>            <section class="mw-list-panel studio-context-card">
                <header><h2><span class="studio-card-icon" aria-hidden="true">✦</span> Actions</h2></header>
                <div class="mw-today-rail-links">
                    <button type="button" data-studio-side="frontdesk"><span>＋</span><span>New Booking</span></button>
                </div>
            </section>
            <section class="mw-list-panel studio-context-card">
                <header><h2><span class="studio-card-icon" aria-hidden="true">▣</span> My Workspaces</h2></header>
                <div class="mw-today-rail-links">${workspaceItems || '<p class="mw-empty">No workspaces available.</p>'}</div>
            </section>
`;
        rail.querySelectorAll('[data-studio-side]').forEach(button => {
            button.onclick = () => window.studioNavigate?.(button.dataset.studioSide);
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

    function installNavigationWrapper() {
        if (typeof window.studioNavigate !== 'function' || window.studioNavigate.__studioShellWrapped) return false;
        const original = window.studioNavigate;
        const wrapped = async (view, options = {}) => {
            currentView = view;
            const result = await original(view, options);
            ensureTools();
            ensureLeftRail();
            syncPageFrame();
            syncGridFooterCount();
            if (view !== 'today'
                && !breadcrumb?.querySelector('[data-studio-crumb="today"]')
                && !breadcrumb?.querySelector('[data-shell-nav="today"]')) {
                window.studioSetBreadcrumb([{ label: labelFor(view), view }]);
            }
            return result;
        };
        wrapped.__studioShellWrapped = true;
        window.studioNavigate = wrapped;
        return true;
    }

    function syncGridFooterCount() {
        const view = document.body.dataset.studioView || currentView;
        if (view === 'workspaces' && document.querySelector('#conversationView.has-open-job .job-detail-page')) {
            const count=document.getElementById('studioFooterRate'),page=document.querySelector('.job-detail-page');if(count)count.textContent=page.dataset.mediaCount!==undefined?page.dataset.mediaCount+' files · '+page.dataset.subjectCount+(page.dataset.subjectCount==='1'?' subject':' subjects'):'1 Job';return;
        }
        if (view === 'today' || view === 'jobs' || view === 'frontdesk') return;
        const footer = document.getElementById('studioFooterRate');
        const root = document.getElementById('genericView');
        if (!footer || !root || root.classList.contains('hidden')) return;
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
        if (view === 'today') {
            const title = document.getElementById('studioTodayBrandTitle');
            if (title) {
                const icon=title.querySelector('.mw-today-focus-icon');if(icon)icon.textContent='✦';
                const heading = title.querySelector('strong');
                if (heading) heading.innerHTML = 'Today <em>Your focus</em>';
                const tagline = title.querySelector('small');
                if (tagline) tagline.textContent = 'What needs your attention, what’s coming up, and the work to move forward.';
            }
            return;
        }
        const main = shell?.querySelector('main');
        const masthead = document.querySelector('.topbar');
        const title = document.getElementById('studioTodayBrandTitle');
        const bell = document.getElementById('notificationButton');
        if (!main || !masthead || !bell) return;

        // Retire the secondary page-heading row rather than hiding it with CSS.
        document.getElementById('studioSharedPageHeading')?.remove();
        if (breadcrumb && breadcrumb.nextElementSibling !== bell) {
            masthead.insertBefore(breadcrumb, bell);
        }
        if (title) {
            const jobOpen=view==='workspaces' && !!document.querySelector('#conversationView.has-open-job .job-detail-page');
            title.querySelector('strong')?.replaceChildren(document.createTextNode(jobOpen?'Job Details':labelFor(view)));
            title.setAttribute('aria-label',jobOpen?'Job Details':labelFor(view));
            const icon=title.querySelector('.mw-today-focus-icon');if(icon)icon.innerHTML=view==='jobs'?'<svg viewBox="0 0 24 24" width="25" height="25" fill="none" stroke="currentColor" stroke-width="1.7" aria-hidden="true"><path d="M4 6h16v14H4zM8 6V3h8v3M4 11h16M10 11v3h4v-3"/></svg>':jobOpen?'<svg viewBox="0 0 24 24" width="25" height="25" fill="none" stroke="currentColor" stroke-width="1.7" aria-hidden="true"><path d="M4 6h4l2-3h4l2 3h4v14H4z"/><circle cx="12" cy="13" r="4"/></svg>':'✦';
            const tagline = title.querySelector('small');
            if (tagline) tagline.textContent = jobOpen?'Media, workflow and conversations':view==='jobs'?'Every job, one clear view':view === 'frontdesk' ? 'Manage new bookings, inquiries and customer requests.' : 'Studio workspace';
        }
        main.classList.add('studio-standard-main');
    }

    function initialize() {
        installNavigationWrapper();
        currentView = document.body.dataset.studioView || currentView;
        ensureTools();
        ensureLeftRail();
        ensureFooter();
        syncFooterIdentity();
        const identityNode = document.getElementById('signedInUser');
        if (identityNode) new MutationObserver(syncFooterIdentity).observe(identityNode, {childList:true,characterData:true,subtree:true});
        ensureBreadcrumb();
        syncPageFrame();
        syncGridFooterCount();
        window.addEventListener('studio-jobs-view-changed',()=>{renderLeftRail();syncPageFrame();});
        window.addEventListener('studio-job-view-changed',()=>{renderLeftRail();syncPageFrame();syncGridFooterCount();});
        if (!window.history.state?.studioView) window.history.replaceState({ studioView: currentView }, '', window.location.href);
        window.addEventListener('popstate', event => {
            const view = event.state?.studioView || 'today';
            if (view !== currentView) window.studioNavigate?.(view, { browserHistory: true });
        });

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

