// Company overview. Operational commands remain in their canonical destination pages.
(() => {
    'use strict';
    let controller;
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
        const sessions = (data.schedule || []).filter(s => s.status !== 'Cancelled' && day(s.startsAtUtc) === selectedDay && (!workspace || byId.has(String(s.jobId)) || belongs(s, workspace))).sort((a,b) => Date.parse(a.startsAtUtc) - Date.parse(b.startsAtUtc));
        const tasks = (data.workload?.activities || []).filter(t => byId.has(String(t.jobId)));
        const deliveries = tasks.filter(t => /^(Delivery|Fulfillment)$/i.test(t.activityType || '') && !closed(t.status) && !closed(byId.get(String(t.jobId))?.status)).sort((a,b) => (Date.parse(a.dueAtUtc) || Infinity) - (Date.parse(b.dueAtUtc) || Infinity));
        const due = deliveries.filter(t => day(t.dueAtUtc) && day(t.dueAtUtc) <= selectedDay);
        const workloadJobs = (data.workload?.jobs || []).filter(j => belongs(j, workspace));
        const workload = window.StudioWorkload.model(data.workload || {}, workloadJobs);
        const lanes = ['ready','progress','qc','attention'].map(key => ({key,count:workloadJobs.filter(j => workload.jobLane(j,tasks.filter(t => String(t.jobId) === String(j.id))) === key).length}));
        return {jobs,byId,pending,inquiries,deposits,unassigned,sessions,deliveries,due,lanes};
    }
    async function render() {
        controller?.abort();
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
            schedule:'/api/today/schedule?from=' + encodeURIComponent(new Date(new Date().setHours(0,0,0,0)).toISOString()) + '&to=' + encodeURIComponent(new Date(new Date().setHours(24,0,0,0)).toISOString()),
            workload:'/api/production/workload'
        };
        const known = (...keys) => keys.every(k => ready.has(k)) && keys.every(k => !failures.has(k));
        const count = (n,...keys) => known(...keys) ? n : keys.some(k => failures.has(k)) ? 'Unavailable' : '…';
        const link = (label,key) => `<button type="button" class="cd-link" data-dashboard-link="${key}">${escape(label)} <span aria-hidden="true">→</span></button>`;
        const card = (title,body,action) => `<section class="cd-card"><header><h2>${escape(title)}</h2>${action || ''}</header>${body}</section>`;
        function paint() {
            if (!active()) return;
            const p = project(data,workspace,selectedDay);
            const metrics = [
                ['New inquiries',count(p.inquiries.length,'inquiries','requests'),'inquiries','✉'],
                ['Awaiting deposits',count(p.deposits.length,'requests'),'deposits','$'],
                ['Needs confirmation',count(p.pending.length,'requests'),'confirmation','▣'],
                ['Unassigned job owners',count(p.unassigned.length,'jobs'),'unassigned','♙'],
                ['Sessions today',count(p.sessions.length,'schedule','jobs'),'schedule','◷'],
                ['Delivery work due',count(p.due.length,'workload','jobs'),'delivery','▤']
            ];
            const unavailable = keys => keys.some(k => failures.has(k)) ? 'Unavailable or outside your permissions. Use Retry to check again.' : 'Loading…';
            const sessions = known('schedule','jobs') ? `<ol class="cd-timeline" aria-label="Today's sessions in time order">${p.sessions.slice(0,5).map(s => `<li><time datetime="${escape(s.startsAtUtc)}">${escape(new Date(s.startsAtUtc).toLocaleTimeString([], {hour:'2-digit',minute:'2-digit'}))}</time><span class="cd-session-copy">${s.jobId ? `<button class="cd-link" data-dashboard-job="${escape(s.jobId)}">${escape(p.byId.get(String(s.jobId))?.title || s.title || 'Session')}</button>` : `<strong>${escape(s.title || 'Session')}</strong>`}<small>${escape(s.photographerUserName || s.assignedTo || 'Not assigned')}</small></span><span class="cd-pill">${escape(s.status || 'Scheduled')}</span></li>`).join('')}</ol>${p.sessions.length ? `<p class="cd-chart-note">Showing ${Math.min(5,p.sessions.length)} of ${p.sessions.length} sessions · View schedule for all.</p>` : '<p>No sessions today.</p>'}` : `<p role="status">${unavailable(['schedule','jobs'])}</p>`;
            const queues = metrics.filter((_,i) => [0,1,2,3,5].includes(i));
            const queueMax = Math.max(1,...queues.map(([,n]) => typeof n === 'number' ? n : 0));
            const attention = `<div class="cd-bars" aria-label="Attention queue counts">${queues.map(([label,n,key]) => `<button type="button" class="cd-bar-row" data-dashboard-link="${key}" ${typeof n === 'string' ? 'disabled' : ''}><span class="cd-bar-label">${escape(label)}</span><strong>${escape(n)}</strong><span class="cd-bar-track" aria-hidden="true"><span style="width:${typeof n === 'number' ? n / queueMax * 100 : 0}%"></span></span></button>`).join('')}</div><p class="cd-chart-note">Queue counts may overlap; a booking can need both a deposit and confirmation.</p>`;
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
            const production = known('workload','jobs') ? `<div class="cd-production-chart"><div class="cd-donut"><svg viewBox="0 0 120 120" role="img" aria-label="${escape('Production mix: '+p.lanes.map(l => laneNames[l.key]+' '+l.count).join(', '))}"><circle class="cd-donut-track" cx="60" cy="60" r="44" fill="none" stroke-width="14"></circle>${segments}</svg><span aria-hidden="true"><strong>${total}</strong><small>jobs</small></span></div><div class="cd-chart-legend">${p.lanes.map(l => `<button type="button" data-dashboard-link="production"><i aria-hidden="true" style="background:${laneColors[l.key]}"></i><span>${laneNames[l.key]}</span><strong>${l.count}</strong></button>`).join('')}</div></div><p class="cd-chart-note">${total ? 'Each job is counted once in its current production group.' : 'No production jobs in this workspace.'}</p>` : `<p role="status">${unavailable(['workload','jobs'])}</p>`;
            const deliveries = `<div class="cd-deliveries">${p.deliveries.slice(0,4).map(t => `<button data-dashboard-job="${escape(t.jobId)}"><span><strong>${escape(p.byId.get(String(t.jobId))?.title || 'Job')}</strong><small>${escape(t.title || 'Delivery work')}</small></span><span class="cd-pill">${t.dueAtUtc ? escape(new Date(t.dueAtUtc).toLocaleDateString()) : 'No due date'}</span></button>`).join('') || `<p>${known('workload','jobs') ? 'No outstanding delivery work recorded.' : unavailable(['workload','jobs'])}</p>`}</div>`;
            const scope = document.getElementById('studioFooterWorkspaces')?.selectedOptions?.[0]?.textContent || 'Workspaces: All';
            host.innerHTML = `<section class="company-dashboard" aria-label="Today company overview"><header class="cd-heading"><div><h1>Studio at a glance</h1><p>Company-wide operations · ${escape(scope)}</p><small>All work visible to you. Delivery counts represent recorded delivery activities.</small></div><div><time>${escape(new Date().toLocaleDateString([], {weekday:'short',day:'numeric',month:'short',year:'numeric'}))}</time><button class="action" data-dashboard-refresh>Refresh</button></div></header>${failures.size ? `<div class="cd-warning" role="status">Some summaries are unavailable or restricted. ${link('Retry','retry')}</div>` : ''}<div class="cd-metrics">${metrics.map(([label,n,key,icon]) => `<button class="cd-metric" data-dashboard-link="${key}" ${typeof n === 'string' ? 'disabled' : ''}><span class="cd-icon" aria-hidden="true">${icon}</span><span>${label}<strong>${escape(n)}</strong><small>View →</small></span></button>`).join('')}</div><div class="cd-main">${card('Production workload',production,link('Open Production','production'))}${card('Needs attention',attention)}</div><div class="cd-bottom">${card('Today’s sessions',sessions,link('View schedule','schedule'))}${card('Upcoming delivery work',deliveries,link('View deliveries','delivery'))}</div></section>`;
            host.querySelector('[data-dashboard-refresh]').onclick = () => render();
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
        if(typeof loadWorkspaces==='function')Promise.resolve(loadWorkspaces()).then(rows=>{
            if(!active())return;
            const available=Array.isArray(rows)?rows:[];
            if(workspace&&!available.some(w=>String(w.id)===workspace)){workspace='';sessionStorage.setItem('studio.footer.workspace.selection','');paint();}
        }).catch(()=>{});
        window.studioDashboardReady = Promise.all(Object.entries(sources).map(async ([key,url]) => {
            try {
                const value = await json(url,{signal:request.signal});
                if (key === 'workload' ? !value || !Array.isArray(value.jobs) : !Array.isArray(value)) throw Error('Unexpected response');
                data[key] = value; ready.add(key);
            } catch (error) {
                if (request.signal.aborted) return;
                failures.add(key);
            }
            paint();
        }));
        return Promise.resolve();
    }
    window.StudioDashboard = {render,project,dispose(){controller?.abort();}};
})();

