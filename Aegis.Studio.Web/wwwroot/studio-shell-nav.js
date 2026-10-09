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
                    <button type="button" data-studio-side="jobs"><span>▣</span><span>Jobs</span></button>
                    <button type="button" data-studio-side="tasks"><span>☑</span><span>Tasks</span></button>
                    <button type="button" data-studio-side="messages"><span>✉</span><span>Send Message</span></button>
                </div>
            </section>
            <section class="mw-list-panel studio-context-card">
                <header><h2><span class="studio-card-icon" aria-hidden="true">↗</span> Quick Links</h2></header>
                <div class="mw-today-rail-links">
                    <button type="button" data-studio-side="schedule"><span>▦</span><span>Calendar</span></button>
                    <button type="button" data-studio-side="production"><span>◆</span><span>Production</span></button>
                    <button type="button" data-studio-side="jobs"><span>▥</span><span>Jobs</span></button>
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
        breadcrumb.innerHTML = crumbs.map((item, index) => {
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
            const previous = currentView;
            if (!options.browserHistory && previous !== view) {
                window.history.pushState({ studioView: view }, '', '#studio=' + encodeURIComponent(view));
            }
            currentView = view;
            const result = await original(view);
            ensureTools();
            ensureLeftRail();
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

    function initialize() {
        installNavigationWrapper();
        currentView = document.body.dataset.studioView || currentView;
        ensureTools();
        ensureLeftRail();
        ensureFooter();
        ensureBreadcrumb();
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
            }
        }).observe(document.body, { attributes: true, attributeFilter: ['data-studio-view'] });
    }

    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', initialize, { once: true });
    else initialize();
})();
