(() => {
    const breadcrumb = document.getElementById('studioBreadcrumb');
    const pageTools = document.getElementById('studioPageTools');
    const shell = document.querySelector('.shell');
    const quickLinks = [
        ['mywork', 'My Work', '▤'],
        ['workspaces', 'Workspaces', '▰'],
        ['pipeline', 'Jobs', '▥'],
        ['production', 'Production', '◆'],
        ['frontdesk', 'Front Desk', '▣'],
        ['finance', 'Finance', '$'],
        ['messages', 'Messages', '✉']
    ];
    const history = [];
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
        if (!pageTools || renderingTools) return;
        if (pageTools.querySelector('[data-studio-persistent-tools]')) return;
        renderingTools = true;
        const host = document.createElement('div');
        host.className = 'studio-persistent-tools';
        host.dataset.studioPersistentTools = '';
        host.innerHTML = '<button type="button" class="studio-shell-back" data-studio-back aria-label="Back within Studio">‹ <span>Back</span></button>';
        pageTools.prepend(host);
        host.querySelector('[data-studio-back]').onclick = goBack;
        renderingTools = false;
        refreshBackState();
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
                    <button type="button" data-studio-side-back><span>‹</span><span>Back</span><b>›</b></button>
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
        rail.querySelector('[data-studio-side-back]')?.addEventListener('click', goBack);
        rail.querySelector('[data-studio-focus-search]')?.addEventListener('click', () => {
            const search = document.getElementById('studioSearch');
            search?.focus();
        });
    }

    function goBack() {
        const target = history.pop() || 'mywork';
        window.studioNavigate?.(target, { studioBack: true });
    }

    function refreshBackState() {
        const disabled = currentView === 'mywork' && history.length === 0;
        const button = pageTools?.querySelector('[data-studio-back]');
        if (button) {
            button.disabled = disabled;
            button.title = disabled ? 'You are at My Work' : 'Back within Studio';
        }
        const sideBack = document.querySelector('[data-studio-side-back]');
        if (sideBack) sideBack.disabled = disabled;
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
            if (!options.studioBack && previous && previous !== view) history.push(previous);
            currentView = view;
            const result = await original(view);
            ensureTools();
            ensureLeftRail();
            if (view !== 'mywork') window.studioSetBreadcrumb([{ label: labelFor(view), view }]);
            refreshBackState();
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
        refreshBackState();

        if (pageTools) {
            new MutationObserver(() => {
                ensureTools();
                refreshBackState();
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
                refreshBackState();
            }
        }).observe(document.body, { attributes: true, attributeFilter: ['data-studio-view'] });
    }

    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', initialize, { once: true });
    else initialize();
})();
