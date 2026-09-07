async function showSchedule() {
    activateNavigation('schedule');
    conversationView.classList.add('hidden');
    genericView.classList.remove('hidden');
    const from = new Date();
    from.setDate(from.getDate() - 7);
    const to = new Date();
    to.setDate(to.getDate() + 60);
    const [entries, resources, jobs] = await Promise.all([
        json(`/api/schedule/entries?from=${encodeURIComponent(from.toISOString())}&to=${encodeURIComponent(to.toISOString())}`),
        json('/api/schedule/resources'),
        json('/api/jobs')
    ]);

    const resourceMap = new Map(resources.map(resource => [resource.id, resource]));
    genericView.innerHTML = `
        <div class="job-detail-header"><div><h1>Schedule</h1><p>Bookings, jobs, staff, rooms and equipment in one conflict-aware schedule.</p></div><button class="primary" onclick="openScheduleEntryForm()">+ Schedule item</button></div>
        <div class="cards">
            <div class="metric">Upcoming<strong>${entries.filter(item => new Date(item.endsAtUtc) >= new Date()).length}</strong></div>
            <div class="metric">Resources<strong>${resources.length}</strong></div>
            <div class="metric">Next 7 days<strong>${entries.filter(item => new Date(item.startsAtUtc) <= new Date(Date.now()+7*86400000) && new Date(item.endsAtUtc) >= new Date()).length}</strong></div>
        </div>
        <section class="job-section"><h2>Upcoming schedule</h2>${entries.length ? entries.map(entry => {
            const assigned = (entry.resourceIds || []).map(id => resourceMap.get(id)?.name).filter(Boolean).join(', ');
            return `<div class="job-row"><div><b>${esc(entry.title)}</b><div class="meta">${new Date(entry.startsAtUtc).toLocaleString()} → ${new Date(entry.endsAtUtc).toLocaleString()}${entry.location ? ' · '+esc(entry.location) : ''}</div><small>${esc(assigned || 'No resources assigned')} · ${esc(entry.status)}</small></div></div>`;
        }).join('') : '<p class="muted">Nothing scheduled yet.</p>'}</section>
        <section class="job-section"><h2>Resources</h2><div id="scheduleResources">${resources.length ? resources.map(resource => `<div class="job-row"><div><b>${esc(resource.name)}</b><div class="meta">${esc(resource.resourceType)} · ${resource.isActive ? 'Active' : 'Inactive'}</div></div></div>`).join('') : '<p class="muted">No staff, rooms or equipment configured yet.</p>'}</div><div class="pos-entry" style="margin-top:16px"><label>Name<input id="scheduleResourceName"></label><label>Type<select id="scheduleResourceType"><option>Staff</option><option>Room</option><option>Equipment</option><option>Vehicle</option></select></label><button class="action" onclick="createScheduleResource()">Add resource</button></div></section>
        <dialog id="scheduleEntryDialog"><form id="scheduleEntryForm" method="dialog" class="request-form"><h2>Schedule item</h2><label>Job<select id="scheduleJob"><option value="">No linked job</option>${jobs.map(job => `<option value="${job.id}">${esc(job.title)} · ${esc(job.clientName)}</option>`).join('')}</select></label><label>Title<input id="scheduleTitle" required></label><div class="row"><label>Starts<input id="scheduleStart" type="datetime-local" required></label><label>Ends<input id="scheduleEnd" type="datetime-local" required></label></div><label>Location<input id="scheduleLocation"></label><label>Resources<select id="scheduleResourceIds" multiple size="6">${resources.map(resource => `<option value="${resource.id}">${esc(resource.resourceType)} · ${esc(resource.name)}</option>`).join('')}</select></label><label>Notes<textarea id="scheduleNotes"></textarea></label><div class="dialog-actions"><button type="button" onclick="scheduleEntryDialog.close()">Cancel</button><button type="submit" class="primary">Save</button></div></form></dialog>`;

    scheduleEntryForm.onsubmit = saveScheduleEntry;
}

function openScheduleEntryForm() {
    const start = new Date(Date.now() + 3600000);
    start.setMinutes(0,0,0);
    const end = new Date(start.getTime() + 3600000);
    const local = date => new Date(date.getTime() - date.getTimezoneOffset()*60000).toISOString().slice(0,16);
    scheduleStart.value = local(start);
    scheduleEnd.value = local(end);
    scheduleEntryDialog.showModal();
}

async function saveScheduleEntry(event) {
    event.preventDefault();
    const resourceIds = Array.from(scheduleResourceIds.selectedOptions).map(option => option.value);
    const payload = {
        jobId: scheduleJob.value || null,
        bookingId: null,
        title: scheduleTitle.value.trim(),
        startsAtUtc: new Date(scheduleStart.value).toISOString(),
        endsAtUtc: new Date(scheduleEnd.value).toISOString(),
        location: scheduleLocation.value.trim() || null,
        status: 'Scheduled',
        notes: scheduleNotes.value.trim() || null,
        resourceIds,
        allowConflicts: false
    };
    try {
        await json('/api/schedule/entries', { method:'POST', headers:{'Content-Type':'application/json'}, body:JSON.stringify(payload) });
        scheduleEntryDialog.close();
        await showSchedule();
    } catch (error) {
        alert('Unable to schedule item. ' + (error.message || error));
    }
}

async function createScheduleResource() {
    const name = scheduleResourceName.value.trim();
    if (!name) return alert('Resource name is required.');
    await json('/api/schedule/resources', { method:'POST', headers:{'Content-Type':'application/json'}, body:JSON.stringify({ name, resourceType:scheduleResourceType.value, isActive:true, notes:null }) });
    await showSchedule();
}

const scheduleNavigationButton = document.querySelector('.brand-button[data-view="schedule"]');
if (scheduleNavigationButton) scheduleNavigationButton.onclick = () => showSchedule();
