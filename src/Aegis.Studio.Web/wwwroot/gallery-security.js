const baseOpenWorkspaceGalleryForSecurity = openWorkspaceGallery;
openWorkspaceGallery = async function(workspaceId, workspaceName) {
    await baseOpenWorkspaceGalleryForSecurity(workspaceId, workspaceName);
    try {
        const settings = await json(`/api/workspaces/${workspaceId}/gallery/access`);
        const section = document.createElement('section');
        section.className = 'job-section';
        section.innerHTML = `
            <h2>Client access & selections</h2>
            <p class="muted">Protect the public gallery with a password and control how many photographs the client may select.</p>
            <div class="pos-entry">
                <label><input id="galleryRequirePassword" type="checkbox" ${settings.requirePassword ? 'checked' : ''}> Require gallery password</label>
                <label>New password<input id="galleryClientPassword" type="password" placeholder="${settings.hasPassword ? 'Leave blank to keep current password' : 'Set a password'}"></label>
                <label>Session hours<input id="gallerySessionHours" type="number" min="1" max="720" value="${settings.sessionHours || 24}"></label>
                <label><input id="gallerySelectionsEnabled" type="checkbox" ${settings.selectionsEnabled ? 'checked' : ''}> Allow client selections</label>
                <label>Selection limit<input id="gallerySelectionLimit" type="number" min="1" placeholder="Unlimited" value="${settings.selectionLimit || ''}"></label>
                <button class="primary" onclick="saveGalleryAccess('${workspaceId}','${esc(workspaceName)}')">Save client access</button>
            </div>`;
        genericView.appendChild(section);
    } catch (error) {
        // Users without gallery administration rights can still use the normal gallery view.
    }
};

async function saveGalleryAccess(workspaceId, workspaceName) {
    const limitText = document.getElementById('gallerySelectionLimit').value.trim();
    const payload = {
        requirePassword: document.getElementById('galleryRequirePassword').checked,
        password: document.getElementById('galleryClientPassword').value || null,
        sessionHours: Number(document.getElementById('gallerySessionHours').value) || 24,
        selectionsEnabled: document.getElementById('gallerySelectionsEnabled').checked,
        selectionLimit: limitText ? Number(limitText) : null
    };
    await json(`/api/workspaces/${workspaceId}/gallery/access`, {method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify(payload)});
    await openWorkspaceGallery(workspaceId, workspaceName);
}
