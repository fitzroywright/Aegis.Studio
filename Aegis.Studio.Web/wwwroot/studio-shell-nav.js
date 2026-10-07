(() => {
    const breadcrumb = document.getElementById('studioBreadcrumb');
    const pageTools = document.getElementById('studioPageTools');
    const quickLinks = [
        ['mywork', 'My Work'],
        ['workspaces', 'Workspaces'],
        ['pipeline', 'Jobs'],
        ['production', 'Production'],
        ['frontdesk', 'Front Desk'],
        ['finance', 'Finance'],
        ['messages', 'Messages']
    ];
    const history = [];
    let currentView = document.body.dataset.studioView || 'mywork';
    let rendering = false;

    const escText = value => String(value ?? '').replace(/[&<>"']/g, character => ({
        '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
    })[character]);

    function labelFor(view) {
        return quickLinks.find(([key]) => key === view)?.[1]
            || String(view || 'Studio').replace(/(^|\s)\S/g, match => match.toUpperCase());
    }

    function ensureTools() {
        if (!pageTools || rendering) return;
        if (pageTools.querySelector('[data-studio-persistent-tools]')) return;
        rendering = true;
        const host = document.createElement('div');
        host.className = 'studio-persistent-tools';
        host.dataset.studioPersistentTools = '';
        host.innerHTML =
            '<button type="button" class="studio-shell-back" data-studio-back aria-label="Back within Studio">‹ <span>Back</span></button>' +
            '<details class="studio-quick-links">' +
                '<summary>Quick Links <span aria-hidden="true">⌄</span></summary>' +
                '<div class="studio-quick-links-menu" role="menu">' +
                    quickLinks.map(([key, label]) =>
                        '<button type="button" data-studio-quick="' + key + '">' + escText(label) + '</button>'
                    ).join('') +
                '</div>' +
            '</details>';
        pageTools.prepend(host);
        host.querySelector('[data-studio-back]').onclick = () => {
            const target = history.pop() || 'mywork';
            window.studioNavigate?.(target, { studioBack: true });
        };
        host.querySelectorAll('[data-studio-quick]').forEach(button => {
            button.onclick = () => window.studioNavigate?.(button.dataset.studioQuick);
        });
        rendering = false;
        refreshBackState();
    }

    function refreshBackState() {
        const button = pageTools?.querySelector('[data-studio-back]');
        if (!button) return;
        button.disabled = currentView === 'mywork' && history.length === 0;
        button.title = button.disabled ? 'You are at My Work' : 'Back within Studio';
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
    }

    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', initialize, { once: true });
    else initialize();
})();
