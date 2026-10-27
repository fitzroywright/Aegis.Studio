/* Client identity is always the stored clientId; client-name snapshots are not joins. */
window.StudioClients=(()=>{
    let revision=0;
    let activeActions={};
    const views=new Map();
    let directoryScroll={top:0,left:0};
    const safe=()=>window.StudioPage.escape;
    const date=value=>{const parts=/^(\d{4})-(\d{2})-(\d{2})$/.exec(String(value||''));const d=parts?new Date(Number(parts[1]),Number(parts[2])-1,Number(parts[3])):new Date(value);return value&&Number.isFinite(+d)?d.toLocaleDateString([],{weekday:'short',month:'short',day:'2-digit',year:'numeric'}).replaceAll(',',''):'—';};
    const amount=(value,currency='JMD')=>new Intl.NumberFormat(undefined,{style:'currency',currency}).format(Number(value)||0);
    const loadArray=async url=>{const result=await json(url);if(!Array.isArray(result))throw Error('Unexpected response. Please refresh.');return result;};
    async function render(options={}){
        const token=++revision;activeActions={};
        const live=()=>token===revision&&document.body.dataset.studioView==='clients';
        const esc=safe();
        conversationView.classList.add('hidden');genericView.classList.remove('hidden');
        genericView.innerHTML='<p role="status">Loading clients…</p>';
        let clients,jobs,workspaces,workflowJobs=[],employees=[];
        try{[clients,jobs,workspaces,workflowJobs,employees]=await Promise.all([loadArray('/api/frontdesk/clients'),loadArray('/api/jobs'),loadArray('/api/workspaces'),json('/api/workflow/jobs').catch(()=>[]),json('/api/studio-employees?assignable=true').catch(()=>[])]);}
        catch(e){if(live()){genericView.innerHTML='<section class="mw-list-panel client-load-error"><h2>Clients could not be loaded</h2><p role="alert">'+esc(e.message)+'</p><button class="action" data-c-retry>Retry</button></section>';StudioPage.query(genericView,'[data-c-retry]').onclick=()=>render(options);}return;}
        if(!live())return;
        const client=clients.find(c=>c.id===options.clientId);
        const state=views.get(client?.id||'directory')||{tab:'jobs',search:'',jobSearch:'',status:'',gallery:null,finance:null,activity:null};
        if(['jobs','finance','reminders','galleries','activity'].includes(options.tab))state.tab=options.tab;
        views.set(client?.id||'directory',state);
        // Refresh invalidates derived data; switching tabs does not refetch it.
        state.gallery=null;state.finance=null;state.activity=null;
        const linked=client?jobs.filter(j=>j.clientId===client.id):[];
        let detailRevision=0;
        const wname=id=>workspaces.find(w=>w.id===id)?.name||'Workspace';
        const workflowByJob=new Map((Array.isArray(workflowJobs)?workflowJobs:[]).map(item=>[String(item.jobId),item]));
        const employeeNames=new Map((Array.isArray(employees)?employees:[]).map(person=>[String(person.userName||'').toLowerCase(),person.displayName||person.fullName||person.userName]));
        const jobOwner=job=>workflowByJob.get(String(job.id))?.owner||job.jobOwner||job.jobLead||job.assignedTo||'';
        const jobOwnerName=job=>employeeNames.get(String(jobOwner(job)).toLowerCase())||jobOwner(job)||'Unassigned';
        const tone=value=>/cancel|changes/i.test(value)?'rose':/completed|delivered|ready for delivery/i.test(value)?'green':/approval|review/i.test(value)?'purple':/production|upload/i.test(value)?'blue':/scheduled/i.test(value)&&!/awaiting/i.test(value)?'teal':'amber';
        const badge=value=>'<span class="sj-pill sj-'+tone(value)+'">'+esc(value||'Not set')+'</span>';
        function paint(){
            if(!live())return;
            const matches=clients.filter(c=>[c.displayName,c.email,c.phone].join(' ').toLowerCase().includes(state.search.toLowerCase()));
            genericView.innerHTML=`<section class="st-page clients-page" aria-label="Clients"><section class="st-board clients-board"><section class="client-directory mw-list-panel"><div class="client-directory-head-clip"><table class="sj-table client-directory-table"><thead><tr><th><label class="client-column-search"><span>Client</span><input data-c-search type="search" aria-label="Search clients by name, email or phone" placeholder="Search clients…" value="${esc(state.search)}"></label></th><th>Email</th><th>Telephone</th></tr></thead></table></div><div class="client-directory-rows"><table class="sj-table client-directory-table"><tbody>${matches.length?matches.map(c=>`<tr tabindex="0" class="${c.id===client?.id?'is-selected':''}" data-c-id="${esc(c.id)}" aria-selected="${c.id===client?.id}"><td><strong>${esc(c.displayName)}</strong></td><td>${esc(c.email||'No email')}</td><td>${esc(c.phone||'No phone')}</td></tr>`).join(''):'<tr><td colspan="3" class="sj-empty">No clients match your search.</td></tr>'}</tbody></table></div></section>${client?`<section class="mw-list-panel client-record"><nav class="client-tabs" aria-label="Client sections">${[['jobs','Jobs ('+linked.length+')'],['galleries','Galleries'],['finance','Financial Statement'],['activity','Activity'],['reminders','Reminders']].map(([key,label])=>`<button type="button" data-c-tab="${key}" aria-pressed="${state.tab===key}">${label}</button>`).join('')}<button type="button" class="client-edit-details" data-c-edit>✎ Edit details</button></nav><div class="client-tab-content" data-c-content></div></section>`:'<section class="mw-list-panel client-welcome"><span aria-hidden="true">♧</span><h2>Every client, one place</h2><p>Select a client to see their jobs, galleries, statements and recent activity.</p></section>'}</section><div data-studio-context><section class="sj-card"><header><h2>Overview</h2></header><div class="sj-card-body"><div class="workspace-summary-row"><span>♧</span><span>${client?'Client jobs':'Clients'}</span><strong>${client?linked.length:clients.length}</strong></div>${client?`<div class="workspace-summary-row"><span>▣</span><span>Active jobs</span><strong>${linked.filter(j=>!['Completed','Cancelled'].includes(j.status)).length}</strong></div><div class="workspace-summary-row"><span>✓</span><span>Completed jobs</span><strong>${linked.filter(j=>j.status==='Completed').length}</strong></div>`:''}</div></section><div data-studio-attention><div class="sj-card-body client-attention-body">${client?`${linked.filter(j=>j.blockedReason).map(j=>`<button class="client-text-link" data-attention-item="${esc(j.id)}" data-c-attention-job="${esc(j.id)}">${esc(j.title)} · ${esc(j.blockedReason)}</button>`).join('')}${linked.some(j=>j.blockedReason)?'':'<p>No client jobs currently need attention.</p>'}`:'<p>Select a client to review their jobs and account.</p>'}</div></div></div></section>`;
            genericView.querySelector('[data-studio-context] > section').dataset.railCard='overview';
            StudioPage.mountContext(genericView,'clients');
            StudioPage.setCount(client?linked.length+' client jobs':matches.length+' clients');
            const search=StudioPage.query(genericView,'[data-c-search]');search.oninput=()=>{const start=search.selectionStart,end=search.selectionEnd,direction=search.selectionDirection;state.search=search.value;paint();const next=StudioPage.query(genericView,'[data-c-search]');next.focus({preventScroll:true});next.setSelectionRange(start,end,direction);};
            const directory=genericView.querySelector('.client-directory-rows');
            directory.scrollTop=directoryScroll.top;directory.scrollLeft=directoryScroll.left;
            directory.onscroll=()=>{directoryScroll={top:directory.scrollTop,left:directory.scrollLeft};};
            StudioPage.queryAll(genericView,'[data-c-id]').forEach(b=>b.onclick=()=>{directoryScroll={top:directory.scrollTop,left:directory.scrollLeft};return window.studioNavigate('clients',{clientId:b.dataset.cId});});
            activeActions.newClient=()=>editClient(null);
            StudioPage.query(genericView,'[data-c-edit]')?.addEventListener('click',()=>editClient(client));
            activeActions.newBooking=async()=>{try{const available=await loadArray('/api/frontdesk/workspaces');if(!live())return;frontDeskWorkspaces=available;await openPendingBookingForm(null,client||null);}catch(e){alert(e.message);}};
            StudioPage.queryAll(genericView,'[data-c-attention-job]').forEach(b=>b.onclick=()=>openWorkspaceJob(b.dataset.cAttentionJob).catch(e=>alert(e.message)));
            StudioPage.queryAll(genericView,'[data-c-id]').forEach(row=>row.onkeydown=e=>{if(e.key==='Enter'||e.key===' '){e.preventDefault();row.click();}});
            StudioPage.queryAll(genericView,'[data-c-tab]').forEach(b=>b.onclick=()=>{state.tab=b.dataset.cTab;paint();});
            if(client)void showTab();
        }
        async function editClient(client){
            const d=studioDialog(client?'Edit client':'New client',`<form class="subject-form"><label>Name<input name="displayName" value="${esc(client?.displayName||'')}" required maxlength="200"></label><label>Email<input name="email" type="email" maxlength="320" value="${esc(client?.email||'')}"></label><label>Phone<input name="phone" maxlength="80" value="${esc(client?.phone||'')}"></label><p role="alert"></p><button class="primary" type="submit">Save client</button></form>`);
            const form=d.querySelector('form');form.onsubmit=async e=>{e.preventDefault();const button=form.querySelector('button');button.disabled=true;try{const saved=await json('/api/frontdesk/clients'+(client?'/'+encodeURIComponent(client.id):''),{method:client?'PUT':'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({displayName:form.elements.displayName.value.trim(),email:form.elements.email.value.trim()||null,phone:form.elements.phone.value.trim()||null})});d.close();if(live())await window.studioNavigate('clients',{clientId:saved.id});}catch(e){form.querySelector('[role=alert]').textContent=e.message;button.disabled=false;}};
        }
        async function showTab(){
            const turn=++detailRevision,tab=state.tab,host=StudioPage.query(genericView,'[data-c-content]');
            const current=()=>live()&&turn===detailRevision&&host.isConnected;
            host.classList.toggle('client-tab-content--jobs',tab==='jobs');
            host.innerHTML='<p class="client-empty" role="status">Loading…</p>';
            try{
                if(tab==='jobs'){
                    const values=linked.filter(j=>(!state.status||j.status===state.status)&&[j.title,j.status,wname(j.workspaceId),jobOwnerName(j)].join(' ').toLowerCase().includes(state.jobSearch.toLowerCase()));
                    host.innerHTML=`<div class="client-jobs-grid"><div class="client-jobs-head-clip"><table class="sj-table client-jobs-table"><thead><tr><th><label class="client-column-search"><span>Job / Workspace</span><input data-c-job-search type="search" placeholder="Search jobs…" aria-label="Search client jobs" value="${esc(state.jobSearch)}"></label></th><th>Event date</th><th>Job Owner</th><th><label class="sj-column-filter sj-column-filter-inline"><span>Status</span><select data-c-job-status aria-label="Job status"><option value="">All statuses</option>${[...new Set(linked.map(j=>j.status))].sort().map(s=>`<option ${s===state.status?'selected':''}>${esc(s)}</option>`).join('')}</select></label></th></tr></thead></table></div><div class="client-jobs-scroll"><table class="sj-table client-jobs-table"><tbody>${values.map(j=>`<tr tabindex="0" data-c-job="${esc(j.id)}" data-sj-job="${esc(j.id)}"><td><strong>${esc(j.title)}</strong><small>${esc(wname(j.workspaceId))}</small></td><td>${esc(date(j.eventDate))}</td><td><span class="sj-owner"><span class="sj-initials" aria-hidden="true">${jobOwner(j)?esc(jobOwnerName(j).split(/[\s.]+/).filter(Boolean).slice(0,2).map(part=>part[0]).join('').toUpperCase()):'–'}</span>${esc(jobOwnerName(j))}</span></td><td>${badge(j.status)}</td></tr>`).join('')||'<tr><td colspan="4">No linked jobs match this view.</td></tr>'}</tbody></table></div></div>`;
                    host.querySelector('[data-c-job-search]').oninput=e=>{const input=e.target,start=input.selectionStart,end=input.selectionEnd,direction=input.selectionDirection;state.jobSearch=input.value;showTab();const next=host.querySelector('[data-c-job-search]');next.focus({preventScroll:true});next.setSelectionRange(start,end,direction);};
                    host.querySelector('[data-c-job-status]').onchange=e=>{state.status=e.target.value;showTab();};
                }else if(tab==='reminders'){
                    await window.StudioClientReminders.mountClient(host,linked,current);
                }else if(tab==='galleries'){
                    if(!state.gallery)state.gallery=await mapJobs(async j=>{const [media,workflow,publications]=await Promise.all([json('/api/jobs/'+j.id+'/media?limit=12'),json('/api/jobs/'+j.id+'/workflow'),loadArray('/api/jobs/'+j.id+'/media/publications')]);return {job:j,media,workflow,publications};});
                    if(!current())return;
                    host.innerHTML=`<p class="client-note">Each gallery belongs to a linked job. Publishing uses its approved finished submission; later uploads are not automatically included.</p>${state.gallery.map(entry=>entry.error?`<article class="client-gallery-row"><h3>${esc(entry.job.title)}</h3><p role="alert">Gallery unavailable: ${esc(entry.error)}</p></article>`:galleryRow(entry)).join('')||'<p class="client-empty">No linked job galleries yet.</p>'}<p class="client-note" role="status" data-c-gallery-feedback></p>`;
                    host.querySelectorAll('[data-c-publish]').forEach(b=>b.onclick=()=>publishOptions(state.gallery.find(x=>x.job.id===b.dataset.cPublish)));
                    host.querySelectorAll('[data-c-media]').forEach(b=>b.onclick=()=>mediaPreview(state.gallery.find(x=>x.job.id===b.dataset.cMedia)));
                }else if(tab==='finance'){
                    if(!state.finance)state.finance=await mapJobs(async j=>{const [invoices,balance]=await Promise.allSettled([loadArray('/api/jobs/'+j.id+'/invoices'),json('/api/jobs/'+j.id+'/finance-release')]);return {job:j,invoices:invoices.status==='fulfilled'?invoices.value:null,balance:balance.status==='fulfilled'?balance.value.decision?.balance:null,error:invoices.status==='rejected'?invoices.reason.message:null};});
                    if(!current())return;
                    const ledger=linked.flatMap(j=>(j.financialEvents||[]).map(e=>({...e,jobId:j.id,jobTitle:j.title}))).sort((a,b)=>new Date(a.createdAt||a.createdAtUtc)-new Date(b.createdAt||b.createdAtUtc));
                    const invoices=[...new Map(state.finance.flatMap(x=>x.invoices||[]).map(i=>[i.id,i])).values()];
                    host.innerHTML=`<div class="client-statement" data-c-statement><header class="client-statement-heading"><div><h3>Financial Statement</h3><p>${esc(client.displayName)} · ${esc(date(new Date()))}</p></div><button class="action" data-c-print>Print statement</button></header><p class="client-note">All recorded activity for linked jobs. Job balances are supplied by Finance. Invoice totals are shown separately and are not added to the ledger.</p><section class="client-statement-section" aria-label="Job balances"><header><h4>Job balances <span>JMD</span></h4><small>${state.finance.length} linked jobs</small></header><div class="client-finance-cards">${state.finance.map(x=>`<article class="client-finance-card"><header><h5>${esc(x.job.title)}</h5>${badge(x.job.status)}</header><span class="client-finance-label">Job balance · JMD</span><strong class="client-finance-amount" data-client-balance>${x.balance==null?'Unavailable':esc(amount(x.balance))}</strong><button type="button" class="client-text-link" data-c-job="${esc(x.job.id)}">Open job →</button></article>`).join('')||'<p class="client-empty">No linked jobs.</p>'}</div>${state.finance.some(x=>x.error||x.balance==null)?'<p class="client-finance-warning" role="alert">Some financial information is unavailable or restricted. Refresh to try again; unavailable balances are not zero.</p>':''}</section><section class="client-statement-section" aria-label="Invoices"><header><h4>Invoices</h4><small>${invoices.length} available</small></header><div class="client-finance-cards">${invoices.map(i=>`<article class="client-finance-card client-invoice-card"><header><h5>${esc(i.invoiceNumber)}</h5>${badge(i.status)}</header><span class="client-finance-label">Invoice total · ${esc(i.currency||'JMD')}</span><strong class="client-finance-amount">${esc(amount(i.totalAmount,i.currency||'JMD'))}</strong><dl><div><dt>Due date</dt><dd>${esc(date(i.dueAtUtc))}</dd></div></dl></article>`).join('')||'<p class="client-empty">No available invoices.</p>'}</div></section><section class="client-statement-section" aria-label="Job ledger"><header><h4>Job ledger <span>JMD</span></h4><small>${ledger.length} recorded entries</small></header><div class="client-ledger-scroll"><table class="client-statement-ledger"><thead><tr><th scope="col">Date / Job</th><th scope="col">Entry</th><th scope="col">Reference / Note</th><th scope="col">Signed amount</th></tr></thead><tbody>${ledger.map(e=>`<tr><td>${esc(date(e.createdAt||e.createdAtUtc))}<small>${esc(e.jobTitle)}</small></td><td>${esc(e.kind)}</td><td>${esc(e.reference||e.note||'—')}</td><td>${esc(amount(e.amount))}</td></tr>`).join('')||'<tr><td colspan="4">No recorded job financial activity.</td></tr>'}</tbody></table></div></section></div>`;
                    host.querySelector('[data-c-print]').onclick=()=>printStatement(host.querySelector('[data-c-statement]'),client.displayName);
                }else{
                    if(!state.activity)state.activity=await mapJobs(async j=>({job:j,events:await loadArray('/api/jobs/'+j.id+'/operations/audit')}));
                    if(!current())return;
                    const events=state.activity.flatMap(x=>(x.events||[]).map(e=>({...e,job:x.job}))).sort((a,b)=>new Date(b.createdAtUtc)-new Date(a.createdAtUtc));
                    host.innerHTML=(state.activity.some(x=>x.error)?'<p role="alert">Some job activity could not be loaded.</p>':'')+events.map(e=>`<article class="client-activity"><span class="client-note">${esc(date(e.createdAtUtc))} · ${esc(e.job.title)}</span><h3>${esc(e.action)}</h3><p>${esc(e.detail||'')}</p><small>${esc(e.userName||'Studio')}</small></article>`).join('')||'<p class="client-empty">No recorded activity.</p>';
                }
                if(!current())return;
                host.querySelectorAll('tr[data-c-job]').forEach(row=>row.onkeydown=e=>{if(e.target===row&&(e.key==='Enter'||e.key===' ')){e.preventDefault();row.click();}});
                host.querySelectorAll('[data-c-job]').forEach(b=>b.onclick=()=>openWorkspaceJob(b.dataset.cJob).catch(e=>alert(e.message)));
            }catch(e){if(current()){host.innerHTML='<p class="client-empty" role="alert">'+esc(e.message)+'</p><button class="action" data-c-retry-tab>Retry</button>';host.querySelector('[data-c-retry-tab]').onclick=()=>{state.gallery=null;state.finance=null;state.activity=null;showTab();};}}
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
            return `<article class="client-gallery-row" data-c-gallery-row="${esc(job.id)}"><div class="client-gallery-image">${asset?`<img loading="lazy" src="/api/jobs/${encodeURIComponent(job.id)}/versions/by-id/${encodeURIComponent(asset.versionId)}/preview?size=thumbnail" alt="${esc(job.title)} gallery preview">`:'<span aria-hidden="true">▧</span>'}</div><div class="client-gallery-copy"><h3>${esc(job.title)}</h3><p>${esc(media.gallery?.name||'Job gallery')} · ${esc(wname(job.workspaceId))}</p><small>${Number(media.totalAssets??media.assets?.length??0)} images · ${esc((media.collections||[]).join(', ')||'Unclassified')}</small></div><div><span class="client-status">${status}</span><small>${latest?esc(date(latest.createdAtUtc)):'Approval and publication checks apply'}</small></div><div class="client-gallery-actions"><button class="action" data-c-media="${esc(job.id)}">View Gallery</button><button class="primary" data-c-publish="${esc(job.id)}">Publishing options</button><button class="client-text-link" data-c-job="${esc(job.id)}">Open job →</button></div></article>`;
        }
        function mediaPreview(entry){
            window.StudioJobDetails.openGallery(entry.job,{data:entry.media,current:live}).catch(e=>{if(live())alert(e.message);});
        }
        async function publishOptions(entry){
            const d=studioDialog(entry.job.title+' · Publishing options','<p role="status">Checking publication…</p>');
            try{
                const workflow=await json('/api/jobs/'+entry.job.id+'/workflow');if(!d.isConnected)return;
                const body=d.querySelector('.studio-dialog-body');const allowed=(workflow.actions||[]).includes('publish');
                body.innerHTML=`<h3>Publish approved gallery</h3><p>${esc(entry.job.title)}</p><p>Current stage: <strong>${esc(workflow.status)}</strong></p><p>This records a handoff of the approved finished submission. A handoff does not confirm that an external client website is live.</p>${entry.publications.length?`<p>Previous handoffs: ${entry.publications.length}. Latest: ${esc(date(entry.publications[0].createdAtUtc))}.</p>`:''}<p>${allowed?'Publishing is available for this job. Final approval and work-order checks will run when you publish.':'Publishing is unavailable at this stage or with your current permissions. Open the job to review its approvals.'}</p><p role="alert"></p><button class="primary" data-c-confirm-publish ${allowed?'':'disabled'}>Publish approved gallery</button><button class="action" data-c-open-job>Open job</button>`;
                body.querySelector('[data-c-open-job]').onclick=()=>{d.close();openWorkspaceJob(entry.job.id).catch(e=>alert(e.message));};
                const operationId=crypto.randomUUID();let sending=false;
                body.querySelector('[data-c-confirm-publish]').onclick=async()=>{if(sending)return;sending=true;const button=body.querySelector('[data-c-confirm-publish]');button.disabled=true;try{await json('/api/jobs/'+entry.job.id+'/media/publication',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({action:'publish',expectedRevision:workflow.revision,operationId})});d.close();state.gallery=null;if(live())paint();}catch(e){if(d.isConnected){body.querySelector('[role=alert]').textContent=e.message;button.disabled=false;}sending=false;}};
            }catch(e){if(d.isConnected)d.querySelector('.studio-dialog-body').textContent=e.message;}
        }
        paint();
    }
    function printStatement(source,name){
        const popup=window.open('about:blank','_blank');if(!popup){alert('Allow popups to print this statement.');return;}
        popup.opener=null;
        const doc=popup.document;doc.title=name+' — Client statement';
        const style=doc.createElement('style');style.textContent='body{font:12px Arial,sans-serif;color:#142d46;padding:24px}table{width:100%;border-collapse:collapse;margin:12px 0 24px}th,td{padding:7px;border-bottom:1px solid #ccc;text-align:left}small{display:block}button{display:none}p{line-height:1.5}.client-statement-heading{margin-bottom:12px}.client-finance-cards{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:12px}.client-finance-card{border:1px solid #ccc;padding:12px;break-inside:avoid}.client-finance-card h5{margin:0 0 8px;font-size:13px}.client-finance-label{display:block;margin-top:10px}.client-finance-amount{display:block;font-size:20px;margin:6px 0}.client-finance-card dl{font-size:11px}.client-statement-section{margin-top:20px}.client-statement-section>header{display:flex;align-items:center;justify-content:space-between}';doc.head.append(style);doc.body.append(source.cloneNode(true));popup.focus();popup.print();
    }
    return {render,newClient(){return activeActions.newClient?.();},newBooking(){return activeActions.newBooking?.();},invalidate(){++revision;activeActions={};}};
})();





