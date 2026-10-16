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
    let pickerJobs = [];
    let inboxThreads = [];
    let inboxFilter = '';
    let inboxQuery = '';
    let inboxSelection = null;
    let inboxOffset = 0;
    let inboxTotal = 0;
    let inboxSearchTimer = null;
    let addressEntries = [];
    let selectedRecipients = [];
    let sendRequestId = null;
    let selectedSubject = 'Studio message';
    let inboxLastLoad = 0;
    let inboxRevision = 0;
    let threadRevision = 0;
    const modeLabel = value => ({InternalOnly:'Studio only',ExternalOnly:'Email only',Combined:'Studio + email'})[value]||value;
    const deliveryLabel = value => ({ProviderAccepted:'Accepted by Microsoft 365',Attention:'Needs attention',Pending:'Queued',Retry:'Retry scheduled',Sending:'Sending',Delivered:'Delivered',Excluded:'Router copy excluded'})[value]||value;
    const inboxDate = value => value ? new Date(value).toLocaleString() : '—';
    const inboxSubject = message => String(message.subject || message.title || String(message.body || '').split('\n')[0] || '(No subject)').slice(0,110);
    const inboxJob = thread => thread.scopeType === 'Job' ? (thread.title || thread.scopeId || 'Job') : '—';
    const inboxRecipient = (message,thread) => (message.recipientAddresses?.length ? message.recipientAddresses.join(', ') : '') || message.recipientUserName || message.recipient || (thread.scopeType === 'Job' ? 'Job team' : thread.scopeType === 'Workspace' ? 'Workspace team' : thread.scopeType === 'Direct' ? thread.title : 'Conversation');
    async function showMessages() {
        const current=StudioPage.begin('messages');
        ++inboxRevision;selectedThread=null;selectedRecipients=[];sendRequestId=null;
        conversationView.classList.add('hidden');
        genericView.classList.remove('hidden');
        genericView.innerHTML = StudioPage.html({title:'Conversations',className:'st-messages-content',showMessagesCard:false,summaryTitle:'Conversation Context',summary:'<div id="studioMessageContext"><p>Select a message to view its job, recipients and delivery context.</p></div>',attention:'<p>External delivery outcomes appear in each conversation. Held deliveries need review before retrying.</p>',content:`
            <div class="st-inbox">
                <section class="studio-inbox-center">
                    <header class="studio-inbox-toolbar"><div><h2>Unified Inbox</h2><small id="studioInboxCount">Loading messages…</small></div><div class="studio-inbox-tools"><select data-job-picker id="studioInboxJobSelect" aria-label="Filter by job"><option value="">All jobs</option></select><input id="studioInboxSearch" type="search" placeholder="Search messages…" aria-label="Search messages"><button class="action" id="studioInboxRefresh">Refresh</button><button class="action" id="studioAddressBook">Address Book</button><button class="primary" id="newDirectMessage">New</button></div></header>
                    <div class="studio-inbox-table-wrap" id="studioInboxRows" role="list" aria-label="Messages"><div class="studio-inbox-loading">Loading messages…</div></div>
                    <nav class="studio-inbox-pagination" aria-label="Message pages"><button id="studioInboxPrevious" class="action">Previous</button><span id="studioInboxPage"></span><button id="studioInboxNext" class="action">Next</button></nav>
                </section>
                <section id="messageConversationPane" class="message-conversation-pane studio-inbox-reading"><div class="empty-state"><h2>Select a message</h2><p>Read the conversation and reply here.</p></div></section>
            </div>`});
        StudioPage.bind(genericView,current);
        document.getElementById('newDirectMessage').onclick = startDirect;
        document.getElementById('studioAddressBook').onclick = () => showAddressBook();
        document.getElementById('studioInboxPrevious').onclick=()=>{inboxOffset=Math.max(0,inboxOffset-100);void loadInbox(true);};
        document.getElementById('studioInboxNext').onclick=()=>{inboxOffset+=100;void loadInbox(true);};
        document.getElementById('studioInboxRefresh').onclick = () => loadInbox(true);
        document.getElementById('studioInboxSearch').oninput = event => { inboxQuery = event.target.value;inboxOffset=0;clearTimeout(inboxSearchTimer);inboxSearchTimer=setTimeout(()=>loadInbox(true),250); };
        document.getElementById('studioInboxJobSelect').onchange = event => { inboxFilter=event.target.value;inboxOffset=0;void loadInbox(true); };
        await loadInbox(true);
        if(current())startRefresh();
    }
    async function loadInbox(force) {
        const revision=++inboxRevision;
        const body=document.getElementById('studioInboxRows');
        if(!body)return;
        if(force)body.innerHTML='<div class="studio-inbox-loading">Loading messages…</div>';
        try {
            const params=new URLSearchParams({offset:String(inboxOffset),limit:'100',query:inboxQuery});
            if(inboxFilter)params.set('jobId',inboxFilter);
            const page=await json('/api/messages/inbox?'+params);
            if(revision!==inboxRevision||document.getElementById('studioInboxRows')!==body)return;
            inboxRows=page.items;inboxTotal=page.total;inboxLastLoad=Date.now();
            if(!addressEntries.length)addressEntries=await json('/api/messages/address-book');
            if(revision!==inboxRevision||document.getElementById('studioInboxRows')!==body)return;
            pickerJobs=await json('/api/jobs');
            if(revision!==inboxRevision||document.getElementById('studioInboxRows')!==body)return;
            renderInboxFilters();renderInbox();
        }catch(error){if(revision!==inboxRevision||document.getElementById('studioInboxRows')!==body)return;body.innerHTML='<div class="studio-inbox-loading">'+esc(error.message||'Unable to load messages.')+'</div>';}
    }
    function renderInboxFilters(){
        const select=document.getElementById('studioInboxJobSelect');if(!select)return;
        select.innerHTML='<option value="">All jobs</option>'+StudioSubjectPicker.jobOptions(pickerJobs,inboxFilter);
        select.value=inboxFilter;
    }
    function renderInbox(){
        const body=document.getElementById('studioInboxRows');if(!body)return;
        const rows=inboxRows;
        StudioPage.setCount(rows.length+' messages');
        document.getElementById('studioInboxCount').textContent=inboxTotal+' messages'+(inboxFilter?' in selected job':'');
        document.getElementById('studioInboxPage').textContent=rows.length?(inboxOffset+1)+'–'+(inboxOffset+rows.length)+' of '+inboxTotal:'No messages';
        document.getElementById('studioInboxPrevious').disabled=inboxOffset===0;
        document.getElementById('studioInboxNext').disabled=inboxOffset+rows.length>=inboxTotal;
        const groups=[{title:'Messages',items:rows}];
        body.innerHTML=groups.map(group=>'<section class="studio-inbox-group"><div class="studio-inbox-group-heading"><span>⌄</span> '+esc(group.title)+': '+group.items.length+' item(s)</div>'+group.items.map(({thread,message})=>'<div role="listitem" tabindex="0" data-inbox-thread="'+esc(thread.id)+'" data-inbox-message="'+esc(message.id)+'" class="studio-inbox-message-row '+(inboxSelection===message.id?'selected':'')+'"><div class="studio-inbox-message-top"><strong class="studio-inbox-message-sender">'+esc(message.senderUserName||'Unknown')+'</strong><strong class="studio-inbox-message-subject">'+esc(inboxSubject(message))+'</strong><time>'+esc(inboxDate(message.createdAtUtc))+'</time></div><div class="studio-inbox-message-bottom"><span class="studio-inbox-message-job">'+esc(inboxJob(thread))+'</span><span class="studio-inbox-message-preview">'+esc(String(message.body||'').replace(/\s+/g,' ').slice(0,220))+'</span><span class="studio-inbox-message-recipient">To: '+esc(inboxRecipient(message,thread))+'</span></div></div>').join('')+'</section>').join('') || '<div class="studio-inbox-loading">No messages.</div>';
        if(!rows.length)body.innerHTML='<div class="studio-inbox-loading">No messages match the selected filters.</div>';
        body.querySelectorAll('[data-inbox-message]').forEach(row=>{
            const open=async()=>{inboxSelection=row.dataset.inboxMessage;renderInbox();const selected=inboxRows.find(item=>item.message.id===inboxSelection);
                if(selected?.thread.kind==='Conversation')await openLegacyConversation(selected.thread.id);
                else await openThread(row.dataset.inboxThread);const msg=document.querySelector('#studioMessageHistory .studio-chat-message[data-message-id="'+CSS.escape(inboxSelection)+'"]');msg?.scrollIntoView({block:'nearest'});};
            row.onclick=open;row.onkeydown=e=>{if(e.key==='Enter'||e.key===' '){e.preventDefault();void open();}};
        });
    }

    window.showStudioMessages = showMessages;
    window.StudioMessages={invalidate(){++inboxRevision;++threadRevision;}};

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
        addressEntries=await json('/api/messages/address-book');
        let dialog=document.getElementById('directMessageDialog');
        if(!dialog){dialog=document.createElement('dialog');dialog.id='directMessageDialog';dialog.className='direct-message-dialog';document.body.appendChild(dialog);}
        dialog.innerHTML=`<form class="direct-message-form"><div class="message-dialog-heading"><h2>New message</h2><button class="message-dialog-close" type="button" aria-label="Close">×</button></div>
            <label>Recipients<select id="unifiedRecipients" multiple required>${addressEntries.filter(e=>e.status==='Active').map(e=>`<option value="${esc(e.id)}">${esc(e.displayName)} · ${esc(e.contactType)} · ${esc(modeLabel(e.deliveryMode))}</option>`).join('')}</select></label>
            <label>Subject<input id="unifiedSubject" maxlength="200" required></label><label>Message<textarea id="unifiedBody" maxlength="10000" required></textarea></label>
            <label>Attachments<input id="unifiedFiles" type="file" multiple></label><p id="unifiedSendFeedback" role="status"></p><div class="dialog-actions"><button type="button" class="action" id="unifiedCancel">Cancel</button><button class="primary" type="submit">Send</button></div></form>`;
        const close=()=>dialog.close();dialog.querySelector('.message-dialog-close').onclick=close;dialog.querySelector('#unifiedCancel').onclick=close;
        const requestId=crypto.randomUUID();
        dialog.querySelector('form').onsubmit=async event=>{
            event.preventDefault();const button=dialog.querySelector('[type=submit]');button.disabled=true;
            try{
                const destinationIds=[...dialog.querySelector('#unifiedRecipients').selectedOptions].map(o=>o.value);
                const request={requestId,threadId:null,destinationIds,subject:dialog.querySelector('#unifiedSubject').value,body:dialog.querySelector('#unifiedBody').value};
                const result=await sendUnified(request,dialog.querySelector('#unifiedFiles').files);
                dialog.close();await loadInbox(true);await openThread(result.threadId);
            }catch(error){dialog.querySelector('#unifiedSendFeedback').textContent=error.message;}finally{button.disabled=false;}
        };
        dialog.showModal();
    }

    async function sendUnified(request,files=[]) {
        const data=new FormData();data.append('message',JSON.stringify(request));
        for(const file of files)data.append('files',file);
        const response=await fetch('/api/messages/send',{method:'POST',body:data});
        const result=await response.json().catch(()=>({error:'Message could not be sent.'}));
        if(!response.ok)throw new Error(result.error||'Message could not be sent.');
        return result;
    }

    async function showAddressBook() {
        const entries=await json('/api/messages/address-book');
        const admin=await fetch('/api/admin/messaging/address-book');const canConfigure=admin.ok;
        let dialog=document.getElementById('studioAddressDialog');
        if(!dialog){dialog=document.createElement('dialog');dialog.id='studioAddressDialog';dialog.className='direct-message-dialog';document.body.appendChild(dialog);}
        dialog.innerHTML=`<div class="direct-message-form"><div class="message-dialog-heading"><h2>Address Book</h2><button type="button" class="message-dialog-close" aria-label="Close">×</button></div><div class="studio-address-list">${entries.map(e=>`<article><strong>${esc(e.displayName)}</strong><small>${esc(e.emailAddress||e.studioInboxId||'No email configured')} · ${esc(e.contactType)} · ${esc(modeLabel(e.deliveryMode))} · ${esc(e.status)}</small>${canConfigure?`<button class="st-link" data-address-config="${esc(e.id)}">Routing settings</button>`:''}</article>`).join('')}</div><form id="studioContactForm"><label>Contact name<input name="displayName" required maxlength="200"></label><label>Email<input name="emailAddress" type="email" required></label><button class="primary" type="submit">Add external contact</button><p role="status" id="studioContactFeedback"></p></form></div>`;
        dialog.querySelector('.message-dialog-close').onclick=()=>dialog.close();
        dialog.querySelectorAll('[data-address-config]').forEach(button=>button.onclick=()=>editAddressRoute(entries.find(e=>e.id===button.dataset.addressConfig),dialog));
        dialog.querySelector('form').onsubmit=async event=>{
            event.preventDefault();const form=event.target;
            try{await json('/api/messages/contacts',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({displayName:form.elements.displayName.value,emailAddress:form.elements.emailAddress.value})});dialog.close();addressEntries=[];await showAddressBook();}
            catch(error){document.getElementById('studioContactFeedback').textContent=error.message;}
        };dialog.showModal();
    }

    async function editAddressRoute(entry,dialog) {
        // The server checks Administrator permission; no provider credentials or membership editing is exposed here.
        const adminResponse=await fetch('/api/admin/messaging/address-book');
        if(!adminResponse.ok){alert('Routing settings require a Studio administrator.');return;}
        entry=(await adminResponse.json()).find(e=>e.id===entry.id);
        const fields=[['inboxType','Inbox type',['Internal','Microsoft365','External']],['deliveryMode','Delivery mode',['InternalOnly','ExternalOnly','Combined']],['combinedMailboxPolicy','Combined mailbox policy',['ExternalCopy','InternalOnly','Hold']],['status','Status',['Active','Inactive','Attention']]];
        dialog.innerHTML=`<form class="direct-message-form"><div class="message-dialog-heading"><h2>${esc(entry.displayName)}</h2><button type="button" class="message-dialog-close">×</button></div>
            <label>Professional or destination email<input name="emailAddress" value="${esc(entry.emailAddress)}"></label>
            ${fields.map(([name,label,options])=>`<label>${esc(label)}<select name="${name}">${options.map(value=>`<option value="${esc(value)}" ${entry[name]===value?'selected':''}>${esc(modeLabel(value))}</option>`).join('')}</select></label>`).join('')}
            <label>Provider group object ID<input name="providerReference" value="${esc(entry.providerReference||'')}"></label><label>Authorized sending mailbox<input name="senderMailbox" value="${esc(entry.senderMailbox||'')}"></label>
            ${['studioManagedGroup','groupVerified','senderVerified'].map(name=>`<label><input type="checkbox" name="${name}" ${entry[name]?'checked':''}>${esc(({studioManagedGroup:'Use Studio-managed group routing',groupVerified:'Group fan-out verified in Microsoft 365',senderVerified:'Professional sender verified in Microsoft 365'})[name])}</label>`).join('')}
            <label>Tenant validation evidence<textarea name="verificationEvidence">${esc(entry.verificationEvidence||'')}</textarea></label><p role="status" id="routeFeedback"></p><button class="primary" type="submit">Save routing</button></form>`;
        dialog.querySelector('.message-dialog-close').onclick=()=>dialog.close();
        dialog.querySelector('form').onsubmit=async event=>{
            event.preventDefault();const form=event.target;const updated={...entry};
            for(const name of ['emailAddress','inboxType','deliveryMode','combinedMailboxPolicy','status','providerReference','senderMailbox','verificationEvidence'])updated[name]=form.elements[name].value||null;
            for(const name of ['studioManagedGroup','groupVerified','senderVerified'])updated[name]=form.elements[name].checked;
            updated.studioInboxId=updated.deliveryMode==='ExternalOnly'?null:(entry.userName||entry.scopeId||null);
            try{await json('/api/admin/messaging/address-book/'+entry.id,{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify(updated)});addressEntries=[];dialog.close();await showAddressBook();}
            catch(error){document.getElementById('routeFeedback').textContent=error.message;}
        };
    }

    function setConversationContext(title, scope, sender, recipients, outcomes = []) {
        const panel=document.getElementById('studioMessageContext');
        if(!panel)return;
        panel.innerHTML='<dl class="studio-context-details"><dt>Conversation</dt><dd>'+esc(title)+'</dd><dt>Linked to</dt><dd>'+esc(scope||'Direct conversation')+'</dd><dt>Send as</dt><dd>'+esc(sender||'Studio')+'</dd><dt>Recipients</dt><dd>'+esc(recipients||'Internal team')+'</dd>'+(outcomes.length?'<dt>Delivery</dt><dd>'+esc([...new Set(outcomes.map(d=>deliveryLabel(d.status)))].join(', '))+'</dd>':'')+'</dl>';
    }

    async function openLegacyConversation(id) {
        const revision=++threadRevision;selectedThread=null;
        const target=document.getElementById('messageConversationPane');
        const [conversation,identities,files]=await Promise.all([json('/api/conversations/'+id),json('/api/conversations/'+id+'/sending-identities').catch(()=>null),json('/api/conversations/'+id+'/attachments').catch(()=>[])]);
        const pane=document.getElementById('messageConversationPane');if(!pane||pane!==target||revision!==threadRevision)return;
        setConversationContext(conversation.subject,'Customer conversation',identities?.identities?.find(i=>i.isDefault)?.label||'Workspace',conversation.contactName);
        pane.innerHTML=`<div class="message-conversation-header"><div><h2>${esc(conversation.subject)}</h2><p>${esc(conversation.contactName)} · ${esc(conversation.contactAddress)}</p></div><button class="st-link" id="openConversationWorkspace">Open conversation</button></div><div class="studio-message-history">${conversation.messages.map(m=>renderMessage({id:m.id,senderUserName:m.author,body:m.body,createdAtUtc:m.createdAt},[])).join('')}${files.length?`<div class="studio-message-files">${files.map(f=>`<a href="/api/attachments/${esc(f.id)}" target="_blank" rel="noopener">${esc(f.fileName)}</a>`).join('')}</div>`:''}</div>
            <form id="legacyMessageComposer" class="studio-message-composer"><div><textarea name="body" maxlength="10000" placeholder="Write a reply…" required></textarea><p id="legacySendFeedback" role="status"></p></div><div class="studio-compose-actions"><select name="sendingIdentity" aria-label="Sender identity">${(identities?.identities||[{type:'Workspace',label:'Workspace'}]).map(i=>`<option value="${esc(i.type)}">${esc(i.label)}</option>`).join('')}</select><label><input name="internal" type="checkbox">Internal note</label><button class="primary" type="submit">Send</button></div></form>`;
        document.getElementById('openConversationWorkspace').onclick=async()=>{await selectWorkspace(conversation.workspaceId);await openConversation(id);};
        StudioMessageCard.bindEditors(pane);
        document.getElementById('legacyMessageComposer').onsubmit=async event=>{
            event.preventDefault();const form=event.target,button=form.querySelector('[type=submit]');button.disabled=true;
            try{await json('/api/conversations/'+id+'/messages',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({body:form.elements.body.value,isInternal:form.elements.internal.checked,sendingIdentity:form.elements.sendingIdentity.value})});await openLegacyConversation(id);await loadInbox(false);}
            catch(error){document.getElementById('legacySendFeedback').textContent=error.message;}finally{button.disabled=false;}
        };
    }

    async function openWorkspaceThread() {
        if (!selectedWorkspace) return;
        const thread = await json(`/api/messages/threads/Workspace/${selectedWorkspace}`, { method: 'POST' });
        await loadThreads();
        await openThread(thread.id);
    }

    async function openThread(id) {
        const revision=++threadRevision;
        const targetPane=document.getElementById('messageConversationPane');
        const previousThread = selectedThread;
        selectedThread = id;
        await joinRealtimeThread(id, previousThread);
        const threads = await json('/api/messages/threads');
        const thread = threads.find(item => item.id === id);
        const [messages,threadAttachments,recipients,deliveries,identity] = await Promise.all([json(`/api/messages/threads/${id}`),json(`/api/messages/threads/${id}/attachments`).catch(()=>[]),json(`/api/messages/threads/${id}/recipients`),json(`/api/messages/threads/${id}/deliveries`),json('/api/messages/identity')]);
        const pane = document.getElementById('messageConversationPane');
        if (!pane || pane!==targetPane || revision!==threadRevision) return;
        selectedRecipients=recipients;sendRequestId=null;
        selectedSubject=[...messages].reverse().find(m=>m.subject)?.subject||thread?.title||'Studio message';
        setConversationContext(thread?.title||selectedSubject,thread?.scopeType,identity?.emailAddress||identity?.displayName,recipients.map(r=>r.displayName).join(', '),deliveries);
        pane.innerHTML = `
            <div class="message-conversation-header"><div><h2>${esc(thread?.title || 'Messages')}</h2><p>${esc(thread?.scopeType || '')} · From: ${esc(identity?.emailAddress||identity?.displayName||'Studio')} · To: ${esc(recipients.map(r=>r.displayName).join(', ')||'Internal team')}</p></div></div>
            <div id="studioMessageHistory" class="studio-message-history">
                ${messages.map(message=>renderMessage(message,threadAttachments,deliveries)).join('') || '<div class="empty-state compact"><p>No messages yet. Start the conversation.</p></div>'}
            </div>
            <form id="studioMessageComposer" class="studio-message-composer">
                <div class="message-composer-input-wrap">
                    <textarea id="studioMessageBody" maxlength="10000" placeholder="Write a message… Use @ to mention a Studio user." required></textarea>
                    <div id="mentionSuggestions" class="mention-suggestions" hidden></div><span id="studioSendFeedback" role="status"></span>
                </div>
                <div class="studio-compose-actions"><label class="action message-attachment-control">Attach document<input id="studioMessageFile" type="file" multiple hidden></label>
                <button class="action" id="studioWhatsAppSend" type="button" title="Send this message to the photographer via WhatsApp">WhatsApp</button><button class="primary" type="submit">Send</button></div>
            </form>`;
        StudioMessageCard.bindEditors(pane);
        document.getElementById('studioMessageComposer').onsubmit = sendMessage;
        document.getElementById('studioWhatsAppSend').onclick = sendWhatsApp;
        pane.querySelectorAll('[data-retry-routing]').forEach(button=>button.onclick=()=>retryRouting(button.dataset.retryRouting));
        pane.querySelectorAll('[data-retry-delivery]').forEach(button=>button.onclick=()=>retryDelivery(button.dataset.retryDelivery,button.dataset.uncertain==='true'));
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

    function renderMessage(message, attachments = [], deliveries = []) {
        const files=(attachments||[]).filter(file=>file.messageId===message.id);
        return `<article class="studio-chat-message" data-message-id="${esc(message.id)}">
            <div class="studio-chat-meta"><strong>${esc(message.senderUserName)}</strong><span>${new Date(message.createdAtUtc).toLocaleString()}</span></div>
            ${message.subject?`<strong>${esc(message.subject)}</strong>`:''}
            <div class="studio-chat-body">${renderMentions(message.body)}</div>
            ${files.length?`<div class="studio-message-files">${files.map(file=>`<a href="/api/message-attachments/${file.id}" target="_blank" rel="noopener"><strong>${esc(file.fileName)}</strong><small>${formatBytes(file.size)}</small></a>`).join('')}</div>`:''}
            ${deliveries.filter(d=>d.messageId===message.id).length?`<details class="studio-delivery-details"><summary>Delivery · ${esc([...new Set(deliveries.filter(d=>d.messageId===message.id).map(d=>deliveryLabel(d.status)))].join(', '))}</summary>${deliveries.filter(d=>d.messageId===message.id).map(d=>`<div><span>${esc(d.transport)} · ${esc(d.recipient)} · ${esc(deliveryLabel(d.status))} ${esc(d.detail||'')}</span>${d.transport==='Routing'&&d.status==='Attention'&&d.recipient!=='Attachments'?`<button class="st-link" data-retry-routing="${esc(message.id)}">Resolve again</button>`:''}${d.deliveryId&&['Failed','Attention'].includes(d.status)?`<button class="st-link" data-retry-delivery="${esc(d.deliveryId)}" data-uncertain="${String(d.status==='Attention'&&String(d.detail).startsWith('ProviderOutcomeUncertain'))}">Retry</button>`:''}</div>`).join('')}</details>`:''}
        </article>`;
    }

    async function retryRouting(messageId){try{await json('/api/messages/'+messageId+'/retry-routing',{method:'POST'});await openThread(selectedThread);}catch(error){alert(error.message);}}
    async function retryDelivery(deliveryId,uncertain){
        const reconciledNotSent=uncertain&&confirm('Check Microsoft 365 sent items or message trace first. Confirm this delivery was NOT sent?');
        if(uncertain&&!reconciledNotSent)return;
        try{await json('/api/messages/deliveries/'+deliveryId+'/retry',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({reconciledNotSent})});await openThread(selectedThread);}catch(error){alert(error.message);}
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
        event.preventDefault();const threadId=selectedThread;const body=document.getElementById('studioMessageBody').value.trim();
        if(!body||!threadId)return;const button=event.target.querySelector('[type=submit]');button.disabled=true;
        sendRequestId ||= crypto.randomUUID();
        try{
            const result=await sendUnified({requestId:sendRequestId,threadId,subject:selectedSubject,body,destinationIds:selectedRecipients.map(r=>r.id)},document.getElementById('studioMessageFile').files);
            sendRequestId=null;document.getElementById('studioMessageBody').value='';await openThread(result.threadId);
        }catch(error){document.getElementById('studioSendFeedback').textContent=error.message;}finally{button.disabled=false;}
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
        host.onclick = async () => { host.classList.remove('show'); await window.studioNavigate('messages'); await openThread(threadId); };
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
                <span class="away-digest-copy"><strong>${esc(thread.title || 'Message')}</strong><small>${esc([sender, thread.scopeType].filter(Boolean).join(' · '))}</small>${preview ? `<span>${esc(String(preview).replace(/\s+/g,' ').trim().slice(0,140))}</span>` : ''}</span>
                <span class="away-digest-meta"><b>${thread.unreadCount}</b><small>unread</small></span>
            </button>`).join('')}</div>
            ${remaining ? `<p class="away-digest-more">+${remaining} more unread conversation${remaining === 1 ? '' : 's'}</p>` : ''}
            <div class="away-digest-actions"><button type="button" class="action" id="dismissAwayDigest">Not now</button><button type="button" class="primary" id="openAllStudioMessages">Open Messages</button></div>
        </div>`;
        const close = () => { if (dialog.open) dialog.close(); sessionStorage.setItem('studio-away-digest-shown', '1'); };
        dialog.querySelector('.message-dialog-close').addEventListener('click', close);
        dialog.querySelector('#dismissAwayDigest').addEventListener('click', close);
        dialog.querySelector('#openAllStudioMessages').addEventListener('click', async () => { close(); await window.studioNavigate('messages'); });
        dialog.querySelectorAll('[data-away-thread]').forEach(button => button.addEventListener('click', async () => { const id = button.dataset.awayThread; close(); await window.studioNavigate('messages'); await openThread(id); }));
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
        const currentThread=selectedThread,revision=threadRevision,targetHistory=document.getElementById('studioMessageHistory');
        const [messages,threadAttachments,deliveries] = await Promise.all([json(`/api/messages/threads/${selectedThread}`),json(`/api/messages/threads/${selectedThread}/attachments`).catch(()=>[]),json(`/api/messages/threads/${selectedThread}/deliveries`)]);
        const history = document.getElementById('studioMessageHistory');
        if (history && history===targetHistory && currentThread===selectedThread && revision===threadRevision) {
            history.innerHTML = messages.map(message=>renderMessage(message,threadAttachments,deliveries)).join('');
            history.querySelectorAll('[data-retry-routing]').forEach(button=>button.onclick=()=>retryRouting(button.dataset.retryRouting));
            history.querySelectorAll('[data-retry-delivery]').forEach(button=>button.onclick=()=>retryDelivery(button.dataset.retryDelivery,button.dataset.uncertain==='true'));
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
        await window.studioNavigate('messages');
        const thread = await json(`/api/messages/threads/Workspace/${workspaceId}`, { method: 'POST' });
        await openThread(thread.id);
    };

    window.openStudioJobMessages = async function(jobId) {
        await window.studioNavigate('messages');
        const thread = await json(`/api/messages/threads/Job/${jobId}`, { method: 'POST' });
        await openThread(thread.id);
    };

    installNavigation();
    startRefresh();
    connectRealtime();
    // Give authentication and the initial shell a moment to settle, then show the digest once.
    setTimeout(() => showAwayDigestOnce(), 450);
})();

