async function renderGlobalRoleAdministration(current=()=>true) {
    try {
        const me=await json('/api/auth/me');
        if(!current()||(me?.user||me)?.isAdministrator!==true)return;
        const [roles,staff]=await Promise.all([json('/api/security/global-roles/'),json('/api/admin/studio-employees/')]);
        if(!current())return;
        const holder=genericView.querySelector('[data-staff-global-roles]');if(!holder)return;
        const supported=roles.filter(role=>['Front Desk','Operations Manager'].includes(role.name));
        holder.innerHTML=`<div class="staff-global-role-section"><p class="muted">Select an existing Studio staff member to assign an operational role. This does not grant workspace security permissions.</p><div class="staff-global-role-columns">${supported.map(role=>`<section class="staff-editor"><h3>${esc(role.name)}</h3><p class="muted">${esc(role.description||'Cross-workspace operational responsibility')}</p><form data-global-role-form="${esc(role.id)}"><label>Staff member<select name="staff" required><option value="">Choose staff member</option>${staff.filter(e=>e.isActive).map(e=>`<option value="${esc(e.userName)}">${esc(e.displayName||e.userName)} (${esc(e.userName)})</option>`).join('')}</select></label><button type="submit" class="primary">Assign role</button><p role="alert"></p></form><div class="staff-grid-scroll" id="${role.name==='Front Desk'?'frontDeskRoleMembers':'operationsManagerRoleMembers'}"></div></section>`).join('')}</div></div>`;
        holder.querySelectorAll('[data-global-role-form]').forEach(form=>{
            form.onsubmit=async event=>{
                event.preventDefault();const userName=form.elements.staff.value;
                const employee=staff.find(e=>e.userName===userName);if(!employee)return;
                const button=form.querySelector('[type=submit]');button.disabled=true;
                try{
                    const roleId=form.dataset.globalRoleForm;
                    await json(`/api/security/global-roles/${encodeURIComponent(roleId)}/members/${encodeURIComponent(userName)}`,{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify({userName,displayName:employee.displayName||userName,isActive:true})});
                    await refreshGlobalRoleMembers(roleId,roles.find(r=>String(r.id)===roleId)?.name==='Front Desk'?'frontDeskRoleMembers':'operationsManagerRoleMembers',async(id,name)=>{
                        if(!confirm(`Remove ${name} from this role?`))return;
                        const response=await fetch(`/api/security/global-roles/${encodeURIComponent(id)}/members/${encodeURIComponent(name)}`,{method:'DELETE'});
                        if(!response.ok)throw Error(await response.text());
                        await renderGlobalRoleAdministration(current);
                    });
                    form.querySelector('[role=alert]').textContent='Role assigned.';
                }catch(error){form.querySelector('[role=alert]').textContent=error.message||String(error);}
                finally{button.disabled=false;}
            };
        });
        for(const role of supported){
            const target=role.name==='Front Desk'?'frontDeskRoleMembers':'operationsManagerRoleMembers';
            await refreshGlobalRoleMembers(role.id,target,async(id,userName)=>{
                if(!confirm(`Remove ${userName} from ${role.name}?`))return;
                const response=await fetch(`/api/security/global-roles/${encodeURIComponent(id)}/members/${encodeURIComponent(userName)}`,{method:'DELETE'});
                if(!response.ok)throw Error(await response.text());
                await renderGlobalRoleAdministration(current);
            });
        }
    }catch(error){console.warn('Global role administration unavailable',error);}
}

async function refreshGlobalRoleMembers(roleId,targetId,remove){
    const members=await json(`/api/security/global-roles/${roleId}/members`),target=document.getElementById(targetId);if(!target)return;
    target.innerHTML=`<table class="staff-editable-grid"><thead><tr><th>User name</th><th>Display name</th><th>Active</th><th>Actions</th></tr></thead><tbody>${members.map((m,i)=>`<tr data-global-member="${i}"><td>${esc(m.userName)}</td><td><input data-member-name aria-label="Display name for ${esc(m.userName)}" value="${esc(m.displayName||m.userName)}" required></td><td><input data-member-active type="checkbox" aria-label="Active: ${esc(m.userName)}" ${m.isActive?'checked':''}></td><td><div class="staff-row-actions"><button type="button" class="action" data-member-save>Save</button><button type="button" class="action" data-member-remove>Remove</button></div><span data-row-status role="status"></span></td></tr>`).join('')||'<tr><td colspan="4">No users assigned.</td></tr>'}</tbody></table>`;
    target.querySelectorAll('[data-global-member]').forEach(row=>{
        const member=members[Number(row.dataset.globalMember)];row.querySelector('[data-member-remove]').onclick=()=>remove(roleId,member.userName);
        row.querySelector('[data-member-save]').onclick=async()=>{
            const input=row.querySelector('[data-member-name]'),status=row.querySelector('[data-row-status]');if(!input.reportValidity()||!input.value.trim()){status.textContent='Display name is required.';return;}
            const payload={userName:member.userName,displayName:input.value.trim(),isActive:row.querySelector('[data-member-active]').checked};row.querySelectorAll('input,button').forEach(el=>el.disabled=true);status.textContent='Saving…';
            try{await json(`/api/security/global-roles/${roleId}/members/${encodeURIComponent(member.userName)}`,{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify(payload)});Object.assign(member,payload);status.textContent='Saved';}catch(error){status.textContent=error.message||'Could not save. Your edits are retained.';}finally{row.querySelectorAll('input,button').forEach(el=>el.disabled=false);}
        };
    });
}
async function refreshFrontDeskRoleMembers(roleId){return refreshGlobalRoleMembers(roleId,'frontDeskRoleMembers',removeFrontDeskRoleMember);}

async function saveFrontDeskRoleMember(roleId) {
    const userName = document.getElementById('frontDeskRoleUser').value.trim();
    if (!userName) return alert('User name is required.');
    const displayName = document.getElementById('frontDeskRoleDisplay').value.trim() || userName;
    await json(`/api/security/global-roles/${roleId}/members/${encodeURIComponent(userName)}`, {method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify({userName,displayName,isActive:true})});
    await refreshFrontDeskRoleMembers(roleId);
}

async function removeFrontDeskRoleMember(roleId, userName) {
    if (!confirm(`Remove ${userName} from Front Desk?`)) return;
    const response = await fetch(`/api/security/global-roles/${roleId}/members/${encodeURIComponent(userName)}`, {method:'DELETE'});
    if (!response.ok) throw new Error(await response.text());
    await refreshFrontDeskRoleMembers(roleId);
}


async function refreshOperationsManagerRoleMembers(roleId){return refreshGlobalRoleMembers(roleId,'operationsManagerRoleMembers',removeOperationsManagerRoleMember);}

async function saveOperationsManagerRoleMember(roleId) {
    const userName = document.getElementById('operationsManagerRoleUser').value.trim();
    if (!userName) return alert('User name is required.');
    const displayName = document.getElementById('operationsManagerRoleDisplay').value.trim() || userName;
    await json(`/api/security/global-roles/${roleId}/members/${encodeURIComponent(userName)}`, {method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify({userName,displayName,isActive:true})});
    await refreshOperationsManagerRoleMembers(roleId);
}

async function removeOperationsManagerRoleMember(roleId, userName) {
    if (!confirm(`Remove ${userName} as Operations Manager?`)) return;
    const response = await fetch(`/api/security/global-roles/${roleId}/members/${encodeURIComponent(userName)}`, {method:'DELETE'});
    if (!response.ok) throw new Error(await response.text());
    await refreshOperationsManagerRoleMembers(roleId);
}



