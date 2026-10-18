(function(root){
    'use strict';
    const esc=value=>String(value??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
    const api=(url,method,body)=>root.json(url,method?{method,headers:{'Content-Type':'application/json'},body:JSON.stringify(body)}:undefined);
    function dialog(title,content){
        const node=document.createElement('dialog');node.className='direct-message-dialog';
        node.innerHTML=`<form class="direct-message-form"><div class="message-dialog-heading"><h2>${esc(title)}</h2><button class="message-dialog-close" type="button" aria-label="Close">×</button></div>${content}<p role="alert" data-error></p></form>`;
        document.body.append(node);node.querySelector('[aria-label=Close]').onclick=()=>node.close();node.onclose=()=>node.remove();node.showModal();return node;
    }
    const error=(node,cause)=>node.querySelector('[data-error]').textContent=cause.message||'Unable to save. Please try again.';
    function submit(node,action){node.querySelector('form').onsubmit=async event=>{event.preventDefault();const button=event.submitter||node.querySelector('[type=submit]');button.disabled=true;try{await action(event.target);node.close();}catch(cause){error(node,cause);}finally{button.disabled=false;}};}
    const pending=new WeakMap();
    async function mount(host,id,conversation,reload){
        const revision={},initialHeader=host.querySelector('.detail-header,.message-conversation-header');pending.set(host,revision);
        let value;try{value=await api(`/api/conversations/${id}/association`);}catch{return;}
        if(!host.isConnected||pending.get(host)!==revision||(initialHeader&&!host.contains(initialHeader)))return;
        host.querySelector('[data-inquiry-association]')?.remove();
        const panel=document.createElement('section');panel.dataset.inquiryAssociation='';panel.className='inquiry-association';
        panel.innerHTML=`<div><strong>Client: ${esc(value.clientName||'Unassigned')}</strong><small>${esc(value.reference)}</small></div>${value.canEdit?`<div class="inquiry-association-actions"><button class="action" data-client>${value.clientId?'Change client':'Assign client'}</button>${value.clientId?'<button class="action" data-remove>Remove client association</button>':''}${!conversation.jobId&&value.canCreateJob?'<button class="action" data-create>Create job</button>':''}<button class="action" data-job>Job association</button><button class="action" data-contact>Add alternate contact</button><button class="action" data-move>Move received email</button></div>`:''}<details><summary>Association history</summary>${value.history.map(h=>`<p>${esc(h.action)} · ${esc(h.actor)} · ${esc(new Date(h.at).toLocaleString())}<br><small>${esc(h.previous)} → ${esc(h.next)}</small></p>`).join('')||'<p>No association changes.</p>'}</details>`;
        const header=host.querySelector('.detail-header,.message-conversation-header');if(header)header.after(panel);else host.prepend(panel);
        const refresh=async()=>{if(reload)await reload();else await mount(host,id,conversation);};
        if(!value.canEdit)return;
        panel.querySelector('[data-client]').onclick=()=>chooseClient(id,value,refresh);
        panel.querySelector('[data-remove]')?.addEventListener('click',async()=>{try{await api(`/api/conversations/${id}/association/client`,'PUT',{clientId:null});await refresh();}catch(cause){root.alert(cause.message);}});
        panel.querySelector('[data-create]')?.addEventListener('click',()=>createJob(id,conversation,refresh));
        panel.querySelector('[data-job]').onclick=()=>chooseJob(id,conversation,refresh);
        panel.querySelector('[data-contact]').onclick=()=>{const node=dialog('Add alternate contact','<p>This deliberately recognizes the address for future routing. It does not grant access to Studio.</p><label>Email<input name="address" type="email" required></label><button class="primary" type="submit">Add contact</button>');submit(node,async form=>{await api(`/api/conversations/${id}/association/contacts`,'POST',{address:form.elements.address.value});await refresh();});};
        panel.querySelector('[data-move]').onclick=()=>moveMessage(id,conversation,refresh);
    }
    async function chooseClient(id,value,refresh){
        const node=dialog(value.clientId?'Change client':'Assign client',`<label>Search clients<input name="query" type="search" autocomplete="off"></label><label>Client<select name="client" required></select></label><p data-match></p><button class="primary" type="submit">Save client association</button>`);
        const form=node.querySelector('form');let revision=0;
        const search=async()=>{const current=++revision;try{const clients=await api(`/api/conversations/${id}/association/clients?query=${encodeURIComponent(form.elements.query.value)}`);if(!node.isConnected||current!==revision)return;form.elements.client.innerHTML='<option value="">Choose a client…</option>'+clients.map(c=>`<option value="${esc(c.id)}" ${c.id===value.clientId?'selected':''}>${esc(c.name)}${c.email?' · '+esc(c.email):''}</option>`).join('');}catch(cause){error(node,cause);}};
        node.querySelector('[data-match]').textContent=value.candidates.length>1?`${value.candidates.length} clients share this sender address. Staff selection is required.`:'The client’s stored email will not be changed.';
        form.elements.query.oninput=search;await search();submit(node,async form=>{await api(`/api/conversations/${id}/association/client`,'PUT',{clientId:form.elements.client.value});await refresh();});
    }
    async function jobOptions(){const jobs=await api('/api/jobs');return (jobs.jobs||jobs).map(j=>`<option value="${esc(j.id)}">${esc(j.title)} · ${esc(j.clientName)}</option>`).join('');}
    async function createJob(id,conversation,refresh){
        const node=dialog('Create job from inquiry',`<p>The selected client and this conversation will carry into the job.</p><label>Job title<input name="title" value="${esc(conversation.subject)}" required></label><label>Event date<input name="date" type="date"></label><button class="primary" type="submit">Create job</button>`);
        submit(node,async form=>{await api(`/api/conversations/${id}/jobs`,'POST',{title:form.elements.title.value,eventDate:form.elements.date.value||null});await refresh();});
    }
    async function chooseJob(id,conversation,refresh){
        const node=dialog('Job association','<label>Job<select name="job"><option value="">No job</option></select></label><p>The original inquiry and messages are retained.</p><button class="primary" type="submit">Save association</button>');
        try{node.querySelector('select').insertAdjacentHTML('beforeend',await jobOptions());node.querySelector('select').value=conversation.jobId||'';}catch(cause){error(node,cause);}
        submit(node,async form=>{await api(`/api/conversations/${id}/association/job`,'PUT',{jobId:form.elements.job.value||null});await refresh();});
    }
    async function moveMessage(id,conversation,refresh){
        const received=conversation.messages.filter(m=>m.externalMessageId);
        const node=dialog('Move received email',`<label>Email<select name="message" required>${received.map(m=>`<option value="${esc(m.id)}">${esc(new Date(m.createdAt).toLocaleString())} · ${esc(m.body.slice(0,100))}</option>`).join('')}</select></label><label>Destination<select name="job"><option value="">New inquiry about separate work</option></select></label><p>The same message and its attachments move together. No copy is created.</p><button class="primary" type="submit">Move email</button>`);
        try{node.querySelector('[name=job]').insertAdjacentHTML('beforeend',await jobOptions());}catch(cause){error(node,cause);}
        submit(node,async form=>{await api(`/api/conversations/${id}/association/messages/${form.elements.message.value}/move`,'POST',{jobId:form.elements.job.value||null,newInquiry:!form.elements.job.value});await refresh();});
    }
    async function review(){
        const node=dialog('Email routing review','<div data-items>Loading…</div>');node.querySelector('form').onsubmit=e=>e.preventDefault();
        try{
            const items=await api('/api/email/routing-review');const list=node.querySelector('[data-items]');list.textContent='';
            if(!items.length)list.textContent='No email needs routing review.';
            for(const item of items){const row=document.createElement('article');row.className='message';row.innerHTML=`<h3>${esc(item.subject)}</h3><p><strong>${esc(item.failureReason)}</strong></p><p>${esc(item.fromName)} · ${esc(item.fromAddress)}</p><p>${esc(item.body)}</p><button class="action" type="button">Review association</button>`;row.querySelector('button').onclick=()=>confirmReview(item,()=>{node.close();return review();});list.append(row);}
        }catch(cause){error(node,cause);}
    }
    async function confirmReview(item,refresh){
        const node=dialog('Confirm email association','<label>Inquiry or job<select name="target"><option value="">Create a separate inquiry</option></select></label><label>Workspace for new inquiry<select name="workspace"></select></label><label><input type="checkbox" name="contact"> Deliberately add sender as an alternate contact</label><p>Reviewing this email does not send a reply or disclose private information.</p><button class="primary" type="submit">Confirm association</button>');
        try{
            const select=node.querySelector('[name=target]');
            for(const id of item.candidateConversations||[]){const inquiry=await api(`/api/conversations/${id}`);select.insertAdjacentHTML('beforeend',`<option value="inquiry:${esc(id)}">Inquiry: ${esc(inquiry.subject)}</option>`);}
            const jobs=await api('/api/jobs');for(const job of jobs.jobs||jobs)select.insertAdjacentHTML('beforeend',`<option value="job:${esc(job.id)}">Job: ${esc(job.title)} · ${esc(job.clientName)}</option>`);
            const workspaces=await api('/api/workspaces');node.querySelector('[name=workspace]').innerHTML='<option value="">Choose workspace…</option>'+(workspaces.workspaces||workspaces).map(w=>`<option value="${esc(w.id)}" ${w.id===item.routingWorkspaceId?'selected':''}>${esc(w.name)}</option>`).join('');
        }catch(cause){error(node,cause);}
        submit(node,async form=>{const [kind,id]=form.elements.target.value.split(':');await api(`/api/email/routing-review/${item.id}/confirm`,'POST',{conversationId:kind==='inquiry'?id:null,jobId:kind==='job'?id:null,workspaceId:form.elements.workspace.value||null,newInquiry:!kind,addContact:form.elements.contact.checked});await refresh();});
    }
    root.StudioInquiryAssociation={mount,review};
})(window);
