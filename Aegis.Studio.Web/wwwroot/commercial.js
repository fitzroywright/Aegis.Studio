let commercialItems=[], commercialTemplates=[], commercialKind='WorkOrder';
function commercialPage(title,body,tools='',summary='',attention='') {
    conversationView.classList.add('hidden');genericView.classList.remove('hidden');
    const sharedTitle=['Products and Services','Templates'].includes(title);
    const content=sharedTitle&&tools?`<div class="commercial-page-actions">${tools}</div>${body}`:body;
    genericView.innerHTML=StudioPage.html({title,tools,showHeader:title!=='Production'&&!sharedTitle,content,className:'st-commercial'+(title==='Products and Services'?' catalog-page':''),summary:summary||'<p>Manage Studio services, templates and production.</p>',attention});
    StudioPage.bind(genericView);
    window.studioSetBreadcrumb?.([{label:title}]);
}
async function showCommercialCatalog(options={}){
    const current=StudioPage.begin('catalog');
    let active=['products','hours'].includes(options.tab)?options.tab:'booking-sessions',productsRequest,bookingRequest,bookingController,summary,settings,allowed=null;
    commercialPage('Products and Services',`<div class="catalog-heading"><div class="catalog-heading-copy"><p>Manage what you sell, what clients can book, and when each studio is open.</p></div><div class="catalog-heading-actions"><button type="button" class="primary" data-add>＋ Add product</button></div></div><div class="catalog-status-strip" aria-label="Studio opening hours and publication"><span data-catalog-location="BK">Kings Plaza · Loading hours...</span><span data-catalog-location="BE">East Queen Street · Loading hours...</span><span data-catalog-publication>Checking publication settings...</span><button type="button" class="action" data-catalog-hours>View hours</button></div><nav class="catalog-segmented-tabs" aria-label="Products and services sections"><button type="button" data-catalog-tab="products">Products</button><button type="button" data-catalog-tab="booking-sessions">Services &amp; Sessions</button><button type="button" data-catalog-tab="hours">Opening Hours</button></nav><section class="catalog-panel" data-catalog-panel="products" aria-label="Product catalog" hidden></section><section class="catalog-panel" data-catalog-panel="booking-sessions" aria-label="Services and opening hours" hidden></section>`,'','<p>Manage products, services, public booking and location hours.</p>');
    const page=genericView.querySelector('.st-page'),live=()=>current()&&page.isConnected;
    const products=page.querySelector('[data-catalog-panel="products"]'),bookings=page.querySelector('[data-catalog-panel="booking-sessions"]'),add=page.querySelector('[data-add]');
    function paintSummary(){
        if(!live())return;
        const names={BK:'Kings Plaza',BE:'East Queen Street'},days=['Sunday','Monday','Tuesday','Wednesday','Thursday','Friday','Saturday'];
        const day=days.indexOf(new Intl.DateTimeFormat('en-US',{timeZone:'America/Jamaica',weekday:'long'}).format(new Date()));
        for(const code of ['BK','BE']){
            const hours=(summary?.hours||[]).filter(row=>row.locationCode===code&&Number(row.dayOfWeek)===day).sort((a,b)=>a.opensAt.localeCompare(b.opensAt));
            page.querySelector('[data-catalog-location="'+code+'"]').textContent=names[code]+' · '+(summary?.hoursLoaded?'Today: '+(hours.map(row=>row.opensAt.slice(0,5)+'–'+row.closesAt.slice(0,5)).join(', ')||'No opening hours')+(summary.dirtyHours?' (unsaved)':''):'Hours unavailable');
        }
        page.querySelector('[data-catalog-publication]').textContent=settings?(summary?.dirtyHours||summary?.dirtySessions?'Unsaved changes · not published':settings.publishingEnabled?'Automatic publishing enabled':'Automatic publishing disabled'):'Publication status unavailable';
    }
    add.onclick=()=>active==='products'?editCommercialItem():bookingController?.add();
    async function loadProducts(){
        products.innerHTML='<p role="status">Loading catalog...</p>';
        try{
            const [items,templates]=await Promise.all([json('/api/admin/pos/catalog/'),json('/api/admin/commercial/templates/WorkOrder')]);if(!live())return;
            commercialItems=items;commercialTemplates=templates;
            const categories=[...new Set(items.map(item=>item.category).filter(Boolean))].sort();
            products.innerHTML=`<div class="catalog-product-toolbar"><span data-product-count></span></div><div class="catalog-product-workspace"><div class="commercial-grid"><table class="pos-grid"><thead><tr><th><label class="catalog-column-filter"><span>Product</span><input class="commercial-search" type="search" placeholder="Search products…" aria-label="Search products"></label></th><th><label class="catalog-column-filter"><span>Category</span><select data-product-category aria-label="Filter product category"><option value="">All categories</option>${categories.map(name=>`<option value="${esc(name)}">${esc(name)}</option>`).join('')}</select></label></th><th>Price · JMD</th><th><label class="catalog-column-filter"><span>Status</span><select data-product-status aria-label="Filter product status"><option value="">All statuses</option><option value="active">Active</option><option value="inactive">Inactive</option></select></label></th></tr></thead><tbody>${items.map(p=>`<tr data-item="${esc(p.id)}" tabindex="0"><td><strong>${esc(p.name)}</strong><small>${esc(p.code)} · ${esc(p.description||'')}</small></td><td>${esc(p.category)}</td><td>${money(p.unitPrice)}</td><td><span class="catalog-product-status">${p.isActive?'Active':'Inactive'}</span></td></tr>`).join('')}</tbody></table><p class="booking-empty" data-products-empty hidden>No products match your filters.</p></div><section class="catalog-product-editor" data-product-editor aria-label="Selected product details"></section></div><footer class="booking-grid-footer">Select a product to edit pricing, tax, options, package components and production template.</footer>`;
            products.querySelectorAll('[data-item]').forEach(row=>{row.onclick=()=>selectProduct(row.dataset.item);row.onkeydown=e=>{if(e.key==='Enter'){e.preventDefault();row.click();}};});
            const selectProduct=id=>{const item=commercialItems.find(p=>p.id===id);products.querySelectorAll('[data-item]').forEach(row=>row.setAttribute('aria-selected',String(row.dataset.item===id)));const host=products.querySelector('[data-product-editor]');if(item)editCommercialItem(item,host);else host.innerHTML='<p>Select a product to edit.</p>';};
            selectProduct(items[0]?.id);
            const filter=()=>{const term=products.querySelector('.commercial-search').value.toLowerCase(),category=products.querySelector('[data-product-category]').value,status=products.querySelector('[data-product-status]').value;let count=0;products.querySelectorAll('[data-item]').forEach(row=>{const item=items.find(p=>p.id===row.dataset.item);row.hidden=!row.textContent.toLowerCase().includes(term)||!!category&&item.category!==category||!!status&&item.isActive!==(status==='active');if(!row.hidden)count++;});products.querySelector('[data-product-count]').textContent=count+' of '+items.length+' products';products.querySelector('[data-products-empty]').hidden=count>0;if(active==='products')StudioPage.setCount(count+' catalog items');};
            products.querySelector('.commercial-search').oninput=filter;products.querySelector('[data-product-category]').onchange=filter;products.querySelector('[data-product-status]').onchange=filter;filter();
        }catch(error){if(live()){products.innerHTML='<p role="alert">'+esc(error.message)+'</p><button type="button" class="action" data-catalog-retry>Retry</button>';products.querySelector('[data-catalog-retry]').onclick=()=>{productsRequest=loadProducts();};}}
    }
    async function loadBookings(){
        bookings.innerHTML='<p role="status">Checking access...</p>';
        try{
            const me=await json('/api/auth/me');if(!live())return;
            allowed=(me?.user||me)?.isAdministrator===true;
            if(!allowed){bookings.innerHTML='<p>Administrator access required to manage services and opening hours.</p>';paintSummary();if(active!=='products')add.hidden=true;return;}
            bookings.innerHTML=window.StudioBookingSessions.html({embedded:true});
            const settingsRequest=json('/api/admin/public-booking/session-types/settings').then(value=>{settings=value;if(live())paintSummary();}).catch(()=>{settings=null;if(live())paintSummary();});
            bookingController=await window.StudioBookingSessions.mount(bookings,live,{onSummary:value=>{summary=value;paintSummary();},embedded:true});
            await settingsRequest;if(!live())return;
            bookingController?.selectTab(active==='hours'?'hours':'sessions');if(active!=='products'){add.disabled=false;add.hidden=false;}
        }catch(error){if(live()){bookings.innerHTML='<p role="alert">'+esc(error.message)+'</p><button type="button" class="action" data-bookings-retry>Retry</button>';bookings.querySelector('[data-bookings-retry]').onclick=()=>{bookingRequest=loadBookings();};add.disabled=active!=='products';}}
    }
    async function selectTab(tab){
        if(!live()||bookingController?.isSaving())return;
        if(tab!=='products'&&bookingController?.selectTab(tab==='hours'?'hours':'sessions')===false)return;
        active=tab;products.hidden=tab!=='products';bookings.hidden=tab==='products';add.hidden=tab!=='products'&&allowed===false;
        add.disabled=tab!=='products'&&!bookingController;add.textContent=tab==='products'?'＋ Add product':tab==='hours'?'＋ Add opening window':'＋ Add service';
        page.querySelectorAll('[data-catalog-tab]').forEach(button=>button.setAttribute('aria-pressed',String(button.dataset.catalogTab===tab)));
        const state={...window.history.state,studioView:'catalog',studioCatalogTab:tab},params=new URLSearchParams(window.location.hash.slice(1));params.set('studio','catalog');params.set('tab',tab);window.history.replaceState(state,'','#'+params);
        if(tab!=='products'){StudioPage.setCount(tab==='hours'?'Opening Hours':'Services & Sessions');await (bookingRequest ||= loadBookings());if(live()&&active===tab)bookingController?.selectTab(tab==='hours'?'hours':'sessions');}
        else{await (productsRequest ||= loadProducts());if(live()&&active==='products')products.querySelector('.commercial-search')?.dispatchEvent(new Event('input'));}
    }
    page.querySelectorAll('[data-catalog-tab]').forEach(button=>button.onclick=()=>selectTab(button.dataset.catalogTab));
    page.querySelector('[data-catalog-hours]').onclick=()=>selectTab('hours');
    await selectTab(active);if(live()&&active==='products')void (bookingRequest ||= loadBookings());
}
function editCommercialItem(item,host=null){
    const p=item||{code:'',name:'',category:'Photography',unitPrice:0,estimatedCost:0,taxRate:0,isActive:true,options:{},bundle:[]};
    const options=Object.entries(p.options||{}).map(([key,values])=>`${key}: ${values.join(', ')}`).join('\n');
    const markup=`<form class="commercial-form"><label>Item code<input name="code" value="${esc(p.code)}" required></label><label>Name<input name="name" value="${esc(p.name)}" required></label><label>Category<input name="category" value="${esc(p.category)}" list="commercialCategories"><datalist id="commercialCategories">${['Photography','Production','Prints','Frames','Canvas','Albums','Digital','Packages'].map(x=>`<option>${x}</option>`).join('')}</datalist></label><label>Description<textarea name="description">${esc(p.description||'')}</textarea></label><label>Selling price before tax<input name="price" type="number" min="0" step=".01" value="${p.unitPrice}" required></label><label>Estimated production cost<input name="cost" type="number" min="0" step=".01" value="${p.estimatedCost||0}" required></label><label>Tax %<input name="tax" type="number" min="0" max="100" step=".01" value="${p.taxRate||0}"></label><label>Options (one per line, e.g. Size: 8×10, 5×7)<textarea name="options">${esc(options)}</textarea></label><label><input name="active" type="checkbox" ${p.isActive?'checked':''}> Active</label><label><input name="production" type="checkbox" ${p.requiresProduction?'checked':''}> Requires production</label><label>Work Order Template<select name="template"><option value="">None</option>${commercialTemplates.filter(t=>t.active||t.id===p.workOrderTemplateId).map(t=>`<option value="${t.id}" ${t.id===p.workOrderTemplateId?'selected':''}>${esc(t.definition.name)} · v${t.version}</option>`).join('')}</select></label><button type="button" class="fd-card-link" data-template>Manage Work Order Templates →</button><fieldset class="commercial-bundle"><legend>Package components</legend>${commercialItems.filter(x=>x.id!==p.id&&x.isActive&&!(x.bundle||[]).length).map(x=>`<label>${esc(x.name)}<input type="number" min="0" max="10000" data-part="${x.id}" value="${(p.bundle||[]).find(b=>b.catalogItemId===x.id)?.quantity||0}"></label>`).join('')}</fieldset><footer><button class="primary">Save catalog item</button><p role="alert"></p></footer></form>`;
    const d=host||studioDialog(item?'Edit catalog item':'New catalog item',markup);
    if(host)host.innerHTML=`<header class="catalog-product-editor-heading"><strong>${esc(p.name||'New product')}</strong><small>${esc(p.code||'Catalog product')}</small></header>`+markup;
    d.querySelector('[data-template]').onclick=()=>{if(!host)d.close();studioNavigate('work-order-templates');};
    d.querySelector('form').onsubmit=async e=>{e.preventDefault();const f=e.currentTarget;try{
        const opts={};for(const line of f.elements.options.value.split('\n').filter(Boolean)){const [name,...values]=line.split(':');if(!values.length)throw Error('Give each option a name followed by a colon and its choices.');opts[name.trim()]=values.join(':').split(',').map(x=>x.trim()).filter(Boolean);}
        const bundle=[...f.querySelectorAll('[data-part]')].filter(x=>Number(x.value)>0).map(x=>({catalogItemId:x.dataset.part,quantity:Number(x.value)}));
        await json(p.id?`/api/admin/pos/catalog/${p.id}`:'/api/admin/pos/catalog/',{method:p.id?'PUT':'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({code:f.elements.code.value,name:f.elements.name.value,category:f.elements.category.value,description:f.elements.description.value,unitPrice:Number(f.elements.price.value),estimatedCost:Number(f.elements.cost.value),taxRate:Number(f.elements.tax.value),isActive:f.elements.active.checked,requiresProduction:f.elements.production.checked,workOrderTemplateId:f.elements.template.value||null,options:opts,bundle})});if(!host)d.close();await showCommercialCatalog({tab:'products'});
    }catch(error){f.querySelector('[role=alert]').textContent=error.message;}};
}
function commercialTemplatePage(kind,body,tools){
    const tabs=`<nav class="booking-tabs" aria-label="Template types"><button type="button" data-template-kind="Job" aria-pressed="${kind==='Job'}">Job Templates</button><button type="button" data-template-kind="WorkOrder" aria-pressed="${kind==='WorkOrder'}">Work Order Templates</button><button type="button" data-template-kind="Automation" aria-pressed="${kind==='Automation'}">Workflow Automation</button></nav>`;
    const guidance=`<aside class="template-guidance" aria-label="Template guidance">
      <article class="template-postit template-postit-job"><h2>Job Templates</h2><p>Job Templates define <strong>what kind of job this is</strong>.</p><p>A Job Template defines the job type and structure: services, deliverables, Session Plan, stages, tasks, roles and expected workflow.</p><p>Example: Graduation, Wedding or Corporate Headshots.</p></article>
      <article class="template-postit template-postit-work"><h2>Work Order Templates</h2><p>Work Order Templates define <strong>what work must be done</strong>.</p><p>They define the internal production work: ordered steps, dependencies, responsible roles, skills, workstation, costs and quality control.</p><p>A Work Order Template is reusable; a Work Order is the actual work created for a job.</p></article>
      <article class="template-postit template-postit-automation"><h2>Workflow Automation</h2><p>Workflow Automation defines <strong>what happens next</strong>.</p><p>It reacts to events by changing status, creating tasks or work orders, notifying staff, waiting for approvals and enforcing prerequisites.</p><p>It must not silently override ownership, scheduling conflicts, financial approvals or final quality-control decisions.</p></article>
      <div class="template-guidance-flow" aria-label="Template relationship"><strong>Job Template</strong><span>→</span><strong>Workflow Automation</strong><span>→</span><strong>Work Orders</strong></div>
    </aside>`;
    commercialPage('Templates',guidance+tabs+body,tools,'<p>Manage job templates, work order templates and workflow automation.</p>');
    genericView.querySelectorAll('[data-template-kind]').forEach(button=>button.onclick=()=>studioNavigate('templates',{kind:button.dataset.templateKind}));
    const params=new URLSearchParams({studio:'templates',kind});window.history.replaceState({...window.history.state,studioView:'templates',studioTemplateKind:kind},'','#'+params);
}
async function showCommercialTemplates(kind='WorkOrder'){
    if(kind==='Automation'){
        const current=StudioPage.begin('templates');
        commercialTemplatePage(kind,'<div class="commercial-grid" data-template-automation aria-label="Workflow Automation"><p role="status">Checking access…</p></div>','');
        const host=genericView.querySelector('[data-template-automation]'),live=()=>current()&&host.isConnected;
        try{
            const me=await json('/api/auth/me');if(!live())return;
            if((me?.user||me)?.isAdministrator!==true){host.innerHTML='<p>Administrator access required to manage workflow automation.</p>';return;}
            host.innerHTML=renderWorkflowSection();await refreshWorkflowList();if(live())StudioPage.setCount(host.querySelectorAll('#workflowList .job-row').length+' workflows');
        }catch(error){if(live()){host.innerHTML='<p role="alert">'+esc(error.message)+'</p><button type="button" class="action" data-automation-retry>Retry</button>';host.querySelector('[data-automation-retry]').onclick=()=>showCommercialTemplates('Automation');}}
        return;
    }
    if(kind==='Job')return StudioGraphTemplates.list();
    const current=StudioPage.begin('templates');
    commercialKind=kind;[commercialTemplates,commercialItems]=await Promise.all([json(`/api/admin/commercial/templates/${kind}`),json('/api/admin/pos/catalog/')]);
    if(!current())return;
    commercialTemplatePage(kind,`<div class="commercial-grid"><table class="pos-grid"><thead><tr><th>Name</th><th>Version</th><th>Steps</th><th>Status</th><th></th></tr></thead><tbody>${commercialTemplates.map(t=>`<tr tabindex="0" data-template-row="${t.id}"><td><button class="fd-card-link" data-edit="${t.id}">${esc(t.definition.name)}</button><small>${esc(t.definition.description)}</small></td><td>v${t.version}</td><td>${t.definition.steps.length}</td><td><span class="status-pill">${t.active?'Active':'Retired'}</span></td><td><button class="fd-card-link" data-copy="${t.id}">Duplicate</button></td></tr>`).join('')}</tbody></table></div>`,`<button class="primary" data-add>Create ${kind==='Job'?'Job Template':'Work Order Template'}</button>`);
    StudioPage.query(genericView,'[data-add]').onclick=()=>editCommercialTemplate(null,kind);
    StudioPage.setCount(commercialTemplates.length+' templates');
    StudioPage.queryAll(genericView,'[data-edit]').forEach(b=>b.onclick=()=>editCommercialTemplate(commercialTemplates.find(t=>t.id===b.dataset.edit),kind));
    StudioPage.queryAll(genericView,'[data-template-row]').forEach(row=>{const open=()=>editCommercialTemplate(commercialTemplates.find(t=>t.id===row.dataset.templateRow),kind);row.onclick=e=>{if(!e.target.closest('button,input,select,a'))open();};row.onkeydown=e=>{if(e.target===row&&(e.key==='Enter'||e.key===' ')){e.preventDefault();open();}};});
    StudioPage.queryAll(genericView,'[data-copy]').forEach(b=>b.onclick=async()=>{await json(`/api/admin/commercial/templates/${b.dataset.copy}/duplicate`,{method:'POST'});await showCommercialTemplates(kind);});
}
function editCommercialTemplate(current,kind){
    if(kind==='Job')return StudioGraphTemplates.edit(current);
    const t=current?.definition||{name:'',description:'',steps:[],products:[],requiredRoles:[],skills:[],schedulingRequired:false,inHouse:true};
    const d=studioDialog(kind==='Job'?'Job Template':'Work Order Template',`<form class="commercial-form template-form"><label>Name<input name="name" required maxlength="200" value="${esc(t.name)}"></label><label>Description<textarea name="description">${esc(t.description)}</textarea></label><label>Required staff roles<input name="roles" value="${esc((t.requiredRoles||[]).join(', '))}"></label><label>Required skills<input name="skills" value="${esc((t.skills||[]).join(', '))}"></label><label><input name="active" type="checkbox" ${current?.active===false?'':'checked'}> Active (clear to retire)</label><label><input name="schedule" type="checkbox" ${t.schedulingRequired?'checked':''}> Scheduling required</label><label><input name="inhouse" type="checkbox" ${t.inHouse!==false?'checked':''}> Production in-house</label><fieldset class="commercial-steps"><legend>Ordered steps and prerequisites</legend><div data-steps></div><button type="button" class="action" data-add-step>Add step</button></fieldset>${kind==='Job'?`<fieldset class="commercial-bundle"><legend>Default services and deliverables</legend>${commercialItems.filter(p=>p.isActive).map(p=>`<label>${esc(p.name)}<input data-product="${p.id}" type="number" min="0" value="${(t.products||[]).find(x=>x.catalogItemId===p.id)?.quantity||0}"></label>`).join('')}</fieldset>`:''}<footer><button class="primary">Save new version</button><p role="alert"></p></footer></form>`);
    let next=t.steps.length;
    const add=step=>{const key=step?.key||`step-${++next}`;const row=document.createElement('section');row.className='commercial-step';row.dataset.key=key;row.innerHTML=`<label>Step title<input data-field="title" required value="${esc(step?.title||'')}"></label><label>Area<select data-field="area">${['Editing','Printing','Framing','Album Production','Assembly','Quality Control'].map(a=>`<option ${a===step?.area?'selected':''}>${a}</option>`).join('')}</select></label><label>Task type<select data-field="type">${['Other','Edit','Retouch','Proof','Print','QC','Delivery','Shoot','Archive','FileIngest','Backup'].map(a=>`<option ${a===step?.activityType?'selected':''}>${a}</option>`).join('')}</select></label><label>Minutes<input data-field="minutes" type="number" min="0" value="${step?.estimatedMinutes||0}"></label><label>Skills<input data-field="skills" value="${esc((step?.skills||[]).join(', '))}"></label><label>Materials<input data-field="materials" value="${esc(step?.materials||'')}"></label><label>Estimated cost<input data-field="cost" type="number" min="0" step=".01" value="${step?.estimatedCost||0}"></label><label>Default employee<input data-field="assignee" value="${esc(step?.defaultAssignee||'')}"></label><label>Variation (matches specifications)<input placeholder="Frame: wood" data-field="variation" value="${esc(step?.variation||'')}"></label><label><input data-field="qc" type="checkbox" ${step?.qualityControl?'checked':''}> Quality control</label><label><input data-field="optional" type="checkbox" ${step?.optional?'checked':''}> Optional</label><label><input data-field="image" type="checkbox" ${step?.requiresApprovedImage?'checked':''}> Requires approved image</label><fieldset><legend>Prerequisites</legend>${[...d.querySelectorAll('.commercial-step')].map(prior=>`<label><input data-dependency="${esc(prior.dataset.key)}" type="checkbox" ${(step?.dependsOn||[]).includes(prior.dataset.key)?'checked':''}>${esc(prior.querySelector('[data-field=title]').value||prior.dataset.key)}</label>`).join('')}</fieldset><button type="button" class="fd-card-link" data-remove>Remove step</button>`;row.querySelector('[data-remove]').onclick=()=>row.remove();d.querySelector('[data-steps]').append(row);};
    t.steps.forEach(add);d.querySelector('[data-add-step]').onclick=()=>add();
    d.querySelector('form').onsubmit=async e=>{e.preventDefault();const f=e.currentTarget;try{
        const list=value=>value.split(',').map(x=>x.trim()).filter(Boolean);
        const steps=[...f.querySelectorAll('.commercial-step')].map(row=>{const field=name=>row.querySelector(`[data-field="${name}"]`);return {key:row.dataset.key,title:field('title').value,activityType:field('type').value,area:field('area').value,dependsOn:[...row.querySelectorAll('[data-dependency]:checked')].map(x=>x.dataset.dependency),skills:list(field('skills').value),estimatedMinutes:Number(field('minutes').value),estimatedCost:Number(field('cost').value),materials:field('materials').value,defaultAssignee:field('assignee').value||null,variation:field('variation').value||null,qualityControl:field('qc').checked,optional:field('optional').checked,requiresApprovedImage:field('image').checked};});
        const definition={name:f.elements.name.value,description:f.elements.description.value,requiredRoles:list(f.elements.roles.value),skills:list(f.elements.skills.value),schedulingRequired:f.elements.schedule.checked,inHouse:f.elements.inhouse.checked,steps,products:[...f.querySelectorAll('[data-product]')].filter(p=>Number(p.value)>0).map(p=>({catalogItemId:p.dataset.product,quantity:Number(p.value)}))};
        await json(`/api/admin/commercial/templates/${kind}${current?'/'+current.id:''}`,{method:current?'PUT':'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({definition,expectedVersion:current?.version||0,active:f.elements.active.checked})});d.close();await showCommercialTemplates(kind);
    }catch(error){f.querySelector('[role=alert]').textContent=error.message;}};
}
let productionBoardController=null;
let productionBoardSnapshot=null;
let productionBoardRefreshTimer=null;
const productionBoardRefreshMs=60000;
async function showCatalogProduction({preserveFilters=false,workspaceId}={}){
    const previous=window.studioWorkloadSelection||{};
    // Only visible job search and workspace selection scope the board.
    window.studioWorkloadSelection={
        workspace:workspaceId!==undefined?String(workspaceId||''):
            preserveFilters||window.studioProductionScopeRequested?String(previous.workspace||''):'',
        search:preserveFilters?String(previous.search||''):''
    };
    window.studioProductionScopeRequested=false;
    clearTimeout(window.productionBoardSearchTimer);
    productionBoardController?.abort();clearInterval(productionBoardRefreshTimer);productionBoardController=new AbortController();const signal=productionBoardController.signal;
    const current=StudioPage.begin('production');
    conversationView.classList.add('hidden');genericView.classList.remove('hidden');
    const showLoadError=error=>{
        if(!current())return;
        genericView.innerHTML='<p role="alert">Production could not load: '+esc(error.message)+'</p><button class="action" data-production-retry>Retry</button>';
        genericView.querySelector('button').onclick=()=>showCatalogProduction({preserveFilters:true});
    };
    const cached=productionBoardSnapshot;
    if(!cached)genericView.innerHTML='<div class="today-loading">Loading Production…</div>';
    let workloadData=cached?.workload,workspaces=cached?.workspaces;
    if(!cached){
        try{[workloadData,workspaces]=await Promise.all([json('/api/production/workload',{signal}),loadWorkspaces()]);}
        catch(error){showLoadError(error);return;}
        if(current())productionBoardSnapshot={workload:workloadData,workspaces};
    }
    if(!current())return;
    const laneLimits=new Map();
    const jobPhotos=new Map();
    const imagePlaceholder=()=>'<span class="production-image-placeholder"><span aria-hidden="true">▧</span>No image ready yet</span>';
    const paintPhotos=()=>{if(!current())return;genericView.querySelectorAll('[data-production-photo]').forEach(slot=>{const version=jobPhotos.get(slot.dataset.productionPhoto);if(!version)return;const image=document.createElement('img');image.alt='';image.loading='lazy';image.decoding='async';image.src='/api/jobs/'+encodeURIComponent(slot.dataset.productionPhoto)+'/versions/by-id/'+encodeURIComponent(version)+'/preview?size=thumbnail';image.onerror=()=>{jobPhotos.delete(slot.dataset.productionPhoto);slot.innerHTML=imagePlaceholder();};slot.replaceChildren(image);});};
    const workload=StudioWorkload.model(workloadData,workloadData.jobs||[]);
    const tasksByJob=new Map(),ordersByJob=new Map();
    for(const task of workload.activities){if(!tasksByJob.has(task.jobId))tasksByJob.set(task.jobId,[]);tasksByJob.get(task.jobId).push(task);}
    for(const order of workloadData.orders||[]){if(!ordersByJob.has(order.jobId))ordersByJob.set(order.jobId,[]);ordersByJob.get(order.jobId).push(order);}
    for(const tasks of tasksByJob.values())tasks.sort((a,b)=>(a.stepOrder||0)-(b.stepOrder||0));
    function paint(){
    if(!current())return;
    window.StudioShell?.setProductionWorkspaceFilter(workspaces,window.studioWorkloadSelection?.workspace,workspace=>{window.studioWorkloadSelection||={};window.studioWorkloadSelection.workspace=workspace;laneLimits.clear();paint();});
    const selection=window.studioWorkloadSelection||={workspace:'',search:''};
    const term=(selection.search||'').trim().toLowerCase();
    const laneNames=[['ready','Ready to Start'],['progress','In Production'],['qc','Quality Control'],['attention','Rework'],['complete','Complete']];
    const jobs=(workloadData.jobs||[]).filter(job=>{
        const tasks=tasksByJob.get(job.id)||[];
        if(selection.workspace&&job.workspaceId!==selection.workspace&&!(job.participatingWorkspaceIds||[]).includes(selection.workspace))return false;
        const orders=ordersByJob.get(job.id)||[];
        return !term||[job.title,job.clientName,job.status,job.workspaceName,...tasks.map(a=>[a.title,a.activityType,a.productionArea,a.assignedTo].join(' ')),...orders.map(o=>[o.id,o.product].join(' '))].join(' ').toLowerCase().includes(term);
    });
    const jobsByLane=new Map(laneNames.map(([key])=>[key,[]]));
    for(const job of jobs)jobsByLane.get(workload.jobLane(job,tasksByJob.get(job.id)||[]))?.push(job);
    const dueLabel=task=>task?.dueAtUtc?new Date(task.dueAtUtc).toLocaleDateString(undefined,{month:'short',day:'numeric'}):'No due date';
    const jobRow=(job)=>{
        const tasks=(tasksByJob.get(job.id)||[]).filter(a=>a.eligibility!=='Inactive');
        const finished=a=>['Completed','Cancelled','Archived','Skipped'].includes(a.status);
        const task=tasks.find(a=>!finished(a)&&['InProgress','Partial'].includes(a.status))||tasks.find(a=>!finished(a)&&!workload.waiting(a))||tasks.find(a=>!finished(a));
        const next=task?.title||task?.activityType||'No next task recorded';
        return '<article class="production-job-card" tabindex="0" data-production-job="'+esc(job.id)+'"><div class="production-job-image" data-production-photo="'+esc(job.id)+'">'+imagePlaceholder()+'</div><div class="production-job-card-body"><h3>'+esc(job.title||'Untitled job')+'</h3><small>'+esc(job.clientName||'')+'</small><p>'+esc(next)+'</p><span class="production-job-stage">'+esc(job.status||'Stage not recorded')+'</span><small>'+esc(task?.assignedTo?studioFirstName(task.assignedTo):'Unassigned')+' · '+esc(dueLabel(task))+'</small></div></article>';
    };
    const lanes=laneNames.map(([key,label])=>{
        const all=jobsByLane.get(key),limit=laneLimits.get(key)||20;
        return '<section class="production-lane production-lane-'+key+'" data-production-lane="'+key+'" aria-label="'+esc(label)+'"><header><h2>'+esc(label)+'</h2><span>'+all.length+'</span></header><div class="production-lane-list">'+(all.slice(0,limit).map(jobRow).join('')||'<p class="production-lane-empty">No jobs in this stage</p>')+(all.length>limit?'<button type="button" class="action" data-production-more="'+key+'">Show more · '+(all.length-limit)+' remaining</button>':'')+'</div></section>';
    }).join('');
    const totalItems=jobs.length,shownItems=laneNames.reduce((sum,[key])=>sum+Math.min(laneLimits.get(key)||20,jobsByLane.get(key).length),0);
    const summary=laneNames.map(([key,label])=>'<div class="workspace-summary-row"><span>'+esc(label)+'</span><strong>'+jobsByLane.get(key).length+'</strong></div>').join('');
    const filters='<div class="production-board-filters"><input type="search" data-production-search value="'+esc(selection.search||'')+'" placeholder="Search jobs, clients or work orders" aria-label="Search jobs, clients or work orders"><button type="button" data-production-clear-search>Clear search</button></div>';
    if(!current())return;
    commercialPage('Production','<div class="production-board-view">'+filters+'<div class="production-kanban" aria-label="Production jobs by current stage">'+lanes+'</div><footer class="commercial-pager production-board-pager"><span>Showing '+shownItems+' of '+totalItems+' jobs · Each column shows its full count</span><button type="button" class="action" data-production-refresh>Refresh</button><p role="alert"></p></footer></div>','',summary,'');
    StudioPage.query(genericView,'.st-board')?.classList.add('production-main-board');
    const reload=()=>{laneLimits.clear();paint();};
    StudioPage.query(genericView,'[data-production-clear-search]').onclick=()=>{selection.search='';reload();};
    StudioPage.query(genericView,'[data-production-search]').oninput=e=>{selection.search=e.target.value;clearTimeout(window.productionBoardSearchTimer);window.productionBoardSearchTimer=setTimeout(()=>{if(!current())return;const start=e.target.selectionStart;reload();const input=genericView.querySelector('[data-production-search]');input.focus();input.setSelectionRange(start,start);},180);};
    StudioPage.query(genericView,'[data-production-refresh]').onclick=()=>{productionBoardSnapshot=null;void showCatalogProduction({preserveFilters:true});};
    StudioPage.queryAll(genericView,'[data-production-more]').forEach(button=>button.onclick=()=>{const scroll=new Map([...genericView.querySelectorAll('[data-production-lane]')].map(lane=>[lane.dataset.productionLane,lane.querySelector('.production-lane-list').scrollTop]));laneLimits.set(button.dataset.productionMore,(laneLimits.get(button.dataset.productionMore)||20)+20);paint();genericView.querySelectorAll('[data-production-lane]').forEach(lane=>{lane.querySelector('.production-lane-list').scrollTop=scroll.get(lane.dataset.productionLane)||0;});});
    const openJob=id=>window.studioNavigate('workspaces',{jobId:id});
    StudioPage.queryAll(genericView,'[data-production-job]').forEach(card=>{card.onclick=e=>{if(!e.target.closest('button,input,select,a,details'))openJob(card.dataset.productionJob);};card.onkeydown=e=>{if(e.target===card&&['Enter',' '].includes(e.key)){e.preventDefault();openJob(card.dataset.productionJob);}};});
    paintPhotos();
    const count=document.getElementById('studioFooterRate');if(count)count.textContent=totalItems+' jobs';
    }
    paint();
    // Cached board paints immediately; refresh in the background without blocking navigation.
    // A changed snapshot is rendered through this same authoritative board function.
    let refreshing=false;
    const refresh=async()=>{
        if(!current()||document.hidden||refreshing)return;
        refreshing=true;
        try{
            const [nextWorkload,nextWorkspaces]=await Promise.all([json('/api/production/workload',{signal}),loadWorkspaces()]);
            if(!current()||signal.aborted)return;
            if(!nextWorkload||!Array.isArray(nextWorkload.jobs))throw Error('Invalid production workload');
            const changed=JSON.stringify(nextWorkload)!==JSON.stringify(productionBoardSnapshot?.workload)||
                JSON.stringify(nextWorkspaces)!==JSON.stringify(productionBoardSnapshot?.workspaces);
            productionBoardSnapshot={workload:nextWorkload,workspaces:nextWorkspaces};
            if(changed)void showCatalogProduction({preserveFilters:true});
        }catch(error){
            if(!current()||signal.aborted)return;
            if(error.status===401||error.status===403){
                productionBoardSnapshot=null;jobPhotos.clear();
                clearInterval(productionBoardRefreshTimer);productionBoardController.abort();
                showLoadError(error);
            }else console.warn('Production refresh failed; retaining last workload.',error);
        }
        finally{refreshing=false;}
    };
    productionBoardRefreshTimer=setInterval(()=>{void refresh();},productionBoardRefreshMs);
    productionBoardRefreshTimer.unref?.();
    if(cached)void refresh();
    void json('/api/production/media-summary').then(rows=>{if(!current())return;for(const row of rows){if(!jobPhotos.has(row.jobId)&&row.thumbnailVersionIds?.length)jobPhotos.set(row.jobId,row.thumbnailVersionIds[Math.floor(Math.random()*row.thumbnailVersionIds.length)]);}paintPhotos();}).catch(()=>{});

}
async function openCommercialWorkOrder(order){
    const authority=await json(`/api/production/work-orders/${order.id}/access`);
    const d=studioDialog(`Work order · ${order.product} × ${order.quantity}`,`<div class="commercial-order"><p>${esc(order.jobTitle)} · ${esc(order.specifications||'')} · Template v${order.templateVersion}</p><button class="action" data-amend>Amend specifications</button>${order.sourceVersionId?'<p>Approved image version pinned.</p>':'<button class="action" data-source>Choose approved Job Gallery image</button>'}${order.exceptionReason?`<p class="commercial-blocker">${esc(order.exceptionReason)}</p><label>Reconciliation note<input data-note></label><button class="action" data-resolve>Resolve exception</button>`:''}<table class="pos-grid"><thead><tr><th>Task</th><th>Area / requirements</th><th>Employee</th><th>Due</th><th>Status</th><th></th></tr></thead><tbody>${order.tasks.map(t=>`<tr data-task="${t.id}"><td>${esc(t.title)}${t.qualityControl?' · QC':''}${t.optional?' · Optional':''}</td><td>${esc(t.area)}<small>${esc((t.step.skills||[]).join(', '))} · ${t.step.estimatedMinutes} min · ${money(t.step.estimatedCost)}</small><small>${esc(t.step.materials||'')}</small></td><td><input data-employee ${authority.canReview?'':'disabled'} value="${esc(t.assignedTo||'')}" aria-label="Assigned employee"></td><td><input data-due ${authority.canReview?'':'disabled'} type="datetime-local" value="${t.dueAtUtc?new Date(new Date(t.dueAtUtc)-new Date(t.dueAtUtc).getTimezoneOffset()*60000).toISOString().slice(0,16):''}" aria-label="Due date"></td><td><select data-status ${StudioWorkflow.canReportTask(authority,{...t,activityType:t.step.activityType})?'':'disabled'}>${['NotStarted','InProgress','Blocked','Completed',...(t.optional?['Cancelled']:[])].filter(s=>s!=='Completed'||!t.qualityControl||authority.canReview).map(s=>`<option ${t.status===s?'selected':''}>${s}</option>`).join('')}</select><small class="commercial-blocker">${esc(t.blockedReason||'')}</small><input data-block placeholder="Blocker / QC note" aria-label="Blocker or QC note"></td><td><button class="action" data-save ${StudioWorkflow.canReportTask(authority,{...t,activityType:t.step.activityType})?'':'disabled'}>Save</button></td></tr>`).join('')}</tbody></table><p role="alert"></p></div>`);
    if(!authority.canReview){d.querySelector('[data-source]')?.remove();d.querySelector('[data-resolve]')?.remove();}
    if(!authority.canManage&&!authority.canReview)d.querySelector('[data-amend]')?.remove();
    const error=err=>d.querySelector('[role=alert]').textContent=err.message;
    d.querySelectorAll('[data-task]').forEach(row=>row.querySelector('[data-save]').onclick=async()=>{try{const task=order.tasks.find(t=>t.id===row.dataset.task);const assigned=row.querySelector('[data-employee]').value||null,due=row.querySelector('[data-due]').value?new Date(row.querySelector('[data-due]').value).toISOString():null;
        if(authority.canReview&&(assigned!==task.assignedTo||due!==task.dueAtUtc)){await StudioWorkflow.command(order.jobId,{action:'task',taskId:task.id,title:task.title,userName:assigned,kind:task.step.activityType,location:task.area,dueAtUtc:due,priority:task.priority},row._dispatchHolder??={},authority.revision);task.assignedTo=assigned;task.dueAtUtc=due;}
        await StudioWorkflow.command(order.jobId,{action:'task-progress',taskId:task.id,status:row.querySelector('[data-status]').value,blockedReason:row.querySelector('[data-block]').value},row.querySelector('[data-save]'),authority.revision);d.close();await showCatalogProduction({preserveFilters:true});
    }catch(err){error(err);}});
    d.querySelector('[data-amend]')?.addEventListener('click',()=>{const edit=studioDialog('Amend work order',`<form class="commercial-form"><label>Specifications<textarea name="specifications">${esc(order.specifications||'')}</textarea></label><label>Reason<input name="reason" required></label><p>Existing production history is retained. A manager must reconcile the changed order before work continues.</p><button class="primary">Save amendment</button><p role="alert"></p></form>`);edit.querySelector('form').onsubmit=async e=>{e.preventDefault();try{await json(`/api/production/work-orders/${order.id}/specifications`,{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify({specifications:e.target.elements.specifications.value,reason:e.target.elements.reason.value,expectedSpecifications:order.specifications})});edit.close();d.close();await showCatalogProduction({preserveFilters:true});}catch(err){edit.querySelector('[role=alert]').textContent=err.message;}};});
    d.querySelector('[data-source]')?.addEventListener('click',()=>{
        const chooser=studioDialog('Approved Job Gallery images','<input data-search placeholder="Search approved image filename"><div data-images></div><button class="action" data-more>Next page</button><p role="alert"></p>');let after=null,timer;
        const load=async()=>{try{const assets=await json(`/api/jobs/${order.jobId}/commercial/approved-sources?term=${encodeURIComponent(chooser.querySelector('[data-search]').value)}${after?'&after='+after:''}`);chooser.querySelector('[data-images]').innerHTML=assets.map(a=>`<button class="action" data-image="${a.id}">${esc(a.fileName||a.assetKey)}</button>`).join('')||'<p>No approved images match. Approve images in Job Gallery first.</p>';chooser.querySelector('[data-more]').disabled=assets.length<100;after=assets.at(-1)?.id;chooser.querySelectorAll('[data-image]').forEach(b=>b.onclick=async()=>{try{await json(`/api/production/work-orders/${order.id}/source`,{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify({assetId:b.dataset.image})});chooser.close();d.close();await showCatalogProduction({preserveFilters:true});}catch(err){chooser.querySelector('[role=alert]').textContent=err.message;}});}catch(err){chooser.querySelector('[role=alert]').textContent=err.message;}};
        chooser.querySelector('[data-search]').oninput=()=>{clearTimeout(timer);timer=setTimeout(()=>{after=null;load();},250);};chooser.querySelector('[data-more]').onclick=load;load();
    });
    d.querySelector('[data-resolve]')?.addEventListener('click',async()=>{try{await json(`/api/production/work-orders/${order.id}/resolve`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({reason:d.querySelector('[data-note]').value})});d.close();await showCatalogProduction({preserveFilters:true});}catch(err){error(err);}});
}

