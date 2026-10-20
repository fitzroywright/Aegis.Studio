// Three read-only planning projections share the Schedule route.
function studioScheduleViewSwitch(active){
    return '<div class="studio-schedule-switch" role="group" aria-label="Schedule view">'+[['photographers','Photographer'],['pipeline','Jobs'],['production','Production']].map(([key,label])=>'<button type="button" data-studio-schedule-panel="'+key+'" aria-pressed="'+(active===key)+'">'+label+'</button>').join('')+'</div>';
}
function bindStudioScheduleSwitch(host){
    host.querySelectorAll('[data-studio-schedule-panel]').forEach(button=>button.onclick=()=>{const navigation=window.studioNavigate('schedule',{...window.StudioPhotographerSchedule?.options(),panel:button.dataset.studioSchedulePanel});Promise.resolve(navigation).then(()=>window.StudioShell?.sync('schedule'),()=>window.StudioShell?.sync('schedule'));});
}
window.StudioScheduleCalendarControls = (() => {
    const dayKey = date => `${date.getFullYear()}-${String(date.getMonth()+1).padStart(2,'0')}-${String(date.getDate()).padStart(2,'0')}`;
    const startOfWeek = date => new Date(date.getFullYear(),date.getMonth(),date.getDate()-((date.getDay()+6)%7));
    const addDays = (date,days) => new Date(date.getFullYear(),date.getMonth(),date.getDate()+days);
    const shortDate = date => date.toLocaleDateString([],{weekday:'short',month:'short',day:'numeric'});
    function label(date,view){
        if(view==='month')return date.toLocaleDateString([],{month:'long',year:'numeric'});
        if(view==='week'){const first=startOfWeek(date);return shortDate(first)+' – '+shortDate(addDays(first,6));}
        return dayKey(date)===dayKey(new Date())?'Today':shortDate(date);
    }
    function html(activePanel,date,view,leftExtra=''){
        return `<div class="ps-toolbar pipeline-calendar-toolbar"><div class="pipeline-toolbar-left">${studioScheduleViewSwitch(activePanel)}${leftExtra}</div><div class="ps-calendar-tools"><div class="mw-calendar-view-switch" role="group" aria-label="Calendar view">${['month','week','day'].map(value=>'<button type="button" data-sp-calendar-view="'+value+'" class="'+(view===value?'active':'')+'" aria-pressed="'+String(view===value)+'">'+value[0].toUpperCase()+value.slice(1)+'</button>').join('')}</div><div class="mw-day-nav" aria-label="${view} photographer calendar navigator"><button type="button" class="mw-day-step" data-sp-calendar-step="-1" aria-label="Previous ${view}">‹</button><button type="button" class="mw-day-current" data-sp-calendar-today title="${dayKey(date)===dayKey(new Date())?'Today':'Return to today'}">${esc(label(date,view))}</button><button type="button" class="mw-day-step" data-sp-calendar-step="1" aria-label="Next ${view}">›</button><div class="mw-calendar-control"><button type="button" class="mw-calendar-trigger" data-sp-calendar-toggle aria-label="Choose date" aria-expanded="false" title="Choose date"><svg aria-hidden="true" width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="5" width="18" height="16" rx="2"/><path d="M7 3v4M17 3v4M3 10h18"/><path d="m9 15 2 2 4-4"/></svg></button><div class="mw-calendar-popover" data-sp-calendar-popover hidden></div></div></div></div></div>`;
    }
    function bind(host,handlers){
        host.querySelectorAll('[data-sp-calendar-view]').forEach(button=>button.addEventListener('click',()=>handlers.view(button.dataset.spCalendarView)));
        host.querySelectorAll('[data-sp-calendar-step]').forEach(button=>button.addEventListener('click',()=>handlers.shift(Number(button.dataset.spCalendarStep))));
        host.querySelector('[data-sp-calendar-today]')?.addEventListener('click',handlers.today);
        const toggle=host.querySelector('[data-sp-calendar-toggle]'),popover=host.querySelector('[data-sp-calendar-popover]');
        if(!toggle||!popover)return;
        let month=new Date(handlers.date().getFullYear(),handlers.date().getMonth(),1);
        const paint=()=>{
            const year=month.getFullYear(),monthIndex=month.getMonth(),first=new Date(year,monthIndex,1),last=new Date(year,monthIndex+1,0),leading=(first.getDay()+6)%7,today=new Date(),cells=[];
            for(let i=0;i<leading;i++)cells.push('<span class="mw-calendar-empty" aria-hidden="true"></span>');
            for(let day=1;day<=last.getDate();day++){
                const date=new Date(year,monthIndex,day),selected=dayKey(date)===dayKey(handlers.date()),isToday=dayKey(date)===dayKey(today);
                cells.push('<button type="button" class="mw-calendar-day'+(selected?' selected':'')+(isToday?' is-today':'')+'" data-sp-calendar-day="'+day+'" aria-label="'+date.toLocaleDateString([],{weekday:'long',month:'long',day:'numeric',year:'numeric'})+'">'+day+'</button>');
            }
            popover.innerHTML='<div class="mw-calendar-head"><button type="button" data-sp-calendar-month="-1" aria-label="Previous month">‹</button><strong>'+month.toLocaleDateString([],{month:'long',year:'numeric'})+'</strong><button type="button" data-sp-calendar-month="1" aria-label="Next month">›</button></div><div class="mw-calendar-week"><span>Mon</span><span>Tue</span><span>Wed</span><span>Thu</span><span>Fri</span><span>Sat</span><span>Sun</span></div><div class="mw-calendar-grid">'+cells.join('')+'</div><button type="button" class="mw-calendar-today" data-sp-calendar-today-action>Today</button>';
            popover.querySelectorAll('[data-sp-calendar-month]').forEach(button=>button.onclick=()=>{month=new Date(year,monthIndex+Number(button.dataset.spCalendarMonth),1);paint();});
            popover.querySelectorAll('[data-sp-calendar-day]').forEach(button=>button.onclick=()=>{popover.hidden=true;toggle.setAttribute('aria-expanded','false');handlers.dateSelected(new Date(year,monthIndex,Number(button.dataset.spCalendarDay),12));});
            popover.querySelector('[data-sp-calendar-today-action]').onclick=()=>{popover.hidden=true;toggle.setAttribute('aria-expanded','false');handlers.today();};
        };
        toggle.onclick=()=>{const opening=popover.hidden;popover.hidden=!opening;toggle.setAttribute('aria-expanded',String(opening));if(opening){const date=handlers.date();month=new Date(date.getFullYear(),date.getMonth(),1);paint();}};
    }
    return {html,bind,label,startOfWeek,dayKey};
})();
// Pipeline is the planning home: stage groups, schedule entries and job tasks share one page.
const pipelineStages = [
    ['Booking / intake', ['New', 'Inquiry', 'Booking', 'Pending', 'Pending Confirmation', 'Awaiting Deposit']],
    ['Needs scheduling', ['Confirmed', 'Needs Scheduling', 'Needs Assignment', 'Awaiting Scheduling', 'Needs Photographer Assignment']],
    ['Scheduled', ['Scheduled', 'Photographer Assigned']],
    ['Photography', ['Photography', 'Shoot Today', 'In Shoot', 'Shoot Complete', 'Shooting', 'Photography In Progress']],
    ['Awaiting upload', ['Awaiting Upload']],
    ['Production', ['Production', 'Culling', 'Editing', 'Retouching', 'QC', 'Printing', 'Awaiting Production Review', 'In Production', 'Active Production']],
    ['Client review', ['Client Review', 'Ready for Client', 'Waiting on Client', 'Waiting On Client', 'Awaiting Approval']],
    ['Delivery', ['Ready for Delivery', 'Ready For Delivery']],
    ['Complete', ['Delivered', 'Completed', 'Complete', 'Closed', 'Archived']],
    ['Cancelled', ['Cancelled']]
];
let pipelineView = 'jobs';
let pipelineCalendarPeriod = 'week';
let pipelineAnchor = new Date();
let pipelineSelectedStage = '';
let pipelineSelectedStatus = '';
let pipelineSelectedWorkspace = '';
let pipelineSearch = '';

