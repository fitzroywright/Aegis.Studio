(() => {
    const resolveView = raw => ({
        conversations:'mywork',
        schedule:'pipeline',
        management:'admin',
        jobs:'jobs',
        mywork:'mywork',
        workspaces:'workspaces',
        messages:'messages',
        media:'media',
        subjects:'subjects',
        tasks:'tasks',
        pipeline:'pipeline',
        production:'production',
        frontdesk:'frontdesk',
        reports:'reports',
        admin:'admin',
        dashboard:'reports'
    })[raw] || raw;

    const fallback = async view => {
        const conversationView=document.getElementById('conversationView');
        const genericView=document.getElementById('genericView');
        const showGeneric=()=>{
            conversationView?.classList.add('hidden');
            genericView?.classList.remove('hidden');
        };

        switch(view){
            case 'mywork':
                if(typeof window.studioNavigate==='function') return window.studioNavigate('mywork');
                showGeneric();
                if(genericView) genericView.innerHTML='<div class="studio-page-heading"><h1>My Work</h1></div><div class="studio-card"><p>Loading…</p></div>';
                return;
            case 'workspaces':
                if(typeof window.studioNavigate==='function') return window.studioNavigate('workspaces');
                if(typeof window.showWorkspaces==='function') return window.showWorkspaces();
                conversationView?.classList.remove('hidden');
                genericView?.classList.add('hidden');
                return;
            case 'messages':
                if(typeof window.showStudioMessages==='function') return window.showStudioMessages();
                if(typeof window.studioNavigate==='function') return window.studioNavigate('messages');
                break;
            case 'pipeline':
                if(typeof window.showPipeline==='function') return window.showPipeline();
                break;
            case 'production':
                if(typeof window.showProduction==='function') return window.showProduction();
                break;
            case 'frontdesk':
                if(typeof window.showFrontDesk==='function') return window.showFrontDesk();
                break;
            case 'reports':
                if(typeof window.showManagement==='function') return window.showManagement();
                break;
            case 'admin':
                if(typeof window.studioNavigate==='function') return window.studioNavigate('admin');
                if(typeof window.showWorkspaces==='function'){
                    await window.showWorkspaces();
                    return;
                }
                break;
            case 'jobs':
            case 'media':
            case 'subjects':
            case 'tasks':
                if(typeof window.studioNavigate==='function') return window.studioNavigate(view);
                break;
        }

        showGeneric();
        if(genericView) genericView.innerHTML='<div class="studio-page-heading"><h1>Unable to load view</h1></div><div class="studio-card"><p>Navigation route unavailable: '+view+'</p></div>';
    };

    const activate = button => {
        const view=resolveView(button.dataset.view||'');
        document.querySelectorAll('.rail [data-view]').forEach(item=>item.classList.toggle('active',item===button));
        fallback(view).catch(error=>{
            const genericView=document.getElementById('genericView');
            document.getElementById('conversationView')?.classList.add('hidden');
            genericView?.classList.remove('hidden');
            if(genericView) genericView.innerHTML='<div class="studio-page-heading"><h1>Unable to load view</h1></div><div class="studio-card"><p>'+String(error?.message||error)+'</p></div>';
        });
    };

    const bind = () => {
        const rail=document.querySelector('.rail');
        if(!rail) return;
        rail.querySelectorAll('[data-view]').forEach(button=>{
            button.type='button';
            button.style.pointerEvents='auto';
            button.disabled=false;
            button.onclick=event=>{
                event.preventDefault();
                event.stopPropagation();
                activate(button);
            };
        });
    };

    bind();

    // Rebind if another Studio script reconstructs the rail later.
    const rail=document.querySelector('.rail');
    if(rail){
        new MutationObserver(bind).observe(rail,{childList:true,subtree:true});
    }

    // Last-resort capture handler. This does not depend on any earlier Studio listener.
    document.addEventListener('pointerdown',event=>{
        const button=event.target.closest?.('.rail [data-view]');
        if(!button) return;
        event.preventDefault();
        event.stopPropagation();
        activate(button);
    },true);
})();
