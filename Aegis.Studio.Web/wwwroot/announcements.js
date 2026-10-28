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
            host.hidden = document.body.dataset.studioView === 'dashboard';
            host.id = 'studioLoginAnnouncements';host.className = 'workspace-announcement-banners';
            host.setAttribute('aria-label','Staff announcements. Click an announcement or use arrow keys to move between announcements.');host.tabIndex = 0;
            const updatePositions=()=>{host.dataset.multiple=String(host.children.length>1);[...host.children].forEach((article,index)=>{article.querySelector('.workspace-announcement-position').textContent=(index+1)+' of '+host.children.length+(host.children.length>1?' · Click for next':'');});};
            const advance=(article,direction=1)=>{
                const slides=[...host.children];if(slides.length<2)return;
                const index=slides.indexOf(article);if(index<0)return;
                const next=slides[(index+direction+slides.length)%slides.length];
                host.scrollTo({left:next.offsetLeft-slides[0].offsetLeft,behavior:window.matchMedia?.('(prefers-reduced-motion: reduce)')?.matches?'auto':'smooth'});
            };
            host.addEventListener('click',event=>{
                if(event.target.closest('button,a,input,textarea,select'))return;
                const article=event.target.closest('.workspace-announcement');if(!article||article.parentElement!==host)return;
                const selection=window.getSelection();if(selection?.toString()&&article.contains(selection.anchorNode))return;
                advance(article);
            });
            host.addEventListener('keydown',event=>{
                if(event.target!==host||!['ArrowRight','ArrowLeft','Enter',' '].includes(event.key)||host.children.length<2)return;
                event.preventDefault();const slides=[...host.children],first=slides[0].offsetLeft;
                const current=slides.reduce((best,slide)=>Math.abs(slide.offsetLeft-first-host.scrollLeft)<Math.abs(best.offsetLeft-first-host.scrollLeft)?slide:best);
                advance(current,event.key==='ArrowLeft'?-1:1);
            });
            for (const item of items) {
                const article=document.createElement('article');article.className='workspace-announcement';
                article.setAttribute('aria-label',item.title);
                const symbol=document.createElement('div');symbol.className='workspace-announcement-symbol';symbol.setAttribute('aria-hidden','true');
                symbol.innerHTML='<svg viewBox="0 0 64 64" fill="none" focusable="false"><path d="m19 36 6 18h9l-7-18" fill="#efbd42"/><path d="M11 26h13l24-13v36L24 37H11z" fill="#fff8df" stroke="#efbd42" stroke-width="3" stroke-linejoin="round"/><path d="M10 28H6v7h4M24 26v11M48 13v36" stroke="#efbd42" stroke-width="3" stroke-linecap="round"/><path d="m54 17 5-4m-4 15h7m-8 12 5 4" stroke="#efbd42" stroke-width="3" stroke-linecap="round"/></svg>';
                const copy=document.createElement('div');copy.className='workspace-announcement-copy';
                const kicker=document.createElement('span');kicker.className='workspace-announcement-kicker';kicker.textContent='Staff announcement';
                const title=document.createElement('h2'),body=document.createElement('p'),error=document.createElement('small');
                title.textContent=item.title;body.textContent=item.body;error.className='dismiss-error';error.setAttribute('role','alert');
                const context=document.createElement('small');context.className='workspace-announcement-context';context.textContent=item.workspaceName;
                copy.append(kicker,title,body,context,error);
                const controls=document.createElement('div');controls.className='workspace-announcement-controls';
                const position=document.createElement('small');position.className='workspace-announcement-position';
                const button=document.createElement('button');button.type='button';button.className='action';button.textContent='Dismiss';button.setAttribute('aria-label','Dismiss announcement: '+item.title);
                button.onclick=async()=>{
                    button.disabled=true;error.textContent='';
                    try {
                        await json('/api/workspaces/'+encodeURIComponent(item.workspaceId)+'/announcements/'+encodeURIComponent(item.id)+'/dismiss',{method:'POST'});
                        const next=article.nextElementSibling||article.previousElementSibling;
                        article.remove();updatePositions();
                        if (!host.children.length) host.remove();
                        else next?.querySelector('button')?.focus();
                    } catch {error.textContent='Could not dismiss. Please try again.';button.disabled=false;}
                };
                controls.append(position,button);article.append(symbol,copy,controls);host.append(article);
            }
            updatePositions();main.prepend(host);
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

