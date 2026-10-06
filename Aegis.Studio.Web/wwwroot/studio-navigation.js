(() => {
    const conversationView=document.getElementById('conversationView');
    const genericView=document.getElementById('genericView');

    function setActive(view){
        document.body.dataset.studioView=view;
        document.querySelectorAll('[data-view]').forEach(button=>{
            button.classList.toggle('active',button.dataset.view===view);
        });
    }

    let myWorkLoad=null;
    let navigationRevision=0;

    function renderMyWork(){
        const controller=new AbortController(),revision=navigationRevision;
        const state={auth:{},jobs:[],tasks:[],schedule:[],notifications:[],media:[],status:{},errors:{},taskFilter:'all'};
        state.active=()=>{if(state.root&&!state.root.isConnected)controller.abort();return !controller.signal.aborted&&revision===navigationRevision&&['mywork','tasks'].includes(document.body.dataset.studioView)&&!genericView.classList.contains('hidden');};
        myWorkLoad=controller;
        conversationView.classList.add('hidden');genericView.classList.remove('hidden');
        const started=performance.now(),run=globalThis.crypto?.randomUUID?.()||String(Date.now());
        const diagnostics={run,startedAt:new Date().toISOString(),requests:[],shellMs:null,coreMs:null,completeMs:null};
        window.studioMyWorkDiagnostics=diagnostics;
        const now=new Date(),from=new Date(now.getFullYear(),now.getMonth(),now.getDate()),to=new Date(now.getFullYear(),now.getMonth(),now.getDate()+1);
        const urls={tasks:'/api/my-work/tasks',schedule:'/api/schedule/entries?from='+encodeURIComponent(from.toISOString())+'&to='+encodeURIComponent(to.toISOString()),jobs:'/api/my-work/jobs',auth:'/api/auth/me',notifications:'/api/notifications?count=80',media:'/api/my-work/media-summary'};
        let inFlight=0;const waiting=[];
        async function load(key){
            while(inFlight>=2)await new Promise(resolve=>waiting.push(resolve));
            if(!state.active())return;
            inFlight++;state.status[key]='loading';delete state.errors[key];paintMyWork(state);
            const begin=performance.now();let outcome='ok',trace=null;
            try{
                const value=await json(urls[key],{signal:controller.signal,headers:{'X-Studio-Startup':run},onResponse:response=>{trace=response.headers?.get('X-Studio-Trace');}});
                if(!state.active())return;
                state[key]=value;state.status[key]='ready';
            }catch(error){
                if(controller.signal.aborted){outcome='cancelled';return;}
                outcome='error';state.status[key]='error';state.errors[key]=error.message;
            }finally{
                inFlight--;waiting.shift()?.();
                diagnostics.requests.push({component:key,url:urls[key],outcome,elapsedMs:Math.round(performance.now()-begin),trace});
                console.debug('[Studio My Work]',diagnostics.requests.at(-1));
                if(state.active())paintMyWork(state);
            }
        }
        state.retry=key=>load(key);
        paintMyWork(state);diagnostics.shellMs=Math.round(performance.now()-started);
        // Two reads at a time: operational essentials first, enrichment afterwards.
        const pending=['tasks','schedule','jobs','auth','notifications','media'];
        async function worker(){
            while(pending.length&&state.active()){
                await load(pending.shift());
                if(diagnostics.coreMs===null&&['tasks','schedule'].every(k=>['ready','error'].includes(state.status[k])))diagnostics.coreMs=Math.round(performance.now()-started);
            }
        }
        window.studioMyWorkReady=Promise.all([worker(),worker()]).then(()=>{
            diagnostics.completeMs=Math.round(performance.now()-started);
            diagnostics.cancelled=controller.signal.aborted;
            console.info('[Studio My Work startup]',diagnostics);
        });
        return Promise.resolve();
    }

    function paintMyWork(state){
        if(!state.active())return;
        const {auth,jobs,tasks,schedule,notifications}=state;
        const now=new Date(),startOfDay=new Date(now.getFullYear(),now.getMonth(),now.getDate()),endOfDay=new Date(now.getFullYear(),now.getMonth(),now.getDate()+1);
        const phase=(key,content)=>state.status[key]==='ready'?content:state.status[key]==='error'?'<div class="mw-empty" role="alert">'+esc(state.errors[key])+' <button data-mw-retry="'+key+'">Retry</button></div>':'<div class="mw-empty" role="status">Loading…</div>';
        const metric=(key,value)=>state.status[key]==='ready'?value:'…';
        const me=auth.user||auth,user=String(me.userName||me.loginName||'').toLowerCase();
        const displayName=String(me.displayName||me.name||me.userName||'Jordan').trim();
        const firstName=(displayName.split(/[\s\\@.]+/).filter(Boolean)[0]||'Jordan');
        const hour=now.getHours(),greeting=hour<12?'Good morning':hour<18?'Good afternoon':'Good evening';
        const roles=[...(Array.isArray(me.roles)?me.roles:[]),...(Array.isArray(auth.roles)?auth.roles:[])].map(x=>String(x).toLowerCase());
        const isManager=me.isAdministrator===true||auth.isAdministrator===true||roles.some(r=>/administrator|manager|owner|finance/.test(r));
        const myTasks=tasks.filter(t=>!['Completed','Cancelled'].includes(t.status)&&(!user||String(t.assignedTo||'').toLowerCase()===user));
        const todaySchedule=schedule.filter(entry=>entry.status!=='Cancelled'&&new Date(entry.startsAtUtc)>=startOfDay&&new Date(entry.startsAtUtc)<endOfDay).sort((a,b)=>new Date(a.startsAtUtc)-new Date(b.startsAtUtc));
        const recentJobs=[...jobs].sort((a,b)=>new Date(b.createdAtUtc||b.createdAt||0)-new Date(a.createdAtUtc||a.createdAt||0)).slice(0,4);
        const mediaByJob=new Map(state.media.map(row=>[String(row.jobId),row]));
        const mediaSnapshots=recentJobs.map(job=>({job,media:mediaByJob.get(String(job.id))||{assetCount:0}}));
        const totalPhotos=state.media.reduce((sum,row)=>sum+row.assetCount,0);
        const messageNotifications=notifications.filter(n=>/message|conversation|reply/i.test(String(n.eventType||n.title||n.type||'')));
        const activityNotifications=notifications.slice(0,8);

        const jobById=id=>jobs.find(j=>String(j.id)===String(id));
        const scheduleJob=entry=>jobById(entry.jobId)||{title:entry.title||'Scheduled session',clientName:entry.title||''};
        const formatTime=value=>new Date(value).toLocaleTimeString([],{hour:'numeric',minute:'2-digit'});
        const formatDay=value=>new Date(value).toLocaleDateString([],{month:'short',day:'numeric'});
        const imageForJob=jobId=>{
            const version=mediaByJob.get(String(jobId))?.thumbnailVersionId;
            return version?'/api/jobs/'+jobId+'/versions/by-id/'+version+'/preview?size=thumbnail':'';
        };

        const recentCards=mediaSnapshots.slice(0,4).map(({job,media})=>{
            const image=imageForJob(job.id);
            const count=state.status.media==='ready'?media.assetCount:'…';
            return `<button class="mw-recent-card" data-open-job="${job.id}">
                <div class="mw-recent-image">${image?`<img loading="lazy" src="${image}" alt="">`:'<span>📷</span>'}</div>
                <div class="mw-recent-copy">
                    <strong>${esc(job.title)}</strong>
                    <span>${count} photos · ${esc(job.status||'')}</span>
                </div>
            </button>`;
        }).join('');

        const sessionRows=todaySchedule.map(entry=>{
            const job=scheduleJob(entry);
            const image=entry.jobId?imageForJob(entry.jobId):'';
            return `<button class="mw-session-row" data-open-job="${entry.jobId||job.id||''}">
                <div class="mw-session-time"><strong>${formatTime(entry.startsAtUtc||entry.start||entry.startsAt)}</strong><span>${formatTime(entry.endsAtUtc||entry.end||entry.endsAt)}</span></div>
                <div class="mw-session-thumb">${image?`<img loading="lazy" src="${image}" alt="">`:'<span>📷</span>'}</div>
                <div class="mw-session-main"><strong>${esc(job.title||entry.title||'Session')}</strong><span>${esc(job.clientName||'')}</span><span>${esc(entry.location||'Location not set')}</span></div>
                <span class="mw-state-pill">${esc(entry.status||'Upcoming')}</span>
                <span class="mw-kebab">⋮</span>
            </button>`;
        }).join('')||'<div class="mw-empty">No sessions scheduled today.</div>';

        const taskRows=myTasks.slice(0,5).map(task=>`<button class="mw-task-row" data-open-job="${task.jobId}" data-task-type="${esc(task.activityType||'')}">
            <span class="mw-check"></span>
            <strong>${esc(task.title)}</strong>
            <span>${esc(task.activityType||'Photography')}</span>
            <span class="mw-due">${task.dueAtUtc?formatDay(task.dueAtUtc):'Open'}</span>
            <span class="mw-priority ${String(task.priority||'').toLowerCase()}">${esc(task.priority||'Normal')}</span>
            <span class="mw-kebab">⋮</span>
        </button>`).join('')||'<div class="mw-empty">No open tasks assigned to you.</div>';

        const scheduleRows=todaySchedule.map((entry,index)=>{
            const job=scheduleJob(entry);
            return `<button class="mw-schedule-row" data-open-job="${entry.jobId||job.id||''}">
                <span class="mw-timeline-dot ${index===0?'now':''}"></span>
                <span class="mw-schedule-time">${formatTime(entry.startsAtUtc||entry.start||entry.startsAt)}</span>
                <span class="mw-schedule-copy"><strong>${esc(job.title||entry.title||'Session')}</strong><small>${esc(entry.location||'Location not set')}</small></span>
                <span class="mw-when">${new Date(entry.endsAtUtc)<now?'Finished':new Date(entry.startsAtUtc)<=now?'Now':'Upcoming'}</span>
            </button>`;
        }).join('')||'<div class="mw-empty">No schedule items today.</div>';

        const messageRows=messageNotifications.slice(0,3).map(n=>`<button class="mw-message-row" data-notification="${n.id}" data-message-job="${n.referenceType==='Job'?n.referenceId||'':''}">
            <span class="mw-message-avatar">${esc(String(n.subject||n.title||n.eventType||'M').slice(0,1))}</span>
            <span class="mw-message-copy"><strong>${esc(n.subject||n.title||'Studio message')}</strong><small>${esc(n.body||n.message||'New activity in Studio')}</small></span>
            <span class="mw-message-time">${n.createdAtUtc?formatTime(n.createdAtUtc):''}</span>
        </button>`).join('')||'<div class="mw-empty">No new messages.</div>';

        const activityRows=activityNotifications.slice(0,4).map(n=>`<div class="mw-activity-row">
            <span class="mw-activity-icon">✓</span>
            <span><strong>${esc(n.subject||n.title||n.eventType||'Studio activity')}</strong><small>${esc(n.body||n.message||'')}</small></span>
            <time>${n.createdAtUtc?formatTime(n.createdAtUtc):''}</time>
        </div>`).join('')||'<div class="mw-empty">No recent activity.</div>';

        const pipelineCounts={
            capture:jobs.filter(j=>/photo|capture|scheduled/i.test(j.status||'')).length,
            edit:jobs.filter(j=>/edit|process/i.test(j.status||'')).length,
            review:jobs.filter(j=>/review|approval/i.test(j.status||'')).length,
            deliver:jobs.filter(j=>/deliver|ready|complete/i.test(j.status||'')).length
        };
        const overdueTasks=myTasks.filter(t=>t.dueAtUtc&&new Date(t.dueAtUtc)<now).length;
        const blockedTasks=myTasks.filter(t=>/blocked|waiting/i.test(String(t.status||'')+' '+String(t.blockedReason||''))).length;
        const attentionJobs=jobs.filter(j=>/blocked|overdue|needs scheduling|awaiting upload/i.test(j.status||'')).length;
        const quickLinks=[
            ['workspaces','Workspaces',jobs.length],
            ['pipeline','Pipeline',todaySchedule.length],
            ['production','Production',myTasks.filter(t=>/edit|retouch|qc|print|deliver|production/i.test(t.activityType||'')).length],
            ['frontdesk','Front Desk',messageNotifications.length],
            ...(isManager?[['finance','Finance',''],['management','Management','']]:[])
        ];

        const productionTasks=myTasks.filter(t=>/edit|retouch|qc|print|deliver|production|gallery|select|design|color|review/i.test(String(t.activityType||'')+' '+String(t.title||'')));
        const attentionItems=productionTasks.slice(0,5);
        const markup=`
            <section class="mw-page">
                <header class="mw-hero" data-mw-region="header">
                    <div class="mw-hero-copy"><h1>${greeting}, ${esc(firstName)}</h1><p>Here's what's happening in your studio today.</p></div>
                    <div class="mw-hero-date"><strong>${now.toLocaleDateString([],{weekday:'short',month:'short',day:'numeric',year:'numeric'})}</strong><div><button type="button">‹</button><button type="button" class="today">Today</button><button type="button">›</button></div></div>
                </header>
                <div class="mw-metrics" data-mw-region="metrics">
                    <article class="mw-metric"><span class="mw-metric-icon blue">▣</span><div><strong>${metric('schedule',todaySchedule.length)}</strong><b>Appointments Today</b><small>${todaySchedule.filter(x=>new Date(x.startsAtUtc)>now).length} upcoming</small></div></article>
                    <article class="mw-metric"><span class="mw-metric-icon green">▧</span><div><strong>${metric('jobs',jobs.length)}</strong><b>Items in Pipeline</b><small>${attentionJobs} need your action</small></div></article>
                    <article class="mw-metric"><span class="mw-metric-icon orange">☑</span><div><strong>${metric('tasks',productionTasks.length)}</strong><b>Production Tasks</b><small>${productionTasks.filter(t=>/review|approval/i.test(String(t.status||'')+' '+String(t.title||''))).length} ready for review</small></div></article>
                    <article class="mw-metric"><span class="mw-metric-icon purple">♧</span><div><strong>${metric('notifications',messageNotifications.filter(n=>!n.isRead).length)}</strong><b>Client Messages</b><small>${messageNotifications.filter(n=>!n.isRead).length} unread</small></div></article>
                </div>
                <div class="mw-dashboard">
                    <div class="mw-dashboard-main">
                        <section class="mw-card mw-schedule-card" data-mw-region="schedule">
                            <header><h2>▣ &nbsp; Today's Schedule</h2><button data-nav="schedule">View Schedule →</button></header>
                            <div class="mw-schedule-list">${phase('schedule',sessionRows)}</div>
                        </section>
                        <section class="mw-card mw-production-attention" data-mw-region="production-attention">
                            <header><h2>▧ &nbsp; Production Items Needing Your Attention</h2><button data-nav="production">View Pipeline →</button></header>
                            <div class="mw-attention-grid">${phase('tasks',attentionItems.length?attentionItems.map(task=>{const job=jobById(task.jobId)||{};const image=imageForJob(task.jobId);return `<button class="mw-attention-card" data-open-job="${task.jobId||''}"><span class="mw-attention-image">${image?`<img loading="lazy" src="${image}" alt="">`:'<span>📷</span>'}</span><strong>${esc(job.clientName||job.title||task.title)}</strong><small>${esc(task.activityType||task.title)}</small><em>${task.dueAtUtc?'Due '+formatTime(task.dueAtUtc):esc(task.status||'Needs attention')}</em></button>`}).join(''):'<div class="mw-empty">No production items need attention.</div>')}</div>
                        </section>
                    </div>
                    <aside class="mw-dashboard-side">
                        <section class="mw-card" data-mw-region="tasks">
                            <header><h2>☑ &nbsp; My Tasks</h2><button data-nav="tasks">View All →</button></header>
                            <div>${phase('tasks',taskRows)}</div>
                        </section>
                        <section class="mw-card" data-mw-region="messages">
                            <header><h2>◯ &nbsp; Recent Messages</h2><button data-nav="messages">View All →</button></header>
                            <div>${phase('notifications',messageRows)}</div>
                        </section>
                        <section class="mw-card mw-quick-actions">
                            <header><h2>ϟ &nbsp; Quick Actions</h2></header>
                            <div><button data-nav="pipeline">▣ &nbsp; Start Capture Session</button><button data-nav="production">⇧ &nbsp; Upload to Pipeline</button><button id="mwCreateJob">＋ &nbsp; Create New Job</button><button data-nav="schedule">▣ &nbsp; Check In Subject</button></div>
                        </section>
                    </aside>
                </div>
            </section>`;
        const next=document.createElement('div');next.innerHTML=markup;
        if(!genericView.querySelector('.mw-page')){genericView.replaceChildren(next.firstElementChild);state.root=genericView.querySelector('.mw-page');}
        else next.querySelectorAll('[data-mw-region]').forEach(region=>{
            const existing=genericView.querySelector('[data-mw-region="'+region.dataset.mwRegion+'"]');
            if(existing.innerHTML===region.innerHTML)return;
            const scroll=[...existing.querySelectorAll('div')].map(el=>el.scrollTop);
            const focused=document.activeElement?.dataset?.mwFilter;
            existing.innerHTML=region.innerHTML;
            existing.querySelectorAll('div').forEach((el,i)=>el.scrollTop=scroll[i]||0);
            if(focused)existing.querySelector('[data-mw-filter="'+focused+'"]')?.focus();
        });
        state.root=genericView.querySelector('.mw-page');
        genericView.querySelectorAll('[data-mw-retry]').forEach(b=>b.onclick=()=>{b.disabled=true;state.retry(b.dataset.mwRetry);});
        genericView.querySelectorAll('[data-mw-filter]').forEach(button=>button.onclick=()=>{
            const filter=button.dataset.mwFilter;state.taskFilter=filter;
            genericView.querySelectorAll('[data-mw-filter]').forEach(b=>b.classList.toggle('active',b===button));
            genericView.querySelectorAll('[data-task-type]').forEach(row=>row.hidden=filter!=='all'&&!row.dataset.taskType.toLowerCase().includes(filter));
        });
        genericView.querySelectorAll('[data-notification]').forEach(button=>button.onclick=async()=>{
            await json('/api/notifications/'+button.dataset.notification+'/read',{method:'POST'});
            if(button.dataset.messageJob)await openWorkspaceJob(button.dataset.messageJob);
            else document.getElementById('notificationButton').click();
        });
        genericView.querySelectorAll('[data-open-job]').forEach(button=>button.onclick=()=>{
            if(button.dataset.openJob)openWorkspaceJob(button.dataset.openJob).catch(error=>alert(error.message));
        });
        genericView.querySelectorAll('[data-mw-filter]').forEach(b=>b.classList.toggle('active',b.dataset.mwFilter===state.taskFilter));
        genericView.querySelectorAll('[data-task-type]').forEach(row=>row.hidden=state.taskFilter!=='all'&&!row.dataset.taskType.toLowerCase().includes(state.taskFilter));
        genericView.querySelectorAll('[data-nav]').forEach(button=>button.onclick=()=>window.studioNavigate(button.dataset.nav));
    }

    async function renderWorkspaces(){
        conversationView.classList.add('hidden');
        genericView.classList.remove('hidden');
        genericView.innerHTML='<div class="mywork-loading">Loading workspaces…</div>';

        const [workspaces,jobs,mediaSummary]=await Promise.all([loadWorkspaces().then(()=>workspaceCache),json('/api/jobs'),json('/api/production/media-summary')]);
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

        const activeCount=workspaceData.filter(item=>item.jobs.some(job=>String(job.status||'').toLowerCase()!=='completed')).length;
        const completedWorkspaceCount=workspaceData.filter(item=>item.jobs.length&&item.jobs.every(job=>String(job.status||'').toLowerCase()==='completed')).length;
        const announcements=workspaceData
            .flatMap(item=>(item.announcements||[]).map(a=>({...a,workspaceName:item.workspace.name})))
            .sort((a,b)=>new Date(b.createdAtUtc||0)-new Date(a.createdAtUtc||0))
            .slice(0,4);

        const renderRows=items=>items.map(item=>{
            const current=item.jobs.filter(job=>String(job.status||'').toLowerCase()!=='completed');
            const latest=latestJob(item);
            const photos=photoCount(item);
            const hero=heroFor(item);
            const previews=previewsFor(item);
            const status=current.length?'Active':item.jobs.length?'Completed':'Ready';
            const statusClass=status.toLowerCase();
            const subtitle=latest?.clientName||item.workspace.emailAddress||item.workspace.routeKey||'Studio workspace';
            const location=latest?.location||latest?.eventLocation||'';
            const lastActivity=latest?.eventDate||latest?.createdAtUtc||latest?.createdAt;
            return `<article class="workspace-reference-row" data-workspace-row data-status="${statusClass}" data-search="${esc((item.workspace.name+' '+subtitle+' '+status).toLowerCase())}">
                <button type="button" class="workspace-reference-main" data-open-workspace="${item.workspace.id}" data-workspace-name="${esc(item.workspace.name)}">
                    <span class="workspace-reference-hero">${hero?`<img src="${hero}" alt="">`:'<span class="workspace-photo-fallback">📷</span>'}</span>
                    <span class="workspace-reference-copy">
                        <strong><span class="workspace-folder">▰</span>${esc(item.workspace.name)}</strong>
                        <small>${esc(subtitle)}</small>
                        ${location?`<small>⌖ ${esc(location)}</small>`:''}
                    </span>
                    <span class="workspace-reference-stat"><b>${item.jobs.length}</b><small>Jobs</small></span>
                    <span class="workspace-reference-stat"><b>${photos}</b><small>Photos</small></span>
                    <span class="workspace-reference-stat"><b>${current.length}</b><small>Active</small></span>
                    <span class="workspace-reference-previews">
                        ${previews.map(url=>`<img src="${url}" alt="">`).join('')}
                        ${photos>previews.length?`<i>+${photos-previews.length}</i>`:''}
                    </span>
                    <span class="workspace-reference-activity">
                        <em class="workspace-reference-pill ${statusClass}">${status}</em>
                        <small>Last activity</small>
                        <b>${lastActivity?esc(new Date(lastActivity).toLocaleDateString()):'—'}</b>
                    </span>
                    <span class="workspace-reference-more">⋮</span>
                </button>
            </article>`;
        }).join('');

        genericView.innerHTML=`
            <section class="workspace-reference-page">
                <header class="workspace-reference-header">
                    <div><h1>Workspaces</h1><p>Organize jobs, clients, and conversations.</p></div>
                    <button type="button" class="primary workspace-new-button" data-workspace-admin>＋ New Workspace</button>
                </header>

                <div class="workspace-reference-layout">
                    <div class="workspace-reference-center">
                        <div class="workspace-reference-toolbar">
                            <div class="workspace-reference-tabs" role="tablist">
                                <button class="active" data-workspace-filter="all">All Workspaces <b>${workspaceData.length}</b></button>
                                <button data-workspace-filter="active">Active <b>${activeCount}</b></button>
                                <button data-workspace-filter="completed">Completed <b>${completedWorkspaceCount}</b></button>
                            </div>
                            <div class="workspace-reference-tools">
                                <div class="workspace-view-toggle" aria-label="Workspace view">
                                    <button type="button" data-workspace-display="grid" title="Grid view">▦ Grid</button>
                                    <button type="button" data-workspace-display="list" class="active" title="List view">☷ List</button>
                                </div>
                                <select data-workspace-sort aria-label="Sort workspaces">
                                    <option value="name">Name (A–Z)</option>
                                    <option value="jobs">Most jobs</option>
                                    <option value="photos">Most photos</option>
                                </select>
                            </div>
                        </div>
                        <div class="workspace-reference-list" data-workspace-list>${renderRows(workspaceData)}</div>
                    </div>

                    <aside class="workspace-reference-side">
                        <section class="workspace-side-card">
                            <header><h2>📣 Announcements</h2></header>
                            <div class="workspace-announcement-list">
                                ${announcements.length?announcements.map(a=>`<article><strong>${esc(a.title)}</strong><p>${esc(a.body)}</p><small>${esc(a.workspaceName)} · ${a.createdAtUtc?new Date(a.createdAtUtc).toLocaleDateString():''}</small></article>`).join(''):'<p class="mw-empty">No workspace announcements.</p>'}
                            </div>
                        </section>

                        <section class="workspace-side-card">
                            <header><h2>⌁ Workspace Filters</h2></header>
                            <label>Search<input type="search" placeholder="Search workspaces…" data-workspace-search-side></label>
                            <label>Status<select data-workspace-status-side><option value="all">All Statuses</option><option value="active">Active</option><option value="completed">Completed</option><option value="ready">Ready</option></select></label>
                            <button type="button" class="action workspace-clear-filters">Clear Filters</button>
                        </section>

                        <section class="workspace-side-card">
                            <header><h2>ϟ Quick Actions</h2></header>
                            <button type="button" class="workspace-quick-action" data-workspace-admin>＋ New Workspace</button>
                            <button type="button" class="workspace-quick-action" data-nav-quick="pipeline">▣ View Calendar</button>
                            <button type="button" class="workspace-quick-action" data-nav-quick="frontdesk">＋ Create Booking</button>
                        </section>
                    </aside>
                </div>
            </section>`;

        genericView.querySelectorAll('[data-workspace-display]').forEach(b=>b.onclick=()=>{genericView.querySelector('[data-workspace-list]').classList.toggle('grid-view',b.dataset.workspaceDisplay==='grid');genericView.querySelectorAll('[data-workspace-display]').forEach(x=>x.classList.toggle('active',x===b));});
        const allRows=()=>[...genericView.querySelectorAll('[data-workspace-row]')];
        let filter='all';
        let term='';
        let sort='name';

        const refresh=()=>{
            const list=genericView.querySelector('[data-workspace-list]');
            const rows=allRows();
            rows.forEach(row=>{
                const status=row.dataset.status;
                const matchesFilter=filter==='all'||status===filter;
                const matchesTerm=!term||row.dataset.search.includes(term);
                row.hidden=!(matchesFilter&&matchesTerm);
            });

            rows.sort((a,b)=>{
                const ai=workspaceData.find(x=>String(x.workspace.id)===String(a.querySelector('[data-open-workspace]').dataset.openWorkspace));
                const bi=workspaceData.find(x=>String(x.workspace.id)===String(b.querySelector('[data-open-workspace]').dataset.openWorkspace));
                if(sort==='jobs')return (bi?.jobs.length||0)-(ai?.jobs.length||0);
                if(sort==='photos')return photoCount(bi||{media:[]})-photoCount(ai||{media:[]});
                return String(ai?.workspace.name||'').localeCompare(String(bi?.workspace.name||''));
            }).forEach(row=>list.appendChild(row));
        };

        genericView.querySelectorAll('[data-workspace-filter]').forEach(button=>{
            button.onclick=()=>{
                filter=button.dataset.workspaceFilter;
                genericView.querySelectorAll('[data-workspace-filter]').forEach(x=>x.classList.toggle('active',x===button));
                const side=genericView.querySelector('[data-workspace-status-side]');
                if(side)side.value=filter;
                refresh();
            };
        });
        const search=genericView.querySelector('[data-workspace-search]');
        const searchSide=genericView.querySelector('[data-workspace-search-side]');
        const updateTerm=value=>{
            term=String(value||'').trim().toLowerCase();
            if(search&&search.value!==value)search.value=value;
            if(searchSide&&searchSide.value!==value)searchSide.value=value;
            refresh();
        };
        if(search)search.oninput=e=>updateTerm(e.target.value);
        if(searchSide)searchSide.oninput=e=>updateTerm(e.target.value);
        const sortControl=genericView.querySelector('[data-workspace-sort]');
        if(sortControl)sortControl.onchange=e=>{sort=e.target.value;refresh();};
        const statusControl=genericView.querySelector('[data-workspace-status-side]');
        if(statusControl)statusControl.onchange=e=>{
            filter=e.target.value;
            genericView.querySelectorAll('[data-workspace-filter]').forEach(x=>x.classList.toggle('active',x.dataset.workspaceFilter===filter));
            refresh();
        };
        genericView.querySelector('.workspace-clear-filters').onclick=()=>{
            filter='all';term='';sort='name';
            if(search)search.value='';
            if(searchSide)searchSide.value='';
            if(sortControl)sortControl.value='name';
            if(statusControl)statusControl.value='all';
            genericView.querySelectorAll('[data-workspace-filter]').forEach(x=>x.classList.toggle('active',x.dataset.workspaceFilter==='all'));
            refresh();
        };

        genericView.querySelectorAll('[data-open-workspace]').forEach(button=>{
            button.onclick=async()=>{
                await selectWorkspace(button.dataset.openWorkspace,button.dataset.workspaceName);
            };
        });
        genericView.querySelectorAll('[data-workspace-admin]').forEach(button=>{
            button.onclick=()=>window.studioNavigate('admin');
        });
        genericView.querySelectorAll('[data-nav-quick]').forEach(button=>{
            button.onclick=()=>window.studioNavigate(button.dataset.navQuick);
        });
    }

    async function renderAdmin(){
        conversationView.classList.add('hidden');
        genericView.classList.remove('hidden');
        genericView.innerHTML='<div class="mywork-loading">Loading administration…</div>';

        const me=await json('/api/auth/me').catch(()=>null);
        const isAdministrator=me?.user?.isAdministrator===true||me?.isAdministrator===true;
        if(!isAdministrator){
            genericView.innerHTML='<section class="admin-page"><header class="admin-page-header"><div><h1>Admin</h1><p>Studio configuration and access management.</p></div></header><section class="admin-card"><h2>Administrator access required</h2><p class="muted">Your account does not have permission to administer Studio.</p></section></section>';
            return;
        }

        workspaceAccessCache=await json('/api/workspace-access').catch(()=>[]);
        studioRolesCache=await json('/api/security/roles').catch(()=>[]);
        const manageable=workspaceAccessCache.filter(item=>item.permissions?.editWorkspace||item.permissions?.manageMembers||item.permissions?.adminWorkspace);

        genericView.innerHTML=`
            <section class="admin-page">
                <header class="admin-page-header">
                    <div><h1>Admin</h1><p>Configure Studio, workspaces, access, roles and workflow automation.</p></div>
                    <span class="admin-status-pill">Administrator</span>
                </header>

                <div class="admin-summary-grid">
                    <article class="admin-summary-card"><strong>${manageable.length}</strong><span>Managed Workspaces</span><small>Membership and routing</small></article>
                    <article class="admin-summary-card"><strong>${studioRolesCache.filter(x=>x.isActive).length}</strong><span>Active Roles</span><small>Reusable permission sets</small></article>
                    <article class="admin-summary-card"><strong>Ready</strong><span>Demo Environment</span><small>Deterministic seed data</small></article>
                </div>

                <div class="admin-layout">
                    <div class="admin-main-column">
                        <section class="admin-card admin-demo-card" id="studioDemoLoader">
                            <header class="admin-card-header">
                                <div><h2>Demo Data</h2><p>Load the complete working-studio demonstration dataset.</p></div>
                                <button class="primary" type="button" data-load-demo>Load Demo Data</button>
                            </header>
                            <p class="admin-card-note">Creates interconnected workspaces, clients, jobs, schedules, photographers, payments, media, production states, messages and history.</p>
                            <progress data-demo-progress max="100" value="0" aria-label="Demo data loading progress" hidden></progress>
                            <p class="admin-demo-status" data-demo-status role="status" aria-live="polite"></p>
                            <small data-demo-elapsed hidden></small>
                        </section>

                        <section class="admin-card">
                            <header class="admin-card-header"><div><h2>Workspaces</h2><p>Manage workspace routing, membership and permissions.</p></div></header>
                            <div class="admin-workspace-list">
                                ${manageable.length?manageable.map(item=>renderWorkspaceAdminRow(item.workspace,item.permissions,true)).join(''):'<p class="muted">No manageable workspaces are available.</p>'}
                            </div>
                        </section>

                        <section id="workspaceMemberPanel" class="admin-card job-section hidden"></section>
                        <section id="workspaceImportPanel" class="admin-card job-section hidden"></section>

                        <div class="admin-generated-sections">
                            ${renderSecurityRolesSection()}
                            ${renderWorkflowSection()}
                        </div>
                    </div>

                    <aside class="admin-side-column">
                        <section class="admin-card">
                            <header class="admin-card-header"><div><h2>Create Workspace</h2><p>Add a new operational workspace and its manager.</p></div></header>
                            <div class="admin-form-stack">
                                <label>Name<input id="workspaceName"></label>
                                <label>Email address<input id="workspaceEmail" type="email"></label>
                                <label>Manager user name<input id="workspaceManagerUser" placeholder="domain user name"></label>
                                <label>Manager display name<input id="workspaceManagerDisplay"></label>
                                <details><summary>Advanced / Integration Settings</summary><label>Route key<input id="workspaceRouteKey" placeholder="Generated automatically"></label><small>Used by website booking and incoming-message integrations. Leave blank to generate automatically.</small></details>
                                <button class="primary" type="button" onclick="createWorkspace()">Create Workspace</button>
                            </div>
                        </section>

                        <section class="admin-card" data-service-status><header class="admin-card-header"><h2>Service health</h2><button class="action" data-refresh-service-status>Refresh</button></header><div data-service-status-body>Loading…</div><button class="action" onclick="showPosCatalogManager()">Manage products</button><button class="action" data-service-audit>Recent audit</button></section>
                        <section class="admin-card admin-help-card">
                            <header class="admin-card-header"><div><h2>Administration</h2></div></header>
                            <p>Workspace access remains membership-based. Use reusable roles where possible and one-off permissions only when required.</p>
                            <p>Every operational workspace requires a Workspace Manager. Integration routing is configured under Advanced / Integration Settings.</p>
                        </section>
                    </aside>
                </div>
            </section>`;

        const statusPanel=document.querySelector('[data-service-status]');
        if(statusPanel){
            const readStatus=async()=>{const body=statusPanel.querySelector('[data-service-status-body]');try{const s=await json('/api/admin/studio-status');body.innerHTML=`<p>Database: ${esc(s.database)}</p><p>Storage: ${esc(s.storage.provider)} · ${s.storage.available?'Available':'Unavailable'} · ${s.storage.writable?'Writable':'Read only'}</p><p>Previews: ${s.previews.queued} queued · ${s.previews.failed} retrying · ${s.previews.unsupported} unsupported</p><p>Messaging: ${s.messaging.queuedEmails} queued emails · ${s.messaging.failedNotifications} failed notifications</p><p>Gallery: ${s.galleryConfigured?'Configured':'Unconfigured'} · ${esc(s.diagnostics)}</p>`;}catch(e){body.textContent=e.message;}};
            statusPanel.querySelector('[data-refresh-service-status]').onclick=readStatus;
            statusPanel.querySelector('[data-service-audit]').onclick=async()=>{const dialog=studioDialog('Recent operational audit','<p>Loading…</p>');try{const rows=await json('/api/admin/operations/audit?count=100');dialog.querySelector('.studio-dialog-body').textContent=JSON.stringify(rows,null,2);}catch(e){dialog.querySelector('.studio-dialog-body').textContent=e.message;}};
            readStatus();
        }
        await hydrateWorkspaceManagers(manageable);
        await refreshWorkflowList();
        await renderGlobalRoleAdministration();
        await renderAnnouncementAdministration();

        const panel=document.getElementById('studioDemoLoader');
        panel.querySelector('[data-load-demo]').onclick=async()=>{
            const button=panel.querySelector('[data-load-demo]');
            const status=panel.querySelector('[data-demo-status]');
            const meter=panel.querySelector('[data-demo-progress]'),elapsed=panel.querySelector('[data-demo-elapsed]');
            button.disabled=true;panel.setAttribute('aria-busy','true');
            meter.hidden=false;meter.value=0;elapsed.hidden=false;
            status.textContent='Starting demo loader…';
            const started=Date.now();
            const tick=()=>{elapsed.textContent='Elapsed: '+Math.floor((Date.now()-started)/1000)+' seconds. Progress follows completed seed steps; photo downloads may pause the meter.';};
            tick();const timer=setInterval(tick,1000);
            try{
                const result=await window.studioLoadDemoData(progress=>{
                    meter.value=progress.percent;
                    meter.setAttribute('aria-valuetext',progress.percent+'% — '+progress.stage);
                    status.textContent=progress.completed?progress.message:progress.percent+'% · '+progress.stage+' — '+progress.message;
                });
                status.textContent=result.message||'Demo data loaded.';
                try{await loadWorkspaces();}catch{status.textContent+=' Workspace refresh failed; reopen Workspaces to refresh.';}
            }catch(error){
                status.textContent='Demo data could not be loaded: '+(error.message||error);
            }finally{
                clearInterval(timer);tick();panel.setAttribute('aria-busy','false');button.disabled=false;
            }
        };
    }

    const routes={
        mywork:renderMyWork,
        messages:async()=>{conversationView.classList.remove('hidden');genericView.classList.add('hidden');await loadWorkspaces();await loadConversations();},
        schedule:()=>showPipeline(),
        tasks:renderMyWork,
        workspaces:renderWorkspaces,
        pipeline:()=>showPipeline(),
        production:()=>showProduction(),
        frontdesk:()=>showFrontDesk(),
        finance:()=>showAccounts(),
        reports:()=>showManagement(),
        management:()=>showManagement(),
        admin:renderAdmin
    };

    window.studioNavigate=async function(view){
        const route=routes[view];
        if(!route) throw new Error('Unknown Studio navigation view: '+view);
        const revision=++navigationRevision;
        myWorkLoad?.abort();
        setActive(view);
        try{
            await route();
            if(revision===navigationRevision)setActive(view);
        }catch(error){
            if(revision!==navigationRevision)return;
            conversationView.classList.add('hidden');
            genericView.classList.remove('hidden');
            genericView.innerHTML='<div class="studio-page-heading"><h1>Unable to load view</h1></div><div class="studio-card"><p>'+esc(error?.message||error)+'</p></div>';
            throw error;
        }
    };

    document.querySelectorAll('[data-view]').forEach(button=>{
        button.type='button';
        button.onclick=()=>window.studioNavigate(button.dataset.view);
    });

    const topNew=document.getElementById('topNewButton');
    if(topNew) topNew.onclick=async()=>{topNew.disabled=true;try{await loadWorkspaces();document.getElementById('newRequestDialog')?.showModal();}catch(error){alert(error.message);}finally{topNew.disabled=false;}};

    const startStudio=async()=>{
        window.studioNavigate('mywork').catch(error=>{
            console.error('Studio startup navigation failed.',error);
        });
    };
    if(document.readyState==='loading') document.addEventListener('DOMContentLoaded',startStudio,{once:true});
    else startStudio();
})();
