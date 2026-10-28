async function renderGlobalRoleAdministration(current=()=>true) {
    try {
        const me = await json('/api/auth/me');
        if (!current() || (me?.user||me)?.isAdministrator !== true) return;
        const roles = await json('/api/security/global-roles/');
        if(!current())return;
        const frontDesk = roles.find(role => role.name === 'Front Desk');
        const operationsManager = roles.find(role => role.name === 'Operations Manager');
        if (!frontDesk && !operationsManager) return;

        const section = document.createElement('section');
        section.className = 'staff-global-role-section';
        section.innerHTML = `<h2>Global operational roles</h2><div class="staff-global-role-columns">${[
            frontDesk&&{role:frontDesk,title:'Front Desk',description:'Cross-workspace reception and client service.',user:'frontDeskRoleUser',display:'frontDeskRoleDisplay',target:'frontDeskRoleMembers',save:'saveFrontDeskRoleMember'},
            operationsManager&&{role:operationsManager,title:'Operations Manager',description:'Cross-workspace escalation and operational oversight.',user:'operationsManagerRoleUser',display:'operationsManagerRoleDisplay',target:'operationsManagerRoleMembers',save:'saveOperationsManagerRoleMember'}
        ].filter(Boolean).map(x=>`<section class="staff-editor"><h3>${x.title}</h3><p class="muted">${x.description}</p><div class="staff-entry"><label>User name<input id="${x.user}" placeholder="domain user name"></label><label>Display name<input id="${x.display}"></label><button type="button" class="primary" onclick="${x.save}('${x.role.id}')">Assign user</button></div><div class="staff-grid-scroll" id="${x.target}"></div></section>`).join('')}</div>`;
        const holder=genericView.querySelector('[data-staff-global-roles]');if(!holder)return;holder.replaceChildren(section);
        if(frontDesk) await refreshFrontDeskRoleMembers(frontDesk.id);
        if(operationsManager) await refreshOperationsManagerRoleMembers(operationsManager.id);
    } catch (error) {
        console.warn('Global role administration unavailable', error);
    }
};

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



