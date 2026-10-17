/* Customer identity is always the stored clientId; customer-name snapshots are not joins. */
window.StudioCustomers=(()=>{
    let revision=0;
    let activeActions={};
    const views=new Map();
    const safe=()=>window.StudioPage.escape;
    const date=value=>{const parts=/^(\d{4})-(\d{2})-(\d{2})$/.exec(String(value||''));const d=parts?new Date(Number(parts[1]),Number(parts[2])-1,Number(parts[3])):new Date(value);return value&&Number.isFinite(+d)?d.toLocaleDateString([],{weekday:'short',month:'short',day:'2-digit',year:'numeric'}):'—';};
    const amount=(value,currency='JMD')=>new Intl.NumberFormat(undefined,{style:'currency',currency}).format(Number(value)||0);
    const loadArray=async url=>{const result=await json(url);if(!Array.isArray(result))throw Error('Unexpected response. Please refresh.');return result;};
    async function render(options={}){
        const token=++revision;activeActions={};
        const live=()=>token===revision&&document.body.dataset.studioView==='customers';
        const esc=safe();
        conversationView.classList.add('hidden');genericView.classList.remove('hidden');
        genericView.innerHTML='<p role="status">Loading customers…</p>';
        let clients,jobs,workspaces;
        try{[clients,jobs,workspaces]=await Promise.all([loadArray('/api/frontdesk/clients'),loadArray('/api/jobs'),loadArray('/api/workspaces')]);}
        catch(e){if(live()){genericView.innerHTML='<section class="mw-list-panel customer-load-error"><h2>Customers could not be loaded</h2><p role="alert">'+esc(e.message)+'</p><button class="action" data-c-retry>Retry</button></section>';genericView.querySelector('[data-c-retry]').onclick=()=>render(options);}return;}
        if(!live())return;
        const customer=clients.find(c=>c.id===options.customerId);
        const state=views.get(customer?.id||'directory')||{tab:'jobs',search:'',jobSearch:'',status:'',gallery:null,finance:null,activity:null};
        views.set(customer?.id||'directory',state);
        // Refresh invalidates derived data; switching tabs does not refetch it.
        state.gallery=null;state.finance=null;state.activity=null;
        const linked=customer?jobs.filter(j=>j.clientId===customer.id):[];
        let detailRevision=0;
        let customerMessages=null,messageError=false;
        const linkedIds=new Set(linked.map(j=>j.id));
        const wname=id=>workspaces.find(w=>w.id===id)?.name||'Workspace';
        const badge=value=>'<span class="customer-status">'+esc(value||'Not set')+'</span>';
        function paint(){
            if(!live())return;
            const matches=clients.filter(c=>[c.displayName,c.email,c.phone].join(' ').toLowerCase().includes(state.search.toLowerCase()));
            genericView.innerHTML=`<section class="st-page customers-page" aria-label="Customers"><section class="st-board customers-board"><section class="customer-directory mw-list-panel"><header><h2>Customers</h2><div class="customer-directory-tools"><button class="customer-text-link" data-c-all ${!customer?'hidden':''}>← All customers</button><button type="button" class="action" data-c-refresh>↻ Refresh</button></div></header><label class="customer-search"><span class="sr-only">Search customers</span><input data-c-search type="search" placeholder="Search customers by name, email or phone…" value="${esc(state.search)}"></label><div class="customer-directory-rows ${customer?'is-compact':''}">${matches.length?matches.map(c=>`<button type="button" class="customer-directory-row ${c.id===customer?.id?'is-selected':''}" data-c-id="${esc(c.id)}" aria-pressed="${c.id===customer?.id}"><strong>${esc(c.displayName)}</strong><span>${esc(c.email||'No email')}</span><span>${esc(c.phone||'No phone')}</span><span aria-hidden="true">›</span></button>`).join(''):'<p class="customer-empty">No customers match your search.</p>'}</div></section>${customer?`<section class="mw-list-panel customer-record"><header class="customer-record-heading"><div><h2>${esc(customer.displayName)}</h2><p>${esc(customer.email||'No email')} · ${esc(customer.phone||'No phone')}</p></div><button class="action" data-c-edit>✎ Edit details</button></header><nav class="customer-tabs" aria-label="Customer sections">${[['jobs','Jobs ('+linked.length+')'],['galleries','Galleries'],['finance','Financial Statement'],['activity','Activity']].map(([key,label])=>`<button type="button" data-c-tab="${key}" aria-pressed="${state.tab===key}">${label}</button>`).join('')}</nav><div class="customer-tab-content" data-c-content></div></section>`:'<section class="mw-list-panel customer-welcome"><span aria-hidden="true">♧</span><h2>Every customer, one place</h2><p>Select a customer to see their jobs, galleries, statements and recent activity.</p></section>'}</section><aside class="st-right customer-right" aria-label="Customer information"><section class="mw-list-panel"><header><h2>Overview</h2></header><div class="workspace-summary-row"><span>♧</span><span>${customer?'Customer jobs':'Customers'}</span><strong>${customer?linked.length:clients.length}</strong></div>${customer?`<div class="workspace-summary-row"><span>▣</span><span>Active jobs</span><strong>${linked.filter(j=>!['Completed','Cancelled'].includes(j.status)).length}</strong></div><div class="workspace-summary-row"><span>✓</span><span>Completed jobs</span><strong>${linked.filter(j=>j.status==='Completed').length}</strong></div><div class="workspace-summary-row"><span>▧</span><button class="customer-text-link" data-c-overview-gallery>View galleries</button><span>→</span></div>`:''}</section><section class="mw-list-panel customer-attention"><header><h2>Needs Attention</h2></header><div class="customer-attention-body">${customer?`${linked.filter(j=>j.blockedReason).map(j=>`<button class="customer-text-link" data-c-attention-job="${esc(j.id)}">${esc(j.title)} · ${esc(j.blockedReason)}</button>`).join('')}<button class="customer-text-link" data-c-attention-gallery>Review galleries and publishing →</button><button class="customer-text-link" data-c-attention-finance>Review statement and invoices →</button>`:'<p>Select a customer to review their jobs and account.</p>'}</div></section><section class="mw-list-panel customer-messages"><header><h2>Messages</h2></header><div class="customer-messages-body">${!customer?'<p>Select a customer to see related message alerts.</p>':messageError?'<p role="alert">Message alerts could not load.</p>':customerMessages===null?'<p role="status">Loading related message alerts…</p>':customerMessages.map(n=>`<button class="customer-message" data-c-message-job="${esc(n.referenceId)}"><strong>${esc(n.subject||'Message')}</strong><span>${esc(n.body||'')}</span><small>${esc(date(n.createdAtUtc))}</small></button>`).join('')||'<p>No recent message alerts for this customer’s jobs.</p>'}</div><footer><button class="customer-text-link" data-c-messages>Open Messages →</button></footer></section></aside></section>`;
            StudioPage.setCount(customer?linked.length+' customer jobs':matches.length+' customers');
            const search=genericView.querySelector('[data-c-search]');search.oninput=()=>{state.search=search.value;paint();genericView.querySelector('[data-c-search]').focus();};
            genericView.querySelectorAll('[data-c-id]').forEach(b=>b.onclick=()=>window.studioNavigate('customers',{customerId:b.dataset.cId}));
            genericView.querySelector('[data-c-all]').onclick=()=>window.studioNavigate('customers');
            genericView.querySelector('[data-c-refresh]').onclick=()=>render(options);
            activeActions.newCustomer=()=>editCustomer(null);
            genericView.querySelector('[data-c-edit]')?.addEventListener('click',()=>editCustomer(customer));
            activeActions.newBooking=async()=>{try{const available=await loadArray('/api/frontdesk/workspaces');if(!live())return;frontDeskWorkspaces=available;await openPendingBookingForm(null,customer||null);}catch(e){alert(e.message);}};
            genericView.querySelectorAll('[data-c-attention-job],[data-c-message-job]').forEach(b=>b.onclick=()=>openWorkspaceJob(b.dataset.cAttentionJob||b.dataset.cMessageJob).catch(e=>alert(e.message)));
            genericView.querySelector('[data-c-attention-gallery]')?.addEventListener('click',()=>{state.tab='galleries';paint();});
            genericView.querySelector('[data-c-attention-finance]')?.addEventListener('click',()=>{state.tab='finance';paint();});
            genericView.querySelector('[data-c-messages]').onclick=()=>window.studioNavigate('messages');
            genericView.querySelectorAll('[data-c-tab]').forEach(b=>b.onclick=()=>{state.tab=b.dataset.cTab;paint();});
            genericView.querySelector('[data-c-overview-gallery]')?.addEventListener('click',()=>{state.tab='galleries';paint();});
            if(customer)void showTab();
        }
        async function editCustomer(client){
            const d=studioDialog(client?'Edit customer':'New customer',`<form class="subject-form"><label>Name<input name="displayName" value="${esc(client?.displayName||'')}" required maxlength="200"></label><label>Email<input name="email" type="email" maxlength="320" value="${esc(client?.email||'')}"></label><label>Phone<input name="phone" maxlength="80" value="${esc(client?.phone||'')}"></label><p role="alert"></p><button class="primary" type="submit">Save customer</button></form>`);
            const form=d.querySelector('form');form.onsubmit=async e=>{e.preventDefault();const button=form.querySelector('button');button.disabled=true;try{const saved=await json('/api/frontdesk/clients'+(client?'/'+encodeURIComponent(client.id):''),{method:client?'PUT':'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({displayName:form.elements.displayName.value.trim(),email:form.elements.email.value.trim()||null,phone:form.elements.phone.value.trim()||null})});d.close();if(live())await window.studioNavigate('customers',{customerId:saved.id});}catch(e){form.querySelector('[role=alert]').textContent=e.message;button.disabled=false;}};
        }
        async function showTab(){
            const turn=++detailRevision,tab=state.tab,host=genericView.querySelector('[data-c-content]');
            const current=()=>live()&&turn===detailRevision&&host.isConnected;
            host.innerHTML='<p class="customer-empty" role="status">Loading…</p>';
            try{
                if(tab==='jobs'){
                    const values=linked.filter(j=>(!state.status||j.status===state.status)&&[j.title,j.status,wname(j.workspaceId)].join(' ').toLowerCase().includes(state.jobSearch.toLowerCase()));
                    host.innerHTML=`<div class="customer-job-filters"><input data-c-job-search type="search" placeholder="Search this customer’s jobs…" aria-label="Search customer jobs" value="${esc(state.jobSearch)}"><select data-c-job-status aria-label="Job status"><option value="">All statuses</option>${[...new Set(linked.map(j=>j.status))].sort().map(s=>`<option ${s===state.status?'selected':''}>${esc(s)}</option>`).join('')}</select></div><table class="studio-table customer-table"><thead><tr><th>Job / Workspace</th><th>Event date</th><th>Status</th><th>Gallery</th></tr></thead><tbody>${values.map(j=>`<tr><td><button class="customer-text-link" data-c-job="${esc(j.id)}">${esc(j.title)}</button><small>${esc(wname(j.workspaceId))}</small></td><td>${esc(date(j.eventDate))}</td><td>${badge(j.status)}</td><td><button class="action" data-c-gallery-job="${esc(j.id)}">View gallery</button></td></tr>`).join('')||'<tr><td colspan="4">No linked jobs match this view.</td></tr>'}</tbody></table>`;
                    host.querySelector('[data-c-job-search]').oninput=e=>{state.jobSearch=e.target.value;showTab();host.querySelector('[data-c-job-search]').focus();};
                    host.querySelector('[data-c-job-status]').onchange=e=>{state.status=e.target.value;showTab();};
                    host.querySelectorAll('[data-c-gallery-job]').forEach(b=>b.onclick=()=>{state.tab='galleries';state.focusJob=b.dataset.cGalleryJob;paint();});
                }else if(tab==='galleries'){
                    if(!state.gallery)state.gallery=await mapJobs(async j=>{const [media,workflow,publications]=await Promise.all([json('/api/jobs/'+j.id+'/media?limit=12'),json('/api/jobs/'+j.id+'/workflow'),loadArray('/api/jobs/'+j.id+'/media/publications')]);return {job:j,media,workflow,publications};});
                    if(!current())return;
                    host.innerHTML=`<p class="customer-note">Each gallery belongs to a linked job. Publishing uses its approved finished submission; later uploads are not automatically included.</p>${state.gallery.map(entry=>entry.error?`<article class="customer-gallery-row"><h3>${esc(entry.job.title)}</h3><p role="alert">Gallery unavailable: ${esc(entry.error)}</p></article>`:galleryRow(entry)).join('')||'<p class="customer-empty">No linked job galleries yet.</p>'}<p class="customer-note" role="status" data-c-gallery-feedback></p>`;
                    host.querySelectorAll('[data-c-publish]').forEach(b=>b.onclick=()=>publishOptions(state.gallery.find(x=>x.job.id===b.dataset.cPublish)));
                    host.querySelectorAll('[data-c-media]').forEach(b=>b.onclick=()=>mediaPreview(state.gallery.find(x=>x.job.id===b.dataset.cMedia)));
                    if(state.focusJob){const row=[...host.querySelectorAll('[data-c-gallery-row]')].find(r=>r.dataset.cGalleryRow===state.focusJob);row?.scrollIntoView?.({block:'nearest'});state.focusJob=null;}
                }else if(tab==='finance'){
                    if(!state.finance)state.finance=await mapJobs(async j=>{const [invoices,balance]=await Promise.allSettled([loadArray('/api/jobs/'+j.id+'/invoices'),json('/api/jobs/'+j.id+'/finance-release')]);return {job:j,invoices:invoices.status==='fulfilled'?invoices.value:null,balance:balance.status==='fulfilled'?balance.value.decision?.balance:null,error:invoices.status==='rejected'?invoices.reason.message:null};});
                    if(!current())return;
                    const ledger=linked.flatMap(j=>(j.financialEvents||[]).map(e=>({...e,jobId:j.id,jobTitle:j.title}))).sort((a,b)=>new Date(a.createdAt||a.createdAtUtc)-new Date(b.createdAt||b.createdAtUtc));
                    const invoices=[...new Map(state.finance.flatMap(x=>x.invoices||[]).map(i=>[i.id,i])).values()];
                    host.innerHTML=`<div class="customer-statement" data-c-statement><header class="customer-statement-heading"><div><h3>Customer financial statement</h3><p>${esc(customer.displayName)} · ${esc(date(new Date()))}</p></div><button class="action" data-c-print>Print statement</button></header><p class="customer-note">All recorded activity for linked jobs. Job balances are supplied by Finance. Invoice totals are shown separately and are not added to the ledger.</p><h4>Job balances · JMD</h4><table class="studio-table customer-table"><thead><tr><th>Job</th><th>Status</th><th>Balance</th></tr></thead><tbody>${state.finance.map(x=>`<tr><td>${esc(x.job.title)}</td><td>${badge(x.job.status)}</td><td>${x.balance==null?'Unavailable':esc(amount(x.balance))}</td></tr>`).join('')||'<tr><td colspan="3">No linked jobs.</td></tr>'}</tbody></table>${state.finance.some(x=>x.error||x.balance==null)?'<p role="alert">Some financial information is unavailable or restricted. Refresh to try again; unavailable balances are not zero.</p>':''}<h4>Invoices</h4><table class="studio-table customer-table"><thead><tr><th>Invoice</th><th>Due</th><th>Status</th><th>Total</th></tr></thead><tbody>${invoices.map(i=>`<tr><td>${esc(i.invoiceNumber)}</td><td>${esc(date(i.dueAtUtc))}</td><td>${badge(i.status)}</td><td>${esc(amount(i.totalAmount,i.currency||'JMD'))}</td></tr>`).join('')||'<tr><td colspan="4">No available invoices.</td></tr>'}</tbody></table><h4>Job ledger · JMD</h4><table class="studio-table customer-table"><thead><tr><th>Date / Job</th><th>Entry</th><th>Reference / Note</th><th>Signed amount</th></tr></thead><tbody>${ledger.map(e=>`<tr><td>${esc(date(e.createdAt||e.createdAtUtc))}<small>${esc(e.jobTitle)}</small></td><td>${esc(e.kind)}</td><td>${esc(e.reference||e.note||'—')}</td><td>${esc(amount(e.amount))}</td></tr>`).join('')||'<tr><td colspan="4">No recorded job financial activity.</td></tr>'}</tbody></table></div>`;
                    host.querySelector('[data-c-print]').onclick=()=>printStatement(host.querySelector('[data-c-statement]'),customer.displayName);
                }else{
                    if(!state.activity)state.activity=await mapJobs(async j=>({job:j,events:await loadArray('/api/jobs/'+j.id+'/operations/audit')}));
                    if(!current())return;
                    const events=state.activity.flatMap(x=>(x.events||[]).map(e=>({...e,job:x.job}))).sort((a,b)=>new Date(b.createdAtUtc)-new Date(a.createdAtUtc));
                    host.innerHTML=(state.activity.some(x=>x.error)?'<p role="alert">Some job activity could not be loaded.</p>':'')+events.map(e=>`<article class="customer-activity"><span class="customer-note">${esc(date(e.createdAtUtc))} · ${esc(e.job.title)}</span><h3>${esc(e.action)}</h3><p>${esc(e.detail||'')}</p><small>${esc(e.userName||'Studio')}</small></article>`).join('')||'<p class="customer-empty">No recorded activity.</p>';
                }
                if(current())host.querySelectorAll('[data-c-job]').forEach(b=>b.onclick=()=>openWorkspaceJob(b.dataset.cJob).catch(e=>alert(e.message)));
            }catch(e){if(current()){host.innerHTML='<p class="customer-empty" role="alert">'+esc(e.message)+'</p><button class="action" data-c-retry-tab>Retry</button>';host.querySelector('[data-c-retry-tab]').onclick=()=>{state.gallery=null;state.finance=null;state.activity=null;showTab();};}}
        }
        async function mapJobs(fn){
            const output=new Array(linked.length);let next=0;
            async function worker(){while(next<linked.length){if(!live())return;const i=next++;try{output[i]=await fn(linked[i]);}catch(e){output[i]={job:linked[i],error:e.message};}}}
            await Promise.all([worker(),worker(),worker()]);return output.filter(Boolean);
        }
        function galleryRow(entry){
            const {job,media,workflow,publications}=entry,latest=publications[0];
            const asset=(media.assets||[]).find(a=>a.versionId&&studioMediaIsPreviewable(a));
            const status=latest?'Handoff recorded':(workflow.actions||[]).includes('publish')?'Ready for publishing review':'Not published';
            return `<article class="customer-gallery-row" data-c-gallery-row="${esc(job.id)}"><div class="customer-gallery-image">${asset?`<img loading="lazy" src="/api/jobs/${encodeURIComponent(job.id)}/versions/by-id/${encodeURIComponent(asset.versionId)}/preview?size=thumbnail" alt="${esc(job.title)} gallery preview">`:'<span aria-hidden="true">▧</span>'}</div><div class="customer-gallery-copy"><h3>${esc(job.title)}</h3><p>${esc(media.gallery?.name||'Job gallery')} · ${esc(wname(job.workspaceId))}</p><small>${Number(media.totalAssets??media.assets?.length??0)} images · ${esc((media.collections||[]).join(', ')||'Unclassified')}</small></div><div><span class="customer-status">${status}</span><small>${latest?esc(date(latest.createdAtUtc)):'Approval and publication checks apply'}</small></div><div class="customer-gallery-actions"><button class="action" data-c-media="${esc(job.id)}">View Gallery</button><button class="primary" data-c-publish="${esc(job.id)}">Publishing options</button><button class="customer-text-link" data-c-job="${esc(job.id)}">Open job →</button></div></article>`;
        }
        function mediaPreview(entry){
            const d=studioDialog(entry.job.title+' · Gallery','<div class="customer-media-preview"></div>');
            renderProductionMedia(entry.job,d.querySelector('.customer-media-preview'),()=>d.isConnected).catch(e=>{if(d.isConnected)d.querySelector('.customer-media-preview').textContent=e.message;});
        }
        async function publishOptions(entry){
            const d=studioDialog(entry.job.title+' · Publishing options','<p role="status">Checking publication…</p>');
            try{
                const workflow=await json('/api/jobs/'+entry.job.id+'/workflow');if(!d.isConnected)return;
                const body=d.querySelector('.studio-dialog-body');const allowed=(workflow.actions||[]).includes('publish');
                body.innerHTML=`<h3>Publish approved gallery</h3><p>${esc(entry.job.title)}</p><p>Current stage: <strong>${esc(workflow.status)}</strong></p><p>This records a handoff of the approved finished submission. A handoff does not confirm that an external customer website is live.</p>${entry.publications.length?`<p>Previous handoffs: ${entry.publications.length}. Latest: ${esc(date(entry.publications[0].createdAtUtc))}.</p>`:''}<p>${allowed?'Publishing is available for this job. Final approval and work-order checks will run when you publish.':'Publishing is unavailable at this stage or with your current permissions. Open the job to review its approvals.'}</p><p role="alert"></p><button class="primary" data-c-confirm-publish ${allowed?'':'disabled'}>Publish approved gallery</button><button class="action" data-c-open-job>Open job</button>`;
                body.querySelector('[data-c-open-job]').onclick=()=>{d.close();openWorkspaceJob(entry.job.id).catch(e=>alert(e.message));};
                const operationId=crypto.randomUUID();let sending=false;
                body.querySelector('[data-c-confirm-publish]').onclick=async()=>{if(sending)return;sending=true;const button=body.querySelector('[data-c-confirm-publish]');button.disabled=true;try{await json('/api/jobs/'+entry.job.id+'/media/publication',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({action:'publish',expectedRevision:workflow.revision,operationId})});d.close();state.gallery=null;if(live())paint();}catch(e){if(d.isConnected){body.querySelector('[role=alert]').textContent=e.message;button.disabled=false;}sending=false;}};
            }catch(e){if(d.isConnected)d.querySelector('.studio-dialog-body').textContent=e.message;}
        }
        paint();
        if(customer){
            loadArray('/api/notifications?count=200').then(items=>{if(!live())return;customerMessages=items.filter(n=>n.channel==='InApp'&&n.referenceType==='Job'&&linkedIds.has(n.referenceId)&&/message|mention|reply|conversation/i.test(n.eventType||'')).slice(0,6);paint();}).catch(()=>{if(live()){messageError=true;paint();}});
        }
    }
    function printStatement(source,name){
        const popup=window.open('about:blank','_blank');if(!popup){alert('Allow popups to print this statement.');return;}
        popup.opener=null;
        const doc=popup.document;doc.title=name+' — Customer statement';
        const style=doc.createElement('style');style.textContent='body{font:12px Arial,sans-serif;color:#142d46;padding:24px}table{width:100%;border-collapse:collapse;margin:12px 0 24px}th,td{padding:7px;border-bottom:1px solid #ccc;text-align:left}small{display:block}button{display:none}p{line-height:1.5}';doc.head.append(style);doc.body.append(source.cloneNode(true));popup.focus();popup.print();
    }
    return {render,newCustomer(){return activeActions.newCustomer?.();},newBooking(){return activeActions.newBooking?.();},invalidate(){++revision;activeActions={};}};
})();
