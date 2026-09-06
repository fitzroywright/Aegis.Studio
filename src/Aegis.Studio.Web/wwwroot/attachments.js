const baseOpenConversation = openConversation;

openConversation = async function(id) {
    await baseOpenConversation(id);
    await renderAttachments(id);
};

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
                <p>Files stay with the conversation and will remain linked when it becomes a job.</p>
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
