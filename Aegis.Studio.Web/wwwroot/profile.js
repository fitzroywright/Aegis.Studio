(() => {
    const dialog = document.getElementById('profileDialog');
    const button = document.getElementById('profileButton');
    const form = document.getElementById('profileForm');
    const close = document.getElementById('closeProfile');
    const cancel = document.getElementById('cancelProfile');
    const status = document.getElementById('profileStatus');

    if (!dialog || !button || !form) return;

    const fields = {
        emailEnabled: document.getElementById('prefEmail'),
        inAppEnabled: document.getElementById('prefInApp'),
        slackEnabled: document.getElementById('prefSlack'),
        teamsEnabled: document.getElementById('prefTeams'),
        whatsappEnabled: document.getElementById('prefWhatsApp'),
        newWorkspaceMessage: document.getElementById('prefWorkspaceMessage'),
        jobAssigned: document.getElementById('prefJobAssigned'),
        bookingChanged: document.getElementById('prefBookingChanged'),
        paymentChanged: document.getElementById('prefPaymentChanged'),
        jobStatusChanged: document.getElementById('prefJobStatusChanged'),
        mentionsAndReplies: document.getElementById('prefMentions')
    };

    function apply(profile) {
        const messaging = profile.messaging || {};
        for (const [key, element] of Object.entries(fields)) element.checked = !!messaging[key];
        document.getElementById('prefWhatsAppMobile').value = messaging.whatsAppMobile || '';
        document.getElementById('prefDelivery').value = messaging.deliveryMode || 'Immediate';
        document.getElementById('prefQuietStart').value = messaging.quietHoursStart || '';
        document.getElementById('prefQuietEnd').value = messaging.quietHoursEnd || '';
    }

    function payload() {
        const value = {};
        for (const [key, element] of Object.entries(fields)) value[key] = element.checked;
        value.whatsAppMobile = document.getElementById('prefWhatsAppMobile').value.trim() || null;
        value.deliveryMode = document.getElementById('prefDelivery').value;
        value.quietHoursStart = document.getElementById('prefQuietStart').value || null;
        value.quietHoursEnd = document.getElementById('prefQuietEnd').value || null;
        return value;
    }

    async function load() {
        status.textContent = 'Loading…';
        const response = await fetch('/api/profile');
        if (!response.ok) {
            status.textContent = 'Could not load profile.';
            return;
        }
        apply(await response.json());
        status.textContent = '';
    }

    button.addEventListener('click', async () => {
        dialog.showModal();
        await load();
    });
    close?.addEventListener('click', () => dialog.close());
    cancel?.addEventListener('click', () => dialog.close());

    form.addEventListener('submit', async event => {
        event.preventDefault();
        status.textContent = 'Saving…';
        const response = await fetch('/api/profile/messaging', {
            method: 'PUT',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(payload())
        });
        if (!response.ok) {
            status.textContent = 'Could not save preferences.';
            return;
        }
        apply(await response.json());
        status.textContent = 'Saved.';
        setTimeout(() => dialog.close(), 450);
    });

})();


