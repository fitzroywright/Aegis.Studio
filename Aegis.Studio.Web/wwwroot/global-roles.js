async function renderGlobalRoleAdministration(current=()=>true) {
    const holder=genericView.querySelector('[data-staff-global-roles]');
    if(!holder||!current())return;
    holder.replaceChildren();
    try {
        const me=await json('/api/auth/me');
        if(!current()||!holder.isConnected||(me?.user||me)?.isAdministrator!==true)return;
        const roles=await json('/api/security/global-roles/');
        if(!current()||!holder.isConnected)return;
        const order=['Administrator','User','Front Desk','Operations Manager','Finance Manager','Production Manager','Media Custodian'];
        const rank=name=>{const index=order.indexOf(name);return index<0?order.length:index;};
        const sorted=roles.slice().sort((a,b)=>rank(a.name)-rank(b.name)||a.name.localeCompare(b.name));
        holder.innerHTML='<section class="staff-global-role-section"><h2>Global operational roles</h2><p>Global roles apply across workspaces. Approve Pending Access accounts separately in the staff directory.</p><div class="staff-global-role-columns"></div></section>';
        const columns=holder.querySelector('.staff-global-role-columns'),loads=[];
        if(!sorted.length)columns.textContent='No global roles are available.';
        for(const [index,role] of sorted.entries()){
            const card=document.createElement('section'),active=role.isActive!==false;
            card.className='staff-editor';card.dataset.globalRoleCard=role.id;
            card.innerHTML=`<h3>${esc(role.name)}${active?'':' (inactive)'}</h3><p class="muted">${esc(role.description||'')}</p><form class="staff-entry" data-role-assignment><label>User name<input name="userName" aria-label="User name for ${esc(role.name)}" placeholder="Enrolled account user name" required ${active?'':'disabled'}></label><label>Display name<input name="displayName" aria-label="Display name for ${esc(role.name)}" ${active?'':'disabled'}></label><button type="submit" class="primary" ${active?'':'disabled'}>Assign user</button><span data-assignment-status role="status"></span></form><div class="staff-grid-scroll" id="globalRoleMembers-${index}"></div>`;
            columns.append(card);
            const form=card.querySelector('form'),target=card.querySelector('.staff-grid-scroll');
            const options={current,active};
            const remove=(id,user,row)=>removeGlobalRoleMember(id,user,row,role.name,reload,current,active);
            const reload=()=>refreshGlobalRoleMembers(role.id,target.id,remove,options);
            form.onsubmit=async event=>{
                event.preventDefault();
                if(!active||!current()||!form.isConnected||form.dataset.saving)return;
                const user=form.elements.namedItem('userName'),display=form.elements.namedItem('displayName'),status=form.querySelector('[data-assignment-status]');
                const userName=user.value.trim();
                if(!userName){status.textContent='User name is required.';user.focus();return;}
                const displayName=display.value.trim()||userName;
                form.dataset.saving='true';form.querySelectorAll('input,button').forEach(el=>el.disabled=true);status.textContent='Assigning…';
                try{
                    await json(globalRoleMemberUrl(role.id,userName),{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify({userName,displayName,isActive:true})});
                    if(!current()||!form.isConnected)return;
                    user.value='';display.value='';status.textContent='Assigned';
                    if(!await reload())status.textContent='Assigned. Retry loading the membership list below.';
                }catch(error){if(current()&&form.isConnected)status.textContent=error.message||'Could not assign. Your entries are retained.';}
                finally{delete form.dataset.saving;form.querySelectorAll('input,button').forEach(el=>el.disabled=false);}
            };
            loads.push(reload());
        }
        await Promise.all(loads);
    }catch(error){
        if(!current()||!holder.isConnected)return;
        holder.innerHTML='<p role="alert"></p><button type="button">Retry global roles</button>';
        holder.querySelector('[role="alert"]').textContent=error.message||'Global roles could not be loaded.';
        holder.querySelector('button').onclick=()=>renderGlobalRoleAdministration(current);
    }
}

function globalRoleMemberUrl(roleId,userName){
    return `/api/security/global-roles/${encodeURIComponent(roleId)}/members/${encodeURIComponent(userName)}`;
}

