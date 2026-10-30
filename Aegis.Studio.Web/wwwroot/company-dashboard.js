// Company overview. Operational commands remain in their canonical destination pages.
(() => {
    'use strict';
    let controller, resizeObserver, refreshTimer;
    // Tab-memory only: never persist customer/job data in browser storage.
    const snapshot = new Map();
    const REFRESH_MS = 60_000;
    const escape = value => String(value ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
    const closed = value => /^(Completed|Cancelled|Archived|Delivered|Published|Closed)$/i.test(value || '');
    const belongs = (item, id) => !id || String(item.workspaceId || item.request?.workspaceId || '') === id || (item.participatingWorkspaceIds || []).map(String).includes(id);
    const day = value => {
        if (!value) return '';
        const date = new Date(/^\d{4}-\d{2}-\d{2}$/.test(value) ? value + 'T12:00:00' : value);
        return Number.isFinite(+date) ? date.toLocaleDateString('en-CA') : '';
    };
    function project(data, workspace, selectedDay) {
        const jobs = (data.jobs || []).filter(j => belongs(j, workspace));
        const byId = new Map(jobs.map(j => [String(j.id), j]));
        const requests = (data.requests || []).filter(r => belongs(r, workspace) || byId.has(String(r.jobId)));
        const pending = requests.filter(r => !closed(r.status) && r.status !== 'Confirmed');
        const inquiries = (data.inquiries || []).filter(e => belongs(e, workspace) && !requests.some(r => String(r.request?.conversationId) === String(e.id)));
        const deposits = pending.filter(r => r.status === 'AwaitingDeposit' || Number(r.request?.requiredDeposit) > Number(r.recordedDeposit || 0));
        const unassigned = jobs.filter(j => !closed(j.status) && !(j.jobLead || j.jobOwner || j.assignedTo));
        const scheduled = (data.schedule || []).filter(s => s.status !== 'Cancelled' && (!workspace || byId.has(String(s.jobId)) || belongs(s, workspace)));
        const sessions = scheduled.filter(s => day(s.startsAtUtc) === selectedDay).sort((a,b) => Date.parse(a.startsAtUtc) - Date.parse(b.startsAtUtc));
        const tasks = (data.workload?.activities || []).filter(t => byId.has(String(t.jobId)));
        const deliveries = tasks.filter(t => /^(Delivery|Fulfillment)$/i.test(t.activityType || '') && !closed(t.status) && !closed(byId.get(String(t.jobId))?.status)).sort((a,b) => (Date.parse(a.dueAtUtc) || Infinity) - (Date.parse(b.dueAtUtc) || Infinity));
        const due = deliveries.filter(t => day(t.dueAtUtc) && day(t.dueAtUtc) <= selectedDay);
        const workloadJobs = (data.workload?.jobs || []).filter(j => belongs(j, workspace));
        const workload = window.StudioWorkload.model(data.workload || {}, workloadJobs);
        const lanes = ['ready','progress','qc','attention'].map(key => ({key,count:workloadJobs.filter(j => workload.jobLane(j,tasks.filter(t => String(t.jobId) === String(j.id))) === key).length}));
        const week = Array.from({length:7},(_,i) => {
            const date = new Date(selectedDay + 'T12:00:00'); date.setDate(date.getDate()+i+1);
            const dateKey = day(date);
            return {date:dateKey,sessions:scheduled.filter(s=>day(s.startsAtUtc)===dateKey).length,deliveries:deliveries.filter(t=>day(t.dueAtUtc)===dateKey).length};
        });
        return {jobs,byId,pending,inquiries,deposits,unassigned,sessions,deliveries,due,lanes,week};
    }
    async function render() {
        controller?.abort();
        resizeObserver?.disconnect();
        clearInterval(refreshTimer);
        const request = controller = new AbortController();
        const host = document.getElementById('genericView');
        const active = () => !request.signal.aborted && document.body.dataset.studioView === 'dashboard';
        const data = {}, failures = new Set(), ready = new Set();
        const selectedDay = day(new Date());
        const footerCount=document.getElementById('studioFooterRate');
        if(footerCount)footerCount.textContent='';
        let workspace = sessionStorage.getItem('studio.footer.workspace.selection') || '';
        const sources = {
            jobs:'/api/jobs',
            requests:'/api/frontdesk/booking-requests',
            inquiries:'/api/frontdesk/enquiries',
            schedule:'/api/today/schedule?from=' + encodeURIComponent(new Date(new Date().setHours(0,0,0,0)).toISOString()) + '&to=' + encodeURIComponent(new Date(new Date().setHours(192,0,0,0)).toISOString()),
            workload:'/api/production/workload',
            media:'/api/today/media-summary',
            resources:'/api/schedule/resources'
        };
        // Paint the last successfully fetched values immediately, then refresh in the background.
        for (const [key] of Object.entries(sources)) {
            const cached = snapshot.get(key);
            if (cached) { data[key] = cached.value; ready.add(key); }
        }
        let refreshing = false;
        async function refresh() {
            if (!active() || refreshing) return;
            refreshing = true;
            try {
                await Promise.all(Object.entries(sources).map(async ([key,url]) => {
                    try {
                        const value = await json(url,{signal:request.signal});
                        if (key === 'workload' ? !value || !Array.isArray(value.jobs) : !Array.isArray(value))
                            throw Error('Unexpected response');
                        if (!active()) return;
                        data[key] = value;
                        snapshot.set(key,{value,at:Date.now()});
                        ready.add(key);
                        failures.delete(key);
                    } catch (error) {
                        if (request.signal.aborted) return;
                        // A revoked session or permission must discard previously readable summaries.
                        if (error.status === 401 || error.status === 403) {
                            snapshot.delete(key);delete data[key];ready.delete(key);
                        }
                        // Retain the last good snapshot during temporary outages.
                        if (!ready.has(key) && !['media','resources'].includes(key)) failures.add(key);
                    }
                    paint();
                }));
            } finally { refreshing = false; }
        }
        const known = (...keys) => keys.every(k => ready.has(k)) && keys.every(k => !failures.has(k));
        const count = (n,...keys) => known(...keys) ? n : keys.some(k => failures.has(k)) ? 'Unavailable' : '…';
        const link = (label,key) => `<button type="button" class="cd-link" data-dashboard-link="${key}">${escape(label)} <span aria-hidden="true">→</span></button>`;
        const card = (title,body,action) => `<section class="cd-card"><header><h2>${escape(title)}</h2>${action || ''}</header>${body}</section>`;
        function paint() {
            if (!active()) return;
            const p = project(data,workspace,selectedDay);
            const queues = [
                ['New inquiries',count(p.inquiries.length,'inquiries','requests'),'inquiries'],
                ['Awaiting deposits',count(p.deposits.length,'requests'),'deposits'],
                ['Needs confirmation',count(p.pending.length,'requests'),'confirmation'],
                ['Unassigned job owners',count(p.unassigned.length,'jobs'),'unassigned'],
                ['Delivery work due',count(p.due.length,'workload','jobs'),'delivery']
            ];
            const unavailable = keys => keys.some(k => failures.has(k)) ? 'Unavailable or outside your permissions. Use Retry to check again.' : 'Loading…';
            const height = host.clientHeight || 800;
            const sessionLimit = height < 560 ? 3 : height < 630 ? 5 : 6;
            const deliveryLimit = height < 560 ? 3 : height < 630 ? 4 : 5;
            const thumbnails = new Map((data.media || []).map(m=>[String(m.jobId),m.thumbnailVersionId]));
            const resources = new Map((data.resources || []).map(r=>[String(r.id),r]));
            const thumbnail = id => {
                const version = thumbnails.get(String(id));
                return `<span class="cd-thumbnail" aria-hidden="true">▧${version ? `<img src="/api/jobs/${encodeURIComponent(id)}/versions/by-id/${encodeURIComponent(version)}/preview?size=thumbnail" alt="" loading="lazy">` : ''}</span>`;
            };
            const jobCell = (id,title) => {
                const job = p.byId.get(String(id));
                return `<span class="cd-job-cell">${thumbnail(id)}<span><strong title="${escape(title)}">${escape(title)}</strong><small>${escape(job?.clientName || job?.linkedClientName || '—')}</small></span></span>`;
            };
            const worker = s => s.photographerUserName || s.assignedTo || ((s.resourceIds || []).length ? (!ready.has('resources') ? 'Unavailable' : (s.resourceIds || []).map(id=>resources.get(String(id))).filter(r=>r && /Photographer|Worker|Staff|Person/i.test(r.resourceType || '')).map(r=>r.name || r.displayName).join(', ') || 'Not assigned') : 'Not assigned');
            const sessionRows = p.sessions.slice(0,sessionLimit).map(s=>`<tr><td><time>${escape(new Date(s.startsAtUtc).toLocaleTimeString([], {hour:'numeric',minute:'2-digit'}))}</time></td><td>${s.jobId ? `<button class="cd-link" data-dashboard-job="${escape(s.jobId)}">${jobCell(s.jobId,p.byId.get(String(s.jobId))?.title || s.title || 'Session')}</button>` : escape(s.title || 'Session')}</td><td title="${escape(worker(s))}">${escape(worker(s))}</td><td title="${escape(s.location || '—')}">${escape(s.location || '—')}</td><td><span class="cd-pill">${escape(s.status || 'Scheduled')}</span></td></tr>`).join('');
            const sessions = known('schedule','jobs') ? `<table class="cd-session-table"><thead><tr><th>Time</th><th>Job &amp; client</th><th>Photographer</th><th>Location</th><th>Status</th></tr></thead><tbody>${sessionRows || '<tr><td colspan="5">No sessions today.</td></tr>'}</tbody></table><p class="cd-chart-note">${Math.min(sessionLimit,p.sessions.length)} of ${p.sessions.length} sessions · View schedule for all.</p>` : `<p role="status">${unavailable(['schedule','jobs'])}</p>`;
            const weekMax = Math.max(2,Math.ceil(Math.max(0,...p.week.flatMap(d=>[d.sessions,d.deliveries]))/2)*2);
            const week = known('schedule','workload','jobs') ? `<div class="cd-week-chart"><div class="cd-week-axis" aria-hidden="true"><span>${weekMax}</span><span>${Math.round(weekMax/2)}</span><span>0</span></div><div class="cd-week-plot">${p.week.map(d=>`<button type="button" class="cd-week-day" data-dashboard-day="${d.date}" aria-label="${d.date}: ${d.sessions} sessions, ${d.deliveries} delivery activities"><span class="cd-week-bars" aria-hidden="true"><span class="cd-week-session" style="height:${d.sessions/weekMax*100}%"><b>${d.sessions}</b></span><span class="cd-week-delivery" style="height:${d.deliveries/weekMax*100}%"><b>${d.deliveries}</b></span></span><span class="cd-week-date">${escape(new Date(d.date+'T12:00:00').toLocaleDateString([],{weekday:'short'}))}<small>${escape(new Date(d.date+'T12:00:00').toLocaleDateString([],{month:'short',day:'numeric'}))}</small></span></button>`).join('')}</div></div><div class="cd-week-legend"><span><i class="cd-week-session"></i> Sessions</span><span><i class="cd-week-delivery"></i> Delivery activities</span></div>` : `<p role="status">${unavailable(['schedule','workload','jobs'])}</p>`;
            const queueMax = Math.max(1,...queues.map(([,n]) => typeof n === 'number' ? n : 0));
            const attention = `<div class="cd-bars" aria-label="Attention queue counts">${queues.map(([label,n,key]) => `<button type="button" class="cd-bar-row cd-queue-${key}" data-dashboard-link="${key}" ${typeof n === 'string' ? 'disabled' : ''}><span class="cd-bar-label">${escape(label)}</span><strong>${escape(n)}</strong><span class="cd-bar-track" aria-hidden="true"><span style="width:${typeof n === 'number' ? n / queueMax * 100 : 0}%"></span></span></button>`).join('')}</div><p class="cd-chart-note">Queue counts may overlap; a booking can need both a deposit and confirmation.</p>`;
            const laneNames = {ready:'Ready to Start',progress:'In Production',qc:'Quality Control',attention:'Rework'};
            const laneColors = {ready:'#6284aa',progress:'#b58a45',qc:'#438979',attention:'#bf6359'};
            const total = p.lanes.reduce((sum,l) => sum + l.count,0);
            let offset = 0;
            const segments = p.lanes.filter(l => l.count > 0).map(l => {
                const length = l.count / total * 100;
                const segment = `<circle class="cd-donut-segment" cx="60" cy="60" r="44" pathLength="100" fill="none" stroke="${laneColors[l.key]}" stroke-width="14" stroke-dasharray="${length} ${100-length}" stroke-dashoffset="${-offset}" transform="rotate(-90 60 60)"></circle>`;
                offset += length;
                return segment;
            }).join('');
            const production = known('workload','jobs') ? `<div class="cd-production-chart"><div class="cd-donut"><svg viewBox="0 0 120 120" role="img" aria-label="${escape('Production mix: '+p.lanes.map(l => laneNames[l.key]+' '+l.count).join(', '))}"><circle class="cd-donut-track" cx="60" cy="60" r="44" fill="none" stroke-width="14"></circle>${segments}</svg><span aria-hidden="true"><strong>${total}</strong><small>jobs</small></span></div><div class="cd-chart-legend">${p.lanes.map(l => `<button type="button" data-dashboard-link="production"><i aria-hidden="true" style="background:${laneColors[l.key]}"></i><span>${laneNames[l.key]}</span><strong>${l.count}</strong></button>`).join('')}</div></div><p class="cd-chart-note">${total ? 'One current production group per job.' : 'No production jobs in this workspace.'}</p>` : `<p role="status">${unavailable(['workload','jobs'])}</p>`;
            const deliveries = known('workload','jobs') ? `<table class="cd-delivery-table"><thead><tr><th>Job &amp; client</th><th>Output / activity</th><th>Due date</th><th>Status</th></tr></thead><tbody>${p.deliveries.slice(0,deliveryLimit).map(t=>`<tr><td><button class="cd-link" data-dashboard-job="${escape(t.jobId)}">${jobCell(t.jobId,p.byId.get(String(t.jobId))?.title || 'Job')}</button></td><td title="${escape(t.title || t.activityType)}">${escape(t.title || t.activityType)}</td><td>${t.dueAtUtc ? escape(new Date(t.dueAtUtc).toLocaleDateString([],{month:'short',day:'numeric'})) : 'Not set'}</td><td><span class="cd-pill ${day(t.dueAtUtc) && day(t.dueAtUtc)<=selectedDay ? 'cd-due' : ''}">${day(t.dueAtUtc) && day(t.dueAtUtc)<=selectedDay ? 'Due' : escape(String(t.status || 'Open').replace(/([a-z])([A-Z])/g,'$1 $2'))}</span></td></tr>`).join('') || '<tr><td colspan="4">No outstanding delivery work recorded.</td></tr>'}</tbody></table><p class="cd-chart-note">${Math.min(deliveryLimit,p.deliveries.length)} of ${p.deliveries.length} delivery activities · ${p.due.length} due or overdue.</p>` : `<p role="status">${unavailable(['workload','jobs'])}</p>`;
            const scope = document.getElementById('studioFooterWorkspaces')?.selectedOptions?.[0]?.textContent || 'Workspaces: All';
            host.innerHTML = `<section class="company-dashboard" aria-label="Today company overview"><header class="cd-heading"><div><h1>Studio at a glance</h1><p>Company-wide operations · ${escape(scope)}</p></div><div><time>${escape(new Date().toLocaleDateString([], {weekday:'short',day:'numeric',month:'short',year:'numeric'}))}</time><button class="action" data-dashboard-refresh>Refresh</button></div>${failures.size ? `<div class="cd-warning" role="status">Some summaries are unavailable or restricted. ${link('Retry','retry')}</div>` : ''}</header><div class="cd-main">${card('Production workload',production,link('Open Production','production'))}${card('Next 7 days',week,link('View schedule','schedule'))}${card('Needs attention',attention)}</div><div class="cd-bottom">${card('Today’s sessions',sessions,link('View schedule','schedule'))}${card('Deliveries & deadlines',deliveries,link('View deliveries','delivery'))}</div></section>`;
            host.querySelectorAll('.cd-thumbnail img').forEach(img=>img.onerror=()=>img.remove());
            host.querySelectorAll('[data-dashboard-day]').forEach(b=>b.onclick=()=>window.studioNavigate('schedule',{workspaceId:workspace,date:b.dataset.dashboardDay,mode:'day'}));
            host.querySelector('[data-dashboard-refresh]').onclick = () => { void refresh(); };
            host.querySelectorAll('[data-dashboard-job]').forEach(b => b.onclick = () => window.studioNavigate('workspaces',{jobId:b.dataset.dashboardJob}));
            host.querySelectorAll('[data-dashboard-link]').forEach(b => b.onclick = async () => {
                const key = b.dataset.dashboardLink;
                if (key === 'retry') return render();
                if (['inquiries','deposits','confirmation'].includes(key)) return window.studioNavigate('frontdesk',{queue:key,workspaceId:workspace});
                if (key === 'schedule') return window.studioNavigate('schedule',{workspaceId:workspace,date:selectedDay,mode:'day'});
                if (key === 'unassigned') return window.studioNavigate('jobs',{queue:'unassigned',workspaceId:workspace});
                return window.studioNavigate(key,{workspaceId:workspace});
            });
            window.studioSetBreadcrumb?.([{label:'Today',view:'dashboard'}]);
        }
        paint();
        if(typeof ResizeObserver === 'function'){
            let lastSize='';
            resizeObserver=new ResizeObserver(()=>{const size=host.clientWidth+':'+host.clientHeight;if(size!==lastSize){lastSize=size;paint();}});
            resizeObserver.observe(host);
        }
        if(typeof loadWorkspaces==='function')Promise.resolve(loadWorkspaces()).then(rows=>{
            if(!active())return;
            const available=Array.isArray(rows)?rows:[];
            if(workspace&&!available.some(w=>String(w.id)===workspace)){workspace='';sessionStorage.setItem('studio.footer.workspace.selection','');paint();}
        }).catch(()=>{});
        window.studioDashboardReady = refresh();
        refreshTimer = setInterval(() => { if (active() && !document.hidden) void refresh(); }, REFRESH_MS);
        return Promise.resolve();
    }
    window.StudioDashboard = {render,project,dispose(){controller?.abort();resizeObserver?.disconnect();clearInterval(refreshTimer);}};
})();


