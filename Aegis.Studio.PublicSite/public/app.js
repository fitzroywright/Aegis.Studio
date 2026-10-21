const $ = id => document.getElementById(id);
const state = { user: null, page: null, selected: new Set(), selectionKey: crypto.randomUUID() };
async function api(path, options = {}) {
  const response = await fetch(path, { ...options, headers: { ...(options.body ? { 'Content-Type': 'application/json' } : {}), ...(options.headers || {}) }, credentials: 'same-origin' });
  const contentType = response.headers.get('content-type') || '';
  if (!response.ok) { let message = `Request failed (${response.status}).`; if (contentType.includes('json')) message = (await response.json()).message || message; throw new Error(message); }
  return contentType.includes('json') ? response.json() : response;
}
function showAuth(user) {
  state.user = user; $('signed-out').hidden = Boolean(user); $('signed-in').hidden = !user;
  if (user) { $('client-name').textContent = user.name.split(' ')[0]; $('gallery-title').textContent = user.provider + ' · ' + user.email; openGallery(user.galleryReference); }
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

document.querySelectorAll('[data-identity]').forEach(button => button.addEventListener('click', async () => {
  button.disabled = true;
  try { const result = await api('/api/demo-login', { method: 'POST', body: JSON.stringify({ identity: button.dataset.identity }) }); showAuth((await api('/api/session')).user); }
  catch (error) { $('auth-note').textContent = error.message; }
  finally { button.disabled = false; }
}));
$('sign-out').addEventListener('click', async () => { await api('/api/logout', { method: 'POST', body: '{}' }); $('gallery-grid').replaceChildren(); showAuth(null); });
let intakeCsrf='';
try{intakeCsrf=(await api('/api/intake-session')).csrf;}catch{}
const bookingForm=$('booking-form');
const kindLabel=document.createElement('label');kindLabel.textContent='Request type';const kindSelect=document.createElement('select');kindSelect.name='intakeKind';kindSelect.append(new Option('Booking request','Booking'),new Option('Inquiry','Inquiry'));kindLabel.append(kindSelect);bookingForm.prepend(kindLabel);
const rosterBox=document.createElement('details'),rosterTitle=document.createElement('summary'),rosterHost=document.createElement('div');rosterTitle.textContent='Subjects and roster (optional)';rosterBox.append(rosterTitle,rosterHost);bookingForm.append(rosterBox);
let draftReceipt=JSON.parse(sessionStorage.getItem('roster-draft')||'null'),draftData={};
if(draftReceipt)try{draftData=await api('/api/roster-drafts/'+draftReceipt.id,{headers:{'X-Intake-Token':draftReceipt.accessToken}});}catch(e){$('booking-result').hidden=false;$('booking-result').textContent='Could not restore saved roster: '+e.message;}
const publicRoster=StudioRosterEditor.open(rosterHost,{...draftData,readFile:async file=>{const data=new FormData();data.append('file',file);const r=await fetch('/api/roster-file',{method:'POST',body:data,headers:{'X-CSRF-Token':intakeCsrf}});if(!r.ok)throw Error((await r.json()).message);return StudioRosterEditor.csv((await r.json()).csv);},save:async value=>{
 const result=await api('/api/roster-drafts'+(draftReceipt?'/'+draftReceipt.id:''),{method:draftReceipt?'PUT':'POST',headers:{'X-CSRF-Token':intakeCsrf,...(draftReceipt?{'X-Intake-Token':draftReceipt.accessToken}:{})},body:JSON.stringify(value)});
 if(!draftReceipt){draftReceipt={id:result.id,accessToken:result.accessToken};sessionStorage.setItem('roster-draft',JSON.stringify(draftReceipt));}return result;
}});
kindSelect.onchange=()=>{const booking=kindSelect.value==='Booking';for(const name of ['clientEmail','sessionType','startsAt','preferredTime'])if(bookingForm.elements[name])bookingForm.elements[name].required=booking;};
$('booking-form').addEventListener('submit',async event=>{
 event.preventDefault();const form=event.currentTarget,button=form.querySelector('button[type=submit]');button.disabled=true;
 try{
  const f=Object.fromEntries(new FormData(form)),payload={kind:f.intakeKind,clientName:f.clientName,clientEmail:f.clientEmail||'',clientPhone:f.clientPhone||'',sessionType:f.sessionType||'',notes:f.notes||'',timeZone:'America/Jamaica',roster:publicRoster.getRows()};
  if(f.startsAt){const start=new Date(`${f.startsAt}T${f.preferredTime||'10:00'}:00-05:00`);payload.startsAt=start.toISOString();payload.endsAt=new Date(start.getTime()+3600000).toISOString();}
  const encoded=JSON.stringify(payload);let draft=JSON.parse(sessionStorage.getItem('intake-submit')||'null');if(!draft||draft.body!==encoded){draft={key:crypto.randomUUID(),body:encoded};sessionStorage.setItem('intake-submit',JSON.stringify(draft));}
  const receipt=await api('/api/bookings',{method:'POST',headers:{'Idempotency-Key':draft.key,'X-CSRF-Token':intakeCsrf},body:draft.body});
  sessionStorage.setItem('intake-receipt',JSON.stringify(receipt));sessionStorage.removeItem('intake-submit');
  $('booking-result').hidden=false;$('booking-result').textContent=`Received — awaiting review.\nReference: ${receipt.intakeReference}\nSaved on the website; awaiting Studio import. This does not confirm availability, payment or booking.`;
  $('track-form').hidden=false;
 }catch(e){$('booking-result').hidden=false;$('booking-result').textContent=e.message;}finally{button.disabled=false;}
});
$('track-form').addEventListener('submit', async event => { event.preventDefault(); const reference = new FormData(event.currentTarget).get('reference').trim(); try { const result = await api(`/api/bookings/${encodeURIComponent(reference)}`,{headers:{'X-Intake-Token':JSON.parse(sessionStorage.getItem('intake-receipt')||'{}').accessToken||''}}); $('track-result').textContent = `Receipt: ${result.receiptStatus} · Studio: ${result.importStatus} · Review: ${result.reviewStatus||'Awaiting review'}`; } catch (error) { $('track-result').textContent = error.message; } });
$('submit-selections').addEventListener('click', async () => { const button = $('submit-selections'); button.disabled = true; try { const result = await api(`/api/galleries/${state.user.galleryReference}/selections/submit`, { method: 'POST', headers: { 'Idempotency-Key': state.selectionKey }, body: '{}' }); $('selection-result').textContent = `${result.versionIds.length} favourite(s) sent to the Studio team.`; state.selectionKey = crypto.randomUUID(); } catch (error) { $('selection-result').textContent = error.message; } finally { button.disabled = false; } });
try { const session = await api('/api/session'); showAuth(session.user); if (!session.demo) { document.querySelectorAll('[data-identity]').forEach(button => button.hidden = true); $('auth-note').textContent = 'Client sign-in is being connected.'; } } catch { /* The public booking form remains visible while sign-in recovers. */ }

