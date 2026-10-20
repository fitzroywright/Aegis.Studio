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
  try { const result = await api('/api/demo-login', { method: 'POST', body: JSON.stringify({ identity: button.dataset.identity }) }); showAuth(result.user); }
  catch (error) { $('auth-note').textContent = error.message; }
  finally { button.disabled = false; }
}));
$('sign-out').addEventListener('click', async () => { await api('/api/logout', { method: 'POST', body: '{}' }); $('gallery-grid').replaceChildren(); showAuth(null); });
let intakeCsrf='';
try{intakeCsrf=(await api('/api/intake-session')).csrf;}catch{}
const bookingForm=$('booking-form');
const kindLabel=document.createElement('label');kindLabel.textContent='Request type';const kindSelect=document.createElement('select');kindSelect.name='intakeKind';kindSelect.append(new Option('Booking request','Booking'),new Option('Inquiry','Inquiry'));kindLabel.append(kindSelect);bookingForm.prepend(kindLabel);
const rosterBox=document.createElement('details'),rosterTitle=document.createElement('summary'),rosterHost=document.createElement('div');rosterTitle.textContent='Subjects and roster (optional)';rosterBox.append(rosterTitle,rosterHost);bookingForm.append(rosterBox);
const publicRoster=StudioRosterEditor.open(rosterHost,{readFile:async file=>{const data=new FormData();data.append('file',file);const r=await fetch('/api/roster-file',{method:'POST',body:data,headers:{'X-CSRF-Token':intakeCsrf}});if(!r.ok)throw Error((await r.json()).message);return (await r.json()).cells;},save:async()=>({})});
$('booking-form').addEventListener('submit', async event => {
  event.preventDefault(); const form = event.currentTarget, button = form.querySelector('button[type=submit]'); button.disabled = true;
  const fields = Object.fromEntries(new FormData(form));
  const starts = new Date(`${fields.startsAt}T${fields.preferredTime || '10:00'}`); const ends = new Date(starts.getTime() + 60 * 60 * 1000);
  const payload = { clientName: fields.clientName, clientEmail: fields.clientEmail, clientPhone: fields.clientPhone, sessionType: fields.sessionType, startsAt: starts.toISOString(), endsAt: ends.toISOString(), location: '', notes: fields.notes };
  const roster=publicRoster.getRows();payload.roster=roster;payload.kind=form.elements.intakeKind.value;payload.timeZone='America/Jamaica';
  const encoded=JSON.stringify(payload);let draft=JSON.parse(sessionStorage.getItem('intake-submit')||'null');if(!draft||draft.body!==encoded){draft={key:crypto.randomUUID(),body:encoded};sessionStorage.setItem('intake-submit',JSON.stringify(draft));}const key=draft.key;
  try {
    const receipt = await api('/api/bookings', { method: 'POST', headers: { 'Idempotency-Key': key,'X-CSRF-Token':intakeCsrf }, body: draft.body });
    $('booking-result').hidden = false; $('booking-result').textContent = `Received — awaiting review.\nReference: ${receipt.intakeReference}\nSaved on the website; awaiting Studio import. This does not confirm your booking.`;
    sessionStorage.setItem('intake-receipt',JSON.stringify(receipt));sessionStorage.removeItem('intake-submit');
    $('track-form').hidden = false;
  } catch (error) { $('booking-result').hidden = false; $('booking-result').textContent = error.message; }
  finally { button.disabled = false; }
});
$('track-form').addEventListener('submit', async event => { event.preventDefault(); const reference = new FormData(event.currentTarget).get('reference').trim(); try { const result = await api(`/api/bookings/${encodeURIComponent(reference)}`,{headers:{'X-Intake-Token':JSON.parse(sessionStorage.getItem('intake-receipt')||'{}').accessToken||''}}); $('track-result').textContent = `Receipt: ${result.receiptStatus} · Studio: ${result.importStatus} · Review: ${result.reviewStatus||'Awaiting review'}`; } catch (error) { $('track-result').textContent = error.message; } });
$('submit-selections').addEventListener('click', async () => { const button = $('submit-selections'); button.disabled = true; try { const result = await api(`/api/galleries/${state.user.galleryReference}/selections/submit`, { method: 'POST', headers: { 'Idempotency-Key': state.selectionKey }, body: '{}' }); $('selection-result').textContent = `${result.versionIds.length} favourite(s) sent to the Studio team.`; state.selectionKey = crypto.randomUUID(); } catch (error) { $('selection-result').textContent = error.message; } finally { button.disabled = false; } });
try { const session = await api('/api/session'); showAuth(session.user); if (!session.demo) { document.querySelectorAll('[data-identity]').forEach(button => button.hidden = true); $('auth-note').textContent = 'Client sign-in is being connected.'; } } catch { /* The public booking form remains visible while sign-in recovers. */ }

