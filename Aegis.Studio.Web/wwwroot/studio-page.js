// Shared Today page structure. Renderers call this directly; no renderer is wrapped or replaced.
(() => {
    'use strict';
    let revision = 0;
    let notifications = null;
    let notificationAt = 0;
    const escape = value => String(value ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
    const card = (title, content, className = '', footer = '') => `<section class="st-card ${className}"><header><h2>${escape(title)}</h2></header><div class="st-card-body">${content}</div>${footer}</section>`;
    function begin(view) {
        const version = ++revision;
        const matches = () => !document.body.dataset.studioView || document.body.dataset.studioView === view || (view === 'management' && document.body.dataset.studioView === 'reports') || (view === 'pipeline' && document.body.dataset.studioView === 'schedule');
        return () => version === revision && matches();
    }
    function html({title, tools = '', content = '', summary = '', attention = '', className = '', scroll = false, showHeader = true, summaryTitle = 'Overview'}) {
        return `<section class="st-page ${className}" aria-label="${escape(title)}"><template data-studio-overview>${card(summaryTitle,summary || '<p>No summary available.</p>')}</template><section class="st-board">${showHeader ? `<header class="st-toolbar"><h1>${escape(title)}</h1><div class="st-tools">${tools}</div></header>` : ''}<div class="st-content${scroll ? ' st-card-scroll' : ''}">${content}</div></section>${attention ? `<div data-studio-context><div data-studio-attention>${attention}</div></div>` : ''}</section>`;
    }
    function metrics(items) {
        return items.map(([label, value]) => `<div class="st-stat"><span>${escape(label)}</span><strong>${escape(value)}</strong></div>`).join('');
    }
    async function readNotifications() {
        if (!notifications || Date.now() - notificationAt > 15000) {
            notificationAt = Date.now();
            notifications = json('/api/notifications?count=40').catch(error => {notifications = null;throw error;});
        }
        const result = await notifications;
        return Array.isArray(result) ? result : [];
    }
    function bind(host, current = () => host.isConnected) {
        const root = host.querySelector('.st-page');
        const overview = root?.querySelector('[data-studio-overview]');
        if(overview)window.studioShellSetOverview?.(overview.innerHTML);
        const live = () => current() && root?.isConnected && host.querySelector('.st-page') === root;
        mountContext(host);
    }
    function query(host, selector) {
        return host.querySelector(selector) || document.getElementById('studioContextRail')?.querySelector(selector) || document.getElementById('studioPageAttention')?.querySelector(selector) || null;
    }
    function queryAll(host, selector) {
        return [...new Set([...host.querySelectorAll(selector), ...(document.getElementById('studioContextRail')?.querySelectorAll(selector) || []), ...(document.getElementById('studioPageAttention')?.querySelectorAll(selector) || [])])];
    }
    function mountContext(host, view) {
        const context = host.querySelector('[data-studio-context]');
        if (!context) return;
        const attention = context.querySelector('[data-studio-attention]');
        if(window.studioShellMountAttention){attention?.remove();window.studioShellMountAttention(attention,view);}
        const overview = context.querySelector('[data-rail-card="overview"]');
        if (overview) window.studioShellSetOverview?.(overview, view);
        window.studioShellMountContext?.(context ? [...context.children] : [], view);
        // The shell moves the same nodes; standalone hosts retain their existing content.
        if (window.studioShellMountContext) context?.remove();
    }
    function setCount(value) {
        const page=document.querySelector('#genericView .st-page');
        if(page)page.dataset.itemCount=value;
        const target = document.getElementById('studioFooterRate');
        if (target) target.textContent = value;
    }
    window.StudioPage = {html, card, metrics, bind, begin, setCount, escape, query, queryAll, mountContext, invalidate(){++revision;}};
    // Legacy page renderers share this explicit global instead of app.js lexical scope.
    window.esc = escape;
})();



