(() => {
    const conversationView=document.getElementById('conversationView');
    const genericView=document.getElementById('genericView');
    const studioBreadcrumb=document.getElementById('studioBreadcrumb');
    const studioPageTools=document.getElementById('studioPageTools');

    function setActive(view){
        if(view!=='today' && studioBreadcrumb && studioPageTools && studioBreadcrumb.parentElement!==studioPageTools.parentElement){
            studioPageTools.parentElement.insertBefore(studioBreadcrumb,studioPageTools);
        }
        document.body.dataset.studioView=view;
        if(view!=='today' && view!=='jobs'){const count=document.getElementById('studioFooterRate');if(count)count.textContent='';}
        document.querySelectorAll('[data-view]').forEach(button=>{
            button.classList.toggle('active',button.dataset.view===view);
        });
    }

    let todayLoad=null;
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


    function renderToday(){
        const controller=new AbortController(),revision=navigationRevision;
        const state={auth:{},jobs:[],tasks:[],schedule:[],notifications:[],media:[],announcements:[],status:{},errors:{},taskFilter:'all',jobView:'involved',jobSearch:'',jobStatusFilter:'',jobRoleFilter:'',selectedDate:new Date(),calendarMonth:new Date(),calendarView:'day',workspaceName:''};
        state.active=()=>{if(state.root&&!state.root.isConnected)controller.abort();return !controller.signal.aborted&&revision===navigationRevision&&['today','tasks'].includes(document.body.dataset.studioView)&&!genericView.classList.contains('hidden');};
        todayLoad=controller;
        conversationView.classList.add('hidden');genericView.classList.remove('hidden');
        const started=performance.now(),run=globalThis.crypto?.randomUUID?.()||String(Date.now());
        const diagnostics={run,startedAt:new Date().toISOString(),requests:[],shellMs:null,coreMs:null,completeMs:null};
        window.studioTodayDiagnostics=diagnostics;
        const now=new Date();
        const dayRange=date=>{
            const from=new Date(date.getFullYear(),date.getMonth(),date.getDate());
            const to=new Date(date.getFullYear(),date.getMonth(),date.getDate()+1);
            return {from,to};
        };
        const scheduleRange=(date,view=state.calendarView)=>{
            if(view==='month'){
                return {
                    from:new Date(date.getFullYear(),date.getMonth(),1),
                    to:new Date(date.getFullYear(),date.getMonth()+1,1)
                };
            }
            if(view==='week'){
                const from=new Date(date.getFullYear(),date.getMonth(),date.getDate());
                from.setDate(from.getDate()-((from.getDay()+6)%7));
                return {from,to:new Date(from.getFullYear(),from.getMonth(),from.getDate()+7)};
            }
            return dayRange(date);
        };
        state.scheduleRange=scheduleRange;
        const scheduleUrl=(date,view=state.calendarView)=>{
            const {from,to}=scheduleRange(date,view);
            return '/api/today/schedule?from='+encodeURIComponent(from.toISOString())+'&to='+encodeURIComponent(to.toISOString());
        };
        const urls={tasks:'/api/today/tasks',schedule:scheduleUrl(state.selectedDate,state.calendarView),jobs:'/api/today/jobs',auth:'/api/auth/me',notifications:'/api/notifications?count=80',media:'/api/today/media-summary'};
        let inFlight=0;const waiting=[];
        async function load(key){
            while(inFlight>=2)await new Promise(resolve=>waiting.push(resolve));
            if(!state.active())return;
            inFlight++;state.status[key]='loading';delete state.errors[key];
            try{paintToday(state);}catch(renderError){console.error('[Studio Today] Render failed before '+key+' request',renderError);}
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
                console.debug('[Studio Today]',diagnostics.requests.at(-1));
                if(state.active()){
                    try{paintToday(state);}
                    catch(renderError){console.error('[Studio Today] Render failed after '+key+' request',renderError);}
                }
            }
        }
        state.retry=key=>load(key);
        state.resolveWorkspace=async()=>{
            try{
                if(typeof loadWorkspaces==='function'&&(!Array.isArray(workspaceCache)||!workspaceCache.length))await loadWorkspaces();
                const available=(typeof workspaceCache!=='undefined'?workspaceCache:[]).filter(w=>w.permissions?.accessWorkspace&&w.permissions?.view);
                const workspace=(typeof selectedWorkspace!=='undefined'&&selectedWorkspace)
                    ? available.find(w=>String(w.id)===String(selectedWorkspace))
                    : null;
                state.workspaceName=workspace?.name||'';
                const announcementWorkspaces=workspace?[workspace]:available.slice(0,6);
                const results=await Promise.allSettled(announcementWorkspaces.map(async w=>{
                    const items=await json('/api/workspaces/'+w.id+'/announcements');
                    return (items||[]).map(a=>({...a,workspaceId:w.id,workspaceName:w.name}));
                }));
                state.announcements=results.flatMap(r=>r.status==='fulfilled'?r.value:[]);
                state.status.announcements='ready';
                if(state.active())paintToday(state);
            }catch{
                state.workspaceName='';
                state.announcements=[];
                state.status.announcements='error';
                if(state.active())paintToday(state);
            }
        };
        state.selectDay=date=>{
            state.selectedDate=new Date(date.getFullYear(),date.getMonth(),date.getDate());
            state.calendarMonth=new Date(date.getFullYear(),date.getMonth(),1);
            urls.schedule=scheduleUrl(state.selectedDate,state.calendarView);
            state.schedule=[];
            state.status.schedule='loading';
            paintToday(state);
            load('schedule');
        };
        state.setCalendarView=view=>{
            if(!['month','week','day'].includes(view)||view===state.calendarView)return;
            state.calendarView=view;
            state.calendarMonth=new Date(state.selectedDate.getFullYear(),state.selectedDate.getMonth(),1);
            urls.schedule=scheduleUrl(state.selectedDate,state.calendarView);
            state.schedule=[];
            state.status.schedule='loading';
            paintToday(state);
            load('schedule');
        };
        state.stepCalendar=direction=>{
            const current=new Date(state.selectedDate||new Date());
            if(state.calendarView==='month'){
                const desiredDay=current.getDate();
                const target=new Date(current.getFullYear(),current.getMonth()+direction,1);
                const lastDay=new Date(target.getFullYear(),target.getMonth()+1,0).getDate();
                target.setDate(Math.min(desiredDay,lastDay));
                state.selectDay(target);
                return;
            }
            current.setDate(current.getDate()+direction*(state.calendarView==='week'?7:1));
            state.selectDay(current);
        };
        try{paintToday(state);}catch(error){
            console.error('[Studio Today] Initial render failed',error);
            genericView.innerHTML='<section class="mw-empty" role="alert">Today could not render. Check the browser console for the error and reload.</section>';
        }
        state.resolveWorkspace();
        diagnostics.shellMs=Math.round(performance.now()-started);
        // Two reads at a time: operational essentials first, enrichment afterwards.
        const pending=['jobs','tasks','schedule','auth','notifications','media'];
        async function worker(){
            while(pending.length&&state.active()){
                await load(pending.shift());
                if(diagnostics.coreMs===null&&['tasks','schedule'].every(k=>['ready','error'].includes(state.status[k])))diagnostics.coreMs=Math.round(performance.now()-started);
            }
        }
        window.studioTodayReady=Promise.all([worker(),worker()]).then(()=>{
            diagnostics.completeMs=Math.round(performance.now()-started);
            diagnostics.cancelled=controller.signal.aborted;
            console.info('[Studio Today startup]',diagnostics);
        });
        return Promise.resolve();
    }

    function paintToday(state){
        if(!state.active())return;
        const {auth}=state;
        const jobs=Array.isArray(state.jobs)?state.jobs:[];
        const tasks=Array.isArray(state.tasks)?state.tasks:[];
        const schedule=Array.isArray(state.schedule)?state.schedule:[];
        const notifications=Array.isArray(state.notifications)?state.notifications:[];
        const workspaceCrumb=state.workspaceName
            ? '<span aria-hidden="true">›</span><button type="button" class="workspace-current" data-current-workspace>'+esc(state.workspaceName)+'</button>'
            : '';
        const now=new Date(),selectedDate=state.selectedDate||now;
        const activeScheduleRange=state.scheduleRange?state.scheduleRange(selectedDate,state.calendarView):{from:new Date(selectedDate.getFullYear(),selectedDate.getMonth(),selectedDate.getDate()),to:new Date(selectedDate.getFullYear(),selectedDate.getMonth(),selectedDate.getDate()+1)};
        const startOfDay=activeScheduleRange.from,endOfDay=activeScheduleRange.to;
        const phase=(key,content)=>state.status[key]==='ready'?content:state.status[key]==='error'?'<div class="mw-empty" role="alert">'+esc(state.errors[key])+' <button data-mw-retry="'+key+'">Retry</button></div>':'<div class="mw-empty" role="status">Loading…</div>';
        const metric=(key,value)=>state.status[key]==='ready'?value:'…';
        const me=auth.user||auth,user=String(me.userName||me.loginName||'').toLowerCase();
        const displayName=String(me.displayName||me.name||me.userName||'Jordan').trim();
        const firstName=(displayName.split(/[\s\\@.]+/).filter(Boolean)[0]||'Jordan');
        const hour=now.getHours(),greeting=hour<12?'Good morning':hour<18?'Good afternoon':'Good evening';
        const roles=[...(Array.isArray(me.roles)?me.roles:[]),...(Array.isArray(auth.roles)?auth.roles:[])].map(x=>String(x).toLowerCase());
        const isAdmin=me.isAdministrator===true||auth.isAdministrator===true||roles.some(r=>/administrator/.test(r));
        const isManager=isAdmin||roles.some(r=>/manager|owner|finance/.test(r));
        const myTasks=tasks.filter(t=>!['Completed','Cancelled'].includes(t.status)&&(!user||String(t.assignedTo||'').toLowerCase()===user));
        const todaySchedule=schedule.filter(entry=>entry.status!=='Cancelled'&&new Date(entry.startsAtUtc)>=startOfDay&&new Date(entry.startsAtUtc)<endOfDay).sort((a,b)=>new Date(a.startsAtUtc)-new Date(b.startsAtUtc));
        const leadJobs=jobs.filter(j=>!['Completed','Closed','Cancelled','Published'].includes(String(j.status||''))&&user&&String(j.jobLead||'').toLowerCase()===user);
        const recentJobs=[...leadJobs].sort((a,b)=>new Date(b.createdAtUtc||b.createdAt||0)-new Date(a.createdAtUtc||a.createdAt||0)).slice(0,4);
        const normalized=value=>String(value||'').trim().toLowerCase();
        const isLead=job=>Boolean(user)&&normalized(job.jobLead)===user;
        const isInvolved=job=>isLead(job)
            ||Boolean(user)&&[job.assignedTo,job.jobOwner,job.photographerUserName].some(value=>normalized(value)===user)
            ||job.isCrewMember===true
            ||myTasks.some(task=>String(task.jobId)===String(job.id))
            ||(Array.isArray(job.crew)&&job.crew.some(member=>normalized(member.userName||member)===user));
        const jobViews={led:'Jobs Led By Me',involved:"Jobs I'm Involved In",all:'All Jobs'};
        const viewJobs=state.jobView==='led'?jobs.filter(isLead):state.jobView==='all'?jobs:jobs.filter(isInvolved);
        const searchedJobs=viewJobs.filter(job=>[job.title,job.clientName,job.clientDisplayName,job.status,job.jobLead,job.id].some(value=>normalized(value).includes(normalized(state.jobSearch))));
        const roleForJob=job=>isLead(job)?'Job Lead':normalized(job.assignedTo)===user?'Assigned':myTasks.some(t=>String(t.jobId)===String(job.id))?'Task Owner':'Team';
        const statusOptions=[...new Set(viewJobs.map(job=>String(job.status||'Unknown')))].sort((a,b)=>a.localeCompare(b));
        const roleOptions=[...new Set(viewJobs.map(roleForJob))].sort((a,b)=>a.localeCompare(b));
        const filteredJobs=searchedJobs.filter(job=>(!state.jobStatusFilter||String(job.status||'Unknown')===state.jobStatusFilter)&&(!state.jobRoleFilter||roleForJob(job)===state.jobRoleFilter));
        const sortedJobs=[...filteredJobs].sort((a,b)=>new Date(b.createdAtUtc||0)-new Date(a.createdAtUtc||0));
        const footerCount=document.getElementById('studioFooterRate');
        if(footerCount)footerCount.textContent=sortedJobs.length+' Jobs';
        const emptyJobText=(state.jobSearch||state.jobStatusFilter||state.jobRoleFilter)?'No jobs match your filters':state.jobView==='led'?'You are not the Lead on any Jobs':state.jobView==='involved'?'You are not involved in any jobs':'No jobs found';
        const media=Array.isArray(state.media)?state.media:[];
        const announcements=Array.isArray(state.announcements)?state.announcements:[];
        const mediaByJob=new Map(media.map(row=>[String(row.jobId),row]));
        const mediaSnapshots=recentJobs.map(job=>({job,media:mediaByJob.get(String(job.id))||{assetCount:0}}));
        const totalPhotos=media.reduce((sum,row)=>sum+Number(row.assetCount||0),0);
        const messageNotifications=notifications.filter(n=>/message|conversation|reply/i.test(String(n.eventType||n.title||n.type||'')));
        const combinedCommunicationFeed=[
            ...messageNotifications.map(n=>({kind:'message',at:n.createdAtUtc||n.createdAt||0,item:n})),
            ...announcements.map(a=>({kind:'announcement',at:a.createdAtUtc||a.createdAt||0,item:a}))
        ].sort((a,b)=>new Date(b.at)-new Date(a.at)).slice(0,6);
        const activityNotifications=notifications.slice(0,8);

        const jobById=id=>jobs.find(j=>String(j.id)===String(id));
        const scheduleJob=entry=>jobById(entry.jobId)||{title:entry.title||'Scheduled session',clientName:entry.title||''};
        const formatTime=value=>new Date(value).toLocaleTimeString([],{hour:'numeric',minute:'2-digit'});
        const formatDay=value=>new Date(value).toLocaleDateString([],{month:'short',day:'numeric'});
        const imageForJob=jobId=>{
            const version=mediaByJob.get(String(jobId))?.thumbnailVersionId;
            return version?'/api/jobs/'+jobId+'/versions/by-id/'+version+'/preview?size=thumbnail':'';
        };
        const fallbackImageForJob=jobId=>{
            const version=mediaByJob.get(String(jobId))?.thumbnailVersionId;
            return version?'/api/jobs/'+jobId+'/versions/by-id/'+version+'/preview/fallback':'';
        };

        const sessionRows=todaySchedule.slice(0,5).map((entry,index)=>{
            const job=scheduleJob(entry);
            const image=entry.jobId?imageForJob(entry.jobId):'';
            const fallbackImage=entry.jobId?fallbackImageForJob(entry.jobId):'';
            const start=new Date(entry.startsAtUtc||entry.start||entry.startsAt);
            const end=new Date(entry.endsAtUtc||entry.end||entry.endsAt);
            const stateLabel=end<now?'Finished':start<=now?'In Progress':'Upcoming';
            return `<button class="mw-session-row ${index===0?'active':''}" data-open-job="${entry.jobId||job.id||''}">
                <div class="mw-session-thumb">${image?`<img loading="lazy" src="${image}" data-fallback-src="${fallbackImage}" alt="">`:'<span>📷</span>'}</div>
                <div class="mw-session-main"><strong>${esc(job.title||entry.title||'Session')}</strong><span>${esc(job.clientName||entry.title||'Photography session')}</span></div>
                <span class="mw-session-location">⌖ &nbsp;${esc(entry.location||'Location not set')}</span>
                <div class="mw-session-time"><strong>${formatTime(start)} – ${formatTime(end)}</strong></div>
                <span class="mw-state-pill ${stateLabel==='In Progress'?'in-progress':''}">${stateLabel}</span>
                <span class="mw-today-open-job" aria-hidden="true" title="Open job">↗</span>
            </button>`;
        }).join('')||'<div class="mw-empty">No sessions scheduled in this '+esc(state.calendarView||'day')+'.</div>';

        const taskRows=myTasks.slice(0,8).map(task=>{
            const job=jobById(task.jobId)||{};
            const context=[job.title,job.clientName].filter(Boolean).join(' · ')||task.activityType||'Studio task';
            const due=task.dueAtUtc?new Date(task.dueAtUtc):null;
            const dueDay=due?new Date(due.getFullYear(),due.getMonth(),due.getDate()):null;
            const todayDay=new Date(now.getFullYear(),now.getMonth(),now.getDate());
            const dueState=!dueDay?'none':dueDay<todayDay?'overdue':dueDay.getTime()===todayDay.getTime()?'today':'upcoming';
            const priority=String(task.priority||'Normal').toLowerCase();
            return `<button class="mw-task-row due-${dueState}" data-open-job="${task.jobId}" data-task-type="${esc(task.activityType||'')}">
                <span class="mw-task-bullet priority-${esc(priority)}" aria-hidden="true"></span>
                <span class="mw-two-line"><strong>${esc(task.title)}</strong><small>${esc(context)}</small></span>
                <span class="mw-due">${due?'<b>'+esc(dueState==='overdue'?'Overdue':dueState==='today'?'Today':'Due')+'</b><small>'+esc(formatDay(task.dueAtUtc))+'</small>':'<b>Open</b>'}</span>
                <span class="mw-chevron">›</span>
            </button>`;
        }).join('')||'<div class="mw-empty">No open tasks assigned to you.</div>';

        const messageRows=combinedCommunicationFeed.map(entry=>{
            if(entry.kind==='announcement'){
                const a=entry.item;
                return `<button class="mw-message-row announcement" data-announcement-workspace="${a.workspaceId||''}">
                    <span class="mw-comm-type announcement" title="Announcement" aria-label="Announcement">📣</span>
                    <span class="mw-message-copy"><strong>${esc(a.title||'Announcement')}</strong><small>${esc((a.workspaceName?a.workspaceName+' · ':'')+(a.body||''))}</small></span>
                    <span class="mw-message-time">${a.createdAtUtc?formatDay(a.createdAtUtc):''}</span>
                    <span class="mw-chevron">›</span>
                </button>`;
            }
            const n=entry.item;
            return `<button class="mw-message-row message" data-notification="${n.id}" data-message-job="${n.referenceType==='Job'?n.referenceId||'':''}">
                <span class="mw-comm-type message" title="Message" aria-label="Message">✉</span>
                <span class="mw-message-copy"><strong>${esc(n.subject||n.title||'Message')}</strong><small>${esc(n.body||n.eventType||'Studio message')}</small></span>
                <span class="mw-message-time">${n.createdAtUtc?formatDay(n.createdAtUtc):''}</span>
                <span class="mw-chevron">›</span>
            </button>`;
        }).join('')||'<div class="mw-empty">No messages or announcements.</div>';

        const recentRows=mediaSnapshots.slice(0,5).map(({job,media})=>{
            const image=imageForJob(job.id);
            const count=state.status.media==='ready'?media.assetCount:'…';
            const status=job.status||'Active';
            return `<button class="mw-pipeline-row" data-open-job="${job.id}">
                <span class="mw-pipeline-thumb">${image?`<img loading="lazy" src="${image}" alt="">`:'<span>📷</span>'}</span>
                <span class="mw-two-line"><strong>${esc(job.title)}</strong><small>${esc(job.clientName||job.jobType||'Photography')} · ${count} photos</small></span>
                <span class="mw-pipeline-status">${esc(status)}</span>
                <span class="mw-pipeline-due">${job.dueAtUtc?'Due '+formatDay(job.dueAtUtc):''}</span>
                <span class="mw-chevron">›</span>
            </button>`;
        }).join('')||'<div class="mw-empty">No active jobs currently led by you.</div>';

        const mainJobRows=sortedJobs.map(job=>{
            const image=imageForJob(job.id);
            const total=Number(job.taskCount||0),completed=Number(job.completedTaskCount||0);
            const percent=total?Math.round(100*completed/total):0;
            const role=roleForJob(job);
            return `<button class="mw-today-job-row" data-open-job="${esc(job.id)}">
                <span class="mw-today-job-title">${image?'<img src="'+image+'" alt="" loading="lazy">':'<span class="mw-today-job-placeholder">▣</span>'}<span><strong>${esc(job.title||'Untitled Job')}</strong><small>${esc(job.clientName||job.clientDisplayName||'Client')}</small></span></span>
                <span class="mw-today-job-stage"><span class="studio-status-pill" data-status="${esc(String(job.status||'Pending').toLowerCase().replace(/[^a-z0-9]+/g,'-'))}"><span class="studio-status-dot" aria-hidden="true"></span>${esc(job.status||'Pending')}</span></span>
                <span class="mw-today-job-progress"><span class="mw-today-job-meter"><i style="width:${percent}%"></i></span>${percent}%</span>
                <span class="mw-today-job-date">${esc(formatJobDate(job.eventDate))}</span>
                <span class="mw-today-job-role"><span class="studio-role-pill" data-role="${esc(role.toLowerCase().replace(/[^a-z0-9]+/g,'-'))}"><span class="studio-role-dot" aria-hidden="true"></span>${esc(role)}</span></span>
                <span class="mw-chevron">›</span>
            </button>`;
        }).join('')||'<div class="mw-today-jobs-empty" role="status">'+esc(emptyJobText)+'</div>';
        const overdueTasks=myTasks.filter(t=>t.dueAtUtc&&new Date(t.dueAtUtc)<now).length;
        const blockedTasks=myTasks.filter(t=>/blocked|rework|required|failed/i.test(String(t.status||''))).length;
        const schedulingJobs=leadJobs.filter(j=>/needs scheduling/i.test(String(j.status||''))).length;
        const uploadJobs=leadJobs.filter(j=>/awaiting upload/i.test(String(j.status||''))).length;
        const exceptionJobs=leadJobs.filter(j=>/blocked|overdue|exception/i.test(String(j.status||''))).length;
        const attentionJobs=schedulingJobs+uploadJobs+exceptionJobs;
        const productionTasks=myTasks.filter(t=>/edit|retouch|qc|print|deliver|production|gallery|select|design|color|review|upload|schedule/i.test(String(t.activityType||'')+' '+String(t.title||'')));
        const attentionItems=[...productionTasks,...myTasks.filter(t=>!productionTasks.includes(t))].slice(0,5);
        const attentionTotal=overdueTasks+blockedTasks+attentionJobs;

        const availableWorkspaces=(typeof workspaceCache!=='undefined'&&Array.isArray(workspaceCache)?workspaceCache:[])
            .filter(w=>w.permissions?.accessWorkspace&&w.permissions?.view);
        const workspaceRows=availableWorkspaces.map(w=>`<button type="button" data-mw-workspace="${esc(w.id)}">▣ <span>${esc(w.name)}</span></button>`).join('')
            ||'<div class="mw-empty">No accessible workspaces.</div>';
        const markup=`
            <section class="mw-page mw-today-page studio-dashboard-template">
                
                
                <div class="mw-today-layout studio-dashboard-template__columns">
                    <main class="mw-today-center studio-dashboard-template__primary">
                        <section class="mw-list-panel mw-today-jobs" data-mw-region="jobs">
                            <div class="mw-today-job-toolbar">
                                <div class="mw-today-view-tabs" role="group" aria-label="Job view">
                                    ${Object.entries(jobViews).map(([key,label])=>`<button type="button" data-mw-job-view="${key}" class="${state.jobView===key?'active':''}" aria-pressed="${state.jobView===key}">${label}</button>`).join('')}
                                </div>
                                <div class="mw-today-calendar-tools" aria-label="Calendar navigation"></div>
                                <label class="mw-today-search"><span class="sr-only">Search jobs</span><input type="search" data-mw-job-search placeholder="Search jobs, clients or status…" value="${esc(state.jobSearch)}"></label>
                            </div>
                            
                            <div class="mw-today-job-columns"><span>Job / Client</span><label class="mw-job-column-filter"><span>Status</span><select data-mw-job-status aria-label="Filter jobs by status"><option value="">All statuses</option>${statusOptions.map(value=>`<option value="${esc(value)}" ${state.jobStatusFilter===value?'selected':''}>${esc(value)}</option>`).join('')}</select></label><span>Progress</span><span>Event Date</span><label class="mw-job-column-filter"><span>My Role</span><select data-mw-job-role aria-label="Filter jobs by role"><option value="">All roles</option>${roleOptions.map(value=>`<option value="${esc(value)}" ${state.jobRoleFilter===value?'selected':''}>${esc(value)}</option>`).join('')}</select></label><span></span></div>
                            <div class="mw-today-job-list">${phase('jobs',mainJobRows)}</div>
                        </section>
                    </main>
                    <aside class="mw-today-right studio-dashboard-template__aside" aria-label="Daily activity">
                        <section class="mw-list-panel" data-mw-region="schedule"><header><h2><span class="studio-card-icon" aria-hidden="true">▦</span> Today's Sessions</h2><button data-nav="schedule">Calendar →</button></header><div class="mw-section-rows">${phase('schedule',sessionRows)}</div></section>
                        <section class="mw-list-panel" data-mw-region="attention"><header><h2><span class="studio-card-icon" aria-hidden="true">⚑</span> Needs Attention</h2><button data-nav="tasks">View All →</button></header><div class="mw-status-list">
                            <button class="mw-status-row warning" data-nav="tasks"><span class="studio-attention-label"><span aria-hidden="true">◷</span>Overdue tasks</span><strong>${metric('tasks',overdueTasks)}</strong></button>
                            <button class="mw-status-row warning" data-nav="schedule"><span class="studio-attention-label"><span aria-hidden="true">▦</span>Needs scheduling</span><strong>${metric('jobs',schedulingJobs)}</strong></button>
                            <button class="mw-status-row warning" data-nav="production"><span class="studio-attention-label"><span aria-hidden="true">⇧</span>Awaiting upload</span><strong>${metric('jobs',uploadJobs)}</strong></button>
                        </div></section>
                        <section class="mw-list-panel" data-mw-region="messages"><header><h2><span class="studio-card-icon" aria-hidden="true">✉</span> Messages & Announcements</h2><button data-nav="messages">View All →</button></header><div class="mw-compact-messages">${phase('notifications',messageRows)}</div><footer class="mw-messages-card-footer"><button type="button" data-nav="messages" class="mw-send-message-link"><span aria-hidden="true">✉</span> Send Message <span aria-hidden="true">→</span></button></footer></section>
                    </aside>
                </div>
            </section>`;
        const next=document.createElement('div');next.innerHTML=markup;
        if(!genericView.querySelector('.mw-page')){genericView.replaceChildren(next.firstElementChild);state.root=genericView.querySelector('.mw-page');}
        else if([...next.querySelectorAll('[data-mw-region]')].some(region=>!genericView.querySelector('[data-mw-region="'+region.dataset.mwRegion+'"]'))){
            console.warn('[Studio Today] Missing grid region; rebuilding Today');
            genericView.replaceChildren(next.firstElementChild);
            state.root=genericView.querySelector('.mw-page');
        }
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
        window.studioSetBreadcrumb?.([{label:'Today',view:'today'}]);
        if(studioPageTools){
            const today=new Date();
            const isToday=selectedDate.getFullYear()===today.getFullYear()
                && selectedDate.getMonth()===today.getMonth()
                && selectedDate.getDate()===today.getDate();
            const calendarView=state.calendarView||'day';
            const range=state.scheduleRange?state.scheduleRange(selectedDate,calendarView):{from:selectedDate,to:selectedDate};
            const inclusiveEnd=new Date(range.to);inclusiveEnd.setDate(inclusiveEnd.getDate()-1);
            const selectedDateLabel=calendarView==='month'
                ? selectedDate.toLocaleDateString([],{month:'long',year:'numeric'})
                : calendarView==='week'
                    ? range.from.toLocaleDateString([],{month:'short',day:'numeric'})+' – '+inclusiveEnd.toLocaleDateString([],{month:'short',day:'numeric'})
                    : isToday?'Today':selectedDate.toLocaleDateString([],{month:'short',day:'numeric'});
            const viewLabel=calendarView[0].toUpperCase()+calendarView.slice(1);
            studioPageTools.innerHTML=`
                <div class="mw-calendar-view-switch" role="group" aria-label="Calendar view">
                    ${['month','week','day'].map(view=>'<button type="button" data-mw-calendar-view="'+view+'" class="'+(calendarView===view?'active':'')+'" aria-pressed="'+String(calendarView===view)+'">'+view[0].toUpperCase()+view.slice(1)+'</button>').join('')}
                </div>
                <div class="mw-day-nav" aria-label="Today ${viewLabel.toLowerCase()} navigator">
                    <button type="button" class="mw-day-step" data-mw-day="-1" aria-label="Previous ${calendarView}">‹</button>
                    <button type="button" class="mw-day-current" data-mw-day="today" title="${isToday?'Today':'Return to today'}">${selectedDateLabel}</button>
                    <button type="button" class="mw-day-step" data-mw-day="1" aria-label="Next ${calendarView}">›</button>
                    <div class="mw-calendar-control">
                        <button type="button" class="mw-calendar-trigger" data-mw-calendar-toggle aria-label="Choose date" aria-expanded="false" title="Choose date">
                            <svg aria-hidden="true" width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="5" width="18" height="16" rx="2"/><path d="M7 3v4M17 3v4M3 10h18"/><path d="m9 15 2 2 4-4"/></svg>
                        </button>
                        <div class="mw-calendar-popover" data-mw-calendar-popover hidden></div>
                    </div>
                </div>`;
        }
        // Calendar navigation belongs to the Today content area, never the global masthead.
        const calendarHost=genericView.querySelector('.mw-today-calendar-tools');
        if(calendarHost && studioPageTools){
            calendarHost.replaceChildren(...Array.from(studioPageTools.children));
        }
        const shellScope=calendarHost||genericView;
        shellScope.querySelectorAll('[data-mw-calendar-view]').forEach(button=>button.onclick=()=>state.setCalendarView(button.dataset.mwCalendarView));
        shellScope.querySelectorAll('[data-mw-day]').forEach(button=>button.onclick=()=>{
            if(button.dataset.mwDay==='today')state.selectDay(new Date());
            else state.stepCalendar(Number(button.dataset.mwDay||0));
        });
        const calendarToggle=shellScope.querySelector('[data-mw-calendar-toggle]');
        const calendarPopover=shellScope.querySelector('[data-mw-calendar-popover]');
        const renderCalendar=()=>{
            if(!calendarPopover)return;
            const cursor=state.calendarMonth||new Date(state.selectedDate||new Date());
            const year=cursor.getFullYear(),month=cursor.getMonth();
            const first=new Date(year,month,1),last=new Date(year,month+1,0);
            const leading=(first.getDay()+6)%7;
            const today=new Date();
            const selected=state.selectedDate||today;
            const cells=[];
            for(let i=0;i<leading;i++)cells.push('<span class="mw-calendar-empty" aria-hidden="true"></span>');
            for(let day=1;day<=last.getDate();day++){
                const current=new Date(year,month,day);
                const isSelected=current.getFullYear()===selected.getFullYear()&&current.getMonth()===selected.getMonth()&&current.getDate()===selected.getDate();
                const isToday=current.getFullYear()===today.getFullYear()&&current.getMonth()===today.getMonth()&&current.getDate()===today.getDate();
                cells.push('<button type="button" class="mw-calendar-day'+(isSelected?' selected':'')+(isToday?' is-today':'')+'" data-calendar-day="'+day+'" aria-label="'+current.toLocaleDateString([],{weekday:'long',month:'long',day:'numeric',year:'numeric'})+'">'+day+'</button>');
            }
            calendarPopover.innerHTML=
                '<div class="mw-calendar-head"><button type="button" data-calendar-month="-1" aria-label="Previous month">‹</button><strong>'+cursor.toLocaleDateString([],{month:'long',year:'numeric'})+'</strong><button type="button" data-calendar-month="1" aria-label="Next month">›</button></div>'+
                '<div class="mw-calendar-week"><span>Mon</span><span>Tue</span><span>Wed</span><span>Thu</span><span>Fri</span><span>Sat</span><span>Sun</span></div>'+
                '<div class="mw-calendar-grid">'+cells.join('')+'</div>'+
                '<button type="button" class="mw-calendar-today" data-calendar-today>Today</button>';
            calendarPopover.querySelectorAll('[data-calendar-month]').forEach(button=>button.onclick=()=>{
                state.calendarMonth=new Date(year,month+Number(button.dataset.calendarMonth||0),1);
                renderCalendar();
            });
            calendarPopover.querySelectorAll('[data-calendar-day]').forEach(button=>button.onclick=()=>{
                calendarPopover.hidden=true;
                calendarToggle?.setAttribute('aria-expanded','false');
                state.selectDay(new Date(year,month,Number(button.dataset.calendarDay)));
            });
            calendarPopover.querySelector('[data-calendar-today]')?.addEventListener('click',()=>{
                calendarPopover.hidden=true;
                calendarToggle?.setAttribute('aria-expanded','false');
                state.selectDay(new Date());
            });
        };
        if(calendarToggle&&calendarPopover){
            calendarToggle.onclick=()=>{
                const opening=calendarPopover.hidden;
                calendarPopover.hidden=!opening;
                calendarToggle.setAttribute('aria-expanded',String(opening));
                if(opening){
                    state.calendarMonth=new Date(state.selectedDate.getFullYear(),state.selectedDate.getMonth(),1);
                    renderCalendar();
                }
            };
        }
        studioBreadcrumb?.querySelector('[data-shell-nav="today"]')?.addEventListener('click',()=>window.studioNavigate?.('today'));
        studioBreadcrumb?.querySelector('[data-current-workspace]')?.addEventListener('click',()=>{
            if(typeof selectedWorkspace!=='undefined'&&selectedWorkspace){
                const workspace=(typeof workspaceCache!=='undefined'?workspaceCache:[]).find(w=>String(w.id)===String(selectedWorkspace));
                if(workspace)selectWorkspace(selectedWorkspace,workspace.name);
            }
        });
        if(typeof renderWorkspaceMenu==='function')renderWorkspaceMenu();
        genericView.querySelectorAll('[data-mw-job-view]').forEach(button=>button.onclick=()=>{
            state.jobView=button.dataset.mwJobView;
            state.jobSearch='';
            paintToday(state);
        });
        genericView.querySelector('[data-mw-job-status]')?.addEventListener('change',event=>{state.jobStatusFilter=event.target.value;paintToday(state);});
        genericView.querySelector('[data-mw-job-role]')?.addEventListener('change',event=>{state.jobRoleFilter=event.target.value;paintToday(state);});
        genericView.querySelector('[data-mw-job-search]')?.addEventListener('input',event=>{
            state.jobSearch=event.target.value;
            const position=event.target.selectionStart;
            paintToday(state);
            const replacement=genericView.querySelector('[data-mw-job-search]');
            replacement?.focus();
            if(position!==null)replacement?.setSelectionRange(position,position);
        });
        genericView.querySelectorAll('[data-mw-workspace]').forEach(button=>button.onclick=()=>{
            const workspace=(typeof workspaceCache!=='undefined'?workspaceCache:[]).find(w=>String(w.id)===button.dataset.mwWorkspace);
            if(workspace)selectWorkspace(workspace.id,workspace.name);
        });
        genericView.querySelectorAll('[data-mw-retry]').forEach(b=>b.onclick=()=>{b.disabled=true;state.retry(b.dataset.mwRetry);});
        genericView.querySelector('[data-assign-job-lead]')?.addEventListener('click',async()=>{
            const activeJobs=jobs.filter(job=>!['Completed','Closed','Cancelled','Published'].includes(String(job.status||'')))
                .sort((a,b)=>String(a.title||'').localeCompare(String(b.title||'')));
            if(!activeJobs.length){alert('There are no active jobs available to assign.');return;}
            let employees;
            try{employees=await json('/api/studio-employees?assignable=true');}
            catch(error){alert(error.message);return;}
            const dialog=studioDialog('Assign Job Lead',`
                <form class="subject-form" data-job-lead-form>
                    <label>Job
                        <select name="jobId" required>
                            ${activeJobs.map(job=>'<option value="'+esc(job.id)+'">'+esc(job.title)+' · '+esc(job.clientName||'Client')+'</option>').join('')}
                        </select>
                    </label>
                    <label>Job Lead
                        <select name="userName">
                            <option value="">Unassigned</option>
                            ${(employees||[]).map(employee=>'<option value="'+esc(employee.userName)+'">'+esc(employee.displayName||employee.userName)+(employee.roles?.length?' · '+esc(employee.roles.join(', ')):'')+'</option>').join('')}
                        </select>
                    </label>
                    <p class="form-help">The selected employee must be an active member of the job or its workspace.</p>
                    <p role="alert"></p>
                    <div class="dialog-actions">
                        <button type="button" data-cancel>Cancel</button>
                        <button type="submit" class="primary">Assign Lead</button>
                    </div>
                </form>`);
            const form=dialog.querySelector('[data-job-lead-form]');
            form.querySelector('[data-cancel]').onclick=()=>dialog.close();
            form.onsubmit=async event=>{
                event.preventDefault();
                const submit=form.querySelector('button[type="submit"]');
                const error=form.querySelector('[role="alert"]');
                submit.disabled=true;error.textContent='';
                try{
                    const jobId=form.elements.jobId.value;
                    const userName=form.elements.userName.value||null;
                    const wf=await json('/api/jobs/'+encodeURIComponent(jobId)+'/connected-workflow');
                    const result=await json('/api/jobs/'+encodeURIComponent(jobId)+'/connected-workflow',{
                        method:'PUT',
                        headers:{'Content-Type':'application/json'},
                        body:JSON.stringify({action:'owner',userName,reason:'Today owner assignment',expectedRevision:wf.data.revision,operationId:crypto.randomUUID()})
                    });
                    state.jobs=state.jobs.map(job=>String(job.id)===String(jobId)?{...job,jobLead:result.jobLead??userName}:job);
                    dialog.close();
                    if(state.active())paintToday(state);
                }catch(errorValue){error.textContent=errorValue.message||String(errorValue);}
                finally{submit.disabled=false;}
            };
        });
        genericView.querySelectorAll('.mw-session-thumb img[data-fallback-src]').forEach(img=>{
            img.addEventListener('error',()=>{
                const fallback=img.dataset.fallbackSrc;
                if(!fallback||img.dataset.fallbackTried==='1')return;
                img.dataset.fallbackTried='1';
                const probe=new Image();
                probe.onload=()=>{img.src=fallback;img.classList.add('fallback-loaded');};
                probe.onerror=()=>{img.closest('.mw-session-thumb')?.classList.add('preview-missing');};
                probe.src=fallback;
            },{once:true});
        });
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
        genericView.querySelectorAll('[data-announcement-workspace]').forEach(button=>button.onclick=async()=>{
            const id=button.dataset.announcementWorkspace;
            const workspace=(typeof workspaceCache!=='undefined'?workspaceCache:[]).find(w=>String(w.id)===String(id));
            if(workspace)await selectWorkspace(workspace.id,workspace.name);
            else window.studioNavigate?.('workspaces');
        });
        genericView.querySelectorAll('[data-open-job]').forEach(button=>button.onclick=()=>{
            if(button.dataset.openJob)openWorkspaceJob(button.dataset.openJob).catch(error=>alert(error.message));
        });
        genericView.querySelectorAll('[data-mw-filter]').forEach(b=>b.classList.toggle('active',b.dataset.mwFilter===state.taskFilter));
        genericView.querySelectorAll('[data-task-type]').forEach(row=>row.hidden=state.taskFilter!=='all'&&!row.dataset.taskType.toLowerCase().includes(state.taskFilter));
        genericView.querySelectorAll('[data-nav]').forEach(button=>button.onclick=()=>window.studioNavigate(button.dataset.nav));
        const openNotifications=genericView.querySelector('#mwOpenNotifications');
        if(openNotifications)openNotifications.onclick=()=>document.getElementById('notificationButton')?.click();
    }

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
            const subtitle=latest?.clientName||item.workspace.emailAddress||item.workspace.routeKey||'Studio workspace';
            const secondary=latest?.title||latest?.jobType||'No recent job';
            const lastActivity=latest?.eventDate||latest?.createdAtUtc||latest?.createdAt;
            return `<article class="workspace-wow-row" data-workspace-row data-status="${item.metrics.activeJobs?'active':'inactive'}" data-search="${esc((item.workspace.name+' '+subtitle+' '+secondary+' '+status).toLowerCase())}">
                <button type="button" class="workspace-wow-main" data-open-workspace="${item.workspace.id}" data-workspace-name="${esc(item.workspace.name)}">
                    <span class="workspace-wow-thumb">${hero?`<img src="${hero}" alt="">`:'<span class="workspace-photo-fallback">📷</span>'}</span>
                    <span class="workspace-wow-copy">
                        <strong>${esc(item.workspace.name)}</strong>
                        <small>${esc(subtitle)} · ${esc(secondary)}</small>
                    </span>
                    <span class="workspace-wow-stat"><b>${item.metrics.activeJobs}</b><small>Active Jobs</small></span>
                    <span class="workspace-wow-stat"><b>${item.metrics.completedJobs}</b><small>Completed Jobs</small></span>
                    <span class="workspace-wow-stat"><b>${item.metrics.upcomingShoots}</b><small>Upcoming Shoots</small></span>
                    <span class="workspace-wow-previews">
                        ${previews.map(url=>`<img src="${url}" alt="">`).join('')}
                        ${photos>previews.length?`<i>+${photos-previews.length}</i>`:''}
                    </span>
                    <span class="workspace-wow-activity">
                        <em class="workspace-reference-pill ${statusClass}">${status}</em>
                        <small>${lastActivity?'Updated '+esc(new Date(lastActivity).toLocaleDateString([],{month:'short',day:'numeric'})):'No activity yet'}</small>
                    </span>
                    <span class="workspace-wow-arrow">›</span>
                </button>
            </article>`;
        }).join('');

        genericView.innerHTML=`
            <section class="workspace-list-page mw-list-page">
                <div class="mw-list-layout workspace-list-layout">
                    <section class="mw-list-panel workspace-center-panel">
                        <header class="workspace-center-header">
                            <div>
                                <h2><span class="mw-title-icon assigned">▰</span> Workspaces</h2>
                                <small>Jobs, clients, conversations and media grouped by operational workspace.</small>
                            </div>
                            <div class="workspace-center-actions"><button type="button" data-workspace-jobs>View Jobs →</button><span class="workspace-wow-list-count">${workspaceData.length}</span></div>
                        </header>

                        <div class="workspace-list-toolbar">
                            <div class="workspace-wow-search">
                                <span>⌕</span><input type="search" placeholder="Search workspaces…" data-workspace-search>
                            </div>
                            <select data-workspace-sort aria-label="Sort workspaces">
                                <option value="name">Name A–Z</option>
                                <option value="jobs">Most jobs</option>
                                <option value="photos">Most photos</option>
                            </select>
                        </div>

                        <div class="workspace-wow-list workspace-list-rows" data-workspace-list>${renderRows(workspaceData)}</div>
                    </section>

                    <aside class="mw-list-right workspace-right-rail">
                        <section class="mw-list-panel">
                            <header><h2><span class="mw-title-icon attention">▤</span> Overview</h2><small>Studio totals</small></header>
                            <div class="mw-status-list">
                                <div class="workspace-summary-row"><span>▥</span><span>Active Jobs</span><strong>${totals.activeJobs}</strong></div>
                                <div class="workspace-summary-row"><span>✓</span><span>Completed Jobs</span><strong>${totals.completedJobs}</strong></div>
                                <div class="workspace-summary-row"><span>▣</span><span>Upcoming Shoots</span><strong>${totals.upcomingShoots}</strong></div>
                                <div class="workspace-summary-row"><span>☑</span><span>Open Tasks</span><strong>${totals.openTasks}</strong></div>
                                <div class="workspace-summary-row"><span>◷</span><span>Overdue Tasks</span><strong>${totals.overdueTasks}</strong></div>
                                <div class="workspace-summary-row"><span>◆</span><span>Jobs Awaiting Production</span><strong>${totals.awaitingProduction}</strong></div>
                            </div>
                        </section>

                        <section class="mw-list-panel workspace-announcements-panel">
                            <header><h2><span class="mw-title-icon messages">✉</span> Announcements</h2><small>${announcements.length} recent</small></header>
                            <div class="workspace-announcement-rail">
                                ${announcements.length?announcements.map(a=>`<article class="workspace-wow-announcement"><strong>${esc(a.title)}</strong><p>${esc(a.body)}</p><small>${esc(a.workspaceName)} · ${a.createdAtUtc?new Date(a.createdAtUtc).toLocaleDateString([],{month:'short',day:'numeric'}):''}</small></article>`).join(''):'<p class="mw-empty">No workspace announcements.</p>'}
                            </div>
                        </section>

                        <section class="mw-list-panel workspace-help-panel">
                            <header><h2><span class="mw-title-icon links">↗</span> Navigation</h2></header>
                            <div class="mw-action-list">
                                <button type="button" data-nav-quick="today"><span>‹</span><span>Back to Today</span><b>›</b></button>
                                <button type="button" data-nav-quick="pipeline"><span>▥</span><span>View Pipeline</span><b>›</b></button>
                            </div>
                        </section>
                    </aside>
                </div>
            </section>`;

        window.studioSetBreadcrumb?.([{label:'Workspaces',view:'workspaces'}]);
        if(studioPageTools) studioPageTools.innerHTML='';


        const allRows=()=>[...genericView.querySelectorAll('[data-workspace-row]')];
        let term='';
        let sort='name';

        const refresh=()=>{
            const list=genericView.querySelector('[data-workspace-list]');
            const rows=allRows();
            rows.forEach(row=>{
                const matchesTerm=!term||row.dataset.search.includes(term);
                row.hidden=!matchesTerm;
            });

            rows.sort((a,b)=>{
                const ai=workspaceData.find(x=>String(x.workspace.id)===String(a.querySelector('[data-open-workspace]').dataset.openWorkspace));
                const bi=workspaceData.find(x=>String(x.workspace.id)===String(b.querySelector('[data-open-workspace]').dataset.openWorkspace));
                if(sort==='jobs')return (bi?.jobs.length||0)-(ai?.jobs.length||0);
                if(sort==='photos')return photoCount(bi||{media:[]})-photoCount(ai||{media:[]});
                return String(ai?.workspace.name||'').localeCompare(String(bi?.workspace.name||''));
            }).forEach(row=>list.appendChild(row));
        };

        const search=genericView.querySelector('[data-workspace-search]');
        const updateTerm=value=>{
            term=String(value||'').trim().toLowerCase();
            if(search&&search.value!==value)search.value=value;
            refresh();
        };
        if(search)search.oninput=e=>updateTerm(e.target.value);
        const sortControl=genericView.querySelector('[data-workspace-sort]');
        if(sortControl)sortControl.onchange=e=>{sort=e.target.value;refresh();};

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
            json,workspaces:workspaceCache||[],
            workspaceFilter:jobsWorkspaceFilter===null?(typeof selectedWorkspace!=='undefined'?selectedWorkspace||'':''):jobsWorkspaceFilter,
            setWorkspaceFilter:value=>{jobsWorkspaceFilter=value;},
            isCurrent,openJob:id=>openWorkspaceJob(id),navigate:view=>window.studioNavigate(view),
            sendMessage:async()=>{await window.studioNavigate('messages');if(document.body.dataset.studioView==='messages')document.getElementById('newDirectMessage')?.click();},
            openWorkspace:id=>selectWorkspace(id,(workspaceCache||[]).find(w=>String(w.id)===String(id))?.name||'Workspace'),
            retry:()=>renderJobs()
        });
    }

    window.studioOpenJobs=workspaceId=>{
        jobsWorkspaceFilter=workspaceId==null?'':String(workspaceId);
        return window.studioNavigate('jobs');
    };

    async function renderAdmin(){
        conversationView.classList.add('hidden');
        genericView.classList.remove('hidden');
        genericView.innerHTML='<div class="today-loading">Loading administration…</div>';

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
                    <article class="admin-summary-card"><strong>Pending</strong><span>Demo Reset</span><small>Connected Workflow fixture tools</small></article>
                </div>

                <div class="admin-layout">
                    <div class="admin-main-column">
                        <section class="admin-card admin-demo-card" id="studioDemoLoader">
                            <header class="admin-card-header">
                                <div><h2>Demo Data</h2><p>Generate workflow-native examples or remove the owned demo fixture.</p></div>
                                <div style="display:flex;gap:8px;flex-wrap:wrap"><button class="action" type="button" data-demo-preview>Preview Reset Scope</button><button class="action" type="button" data-demo-remove>Remove Demo Data</button><button class="primary" type="button" data-load-demo>Generate Demo Data</button><button class="primary" type="button" data-demo-reset>Reset and Regenerate</button></div>
                            </header>
                            <p class="admin-card-note">Removal requires server-side DemoData:AllowDestructiveReset and an explicit confirmation. Generation uses fictional contacts only.</p>
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
                            ${renderEmployeeDirectorySection()}
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
                                <label>Manager display name<input id="workspaceManagerDisplay"></label><label>Default Job Lead<select id="workspaceDefaultLead"><option value="">Use Workspace Manager</option></select></label>
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
        await refreshStudioEmployees();
        await refreshWorkflowList();
        await renderGlobalRoleAdministration();
        await renderAnnouncementAdministration();

        const panel=document.getElementById('studioDemoLoader');
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
        panel.querySelector('[data-demo-reset]').onclick=async()=>{
            const status=panel.querySelector('[data-demo-status]');
            const confirmation=window.prompt('This removes all Studio business records and regenerates the Connected Workflow demo. Type REMOVE STUDIO DEMO DATA to continue.');
            if(confirmation!=='REMOVE STUDIO DEMO DATA')return;
            const button=panel.querySelector('[data-demo-reset]');
            button.disabled=true;panel.setAttribute('aria-busy','true');
            status.textContent='Resetting and regenerating Studio demo data…';
            try{
                const result=await json('/api/admin/connected-workflow/demo-data/reset-and-generate',{
                    method:'POST',headers:{'Content-Type':'application/json'},
                    body:JSON.stringify({confirmation})
                });
                status.textContent=result.message||'Reset and regeneration completed.';
                await loadWorkspaces();
            }catch(e){status.textContent='Reset/regeneration failed: '+e.message;}
            finally{button.disabled=false;panel.setAttribute('aria-busy','false');}
        };
        panel.querySelector('[data-demo-remove]').onclick=async()=>{
            const status=panel.querySelector('[data-demo-status]');
            const confirmation=window.prompt('Type REMOVE STUDIO DEMO DATA to remove the Connected Workflow fixture. This cannot be undone.');
            if(confirmation!=='REMOVE STUDIO DEMO DATA')return;
            const button=panel.querySelector('[data-demo-remove]');
            button.disabled=true;
            status.textContent='Removing owned demo records…';
            try{
                const result=await json('/api/admin/connected-workflow/demo-data/remove',{
                    method:'POST',headers:{'Content-Type':'application/json'},
                    body:JSON.stringify({confirmation})
                });
                status.textContent=result.message||'Demo fixture removed.';
                await loadWorkspaces();
            }catch(e){status.textContent='Removal failed: '+e.message;}
            finally{button.disabled=false;}
        };
        panel.querySelector('[data-load-demo]').onclick=async()=>{
            const button=panel.querySelector('[data-load-demo]');
            const status=panel.querySelector('[data-demo-status]');
            button.disabled=true;status.textContent='Generating Connected Workflow records…';
            try{
                const result=await json('/api/admin/connected-workflow/demo-data/generate',{method:'POST'});
                status.textContent=result.message||'Connected Workflow demo generated.';
                await loadWorkspaces();
            }catch(e){status.textContent='Generation failed: '+e.message;}
            finally{button.disabled=false;}
        };

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

    const routes={
        today:renderToday,
        messages:async()=>{conversationView.classList.remove('hidden');genericView.classList.add('hidden');await loadWorkspaces();await loadConversations();},
        schedule:()=>showPipeline(),
        tasks:renderToday,
        workspaces:renderWorkspaces,
        jobs:renderJobs,
        pipeline:()=>showPipeline(),
        production:()=>showCatalogProduction(),
        'studio-management':()=>showStudioManagement(),
        catalog:()=>showCommercialCatalog(),
        'job-templates':()=>showCommercialTemplates('Job'),
        'work-order-templates':()=>showCommercialTemplates('WorkOrder'),
        media:()=>showMediaReview(),
        frontdesk:()=>showFrontDesk(),
        finance:()=>showAccounts(),
        reports:()=>showManagement(),
        management:()=>showManagement(),
        admin:renderAdmin
    };

    let currentNavigationView=document.body.dataset.studioView||'today';
    const navigationLabels={'studio-management':'Studio Management',catalog:'Product & Services Catalog','job-templates':'Job Templates','work-order-templates':'Work Order Templates',today:'Today',workspaces:'Workspaces',jobs:'Jobs',pipeline:'Pipeline',production:'Production',media:'Media',frontdesk:'Front Desk',finance:'Finance',admin:'Administration',reports:'Reports',management:'Management',messages:'Messages',tasks:'Today',schedule:'Pipeline'};

    function replaceNavigationHistory(view){
        const url='#studio='+encodeURIComponent(view);
        window.history.replaceState({studioView:view},'',url);
    }

    window.studioNavigate=async function(view,options={}){
        const route=routes[view];
        if(!route) throw new Error('Unknown Studio navigation view: '+view);
        const previous=currentNavigationView;
        if(!options.browserHistory&&options.history!==false&&previous!==view){
            window.history.pushState({studioView:view},'','#studio='+encodeURIComponent(view));
        }
        currentNavigationView=view;
        const revision=++navigationRevision;
        window.StudioJobsPage?.dispose();
        todayLoad?.abort();
        setActive(view);
        if(view!=='today'){
            if(studioPageTools)studioPageTools.innerHTML='';
            window.studioSetBreadcrumb?.([{label:view==='workspaces'?'Workspaces':view.replace(/(^|\s)\S/g,m=>m.toUpperCase()),view}]);
        }
        try{
            await route();
            if(revision===navigationRevision){
                setActive(view);
                if(view!=='today'&&typeof renderWorkspaceMenu==='function')renderWorkspaceMenu();
            }
        }catch(error){
            if(revision!==navigationRevision)return;
            conversationView.classList.add('hidden');
            genericView.classList.remove('hidden');
            genericView.innerHTML='<div class="studio-page-heading"><h1>Unable to load view</h1></div><div class="studio-card"><p>'+esc(error?.message||error)+'</p></div>';
            throw error;
        }
    };

    window.addEventListener('popstate',event=>{
        const view=event.state?.studioView||'today';
        if(view!==currentNavigationView)window.studioNavigate(view,{browserHistory:true,history:false}).catch(error=>console.error('Studio browser navigation failed.',error));
    });

    document.querySelectorAll('[data-view]').forEach(button=>{
        button.type='button';
        button.onclick=()=>window.studioNavigate(button.dataset.view);
    });

    const topNew=document.getElementById('topNewButton');
    if(topNew) topNew.onclick=async()=>{topNew.disabled=true;try{await loadWorkspaces();document.getElementById('newRequestDialog')?.showModal();}catch(error){alert(error.message);}finally{topNew.disabled=false;}};

    const startStudio=async()=>{
        const requested=new URLSearchParams(window.location.hash.replace(/^#/,'')).get('studio');
        const initial=requested&&routes[requested]?requested:'today';
        currentNavigationView=initial;
        replaceNavigationHistory(initial);
        window.studioNavigate(initial,{history:false}).catch(error=>{
            console.error('Studio startup navigation failed.',error);
        });
    };
    if(document.readyState==='loading') document.addEventListener('DOMContentLoaded',startStudio,{once:true});
    else startStudio();
})();

