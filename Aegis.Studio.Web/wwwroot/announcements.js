// One announcement banner per login. Dismissal is stored by the server for this user.
(() => {
    let started = false;
    async function showAtLogin(user) {
        if (!user || started) return;
        started = true;
        const identity = String(user.userName || user.username || user.email || user.id || '').toLowerCase();
        if (!identity || sessionStorage.getItem('studio-announcements-login') === identity) return;
        const main = document.querySelector('.shell > main');
        if (!main) return;
        try {
            const workspaces = await loadWorkspaces();
            const results = await Promise.allSettled(workspaces.filter(w=>w.permissions?.accessWorkspace && w.permissions?.view).map(async workspace => {
                const items = await json('/api/workspaces/'+encodeURIComponent(workspace.id)+'/announcements');
                return items.map(item=>({...item,workspaceId:workspace.id,workspaceName:workspace.name}));
            }));
            const items = results.flatMap(result=>result.status==='fulfilled'?result.value:[]);
            if (results.every(result=>result.status==='fulfilled')) sessionStorage.setItem('studio-announcements-login', identity);
            if (!items.length) return;
            const host = document.createElement('section');
            host.id = 'studioLoginAnnouncements';host.className = 'workspace-announcement-banners';
            host.setAttribute('aria-label','Announcements — scroll horizontally for more');host.tabIndex = 0;
            for (const item of items) {
                const article=document.createElement('article');article.className='workspace-announcement';
                const copy=document.createElement('div'),title=document.createElement('strong'),body=document.createElement('p'),error=document.createElement('small');
                title.textContent=item.title;body.textContent=item.body;error.className='dismiss-error';error.setAttribute('role','alert');
                const context=document.createElement('small');context.textContent=item.workspaceName;
                copy.append(title,body,context,error);
                const button=document.createElement('button');button.type='button';button.className='action';button.textContent='Dismiss';button.setAttribute('aria-label','Dismiss announcement: '+item.title);
                button.onclick=async()=>{
                    button.disabled=true;error.textContent='';
                    try {
                        await json('/api/workspaces/'+encodeURIComponent(item.workspaceId)+'/announcements/'+encodeURIComponent(item.id)+'/dismiss',{method:'POST'});
                        const next=article.nextElementSibling||article.previousElementSibling;
                        article.remove();
                        if (!host.children.length) host.remove();
                        else next?.querySelector('button')?.focus();
                    } catch {error.textContent='Could not dismiss. Please try again.';button.disabled=false;}
                };
                article.append(copy,button);host.append(article);
            }
            main.prepend(host);
        } catch(error) { console.error('Announcements could not be loaded.', error); }
    }
    async function attachComposer(toolbar,current) {
        if(!toolbar)return;
        try {
            const workspaces=await loadWorkspaces();
            if(!current()||!toolbar.isConnected)return;
            const eligible=workspaces.filter(w=>w.permissions?.accessWorkspace&&w.permissions?.view&&w.permissions?.notifyMembers);
            if(!eligible.length)return;
            const button=document.createElement('button');button.type='button';button.className='action';button.dataset.postAnnouncement='';button.textContent='Post Announcement';
            toolbar.append(button);
            button.onclick=()=>{
                if(!current())return;
                const dialog=studioDialog('Staff Announcement','<p>For Bryan’s staff in the selected workspace. Announcements are not sent to clients.</p><form class="announcement-form"><label>Staff workspace<select name="workspace"></select></label><label>Title<input name="title" required maxlength="200"></label><label>Announcement<textarea name="body" required maxlength="10000"></textarea></label><button class="primary">Post announcement</button><p role="status"></p></form>');
                const form=dialog.querySelector('form');
                for(const workspace of eligible){const option=document.createElement('option');option.value=workspace.id;option.textContent=workspace.name;form.elements.workspace.append(option);}
                form.onsubmit=async event=>{
                    event.preventDefault();if(!current()){dialog.close();return;}const submit=form.querySelector('button');submit.disabled=true;
                    try {
                        await json('/api/workspaces/'+encodeURIComponent(form.elements.workspace.value)+'/announcements',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({title:form.elements.title.value,body:form.elements.body.value})});
                        form.elements.title.value='';form.elements.body.value='';form.querySelector('[role=status]').textContent='Staff announcement posted. It will appear at the next login.';
                    }catch(error){form.querySelector('[role=status]').textContent=error.message||'Could not post announcement.';}
                    finally{submit.disabled=false;}
                };
            };
        }catch(error){console.error('Announcement permissions could not be loaded.',error);}
    }
    window.StudioAnnouncements = {showAtLogin,attachComposer};
    window.studioSignedInUserReady?.then(showAtLogin).catch(error=>console.error('Announcement sign-in check failed.',error));
})();
