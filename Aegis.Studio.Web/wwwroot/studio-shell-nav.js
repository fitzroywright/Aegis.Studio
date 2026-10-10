(() => {
    const breadcrumb = document.getElementById('studioBreadcrumb');
    const pageTools = document.getElementById('studioPageTools');
    const shell = document.querySelector('.shell');
    const quickLinks = [
        ['today', 'Today', '▤'],
        ['workspaces', 'Workspaces', '▰'],
        ['jobs', 'Jobs', '▥'],
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
        const workspaces = Array.isArray(window.workspaceCache) ? window.workspaceCache : [];
        const workspaceItems = workspaces
            .filter(workspace => workspace.permissions?.accessWorkspace !== false && workspace.permissions?.view !== false)
            .map(workspace => '<button type="button" data-studio-workspace-id="' + escText(workspace.id) + '"><span>▣</span><span>' + escText(workspace.name) + '</span></button>')
            .join('');
        rail.innerHTML = `

            <section class="mw-list-panel studio-context-card">
                <header><h2><span class="studio-card-icon" aria-hidden="true">▣</span> My Workspaces</h2></header>
                <div class="mw-today-rail-links">${workspaceItems || '<p class="mw-empty">No workspaces available.</p>'}</div>
            </section>
            <section class="mw-list-panel studio-context-card">
                <header><h2><span class="studio-card-icon" aria-hidden="true">✦</span> Actions</h2></header>
                <div class="mw-today-rail-links">
                    <button type="button" data-studio-side="frontdesk"><span>＋</span><span>New Booking</span></button>
                </div>
            </section>
            <section class="mw-list-panel studio-context-card">
                <header><h2><span class="studio-card-icon" aria-hidden="true">↗</span> Quick Links</h2></header>
                <div class="mw-today-rail-links">
                    <button type="button" data-studio-side="workspaces"><span>▰</span><span>Workspaces</span></button>
                    <button type="button" data-studio-side="frontdesk"><span>▣</span><span>Front Desk</span></button>
                    <button type="button" data-studio-side="production"><span>◆</span><span>Production</span></button>
                    <button type="button" data-studio-side="schedule"><span>▦</span><span>Schedule</span></button>
                </div>
            </section>`;
        rail.querySelectorAll('[data-studio-side]').forEach(button => {
            button.onclick = () => window.studioNavigate?.(button.dataset.studioSide);
        });
        rail.querySelectorAll('[data-studio-workspace-id]').forEach(button => {
            button.onclick = () => {
                const id = button.dataset.studioWorkspaceId;
                const workspace = (window.workspaceCache || []).find(item => String(item.id) === id);
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
                    const workspace = (window.workspaceCache || []).find(item => String(item.id) === String(button.dataset.studioWorkspace));
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
        if (view === 'today' || view === 'jobs') return;
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
            footer.textContent = rows.length + ' ' + noun + (rows.length === 1 ? '' : 's');
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
            title.querySelector('strong')?.replaceChildren(document.createTextNode(labelFor(view)));
            const tagline = title.querySelector('small');
            if (tagline) tagline.textContent = view === 'frontdesk' ? 'Manage new bookings, inquiries and customer requests.' : 'Studio workspace';
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
