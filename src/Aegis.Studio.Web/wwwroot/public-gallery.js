const token = location.pathname.split('/').filter(Boolean)[1];
const titleEl = document.getElementById('galleryTitle');
const descriptionEl = document.getElementById('galleryDescription');
const emptyState = document.getElementById('emptyState');
const experience = document.getElementById('galleryExperience');
const previewImage = document.getElementById('previewImage');
const previewCaption = document.getElementById('previewCaption');
const thumbnailStrip = document.getElementById('thumbnailStrip');
const galleryGrid = document.getElementById('galleryGrid');
const previousButton = document.getElementById('previousImage');
const nextButton = document.getElementById('nextImage');
const loginPanel = document.getElementById('galleryLoginPanel');
const loginForm = document.getElementById('galleryLoginForm');
const passwordInput = document.getElementById('galleryPassword');
const loginStatus = document.getElementById('galleryLoginStatus');
const selectionBar = document.getElementById('selectionBar');
const selectionCount = document.getElementById('selectionCount');
const submitSelectionsButton = document.getElementById('submitSelections');
let gallery = null;
let selectedIndex = 0;
let selections = new Map();

async function request(url, options) {
  const response = await fetch(url, options);
  if (!response.ok) {
    const error = new Error(await response.text());
    error.status = response.status;
    throw error;
  }
  return response.status === 204 ? null : response.json();
}

async function loadGallery() {
  try {
    gallery = await request(`/public/galleries/${token}`);
    loginPanel.classList.add('hidden');
    await loadSelections();
    renderGallery();
  } catch (error) {
    if (error.status === 401) {
      loginPanel.classList.remove('hidden');
      experience.classList.add('hidden');
      emptyState.classList.add('hidden');
      return;
    }
    emptyState.classList.remove('hidden');
    emptyState.querySelector('h2').textContent = 'Gallery unavailable';
  }
}

async function loadSelections() {
  try {
    const values = await request(`/public/galleries/${token}/selections`);
    selections = new Map(values.filter(x => x.selected).map(x => [x.itemId, x]));
  } catch {
    selections = new Map();
  }
}

function renderGallery() {
  titleEl.textContent = gallery.title || 'Client Gallery';
  descriptionEl.textContent = gallery.description || '';
  const items = gallery.items || [];
  if (!items.length) {
    emptyState.classList.remove('hidden');
    experience.classList.add('hidden');
    return;
  }
  emptyState.classList.add('hidden');
  experience.classList.remove('hidden');
  selectedIndex = Math.min(selectedIndex, items.length - 1);
  renderPreview();
  thumbnailStrip.innerHTML = items.map((item, index) => `<button class="thumb ${index === selectedIndex ? 'active' : ''}" data-index="${index}"><img src="${item.thumbnailUrl}" alt="${escapeHtml(item.caption || item.fileName)}"></button>`).join('');
  galleryGrid.innerHTML = items.map((item, index) => `<article class="gallery-card" data-index="${index}"><img src="${item.thumbnailUrl}" alt="${escapeHtml(item.caption || item.fileName)}"><div class="gallery-card-actions"><button class="select-button ${selections.has(item.id) ? 'selected' : ''}" data-select-id="${item.id}">${selections.has(item.id) ? 'Selected ✓' : 'Select'}</button><button class="preview-button" data-index="${index}">Preview</button></div></article>`).join('');
  document.querySelectorAll('[data-index]').forEach(element => element.addEventListener('click', event => {
    if (event.target.closest('[data-select-id]')) return;
    selectedIndex = Number(element.dataset.index); renderPreview(); renderGallery();
  }));
  document.querySelectorAll('[data-select-id]').forEach(button => button.addEventListener('click', async event => {
    event.stopPropagation();
    const itemId = button.dataset.selectId;
    const selected = !selections.has(itemId);
    try {
      const result = await request(`/public/galleries/${token}/selections/${itemId}`, { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ selected, note: null }) });
      if (result.selected) selections.set(itemId, result); else selections.delete(itemId);
      updateSelectionBar(); renderGallery();
    } catch (error) { alert(readError(error)); }
  }));
  updateSelectionBar();
}

function renderPreview() {
  const item = gallery.items[selectedIndex];
  previewImage.src = item.previewUrl;
  previewImage.alt = item.caption || item.fileName;
  previewCaption.textContent = item.caption || '';
}

function updateSelectionBar() {
  selectionCount.textContent = `${selections.size} selected`;
  selectionBar.classList.toggle('hidden', !gallery?.items?.length);
}

previousButton.addEventListener('click', () => { if (!gallery?.items?.length) return; selectedIndex = (selectedIndex - 1 + gallery.items.length) % gallery.items.length; renderGallery(); });
nextButton.addEventListener('click', () => { if (!gallery?.items?.length) return; selectedIndex = (selectedIndex + 1) % gallery.items.length; renderGallery(); });

loginForm.addEventListener('submit', async event => {
  event.preventDefault(); loginStatus.textContent = 'Signing in…';
  try {
    await request(`/public/galleries/auth/${token}`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ password: passwordInput.value }) });
    passwordInput.value = ''; loginStatus.textContent = ''; await loadGallery();
  } catch { loginStatus.textContent = 'That password was not accepted.'; }
});

submitSelectionsButton.addEventListener('click', async () => {
  if (!selections.size) return alert('Select at least one photograph first.');
  const clientName = prompt('Your name'); if (clientName === null) return;
  const clientEmail = prompt('Your email address'); if (clientEmail === null) return;
  const note = prompt('Any notes for Bryan Studios?', '') ?? '';
  try {
    const result = await request(`/public/galleries/${token}/selections/submit`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ clientName, clientEmail, note }) });
    alert(`Thank you. ${result.selectionCount} selection(s) were submitted.`);
  } catch (error) { alert(readError(error)); }
});

function readError(error) { try { return JSON.parse(error.message).error || 'The request could not be completed.'; } catch { return 'The request could not be completed.'; } }
function escapeHtml(value) { return String(value ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c])); }

loadGallery();
