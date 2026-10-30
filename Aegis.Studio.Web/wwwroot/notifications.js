let studioNotificationRevision=0;
async function showNotifications() {
    const revision=++studioNotificationRevision;
    const live=()=>revision===studioNotificationRevision&&document.body.dataset.studioView==='notifications';
    conversationView.classList.add('hidden');
    genericView.classList.remove('hidden');
    genericView.innerHTML='<p role="status">Loading your notifications…</p>';
    let items=[],filter='all',category='',term='',selected=null,busy=false,error='';
    const group=item=>/message|mention|reply|conversation|announcement/i.test(item.eventType)?'Messages':/payment|refund|invoice/i.test(item.eventType)?'Payments':/schedule|shoot|booking/i.test(item.eventType)?'Bookings & schedule':/assign|job|task|workflow/i.test(item.eventType)?'Jobs & tasks':'Other';
    const date=item=>{const value=new Date(item.createdAtUtc);return Number.isFinite(+value)?value.toLocaleString([],{dateStyle:'medium',timeStyle:'short'}):'Date unavailable';};
    const related=item=>item?.referenceId&&['Job','Workspace'].includes(item.referenceType)?item.referenceType:/message|mention|reply|conversation/i.test(item?.eventType||'')?'Messages':null;
    function paint(selectionOnly=false){
        if(!live())return;
        const unread=items.filter(n=>!n.isRead).length;
        const rows=items.filter(n=>(filter==='all'||(filter==='unread'?!n.isRead:n.isRead))&&(!category||group(n)===category)&&[n.subject,n.body,n.eventType].join(' ').toLowerCase().includes(term.toLowerCase()));
        const item=items.find(n=>n.id===selected);
        const markup=`<section class="st-page notification-page" aria-label="Notifications"><section class="st-board"><header class="st-toolbar"><div><h1>Your notifications</h1><p>Updates about your work, conversations and bookings.</p></div><div class="st-tools"><button type="button" class="action" data-n-refresh ${busy?'disabled':''}>↻ Refresh</button><button type="button" class="action" data-n-all ${busy||!unread?'disabled':''}>✓ Mark all loaded as read</button></div></header><div class="notification-controls"><div class="notification-tabs" role="group" aria-label="Read status">${[['all','All',items.length],['unread','Unread',unread],['read','Read',items.length-unread]].map(([key,label,count])=>`<button type="button" data-n-filter="${key}" aria-pressed="${filter===key}">${label} <span>${count}</span></button>`).join('')}</div><div class="notification-search"><input type="search" data-n-search aria-label="Search notifications" placeholder="Search notifications…" value="${esc(term)}"><select data-n-category aria-label="Notification category"><option value="">All categories</option>${['Messages','Jobs & tasks','Bookings & schedule','Payments','Other'].map(c=>`<option ${c===category?'selected':''}>${esc(c)}</option>`).join('')}</select></div></div><p class="notification-feedback" role="status">${esc(error)}</p><div class="st-content notification-list" aria-label="Notification list">${rows.length?rows.map(n=>`<button type="button" class="notification-row ${n.isRead?'':'is-unread'} ${n.id===selected?'is-selected':''}" data-n-id="${esc(n.id)}" aria-pressed="${n.id===selected}"><span class="notification-symbol" aria-hidden="true">${({'Messages':'✉','Payments':'$','Bookings & schedule':'▦','Jobs & tasks':'▣','Other':'◉'})[group(n)]}</span><span class="notification-copy"><span class="notification-meta">${esc(group(n))} · ${esc(date(n))}${n.isRead?'':' · Unread'}</span><strong>${esc(n.subject||'Studio update')}</strong><span class="notification-preview">${esc(n.body||'No additional details.')}</span></span><span aria-hidden="true">›</span></button>`).join(''):`<div class="notification-empty"><span aria-hidden="true">✓</span><h2>${items.length?'No matching notifications':'You’re all caught up'}</h2><p>${items.length?'Try another filter or search.':'Updates about your work will appear here.'}</p></div>`}</div><section class="mw-list-panel notification-detail"><header><h2>Notification details</h2></header><div class="notification-detail-body">${item?`<span class="notification-meta">${esc(group(item))} · ${esc(date(item))}</span><h3>${esc(item.subject||'Studio update')}</h3><p>${esc(item.body||'No additional details.')}</p><div class="notification-detail-actions">${related(item)?`<button type="button" class="primary" data-n-open>Open ${esc(related(item).toLowerCase())} →</button>`:''}<button type="button" class="action" data-n-read ${busy||item.isRead?'disabled':''}>${item.isRead?'✓ Read':'Mark as read'}</button></div>`:'<p>Select a notification to read its full details and open the related work.</p>'}</div></section></section></section>`;
        if(selectionOnly){
            const template=document.createElement('template');template.innerHTML=markup;
            StudioPage.query(genericView,'.notification-detail-body').replaceWith(template.content.querySelector('.notification-detail-body'));
            genericView.querySelectorAll('[data-n-id]').forEach(row=>{const active=row.dataset.nId===selected;row.classList.toggle('is-selected',active);row.setAttribute('aria-pressed',String(active));});
        }else genericView.innerHTML=markup;
        window.studioShellSetOverview?.(`<section class="mw-list-panel studio-context-card" data-rail-card="overview"><header><h2>Overview</h2></header><div class="workspace-summary-row"><span>●</span><span>Unread</span><strong>${unread}</strong></div><div class="workspace-summary-row"><span>▤</span><span>Loaded notifications</span><strong>${items.length}</strong></div></section>`,'notifications');
        StudioPage.mountContext(genericView,'notifications');
        StudioPage.setCount(rows.length+' notifications');
        StudioPage.query(genericView,'[data-n-refresh]').onclick=load;
        StudioPage.queryAll(genericView,'[data-n-filter]').forEach(b=>b.onclick=()=>{filter=b.dataset.nFilter;paint();});
        StudioPage.query(genericView,'[data-n-category]').onchange=e=>{category=e.target.value;paint();};
        StudioPage.query(genericView,'[data-n-search]').oninput=e=>{term=e.target.value;paint();const input=StudioPage.query(genericView,'[data-n-search]');input.focus();};
        StudioPage.queryAll(genericView,'[data-n-id]').forEach(b=>b.onclick=()=>{selected=b.dataset.nId;paint(true);});
        StudioPage.query(genericView,'[data-n-read]')?.addEventListener('click',()=>markRead([item]));
        StudioPage.query(genericView,'[data-n-all]').onclick=()=>markRead(items.filter(n=>!n.isRead));
        StudioPage.query(genericView,'[data-n-open]')?.addEventListener('click',async()=>{try{await markRead(item.isRead?[]:[item]);if(!live())return;if(related(item)==='Job')await openWorkspaceJob(item.referenceId);else if(related(item)==='Workspace')await selectWorkspace(item.referenceId);else await window.studioNavigate('messages');}catch(e){error=e.message;paint();}});
    }
    async function markRead(list){
        if(busy||!list.length)return;
        busy=true;error='';paint();
        const results=await Promise.allSettled(list.map(async n=>{await json('/api/notifications/'+encodeURIComponent(n.id)+'/read',{method:'POST'});n.isRead=true;}));
        busy=false;if(results.some(r=>r.status==='rejected'))error='Some notifications could not be marked as read. Please try again.';paint();
    }
    async function load(){
        if(busy)return;busy=true;error='';
        try{const result=await json('/api/notifications?count=200');if(!Array.isArray(result))throw Error('Unexpected response');items=result.filter(n=>n.channel==='InApp').sort((a,b)=>new Date(b.createdAtUtc)-new Date(a.createdAtUtc));}
        catch(e){error='Your notifications could not be loaded. Use Refresh to try again.';}
        busy=false;paint();
    }
    await load();
}
// Notifications remain a dedicated navigation page; the masthead bell is a message preview.
const notificationButton=document.getElementById('notificationButton');
if(notificationButton){
    notificationButton.className='studio-attention-trigger';
    notificationButton.querySelector('svg')?.classList.add('studio-status-icon');
    notificationButton.title='Messages';
    notificationButton.setAttribute('aria-label','Show unread messages');
    notificationButton.setAttribute('aria-haspopup','dialog');
    notificationButton.setAttribute('aria-expanded','false');
    const unreadBadge=document.createElement('strong');
    unreadBadge.id='messageUnreadBadge';unreadBadge.className='studio-message-bell-count';
    unreadBadge.textContent='…';unreadBadge.setAttribute('aria-hidden','true');
    notificationButton.append(unreadBadge);
    const chevron=document.createElement('span');chevron.textContent='⌄';chevron.setAttribute('aria-hidden','true');notificationButton.append(chevron);
    const panel=document.createElement('section');
    panel.id='studioMessageBellPanel';
    panel.className='studio-message-bell-panel';
    panel.setAttribute('role','dialog');
    panel.setAttribute('aria-label','Unread messages');
    panel.hidden=true;
    panel.innerHTML='<header><h2>Unread Messages</h2><button type="button" data-bell-close aria-label="Close messages">×</button></header><div class="studio-message-bell-content"><div class="studio-message-bell-list" role="status">Open to load unread messages.</div></div><footer><button type="button" data-bell-all>Open Messages →</button></footer>';
    notificationButton.insertAdjacentElement('afterend',panel);
    let requestRevision=0;
    const close=()=>{panel.hidden=true;notificationButton.setAttribute('aria-expanded','false');requestRevision++;};
    panel.querySelector('[data-bell-close]').onclick=()=>{close();notificationButton.focus();};
    panel.querySelector('[data-bell-all]').onclick=()=>{close();window.studioNavigate?.('messages');};
    notificationButton.onclick=async()=>{
        if(!panel.hidden){close();return;}
        panel.hidden=false;
        notificationButton.setAttribute('aria-expanded','true');
        const revision=++requestRevision;
        const list=panel.querySelector('.studio-message-bell-list');
        list.textContent='Loading messages…';
        try{
            const messageResponse=await fetch('/api/messages/inbox?offset=0&limit=8&unreadOnly=true',{credentials:'same-origin'});
            if(!messageResponse.ok)throw new Error('Message inbox unavailable');
            const payload=await messageResponse.json();
            const items=(Array.isArray(payload.items)?payload.items:[]).filter(item=>item.isUnread===true).map(item=>({title:item.message?.subject||item.thread?.title||item.message?.senderUserName||'Message',body:item.message?.body||item.message?.text||item.message?.content||''}));
            if(revision!==requestRevision||panel.hidden)return;
            list.replaceChildren();
            if(!items.length){list.textContent='No unread messages.';return;}
            for(const item of items){
                const button=document.createElement('button');
                button.type='button';
                const title=document.createElement('strong');
                title.textContent='✉ '+String(item.title);
                const preview=document.createElement('span');
                preview.textContent=String(item.body).slice(0,170);
                button.append(title,preview);
                button.onclick=()=>{
                    close();
                    window.studioNavigate?.('messages');
                };
                list.append(button);
            }
        }catch(error){if(revision===requestRevision&&!panel.hidden)list.textContent='Messages could not be loaded. Open Messages to try again.';}
    };
    document.addEventListener('pointerdown',event=>{
        if(!panel.hidden&&!panel.contains(event.target)&&!notificationButton.contains(event.target))close();
    });
    document.addEventListener('keydown',event=>{if(event.key==='Escape'&&!panel.hidden){close();notificationButton.focus();}});
}



