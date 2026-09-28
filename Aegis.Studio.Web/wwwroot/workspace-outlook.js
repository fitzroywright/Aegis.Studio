// Keep navigation in the workspace view and make the reading list searchable.
(() => {
    const search = document.getElementById('workspaceSearch');
    const filter = () => {
        const term = search.value.trim().toLocaleLowerCase();
        document.querySelectorAll('#conversationList .conversation-card').forEach(card => {
            card.hidden = !card.textContent.toLocaleLowerCase().includes(term);
        });
    };
    search.addEventListener('input', filter);
    const baseLoad = loadConversations;
    loadConversations = async function() {
        await baseLoad();
        document.querySelectorAll('#conversationList .conversation-card').forEach(card => {
            card.tabIndex = 0;
            card.setAttribute('role', 'button');
            card.onkeydown = event => {
                if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); card.click(); }
            };
        });
        filter();
    };
    const baseSelect = selectWorkspace;
    selectWorkspace = async function(id, name) {
        activateView('conversations');
        search.value = '';
        selectedConversation = null;
        detailPane.classList.add('empty');
        detailPane.innerHTML = '<div class="empty-state"><h2>Select a conversation</h2><p>Read the enquiry, follow its history, and open the linked job.</p></div>';
        await baseSelect(id, name);
    };
    document.getElementById('workspaceRefresh').onclick = async function() {
        this.disabled = true;
        try { await loadConversations(); }
        catch (error) { alert(error.message || error); }
        finally { this.disabled = false; }
    };
})();