function pipelineStage(job) {
    return pipelineStages.find(([, statuses]) => statuses.includes(job.status))?.[0] || 'Other';
}
function pipelineLocalDay(value) {
    const date = new Date(value);
    return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}`;
}
function pipelineDayAt(date, offset) {
    const result = new Date(date.getFullYear(), date.getMonth(), date.getDate() + offset);
    return pipelineLocalDay(result);
}
function pipelineItems(entries, tasks, jobs) {
    const visible = new Set(jobs.map(job => job.id));
    return [
        ...entries.filter(item => !item.jobId || visible.has(item.jobId)).map(item => ({
            id: item.id, jobId: item.jobId, type: 'schedule', title: item.title,
            start: item.startsAtUtc, end: item.endsAtUtc, location: item.location,
            status: item.status, resourceIds: item.resourceIds || []
        })),
        ...tasks.filter(task => visible.has(task.jobId) && (task.scheduledAtUtc || task.dueAtUtc)).map(task => ({
            id: task.id, jobId: task.jobId, type: 'task', title: task.title,
            start: new Date(Math.min(new Date(task.scheduledAtUtc || task.dueAtUtc),new Date(task.dueAtUtc || task.scheduledAtUtc))).toISOString(),
            end: new Date(Math.max(new Date(task.scheduledAtUtc || task.dueAtUtc),new Date(task.dueAtUtc || task.scheduledAtUtc))).toISOString(),
            location: task.location, status: task.status, assignedTo: task.assignedTo
        }))
    ];
}
function pipelineItemMarkup(item, resourceMap) {
    const staff = item.type === 'task' ? studioFirstName(item.assignedTo) : item.resourceIds.map(id => resourceMap.get(id)?.name).filter(Boolean).join(', ');
    const draggable = item.jobId ? ' draggable="true" data-pipeline-item="'+item.id+'" data-pipeline-item-type="'+item.type+'"' : '';
    return `<button class="pipeline-event ${item.type}"${draggable} data-open-job="${esc(item.jobId || '')}" title="${esc(item.title)} · ${esc(staff || 'Unassigned')}${item.location ? ' · ' + esc(item.location) : ''}"><span class="pipeline-event-symbol">${item.type === 'task' ? '✓' : '◉'}</span><span class="pipeline-event-copy">${esc(item.title)}<small>${esc(staff || item.status || '')}</small></span></button>`;
}
function pipelineTimeline(jobs, items, resourceMap) {
    const controls=window.StudioScheduleCalendarControls;
    const start=pipelineCalendarPeriod==='month'?new Date(pipelineAnchor.getFullYear(),pipelineAnchor.getMonth(),1):pipelineCalendarPeriod==='week'?controls.startOfWeek(pipelineAnchor):new Date(pipelineAnchor.getFullYear(),pipelineAnchor.getMonth(),pipelineAnchor.getDate());
    const dayCount=pipelineCalendarPeriod==='month'?new Date(start.getFullYear(),start.getMonth()+1,0).getDate():pipelineCalendarPeriod==='week'?7:1;
    const days = Array.from({ length: dayCount }, (_, i) => pipelineDayAt(start, i));
    const today = pipelineLocalDay(new Date());
    const head = `<div class="pipeline-timeline-head" style="grid-template-columns:220px repeat(${dayCount},minmax(90px,1fr))"><span>Job / stage</span>${days.map(day => `<span class="${day === today ? 'today' : ''}">${new Date(day + 'T12:00:00').toLocaleDateString(undefined, { weekday:'short', day:'numeric' })}</span>`).join('')}</div>`;
    const rows = [...pipelineStages.map(([name]) => name), 'Other'].map(stage => {
        const grouped = jobs.filter(job => pipelineStage(job) === stage);
        if (!grouped.length) return '';
        return `<section class="pipeline-stage-lane"><h3>${esc(stage)} <small>${grouped.length}</small></h3>${grouped.map(job => {
            const byDay = days.map(day => items.filter(item => item.jobId === job.id && pipelineLocalDay(item.start) <= day && pipelineLocalDay(item.end) >= day));
            const jobItems=items.filter(item=>item.jobId===job.id),done=jobItems.filter(item=>['Completed','Ready'].includes(item.status)).length,progress=jobItems.length?Math.round(done/jobItems.length*100):0;
            return `<div class="pipeline-timeline-row"><div class="pipeline-job-flow-card"><button class="pipeline-job-link" data-open-job="${job.id}"><strong>${esc(job.title)}</strong><small>${esc(job.clientName || 'Client not linked')} · ${esc(job.status)}</small></button><div class="pipeline-job-flow"><span><i style="width:${progress}%"></i></span><small>${progress}%</small></div><div class="pipeline-job-flow-actions"><button type="button" class="action" data-pipeline-advance="${job.id}" title="Open the next workflow action">Next</button><button type="button" class="action" data-pipeline-manage="${job.id}" title="Transfer workspace through the job workflow">Move</button></div></div>${byDay.map((events, i) => `<div class="pipeline-day-cell ${days[i] === today ? 'today' : ''}" data-pipeline-day="${days[i]}">${events.map(event => pipelineItemMarkup(event,resourceMap)).join('')}</div>`).join('')}</div>`;
        }).join('')}</section>`;
    }).join('');
    const unlinked = items.filter(item => !item.jobId);
    const other = unlinked.length ? `<section class="pipeline-stage-lane"><h3>Unlinked schedule <small>${unlinked.length}</small></h3><div class="pipeline-timeline-row"><span class="pipeline-job-link"><strong>General bookings and resources</strong></span>${days.map(day => `<div class="pipeline-day-cell ${day === today ? 'today' : ''}" data-pipeline-day="${day}">${unlinked.filter(item => pipelineLocalDay(item.start) <= day && pipelineLocalDay(item.end) >= day).map(item => pipelineItemMarkup(item,resourceMap)).join('')}</div>`).join('')}</div></section>` : '';
    return `<div class="pipeline-timeline-scroll"><div class="pipeline-timeline" style="--pipeline-days:${dayCount}">${head}${rows || '<p>No jobs in this pipeline.</p>'}${other}</div></div>`;
}
function pipelineCalendar(items, resourceMap) {
    const controls=window.StudioScheduleCalendarControls;
    const first=pipelineCalendarPeriod==='month'?new Date(pipelineAnchor.getFullYear(),pipelineAnchor.getMonth(),1):pipelineCalendarPeriod==='week'?controls.startOfWeek(pipelineAnchor):new Date(pipelineAnchor.getFullYear(),pipelineAnchor.getMonth(),pipelineAnchor.getDate());
    const offset=pipelineCalendarPeriod==='month'?first.getDay():0;
    const count=pipelineCalendarPeriod==='month'?Math.ceil((offset+new Date(first.getFullYear(),first.getMonth()+1,0).getDate())/7)*7:pipelineCalendarPeriod==='week'?7:1;
    const dates=Array.from({length:count},(_,i)=>new Date(first.getFullYear(),first.getMonth(),first.getDate()-offset+i));
    const dayNames=pipelineCalendarPeriod==='day'?[first.toLocaleDateString(undefined,{weekday:'short'})]:pipelineCalendarPeriod==='week'?dates.map(date=>date.toLocaleDateString(undefined,{weekday:'short'})):['Sun','Mon','Tue','Wed','Thu','Fri','Sat'];
    return `<div class="pipeline-calendar"><div class="pipeline-calendar-days" style="grid-template-columns:repeat(${dayNames.length},minmax(0,1fr))">${dayNames.map(x => `<span>${x}</span>`).join('')}</div><div class="pipeline-calendar-grid" style="grid-template-columns:repeat(${dayNames.length},minmax(0,1fr))">${dates.map(date => {
        const day = pipelineLocalDay(date);
        const events = items.filter(item => pipelineLocalDay(item.start) <= day && pipelineLocalDay(item.end) >= day);
        return `<section class="pipeline-calendar-day ${date.getMonth() !== first.getMonth() ? 'outside' : ''} ${day === pipelineLocalDay(new Date()) ? 'today' : ''}" data-pipeline-day="${day}"><time datetime="${day}">${date.getDate()}</time>${events.map(item => pipelineItemMarkup(item, resourceMap)).join('')}</section>`;
    }).join('')}</div></div>`;
}

window.StudioScheduleWorkspaceCard=(()=>{
    let activeWorkspace='';
    let initialized=false;
    let visibleWorkspaces=[];
    let selectionHandler=null;
    const selected=()=>activeWorkspace;
    const allowed=items=>(Array.isArray(items)?items:[]).filter(workspace=>workspace.permissions?.accessWorkspace!==false&&workspace.permissions?.view!==false);
    const make=(id,name,icon)=>'<button type="button" data-schedule-workspace-filter="'+esc(id)+'" aria-pressed="'+String(activeWorkspace===String(id))+'" class="'+(activeWorkspace===String(id)?'is-active':'')+'"><span>'+icon+'</span><span>'+esc(name)+'</span></button>';
    function initialize(value=''){
        if(!initialized){activeWorkspace=String(value||'');initialized=true;}
        return activeWorkspace;
    }
    function select(value){activeWorkspace=String(value||'');initialized=true;return activeWorkspace;}
    function markup(){
        if(!visibleWorkspaces.length)return null;
        return make('','All Workspaces','▦')+visibleWorkspaces.map(workspace=>make(workspace.id,workspace.name,'▣')).join('');
    }
    function bindList(list){
        if(!list)return;
        list.querySelectorAll('[data-schedule-workspace-filter]').forEach(button=>button.addEventListener('click',()=>{
            select(button.dataset.scheduleWorkspaceFilter);
            const current=list.closest('[data-rail-card="workspaces"]')?.querySelector('.mw-today-rail-links');
            if(current)current.innerHTML=markup()||'';
            bindList(current);
            selectionHandler?.(activeWorkspace);
        }));
    }
    function render(workspaces,value,onSelect){
        initialize(value);
        const visible=allowed(workspaces);
        if(visible.length)visibleWorkspaces=visible;
        if(typeof onSelect==='function')selectionHandler=onSelect;
        const list=document.querySelector('#studioContextRail [data-rail-card="workspaces"] .mw-today-rail-links');
        if(list&&markup()!==null){list.innerHTML=markup();bindList(list);}
    }
    function bindRail(rail=document.querySelector('#studioContextRail')){
        const list=rail?.querySelector('[data-rail-card="workspaces"] .mw-today-rail-links');
        if(list&&markup()!==null){list.innerHTML=markup();bindList(list);}
    }
    return {render,initialize,select,selected,markup,bindRail};
})();
function renderPipelineWorkspaceFilters(workspaces){
    const selected=window.StudioScheduleWorkspaceCard.initialize(pipelineSelectedWorkspace);
    pipelineSelectedWorkspace=selected;
    window.StudioScheduleWorkspaceCard.render(workspaces,selected,value=>{
        pipelineSelectedWorkspace=value;
        showPipeline().catch(pipelineError);
    });
}
function pipelineJobGrid(jobs, tasks, workspaces) {
    const names=new Map(workspaces.map(w=>[String(w.id),w.name]));
    const statuses=StudioStatusOrder.sort(jobs.map(job=>job.status));
    return `<div class="operational-grid-scroll"><table class="operational-grid"><thead><tr><th><div class="pipeline-job-heading"><span>Job / Client</span><input type="search" id="pipelineSearch" aria-label="Search job or client" value="${esc(pipelineSearch)}" placeholder="Search…"></div></th><th><div class="pipeline-status-heading"><select id="pipelineStatusFilter" aria-label="Filter by job status"><option value="">All Open Jobs</option>${statuses.map(status=>`<option value="${esc(status)}" ${pipelineSelectedStatus===status?'selected':''}>${esc(status)}</option>`).join('')}</select></div></th><th>Job Lead</th><th>Next Action</th><th>Due Date</th><th>Actions</th></tr></thead><tbody>${jobs.map(job=>{
        const outstanding=tasks.filter(t=>t.jobId===job.id&&!['Completed','Cancelled'].includes(t.status)).sort((a,b)=>String(a.dueAtUtc||'9999').localeCompare(String(b.dueAtUtc||'9999')));
        const next=outstanding[0],due=next?.dueAtUtc||job.dueAtUtc;
        return `<tr data-pipeline-row="${esc(job.id)}"><td><button class="st-link" data-open-job="${esc(job.id)}">${esc(job.title||job.jobNumber||'Job')}</button><small>${esc(job.clientName||'Client not linked')}</small><small>${esc(names.get(String(job.workspaceId))||'Unassigned')}</small></td><td><span class="studio-status-pill">${esc(job.status)}</span><small>${esc(pipelineStage(job))}</small></td><td>${esc(studioFirstName(job.jobLead||job.assignedTo)||'Unassigned')}</td><td>${esc(next?.title||'Review job workflow')}</td><td>${due?esc(new Date(due).toLocaleDateString(undefined,{weekday:'short',month:'short',day:'2-digit',year:'numeric'})):'—'}</td><td><div class="operational-row-actions"><button type="button" class="action" data-pipeline-advance="${esc(job.id)}">Next</button><button type="button" class="action" data-pipeline-manage="${esc(job.id)}">Move</button></div></td></tr>`;
    }).join('')}</tbody></table>${jobs.length?'':'<p>No open jobs match these filters.</p>'}</div>`;
}
async function showPipeline() {
    const current=StudioPage.begin('pipeline');
    conversationView.classList.add('hidden');
    genericView.classList.remove('hidden');
    genericView.innerHTML = '<p>Loading Pipeline…</p>';
    const from = new Date(pipelineAnchor.getFullYear(), pipelineAnchor.getMonth() - 1, 1);
    const to = new Date(pipelineAnchor.getFullYear(), pipelineAnchor.getMonth() + 3, 1);
    const [jobs, entries, tasks, resources, profile, people, workspaces] = await Promise.all([
        json('/api/jobs'),
        json(`/api/schedule/entries?from=${encodeURIComponent(from.toISOString())}&to=${encodeURIComponent(to.toISOString())}`),
        json('/api/operations/activities'),
        json('/api/schedule/resources'),
        json('/api/auth/me'),
        json('/api/studio-employees?assignable=true').catch(() => []),
        json('/api/workspaces')
    ]);
    if(!current())return;
    pipelineSelectedWorkspace=window.StudioScheduleWorkspaceCard.initialize(pipelineSelectedWorkspace);
    studioRememberPeople(people);
    const candidates = jobs.filter(job => (pipelineSelectedStatus?job.status===pipelineSelectedStatus:!StudioSubjectPicker.isClosed(job)) && (!pipelineSelectedWorkspace||String(job.workspaceId)===pipelineSelectedWorkspace) && (!pipelineSearch||[job.title,job.clientName,job.jobNumber].join(' ').toLowerCase().includes(pipelineSearch.toLowerCase())));
    const selected = candidates.filter(job => !pipelineSelectedStage || pipelineStage(job) === pipelineSelectedStage);
    const resourceMap = new Map(resources.map(resource => [resource.id, resource]));
    const items = pipelineItems(entries, tasks, selected).filter(item => item.jobId || !pipelineSelectedStage);
    const undated = tasks.filter(task => selected.some(job => job.id===task.jobId) && !task.scheduledAtUtc && !task.dueAtUtc && !['Completed','Cancelled'].includes(task.status));
    const stages = [...pipelineStages.map(([name]) => name), 'Other'];
    const today = pipelineLocalDay(new Date());
    const nearby = items.filter(item => pipelineLocalDay(item.end) >= today && pipelineLocalDay(item.start) <= pipelineDayAt(new Date(), 7));
    const isAdmin = (profile.user || profile).isAdministrator === true;
    renderPipelineWorkspaceFilters(workspaces);
    const pipelineModeSwitch='<div class="pipeline-mode-switch" role="group" aria-label="Job pipeline view">'+['jobs','calendar','timeline'].map(view=>'<button type="button" data-pipeline-view="'+view+'" class="'+(pipelineView===view?'active':'')+'" aria-pressed="'+String(pipelineView===view)+'">'+view[0].toUpperCase()+view.slice(1)+'</button>').join('')+'</div>';

    genericView.innerHTML=StudioPage.html({title:'Job Pipeline',className:'st-pipeline-content',showHeader:false,summary:StudioPage.metrics([['Jobs',selected.length],['Resources',resources.length],['Next 7 days',nearby.length],['Tasks without dates',undated.length]])+'<div class="pipeline-stage-summary">'+stages.filter(stage=>candidates.some(job=>pipelineStage(job)===stage)||pipelineSelectedStage===stage).map(stage => `<button class="pipeline-stage-tile ${pipelineSelectedStage===stage?'active':''}" data-stage="${esc(stage)}" aria-pressed="${pipelineSelectedStage===stage}"><strong>${esc(stage)}</strong><span>${candidates.filter(job => pipelineStage(job) === stage).length}</span></button>`).join('')+'</div>',attention:'<p>No pipeline issues require attention.</p>',content:`${window.StudioScheduleCalendarControls.html('pipeline',pipelineAnchor,pipelineCalendarPeriod,pipelineModeSwitch)}

        <p class="pipeline-key"><span>◉ Scheduled work</span><span>✓ Task / deadline</span><span>${nearby.length} items in the next 7 days</span></p>
        <section class="pipeline-planning-surface">${pipelineView === 'jobs' ? pipelineJobGrid(selected,tasks,workspaces) : pipelineView === 'calendar' ? pipelineCalendar(items, resourceMap) : pipelineTimeline(selected, items, resourceMap)}</section>${undated.length?`<details class="pipeline-resource-panel"><summary>${undated.length} tasks without dates</summary>${undated.map(task=>`<article class="task-card"><button class="link-button" data-open-job="${task.jobId}">${esc(task.title)}</button><small>${esc(task.assignedTo?studioFirstName(task.assignedTo):'Unassigned')} · ${esc(task.status)}</small></article>`).join('')}</details>`:''}
        <details class="pipeline-resource-panel"><summary>Resources (${resources.length})</summary><div id="scheduleResources">${resources.map(resource => `<div class="job-row"><div><b>${esc(resource.name)}</b><small>${esc(resource.resourceType)} · ${resource.isActive ? 'Active' : 'Inactive'}</small></div></div>`).join('') || '<p>No resources configured.</p>'}</div>${isAdmin ? '<div class="pos-entry"><label>Name<input id="scheduleResourceName"></label><label>Type<select id="scheduleResourceType"><option>Staff</option><option>Room</option><option>Equipment</option><option>Vehicle</option></select></label><button class="action" id="scheduleAddResource">Add resource</button></div>' : ''}</details>
        <dialog id="scheduleEntryDialog"><form id="scheduleEntryForm" class="request-form"><h2>Schedule item</h2><label>Job<select data-job-picker id="scheduleJob"><option value="">No linked job (administrators only)</option>${StudioSubjectPicker.jobOptions(jobs)}</select></label><label>Title<input id="scheduleTitle" required></label><div class="row"><label>Starts<input id="scheduleStart" type="datetime-local" required></label><label>Ends<input id="scheduleEnd" type="datetime-local" required></label></div><label>Location<input id="scheduleLocation"></label><label>Resources<select id="scheduleResourceIds" multiple size="6">${resources.map(resource => `<option value="${resource.id}">${esc(resource.resourceType)} · ${esc(resource.name)}</option>`).join('')}</select></label><label>Notes<textarea id="scheduleNotes"></textarea></label><p role="alert"></p><div class="dialog-actions"><button type="button" data-cancel>Cancel</button><button type="submit" class="primary">Save</button></div></form></dialog>`});
    bindStudioScheduleSwitch(genericView);
    window.StudioScheduleCalendarControls.bind(genericView,{date:()=>pipelineAnchor,view:value=>{pipelineCalendarPeriod=value;showPipeline().catch(pipelineError);},shift:shiftPipeline,today:()=>{pipelineAnchor=new Date();showPipeline().catch(pipelineError);},dateSelected:date=>{pipelineAnchor=date;showPipeline().catch(pipelineError);}});
    StudioPage.bind(genericView,current);
    StudioPage.setCount(selected.length+' jobs');
    genericView.querySelectorAll('[data-open-job]').forEach(button => button.onclick = () => button.dataset.openJob && openWorkspaceJob(button.dataset.openJob).catch(error => alert(error.message)));
    genericView.querySelectorAll('[data-pipeline-view]').forEach(button => button.onclick = () => { pipelineView = button.dataset.pipelineView; showPipeline().catch(pipelineError); });
    genericView.querySelector('#pipelineStatusFilter').onchange=event=>{pipelineSelectedStatus=event.target.value;pipelineSelectedStage='';showPipeline().catch(pipelineError);};
    genericView.querySelector('#pipelineSearch').onchange=event=>{pipelineSearch=event.target.value;showPipeline().catch(pipelineError);};
    genericView.querySelectorAll('[data-pipeline-row]').forEach(row=>row.onclick=event=>{if(!event.target.closest('button,a,input,select'))void openWorkspaceJob(row.dataset.pipelineRow).catch(error=>alert(error.message));});
    genericView.querySelectorAll('[data-stage]').forEach(button => button.onclick = () => {pipelineSelectedStage = pipelineSelectedStage === button.dataset.stage ? '' : button.dataset.stage; showPipeline().catch(pipelineError);});
    genericView.querySelectorAll('[data-pipeline-manage]').forEach(button=>button.onclick=event=>{event.stopPropagation();openPipelineMove(button.dataset.pipelineManage).catch(error=>alert(error.message));});
    genericView.querySelectorAll('[data-pipeline-advance]').forEach(button=>button.onclick=async event=>{event.stopPropagation();await advancePipelineJob(button.dataset.pipelineAdvance,jobs);});
    installPipelineDrag(entries,tasks);
    genericView.querySelector('#scheduleEntryForm').onsubmit = saveScheduleEntry;
    genericView.querySelector('#scheduleEntryDialog [data-cancel]').onclick = () => genericView.querySelector('#scheduleEntryDialog').close();
    if (isAdmin) genericView.querySelector('#scheduleAddResource').onclick = () => createScheduleResource().catch(pipelineError);
}
async function openPipelineMove(jobId){
    // The connected workflow owns versioned workspace transfer and permission checks.
    if(window.StudioWorkflow?.open)return StudioWorkflow.open(jobId,'[data-wf-transfer]');
    return openWorkspaceJob(jobId);
}
async function advancePipelineJob(jobId){
    // Stages depend on the job's workflow; there is no direct status write from Pipeline.
    if(window.StudioWorkflow?.open)return StudioWorkflow.open(jobId);
    return openWorkspaceJob(jobId);
}
function installPipelineDrag(entries,tasks){
    const scheduleMap=new Map(entries.map(item=>[item.id,item])),taskMap=new Map(tasks.map(item=>[item.id,item]));
    genericView.querySelectorAll('[data-pipeline-item]').forEach(element=>{
        element.addEventListener('dragstart',event=>{
            event.stopPropagation();
            event.dataTransfer.effectAllowed='move';
            event.dataTransfer.setData('text/plain',JSON.stringify({id:element.dataset.pipelineItem,type:element.dataset.pipelineItemType}));
            element.classList.add('dragging');
        });
        element.addEventListener('dragend',()=>element.classList.remove('dragging'));
    });
    genericView.querySelectorAll('[data-pipeline-day]').forEach(cell=>{
        cell.addEventListener('dragover',event=>{event.preventDefault();event.dataTransfer.dropEffect='move';cell.classList.add('drag-target');});
        cell.addEventListener('dragleave',()=>cell.classList.remove('drag-target'));
        cell.addEventListener('drop',async event=>{
            event.preventDefault();cell.classList.remove('drag-target');
            let payload;try{payload=JSON.parse(event.dataTransfer.getData('text/plain'));}catch{return;}
            try{
                if(payload.type==='schedule')await reschedulePipelineEntry(scheduleMap.get(payload.id),cell.dataset.pipelineDay);
                else if(payload.type==='task')await reschedulePipelineTask(taskMap.get(payload.id),cell.dataset.pipelineDay);
                await showPipeline();
            }catch(error){alert(error.message||error);}
        });
    });
}
function movePipelineDate(value,targetDay){
    const original=new Date(value),target=new Date(targetDay+'T00:00:00');
    target.setHours(original.getHours(),original.getMinutes(),original.getSeconds(),original.getMilliseconds());
    return target;
}
async function reschedulePipelineEntry(entry,targetDay){
    if(!entry)return;
    const start=new Date(entry.startsAtUtc),end=new Date(entry.endsAtUtc),duration=end-start,newStart=movePipelineDate(entry.startsAtUtc,targetDay),newEnd=new Date(newStart.getTime()+duration);
    await json(`/api/schedule/entries/${entry.id}`,{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify({jobId:entry.jobId||null,bookingId:entry.bookingId||null,title:entry.title,startsAtUtc:newStart.toISOString(),endsAtUtc:newEnd.toISOString(),location:entry.location||null,status:entry.status,notes:entry.notes||null,resourceIds:entry.resourceIds||[],allowConflicts:false})});
}
async function reschedulePipelineTask(task,targetDay){
    if(!task)return;
    const scheduled=task.scheduledAtUtc?movePipelineDate(task.scheduledAtUtc,targetDay):null;
    let due=task.dueAtUtc?movePipelineDate(task.dueAtUtc,targetDay):null;
    if(task.scheduledAtUtc&&task.dueAtUtc){
        const duration=new Date(task.dueAtUtc)-new Date(task.scheduledAtUtc);
        due=new Date(scheduled.getTime()+Math.max(0,duration));
    }
    await json(`/api/operations/activities/${task.id}`,{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify({activityType:task.activityType,title:task.title,description:task.description||null,status:task.status,priority:task.priority,assignedTo:task.assignedTo||null,scheduledAtUtc:scheduled?scheduled.toISOString():null,dueAtUtc:due?due.toISOString():null,location:task.location||null,blockedReason:task.blockedReason||null,handoffTo:task.handoffTo||null})});
}

function pipelineError(error) { genericView.innerHTML = `<h1>Pipeline could not be loaded</h1><p role="alert">${esc(error.message || error)}</p><button class="action" id="pipelineRetry">Retry</button>`; genericView.querySelector('#pipelineRetry').onclick = () => showPipeline().catch(pipelineError); }
function shiftPipeline(direction) {
    if(pipelineCalendarPeriod==='month')pipelineAnchor=new Date(pipelineAnchor.getFullYear(),pipelineAnchor.getMonth()+direction,1);
    else if(pipelineCalendarPeriod==='week')pipelineAnchor=new Date(pipelineAnchor.getFullYear(),pipelineAnchor.getMonth(),pipelineAnchor.getDate()+direction*7);
    else pipelineAnchor=new Date(pipelineAnchor.getFullYear(),pipelineAnchor.getMonth(),pipelineAnchor.getDate()+direction);
    showPipeline().catch(pipelineError);
}
function openScheduleEntryForm(jobId) {
    const dialog = genericView.querySelector('#scheduleEntryDialog');
    const start = new Date(Date.now() + 3600000); start.setMinutes(0,0,0);
    const local = date => new Date(date.getTime() - date.getTimezoneOffset()*60000).toISOString().slice(0,16);
    dialog.querySelector('#scheduleJob').value = jobId || '';
    dialog.querySelector('#scheduleStart').value = local(start);
    dialog.querySelector('#scheduleEnd').value = local(new Date(start.getTime() + 3600000));
    dialog.showModal();
}
async function saveScheduleEntry(event) {
    event.preventDefault();
    const form = event.currentTarget;
    const selected = id => form.querySelector('#' + id);
    const start = new Date(selected('scheduleStart').value), end = new Date(selected('scheduleEnd').value);
    const error = form.querySelector('[role="alert"]');
    if (!Number.isFinite(+start) || !Number.isFinite(+end) || end <= start) { error.textContent = 'Choose a valid start and end time.'; return; }
    const button = form.querySelector('[type="submit"]'); button.disabled = true;
    try {
        await json('/api/schedule/entries', { method:'POST', headers:{'Content-Type':'application/json'}, body:JSON.stringify({
            jobId:selected('scheduleJob').value || null, bookingId:null, title:selected('scheduleTitle').value.trim(),
            startsAtUtc:start.toISOString(), endsAtUtc:end.toISOString(), location:selected('scheduleLocation').value.trim() || null,
            status:'Scheduled', notes:selected('scheduleNotes').value.trim() || null,
            resourceIds:Array.from(selected('scheduleResourceIds').selectedOptions, option => option.value), allowConflicts:false
        }) });
        form.closest('dialog').close();
        await showPipeline();
    } catch (cause) { error.textContent = cause.message || String(cause); button.disabled = false; }
}
async function createScheduleResource() {
    const name = genericView.querySelector('#scheduleResourceName').value.trim();
    if (!name) return alert('Resource name is required.');
    await json('/api/schedule/resources', { method:'POST', headers:{'Content-Type':'application/json'}, body:JSON.stringify({ name, resourceType:genericView.querySelector('#scheduleResourceType').value, isActive:true, notes:null }) });
    await showPipeline();
}

window.addEventListener('studio-schedule-item',()=>{
    if(document.body.dataset.studioView==='schedule'&&window.history.state?.studioSchedulePanel==='pipeline')openScheduleEntryForm();
});
// Older Schedule calls now open the dedicated photographer calendar.
async function showSchedule() { return window.studioNavigate('schedule'); }