async function refreshGlobalRoleMembers(roleId,targetId,remove,options={}){
    const target=document.getElementById(targetId),current=options.current||(()=>true);
    if(!target||!current())return false;
    const generation=(target.studioRoleLoadGeneration||0)+1;target.studioRoleLoadGeneration=generation;
    try{
        const members=await json(`/api/security/global-roles/${encodeURIComponent(roleId)}/members`);
        if(!current()||!target.isConnected||target.studioRoleLoadGeneration!==generation)return false;
        target.innerHTML=`<table class="staff-editable-grid"><thead><tr><th>User name</th><th>Display name</th><th>Active</th><th>Actions</th></tr></thead><tbody>${members.map((m,i)=>`<tr data-global-member="${i}"><td>${esc(m.userName)}</td><td><input data-member-name aria-label="Display name for ${esc(m.userName)}" value="${esc(m.displayName||m.userName)}" required></td><td><input data-member-active type="checkbox" aria-label="Active: ${esc(m.userName)}" ${m.isActive?'checked':''}></td><td><div class="staff-row-actions"><button type="button" class="action" data-member-save>Save</button><button type="button" class="action" data-member-remove>Remove</button></div><span data-row-status role="status"></span></td></tr>`).join('')||'<tr><td colspan="4">No users assigned.</td></tr>'}</tbody></table>`;
        target.querySelectorAll('[data-global-member]').forEach(row=>{
            const member=members[Number(row.dataset.globalMember)];
            const restore=()=>{row.querySelectorAll('input,button').forEach(el=>el.disabled=false);if(options.active===false)row.querySelectorAll('input,[data-member-save]').forEach(el=>el.disabled=true);};
            restore();
            row.querySelector('[data-member-remove]').onclick=()=>remove(roleId,member.userName,row);
            row.querySelector('[data-member-save]').onclick=async()=>{
                if(options.active===false||!current()||!row.isConnected||row.dataset.saving)return;
                const input=row.querySelector('[data-member-name]'),status=row.querySelector('[data-row-status]');
                if(!input.reportValidity()||!input.value.trim()){status.textContent='Display name is required.';return;}
                const payload={userName:member.userName,displayName:input.value.trim(),isActive:row.querySelector('[data-member-active]').checked};
                row.dataset.saving='true';row.querySelectorAll('input,button').forEach(el=>el.disabled=true);status.textContent='Saving…';
                try{await json(globalRoleMemberUrl(roleId,member.userName),{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify(payload)});Object.assign(member,payload);if(current()&&row.isConnected)status.textContent='Saved';}
                catch(error){if(current()&&row.isConnected)status.textContent=error.message||'Could not save. Your edits are retained.';}
                finally{delete row.dataset.saving;restore();}
            };
        });
        return true;
    }catch(error){
        if(!current()||!target.isConnected||target.studioRoleLoadGeneration!==generation)return false;
        target.innerHTML='<p role="alert"></p><button type="button">Retry members</button>';
        target.querySelector('[role="alert"]').textContent=error.message||'Role members could not be loaded.';
        target.querySelector('button').onclick=()=>refreshGlobalRoleMembers(roleId,targetId,remove,options);
        return false;
    }
}

async function removeGlobalRoleMember(roleId,userName,row,roleName,reload,current,active=true){
    if(!current()||!row.isConnected||row.dataset.saving||!confirm(`Remove ${userName} from ${roleName}?`))return;
    const status=row.querySelector('[data-row-status]');row.dataset.saving='true';row.querySelectorAll('input,button').forEach(el=>el.disabled=true);status.textContent='Removing…';
    try{await json(globalRoleMemberUrl(roleId,userName),{method:'DELETE'});if(current()&&row.isConnected)await reload();}
    catch(error){if(current()&&row.isConnected)status.textContent=error.message||'Could not remove this membership. Retry when available.';}
    finally{delete row.dataset.saving;row.querySelectorAll('input,button').forEach(el=>el.disabled=false);if(!active)row.querySelectorAll('input,[data-member-save]').forEach(el=>el.disabled=true);}
}




