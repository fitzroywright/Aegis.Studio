// Canonical presentation for page, section and account access states.
(() => {
    'use strict';
    let user = null;
    let accountState = '';
    const escape = value => String(value ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
    const pending = () => accountState === 'PendingAccess';
    const provider = value => ({ActiveDirectory:'Active Directory',EntraId:'Entra ID',LocalDatabase:'Local account',Mock:'Demo account'})[value] || 'Configured sign-in service';
    function setSession(payload) {
        user = payload.user || payload;
        accountState = payload.accountState || user.accountState || '';
        document.body.dataset.studioAccess = pending() ? 'pending' : 'active';
    }
    function render(host, {kind = 'page', title = 'this page', description = '', action = null} = {}) {
        const awaiting = kind === 'pending', section = kind === 'section';
        const heading = awaiting ? 'You’re signed in. Your access is pending.' : section ? `You don’t have access to ${title}` : 'You don’t have access to this page';
        const body = description || (awaiting
            ? 'Your Studio account has been created. A Studio administrator needs to assign your roles and workspace access before you can start working.'
            : section ? 'You can continue working with the information available to your role. Contact your Studio administrator if you need access to this section.'
            : `Your current role does not allow you to view ${title}. Contact your Studio administrator if you need access.`);
        host.innerHTML = `<section class="studio-access-state${section?' studio-access-section':''}" aria-label="${escape(awaiting?'Access pending':section?'Restricted section':'Page access denied')}"><div class="studio-access-card"><span class="studio-access-icon" aria-hidden="true"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5"><rect x="5" y="10" width="14" height="11" rx="2"/><path d="M8 10V7a4 4 0 0 1 8 0v3m-4 5v2"/></svg></span><h1 tabindex="-1">${escape(heading)}</h1><p>${escape(body)}</p>${awaiting?`<dl class="studio-access-identity"><div><dt>Signed in as</dt><dd>${escape(user?.displayName || user?.userName || 'Studio user')}</dd></div><div><dt>Sign-in method</dt><dd>${escape(provider(user?.authenticationSource))}</dd></div><div><dt>Access status</dt><dd>Awaiting role and workspace access</dd></div></dl>`:''}<div class="studio-access-actions">${action?`<button type="button" class="primary" data-access-action>${escape(action.label)}</button>`:awaiting?'<button type="button" class="primary" data-access-about>View About</button><button type="button" class="action" data-access-signout>Sign out</button>':section?'':'<button type="button" class="primary" data-access-home>Go to Today</button><button type="button" class="action" data-access-about>View About</button>'}</div><p class="studio-access-help" role="status">Contact your Studio administrator to review your access.</p></div></section>`;
        // A section is a subheading inside its containing page.
        if (section) {
            const heading = host.querySelector('h1'), subheading = document.createElement('h2');
            subheading.textContent = heading.textContent;heading.replaceWith(subheading);
        }
        host.querySelector('[data-access-home]')?.addEventListener('click', () => window.studioNavigate('dashboard'));
        host.querySelector('[data-access-about]')?.addEventListener('click', () => window.studioNavigate('about'));
        host.querySelector('[data-access-action]')?.addEventListener('click', action?.run);
        host.querySelector('[data-access-signout]')?.addEventListener('click', async event => {
            const button=event.currentTarget;button.disabled = true;
            try {
                const response = await fetch('/api/auth/logout', {method:'POST'});
                if (!response.ok && response.status !== 401) throw Error('Sign out could not be completed. Try again.');
                sessionStorage.removeItem('studio-announcements-login');window.location.replace('/login');
            } catch (error) { if(button.isConnected){host.querySelector('[role=status]').textContent = error.message;button.disabled = false;} }
        });
    }
    function page(options = {}) {
        const conversation = document.getElementById('conversationView'), host = document.getElementById('genericView');
        conversation?.classList.add('hidden');host.classList.remove('hidden');
        window.studioShellMountContext?.([], 'access');
        const tools = document.getElementById('studioPageTools');if (tools) tools.replaceChildren();
        const count = document.getElementById('studioFooterRate');if (count) count.textContent = '';
        render(host, options);host.querySelector('h1')?.focus({preventScroll:true});
    }
    window.StudioAccess = {setSession, pending, render, page};
})();
