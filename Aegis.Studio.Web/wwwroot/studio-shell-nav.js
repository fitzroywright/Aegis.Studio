(() => {
    const breadcrumb = document.getElementById('studioBreadcrumb');
    const pageTools = document.getElementById('studioPageTools');
    const shell = document.querySelector('.shell');
    const quickLinks = [
        ['mywork', 'Today', '▤'],
        ['workspaces', 'Workspaces', '▰'],
        ['jobs', 'Jobs', '▥'],
        ['pipeline', 'Pipeline', '⇥'],
        ['production', 'Production', '◆'],
        ['frontdesk', 'Front Desk', '▣'],
        ['finance', 'Finance', '$'],
        ['messages', 'Messages', '✉']
    ];
    let currentView = document.body.dataset.studioView || 'mywork';
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
        const page = labelFor(currentView);
        rail.innerHTML = `
            <section class="mw-list-panel studio-context-card studio-context-status">
                <header><h2><span class="mw-title-icon status">${iconFor(currentView)}</span> Status</h2></header>
                <div class="mw-status-list">
                    <button class="mw-status-row active" type="button" aria-current="page">
                        <span class="mw-status-icon">${iconFor(currentView)}</span>
                        <span>${escText(page)}</span>
                        <strong>Current</strong>
                    </button>
                    <button class="mw-status-row" type="button" data-studio-side="mywork">
                        <span class="mw-status-icon">▤</span><span>My Work</span><strong>Home</strong>
                    </button>
                    <button class="mw-status-row" type="button" data-studio-side="workspaces">
                        <span class="mw-status-icon">▰</span><span>Workspaces</span><strong>Open</strong>
                    </button>
                </div>
            </section>

            <section class="mw-list-panel mw-action-panel studio-context-card">
                <header><h2><span class="mw-title-icon actions">◆</span> Actions</h2></header>
                <div class="mw-action-list">
                    <button type="button" data-studio-side="mywork"><span>▤</span><span>Go to My Work</span><b>›</b></button>
                    <button type="button" data-studio-focus-search><span>⌕</span><span>Search Studio</span><b>›</b></button>
                </div>
            </section>

            <section class="mw-list-panel mw-quick-links-panel studio-context-card">
                <header><h2><span class="mw-title-icon links">↗</span> Quick Links</h2></header>
                <div class="mw-quick-link-list">
                    ${quickLinks.filter(([key]) => !['mywork', currentView].includes(key)).slice(0,4).map(([key, label, icon]) =>
                        '<button type="button" data-studio-side="' + key + '"><span>' + icon + '</span><span>' + escText(label) + '</span></button>'
                    ).join('')}
                </div>
            </section>`;

        rail.querySelectorAll('[data-studio-side]').forEach(button => {
            button.onclick = () => window.studioNavigate?.(button.dataset.studioSide);
        });
        rail.querySelector('[data-studio-focus-search]')?.addEventListener('click', () => {
            const search = document.getElementById('studioSearch');
            search?.focus();
        });
    }

    function ensureBreadcrumb() {
        if (!breadcrumb || breadcrumb.children.length) return;
        window.studioSetBreadcrumb?.([{ label: labelFor(currentView), view: currentView }]);
    }

    window.studioSetBreadcrumb = items => {
        if (!breadcrumb) return;
        const supplied = Array.isArray(items) ? items.filter(Boolean) : [];
        const crumbs = supplied[0]?.view === 'mywork'
            ? supplied
            : [{ label: 'My Work', view: 'mywork' }, ...supplied];
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
            if (view !== 'mywork'
                && !breadcrumb?.querySelector('[data-studio-crumb="mywork"]')
                && !breadcrumb?.querySelector('[data-shell-nav="mywork"]')) {
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
        ensureBreadcrumb();
        if (!window.history.state?.studioView) window.history.replaceState({ studioView: currentView }, '', window.location.href);
        window.addEventListener('popstate', event => {
            const view = event.state?.studioView || 'mywork';
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
