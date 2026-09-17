(() => {
    const signalRRecordSeparator = '\u001e';
    let selectedThread = null;
    let refreshTimer = null;
    let lastUnreadTotal = 0;
    let knownThreadUnread = new Map();
    let mentionUsers = [];
    let toastTimer = null;
    let realtimeSocket = null;
    let realtimeReconnectTimer = null;
    let realtimeInvocationId = 0;
    let realtimeHandshakeComplete = false;
    let joinedRealtimeThread = null;
    let recentMentionThread = null;
    let recentMentionAt = 0;

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
        if (list) {
            list.innerHTML = threads.map(thread => `
                <button class="message-thread ${selectedThread === thread.id ? 'active' : ''}" data-thread-id="${thread.id}">
                    <div><strong>${esc(thread.title)}</strong><small>${esc(thread.scopeType)}</small></div>
                    ${thread.unreadCount ? `<span class="thread-unread">${thread.unreadCount}</span>` : ''}
                </button>`).join('') || '<div class="empty-state compact"><p>No messages yet.</p></div>';
            list.querySelectorAll('[data-thread-id]').forEach(button => button.onclick = () => openThread(button.dataset.threadId));
        }
        handleUnreadChanges(threads);
        return threads;
    }

    async function startDirect() {
        const users = await loadMentionUsers();
        if (!users.length) {
            alert('No other Studio users are available for direct messaging yet.');
            return;
        }
        let dialog = document.getElementById('directMessageDialog');
        if (!dialog) {
            dialog = document.createElement('dialog');
            dialog.id = 'directMessageDialog';
            dialog.className = 'direct-message-dialog';
            document.body.appendChild(dialog);
        }
        dialog.innerHTML = `
            <form method="dialog" class="direct-message-form">
                <div class="message-dialog-heading"><div><h2>New message</h2><p>Start a private Studio conversation.</p></div><button type="button" class="message-dialog-close" aria-label="Close">×</button></div>
                <label>Studio user
                    <select id="directMessageUser" required>
                        ${users.map(user => `<option value="${esc(user.userName)}">${esc(user.displayName || user.userName)}${user.displayName && user.displayName !== user.userName ? ` · ${esc(user.userName)}` : ''}</option>`).join('')}
                    </select>
                </label>
                <div class="dialog-actions"><button type="button" class="direct-message-cancel">Cancel</button><button type="submit" class="primary">Open conversation</button></div>
            </form>`;
        const close = () => dialog.close();
        dialog.querySelector('.message-dialog-close').onclick = close;
        dialog.querySelector('.direct-message-cancel').onclick = close;
        dialog.querySelector('form').onsubmit = async event => {
            event.preventDefault();
            const userName = document.getElementById('directMessageUser').value;
            if (!userName) return;
            const thread = await json('/api/messages/threads/direct', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ userName })
            });
            dialog.close();
            await loadThreads();
            await openThread(thread.id);
        };
        dialog.showModal();
    }

    async function openWorkspaceThread() {
        if (!selectedWorkspace) return;
        const thread = await json(`/api/messages/threads/Workspace/${selectedWorkspace}`, { method: 'POST' });
        await loadThreads();
        await openThread(thread.id);
    }

    async function openThread(id) {
        const previousThread = selectedThread;
        selectedThread = id;
        await joinRealtimeThread(id, previousThread);
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
                <div class="message-composer-input-wrap">
                    <textarea id="studioMessageBody" maxlength="10000" placeholder="Write a message… Use @ to mention a Studio user." required></textarea>
                    <div id="mentionSuggestions" class="mention-suggestions" hidden></div>
                </div>
                <button class="primary" type="submit">Send</button>
            </form>`;
        document.getElementById('studioMessageComposer').onsubmit = sendMessage;
        const textarea = document.getElementById('studioMessageBody');
        textarea.addEventListener('input', () => updateMentionSuggestions(textarea));
        textarea.addEventListener('keydown', handleMentionKeys);
        await loadMentionUsers();
        await fetch(`/api/messages/threads/${id}/read`, { method: 'POST' });
        await loadThreads();
        const history = document.getElementById('studioMessageHistory');
        if (history) history.scrollTop = history.scrollHeight;
    }

    function renderMessage(message) {
        return `<article class="studio-chat-message">
            <div class="studio-chat-meta"><strong>${esc(message.senderUserName)}</strong><span>${new Date(message.createdAtUtc).toLocaleString()}</span></div>
            <div class="studio-chat-body">${renderMentions(message.body)}</div>
        </article>`;
    }

    function renderMentions(body) {
        const safe = esc(body);
        return safe.replace(/(^|\s)@([A-Za-z0-9._\\-]+)/g, '$1<span class="studio-mention">@$2</span>');
    }

    async function loadMentionUsers() {
        if (!mentionUsers.length) {
            try { mentionUsers = await json('/api/messages/users'); } catch { mentionUsers = []; }
        }
        return mentionUsers;
    }

    function updateMentionSuggestions(textarea) {
        const box = document.getElementById('mentionSuggestions');
        if (!box) return;
        const before = textarea.value.slice(0, textarea.selectionStart);
        const match = before.match(/(?:^|\s)@([A-Za-z0-9._\\-]*)$/);
        if (!match) { box.hidden = true; box.innerHTML = ''; return; }
        const needle = match[1].toLowerCase();
        const matches = mentionUsers.filter(user =>
            user.userName.toLowerCase().includes(needle) || (user.displayName || '').toLowerCase().includes(needle)).slice(0, 8);
        if (!matches.length) { box.hidden = true; box.innerHTML = ''; return; }
        box.innerHTML = matches.map((user, index) => `<button type="button" class="mention-option${index === 0 ? ' active' : ''}" data-mention-user="${esc(user.userName)}"><strong>${esc(user.displayName || user.userName)}</strong><small>${esc(user.userName)}</small></button>`).join('');
        box.hidden = false;
        box.querySelectorAll('[data-mention-user]').forEach(button => button.onclick = () => insertMention(textarea, button.dataset.mentionUser));
    }

    function handleMentionKeys(event) {
        const box = document.getElementById('mentionSuggestions');
        if (!box || box.hidden) return;
        const options = [...box.querySelectorAll('.mention-option')];
        if (!options.length) return;
        let current = Math.max(0, options.findIndex(x => x.classList.contains('active')));
        if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
            event.preventDefault();
            options[current].classList.remove('active');
            current = event.key === 'ArrowDown' ? (current + 1) % options.length : (current - 1 + options.length) % options.length;
            options[current].classList.add('active');
        } else if (event.key === 'Enter' || event.key === 'Tab') {
            event.preventDefault();
            insertMention(event.target, options[current].dataset.mentionUser);
        } else if (event.key === 'Escape') {
            box.hidden = true;
        }
    }

    function insertMention(textarea, userName) {
        const cursor = textarea.selectionStart;
        const before = textarea.value.slice(0, cursor);
        const after = textarea.value.slice(cursor);
        const replaced = before.replace(/@([A-Za-z0-9._\\-]*)$/, `@${userName} `);
        textarea.value = replaced + after;
        textarea.focus();
        textarea.selectionStart = textarea.selectionEnd = replaced.length;
        const box = document.getElementById('mentionSuggestions');
        if (box) box.hidden = true;
    }

    async function sendMessage(event) {
        event.preventDefault();
        const body = document.getElementById('studioMessageBody').value.trim();
        if (!body || !selectedThread) return;
        await json(`/api/messages/threads/${selectedThread}`, {
            method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ body })
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

    function handleUnreadChanges(threads) {
        const total = threads.reduce((sum, item) => sum + Number(item.unreadCount || 0), 0);
        updateUnreadBadge(total);
        if (lastUnreadTotal > 0 && total > lastUnreadTotal) {
            const changed = threads.find(thread => Number(thread.unreadCount || 0) > Number(knownThreadUnread.get(thread.id) || 0));
            const recentlyMentioned = changed && recentMentionThread === changed.id && Date.now() - recentMentionAt < 4000;
            if (changed && selectedThread !== changed.id && !recentlyMentioned) showMessageToast(changed);
        }
        knownThreadUnread = new Map(threads.map(thread => [thread.id, Number(thread.unreadCount || 0)]));
        lastUnreadTotal = total;
    }

    function showMessageToast(thread) {
        showToast('New Studio message', thread.title, `${Number(thread.unreadCount || 1)} unread`, thread.id);
    }

    function showMentionToast(payload) {
        recentMentionThread = payload.threadId;
        recentMentionAt = Date.now();
        const sender = payload.senderUserName || 'A Studio user';
        const preview = String(payload.body || '').replace(/\s+/g, ' ').trim();
        showToast('You were mentioned', sender, preview.length > 110 ? `${preview.slice(0, 107)}…` : preview, payload.threadId);
    }

    function showToast(kicker, title, detail, threadId) {
        let host = document.getElementById('studioMessageToast');
        if (!host) {
            host = document.createElement('button');
            host.id = 'studioMessageToast';
            host.className = 'studio-message-toast';
            document.body.appendChild(host);
        }
        host.innerHTML = `<span class="toast-kicker">${esc(kicker)}</span><strong>${esc(title)}</strong><span>${esc(detail || '')}</span>`;
        host.onclick = async () => { host.classList.remove('show'); await showMessages(); await openThread(threadId); };
        host.classList.add('show');
        clearTimeout(toastTimer);
        toastTimer = setTimeout(() => host.classList.remove('show'), 7000);
    }

    async function showAwayDigestOnce() {
        if (sessionStorage.getItem('studio-away-digest-shown') === '1') return;
        let threads;
        try { threads = await json('/api/messages/threads'); } catch { return; }
        const unread = threads.filter(x => Number(x.unreadCount || 0) > 0);
        if (!unread.length) { sessionStorage.setItem('studio-away-digest-shown', '1'); return; }
        let dialog = document.getElementById('studioAwayDigest');
        if (!dialog) {
            dialog = document.createElement('dialog');
            dialog.id = 'studioAwayDigest';
            dialog.className = 'studio-away-digest';
            document.body.appendChild(dialog);
        }
        const total = unread.reduce((sum, x) => sum + Number(x.unreadCount || 0), 0);
        dialog.innerHTML = `<div class="away-digest-card"><div class="message-dialog-heading"><div><h2>While You Were Away</h2><p>${total} unread message${total === 1 ? '' : 's'} across ${unread.length} conversation${unread.length === 1 ? '' : 's'}.</p></div><button type="button" class="message-dialog-close">×</button></div><div class="away-digest-list">${unread.slice(0, 8).map(x => `<button type="button" data-away-thread="${x.id}"><span><strong>${esc(x.title)}</strong><small>${esc(x.scopeType)}</small></span><b>${x.unreadCount}</b></button>`).join('')}</div><div class="dialog-actions"><button type="button" class="primary" id="openAllStudioMessages">Open Messages</button></div></div>`;
        const close = () => { dialog.close(); sessionStorage.setItem('studio-away-digest-shown', '1'); };
        dialog.querySelector('.message-dialog-close').onclick = close;
        dialog.querySelector('#openAllStudioMessages').onclick = async () => { close(); await showMessages(); };
        dialog.querySelectorAll('[data-away-thread]').forEach(button => button.onclick = async () => { const id = button.dataset.awayThread; close(); await showMessages(); await openThread(id); });
        dialog.addEventListener('close', () => sessionStorage.setItem('studio-away-digest-shown', '1'), { once: true });
        dialog.showModal();
    }

    async function refreshIfVisible() {
        try {
            const messagesButton = document.querySelector('[data-view="messages"]');
            if (messagesButton?.classList.contains('active')) {
                await loadThreads();
                if (selectedThread) await refreshSelectedThread(false);
            } else {
                const threads = await json('/api/messages/threads');
                handleUnreadChanges(threads);
            }
        } catch { }
    }

    async function refreshSelectedThread(markRead) {
        if (!selectedThread) return;
        const messages = await json(`/api/messages/threads/${selectedThread}`);
        const history = document.getElementById('studioMessageHistory');
        if (history) {
            history.innerHTML = messages.map(renderMessage).join('');
            history.scrollTop = history.scrollHeight;
        }
        if (markRead) {
            await fetch(`/api/messages/threads/${selectedThread}/read`, { method: 'POST' });
            await loadThreads();
        }
    }

    function startRefresh() {
        if (refreshTimer) clearInterval(refreshTimer);
        refreshTimer = setInterval(refreshIfVisible, 5000);
    }

    async function connectRealtime() {
        clearTimeout(realtimeReconnectTimer);
        if (realtimeSocket && (realtimeSocket.readyState === WebSocket.OPEN || realtimeSocket.readyState === WebSocket.CONNECTING)) return;
        try {
            const response = await fetch('/hubs/messages/negotiate?negotiateVersion=1', { method: 'POST', credentials: 'same-origin' });
            if (!response.ok) throw new Error(`SignalR negotiate failed (${response.status}).`);
            const negotiation = await response.json();
            const token = negotiation.connectionToken || negotiation.connectionId;
            if (!token) throw new Error('SignalR negotiate did not return a connection token.');
            const scheme = location.protocol === 'https:' ? 'wss:' : 'ws:';
            const socket = new WebSocket(`${scheme}//${location.host}/hubs/messages?id=${encodeURIComponent(token)}`);
            realtimeSocket = socket;
            realtimeHandshakeComplete = false;
            socket.onopen = () => {
                socket.send(JSON.stringify({ protocol: 'json', version: 1 }) + signalRRecordSeparator);
            };
            socket.onmessage = event => handleRealtimeFrames(String(event.data || ''));
            socket.onerror = () => { try { socket.close(); } catch { } };
            socket.onclose = () => {
                if (realtimeSocket === socket) realtimeSocket = null;
                realtimeHandshakeComplete = false;
                joinedRealtimeThread = null;
                realtimeReconnectTimer = setTimeout(connectRealtime, 3000);
            };
        } catch {
            realtimeSocket = null;
            realtimeHandshakeComplete = false;
            realtimeReconnectTimer = setTimeout(connectRealtime, 5000);
        }
    }

    function handleRealtimeFrames(payload) {
        for (const frame of payload.split(signalRRecordSeparator)) {
            if (!frame.trim()) continue;
            let message;
            try { message = JSON.parse(frame); } catch { continue; }

            if (!realtimeHandshakeComplete) {
                if (message.error) {
                    try { realtimeSocket?.close(); } catch { }
                    return;
                }
                if (message.type === undefined) {
                    realtimeHandshakeComplete = true;
                    joinedRealtimeThread = null;
                    if (selectedThread) joinRealtimeThread(selectedThread, null);
                    continue;
                }
                continue;
            }

            if (message.type !== 1 || !message.target) continue;
            const target = String(message.target).toLowerCase();
            const value = Array.isArray(message.arguments) ? message.arguments[0] : null;
            if (target === 'mentionreceived' && value) {
                showMentionToast(value);
                refreshIfVisible();
            } else if (target === 'threadchanged') {
                const threadId = value;
                if (selectedThread && threadId === selectedThread) refreshSelectedThread(true);
                else refreshIfVisible();
            } else if (target === 'messagereceived' && value && selectedThread && value.threadId === selectedThread) {
                refreshSelectedThread(true);
            }
        }
    }

    function invokeHub(target, args) {
        if (!realtimeSocket || realtimeSocket.readyState !== WebSocket.OPEN || !realtimeHandshakeComplete) return false;
        realtimeSocket.send(JSON.stringify({
            type: 1,
            invocationId: String(++realtimeInvocationId),
            target,
            arguments: args || []
        }) + signalRRecordSeparator);
        return true;
    }

    async function joinRealtimeThread(threadId, previousThread) {
        if (previousThread && previousThread !== threadId && joinedRealtimeThread === previousThread) {
            invokeHub('LeaveThread', [previousThread]);
            joinedRealtimeThread = null;
        }
        if (joinedRealtimeThread === threadId) return;
        if (invokeHub('JoinThread', [threadId])) joinedRealtimeThread = threadId;
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
    connectRealtime();
    refreshIfVisible().then(showAwayDigestOnce);
})();
