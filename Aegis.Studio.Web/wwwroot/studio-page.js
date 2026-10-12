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
    function html({title, tools = '', content = '', summary = '', attention = '', className = '', scroll = false, showMessagesCard = true}) {
        return `<section class="st-page ${className}" aria-label="${escape(title)}"><section class="st-board"><header class="st-toolbar"><h1>${escape(title)}</h1><div class="st-tools">${tools}</div></header><div class="st-content${scroll ? ' st-card-scroll' : ''}">${content}</div></section><aside class="st-right" aria-label="Page information">${card('Overview',summary || '<p>No summary available.</p>')}${card('Needs Attention',attention || '<p>No attention items reported.</p>','st-attention')}${showMessagesCard ? StudioMessageCard.html({id:'pageMessageCard',body:'<div data-page-messages role="status">Loading messages…</div>',footer:'<button type="button" data-page-send>✉ Send Message →</button>'}) : ''}</aside></section>`;
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
        const live = () => current() && root?.isConnected && host.querySelector('.st-page') === root;
        root?.querySelector('[data-page-send]')?.addEventListener('click',async () => {
            await window.studioNavigate('messages');
            if (document.body.dataset.studioView === 'messages') document.getElementById('newDirectMessage')?.click();
        });
        StudioMessageCard.bind(root?.querySelector('.smc-card'));
        const panel = root?.querySelector('[data-page-messages]');
        if (!panel) return;
        const load = async () => {
            try {
                const rows = (await readNotifications()).filter(n => /message|announcement|conversation|reply/i.test(n.eventType || n.type || n.title || '')).slice(0,6);
                if (!live()) return;
                panel.innerHTML = rows.map(n => `<button type="button" class="smc-row" data-page-job="${escape(n.referenceType === 'Job' ? n.referenceId : '')}"><span class="smc-copy"><strong>${escape(n.subject || n.title || 'Message')}</strong><small>${escape(n.body || '')}</small></span></button>`).join('') || '<p>No messages or announcements.</p>';
                panel.querySelectorAll('button').forEach(button => button.onclick = () => button.dataset.pageJob ? window.openWorkspaceJob(button.dataset.pageJob) : window.studioNavigate('messages'));
            } catch (error) {
                if (!live()) return;
                panel.innerHTML = '<p>Messages could not load.</p><button type="button" class="st-link" data-retry>Retry</button>';
                panel.querySelector('[data-retry]').onclick = load;
            }
        };
        void load();
    }
    function setCount(value) {
        const page=document.querySelector('#genericView .st-page');
        if(page)page.dataset.itemCount=value;
        const target = document.getElementById('studioFooterRate');
        if (target) target.textContent = value;
    }
    window.StudioPage = {html, card, metrics, bind, begin, setCount, escape, invalidate(){++revision;}};
})();

