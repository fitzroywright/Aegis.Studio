const token = location.pathname.split('/').filter(Boolean)[1];
const portal = document.getElementById('portal');
const esc = value => String(value ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const money = (value, currency='JMD') => new Intl.NumberFormat('en-JM',{style:'currency',currency}).format(value||0);

async function api(url, options) {
  const response = await fetch(url, options);
  if (!response.ok) throw new Error(await response.text());
  return response.json();
}

async function load() {
  try {
    const data = await api(`/client/${token}/data`);
    portal.innerHTML = `
      <section class="hero">
        <div class="meta">${esc(data.clientName)}</div>
        <h1>${esc(data.jobTitle)}</h1>
        <div class="meta">${esc(data.status)}${data.eventDate ? ' · ' + esc(data.eventDate) : ''}</div>
        <div class="cards">
          <div class="card">Status<strong>${esc(data.status)}</strong></div>
          <div class="card">Invoices<strong>${data.invoices.length}</strong></div>
          <div class="card">Messages<strong>${data.messages.length}</strong></div>
        </div>
      </section>
      <section class="panel"><h2>Billing</h2>
        ${data.invoices.length ? data.invoices.map(invoice => `<div class="invoice"><div><b>${esc(invoice.invoiceNumber)}</b><div class="meta">Due ${new Date(invoice.dueAtUtc).toLocaleDateString()} · <span class="status">${esc(invoice.status)}</span></div></div><strong>${money(invoice.totalAmount, invoice.currency)}</strong></div>`).join('') : '<p class="meta">No invoices have been issued for this job.</p>'}
      </section>
      <section class="panel"><h2>Messages</h2><div class="messages">
        ${data.messages.length ? data.messages.map(message => `<div class="message"><small>${esc(message.author)} · ${new Date(message.createdAtUtc).toLocaleString()}</small>${esc(message.body)}</div>`).join('') : '<p class="meta">No messages yet.</p>'}
      </div>
      <div class="composer"><h3>Send Bryan Studios a message</h3><textarea id="portalMessage" placeholder="Write your message..."></textarea><button id="sendPortalMessage">Send message</button></div></section>`;
    document.getElementById('sendPortalMessage').onclick = sendMessage;
  } catch (error) {
    portal.innerHTML = `<div class="error">This client portal link is invalid, expired, or has been revoked.</div>`;
  }
}

async function sendMessage() {
  const box = document.getElementById('portalMessage');
  const body = box.value.trim();
  if (!body) return;
  const button = document.getElementById('sendPortalMessage');
  button.disabled = true;
  try {
    await api(`/client/${token}/messages`, {method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({body})});
    await load();
  } catch (error) {
    alert('Your message could not be sent.');
    button.disabled = false;
  }
}

load();
