async function showNotifications() {
    conversationView.classList.add('hidden');
    genericView.classList.remove('hidden');
    const [items, status] = await Promise.all([json('/api/notifications?count=200'), json('/api/email/status')]);
    const inApp = items.filter(item => item.channel === 'InApp');
    genericView.innerHTML = `
        <div class="job-detail-header"><div><h1>Notifications</h1><p>Studio event delivery across in-app, email, Slack and Teams.</p></div></div>
        <div class="cards">
            <div class="metric">In-app<strong>${inApp.length}</strong></div>
            <div class="metric">Graph email<strong class="metric-text">${status.configured ? 'Ready' : 'Needs secret'}</strong></div>
            <div class="metric">Slack<strong class="metric-text">${status.slackConfigured ? 'Ready' : 'Needs webhook'}</strong></div>
            <div class="metric">Teams<strong class="metric-text">${status.teamsConfigured ? 'Ready' : 'Needs webhook'}</strong></div>
        </div>
        <section class="job-section"><h2>Recent activity</h2>${inApp.length ? inApp.map(item => `<div class="job-row"><div><b>${esc(item.subject)}</b><div class="meta">${new Date(item.createdAtUtc).toLocaleString()} · ${esc(item.eventType)}</div><small>${esc(item.body)}</small></div></div>`).join('') : '<p class="muted">No notifications yet.</p>'}</section>`;
}

const notificationButton = document.getElementById('notificationButton');
if (notificationButton) notificationButton.onclick = () => showNotifications();
