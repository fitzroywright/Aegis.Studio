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
        // Messages are contextual to Workspace/Job. Do not create a duplicate top-level destination.
        // showMessages remains available to contextual actions and direct links.
    }

    async function showMessages() {
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

    window.showStudioMessages = showMessages;

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
                        ${users.map(user => `<option value="${esc(user.userName)}">${esc(studioFirstName(user))}</option>`).join('')}
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
        const [messages,threadAttachments] = await Promise.all([json(`/api/messages/threads/${id}`),json(`/api/messages/threads/${id}/attachments`).catch(()=>[])]);
        const pane = document.getElementById('messageConversationPane');
        if (!pane) return;
        pane.innerHTML = `
            <div class="message-conversation-header"><div><h2>${esc(thread?.title || 'Messages')}</h2><p>${esc(thread?.scopeType || '')}</p></div></div>
            <div id="studioMessageHistory" class="studio-message-history">
                ${messages.map(message=>renderMessage(message,threadAttachments)).join('') || '<div class="empty-state compact"><p>No messages yet. Start the conversation.</p></div>'}
            </div>
            <form id="studioMessageComposer" class="studio-message-composer">
                <div class="message-composer-input-wrap">
                    <textarea id="studioMessageBody" maxlength="10000" placeholder="Write a message… Use @ to mention a Studio user." required></textarea>
                    <div id="mentionSuggestions" class="mention-suggestions" hidden></div>
                </div>
                <label class="action message-attachment-control">Attach document<input id="studioMessageFile" type="file" hidden></label>
                <button class="action" id="studioWhatsAppSend" type="button" title="Send this message to the photographer via WhatsApp">WhatsApp</button><button class="primary" type="submit">Send</button>
            </form>`;
        document.getElementById('studioMessageComposer').onsubmit = sendMessage;
        document.getElementById('studioWhatsAppSend').onclick = sendWhatsApp;
        const textarea = document.getElementById('studioMessageBody');
        textarea.addEventListener('input', () => updateMentionSuggestions(textarea));
        textarea.addEventListener('keydown', handleMentionKeys);
        await loadMentionUsers();
        await fetch(`/api/messages/threads/${id}/read`, { method: 'POST' });
        await loadThreads();
        const history = document.getElementById('studioMessageHistory');
        if (history) history.scrollTop = history.scrollHeight;
    }

    function renderMessage(message, attachments = []) {
        const files=(attachments||[]).filter(file=>file.messageId===message.id);
        return `<article class="studio-chat-message">
            <div class="studio-chat-meta"><strong>${esc(studioFirstName(message.senderUserName))}</strong><span>${new Date(message.createdAtUtc).toLocaleString()}</span></div>
            <div class="studio-chat-body">${renderMentions(message.body)}</div>
            ${files.length?`<div class="studio-message-files">${files.map(file=>`<a href="/api/message-attachments/${file.id}" target="_blank" rel="noopener"><strong>${esc(file.fileName)}</strong><small>${formatBytes(file.size)}</small></a>`).join('')}</div>`:''}
        </article>`;
    }

    function renderMentions(body) {
        const safe = esc(body);
        return safe.replace(/(^|\s)@([A-Za-z0-9._\\-]+)/g, '$1<span class="studio-mention">@$2</span>');
    }

    async function loadMentionUsers() {
        if (!mentionUsers.length) {
            try { mentionUsers = await json('/api/messages/users'); studioRememberPeople(mentionUsers); } catch { mentionUsers = []; }
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
        box.innerHTML = matches.map((user, index) => `<button type="button" class="mention-option${index === 0 ? ' active' : ''}" data-mention-user="${esc(user.userName)}"><strong>${esc(studioFirstName(user))}</strong></button>`).join('');
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

    async function sendWhatsApp() {
        const body = document.getElementById('studioMessageBody')?.value.trim();
        if (!body || !selectedThread) return;
        const response = await fetch(`/api/messages/threads/${selectedThread}/whatsapp`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ body })
        });
        if (!response.ok) {
            const detail = await response.text();
            alert(detail || 'The WhatsApp message could not be queued.');
            return;
        }
        document.getElementById('studioMessageBody').value = '';
    }

    async function sendMessage(event) {
        event.preventDefault();
        const body = document.getElementById('studioMessageBody').value.trim();
        if (!body || !selectedThread) return;
        const threads=await json('/api/messages/threads');
        const thread=threads.find(item=>item.id===selectedThread);
        const sent=await json(`/api/messages/threads/${selectedThread}`, {
            method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ body })
        });
        const file=document.getElementById('studioMessageFile')?.files?.[0];
        if(file){
            if(thread?.scopeType==='Job'&&thread.scopeId){
                const job=await json('/api/jobs/'+thread.scopeId);
                if(!job.conversationId)throw new Error('This job has no linked conversation for Conversation Files.');
                const data=new FormData();data.append('file',file);
                const upload=await fetch('/api/conversations/'+job.conversationId+'/attachments?visibility=Internal',{method:'POST',body:data});
                if(!upload.ok)throw new Error(await upload.text());
                const attachment=await upload.json();
                const link=await fetch('/api/attachments/'+attachment.id+'/message-link',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({messageId:sent.id,messageKind:'JobMessage',jobId:thread.scopeId})});
                if(!link.ok)throw new Error(await link.text());
            }else{
                const data=new FormData();data.append('file',file);
                const upload=await fetch('/api/messages/'+sent.id+'/attachments',{method:'POST',body:data});
                if(!upload.ok)throw new Error(await upload.text());
            }
        }
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
        const sender = payload.senderUserName ? studioFirstName(payload.senderUserName) : 'A Studio user';
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

    async function showAwayDigestOnce(attempt = 0) {
        if (sessionStorage.getItem('studio-away-digest-shown') === '1') return;
        let threads;
        try {
            threads = await json('/api/messages/threads');
        } catch {
            // Startup can race authentication/session initialization. Retry briefly instead
            // of silently losing the once-per-session digest.
            if (attempt < 3) setTimeout(() => showAwayDigestOnce(attempt + 1), 1200 * (attempt + 1));
            return;
        }
        const unread = threads
            .filter(x => Number(x.unreadCount || 0) > 0)
            .sort((a,b) => new Date(b.lastMessageAtUtc || b.createdAtUtc || 0) - new Date(a.lastMessageAtUtc || a.createdAtUtc || 0));
        if (!unread.length) { sessionStorage.setItem('studio-away-digest-shown', '1'); return; }

        const digest = await Promise.all(unread.slice(0, 8).map(async thread => {
            let preview = '';
            let sender = '';
            try {
                const messages = await json(`/api/messages/threads/${thread.id}`);
                const latest = Array.isArray(messages) ? messages.at(-1) : null;
                preview = latest?.body || '';
                sender = latest?.senderUserName ? studioFirstName(latest.senderUserName) : '';
            } catch { }
            return { thread, preview, sender };
        }));

        let dialog = document.getElementById('studioAwayDigest');
        if (!dialog) {
            dialog = document.createElement('dialog');
            dialog.id = 'studioAwayDigest';
            dialog.className = 'studio-away-digest';
            document.body.appendChild(dialog);
        }
        const total = unread.reduce((sum, x) => sum + Number(x.unreadCount || 0), 0);
        const remaining = Math.max(0, unread.length - digest.length);
        dialog.innerHTML = `<div class="away-digest-card">
            <div class="away-digest-heading">
                <div class="away-digest-mark" aria-hidden="true">✉</div>
                <div><span class="away-digest-kicker">MESSAGE DIGEST</span><h2>While You Were Away</h2><p>${total} unread message${total === 1 ? '' : 's'} across ${unread.length} conversation${unread.length === 1 ? '' : 's'}.</p></div>
                <button type="button" class="message-dialog-close" aria-label="Close">×</button>
            </div>
            <div class="away-digest-list">${digest.map(({thread,preview,sender}) => `<button type="button" data-away-thread="${thread.id}">
                <span class="away-digest-thread-icon" aria-hidden="true">✉</span>
                <span class="away-digest-copy"><strong>${esc(thread.title || 'Message')}</strong><small>${esc([sender, thread.scopeType].filter(Boolean).join(' · '))}</small>${preview ? `<span>${esc(String(preview).replace(/\\s+/g,' ').trim().slice(0,140))}</span>` : ''}</span>
                <span class="away-digest-meta"><b>${thread.unreadCount}</b><small>unread</small></span>
            </button>`).join('')}</div>
            ${remaining ? `<p class="away-digest-more">+${remaining} more unread conversation${remaining === 1 ? '' : 's'}</p>` : ''}
            <div class="away-digest-actions"><button type="button" class="action" id="dismissAwayDigest">Not now</button><button type="button" class="primary" id="openAllStudioMessages">Open Messages</button></div>
        </div>`;
        const close = () => { if (dialog.open) dialog.close(); sessionStorage.setItem('studio-away-digest-shown', '1'); };
        dialog.querySelector('.message-dialog-close').addEventListener('click', close);
        dialog.querySelector('#dismissAwayDigest').addEventListener('click', close);
        dialog.querySelector('#openAllStudioMessages').addEventListener('click', async () => { close(); await showMessages(); });
        dialog.querySelectorAll('[data-away-thread]').forEach(button => button.addEventListener('click', async () => { const id = button.dataset.awayThread; close(); await showMessages(); await openThread(id); }));
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
        const [messages,threadAttachments] = await Promise.all([json(`/api/messages/threads/${selectedThread}`),json(`/api/messages/threads/${selectedThread}/attachments`).catch(()=>[])]);
        const history = document.getElementById('studioMessageHistory');
        if (history) {
            history.innerHTML = messages.map(message=>renderMessage(message,threadAttachments)).join('');
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
    // Give authentication and the initial shell a moment to settle, then show the digest once.
    setTimeout(() => showAwayDigestOnce(), 450);
})();
