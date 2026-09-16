(() => {
    let selectedThread = null;
    let refreshTimer = null;

    function installNavigation() {
        const rail = document.querySelector('.rail');
        if (!rail || rail.querySelector('[data-view="messages"]')) return;
        const button = document.createElement('button');
        button.className = 'brand-button';
        button.dataset.view = 'messages';
        button.innerHTML = '<span class="nav-icon message-nav-icon">✉</span><span class="nav-label">Messages</span><span id="messageUnreadBadge" class="message-unread-badge" hidden></span>';
        button.onclick = showMessages;
        const jobsButton = rail.querySelector('[data-view="jobs"]');
        rail.insertBefore(button, jobsButton || rail.firstChild);
    }

    async function showMessages() {
        activateNavigation('messages');
        conversationView.classList.add('hidden');
        genericView.classList.remove('hidden');
        genericView.innerHTML = `
            <div class="studio-messaging">
                <aside class="message-thread-pane">
                    <div class="message-pane-heading">
                        <div><h1>Messages</h1><p>Studio staff conversations</p></div>
                        <button class="primary" id="newDirectMessage">New</button>
                    </div>
                    <div class="message-scope-actions">
                        <button class="action" id="workspaceTeamMessage" ${selectedWorkspace ? '' : 'disabled'}>Workspace team</button>
                    </div>
                    <div id="messageThreadList" class="message-thread-list"></div>
                </aside>
                <section id="messageConversationPane" class="message-conversation-pane">
                    <div class="empty-state"><h2>Select a message thread</h2><p>Direct, workspace-team and job-team conversations stay inside Studio.</p></div>
                </section>
            </div>`;
        document.getElementById('newDirectMessage').onclick = startDirect;
        document.getElementById('workspaceTeamMessage').onclick = openWorkspaceThread;
        await loadThreads();
        startRefresh();
    }

    async function loadThreads() {
        const threads = await json('/api/messages/threads');
        const list = document.getElementById('messageThreadList');
        if (!list) return;
        list.innerHTML = threads.map(thread => `
            <button class="message-thread ${selectedThread === thread.id ? 'active' : ''}" data-thread-id="${thread.id}">
                <div><strong>${esc(thread.title)}</strong><small>${esc(thread.scopeType)}</small></div>
                ${thread.unreadCount ? `<span class="thread-unread">${thread.unreadCount}</span>` : ''}
            </button>`).join('') || '<div class="empty-state compact"><p>No messages yet.</p></div>';
        list.querySelectorAll('[data-thread-id]').forEach(button => button.onclick = () => openThread(button.dataset.threadId));
        updateUnreadBadge(threads.reduce((sum, item) => sum + Number(item.unreadCount || 0), 0));
    }

    async function startDirect() {
        const userName = prompt('Studio user name');
        if (!userName) return;
        const thread = await json('/api/messages/threads/direct', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ userName })
        });
        await loadThreads();
        await openThread(thread.id);
    }

    async function openWorkspaceThread() {
        if (!selectedWorkspace) return;
        const thread = await json(`/api/messages/threads/Workspace/${selectedWorkspace}`, { method: 'POST' });
        await loadThreads();
        await openThread(thread.id);
    }

    async function openThread(id) {
        selectedThread = id;
        const threads = await json('/api/messages/threads');
        const thread = threads.find(item => item.id === id);
        const messages = await json(`/api/messages/threads/${id}`);
        const pane = document.getElementById('messageConversationPane');
        if (!pane) return;
        pane.innerHTML = `
            <div class="message-conversation-header"><div><h2>${esc(thread?.title || 'Messages')}</h2><p>${esc(thread?.scopeType || '')}</p></div></div>
            <div id="studioMessageHistory" class="studio-message-history">
                ${messages.map(renderMessage).join('') || '<div class="empty-state compact"><p>No messages yet. Start the conversation.</p></div>'}
            </div>
            <form id="studioMessageComposer" class="studio-message-composer">
                <textarea id="studioMessageBody" maxlength="10000" placeholder="Write a message…" required></textarea>
                <button class="primary" type="submit">Send</button>
            </form>`;
        document.getElementById('studioMessageComposer').onsubmit = sendMessage;
        await fetch(`/api/messages/threads/${id}/read`, { method: 'POST' });
        await loadThreads();
        const history = document.getElementById('studioMessageHistory');
        if (history) history.scrollTop = history.scrollHeight;
    }

    function renderMessage(message) {
        return `<article class="studio-chat-message">
            <div class="studio-chat-meta"><strong>${esc(message.senderUserName)}</strong><span>${new Date(message.createdAtUtc).toLocaleString()}</span></div>
            <div class="studio-chat-body">${esc(message.body)}</div>
        </article>`;
    }

    async function sendMessage(event) {
        event.preventDefault();
        const body = document.getElementById('studioMessageBody').value.trim();
        if (!body || !selectedThread) return;
        await json(`/api/messages/threads/${selectedThread}`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ body })
        });
        document.getElementById('studioMessageBody').value = '';
        await openThread(selectedThread);
    }

    function updateUnreadBadge(count) {
        const badge = document.getElementById('messageUnreadBadge');
        if (!badge) return;
        badge.hidden = count < 1;
        badge.textContent = count > 99 ? '99+' : String(count);
    }

    async function refreshIfVisible() {
        try {
            const messagesButton = document.querySelector('[data-view="messages"]');
            if (messagesButton?.classList.contains('active')) {
                await loadThreads();
                if (selectedThread) {
                    const messages = await json(`/api/messages/threads/${selectedThread}`);
                    const history = document.getElementById('studioMessageHistory');
                    if (history) history.innerHTML = messages.map(renderMessage).join('');
                }
            } else {
                const threads = await json('/api/messages/threads');
                updateUnreadBadge(threads.reduce((sum, item) => sum + Number(item.unreadCount || 0), 0));
            }
        } catch { }
    }

    function startRefresh() {
        if (refreshTimer) clearInterval(refreshTimer);
        refreshTimer = setInterval(refreshIfVisible, 5000);
    }

    window.openStudioWorkspaceMessages = async function(workspaceId) {
        await showMessages();
        const thread = await json(`/api/messages/threads/Workspace/${workspaceId}`, { method: 'POST' });
        await openThread(thread.id);
    };

    window.openStudioJobMessages = async function(jobId) {
        await showMessages();
        const thread = await json(`/api/messages/threads/Job/${jobId}`, { method: 'POST' });
        await openThread(thread.id);
    };

    installNavigation();
    startRefresh();
    refreshIfVisible();
})();
