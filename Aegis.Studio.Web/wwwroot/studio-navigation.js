(() => {
    const conversationView=document.getElementById('conversationView');
    const genericView=document.getElementById('genericView');
    const studioBreadcrumb=document.getElementById('studioBreadcrumb');
    const studioPageTools=document.getElementById('studioPageTools');

    function setActive(view){
        if(view!=='workspaces'){
            window.studioShellMountJobAttention?.(null);
        }
        document.body.dataset.studioView=view;
        if(view!=='dashboard' && view!=='jobs'){const count=document.getElementById('studioFooterRate');if(count)count.textContent='';}
        document.querySelectorAll('[data-view]').forEach(button=>{
            button.classList.toggle('active',button.dataset.view===view);
        });
    }

    let navigationRevision=0;
    let jobsWorkspaceFilter=null;

        const formatJobDate=value=>{
            if(!value)return '—';
            const raw=String(value);
            const dateOnly=/^(\d{4})-(\d{2})-(\d{2})(?:$|T00:00:00(?:\.0+)?(?:Z)?)?/.exec(raw);
            const date=dateOnly
                ?new Date(Number(dateOnly[1]),Number(dateOnly[2])-1,Number(dateOnly[3]))
                :new Date(raw);
            return Number.isNaN(date.getTime())?raw:date.toLocaleDateString('en-US',{weekday:'short',month:'short',day:'2-digit',year:'numeric'});
        };


    async function renderWorkspaces(){
        conversationView.classList.add('hidden');
        genericView.classList.remove('hidden');
        genericView.innerHTML='<div class="today-loading">Loading workspaces…</div>';

        const [workspaces,jobs,mediaSummary,allTasks,schedule]=await Promise.all([
            loadWorkspaces().then(()=>workspaceCache),
            json('/api/jobs'),
            json('/api/production/media-summary'),
            json('/api/operations/activities').catch(()=>[]),
            json('/api/schedule/entries?from='+encodeURIComponent(new Date().toISOString())+'&to='+encodeURIComponent(new Date(Date.now()+365*24*60*60*1000).toISOString())).catch(()=>[])
        ]);
        const workspaceData=[];
        // Announcement reads are bounded; galleries are represented by counts and three IDs.
        for(const workspace of workspaces){
            const workspaceJobs=jobs.filter(job=>String(job.workspaceId)===String(workspace.id)||(job.participatingWorkspaceIds||[]).includes(workspace.id));
            const media=workspaceJobs.map(job=>{const rows=mediaSummary.filter(x=>x.jobId===job.id);return {job,snapshot:{totalAssets:rows.reduce((n,x)=>n+Number(x.assetCount),0),assets:[...new Set(rows.flatMap(x=>x.thumbnailVersionIds||[]))].map(versionId=>({versionId,contentType:'image/jpeg'}))}};});
            const announcements=await json('/api/workspaces/'+workspace.id+'/announcements').catch(()=>[]);
            workspaceData.push({workspace,jobs:workspaceJobs,media,announcements});
        }
        const photoCount=item=>item.media.reduce((sum,x)=>sum+(x.snapshot.totalAssets||0),0);
        const latestJob=item=>[...item.jobs].sort((a,b)=>new Date(b.eventDate||b.createdAtUtc||b.createdAt||0)-new Date(a.eventDate||a.createdAtUtc||a.createdAt||0))[0];
        const thumbFor=(job,snapshot)=>{
            const asset=(snapshot.assets||[]).find(a=>String(a.contentType||'').startsWith('image/'))||(snapshot.assets||[])[0];
            return asset?.versionId?'/api/jobs/'+job.id+'/versions/by-id/'+asset.versionId+'/preview?size=thumbnail':'';
        };
        const heroFor=item=>{
            for(const pair of item.media){
                const url=thumbFor(pair.job,pair.snapshot);
                if(url)return url;
            }
            return '';
        };
        const previewsFor=item=>{
            const values=[];
            for(const pair of item.media){
                for(const asset of (pair.snapshot.assets||[])){
                    if(!asset.versionId)continue;
                    values.push('/api/jobs/'+pair.job.id+'/versions/by-id/'+asset.versionId+'/preview?size=thumbnail');
                    if(values.length===3)return values;
                }
            }
            return values;
        };

        const completedStatus=status=>String(status||'').toLowerCase()==='completed';
        const productionStatuses=new Set(['needs review','in production','production','culling','editing','retouching','qc','printing','ready for client','ready for delivery']);
        const jobIds=item=>new Set(item.jobs.map(job=>String(job.id)));
        const metricsFor=item=>{
            const ids=jobIds(item);
            const tasks=(allTasks||[]).filter(task=>ids.has(String(task.jobId)));
            const shoots=(schedule||[]).filter(entry=>entry.jobId&&ids.has(String(entry.jobId))&&String(entry.status||'').toLowerCase()!=='cancelled');
            return {
                activeJobs:item.jobs.filter(job=>!completedStatus(job.status)).length,
                completedJobs:item.jobs.filter(job=>completedStatus(job.status)).length,
                upcomingShoots:shoots.filter(entry=>new Date(entry.startsAtUtc||entry.start||entry.startsAt)>new Date()).length,
                openTasks:tasks.filter(task=>!['completed','cancelled'].includes(String(task.status||'').toLowerCase())).length,
                overdueTasks:tasks.filter(task=>!['completed','cancelled'].includes(String(task.status||'').toLowerCase())&&task.dueAtUtc&&new Date(task.dueAtUtc)<new Date()).length,
                awaitingProduction:item.jobs.filter(job=>productionStatuses.has(String(job.status||'').toLowerCase())).length
            };
        };
        workspaceData.forEach(item=>item.metrics=metricsFor(item));
        const totals=workspaceData.reduce((sum,item)=>{
            Object.keys(sum).forEach(key=>sum[key]+=item.metrics[key]);
            return sum;
        },{activeJobs:0,completedJobs:0,upcomingShoots:0,openTasks:0,overdueTasks:0,awaitingProduction:0});
        const announcements=workspaceData
            .flatMap(item=>(item.announcements||[]).map(a=>({...a,workspaceName:item.workspace.name})))
            .sort((a,b)=>new Date(b.createdAtUtc||0)-new Date(a.createdAtUtc||0))
            .slice(0,4);

        const renderRows=items=>items.map(item=>{
            const current=item.jobs.filter(job=>!completedStatus(job.status));
            const latest=latestJob(item);
            const photos=photoCount(item);
            const hero=heroFor(item);
            const previews=previewsFor(item);
            const status=current.length?'Active':item.jobs.length?'No active jobs':'Ready';
            const statusClass=current.length?'active':'ready';
            const unassigned=String(item.workspace.name||'').trim().toLowerCase()==='unassigned'||String(item.workspace.systemKey||'').trim().toLowerCase()==='unassigned';
            const email=String(item.workspace.emailAddress||'').trim();
            const subtitle=latest?.clientName||item.workspace.emailAddress||item.workspace.routeKey||'Studio workspace';
            const secondary=latest?.title||latest?.jobType||'No recent job';
            const lastActivity=latest?.eventDate||latest?.createdAtUtc||latest?.createdAt;
            return `<article class="workspace-wow-row" data-workspace-row data-status="${item.metrics.activeJobs?'active':'inactive'}" data-search="${esc((item.workspace.name+' '+(item.workspace.emailAddress||'')+' '+subtitle+' '+secondary+' '+status).toLowerCase())}">
                <button type="button" class="workspace-wow-main" data-open-workspace="${item.workspace.id}" data-workspace-name="${esc(item.workspace.name)}">
                    <span class="workspace-wow-thumb">${hero?`<img src="${hero}" alt="">`:'<span class="workspace-photo-fallback">📷</span>'}</span>
                    <span class="workspace-wow-copy">
                        <strong>${esc(item.workspace.name)}</strong>
                        <small>${esc(subtitle)} · ${esc(secondary)}</small>
                    </span>
                    <span class="workspace-wow-email ${!email&&!unassigned?'workspace-email-warning':''}" title="${esc(email||(!unassigned?'Workspace email address must be configured':'System workspace: email not required'))}">${email?esc(email):unassigned?'—':'<span aria-label="Warning: email address not configured">⚠ Set email</span>'}</span>
                    <span class="workspace-wow-stat"><b>${item.metrics.activeJobs}</b></span>
                    <span class="workspace-wow-stat"><b>${item.metrics.completedJobs}</b></span>
                    <span class="workspace-wow-stat"><b>${item.metrics.upcomingShoots}</b></span>
                    <span class="workspace-wow-activity">
                        <em class="workspace-reference-pill ${statusClass}">${status}</em>
                        <small>${lastActivity?'Updated '+esc(new Date(lastActivity).toLocaleDateString([],{month:'short',day:'numeric'})):'No activity yet'}</small>
                    </span>
                    <span class="workspace-wow-previews">
                        ${previews.map(url=>`<img src="${url}" alt="">`).join('')}
                        ${photos>previews.length?`<i>+${photos-previews.length}</i>`:''}
                    </span>
                    <span class="workspace-wow-arrow">›</span>
                </button>
            </article>`;
        }).join('');

        genericView.innerHTML=`
            <section class="workspace-list-page mw-list-page">
                <div class="mw-list-layout workspace-list-layout">
                    <section class="mw-list-panel workspace-center-panel">
                        <div class="workspace-grid-scroll" tabindex="0" role="region" aria-label="Workspace grid">
                        <div class="workspace-grid-table">
                        <div class="workspace-column-header" role="row" aria-label="Workspace columns">
                            <span class="workspace-column-thumbnail" aria-hidden="true"></span>
                            <label class="workspace-column-name"><span>Workspace</span><input type="search" placeholder="Search workspaces…" aria-label="Search workspaces" data-workspace-search></label>
                            <span>Email Address</span>
                            <span>Active<br> Jobs</span>
                            <span>Completed<br> Jobs</span>
                            <span>Upcoming<br> Shoots</span>
                            <span>Status / Activity</span>
                            <span title="Photo previews from workspace jobs">Media</span>
                            <span aria-hidden="true"></span>
                        </div>

                        <div class="workspace-list-body" data-workspace-list>${renderRows(workspaceData)}</div>
                        </div>
                        </div>
                    </section>


                </div>
            </section>`;

        window.studioShellSetOverview?.(`<section class="mw-list-panel studio-context-card studio-overview-rail" data-rail-card="overview"><header><h2>Overview</h2><small>Studio totals</small></header><div class="mw-status-list">${[['▥','Active Jobs','activeJobs'],['✓','Completed Jobs','completedJobs'],['▣','Upcoming Shoots','upcomingShoots'],['☑','Open Tasks','openTasks'],['◷','Overdue Tasks','overdueTasks'],['◆','Jobs Awaiting Production','awaitingProduction']].map(([icon,label,key])=>`<div class="workspace-summary-row"><span>${icon}</span><span>${label}</span><strong>${Number(totals[key]||0)}</strong></div>`).join('')}</div></section>`,'workspaces');
        window.studioSetBreadcrumb?.([{label:'Workspaces',view:'workspaces'}]);
        if(studioPageTools) studioPageTools.innerHTML='';


        const allRows=()=>[...genericView.querySelectorAll('[data-workspace-row]')];
        let term='';

        const refresh=()=>{
            const list=genericView.querySelector('[data-workspace-list]');
            const rows=allRows();
            rows.forEach(row=>{
                const matchesTerm=!term||row.dataset.search.includes(term);
                row.hidden=!matchesTerm;
            });


        };

        const search=genericView.querySelector('[data-workspace-search]');
        const updateTerm=value=>{
            term=String(value||'').trim().toLowerCase();
            if(search&&search.value!==value)search.value=value;
            refresh();
        };
        if(search)search.oninput=e=>updateTerm(e.target.value);

        genericView.querySelectorAll('[data-open-workspace]').forEach(button=>{
            button.onclick=async()=>{
                await selectWorkspace(button.dataset.openWorkspace,button.dataset.workspaceName);
            };
        });
        genericView.querySelectorAll('[data-nav-quick]').forEach(button=>{
            button.onclick=()=>window.studioNavigate(button.dataset.navQuick);
        });
        genericView.querySelectorAll('[data-workspace-jobs]').forEach(button=>{
            button.onclick=()=>{ jobsWorkspaceFilter=''; window.studioNavigate('jobs'); };
        });
    }

    async function renderJobs(){
        const revision=navigationRevision;
        const isCurrent=()=>revision===navigationRevision&&document.body.dataset.studioView==='jobs';
        conversationView.classList.add('hidden');
        genericView.classList.remove('hidden');
        genericView.innerHTML='<div class="today-loading">Loading jobs…</div>';
        if(typeof loadWorkspaces==='function'&&(!Array.isArray(workspaceCache)||!workspaceCache.length))await loadWorkspaces();
        if(!isCurrent())return;
        return StudioJobsPage.render(genericView,{
            json,workspaces:workspaceCache||[],dashboardQueue:window.history.state?.studioDashboardQueue,
            workspaceFilter:jobsWorkspaceFilter===null?(typeof selectedWorkspace!=='undefined'?selectedWorkspace||'':''):jobsWorkspaceFilter,
            setWorkspaceFilter:value=>{jobsWorkspaceFilter=value;recordNavigation('jobs',{workspaceId:value,replaceHistory:true});},
            isCurrent,openJob:id=>openWorkspaceJob(id),navigate:view=>window.studioNavigate(view),
            sendMessage:async()=>{await window.studioNavigate('messages');if(document.body.dataset.studioView==='messages')document.getElementById('newDirectMessage')?.click();},
            openWorkspace:id=>selectWorkspace(id,(workspaceCache||[]).find(w=>String(w.id)===String(id))?.name||'Workspace'),
            openWorkspaceSettings:id=>window.studioNavigate('workspace-management'),
            retry:()=>renderJobs()
        });
    }

    window.studioOpenJobs=workspaceId=>{
        jobsWorkspaceFilter=workspaceId==null?'':String(workspaceId);
        return window.studioNavigate('jobs');
    };

    async function renderAdmin(view='about'){
        const title=window.StudioAdmin?.entries.find(entry=>entry.view===view)?.label||'About';
        const current=StudioPage.begin(view);
        conversationView.classList.add('hidden');
        genericView.classList.remove('hidden');
        genericView.innerHTML='<div class="today-loading">Loading administration…</div>';

        const me=await json('/api/auth/me').catch(()=>null);
        if(!current())return;
        const isAdministrator=me?.user?.isAdministrator===true||me?.isAdministrator===true;
        if(!isAdministrator){
            genericView.innerHTML=StudioPage.html({title,content:'<section class="admin-card"><h2>Administrator access required</h2><p>Your account does not have permission to administer Studio.</p></section>'});StudioPage.bind(genericView,current);
            return;
        }

        workspaceAccessCache=await json('/api/workspace-access').catch(()=>[]);
        studioRolesCache=await json('/api/security/roles').catch(()=>[]);
        if(!current())return;


        let content='';
        switch(view){
            case 'about': content=`<section class="studio-about-card studio-about" aria-label="Thinking Machines support"><div class="studio-about-card-body"><img class="studio-about-logo" src="/thinking-machines-letterhead.png" alt="Thinking Machines — pixelated thinking man" width="2025" height="375"><dl class="studio-about-details"><div><dt>Support</dt><dd><a href="mailto:it@thinkingmachines.ltd">it@thinkingmachines.ltd</a></dd></div><div><dt>Studio</dt><dd>Bryan’s Photo Studios</dd></div><div><dt>Time zone</dt><dd>America/Jamaica</dd></div><div><dt>Installation</dt><dd>${esc(window.location.origin)}</dd></div></dl><button type="button" class="action studio-about-key" data-about-api-key>Generate API Key</button></div></section>
                <section class="studio-about-card" data-service-status><header class="studio-about-card-heading"><h2>Service health</h2><div class="studio-about-card-tools"><button class="action" data-refresh-service-status>Refresh</button><button class="action" data-service-audit>Recent audit</button></div></header><div class="studio-about-card-body" data-service-status-body>Loading…</div></section>
                <section class="studio-about-card studio-about-demo" id="studioDemoLoader"><header class="studio-about-card-heading"><h2>Demo Data</h2></header><div class="studio-about-card-body"><p class="studio-about-demo-warning">Reset clears business records and files. Staff accounts, configuration and Unassigned are preserved.</p><div class="studio-about-demo-actions"><button class="action" type="button" data-demo-preview>Preview reset scope</button><button class="action" type="button" data-demo-remove>Remove demo data</button><button class="primary" type="button" data-load-demo>Clear and recreate</button></div><progress data-demo-progress max="100" value="0" aria-label="Demo data loading progress" hidden></progress><p class="admin-demo-status" data-demo-status role="status" aria-live="polite"></p><small data-demo-elapsed hidden></small></div></section>`;break;
            case 'staff-roles': content=renderEmployeeDirectorySection()+renderSecurityRolesSection('Job');break;
        }
        genericView.innerHTML=StudioPage.html({title,scroll:view!=='about',summary:view==='about'?'<p>Product information, support, service health and administrator demo tools.</p>':view==='staff-roles'?'<p>Maintain staff identities, reusable job roles and global operational access. Workspace administration stays on Workspaces.</p>':'',content:`<div class="${view==='about'?'studio-about-grid':'admin-generated-sections'}">${content}</div>`});
        StudioPage.bind(genericView,current);

        if(!current())return;

        const statusPanel=document.querySelector('[data-service-status]');
        if(statusPanel){
            const readStatus=async()=>{const body=statusPanel.querySelector('[data-service-status-body]');try{const s=await json('/api/admin/studio-status');if(!current()||!body.isConnected)return;body.innerHTML=`<dl class="studio-health-details"><div><dt>Database</dt><dd>${esc(s.database)}</dd></div><div><dt>Storage</dt><dd>${esc(s.storage.provider)} · ${s.storage.available?'Available':'Unavailable'} · ${s.storage.writable?'Writable':'Read only'}</dd></div><div><dt>Previews</dt><dd>${esc(s.previews.queued)} queued · ${esc(s.previews.failed)} retrying · ${esc(s.previews.unsupported)} unsupported</dd></div><div><dt>Messaging</dt><dd>${esc(s.messaging.queuedEmails)} emails queued · ${esc(s.messaging.failedNotifications)} failed notices</dd></div><div><dt>Gallery</dt><dd>${s.galleryConfigured?'Configured':'Unconfigured'}</dd></div><div><dt>Diagnostics</dt><dd>${esc(s.diagnostics||'Not supplied')}</dd></div><div><dt>Last checked</dt><dd>${s.checkedAtUtc?esc(new Date(s.checkedAtUtc).toLocaleString()):'Not supplied by server'}</dd></div></dl>`;}catch(e){if(current()&&body.isConnected)body.textContent=e.message;}};
            statusPanel.querySelector('[data-refresh-service-status]').onclick=readStatus;
            statusPanel.querySelector('[data-service-audit]').onclick=async()=>{const dialog=studioDialog('Recent operational audit','<p>Loading…</p>');try{const rows=await json('/api/admin/operations/audit?count=100');dialog.querySelector('.studio-dialog-body').textContent=JSON.stringify(rows,null,2);}catch(e){dialog.querySelector('.studio-dialog-body').textContent=e.message;}};
            readStatus();
        }
        if(view==='about')genericView.querySelector('[data-about-api-key]').onclick=()=>{
            if(!current())return;
            const dialog=studioDialog('Generate API Key',window.StudioAdmin.keyForm());
            window.StudioAdmin.bindKeyForm(dialog);
            const observer=new MutationObserver(()=>{if(!current()||!dialog.isConnected){dialog.close();observer.disconnect();}});
            observer.observe(document.body,{attributes:true,attributeFilter:['data-studio-view']});observer.observe(genericView,{childList:true});
            dialog.addEventListener('close',()=>observer.disconnect(),{once:true});
        };
        if(view==='staff-roles')await refreshStudioEmployees();
        if(view==='staff-roles'&&current())await renderGlobalRoleAdministration(current);
        if(!current())return;
        const adminGrid=genericView.querySelector('.admin-generated-sections');
        for(const section of [...genericView.children].filter(el=>el.classList.contains('job-section'))){section.classList.add('admin-card');adminGrid.appendChild(section);}

        const panel=document.getElementById('studioDemoLoader');
        if(!panel)return;
        panel.querySelector('[data-demo-preview]').onclick=async()=>{
            const status=panel.querySelector('[data-demo-status]');
            const button=panel.querySelector('[data-demo-preview]');
            button.disabled=true;status.textContent='Inspecting Studio database schema…';
            try{
                const result=await json('/api/admin/connected-workflow/demo-data/preview');
                const rows=result.tables||[];
                status.textContent=rows.length+' Studio tables inventoried. Generation is available; removal requires the explicit server safeguard.';
                const dialog=studioDialog('Demo reset preview','<p>'+esc(result.warning||'Preview only')+'</p><p>'+rows.length+' Studio tables identified. No records were changed.</p><div style="max-height:45vh;overflow:auto">'+rows.map(x=>'<div>'+esc(x.table)+'</div>').join('')+'</div>');
            }catch(error){status.textContent='Preview failed: '+error.message;}
            finally{button.disabled=false;}
        };
        const runDemo=async(reset=true)=>{
            reset=true;
            const status=panel.querySelector('[data-demo-status]'),meter=panel.querySelector('[data-demo-progress]');
            if(reset&&window.prompt('This clears Studio business records and files, then rebuilds the complete demo including job templates and photographs. Type REMOVE STUDIO DEMO DATA to continue.')!=='REMOVE STUDIO DEMO DATA')return;
            const buttons=[...panel.querySelectorAll('button')];buttons.forEach(b=>b.disabled=true);panel.setAttribute('aria-busy','true');meter.hidden=false;meter.value=0;
            status.textContent=reset?'Starting full demo reset and regeneration…':'Starting full demo reset and recreation…';
            try{
                const run=await window.studioLoadDemoData(progress=>{
                    if(!panel.isConnected)return;
                    meter.value=progress.percent||0;meter.setAttribute('aria-valuetext',`${progress.percent||0}% — ${progress.stage||progress.status}`);
                    status.textContent=`${progress.percent||0}% · ${progress.stage||progress.status} — ${progress.message||''}`;
                },{reset});
                status.textContent=run.message;await loadWorkspaces();
            }catch(e){status.textContent='Demo generation failed: '+e.message;}
            finally{buttons.forEach(b=>b.disabled=false);panel.setAttribute('aria-busy','false');}
        };
        panel.querySelector('[data-demo-remove]').onclick=async()=>{
            const status=panel.querySelector('[data-demo-status]');
            const confirmation=window.prompt('This clears all Studio business records and stored files while preserving system configuration. Type REMOVE STUDIO DEMO DATA to continue.');
            if(confirmation!=='REMOVE STUDIO DEMO DATA')return;
            const button=panel.querySelector('[data-demo-remove]');
            button.disabled=true;
            status.textContent='Clearing Studio business records and files…';
            try{
                const result=await json('/api/admin/connected-workflow/demo-data/remove',{
                    method:'POST',headers:{'Content-Type':'application/json'},
                    body:JSON.stringify({confirmation})
                });
                status.textContent=result.message||'Studio data cleared.';
                await loadWorkspaces();
            }catch(e){status.textContent='Removal failed: '+e.message;}
            finally{button.disabled=false;}
        };
        panel.querySelector('[data-load-demo]').onclick=()=>runDemo(true);

        try{
            const current=await window.studioGetCurrentDemoDataRun?.();
            if(current){
                const button=panel.querySelector('[data-load-demo]');
                const status=panel.querySelector('[data-demo-status]');
                const meter=panel.querySelector('[data-demo-progress]');
                meter.hidden=false;meter.value=current.percent||0;
                meter.setAttribute('aria-valuetext',(current.percent||0)+'% — '+(current.stage||current.status||'Demo load'));
                status.textContent=(current.percent||0)+'% · '+(current.stage||current.status)+' — '+(current.message||'');
                if(current.status==='Running')button.textContent='Reconnect to Demo Load';
            }
        }catch{/* Demo status is advisory; the load action remains available. */}
    }


    const routeAliases={today:'dashboard',admin:'about','studio-management':'about','demo-data':'about','api-site-key':'about','booking-sessions':'catalog','job-templates':'templates','work-order-templates':'templates','workflows-admin':'templates','staff-admin':'staff-roles','roles-admin':'staff-roles'};
    const canonicalRoute=view=>routeAliases[view]||view;
    const routes={
        dashboard:()=>window.StudioDashboard.render(),
        notifications:()=>showNotifications(),
        clients:()=>window.StudioClients.render({clientId:window.history.state?.studioClientId,tab:window.history.state?.studioClientTab}),
        messages:()=>window.showStudioMessages(),
        schedule:()=>window.history.state?.studioSchedulePanel==='pipeline'?showPipeline():window.history.state?.studioSchedulePanel==='production'?window.StudioProductionPlanning.render():window.StudioPhotographerSchedule.render({workspaceId:window.history.state?.studioWorkspaceId,photographer:window.history.state?.studioPhotographer||'',date:window.history.state?.studioScheduleDate||'',view:window.history.state?.studioScheduleView||'',resourceId:window.history.state?.studioScheduleResource||''}),
        tasks:()=>window.StudioTasks.render(),
        workspaces:renderWorkspaces,
        'workspace-management':()=>window.StudioWorkspaceManagement.render(),
        jobs:renderJobs,
        pipeline:()=>showPipeline(),
        production:()=>showCatalogProduction({workspaceId:window.history.state?.studioWorkspaceId}),
        ...Object.fromEntries(['about','staff-roles'].map(view=>[view,()=>renderAdmin(view)])),
        catalog:()=>showCommercialCatalog({tab:window.history.state?.studioCatalogTab}),
        templates:()=>showCommercialTemplates(window.history.state?.studioTemplateKind||'Job'),
        media:()=>showMediaReview(),
        frontdesk:()=>showFrontDesk({queue:window.history.state?.studioDashboardQueue,workspaceId:window.history.state?.studioWorkspaceId}),
        'session-plan':()=>StudioWorkflowViews.showSessionPlan(window.history.state?.studioJobId),
        'session-plan-import':()=>StudioWorkflowViews.showSessionPlan(window.history.state?.studioJobId),
        delivery:()=>StudioWorkflowViews.showDelivery({workspaceId:window.history.state?.studioWorkspaceId}),
        finance:()=>showAccounts(),
        reports:()=>showManagement(),
        management:()=>showManagement()
    };

    let currentNavigationView=document.body.dataset.studioView||'dashboard';
    const navigationLabels={...Object.fromEntries((window.StudioAdmin?.entries||[]).map(entry=>[entry.view,entry.label])),'workspace-management':'Workspaces','session-plan-import':'Session Plan Import','session-plan':'Session Plan',delivery:'Delivery',catalog:'Products and Services','job-templates':'Job Templates','work-order-templates':'Work Order Templates',clients:'Client Management',dashboard:'Today',workspaces:'Workspaces',jobs:'Jobs',pipeline:'Pipeline',production:'Production',media:'Media',frontdesk:'Front Desk',finance:'Finance',reports:'Reports',management:'Management',messages:'Messages',notifications:'Notifications',tasks:'Tasks',schedule:'Schedule'};

    function navigationContext(view,options={}){
        return {studioView:view,studioSchedulePanel:view==='schedule'?(['pipeline','production'].includes(options.panel)?options.panel:'workers'):null,studioPhotographer:view==='schedule'?String(options.photographer||''):null,studioScheduleResource:view==='schedule'?String(options.resourceId||''):null,studioScheduleDate:view==='schedule'?String(options.date||''):null,studioScheduleView:view==='schedule'?String(options.mode||''):null,studioClientId:view==='clients'?String(options.clientId||''):null,studioDashboardQueue:options.queue||null,studioWorkspaceId:['jobs','frontdesk','production','delivery','schedule'].includes(view)?String(options.workspaceId??jobsWorkspaceFilter??''):null,studioClientTab:view==='clients'?String(options.tab||''):null,studioTemplateKind:view==='templates'?(['Job','WorkOrder','Automation'].includes(options.kind)?options.kind:'Job'):null,studioCatalogTab:view==='catalog'?(options.tab==='booking-sessions'?'booking-sessions':'products'):null,studioJobId:['workspaces','session-plan','session-plan-import'].includes(view)&&options.jobId?String(options.jobId):null};
    }
    function recordNavigation(view,options={}){
        const next=navigationContext(view,options),previous=window.history.state;
        const params=new URLSearchParams({studio:view});
        if(['pipeline','production'].includes(next.studioSchedulePanel))params.set('panel',next.studioSchedulePanel);
        if(next.studioPhotographer)params.set('photographer',next.studioPhotographer);
        if(next.studioScheduleResource)params.set('resource',next.studioScheduleResource);
        if(next.studioScheduleDate)params.set('date',next.studioScheduleDate);
        if(next.studioScheduleView)params.set('view',next.studioScheduleView);
        if(next.studioClientId)params.set('client',next.studioClientId);
        if(next.studioClientTab)params.set('tab',next.studioClientTab);
        if(next.studioCatalogTab)params.set('tab',next.studioCatalogTab);
        if(next.studioTemplateKind)params.set('kind',next.studioTemplateKind);
        if(next.studioWorkspaceId)params.set('workspace',next.studioWorkspaceId);
        if(next.studioJobId)params.set('job',next.studioJobId);
        if(next.studioDashboardQueue)params.set('queue',next.studioDashboardQueue);
        const same=previous?.studioDashboardQueue===next.studioDashboardQueue&&previous?.studioView===next.studioView&&['studioSchedulePanel','studioPhotographer','studioScheduleResource','studioScheduleDate','studioScheduleView','studioCatalogTab','studioTemplateKind'].every(key=>(previous[key]||'')===(next[key]||''))&&(previous.studioClientId||null)===(next.studioClientId||null)&&(previous.studioWorkspaceId||null)===(next.studioWorkspaceId||null)&&(previous.studioJobId||null)===next.studioJobId&&(previous.studioClientTab||'')===(next.studioClientTab||'');
        if(options.replaceHistory)window.history.replaceState(next,'','#'+params);
        else if(!options.browserHistory&&options.history!==false&&!same)window.history.pushState(next,'','#'+params);
        currentNavigationView=view;
        if(view==='jobs')jobsWorkspaceFilter=next.studioWorkspaceId;
        return next;
    }
    function beginNavigation(view,options){
        const context=recordNavigation(view,options),revision=++navigationRevision;
        // Switch mutually exclusive page surfaces before awaiting route data.
        // Otherwise the previous Job Details view remains above the new page.
        const openingJob = view === 'workspaces' && !!context.studioJobId;
        if (!openingJob) {
            conversationView.classList.add('hidden');
             genericView.classList.remove('hidden');
            genericView.replaceChildren();
        } else {
            genericView.classList.add('hidden');
        }
        window.StudioProductionPlanning?.invalidate();
        window.StudioPhotographerSchedule?.invalidate();
        window.StudioClients?.invalidate();
        window.StudioJobsPage?.dispose();
        window.StudioPage?.invalidate();
        window.StudioMessages?.invalidate();
        window.StudioWorkspace?.invalidate();
        window.StudioDashboard?.dispose();
        return {context,revision};
    }
    // Job Details shares the workspace surface, but has its own history destination.
    window.studioBeginJobNavigation=(id,options={})=>beginNavigation('workspaces',{...options,jobId:id});

    window.studioNavigate=async function(view,options={}){
        if(view==='workflows-admin')options={...options,kind:'Automation'};
        if(view==='job-templates'||view==='work-order-templates')options={...options,kind:view==='work-order-templates'?'WorkOrder':'Job'};
        if(view==='booking-sessions')options={...options,tab:'booking-sessions'};
        view=canonicalRoute(view);
        const route=routes[view];
        if(!route) throw new Error('Unknown Studio navigation view: '+view);
        const {context,revision}=beginNavigation(view,options);
        setActive(view);
        window.studioShellMountContext?.([],view==='workspaces'&&context.studioJobId?'job-detail':view);
        if(studioPageTools)studioPageTools.innerHTML='';
        if(view!=='dashboard'){
            window.studioSetBreadcrumb?.([{label:navigationLabels[view]||view.replace(/(^|\s)\S/g,m=>m.toUpperCase()),view}]);
        }
        try{
            if(view==='workspaces'&&context.studioJobId)await window.openWorkspaceJob(context.studioJobId,{navigationRecorded:true,history:false});
            else await route();
            if(revision===navigationRevision){
                setActive(view);
                if(view!=='dashboard'&&typeof renderWorkspaceMenu==='function')renderWorkspaceMenu();
                window.StudioShell?.sync(view);
            }
        }catch(error){
            if(revision!==navigationRevision)return;
            conversationView.classList.add('hidden');
            genericView.classList.remove('hidden');
            genericView.innerHTML='<div class="studio-page-heading"><h1>Unable to load view</h1></div><div class="studio-card"><p>'+esc(error?.message||error)+'</p></div>';
            throw error;
        }
    };

    function destinationFromLocation(state){
        const params=new URLSearchParams(window.location.hash.replace(/^#/,''));
        const view=canonicalRoute(state?.studioView&&routes[canonicalRoute(state.studioView)]?state.studioView:params.get('studio'));
        return {view:view&&routes[view]?view:'dashboard',kind:(state?.studioView==='workflows-admin'||(!state?.studioView&&params.get('studio')==='workflows-admin'))?'Automation':(state?.studioView==='work-order-templates'||(!state?.studioView&&params.get('studio')==='work-order-templates'))?'WorkOrder':state?.studioTemplateKind||params.get('kind')||'Job',panel:state?.studioView?(state.studioSchedulePanel||'workers'):(params.get('panel')||'workers'),photographer:state?.studioView?(state.studioPhotographer||''):(params.get('photographer')||''),resourceId:state?.studioView?(state.studioScheduleResource||''):(params.get('resource')||''),date:state?.studioView?(state.studioScheduleDate||''):(params.get('date')||''),mode:state?.studioView?(state.studioScheduleView||''):(params.get('view')||''),tab:(state?.studioView==='booking-sessions'||(!state?.studioView&&params.get('studio')==='booking-sessions'))?'booking-sessions':state?.studioView?(state.studioView==='catalog'?(state.studioCatalogTab||'products'):(state.studioClientTab||'')):(params.get('tab')||''),clientId:state?.studioView?(state.studioClientId||''):(params.get('client')||''),workspaceId:state?.studioView?(state.studioWorkspaceId||''):(params.get('workspace')||''),queue:state?.studioDashboardQueue||params.get('queue')||'',jobId:state?.studioView?(state.studioJobId||null):(params.get('job')||null)};
    }
    window.addEventListener('popstate',event=>{
        const destination=destinationFromLocation(event.state);
        window.studioNavigate(destination.view,{...destination,browserHistory:true,history:false}).catch(error=>console.error('Studio browser navigation failed.',error));
    });

    document.querySelectorAll('[data-view]').forEach(button=>{
        button.type='button';
        button.onclick=()=>window.studioNavigate(button.dataset.view);
    });

    const topNew=document.getElementById('topNewButton');
    if(topNew) topNew.onclick=async()=>{topNew.disabled=true;try{await loadWorkspaces();document.getElementById('newRequestDialog')?.showModal();}catch(error){alert(error.message);}finally{topNew.disabled=false;}};

    const startStudio=async()=>{
        const destination=destinationFromLocation(window.history.state);
        window.studioNavigate(destination.view,{...destination,history:false,replaceHistory:true}).catch(error=>{
            console.error('Studio startup navigation failed.',error);
        });
    };
    if(document.readyState==='loading') document.addEventListener('DOMContentLoaded',startStudio,{once:true});
    else startStudio();
})();












