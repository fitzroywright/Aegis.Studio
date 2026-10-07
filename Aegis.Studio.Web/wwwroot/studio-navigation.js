(() => {
    const conversationView=document.getElementById('conversationView');
    const genericView=document.getElementById('genericView');
    const studioBreadcrumb=document.getElementById('studioBreadcrumb');
    const studioPageTools=document.getElementById('studioPageTools');

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
        const state={auth:{},jobs:[],tasks:[],schedule:[],notifications:[],media:[],announcements:[],status:{},errors:{},taskFilter:'all',selectedDate:new Date(),calendarMonth:new Date(),workspaceName:''};
        state.active=()=>{if(state.root&&!state.root.isConnected)controller.abort();return !controller.signal.aborted&&revision===navigationRevision&&['mywork','tasks'].includes(document.body.dataset.studioView)&&!genericView.classList.contains('hidden');};
        myWorkLoad=controller;
        conversationView.classList.add('hidden');genericView.classList.remove('hidden');
        const started=performance.now(),run=globalThis.crypto?.randomUUID?.()||String(Date.now());
        const diagnostics={run,startedAt:new Date().toISOString(),requests:[],shellMs:null,coreMs:null,completeMs:null};
        window.studioMyWorkDiagnostics=diagnostics;
        const now=new Date();
        const dayRange=date=>{
            const from=new Date(date.getFullYear(),date.getMonth(),date.getDate());
            const to=new Date(date.getFullYear(),date.getMonth(),date.getDate()+1);
            return {from,to};
        };
        const scheduleUrl=date=>{
            const {from,to}=dayRange(date);
            return '/api/my-work/schedule?from='+encodeURIComponent(from.toISOString())+'&to='+encodeURIComponent(to.toISOString());
        };
        const urls={tasks:'/api/my-work/tasks',schedule:scheduleUrl(state.selectedDate),jobs:'/api/my-work/jobs',auth:'/api/auth/me',notifications:'/api/notifications?count=80',media:'/api/my-work/media-summary'};
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
                if(state.active())paintMyWork(state);
            }catch{
                state.workspaceName='';
                state.announcements=[];
                state.status.announcements='error';
                if(state.active())paintMyWork(state);
            }
        };
        state.selectDay=date=>{
            state.selectedDate=new Date(date.getFullYear(),date.getMonth(),date.getDate());
            state.calendarMonth=new Date(date.getFullYear(),date.getMonth(),1);
            urls.schedule=scheduleUrl(state.selectedDate);
            state.schedule=[];
            state.status.schedule='loading';
            paintMyWork(state);
            load('schedule');
        };
        paintMyWork(state);
        state.resolveWorkspace();
        diagnostics.shellMs=Math.round(performance.now()-started);
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
        const workspaceCrumb=state.workspaceName
            ? '<span aria-hidden="true">›</span><button type="button" class="workspace-current" data-current-workspace>'+esc(state.workspaceName)+'</button>'
            : '';
        const now=new Date(),selectedDate=state.selectedDate||now,startOfDay=new Date(selectedDate.getFullYear(),selectedDate.getMonth(),selectedDate.getDate()),endOfDay=new Date(selectedDate.getFullYear(),selectedDate.getMonth(),selectedDate.getDate()+1);
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
        const leadJobs=jobs.filter(j=>!['Completed','Closed','Cancelled','Published'].includes(String(j.status||''))&&user&&String(j.jobLead||'').toLowerCase()===user);
        const recentJobs=[...leadJobs].sort((a,b)=>new Date(b.createdAtUtc||b.createdAt||0)-new Date(a.createdAtUtc||a.createdAt||0)).slice(0,4);
        const mediaByJob=new Map(state.media.map(row=>[String(row.jobId),row]));
        const mediaSnapshots=recentJobs.map(job=>({job,media:mediaByJob.get(String(job.id))||{assetCount:0}}));
        const totalPhotos=state.media.reduce((sum,row)=>sum+row.assetCount,0);
        const messageNotifications=notifications.filter(n=>/message|conversation|reply/i.test(String(n.eventType||n.title||n.type||'')));
        const combinedCommunicationFeed=[
            ...messageNotifications.map(n=>({kind:'message',at:n.createdAtUtc||n.createdAt||0,item:n})),
            ...state.announcements.map(a=>({kind:'announcement',at:a.createdAtUtc||a.createdAt||0,item:a}))
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
                <span class="mw-chevron">›</span>
            </button>`;
        }).join('')||'<div class="mw-empty">No sessions scheduled today.</div>';

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
                    <span class="mw-comm-type announcement" title="Announcement" aria-label="Announcement">!</span>
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

        const overdueTasks=myTasks.filter(t=>t.dueAtUtc&&new Date(t.dueAtUtc)<now).length;
        const blockedTasks=myTasks.filter(t=>/blocked|rework|required|failed/i.test(String(t.status||''))).length;
        const schedulingJobs=leadJobs.filter(j=>/needs scheduling/i.test(String(j.status||''))).length;
        const uploadJobs=leadJobs.filter(j=>/awaiting upload/i.test(String(j.status||''))).length;
        const exceptionJobs=leadJobs.filter(j=>/blocked|overdue|exception/i.test(String(j.status||''))).length;
        const attentionJobs=schedulingJobs+uploadJobs+exceptionJobs;
        const productionTasks=myTasks.filter(t=>/edit|retouch|qc|print|deliver|production|gallery|select|design|color|review|upload|schedule/i.test(String(t.activityType||'')+' '+String(t.title||'')));
        const attentionItems=[...productionTasks,...myTasks.filter(t=>!productionTasks.includes(t))].slice(0,5);
        const attentionTotal=overdueTasks+blockedTasks+attentionJobs;

        const markup=`
            <section class="mw-page mw-list-page">
                <div class="mw-list-layout">
                    <div class="mw-list-summary">
                        <section class="mw-list-panel" data-mw-region="status">
                            <header><h2><span class="mw-title-icon status">▥</span> Status</h2><small>Assigned to ${esc(displayName)}</small></header>
                            <div class="mw-status-list">
                                <button class="mw-status-row active" data-nav="mywork"><span class="mw-status-icon">▤</span><span>Jobs I lead</span><strong>${metric('jobs',leadJobs.length)}</strong></button>
                                <button class="mw-status-row" data-nav="schedule"><span class="mw-status-icon">▣</span><span>Today's sessions</span><strong>${metric('schedule',todaySchedule.length)}</strong></button>
                                <button class="mw-status-row" data-nav="tasks"><span class="mw-status-icon">☑</span><span>Open tasks</span><strong>${metric('tasks',myTasks.length)}</strong></button>
                                <button class="mw-status-row warning" data-nav="production"><span class="mw-status-icon">▲</span><span>Needs attention</span><strong>${metric('tasks',attentionTotal)}</strong></button>
                                <button class="mw-status-row" data-nav="messages"><span class="mw-status-icon">✉</span><span>Unread messages</span><strong>${metric('notifications',messageNotifications.filter(n=>!n.isRead).length)}</strong></button>
                            </div>
                        </section>

                        <section class="mw-list-panel mw-action-panel" data-mw-region="actions">
                            <header><h2><span class="mw-title-icon actions">◆</span> Actions</h2></header>
                            <div class="mw-action-list">
                                <button data-nav="messages"><span>✉</span><span>Send Message</span><b>›</b></button>
                                <button data-nav="schedule"><span>▣</span><span>Book a Schedule</span><b>›</b></button>
                                <button data-nav="tasks"><span>＋</span><span>Create Task</span><b>›</b></button>
                            </div>
                        </section>

                        <section class="mw-list-panel mw-quick-links-panel" data-mw-region="quick-links">
                            <header><h2><span class="mw-title-icon links">↗</span> Quick Links</h2></header>
                            <div class="mw-quick-link-list">
                                <button data-nav="workspaces"><span>▰</span><span>Workspaces</span></button>
                                <button data-nav="pipeline"><span>▥</span><span>Pipeline</span></button>
                                <button data-nav="production"><span>◆</span><span>Production</span></button>
                                <button data-nav="reports"><span>▤</span><span>Reports</span></button>
                            </div>
                        </section>
                    </div>

                    <section class="mw-list-panel mw-assigned-panel">
                        <header class="mw-assigned-header">
                            <div><h2><span class="mw-title-icon assigned">▣</span> Assigned</h2><small>Sessions, tasks and jobs currently belonging to ${esc(displayName)}</small></div>
                        </header>

                        <div class="mw-work-section" data-mw-region="schedule">
                            <div class="mw-section-title"><h3>Today's Sessions</h3><span>${metric('schedule',todaySchedule.length)}</span></div>
                            <div class="mw-section-rows">${phase('schedule',sessionRows)}</div>
                        </div>

                        <div class="mw-work-section" data-mw-region="pipeline">
                            <div class="mw-section-title"><h3>Jobs I Lead</h3><span>${state.status.media==='error'?'<button class="action" data-mw-retry="media">Retry photos</button>':''}<button data-nav="pipeline">View all →</button></span></div>
                            <div class="mw-section-rows">${phase('jobs',recentRows)}</div>
                        </div>

                    </section>

                    <aside class="mw-list-right">
                        <section class="mw-list-panel" data-mw-region="attention">
                            <header><h2><span class="mw-title-icon attention">▲</span> Needs Attention</h2><small>${metric('tasks',attentionTotal)} items</small></header>
                            <div class="mw-status-list">
                                <button class="mw-status-row warning" data-nav="tasks"><span class="mw-status-icon">◷</span><span>Overdue tasks</span><strong>${metric('tasks',overdueTasks)}</strong></button>
                                <button class="mw-status-row warning" data-nav="tasks"><span class="mw-status-icon">!</span><span>Blocked / rework</span><strong>${metric('tasks',blockedTasks)}</strong></button>
                                <button class="mw-status-row warning" data-nav="schedule"><span class="mw-status-icon">▣</span><span>Needs scheduling</span><strong>${metric('jobs',schedulingJobs)}</strong></button>
                                <button class="mw-status-row warning" data-nav="production"><span class="mw-status-icon">⇧</span><span>Awaiting upload</span><strong>${metric('jobs',uploadJobs)}</strong></button>
                                <button class="mw-status-row warning" data-nav="production"><span class="mw-status-icon">◆</span><span>Production exceptions</span><strong>${metric('jobs',exceptionJobs)}</strong></button>
                            </div>
                        </section>

                        <section class="mw-list-panel mw-right-tasks-panel" data-mw-region="tasks">
                            <header><h2><span class="mw-title-icon tasks">☑</span> My Tasks</h2><small>${metric('tasks',myTasks.length)} open</small></header>
                            <div class="mw-right-tasks">${phase('tasks',taskRows)}</div>
                        </section>

                        <section class="mw-list-panel mw-messages-panel" data-mw-region="messages">
                            <header><h2><span class="mw-title-icon messages">✉</span> Messages &amp; Announcements</h2></header>
                            <div class="mw-compact-messages">${phase('notifications',messageRows)}</div>
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
        if(studioBreadcrumb){
            studioBreadcrumb.innerHTML=`
                <details class="workspace-menu breadcrumb-workspace-menu" id="workspaceMenu">
                    <summary class="workspace-breadcrumb-trigger"><span>Workspaces</span><span class="workspace-menu-caret">⌄</span></summary>
                    <div class="workspace-menu-popover" id="workspaceMenuList" role="menu" aria-label="Workspaces"></div>
                </details>
                ${workspaceCrumb}
                <span class="breadcrumb-separator" aria-hidden="true">›</span>
                <button type="button" data-shell-nav="mywork">My Work</button>
                <span class="breadcrumb-separator" aria-hidden="true">›</span>
                <strong>${esc(displayName)}</strong>
                <span class="breadcrumb-date">· ${selectedDate.toLocaleDateString([],{weekday:'short',month:'short',day:'numeric',year:'numeric'})}</span>`;
        }
        if(studioPageTools){
            const today=new Date();
            const isToday=selectedDate.getFullYear()===today.getFullYear()
                && selectedDate.getMonth()===today.getMonth()
                && selectedDate.getDate()===today.getDate();
            const selectedDateLabel=isToday
                ? 'Today'
                : selectedDate.toLocaleDateString([],{month:'short',day:'numeric'});
            studioPageTools.innerHTML=`
                <div class="mw-day-nav" aria-label="My Work day navigator">
                    <button type="button" class="mw-day-step" data-mw-day="-1" aria-label="Previous day">‹</button>
                    <button type="button" class="mw-day-current" data-mw-day="today" title="${isToday?'Today':'Return to today'}">${selectedDateLabel}</button>
                    <button type="button" class="mw-day-step" data-mw-day="1" aria-label="Next day">›</button>
                    <div class="mw-calendar-control">
                        <button type="button" class="mw-calendar-trigger" data-mw-calendar-toggle aria-label="Choose date" aria-expanded="false" title="Choose date">
                            <span aria-hidden="true">▣</span>
                        </button>
                        <div class="mw-calendar-popover" data-mw-calendar-popover hidden></div>
                    </div>
                </div>`;
        }
        const shellScope=studioPageTools||genericView;
        shellScope.querySelectorAll('[data-mw-day]').forEach(button=>button.onclick=()=>{
            if(button.dataset.mwDay==='today')state.selectDay(new Date());
            else{
                const next=new Date(state.selectedDate||new Date());
                next.setDate(next.getDate()+Number(button.dataset.mwDay||0));
                state.selectDay(next);
            }
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
        studioBreadcrumb?.querySelector('[data-shell-nav="mywork"]')?.addEventListener('click',()=>window.studioNavigate?.('mywork'));
        studioBreadcrumb?.querySelector('[data-current-workspace]')?.addEventListener('click',()=>{
            if(typeof selectedWorkspace!=='undefined'&&selectedWorkspace){
                const workspace=(typeof workspaceCache!=='undefined'?workspaceCache:[]).find(w=>String(w.id)===String(selectedWorkspace));
                if(workspace)selectWorkspace(selectedWorkspace,workspace.name);
            }
        });
        if(typeof renderWorkspaceMenu==='function')renderWorkspaceMenu();
        genericView.querySelectorAll('[data-mw-retry]').forEach(b=>b.onclick=()=>{b.disabled=true;state.retry(b.dataset.mwRetry);});
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
            const secondary=latest?.title||latest?.jobType||'No recent job';
            const lastActivity=latest?.eventDate||latest?.createdAtUtc||latest?.createdAt;
            return `<article class="workspace-wow-row" data-workspace-row data-status="${statusClass}" data-search="${esc((item.workspace.name+' '+subtitle+' '+secondary+' '+status).toLowerCase())}">
                <button type="button" class="workspace-wow-main" data-open-workspace="${item.workspace.id}" data-workspace-name="${esc(item.workspace.name)}">
                    <span class="workspace-wow-thumb">${hero?`<img src="${hero}" alt="">`:'<span class="workspace-photo-fallback">📷</span>'}</span>
                    <span class="workspace-wow-copy">
                        <strong>${esc(item.workspace.name)}</strong>
                        <small>${esc(subtitle)} · ${esc(secondary)}</small>
                    </span>
                    <span class="workspace-wow-stat"><b>${item.jobs.length}</b><small>Jobs</small></span>
                    <span class="workspace-wow-stat"><b>${photos}</b><small>Photos</small></span>
                    <span class="workspace-wow-stat"><b>${current.length}</b><small>Active</small></span>
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
            <section class="workspace-wow-page">
                <header class="workspace-wow-header">
                    <div class="workspace-wow-header-copy">
                        <span class="workspace-wow-kicker">STUDIO ORGANIZATION</span>
                        <h1>Workspaces</h1>
                        <p>Jobs, clients, conversations and media — organized by the way the studio actually works.</p>
                    </div>
                    <div class="workspace-wow-header-art" aria-hidden="true">
                        <span></span><span></span><span></span>
                    </div>
                    <button type="button" class="workspace-wow-new" data-workspace-admin>＋ New Workspace</button>
                </header>

                <section class="workspace-wow-overview">
                    <article><span>▦</span><strong>${workspaceData.length}</strong><small>Total Workspaces</small></article>
                    <article><span>●</span><strong>${activeCount}</strong><small>Active</small></article>
                    <article><span>✓</span><strong>${completedWorkspaceCount}</strong><small>Completed</small></article>
                    <article><span>▣</span><strong>${workspaceData.reduce((sum,item)=>sum+item.jobs.length,0)}</strong><small>Total Jobs</small></article>
                    <article><span>▧</span><strong>${workspaceData.reduce((sum,item)=>sum+photoCount(item),0)}</strong><small>Photos</small></article>
                </section>

                <div class="workspace-wow-toolbar">
                    <div class="workspace-reference-tabs" role="tablist">
                        <button class="active" data-workspace-filter="all">All <b>${workspaceData.length}</b></button>
                        <button data-workspace-filter="active">Active <b>${activeCount}</b></button>
                        <button data-workspace-filter="completed">Completed <b>${completedWorkspaceCount}</b></button>
                    </div>
                    <div class="workspace-wow-search">
                        <span>⌕</span><input type="search" placeholder="Search workspaces…" data-workspace-search>
                    </div>
                    <select data-workspace-sort aria-label="Sort workspaces">
                        <option value="name">Name A–Z</option>
                        <option value="jobs">Most jobs</option>
                        <option value="photos">Most photos</option>
                    </select>
                </div>

                <div class="workspace-wow-layout">
                    <section class="workspace-wow-list-card">
                        <header><div><h2>All Workspaces</h2><p>Open a workspace to see its jobs, people, messages and production state.</p></div><span class="workspace-wow-list-count">${workspaceData.length}</span></header>
                        <div class="workspace-wow-list" data-workspace-list>${renderRows(workspaceData)}</div>
                    </section>

                    <aside class="workspace-wow-side">
                        <section class="workspace-wow-side-card announcements">
                            <header><h2>Announcements</h2><span>📣</span></header>
                            <div>
                                ${announcements.length?announcements.map(a=>`<article class="workspace-wow-announcement"><strong>${esc(a.title)}</strong><p>${esc(a.body)}</p><small>${esc(a.workspaceName)} · ${a.createdAtUtc?new Date(a.createdAtUtc).toLocaleDateString([],{month:'short',day:'numeric'}):''}</small></article>`).join(''):'<p class="mw-empty">No workspace announcements.</p>'}
                            </div>
                        </section>

                        <section class="workspace-wow-side-card actions">
                            <header><h2>Quick Actions</h2><span>ϟ</span></header>
                            <div>
                                <button type="button" data-workspace-admin><span>＋</span><span><strong>New Workspace</strong><small>Create a new operational workspace</small></span><i>›</i></button>
                                <button type="button" data-nav-quick="pipeline"><span>▥</span><span><strong>Open Pipeline</strong><small>See work moving through production</small></span><i>›</i></button>
                                <button type="button" data-nav-quick="frontdesk"><span>▣</span><span><strong>Front Desk</strong><small>Create or manage incoming work</small></span><i>›</i></button>
                            </div>
                        </section>
                    </aside>
                </div>
            </section>`;

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
                refresh();
            };
        });
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
        if(view!=='mywork'){
            if(studioPageTools)studioPageTools.innerHTML='';
            if(studioBreadcrumb){
                const label=view==='workspaces'?'':view.replace(/(^|\s)\S/g,m=>m.toUpperCase());
                studioBreadcrumb.innerHTML=`
                    <details class="workspace-menu breadcrumb-workspace-menu" id="workspaceMenu">
                        <summary class="workspace-breadcrumb-trigger"><span>Workspaces</span><span class="workspace-menu-caret">⌄</span></summary>
                        <div class="workspace-menu-popover" id="workspaceMenuList" role="menu" aria-label="Workspaces"></div>
                    </details>
                    ${label?'<span class="breadcrumb-separator" aria-hidden="true">›</span><strong>'+esc(label)+'</strong>':''}`;
                if(typeof renderWorkspaceMenu==='function')renderWorkspaceMenu();
            }
        }
        try{
            await route();
            if(revision===navigationRevision){
                setActive(view);
                if(view!=='mywork'&&typeof renderWorkspaceMenu==='function')renderWorkspaceMenu();
            }
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
