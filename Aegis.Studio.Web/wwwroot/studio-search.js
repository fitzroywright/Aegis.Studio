// Universal lookup follows the accessible job graph and permitted Front Desk records.
(() => {
 const form=document.getElementById('studioSearchForm');if(!form)return;
 form.onsubmit=async event=>{
  event.preventDefault();const term=document.getElementById('studioSearch').value.trim().toLowerCase();if(!term)return;
  const dialog=studioDialog('Search Studio','<p role="status">Searching…</p><div data-search-results></div>');
  const contains=values=>values.join(' ').toLowerCase().includes(term);
  try{
   const [jobs,auth,role]=await Promise.all([json('/api/jobs'),json('/api/auth/me'),json('/api/security/global-role-me')]);
   const subjects=await Promise.all(jobs.map(async job=>({job,subjects:await json('/api/jobs/'+job.id+'/subjects')})));
   const matches=subjects.filter(({job,subjects})=>contains([job.title,job.clientName,job.linkedClientName,job.id,...subjects.flatMap(s=>[s.name,s.reference,s.contactName,s.contactEmail,s.contactPhone])]));
   let clients=[],bookings=[];
   if((auth.user||auth).isAdministrator||role.frontDesk){
    const records=await Promise.all([json('/api/frontdesk/clients'),json('/api/frontdesk/booking-requests')]);
    clients=records[0].filter(c=>contains([c.displayName,c.email,c.phone]));
    bookings=records[1].map(b=>({...b,...b.request})).filter(b=>contains([b.title,b.clientName,b.clientEmail,b.clientPhone,b.externalBookingId]));
   }
   const host=dialog.querySelector('[data-search-results]');
   host.innerHTML=matches.map(({job,subjects})=>`<button class="op-link-row" data-search-job="${job.id}"><span><strong>${esc(job.title)}</strong><small>${esc(job.clientName)} · ${esc(job.status)}</small><small>${esc(subjects.filter(s=>contains([s.name,s.reference,s.contactEmail,s.contactPhone])).map(s=>s.name+' ('+s.reference+')').join(', '))}</small></span></button>`).join('')+clients.map(c=>`<button class="op-link-row" data-search-client="${c.id}"><span><strong>${esc(c.displayName)}</strong><small>Client · ${esc(c.email)} · ${esc(c.phone)}</small></span></button>`).join('')+bookings.map(b=>`<button class="op-link-row" data-search-booking="${b.id}"><span><strong>${esc(b.title)}</strong><small>Booking · ${esc(b.clientName)} · ${esc(b.status)}</small></span></button>`).join('')||'<p>No matching records.</p>';
   host.querySelectorAll('[data-search-job]').forEach(b=>b.onclick=()=>{dialog.close();openWorkspaceJob(b.dataset.searchJob).catch(e=>alert(e.message));});
   host.querySelectorAll('[data-search-client]').forEach(b=>b.onclick=async()=>{dialog.close();await studioNavigate('frontdesk');selectFrontDeskClient(b.dataset.searchClient);});
   host.querySelectorAll('[data-search-booking]').forEach(b=>b.onclick=async()=>{dialog.close();await studioNavigate('frontdesk');frontDeskTab='requests';frontDeskTerm=bookings.find(x=>x.id===b.dataset.searchBooking).title;renderFrontDesk();});
   dialog.querySelector('[role=status]').textContent=(matches.length+clients.length+bookings.length)+' records found';
  }catch(error){dialog.querySelector('[role=status]').textContent=error.message;}
 };
})();
