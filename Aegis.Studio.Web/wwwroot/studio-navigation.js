(() => {
    const conversationView=document.getElementById('conversationView');
    const genericView=document.getElementById('genericView');

    function setActive(view){
        document.querySelectorAll('.rail [data-view]').forEach(button=>{
            button.classList.toggle('active',button.dataset.view===view);
        });
    }

    async function renderMyWork(){
        if(typeof window.renderStudioMyWork==='function')
            return window.renderStudioMyWork();

        conversationView.classList.add('hidden');
        genericView.classList.remove('hidden');
        genericView.innerHTML='<div class="studio-page-heading"><h1>My Work</h1></div><div class="studio-card"><p>Loading…</p></div>';

        const [jobs,tasks]=await Promise.all([
            json('/api/jobs'),
            json('/api/operations/activities').catch(()=>[])
        ]);

        genericView.innerHTML='<div class="studio-page-heading"><h1>My Work</h1></div><div class="my-work-grid"><section class="studio-card"><div class="studio-card-heading"><h2>Active Jobs</h2><span>'+jobs.length+'</span></div>'+jobs.slice(0,12).map(job=>'<button class="studio-list-row" data-open-job="'+job.id+'"><strong>'+esc(job.title)+'</strong><span>'+esc(job.clientName||'')+' · '+esc(job.status||'')+'</span></button>').join('')+'</section><section class="studio-card"><div class="studio-card-heading"><h2>Open Tasks</h2><span>'+tasks.filter(t=>!['Completed','Cancelled'].includes(t.status)).length+'</span></div>'+tasks.filter(t=>!['Completed','Cancelled'].includes(t.status)).slice(0,12).map(task=>'<button class="studio-list-row" data-open-job="'+task.jobId+'"><strong>'+esc(task.title)+'</strong><span>'+esc(task.status||'')+'</span></button>').join('')+'</section></div>';
        genericView.querySelectorAll('[data-open-job]').forEach(button=>button.onclick=()=>openWorkspaceJob(button.dataset.openJob));
    }

    async function renderPeople(){
        conversationView.classList.add('hidden');
        genericView.classList.remove('hidden');
        genericView.innerHTML='<div class="studio-page-heading"><h1>People</h1></div><div class="studio-card"><p>Loading…</p></div>';

        const jobs=await json('/api/jobs');
        const rows=[];
        for(const job of jobs.slice(0,30)){
            const media=await json('/api/jobs/'+job.id+'/media').catch(()=>({subjects:[]}));
            for(const subject of (media.subjects||[]).filter(x=>!x.isSystem))
                rows.push({job,subject});
        }

        genericView.innerHTML='<div class="studio-page-heading"><h1>People</h1></div><div class="studio-card studio-table-list">'+rows.map(x=>'<button class="studio-list-row" data-open-job="'+x.job.id+'"><strong>'+esc(x.subject.name)+'</strong><span>'+esc(x.job.title)+' · '+esc(x.subject.imageCount??0)+' photos</span></button>').join('')+'</div>';
        genericView.querySelectorAll('[data-open-job]').forEach(button=>button.onclick=()=>openWorkspaceJob(button.dataset.openJob));
    }

    async function renderTasks(){
        conversationView.classList.add('hidden');
        genericView.classList.remove('hidden');
        const tasks=await json('/api/operations/activities');
        genericView.innerHTML='<div class="studio-page-heading"><h1>Tasks</h1></div><div class="studio-card studio-table-list">'+tasks.map(task=>'<button class="studio-list-row" data-open-job="'+task.jobId+'"><strong>'+esc(task.title)+'</strong><span>'+esc(task.status||'')+(task.assignedTo?' · '+esc(studioFirstName(task.assignedTo)):'')+'</span></button>').join('')+'</div>';
        genericView.querySelectorAll('[data-open-job]').forEach(button=>button.onclick=()=>openWorkspaceJob(button.dataset.openJob));
    }

    async function renderMedia(){
        conversationView.classList.add('hidden');
        genericView.classList.remove('hidden');
        genericView.innerHTML='<div class="studio-page-heading"><h1>Media</h1></div><section class="studio-card" id="globalMediaQueue"><p>Loading…</p></section>';
        const host=document.getElementById('globalMediaQueue');
        if(typeof renderMediaAttentionQueue==='function') await renderMediaAttentionQueue(host);
        else host.innerHTML='<p>Media queue is unavailable.</p>';
    }

    async function renderAdmin(){
        if(typeof showWorkspaces==='function') await showWorkspaces();
        else {
            conversationView.classList.add('hidden');
            genericView.classList.remove('hidden');
        }

        const me=await json('/api/auth/me').catch(()=>null);
        if(me?.user?.isAdministrator!==true) return;

        let panel=document.getElementById('studioDemoLoader');
        if(panel) return;

        panel=document.createElement('section');
        panel.id='studioDemoLoader';
        panel.className='studio-card studio-demo-loader';
        panel.innerHTML='<div class="studio-card-heading"><div><h2>Demo Data</h2><p>Load Carter Wedding with 50 wedding photos, linked People, messages, tasks, appointments and production data.</p></div><button class="primary" type="button" data-load-demo>Load Demo Data</button></div><p data-demo-status></p>';
        genericView.prepend(panel);

        panel.querySelector('[data-load-demo]').onclick=async()=>{
            const button=panel.querySelector('[data-load-demo]');
            const status=panel.querySelector('[data-demo-status]');
            button.disabled=true;
            status.textContent='Loading demo data…';
            try{
                const result=await json('/api/admin/demo/seed',{method:'POST'});
                status.textContent=result.message||'Demo data loaded.';
                await loadWorkspaces();
            }catch(error){
                status.textContent='Demo data could not be loaded: '+(error.message||error);
            }finally{
                button.disabled=false;
            }
        };
    }

    const routes={
        mywork:renderMyWork,
        workspaces:async()=>{
            if(typeof showWorkspaces==='function') return showWorkspaces();
            conversationView.classList.remove('hidden');
            genericView.classList.add('hidden');
        },
        jobs:()=>showJobs(),
        messages:()=>{
            if(typeof window.showStudioMessages==='function') return window.showStudioMessages();
            throw new Error('Messages view is unavailable.');
        },
        media:renderMedia,
        subjects:renderPeople,
        tasks:renderTasks,
        pipeline:()=>showPipeline(),
        production:()=>showProduction(),
        frontdesk:()=>showFrontDesk(),
        reports:()=>showManagement(),
        admin:renderAdmin
    };

    window.studioNavigate=async function(view){
        const route=routes[view];
        if(!route) throw new Error('Unknown Studio navigation view: '+view);
        setActive(view);
        try{
            await route();
            setActive(view);
        }catch(error){
            conversationView.classList.add('hidden');
            genericView.classList.remove('hidden');
            genericView.innerHTML='<div class="studio-page-heading"><h1>Unable to load view</h1></div><div class="studio-card"><p>'+esc(error?.message||error)+'</p></div>';
            throw error;
        }
    };

    document.querySelectorAll('.rail [data-view]').forEach(button=>{
        button.type='button';
        button.onclick=()=>window.studioNavigate(button.dataset.view);
    });

    const topNew=document.getElementById('topNewButton');
    if(topNew) topNew.onclick=()=>document.getElementById('newRequestDialog')?.showModal();

    setActive('mywork');
})();