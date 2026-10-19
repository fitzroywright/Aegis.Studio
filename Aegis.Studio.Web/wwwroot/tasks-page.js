(() => {
    const state = { filter: 'mine', due: 'any', search: '', jobSearch: '', priority: 'any', status: 'any', owner: 'any', focusDate: new Date(), page: 1 };
    const escapeHtml = value => window.esc ? window.esc(value ?? '') : String(value ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
    const sameDay = (a,b) => a.getFullYear()===b.getFullYear() && a.getMonth()===b.getMonth() && a.getDate()===b.getDate();
    const dayStart = value => new Date(value.getFullYear(),value.getMonth(),value.getDate());
    const labelStatus = value => String(value||'Not Started').replace(/([a-z])([A-Z])/g,'$1 $2');
    const dueLabel = (value, now) => {
        if(!value) return '<span class="mw-task-due">Unscheduled</span>';
        const due=new Date(value), today=dayStart(now), day=dayStart(due);
        const text=day<today?'Overdue':sameDay(day,today)?'Today':due.toLocaleDateString([],{month:'short',day:'numeric'});
        const time=due.toLocaleTimeString([],{hour:'numeric',minute:'2-digit'});
        return '<span class="mw-task-due '+(day<today?'is-overdue':'')+'"><strong>'+text+'</strong><small>'+time+'</small></span>';
    };
    const priorityTone = value => /urgent|high/i.test(String(value||''))?'danger':/low/i.test(String(value||''))?'neutral':'warning';
    const statusTone = value => /complete|ready|approved/i.test(String(value||''))?'success':/block|cancel|fail/i.test(String(value||''))?'danger':/progress|review|await/i.test(String(value||''))?'warning':'info';

    async function render() {
        const view=document.getElementById('genericView'), conversation=document.getElementById('conversationView');
        if(!view) return;
        conversation?.classList.add('hidden'); view.classList.remove('hidden');
        view.innerHTML='<section class="st-tasks-page"><div class="st-tasks-loading">Loading tasks…</div></section>';
        try {
            const today=dayStart(new Date()), from=today.toISOString(), to=new Date(today.getTime()+14*86400000).toISOString();
            const schedulePromise=json('/api/today/schedule?from='+encodeURIComponent(from)+'&to='+encodeURIComponent(to)).catch(()=>[]);
            const noticesPromise=json('/api/notifications?count=80').catch(()=>[]);
            const results=await Promise.all([
                json('/api/auth/me'),
                json('/api/operations/activities?includeWorkflowDetails=false'),
                json('/api/today/jobs').catch(()=>[])
            ]);
            const me=results[0]?.user||results[0]||{}, user=String(me.userName||me.username||me.email||'').toLowerCase();
            const activities=Array.isArray(results[1])?results[1]:[];
            const jobs=Array.isArray(results[2])?results[2]:[];
            const sessions=[];
            const notices=[];
            const jobMap=new Map(jobs.map(job=>[String(job.id),job]));
            const open=activities.filter(task=>!['completed','cancelled'].includes(String(task.status||'').toLowerCase()));
            const overdue=open.filter(task=>task.dueAtUtc&&new Date(task.dueAtUtc)<new Date()).length;
            const blocked=open.filter(task=>/blocked|failed|rework/i.test(String(task.status||'')+' '+String(task.blockedReason||''))).length;
            const dueToday=open.filter(task=>task.dueAtUtc&&sameDay(dayStart(new Date(task.dueAtUtc)),today)).length;
            const todaySessions=sessions.filter(item=>sameDay(new Date(item.startsAtUtc),today)).slice(0,5);
            const messageRows=notices.slice(0,4).map(item=>'<button type="button" class="smc-row" data-task-message="'+escapeHtml(item.id)+'"><span class="smc-type" aria-hidden="true">✉</span><span class="smc-copy"><strong>'+escapeHtml(item.subject||item.title||'Studio message')+'</strong><small>'+escapeHtml(item.body||item.eventType||'Team update')+'</small></span><span class="mw-chevron">›</span></button>').join('')||'<div class="mw-empty">No new messages or announcements.</div>';
            const sessionRows=todaySessions.map(item=>'<button type="button" class="mw-session-row" data-task-job="'+escapeHtml(item.jobId||'')+'"><span class="mw-session-time">'+escapeHtml(new Date(item.startsAtUtc).toLocaleTimeString([],{hour:'numeric',minute:'2-digit'}))+' – '+escapeHtml(item.endsAtUtc?new Date(item.endsAtUtc).toLocaleTimeString([],{hour:'numeric',minute:'2-digit'}):'')+'</span><span class="mw-session-main"><strong>'+escapeHtml(jobMap.get(String(item.jobId))?.title||item.title||'Studio session')+'</strong><span>'+escapeHtml(jobMap.get(String(item.jobId))?.clientName||item.location||'Scheduled session')+'</span></span></button>').join('')||'<p class="muted">No sessions scheduled today.</p>';
            view.innerHTML='<section class="sj-page st-tasks-page"><main class="sj-board st-tasks-panel"><div class="sj-summary"><div class="sj-view-tabs" role="group" aria-label="Task view"><button type="button" data-task-filter="mine">My Open Tasks <span data-task-count-for="mine"></span></button><button type="button" data-task-filter="all">All Open Tasks <span data-task-count-for="all"></span></button><button type="button" data-task-filter="completed">Completed <span data-task-count-for="completed"></span></button></div><button type="button" class="primary" data-task-add>＋ Add Task</button></div><div class="sj-table-scroll st-task-table-scroll"><table class="sj-table st-task-table"><thead><tr><th><label class="sj-job-header"><span>Task</span><input type="search" data-task-search placeholder="Search tasks…" value="'+escapeHtml(state.search)+'"></label></th><th><label class="sj-job-header"><span>Job / Client</span><input type="search" data-task-job-search placeholder="Search…" value=''+escapeHtml(state.jobSearch)+''></label></th><th><label class="sj-column-filter sj-column-filter-inline"><span>Due</span><select data-task-due><option value="any">Any date</option><option value="today">Today</option><option value="overdue">Overdue</option><option value="upcoming">Upcoming</option><option value="none">Unscheduled</option></select></label></th><th><label class="sj-column-filter sj-column-filter-inline"><span>Priority</span><select data-task-priority><option value="any">Any</option>'+[...new Set(activities.map(task=>String(task.priority||'Normal')))].sort().map(value=>'<option value="'+escapeHtml(value)+'">'+escapeHtml(value)+'</option>').join('')+'</select></label></th><th><label class="sj-column-filter sj-column-filter-inline"><span>Status</span><select data-task-status><option value="any">Any</option>'+[...new Set(activities.map(task=>labelStatus(task.status||'Not Started')))].sort().map(value=>'<option value="'+escapeHtml(value)+'">'+escapeHtml(value)+'</option>').join('')+'</select></label></th><th><label class="sj-column-filter sj-column-filter-inline"><span>Owner</span><select data-task-owner><option value="any">Any</option>'+[...new Set(activities.map(task=>task.assignedTo?String(task.assignedTo).split(/[.@]/)[0]:'Unassigned'))].sort().map(value=>'<option value="'+escapeHtml(value)+'">'+escapeHtml(value)+'</option>').join('')+'</select></label></th></tr></thead><tbody data-task-rows></tbody></table></div><footer class="sj-pagination st-task-count" data-task-count></footer></main><aside class="sj-right st-tasks-rail" aria-label="Task context"><section class="mw-list-panel" data-mw-region="schedule"><header><h2><span class="studio-card-icon" aria-hidden="true">▦</span> Today’s Sessions</h2><button type="button" data-task-nav="schedule">Calendar →</button></header><div class="mw-section-rows">'+sessionRows+'</div></section><section class="mw-list-panel st-task-attention" data-mw-region="attention"><header><h2><span class="studio-card-icon" aria-hidden="true">⚑</span> Needs Attention</h2><button type="button" data-task-nav="tasks">View All →</button></header><div class="mw-status-list"><div class="mw-status-row warning"><span class="studio-attention-label"><span aria-hidden="true">◷</span>Overdue tasks</span><strong>'+overdue+'</strong></div><div class="mw-status-row warning"><span class="studio-attention-label"><span aria-hidden="true">▦</span>Due today</span><strong>'+dueToday+'</strong></div><div class="mw-status-row warning"><span class="studio-attention-label"><span aria-hidden="true">⚑</span>Blocked tasks</span><strong>'+blocked+'</strong></div></div></section>'+StudioMessageCard.html({id:'tasksMessageCard',title:'Messages',body:messageRows,actions:'<button type="button" data-task-nav="messages">View All →</button>',footer:'<button type="button" data-task-nav="messages">✉ Send Message →</button>',attributes:'data-mw-region="messages"'})+'</aside></section>';
            StudioMessageCard.bind(view.querySelector('#tasksMessageCard'));
            const body=view.querySelector('[data-task-rows]'), count=view.querySelector('[data-task-count]');
            const rows=[...activities].sort((a,b)=>{
                const da=a.dueAtUtc?new Date(a.dueAtUtc).getTime():Number.MAX_SAFE_INTEGER;
                const db=b.dueAtUtc?new Date(b.dueAtUtc).getTime():Number.MAX_SAFE_INTEGER;
                return da-db;
            });
            const apply=()=>{
                const needle=state.search.trim().toLowerCase(), jobNeedle=state.jobSearch.trim().toLowerCase();
                const selected=rows.filter(task=>{
                    const isDone=String(task.status||'').toLowerCase()==='completed';
                    if(state.filter==='completed'?!isDone:isDone)return false;
                    if(state.filter==='mine'&&String(task.assignedTo||'').toLowerCase()!==user)return false;
                    if(state.filter!=='completed'&&['cancelled'].includes(String(task.status||'').toLowerCase()))return false;
                    const due=task.dueAtUtc?new Date(task.dueAtUtc):null, todayDay=dayStart(new Date());
                    if(state.due==='none'&&due)return false;
                    if(state.due==='today'&&(!due||!sameDay(dayStart(due),todayDay)))return false;
                    if(state.due==='overdue'&&(!due||dayStart(due)>=todayDay))return false;
                    if(state.due==='upcoming'&&(!due||dayStart(due)<=todayDay))return false;
                    const job=jobMap.get(String(task.jobId))||{};
                    const hay=[task.title,task.activityType,task.status,task.assignedTo,job.title,job.clientName,task.blockedReason].join(' ').toLowerCase();
                    const jobHay=[job.title,job.clientName,job.clientDisplayName].join(' ').toLowerCase();
                    const owner=task.assignedTo?String(task.assignedTo).split(/[.@]/)[0]:'Unassigned';
                    if(state.priority!=='any'&&String(task.priority||'Normal')!==state.priority)return false;
                    if(state.status!=='any'&&labelStatus(task.status||'Not Started')!==state.status)return false;
                    if(state.owner!=='any'&&owner!==state.owner)return false;
                    return (!needle||hay.includes(needle))&&(!jobNeedle||jobHay.includes(jobNeedle));
                });
                const pageSize=25, pageCount=Math.max(1,Math.ceil(selected.length/pageSize));
                state.page=Math.min(Math.max(1,state.page),pageCount);
                const visible=selected.slice((state.page-1)*pageSize,state.page*pageSize);
                body.innerHTML=visible.map(task=>{
                    const job=jobMap.get(String(task.jobId))||{};
                    const owner=task.assignedTo?String(task.assignedTo).split(/[.@]/)[0]:'Unassigned';
                    return '<tr tabindex="0" role="link" class="st-task-row" data-task-job="'+escapeHtml(task.jobId||'')+'"><td><strong>'+escapeHtml(task.title||task.activityType||'Studio task')+'</strong><small>'+escapeHtml(task.activityType||'Task')+'</small></td><td><strong>'+escapeHtml(job.title||'Job')+'</strong><small>'+escapeHtml(job.clientName||job.clientDisplayName||'Client')+'</small></td><td>'+dueLabel(task.dueAtUtc,new Date())+'</td><td><span class="fd-data-pill fd-data-pill--'+priorityTone(task.priority)+'">'+escapeHtml(task.priority||'Normal')+'</span></td><td><span class="fd-data-pill fd-data-pill--'+statusTone(task.status)+'">'+escapeHtml(labelStatus(task.status))+'</span></td><td>'+escapeHtml(owner)+'</td></tr>';
                }).join('')||'<tr><td colspan="6" class="st-task-empty">'+(state.filter==='mine'?'No open tasks are assigned to you.':state.filter==='completed'?'No completed tasks found.':'No tasks match these filters.')+'</td></tr>';
                count.innerHTML='<span>Showing '+(selected.length?((state.page-1)*pageSize+1)+'–'+Math.min(state.page*pageSize,selected.length):'0')+' of '+selected.length+' tasks</span><div><button type="button" data-task-page="-1" aria-label="Previous page" '+(state.page<=1?'disabled':'')+'>‹</button><span>Page '+state.page+' of '+pageCount+'</span><button type="button" data-task-page="1" aria-label="Next page" '+(state.page>=pageCount?'disabled':'')+'>›</button></div>';
                const footer=document.getElementById('studioFooterRate');if(footer)footer.textContent=selected.length+' tasks';
                view.querySelectorAll('[data-task-filter]').forEach(button=>{button.classList.toggle('active',button.dataset.taskFilter===state.filter);button.setAttribute('aria-pressed',String(button.dataset.taskFilter===state.filter));});
                const openCount=rows.filter(task=>!['completed','cancelled'].includes(String(task.status||'').toLowerCase())).length;
                const mineCount=rows.filter(task=>!['completed','cancelled'].includes(String(task.status||'').toLowerCase())&&String(task.assignedTo||'').toLowerCase()===user).length;
                const completedCount=rows.filter(task=>String(task.status||'').toLowerCase()==='completed').length;
                const tabCounts={mine:mineCount,all:openCount,completed:completedCount};
                Object.entries(tabCounts).forEach(([key,value])=>{const target=view.querySelector('[data-task-count-for="'+key+'"]');if(target)target.textContent=' ('+value+')';});
                const headings={mine:['My Open Tasks','Tasks assigned to you.'],all:['All Open Tasks','Open tasks in workspaces you can access.'],completed:['Completed Tasks','Tasks completed across workspaces you can access.']};
                
            };
            apply();
            view.querySelector('[data-task-search]').oninput=event=>{state.search=event.target.value;state.page=1;apply();};
            view.querySelector('[data-task-job-search]').oninput=event=>{state.jobSearch=event.target.value;state.page=1;apply();};
            view.querySelector('[data-task-due]').onchange=event=>{state.due=event.target.value;state.page=1;apply();};
            view.querySelector('[data-task-priority]').onchange=event=>{state.priority=event.target.value;state.page=1;apply();};
            view.querySelector('[data-task-status]').onchange=event=>{state.status=event.target.value;state.page=1;apply();};
            view.querySelector('[data-task-owner]').onchange=event=>{state.owner=event.target.value;state.page=1;apply();};
            view.querySelectorAll('[data-task-filter]').forEach(button=>button.onclick=()=>{state.filter=button.dataset.taskFilter;state.page=1;apply();});
            count.onclick=event=>{const button=event.target.closest('[data-task-page]');if(!button||button.disabled)return;state.page+=Number(button.dataset.taskPage);apply();view.querySelector('.st-task-table-scroll').scrollTop=0;};
            view.querySelector('[data-task-add]').onclick=()=>openAddTask(jobs);
            view.querySelectorAll('[data-task-job]').forEach(row=>row.addEventListener('click',()=>{if(row.dataset.taskJob)window.openWorkspaceJob?.(row.dataset.taskJob);}));
            view.querySelectorAll('[data-task-job]').forEach(row=>row.addEventListener('keydown',event=>{if((event.key==='Enter'||event.key===' ')&&row.matches('tr')){event.preventDefault();if(row.dataset.taskJob)window.openWorkspaceJob?.(row.dataset.taskJob);}}));
            view.querySelectorAll('[data-task-nav]').forEach(button=>button.onclick=()=>window.studioNavigate?.(button.dataset.taskNav));
            window.studioSetBreadcrumb?.([{label:'Tasks',view:'tasks'}]);
            Promise.all([schedulePromise,noticesPromise]).then(([sessionData,noticeData])=>{
                if(!view.isConnected||!view.querySelector('[data-task-rows]'))return;
                const freshSessions=Array.isArray(sessionData)?sessionData.filter(item=>sameDay(new Date(item.startsAtUtc),today)).slice(0,5):[];
                const freshNotices=Array.isArray(noticeData)?noticeData:(noticeData?.items||[]);
                const sessionTarget=view.querySelector('.st-tasks-rail .mw-section-rows');
                const messageTarget=view.querySelector('.st-tasks-rail .smc-feed');
                if(sessionTarget)sessionTarget.innerHTML=freshSessions.map(item=>'<button type="button" class="mw-session-row" data-task-job="'+escapeHtml(item.jobId||'')+'"><span class="mw-session-time">'+escapeHtml(new Date(item.startsAtUtc).toLocaleTimeString([],{hour:'numeric',minute:'2-digit'}))+' – '+escapeHtml(item.endsAtUtc?new Date(item.endsAtUtc).toLocaleTimeString([],{hour:'numeric',minute:'2-digit'}):'')+'</span><span class="mw-session-main"><strong>'+escapeHtml(jobMap.get(String(item.jobId))?.title||item.title||'Studio session')+'</strong><span>'+escapeHtml(jobMap.get(String(item.jobId))?.clientName||item.location||'Scheduled session')+'</span></span></button>').join('')||'<p class="muted">No sessions scheduled today.</p>';
                if(messageTarget)messageTarget.innerHTML=freshNotices.slice(0,4).map(item=>'<button type="button" class="smc-row" data-task-message="'+escapeHtml(item.id)+'"><span class="smc-type" aria-hidden="true">✉</span><span class="smc-copy"><strong>'+escapeHtml(item.subject||item.title||'Studio message')+'</strong><small>'+escapeHtml(item.body||item.eventType||'Team update')+'</small></span><span class="mw-chevron">›</span></button>').join('')||'<div class="mw-empty">No new messages or announcements.</div>';
                view.querySelectorAll('[data-task-job]').forEach(row=>row.addEventListener('click',()=>{if(row.dataset.taskJob)window.openWorkspaceJob?.(row.dataset.taskJob);}));
            }).catch(()=>{});
        } catch(error) {
            view.innerHTML='<section class="st-tasks-page"><div class="studio-card"><h1>Tasks could not be loaded</h1><p role="alert">'+escapeHtml(error.message||error)+'</p><button type="button" class="action" data-task-retry>Retry</button></div></section>';
            view.querySelector('[data-task-retry]').onclick=()=>render();
        }
    }

    async function openAddTask(jobs) {
        if(!jobs.length){alert('No jobs are available for task assignment.');return;}
        let employees=[];
        try{employees=await json('/api/studio-employees?assignable=true');}catch{/* Unassigned remains available. */}
        const dialog=document.createElement('dialog');dialog.className='studio-dialog st-task-dialog';
        dialog.innerHTML='<form method="dialog" class="st-task-form"><header><h2>Add Task</h2><button type="submit" value="cancel" aria-label="Close">×</button></header><label>Task title<input name="title" required maxlength="200" autofocus></label><label>Job<select name="jobId" required>'+jobs.map(job=>'<option value="'+escapeHtml(job.id)+'">'+escapeHtml(job.title||'Job')+' · '+escapeHtml(job.clientName||job.clientDisplayName||'Client')+'</option>').join('')+'</select></label><label>Assign to<select name="assignedTo"><option value="">Unassigned</option>'+employees.map(employee=>'<option value="'+escapeHtml(employee.userName)+'">'+escapeHtml(employee.displayName||employee.userName)+'</option>').join('')+'</select></label><div class="st-task-form-row"><label>Priority<select name="priority"><option>Normal</option><option>High</option><option>Urgent</option><option>Low</option></select></label><label>Due<input name="due" type="datetime-local"></label></div><p role="alert"></p><footer><button type="button" class="action" data-task-cancel>Cancel</button><button type="submit" class="primary" data-task-save>Create Task</button></footer></form>';
        document.body.appendChild(dialog);dialog.showModal();
        const form=dialog.querySelector('form'), error=form.querySelector('[role="alert"]');
        dialog.querySelector('[data-task-cancel]').onclick=()=>dialog.close();
        dialog.addEventListener('close',()=>dialog.remove(),{once:true});
        form.addEventListener('submit',async event=>{
            if(event.submitter?.value==='cancel')return;
            event.preventDefault();
            const button=form.querySelector('[data-task-save]');button.disabled=true;error.textContent='';
            try{
                await window.StudioWorkflow.command(form.elements.jobId.value,{action:'task',title:form.elements.title.value,kind:'Other',priority:form.elements.priority.value,userName:form.elements.assignedTo.value||null,dueAtUtc:form.elements.due.value?new Date(form.elements.due.value).toISOString():null},form);
                dialog.close();await render();
            }catch(exception){error.textContent=exception.message||String(exception);button.disabled=false;}
        });
    }
    window.StudioTasks={render};
})();