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
    function html({title, tools = '', content = '', summary = '', attention = '', className = '', scroll = false, showMessagesCard = true, showHeader = true, summaryTitle = 'Overview'}) {
        return `<section class="st-page ${className}" aria-label="${escape(title)}"><template data-studio-overview>${card(summaryTitle,summary || '<p>No summary available.</p>')}</template><section class="st-board">${showHeader ? `<header class="st-toolbar"><h1>${escape(title)}</h1><div class="st-tools">${tools}</div></header>` : ''}<div class="st-content${scroll ? ' st-card-scroll' : ''}">${content}</div></section><aside class="st-right" aria-label="Page information"></aside></section>`;
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
        if(root)window.dispatchEvent(new CustomEvent('studio-page-overview-updated',{detail:{view:document.body.dataset.studioView,markup:root.querySelector('[data-studio-overview]')?.innerHTML||''}}));
        const live = () => current() && root?.isConnected && host.querySelector('.st-page') === root;
        // Messages and announcements are owned by the masthead slide-down only.
    }
    function setCount(value) {
        const page=document.querySelector('#genericView .st-page');
        if(page)page.dataset.itemCount=value;
        const target = document.getElementById('studioFooterRate');
        if (target) target.textContent = value;
    }
    window.StudioPage = {html, card, metrics, bind, begin, setCount, escape, invalidate(){++revision;}};
    // Legacy page renderers share this explicit global instead of app.js lexical scope.
    window.esc = escape;
})();


