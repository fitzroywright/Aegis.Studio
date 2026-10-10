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

    let inboxRows = [];
    let inboxThreads = [];
    let inboxFilter = '';
    let inboxQuery = '';
    let inboxSelection = null;
    let inboxLastLoad = 0;
    const inboxDate = value => value ? new Date(value).toLocaleString() : '—';
    const inboxSubject = message => String(message.subject || message.title || String(message.body || '').split('\\n')[0] || '(No subject)').slice(0,110);
    const inboxJob = thread => thread.scopeType === 'Job' ? (thread.title || thread.scopeId || 'Job') : '—';
    const inboxRecipient = (message,thread) => message.recipientUserName || message.recipient || (thread.scopeType === 'Job' ? 'Job team' : thread.scopeType === 'Workspace' ? 'Workspace team' : thread.scopeType === 'Direct' ? thread.title : 'Conversation');
    async function showMessages() {
        conversationView.classList.add('hidden');
        genericView.classList.remove('hidden');
        genericView.innerHTML = `
            <div class="studio-messaging studio-outlook-inbox">
                <aside class="studio-inbox-filters">
                    <header><h2>Messages</h2><small>Job filters</small></header>
                    <input id="studioInboxJobSearch" type="search" placeholder="Find a job…" aria-label="Find a job">
                    <div id="studioInboxJobFilters" class="studio-inbox-job-filters"></div>
                </aside>
                <section class="studio-inbox-center">
                    <header class="studio-inbox-toolbar"><div><h1>All Messages</h1><small id="studioInboxCount">Loading messages…</small></div><div class="studio-inbox-tools"><input id="studioInboxSearch" type="search" placeholder="Search messages…" aria-label="Search messages"><button class="action" id="studioInboxRefresh">Refresh</button><button class="primary" id="newDirectMessage">New</button></div></header>
                    <div class="studio-inbox-table-wrap"><table class="studio-inbox-table"><thead><tr><th>Job</th><th>Message title & contents</th><th>Sender</th><th>Recipient</th><th>Date</th></tr></thead><tbody id="studioInboxRows"><tr><td colspan="5">Loading messages…</td></tr></tbody></table></div>
                </section>
                <section id="messageConversationPane" class="message-conversation-pane studio-inbox-reading"><div class="empty-state"><h2>Select a message</h2><p>Read the conversation and reply here.</p></div></section>
            </div>`;
        document.getElementById('newDirectMessage').onclick = startDirect;
        document.getElementById('studioInboxRefresh').onclick = () => loadInbox(true);
        document.getElementById('studioInboxSearch').oninput = event => { inboxQuery = event.target.value.toLowerCase(); renderInbox(); };
        document.getElementById('studioInboxJobSearch').oninput = () => renderInboxFilters();
        await loadInbox(true);
        startRefresh();
    }
    async function loadInbox(force) {
        const body=document.getElementById('studioInboxRows');
        if(!body)return;
        if(force)body.innerHTML='<tr><td colspan="5">Loading messages…</td></tr>';
        try {
            const threads=await json('/api/messages/threads');
            inboxThreads=threads;
            const results=[];
            // Limit concurrent requests to avoid flooding the messaging service.
            let next=0;
            const workers=Array.from({length:Math.min(5,threads.length)},async()=>{
                while(next<threads.length){
                    const thread=threads[next++];
                    try {
                        const messages=await json('/api/messages/threads/'+encodeURIComponent(thread.id));
                        for(const message of messages)results.push({thread,message});
                    }catch { results.push({thread,error:true}); }
                }
            });
            await Promise.all(workers);
            inboxLastLoad=Date.now();
            inboxRows=results.filter(x=>x.message).sort((a,b)=>new Date(b.message.createdAtUtc||0)-new Date(a.message.createdAtUtc||0));
            renderInboxFilters();renderInbox();handleUnreadChanges(threads);
        }catch(error){body.innerHTML='<tr><td colspan="5">'+esc(error.message||'Unable to load messages.')+'</td></tr>';}
    }
    function renderInboxFilters(){
        const host=document.getElementById('studioInboxJobFilters');if(!host)return;
        const term=(document.getElementById('studioInboxJobSearch')?.value||'').toLowerCase();
        const jobs=inboxThreads.filter(x=>x.scopeType==='Job');
        const names=new Map(jobs.map(t=>[String(t.scopeId||t.id),inboxJob(t)]));
        const counts=new Map();
        for(const row of inboxRows)if(row.thread.scopeType==='Job'){const id=String(row.thread.scopeId||row.thread.id);counts.set(id,(counts.get(id)||0)+1);}
        host.innerHTML='<button data-inbox-job="" class="'+(!inboxFilter?'active':'')+'">All messages <span>'+inboxRows.length+'</span></button>'+[...names].filter(([,name])=>name.toLowerCase().includes(term)).sort((a,b)=>a[1].localeCompare(b[1])).map(([id,name])=>'<button data-inbox-job="'+esc(id)+'" class="'+(inboxFilter===id?'active':'')+'">'+esc(name)+' <span>'+(counts.get(id)||0)+'</span></button>').join('');
        host.querySelectorAll('[data-inbox-job]').forEach(button=>button.onclick=()=>{inboxFilter=button.dataset.inboxJob;renderInboxFilters();renderInbox();});
    }
    function renderInbox(){
        const body=document.getElementById('studioInboxRows');if(!body)return;
        const rows=inboxRows.filter(({thread,message})=>{
            const jobId=String(thread.scopeId||thread.id);
            if(inboxFilter&&jobId!==inboxFilter)return false;
            if(!inboxQuery)return true;
            return [inboxJob(thread),inboxSubject(message),message.body,message.senderUserName,inboxRecipient(message,thread),message.createdAtUtc].some(v=>String(v||'').toLowerCase().includes(inboxQuery));
        });
        document.getElementById('studioInboxCount').textContent=rows.length+' messages'+(inboxFilter?' in selected job':'');
        body.innerHTML=rows.map(({thread,message})=>'<tr tabindex="0" data-inbox-thread="'+esc(thread.id)+'" data-inbox-message="'+esc(message.id)+'" class="'+(inboxSelection===message.id?'selected':'')+'"><td>'+esc(inboxJob(thread))+'</td><td><strong>'+esc(inboxSubject(message))+'</strong><small>'+esc(String(message.body||'').replace(/\\s+/g,' ').slice(0,170))+'</small></td><td>'+esc(message.senderUserName||'Unknown')+'</td><td>'+esc(inboxRecipient(message,thread))+'</td><td>'+esc(inboxDate(message.createdAtUtc))+'</td></tr>').join('')||'<tr><td colspan="5">No messages match the selected filters.</td></tr>';
        body.querySelectorAll('[data-inbox-message]').forEach(row=>{
            const open=async()=>{inboxSelection=row.dataset.inboxMessage;renderInbox();await openThread(row.dataset.inboxThread);const msg=document.querySelector('#studioMessageHistory .studio-chat-message[data-message-id="'+CSS.escape(inboxSelection)+'"]');msg?.scrollIntoView({block:'nearest'});};
            row.onclick=open;row.onkeydown=e=>{if(e.key==='Enter'||e.key===' '){e.preventDefault();void open();}};
        });
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
        if(document.getElementById('studioInboxRows')) await loadInbox(false);
        else await loadThreads();
        const history = document.getElementById('studioMessageHistory');
        if (history) history.scrollTop = history.scrollHeight;
    }

    function renderMessage(message, attachments = []) {
        const files=(attachments||[]).filter(file=>file.messageId===message.id);
        return `<article class="studio-chat-message" data-message-id="${esc(message.id)}">
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
                if(document.getElementById('studioInboxRows')){if(Date.now()-inboxLastLoad>30000)await loadInbox(false);}else await loadThreads();
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
