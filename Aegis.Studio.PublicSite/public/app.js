// Hash navigation keeps a single canonical instance of every service interface.
const pages = [...document.querySelectorAll('[data-page]')];
function showPage() {
  const requested = location.hash.slice(1);
  const active = pages.find(page => page.id === requested)?.id || 'home';
  for (const page of pages) page.hidden = page.id !== active;
  for (const link of document.querySelectorAll('.topbar nav a')) {
    if (link.hash === '#' + active) link.setAttribute('aria-current', 'page');
    else link.removeAttribute('aria-current');
  }
  document.title = active === 'home' ? 'Bryan’s Studios — More Than Photographs' :
    ({ booking: 'Book a Session', galleries: 'Client Galleries', workday: 'WorkDay' }[active] + ' — Bryan’s Studios');
}
showPage();
document.querySelector('.skip-link').addEventListener('click', event => {
  event.preventDefault();
  document.getElementById('main-content').focus();
});
window.addEventListener('hashchange', () => {
  showPage();
  window.scrollTo({ top: 0, behavior: 'instant' });
  document.getElementById('main-content').focus({ preventScroll: true });
});

const $ = id => document.getElementById(id);
const state = { user: null, page: null, selected: new Set(), selectionKey: crypto.randomUUID() };
async function api(path, options = {}) {
  const response = await fetch(path, { ...options, headers: { ...(options.body ? { 'Content-Type': 'application/json' } : {}), ...(options.headers || {}) }, credentials: 'same-origin' });
  const contentType = response.headers.get('content-type') || '';
  if (!response.ok) { let message = `Request failed (${response.status}).`; if (contentType.includes('json')) message = (await response.json()).message || message; throw new Error(message); }
  return contentType.includes('json') ? response.json() : response;
}
function showAuth(user) {
  state.user=user;
  $('signed-out').hidden=Boolean(user);
  $('signed-in').hidden=!user;
  $('client-link-form').hidden=!user||Boolean(user.linked);
  if(!user)return;
  $('client-name').textContent=(user.name||'Client').split(' ')[0];
  $('gallery-title').textContent=user.provider+' · '+(user.email||'Verified identity');
  $('gallery-status').textContent=user.linked?'Loading your published galleries…':
    'Your identity is verified. Connect your Studio Client ID with a one-time proof from Bryan\'s Front Desk.';
  loadClientGalleries();
}
async function openGallery(reference) {
  $('gallery-status').textContent = 'Loading your private gallery…'; $('gallery-grid').replaceChildren(); state.selected.clear();
  try {
    state.page = await api(`/api/galleries/${reference}`); $('gallery-title').textContent = state.page.title;
    $('gallery-status').textContent = state.page.finalDeliveryAvailable ? 'Your finished photographs are ready.' : 'Your proofs are ready. Choose the photographs you love.';
    for (const item of state.page.items) {
      const card = document.createElement('article'); card.className = 'proof';
      const image = document.createElement('img'); image.alt = item.fileName; image.loading = 'lazy'; image.src = `/api/galleries/${reference}/items/${item.versionId}/thumbnail`;
      const select = document.createElement('button'); select.type = 'button'; select.textContent = item.selected ? 'Selected ✓' : 'Choose photo'; select.setAttribute('aria-pressed', String(item.selected));
      if (item.selected) state.selected.add(item.versionId);
      select.onclick = async () => {
        select.disabled = true; const selected = select.getAttribute('aria-pressed') !== 'true';
        try { await api(`/api/galleries/${reference}/selections/${item.versionId}`, { method: 'PUT', body: JSON.stringify({ selected }) }); select.setAttribute('aria-pressed', String(selected)); select.textContent = selected ? 'Selected ✓' : 'Choose photo'; if (selected) state.selected.add(item.versionId); else state.selected.delete(item.versionId); state.selectionKey = crypto.randomUUID(); $('submit-selections').hidden = state.selected.size === 0; }
        catch (error) { $('gallery-status').textContent = error.message; } finally { select.disabled = false; }
      };
      card.append(image, select); $('gallery-grid').append(card);
      if (state.page.finalDeliveryAvailable) { const download = document.createElement('button'); download.type = 'button'; download.textContent = 'Download'; download.onclick = async () => { try { const file = await api(`/api/galleries/${reference}/items/${item.versionId}/download`); const link = document.createElement('a'); link.href = URL.createObjectURL(await file.blob()); link.download = item.fileName; link.click(); setTimeout(() => URL.revokeObjectURL(link.href), 30000); } catch (error) { $('gallery-status').textContent = error.message; } }; card.append(download); }
    }
    $('submit-selections').hidden = state.selected.size === 0;
  } catch (error) { $('gallery-status').textContent = error.message; }
}
async function loadClientGalleries(){try{const result=await api('/api/client-galleries'),picker=$('client-gallery-picker'),select=$('client-gallery-select'),items=result.items||[];select.replaceChildren();for(const item of items)select.add(new Option(item.title,item.reference));picker.hidden=items.length<2;if(items.length&&!items.some(x=>x.reference===state.user?.galleryReference)){state.user.galleryReference=items[0].reference;openGallery(items[0].reference);}if(items.length===1&&state.user?.galleryReference!==items[0].reference){state.user.galleryReference=items[0].reference;openGallery(items[0].reference);}if(state.user?.galleryReference)select.value=state.user.galleryReference;}catch{/* Keep the signed-in state usable if gallery listing is temporarily unavailable. */}}
document.getElementById('client-gallery-select').addEventListener('change',event=>{const reference=event.currentTarget.value;if(reference&&state.user){state.user.galleryReference=reference;openGallery(reference);}});

