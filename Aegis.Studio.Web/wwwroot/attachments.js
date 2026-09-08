const baseOpenConversation = openConversation;

openConversation = async function(id) {
    await baseOpenConversation(id);
    await tightenConversationExperience(id);
    await renderAttachments(id);
};

async function tightenConversationExperience(conversationId) {
    const conversation = await json('/api/conversations/' + conversationId);
    const workspace = workspaceCache.find(item => item.id === conversation.workspaceId);
    const composer = document.querySelector('.composer');
    const sendButton = composer?.querySelector('button.primary');
    const toggle = document.getElementById('internalToggle');

    if (composer && sendButton && toggle) {
        const identity = document.createElement('div');
        identity.className = 'reply-identity';
        identity.innerHTML = `<strong id="replyTarget">Reply to ${esc(conversation.contactName)}</strong><small id="replyRoute">From ${esc(workspace?.address || 'workspace mailbox')} to ${esc(conversation.contactAddress)}</small>`;
        composer.insertBefore(identity, composer.firstChild);

        const refreshReplyLabel = () => {
            const internal = toggle.checked;
            sendButton.textContent = internal ? 'Add internal note' : `Reply to ${conversation.contactName}`;
            document.getElementById('replyTarget').textContent = internal ? 'Internal note' : `Reply to ${conversation.contactName}`;
            document.getElementById('replyRoute').textContent = internal
                ? 'Visible only to Aegis staff'
                : `From ${workspace?.address || 'workspace mailbox'} to ${conversation.contactAddress}`;
        };
        toggle.addEventListener('change', refreshReplyLabel);
        refreshReplyLabel();
    }

    document.querySelectorAll('.message.external p').forEach(paragraph => collapseQuotedHistory(paragraph));
}

function collapseQuotedHistory(paragraph) {
    const text = paragraph.textContent || '';
    const markers = [
        '\nFrom:',
        '\n-----Original Message-----',
        '\nOn ',
        '\n________________________________',
        '\n> '
    ];
    let splitAt = -1;
    for (const marker of markers) {
        const candidate = text.indexOf(marker);
        if (candidate > 0 && (splitAt < 0 || candidate < splitAt)) {
            splitAt = candidate;
        }
    }

    if (splitAt < 0) {
        return;
    }

    const current = text.slice(0, splitAt).trim();
    const quoted = text.slice(splitAt).trim();
    paragraph.textContent = current;

    const details = document.createElement('details');
    details.className = 'quoted-history';
    const summary = document.createElement('summary');
    summary.textContent = 'Show previous email';
    const body = document.createElement('pre');
    body.textContent = quoted;
    details.append(summary, body);
    paragraph.after(details);
}

async function renderAttachments(conversationId) {
    const attachments = await json('/api/conversations/' + conversationId + '/attachments');
    const composer = document.querySelector('.composer');
    if (!composer) {
        return;
    }

    const section = document.createElement('section');
    section.className = 'attachment-section';
    section.innerHTML = `
        <div class="attachment-header">
            <div>
                <h3>Attachments</h3>
                <p>Files stay with the conversation and remain linked when it becomes a job.</p>
            </div>
            <label class="action attachment-upload-button">
                Add file
                <input id="conversationAttachment" type="file" hidden>
            </label>
        </div>
        <div class="attachment-list">
            ${attachments.length ? attachments.map(attachment => `
                <a class="attachment-row" href="/api/attachments/${attachment.id}" target="_blank" rel="noopener">
                    <div>
                        <b>${esc(attachment.fileName)}</b>
                        <small>${formatBytes(attachment.size)} · ${esc(attachment.visibility)} · ${new Date(attachment.createdAt).toLocaleString()}</small>
                    </div>
                    <span>Open</span>
                </a>`).join('') : '<p class="muted">No attachments yet.</p>'}
        </div>`;

    composer.parentNode.insertBefore(section, composer);

    document.getElementById('conversationAttachment').onchange = async event => {
        const file = event.target.files[0];
        if (!file) {
            return;
        }

        const internal = document.getElementById('internalToggle')?.checked === true;
        const data = new FormData();
        data.append('file', file);

        const response = await fetch(
            '/api/conversations/' + conversationId + '/attachments?visibility=' + encodeURIComponent(internal ? 'Internal' : 'External'),
            { method: 'POST', body: data });

        if (!response.ok) {
            alert(await response.text());
            return;
        }

        await openConversation(conversationId);
    };
}

function formatBytes(bytes) {
    if (bytes < 1024) {
        return bytes + ' B';
    }

    if (bytes < 1024 * 1024) {
        return (bytes / 1024).toFixed(1) + ' KB';
    }

    return (bytes / (1024 * 1024)).toFixed(1) + ' MB';
}