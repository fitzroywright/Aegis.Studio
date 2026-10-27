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
        const state={auth:{},jobs:[],tasks:[],schedule:[],notifications:[],media:[],announcements:[],status:{},errors:{},taskFilter:'all',jobView:'involved',jobSearch:'',jobStatusFilter:'',jobRoleFilter:'',includeClosedJobs:false,selectedDate:new Date(),calendarMonth:new Date(),calendarView:'day',layout:'jobs',workspaceName:''};
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
        const urls={tasks:'/api/today/tasks',schedule:scheduleUrl(new Date(),'day'),jobs:'/api/today/jobs',auth:'/api/auth/me',notifications:'/api/notifications?count=80',media:'/api/today/media-summary'};
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
            paintToday(state);
        };
        state.setCalendarView=view=>{
            if(!['month','week','day'].includes(view)||view===state.calendarView)return;
            state.calendarView=view;
            state.calendarMonth=new Date(state.selectedDate.getFullYear(),state.selectedDate.getMonth(),1);
            paintToday(state);
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
        const activeJobRange=state.scheduleRange?state.scheduleRange(selectedDate,state.calendarView):{from:new Date(selectedDate.getFullYear(),selectedDate.getMonth(),selectedDate.getDate()),to:new Date(selectedDate.getFullYear(),selectedDate.getMonth(),selectedDate.getDate()+1)};
        const startOfDay=new Date(now.getFullYear(),now.getMonth(),now.getDate());
        const endOfDay=new Date(now.getFullYear(),now.getMonth(),now.getDate()+1);
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
        const searchedJobs=viewJobs.filter(job=>state.jobStatusFilter ? String(job.status||'Unknown')===state.jobStatusFilter : !StudioSubjectPicker.isClosed(job)).filter(job=>[job.title,job.clientName,job.clientDisplayName,job.status,job.jobLead,job.id].some(value=>normalized(value).includes(normalized(state.jobSearch))));
        const roleForJob=job=>isLead(job)?'Job Lead':normalized(job.assignedTo)===user?'Assigned':myTasks.some(t=>String(t.jobId)===String(job.id))?'Task Owner':'Team';
        const statusOptions=StudioStatusOrder.sort(viewJobs.map(job=>String(job.status||'Unknown')));
        const roleOptions=[...new Set(viewJobs.map(roleForJob))].sort((a,b)=>a.localeCompare(b));
        // EventDate is a calendar date: do not parse YYYY-MM-DD as UTC midnight.
        const eventDate=job=>{
            const match=/^(\d{4})-(\d{2})-(\d{2})(?:$|T)/.exec(String(job.eventDate||''));
            return match?new Date(Number(match[1]),Number(match[2])-1,Number(match[3])):null;
        };
        const datedJobs=searchedJobs.filter(job=>{
            const date=eventDate(job);
            return date&&date>=activeJobRange.from&&date<activeJobRange.to;
        });
        const filteredJobs=datedJobs.filter(job=>(!state.jobStatusFilter||String(job.status||'Unknown')===state.jobStatusFilter)&&(!state.jobRoleFilter||roleForJob(job)===state.jobRoleFilter));
        const sortedJobs=[...filteredJobs].sort((a,b)=>eventDate(a)-eventDate(b)||String(a.title||'').localeCompare(String(b.title||''))||String(a.id).localeCompare(String(b.id)));
        const footerCount=document.getElementById('studioFooterRate');
        if(footerCount)footerCount.textContent=sortedJobs.length+' Jobs';
        const emptyJobText=!viewJobs.length?(state.jobView==='led'?'You are not the lead on any jobs.':state.jobView==='involved'?'You are not involved in any jobs.':'No jobs found.'):'No jobs match the selected date range and filters.';
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
                
            </button>`;
        }).join('')||'<div class="mw-empty">No open tasks assigned to you.</div>';

        const messageRows=combinedCommunicationFeed.map(entry=>{
            if(entry.kind==='announcement'){
                const a=entry.item;
                return `<button class="smc-row" data-announcement-workspace="${a.workspaceId||''}">
                    <span class="smc-type" title="Announcement" aria-label="Announcement">📣</span>
                    <span class="smc-copy"><strong>${esc(a.title||'Announcement')}</strong><small>${esc((a.workspaceName?a.workspaceName+' · ':'')+(a.body||''))}</small></span>
                    <span class="smc-time">${a.createdAtUtc?formatDay(a.createdAtUtc):''}</span>
                    
                </button>`;
            }
            const n=entry.item;
            return `<button class="smc-row" data-notification="${n.id}" data-message-job="${n.referenceType==='Job'?n.referenceId||'':''}">
                <span class="smc-type" title="Message" aria-label="Message">✉</span>
                <span class="smc-copy"><strong>${esc(n.subject||n.title||'Message')}</strong><small>${esc(n.body||n.eventType||'Studio message')}</small></span>
                <span class="smc-time">${n.createdAtUtc?formatDay(n.createdAtUtc):''}</span>
                
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
                
            </button>`;
        }).join('')||'<div class="mw-today-jobs-empty" role="status">'+esc(emptyJobText)+'</div>';
        const jobLayout=state.layout==='jobs'?mainJobRows:StudioCalendarLayouts.render({layout:state.layout,anchor:selectedDate,period:state.calendarView,attribute:'data-open-job',items:sortedJobs.map(job=>({id:job.id,title:job.title||'Untitled job',context:job.clientName||'',start:job.eventDate,allDay:true,status:job.status}))});
        const taskAttention=tasks.filter(task=>!/[Cc]ompleted|[Cc]ancelled|[Aa]rchived/.test(task.status||'')).flatMap(task=>{
            const reasons=[];
            if(task.dueAtUtc&&new Date(task.dueAtUtc)<now)reasons.push('Overdue');
            if(/blocked|rework|required|failed/i.test(String(task.status||'')))reasons.push(task.blockedReason||task.status);
            if(!reasons.length)return [];
            const job=jobs.find(job=>String(job.id)===String(task.jobId));
            return [{id:'task:'+task.id,jobId:task.jobId,title:task.title||task.activityType||'Studio task',context:[job?.title,...reasons].filter(Boolean).join(' · ')}];
        });
        const jobAttention=jobs.flatMap(job=>{
            const status=normalized(job.status);
            if(!['confirmed','needs scheduling','awaiting scheduling','needs assignment','unassigned','needs job owner','needs photographer','needs photographer assignment'].includes(status)&&!/awaiting upload|blocked|overdue|exception|changes requested/i.test(status))return [];
            return [{id:'job:'+job.id,jobId:job.id,title:job.title||'Untitled job',context:[job.clientName,job.status,job.blockedReason].filter(Boolean).join(' · ')}];
        });
        window.dispatchEvent(new CustomEvent('studio-attention-updated', {detail: {view:'today',loaded:state.status.jobs==='ready'&&state.status.tasks==='ready',items:[...taskAttention,...jobAttention],error:state.errors.jobs||state.errors.tasks||''}}));

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
                                <div class="mw-today-calendar-tools" aria-label="Calendar navigation"></div><div class="mw-today-toolbar-actions">${state.status.media==='error'?'<button type="button" data-mw-retry="media">Retry previews</button>':''}</div>
                                
                            </div>
                            
                            <div class="mw-today-job-columns"><label class="mw-job-column-search"><span>Job / Client</span><label class="mw-today-search"><span class="sr-only">Search jobs</span><input type="search" data-mw-job-search placeholder="Search jobs, clients or status…" value="${esc(state.jobSearch)}"></label></label><label class="mw-job-column-filter"><span>Status</span><select data-mw-job-status aria-label="Filter jobs by status"><option value="">All Open Jobs</option>${statusOptions.map(value=>`<option value="${esc(value)}" ${state.jobStatusFilter===value?'selected':''}>${esc(value)}</option>`).join('')}</select></label><span>Progress</span><span>Event Date</span><label class="mw-job-column-filter"><span>My Role</span><select data-mw-job-role aria-label="Filter jobs by role"><option value="">All roles</option>${roleOptions.map(value=>`<option value="${esc(value)}" ${state.jobRoleFilter===value?'selected':''}>${esc(value)}</option>`).join('')}</select></label><span></span></div>
                            <div class="mw-today-job-list" data-layout="${state.layout}">${phase('jobs',jobLayout)}</div>
                        </section>
                    </main>

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
            // Keep the scroll container and each job button alive during enrichment.
            // Replacing the region resets browser focus/scroll anchoring while a
            // pointer gesture may still be targeting one of its rows.
            const list=existing.querySelector('.mw-today-job-list');
            const nextList=region.querySelector('.mw-today-job-list');
            const top=list.scrollTop,left=list.scrollLeft;
            if(list.dataset.layout!=='jobs'||nextList.dataset.layout!=='jobs'){
                if(list.innerHTML!==nextList.innerHTML)list.innerHTML=nextList.innerHTML;
            }else{
            const rows=new Map([...list.querySelectorAll('[data-open-job]')].map(row=>[row.dataset.openJob,row]));
            let cursor=list.firstChild;
            for(const nextRow of [...nextList.children]){
                const row=rows.get(nextRow.dataset.openJob)||nextRow;
                if(row!==nextRow&&row.innerHTML!==nextRow.innerHTML)row.innerHTML=nextRow.innerHTML;
                if(row!==cursor)list.insertBefore(row,cursor);
                cursor=row.nextSibling;
            }
            while(cursor){const removed=cursor;cursor=cursor.nextSibling;removed.remove();}
            }
            list.dataset.layout=nextList.dataset.layout;
            [...region.children].forEach((child,index)=>{
                if(child!==nextList&&existing.children[index].innerHTML!==child.innerHTML)existing.children[index].replaceWith(child);
            });
            list.scrollTop=top;list.scrollLeft=left;
        });
        state.root=genericView.querySelector('.mw-page');
        window.studioShellSetOverview?.(`<section class="mw-list-panel studio-context-card studio-overview-rail" data-rail-card="overview"><header><h2>Overview</h2></header><div class="workspace-summary-row"><span>Jobs</span><strong>${jobs.length}</strong></div><div class="workspace-summary-row"><span>Tasks</span><strong>${tasks.length}</strong></div><div class="workspace-summary-row"><span>Sessions</span><strong>${schedule.length}</strong></div></section>`,'today');
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
            studioPageTools.innerHTML=`${StudioCalendarLayouts.switcher(state.layout)}
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
        StudioCalendarLayouts.bind(genericView,{onLayout:value=>{state.layout=value;paintToday(state);}});
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
        
        const statusFilter=genericView.querySelector('[data-mw-job-status]');
        if(statusFilter)statusFilter.onchange=event=>{state.jobStatusFilter=event.target.value;paintToday(state);};
        const roleFilter=genericView.querySelector('[data-mw-job-role]');
        if(roleFilter)roleFilter.onchange=event=>{state.jobRoleFilter=event.target.value;paintToday(state);};
        const jobSearch=genericView.querySelector('[data-mw-job-search]');
        if(jobSearch)jobSearch.oninput=event=>{
            state.jobSearch=event.target.value;
            const position=event.target.selectionStart;
            paintToday(state);
            const replacement=genericView.querySelector('[data-mw-job-search]');
            replacement?.focus();
            if(position!==null)replacement?.setSelectionRange(position,position);
        };
        genericView.querySelectorAll('[data-mw-workspace]').forEach(button=>button.onclick=()=>{
            const workspace=(typeof workspaceCache!=='undefined'?workspaceCache:[]).find(w=>String(w.id)===button.dataset.mwWorkspace);
            if(workspace)selectWorkspace(workspace.id,workspace.name);
        });
        genericView.querySelectorAll('[data-mw-retry]').forEach(b=>b.onclick=()=>{b.disabled=true;state.retry(b.dataset.mwRetry);});
        genericView._assignJobLeadController?.abort();
        genericView._assignJobLeadController = new AbortController();
        genericView.addEventListener('studio-assign-job-lead',async()=>{
            if(!(state.auth?.user||state.auth)?.isAdministrator)return;
            const activeJobs=jobs.filter(job=>!['Completed','Closed','Cancelled','Published'].includes(String(job.status||'')))
                .sort((a,b)=>String(a.title||'').localeCompare(String(b.title||'')));
            if(!activeJobs.length){alert('There are no active jobs available to assign.');return;}
            if(genericView._assignJobLeadOpening)return;
            genericView._assignJobLeadOpening=true;
            let employees;
            try{employees=await json('/api/studio-employees?assignable=true');}
            catch(error){genericView._assignJobLeadOpening=false;alert(error.message);return;}
            genericView._assignJobLeadOpening=false;
            const dialog=studioDialog('Assign Job Lead',`
                <form class="subject-form" data-job-lead-form>
                    <label>Job
                        <select data-job-picker name="jobId" required>
                            ${StudioSubjectPicker.jobOptions(jobs)}
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
                    const wf=await json('/api/jobs/'+encodeURIComponent(jobId)+'/workflow-graph');
                    const result=await json('/api/jobs/'+encodeURIComponent(jobId)+'/workflow-graph',{
                        method:'POST',
                        headers:{'Content-Type':'application/json'},
                        body:JSON.stringify({action:'assign-owner',userName,reason:'Today owner assignment',expectedRevision:wf.revision,operationId:crypto.randomUUID()})
                    });
                    state.jobs=state.jobs.map(job=>String(job.id)===String(jobId)?{...job,jobLead:result.jobLead??userName}:job);
                    dialog.close();
                    if(state.active())paintToday(state);
                }catch(errorValue){error.textContent=errorValue.message||String(errorValue);}
                finally{submit.disabled=false;}
            };
        }, {signal:genericView._assignJobLeadController.signal});
        genericView.querySelectorAll('.mw-session-thumb img[data-fallback-src]').forEach(img=>{
            img.dataset.previewOwner='today';
            img.addEventListener('error',()=>{
                delete img.dataset.previewOwner;
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
            if(state.active()&&button.dataset.openJob)openWorkspaceJob(button.dataset.openJob).catch(error=>alert(error.message));
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
            json,workspaces:workspaceCache||[],
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
                <section class="studio-about-card studio-about-demo" id="studioDemoLoader"><header class="studio-about-card-heading"><h2>Demo Data</h2></header><div class="studio-about-card-body"><p class="studio-about-demo-warning">Reset clears business records and files. Staff accounts, configuration and Unassigned are preserved.</p><div class="studio-about-demo-actions"><button class="action" type="button" data-demo-preview>Preview reset scope</button><button class="action" type="button" data-demo-remove>Remove demo data</button><button class="primary" type="button" data-load-demo>Clear and recreate</button><button class="primary" type="button" data-demo-reset>Reset and regenerate</button></div><progress data-demo-progress max="100" value="0" aria-label="Demo data loading progress" hidden></progress><p class="admin-demo-status" data-demo-status role="status" aria-live="polite"></p><small data-demo-elapsed hidden></small></div></section>`;break;
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
        panel.querySelector('[data-demo-reset]').onclick=()=>runDemo(true);
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


    const routeAliases={admin:'about','studio-management':'about','demo-data':'about','api-site-key':'about','booking-sessions':'catalog','job-templates':'templates','work-order-templates':'templates','workflows-admin':'templates','staff-admin':'staff-roles','roles-admin':'staff-roles'};
    const canonicalRoute=view=>routeAliases[view]||view;
    const routes={
        today:renderToday,
        notifications:()=>showNotifications(),
        clients:()=>window.StudioClients.render({clientId:window.history.state?.studioClientId,tab:window.history.state?.studioClientTab}),
        messages:()=>window.showStudioMessages(),
        schedule:()=>window.history.state?.studioSchedulePanel==='pipeline'?showPipeline():window.history.state?.studioSchedulePanel==='production'?window.StudioProductionPlanning.render():window.StudioPhotographerSchedule.render({photographer:window.history.state?.studioPhotographer||'',date:window.history.state?.studioScheduleDate||'',view:window.history.state?.studioScheduleView||'',resourceId:window.history.state?.studioScheduleResource||''}),
        tasks:()=>window.StudioTasks.render(),
        workspaces:renderWorkspaces,
        'workspace-management':()=>window.StudioWorkspaceManagement.render(),
        jobs:renderJobs,
        pipeline:()=>showPipeline(),
        production:()=>showCatalogProduction(),
        ...Object.fromEntries(['about','staff-roles'].map(view=>[view,()=>renderAdmin(view)])),
        catalog:()=>showCommercialCatalog({tab:window.history.state?.studioCatalogTab}),
        templates:()=>showCommercialTemplates(window.history.state?.studioTemplateKind||'Job'),
        media:()=>showMediaReview(),
        frontdesk:()=>showFrontDesk(),
        'session-plan':()=>StudioWorkflowViews.showSessionPlan(window.history.state?.studioJobId),
        'session-plan-import':()=>StudioWorkflowViews.showSessionPlan(window.history.state?.studioJobId),
        delivery:()=>StudioWorkflowViews.showDelivery(),
        finance:()=>showAccounts(),
        reports:()=>showManagement(),
        management:()=>showManagement()
    };

    let currentNavigationView=document.body.dataset.studioView||'today';
    const navigationLabels={...Object.fromEntries((window.StudioAdmin?.entries||[]).map(entry=>[entry.view,entry.label])),'workspace-management':'Workspaces','session-plan-import':'Session Plan Import','session-plan':'Session Plan',delivery:'Delivery',catalog:'Products and Services','job-templates':'Job Templates','work-order-templates':'Work Order Templates',clients:'Client Management',today:'Today',workspaces:'Workspaces',jobs:'Jobs',pipeline:'Pipeline',production:'Production',media:'Media',frontdesk:'Front Desk',finance:'Finance',reports:'Reports',management:'Management',messages:'Messages',notifications:'Notifications',tasks:'Tasks',schedule:'Schedule'};

    function navigationContext(view,options={}){
        return {studioView:view,studioSchedulePanel:view==='schedule'?(['pipeline','production'].includes(options.panel)?options.panel:'workers'):null,studioPhotographer:view==='schedule'?String(options.photographer||''):null,studioScheduleResource:view==='schedule'?String(options.resourceId||''):null,studioScheduleDate:view==='schedule'?String(options.date||''):null,studioScheduleView:view==='schedule'?String(options.mode||''):null,studioClientId:view==='clients'?String(options.clientId||''):null,studioWorkspaceId:view==='jobs'?String(options.workspaceId??jobsWorkspaceFilter??''):null,studioClientTab:view==='clients'?String(options.tab||''):null,studioTemplateKind:view==='templates'?(['Job','WorkOrder','Automation'].includes(options.kind)?options.kind:'Job'):null,studioCatalogTab:view==='catalog'?(options.tab==='booking-sessions'?'booking-sessions':'products'):null,studioJobId:['workspaces','session-plan','session-plan-import'].includes(view)&&options.jobId?String(options.jobId):null};
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
        const same=previous?.studioView===next.studioView&&['studioSchedulePanel','studioPhotographer','studioScheduleResource','studioScheduleDate','studioScheduleView','studioCatalogTab','studioTemplateKind'].every(key=>(previous[key]||'')===(next[key]||''))&&(previous.studioClientId||null)===(next.studioClientId||null)&&(previous.studioWorkspaceId||null)===(next.studioWorkspaceId||null)&&(previous.studioJobId||null)===next.studioJobId&&(previous.studioClientTab||'')===(next.studioClientTab||'');
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
        todayLoad?.abort();
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
        if(view!=='today'){
            if(studioPageTools)studioPageTools.innerHTML='';
            window.studioSetBreadcrumb?.([{label:navigationLabels[view]||view.replace(/(^|\s)\S/g,m=>m.toUpperCase()),view}]);
        }
        try{
            if(view==='workspaces'&&context.studioJobId)await window.openWorkspaceJob(context.studioJobId,{navigationRecorded:true,history:false});
            else await route();
            if(revision===navigationRevision){
                setActive(view);
                if(view!=='today'&&typeof renderWorkspaceMenu==='function')renderWorkspaceMenu();
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
        return {view:view&&routes[view]?view:'today',kind:(state?.studioView==='workflows-admin'||(!state?.studioView&&params.get('studio')==='workflows-admin'))?'Automation':(state?.studioView==='work-order-templates'||(!state?.studioView&&params.get('studio')==='work-order-templates'))?'WorkOrder':state?.studioTemplateKind||params.get('kind')||'Job',panel:state?.studioView?(state.studioSchedulePanel||'workers'):(params.get('panel')||'workers'),photographer:state?.studioView?(state.studioPhotographer||''):(params.get('photographer')||''),resourceId:state?.studioView?(state.studioScheduleResource||''):(params.get('resource')||''),date:state?.studioView?(state.studioScheduleDate||''):(params.get('date')||''),mode:state?.studioView?(state.studioScheduleView||''):(params.get('view')||''),tab:(state?.studioView==='booking-sessions'||(!state?.studioView&&params.get('studio')==='booking-sessions'))?'booking-sessions':state?.studioView?(state.studioView==='catalog'?(state.studioCatalogTab||'products'):(state.studioClientTab||'')):(params.get('tab')||''),clientId:state?.studioView?(state.studioClientId||''):(params.get('client')||''),workspaceId:state?.studioView?(state.studioWorkspaceId||''):(params.get('workspace')||''),jobId:state?.studioView?(state.studioJobId||null):(params.get('job')||null)};
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