$('sign-out').addEventListener('click', async () => { await api('/api/logout', { method: 'POST', body: '{}' }); $('gallery-grid').replaceChildren(); showAuth(null); });
$('client-link-form').addEventListener('submit',async event=>{event.preventDefault();const form=event.currentTarget,button=form.querySelector('button[type=submit]'),result=$('client-link-result');button.disabled=true;result.textContent='';try{const code=new FormData(form).get('clientLinkCode').trim();await api('/api/v1/client-link-proofs/redeem',{method:'POST',body:JSON.stringify({code})});form.reset();const session=await api('/api/session');showAuth(session.user);result.textContent=session.user?.galleryReference?'Client connected. Your Studio galleries are shown below.':'Client connected. Galleries will appear here after Studio publishes them.';}catch(error){result.textContent=error.message;}finally{button.disabled=false;}});
document.getElementById('gallery-request-form').addEventListener('submit',async event=>{
  event.preventDefault();
  const form=event.currentTarget,button=form.querySelector('button[type=submit]'),result=document.getElementById('gallery-request-result');
  if(!form.reportValidity())return;
  const data=new FormData(form);
  const payload=Object.fromEntries(['clientName','clientEmail','telephone','bookingReference','notes'].map(name=>[name,String(data.get(name)||'').trim()]));
  // Retain the key across network failures so retrying cannot duplicate requests.
  const fingerprint=JSON.stringify(payload),prior=JSON.parse(sessionStorage.getItem('gallery-request-draft')||'null');
  const key=prior?.fingerprint===fingerprint?prior.key:crypto.randomUUID();
  sessionStorage.setItem('gallery-request-draft',JSON.stringify({fingerprint,key}));
  button.disabled=true;result.textContent='Submitting gallery request…';
  try{
    const response=await api('/api/gallery-requests',{method:'POST',headers:{'X-CSRF-Token':intakeCsrf,'Idempotency-Key':key},body:JSON.stringify(payload)});
    result.textContent='Request received. Reference: '+response.requestReference+'. Bryan\'s Front Desk will verify your identity before photographs are released.';
    sessionStorage.removeItem('gallery-request-draft');form.reset();
  }catch(error){result.textContent='Request could not be confirmed: '+error.message+'. Retry without changing the details.';}
  finally{button.disabled=false;}
});
let intakeCsrf='';
try{intakeCsrf=(await api('/api/intake-session')).csrf;}catch{}
const bookingForm=$('booking-form');
const kindLabel=document.createElement('label');kindLabel.textContent='Request type';const kindSelect=document.createElement('select');kindSelect.name='intakeKind';kindSelect.append(new Option('Booking request','Booking'),new Option('Inquiry','Inquiry'));kindLabel.append(kindSelect);bookingForm.prepend(kindLabel);
const rosterBox=document.createElement('details'),rosterTitle=document.createElement('summary'),rosterHost=document.createElement('div');rosterTitle.textContent='Subjects and roster (optional)';rosterBox.append(rosterTitle,rosterHost);bookingForm.insertBefore(rosterBox, bookingForm.querySelector('.submit-button'));
let draftReceipt=JSON.parse(sessionStorage.getItem('roster-draft')||'null'),draftData={};
if(draftReceipt)try{draftData=await api('/api/roster-drafts/'+draftReceipt.id,{headers:{'X-Intake-Token':draftReceipt.accessToken}});}catch(e){$('booking-result').hidden=false;$('booking-result').textContent='Could not restore saved roster: '+e.message;}
const publicRoster=StudioRosterEditor.open(rosterHost,{...draftData,readFile:async file=>{const data=new FormData();data.append('file',file);const r=await fetch('/api/roster-file',{method:'POST',body:data,headers:{'X-CSRF-Token':intakeCsrf}});if(!r.ok)throw Error((await r.json()).message);return StudioRosterEditor.csv((await r.json()).csv);},save:async value=>{
 const result=await api('/api/roster-drafts'+(draftReceipt?'/'+draftReceipt.id:''),{method:draftReceipt?'PUT':'POST',headers:{'X-CSRF-Token':intakeCsrf,...(draftReceipt?{'X-Intake-Token':draftReceipt.accessToken}:{})},body:JSON.stringify(value)});
 if(!draftReceipt){draftReceipt={id:result.id,accessToken:result.accessToken};sessionStorage.setItem('roster-draft',JSON.stringify(draftReceipt));}return result;
}});
const sessionSelect=bookingForm.elements.sessionTypeId,locationSelect=bookingForm.elements.locationCode,dateSelect=bookingForm.elements.bookingDate,slotSelect=bookingForm.elements.slotId;
let bookingCatalog=null,sessionById=new Map();
const money=minor=>new Intl.NumberFormat('en-JM',{style:'currency',currency:'JMD',maximumFractionDigits:2}).format(minor/100);
function renderSessionChoices(){
 const location=locationSelect.value,items=(bookingCatalog?.sessionTypes||[]).filter(x=>x.active&&(!location||x.locations.includes(location)));
 sessionSelect.replaceChildren(new Option(items.length?'Choose a session':'No sessions published for this location',''));
 for(const item of items)sessionSelect.add(new Option(`${item.name} · ${money(item.priceMinor)}`,item.id));
 sessionById=new Map(items.map(x=>[x.id,x]));$('booking-price-note').textContent='';
}
async function refreshBookingSlots(){
 slotSelect.replaceChildren(new Option('Loading Studio availability…',''));
 const session=sessionById.get(sessionSelect.value),day=dateSelect.value,location=locationSelect.value;
 if(!session||!day||!location){slotSelect.replaceChildren(new Option('Choose a location, session and date first',''));$('booking-price-note').textContent=session?`Session price ${money(session.priceMinor)} · deposit ${money(session.depositMinor)}.`:'';return;}
 try{
  const query=new URLSearchParams({from:day,to:day,locationCode:location,sessionTypeId:session.id});const result=await api('/api/v1/booking/availability?'+query);
  if(result.catalogRevision!==bookingCatalog.revision)throw new Error('Studio availability changed. Reload the booking page.');
  slotSelect.replaceChildren(new Option(result.slots.length?'Choose an available time':'No times are published for this date',''));
  for(const slot of result.slots){const start=new Date(slot.startsAt),label=new Intl.DateTimeFormat('en-JM',{timeZone:'America/Jamaica',hour:'numeric',minute:'2-digit'}).format(start);const option=new Option(label,slot.id);option.dataset.startsAt=slot.startsAt;option.dataset.endsAt=slot.endsAt;slotSelect.add(option);}
  $('booking-price-note').textContent=`Session price ${money(session.priceMinor)} · deposit ${money(session.depositMinor)}.`;
 }catch(error){slotSelect.replaceChildren(new Option('Availability could not be loaded',''));$('booking-catalog-note').textContent=error.message;}
}
async function loadBookingCatalog(){
 try{bookingCatalog=await api('/api/v1/booking/catalog');renderSessionChoices();$('booking-catalog-note').textContent=bookingCatalog.revision?'Session types and prices are published by Studio. Availability is confirmed by Studio.':'Studio has not published bookable sessions yet.';}
 catch(error){$('booking-catalog-note').textContent='Studio session information is temporarily unavailable. '+error.message;sessionSelect.replaceChildren(new Option('Sessions unavailable',''));}
}
const jamaicaToday=new Intl.DateTimeFormat('en-CA',{timeZone:'America/Jamaica',year:'numeric',month:'2-digit',day:'2-digit'}).format(new Date());dateSelect.min=jamaicaToday;
kindSelect.onchange=()=>{const booking=kindSelect.value==='Booking';for(const name of ['clientEmail','locationCode','sessionTypeId','bookingDate','slotId'])if(bookingForm.elements[name])bookingForm.elements[name].required=booking;};
locationSelect.addEventListener('change',()=>{renderSessionChoices();refreshBookingSlots();});
sessionSelect.addEventListener('change',refreshBookingSlots);dateSelect.addEventListener('change',refreshBookingSlots);
loadBookingCatalog();
$('booking-form').addEventListener('submit',async event=>{
 event.preventDefault();const form=event.currentTarget,button=form.querySelector('button[type=submit]');button.disabled=true;
 try{
  const f=Object.fromEntries(new FormData(form)),session=sessionById.get(f.sessionTypeId),slot=slotSelect.selectedOptions[0],payload={kind:f.intakeKind,clientName:f.clientName,clientEmail:f.clientEmail||'',clientPhone:f.clientPhone||'',sessionTypeId:f.sessionTypeId||'',sessionType:session?.name||'',catalogRevision:bookingCatalog?.revision||0,slotId:f.slotId||'',locationCode:f.locationCode||'',notes:f.notes||'',timeZone:'America/Jamaica',roster:publicRoster.getRows()};
  if(f.intakeKind==='Booking'&&(!slot?.dataset.startsAt||!slot.dataset.endsAt))throw new Error('Choose a time that Studio has published as available.');
  const encoded=JSON.stringify(payload);let draft=JSON.parse(sessionStorage.getItem('intake-submit')||'null');if(!draft||draft.body!==encoded){draft={key:crypto.randomUUID(),body:encoded};sessionStorage.setItem('intake-submit',JSON.stringify(draft));}
  const receipt=await api('/api/bookings',{method:'POST',headers:{'Idempotency-Key':draft.key,'X-CSRF-Token':intakeCsrf},body:draft.body});
  sessionStorage.setItem('intake-receipt',JSON.stringify(receipt));sessionStorage.removeItem('intake-submit');
  $('booking-result').hidden=false;$('booking-result').textContent=`Received — awaiting review.\nReference: ${receipt.intakeReference}\n${receipt.requiredDepositMinor?`Published deposit: ${money(receipt.requiredDepositMinor)} JMD.\n`:''}Saved on the website; awaiting Studio import. This does not confirm payment or final booking confirmation.`;
  $('track-form').hidden=false;
 }catch(e){$('booking-result').hidden=false;$('booking-result').textContent=e.message;}finally{button.disabled=false;}
});
$('track-form').addEventListener('submit', async event => { event.preventDefault(); const reference = new FormData(event.currentTarget).get('reference').trim(); try { const result = await api(`/api/bookings/${encodeURIComponent(reference)}`,{headers:{'X-Intake-Token':JSON.parse(sessionStorage.getItem('intake-receipt')||'{}').accessToken||''}}); $('track-result').textContent = `Receipt: ${result.receiptStatus} · Studio: ${result.importStatus} · Review: ${result.reviewStatus||'Awaiting review'}`; } catch (error) { $('track-result').textContent = error.message; } });
$('submit-selections').addEventListener('click', async () => { const button = $('submit-selections'); button.disabled = true; try { const result = await api(`/api/galleries/${state.user.galleryReference}/selections/submit`, { method: 'POST', headers: { 'Idempotency-Key': state.selectionKey }, body: '{}' }); $('selection-result').textContent = `${result.versionIds.length} favourite(s) saved; awaiting Studio import.`; state.selectionKey = crypto.randomUUID(); } catch (error) { $('selection-result').textContent = error.message; } finally { button.disabled = false; } });
try { const session=await api('/api/session');showAuth(session.user); }
catch { /* Booking and gallery-request forms remain visible if sign-in is unavailable. */ }



// Identity providers appear only after real client credentials are configured.
(async()=>{
 const links=document.getElementById('client-provider-links');
 if(!links)return;
 try{
  const reply=await api('/api/auth/providers');
  const names={microsoft:'Microsoft (Hotmail / Outlook)',facebook:'Facebook',yahoo:'Yahoo'};
  for(const name of reply.providers||[]){
   if(!names[name])continue;
   const link=document.createElement('a');link.className='social-button';
   link.href='/auth/'+encodeURIComponent(name);
   link.textContent='Continue with '+names[name];
   links.append(link);
  }
  if(!links.childElementCount)document.getElementById('auth-note').textContent='Secure client sign-in is not yet configured.';
 }catch{document.getElementById('auth-note').textContent='Secure sign-in is currently unavailable.';}
})();
