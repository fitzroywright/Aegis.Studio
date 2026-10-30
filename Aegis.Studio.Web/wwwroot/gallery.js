async function openWorkspaceGallery(workspaceId, workspaceName) {
    let gallery;
    try {
        gallery = await json(`/api/workspaces/${workspaceId}/gallery`);
    } catch (error) {
        alert(error.message || error);
        return;
    }

    conversationView.classList.add('hidden');
    genericView.classList.remove('hidden');
    const items = gallery.items || [];
    genericView.innerHTML = `
        <div class="job-detail-header">
            <div><button class="link-button" onclick="showWorkspaces()">← Workspaces</button><h1>${esc(workspaceName)} — Gallery</h1><p>Internal gallery with client-ready previews, thumbnails and publishing.</p></div>
            <div class="job-actions">
                ${gallery.isPublished ? `<a class="action" href="/g/${gallery.publicToken}" target="_blank" rel="noopener">Open public gallery</a><button class="action" onclick="unpublishWorkspaceGallery('${workspaceId}','${esc(workspaceName)}')">Unpublish</button>` : `<button class="primary" onclick="publishWorkspaceGallery('${workspaceId}','${esc(workspaceName)}')">Publish</button>`}
            </div>
        </div>
        <section class="job-section">
            <div class="pos-entry">
                <label>Gallery title<input id="galleryTitle" value="${esc(gallery.title || workspaceName)}"></label>
                <label>Description<input id="galleryDescription" value="${esc(gallery.description || '')}"></label>
                <button class="action" onclick="saveGalleryDetails('${workspaceId}','${esc(workspaceName)}')">Save details</button>
            </div>
        </section>
        <section class="job-section">
            <h2>Add images</h2>
            <p class="muted">Studio generates a client preview and thumbnail automatically while retaining the original.</p>
            <input id="galleryFiles" type="file" accept="image/*" multiple>
            <button class="primary" onclick="uploadGalleryImages('${workspaceId}','${esc(workspaceName)}')">Upload selected images</button>
        </section>
        <section class="job-section">
            <h2>Images</h2>
            <div class="gallery-admin-grid">${items.length ? items.map(item => `
                <article class="gallery-admin-card">
                    <img src="/api/workspaces/${workspaceId}/gallery/items/${item.id}/thumbnail" alt="${esc(item.caption || item.fileName)}" onclick="previewGalleryItem('${workspaceId}','${item.id}')">
                    <div><b>${esc(item.fileName)}</b><small>${item.isVisible ? 'Visible' : 'Hidden'} · order ${item.sortOrder}</small></div>
                    <div class="job-actions"><button class="action" onclick="editGalleryItem('${workspaceId}','${item.id}','${esc(item.caption || '')}',${item.sortOrder},${item.isVisible},'${esc(workspaceName)}')">Edit</button><button class="action" onclick="deleteGalleryItem('${workspaceId}','${item.id}','${esc(workspaceName)}')">Delete</button></div>
                </article>`).join('') : '<p class="muted">No images yet.</p>'}</div>
        </section>`;
}

async function saveGalleryDetails(workspaceId, workspaceName) {
    await json(`/api/workspaces/${workspaceId}/gallery`, {method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify({title:document.getElementById('galleryTitle').value.trim(),description:document.getElementById('galleryDescription').value.trim()||null})});
    await openWorkspaceGallery(workspaceId, workspaceName);
}

async function uploadGalleryImages(workspaceId, workspaceName) {
    const files = document.getElementById('galleryFiles').files;
    if (!files.length) return alert('Select at least one image.');
    const form = new FormData();
    for (const file of files) form.append('files', file);
    const response = await fetch(`/api/workspaces/${workspaceId}/gallery/items`, {method:'POST',body:form});
    if (!response.ok) throw new Error(await response.text());
    await openWorkspaceGallery(workspaceId, workspaceName);
}

async function editGalleryItem(workspaceId, itemId, currentCaption, currentOrder, currentVisible, workspaceName) {
    const caption = prompt('Caption', currentCaption); if (caption === null) return;
    const orderText = prompt('Sort order', String(currentOrder)); if (orderText === null) return;
    const visibleText = prompt('Visible in published gallery? yes/no', currentVisible ? 'yes' : 'no'); if (visibleText === null) return;
    await json(`/api/workspaces/${workspaceId}/gallery/items/${itemId}`, {method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify({caption:caption.trim()||null,sortOrder:Number(orderText)||0,isVisible:!['no','false','0'].includes(visibleText.trim().toLowerCase())})});
    await openWorkspaceGallery(workspaceId, workspaceName);
}

async function deleteGalleryItem(workspaceId, itemId, workspaceName) {
    if (!confirm('Delete this image from the gallery?')) return;
    const response = await fetch(`/api/workspaces/${workspaceId}/gallery/items/${itemId}`, {method:'DELETE'});
    if (!response.ok) throw new Error(await response.text());
    await openWorkspaceGallery(workspaceId, workspaceName);
}

async function publishWorkspaceGallery(workspaceId, workspaceName) {
    try {
        const result = await json(`/api/workspaces/${workspaceId}/gallery/publish`, {method:'POST'});
        await openWorkspaceGallery(workspaceId, workspaceName);
        if (result.publicUrl) window.open(result.publicUrl, '_blank', 'noopener');
    } catch (error) { alert(error.message || error); }
}

async function unpublishWorkspaceGallery(workspaceId, workspaceName) {
    if (!confirm('Take this client gallery offline?')) return;
    await json(`/api/workspaces/${workspaceId}/gallery/unpublish`, {method:'POST'});
    await openWorkspaceGallery(workspaceId, workspaceName);
}

function previewGalleryItem(workspaceId, itemId) {
    window.open(`/api/workspaces/${workspaceId}/gallery/items/${itemId}/preview`, '_blank', 'noopener');
}

