(() => {
    const permissionLabels = [
        ['accessJob','Access job'],['viewJob','View job'],['viewConversation','View conversation'],['replyRequester','Reply to requester'],
        ['viewAttachments','View attachments'],['addAttachments','Add attachments'],['editJob','Edit job'],['changeStatus','Change status'],
        ['assignStaff','Assign staff'],['manageSchedule','Manage schedule'],['manageProduction','Manage production'],['viewFinancials','View financials'],
        ['recordFinancialActivity','Record financial activity'],['usePos','Use P O S'],['viewPosTransactions','View P O S transactions'],
        ['refundVoidPos','Refund / void P O S'],['notifyJobMembers','Notify job members'],['manageJobMembers','Manage job members'],['adminJob','Job administrator']
    ];

    function preset(name) {
        const none = Object.fromEntries(permissionLabels.map(([key]) => [key,false]));
        if (name === 'viewer') return {...none,accessJob:true,viewJob:true,viewConversation:true,viewAttachments:true};
        if (name === 'contributor') return {...none,accessJob:true,viewJob:true,viewConversation:true,viewAttachments:true,addAttachments:true,manageProduction:true,notifyJobMembers:true};
        if (name === 'staff') return {...none,accessJob:true,viewJob:true,viewConversation:true,viewAttachments:true,addAttachments:true,manageSchedule:true,manageProduction:true,notifyJobMembers:true};
        if (name === 'manager') return {...none,accessJob:true,viewJob:true,viewConversation:true,replyRequester:true,viewAttachments:true,addAttachments:true,editJob:true,changeStatus:true,assignStaff:true,manageSchedule:true,manageProduction:true,viewFinancials:true,recordFinancialActivity:true,usePos:true,viewPosTransactions:true,notifyJobMembers:true,manageJobMembers:true};
        return Object.fromEntries(permissionLabels.map(([key]) => [key,true]));
    }

    const originalOpenJob = window.openJob;
    if (typeof originalOpenJob !== 'function') return;

    window.openJob = async function(id) {
        await originalOpenJob(id);
        try {
            const access = await json(`/api/jobs/${id}/access`);
            const p = access.permissions || {};
            const actions = document.querySelector('.job-actions');
            if (actions) {
                actions.querySelectorAll('button').forEach(button => {
                    const text = button.textContent.trim().toLowerCase();
                    if (text === 'assign staff' && !p.assignStaff) button.remove();
                    else if (['schedule','photography','production','ready for client','complete'].includes(text) && !p.changeStatus) button.remove();
                    else if (text === 'record money' && !p.recordFinancialActivity) button.remove();
                });
            }

            const detail = document.getElementById('genericView');
            if (!detail) return;
            const section = document.createElement('section');
            section.className = 'job-section';
            section.id = 'jobSecurityPanel';
            const canManage = p.manageJobMembers || p.adminJob;
            const canNotify = p.notifyJobMembers || p.adminJob;
            section.innerHTML = `<h2>Job access</h2><p class="muted">Job permissions inherit from the workspace by default. Explicit job membership can add access or replace inherited permissions for this job only.</p><div id="jobAccessBody"><span class="muted">Loading job members…</span></div>`;
            detail.appendChild(section);

            if (!canManage && !canNotify) {
                document.getElementById('jobAccessBody').innerHTML = `<div class="meta">Your effective permissions: ${esc(permissionLabels.filter(([key]) => p[key]).map(([,label]) => label).join(', ') || 'None')}</div>`;
                return;
            }

            let members = [];
            if (canManage) members = await json(`/api/jobs/${id}/members`);
            let roleControls = '';
            if (canManage) {
                try {
                    const me = await json('/api/auth/me');
                    if (me?.user?.isAdministrator === true) {
                        const roles = (await json('/api/security/roles')).filter(x => x.scope === 'Job' && x.isActive);
                        if (roles.length) roleControls = `<h3>Assign reusable role</h3><div class="pos-entry"><label>User name<input id="jobRoleUser"></label><label>Role<select id="jobRoleSelect">${roles.map(x=>`<option value="${x.id}">${esc(x.name)}</option>`).join('')}</select></label><button class="primary" onclick="assignJobRole('${id}')">Assign role</button></div>`;
                    }
                } catch (error) { console.warn('Unable to load reusable job roles', error); }
            }
            document.getElementById('jobAccessBody').innerHTML = `
                ${canManage ? `<div>${members.length ? members.map(member => renderMember(id, member, canNotify)).join('') : '<p class="muted">No explicit job members yet. Workspace permissions still apply.</p>'}</div>
                <h3>Add or update job member</h3>
                <div class="pos-entry"><label>User name<input id="jobMemberUser"></label><label>Display name<input id="jobMemberDisplay"></label><label>Preset<select id="jobMemberPreset"><option value="viewer">Viewer</option><option value="contributor">Contributor</option><option value="staff">Job staff</option><option value="manager">Job manager</option><option value="admin">Job admin</option></select></label><label><input type="checkbox" id="jobMemberInherit" checked> Inherit workspace permissions</label><button class="primary" onclick="saveJobMember('${id}')">Save member</button></div>${roleControls}` : '<p class="muted">You can notify explicit job members, but cannot change membership.</p>'}`;
        } catch (error) {
            console.warn('Unable to load job access', error);
        }
    };

    function renderMember(jobId, member, canNotify) {
        const rights = permissionLabels.filter(([key]) => member.permissions?.[key]).map(([,label]) => label).join(', ');
        return `<div class="job-row"><div><b>${esc(studioFirstName(member))}</b><div class="meta">${member.isActive ? 'Active' : 'Inactive'} · ${member.inheritWorkspacePermissions ? 'Inherits workspace' : 'Job override'}</div><div class="meta">${esc(rights || 'No explicit job rights')}</div></div><div>${canNotify ? `<button class="action" onclick="notifyJobMember('${jobId}','${esc(member.userName)}','${esc(studioFirstName(member))}')">Notify</button>` : ''}<button class="action" onclick="editJobMember('${jobId}','${esc(member.userName)}','${esc(studioFirstName(member))}')">Permissions</button><button class="action" onclick="removeJobMember('${jobId}','${esc(member.userName)}')">Remove</button></div></div>`;
    }

    window.saveJobMember = async function(jobId) {
        const userName = document.getElementById('jobMemberUser').value.trim();
        if (!userName) return alert('User name is required.');
        const displayName = document.getElementById('jobMemberDisplay').value.trim() || userName;
        const permissions = preset(document.getElementById('jobMemberPreset').value);
        const inheritWorkspacePermissions = document.getElementById('jobMemberInherit').checked;
        await put(jobId,userName,displayName,inheritWorkspacePermissions,permissions);
        await window.openJob(jobId);
    };

    window.assignJobRole = async function(jobId) {
        const userName = document.getElementById('jobRoleUser').value.trim(); if (!userName) return alert('User name is required.');
        const roleId = document.getElementById('jobRoleSelect').value;
        await json(`/api/security/jobs/${jobId}/members/${encodeURIComponent(userName)}/role/${roleId}`, {method:'PUT'});
        await window.openJob(jobId);
    };

    window.editJobMember = async function(jobId,userName,displayName) {
        const members = await json(`/api/jobs/${jobId}/members`);
        const member = members.find(x => x.userName.toLowerCase() === userName.toLowerCase());
        if (!member) return;
        const current = permissionLabels.map(([key,label]) => `${label}: ${member.permissions[key] ? 'yes' : 'no'}`).join('\n');
        const choice = prompt(`Current explicit permissions:\n${current}\n\nPreset: viewer, contributor, staff, manager, admin`, member.permissions.adminJob ? 'admin' : member.permissions.manageJobMembers ? 'manager' : member.permissions.manageSchedule ? 'staff' : member.permissions.addAttachments ? 'contributor' : 'viewer');
        if (!choice) return;
        const inheritText = prompt('Inherit workspace permissions? yes/no', member.inheritWorkspacePermissions ? 'yes' : 'no');
        if (inheritText === null) return;
        await put(jobId,userName,displayName,!['no','false','0'].includes(inheritText.trim().toLowerCase()),preset(choice.trim().toLowerCase()));
        await window.openJob(jobId);
    };

    async function put(jobId,userName,displayName,inheritWorkspacePermissions,permissions) {
        await json(`/api/jobs/${jobId}/members/${encodeURIComponent(userName)}`, {method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify({userName,displayName,isActive:true,inheritWorkspacePermissions,permissions})});
    }

    window.removeJobMember = async function(jobId,userName) {
        if (!confirm(`Remove ${userName} from this job?`)) return;
        const response = await fetch(`/api/jobs/${jobId}/members/${encodeURIComponent(userName)}`, {method:'DELETE'});
        if (!response.ok) throw new Error(await response.text());
        await window.openJob(jobId);
    };

    window.notifyJobMember = async function(jobId,userName,displayName) {
        const subject = prompt(`Notification subject for ${displayName}`, 'Job notification'); if (!subject) return;
        const body = prompt('Message'); if (!body) return;
        await json(`/api/jobs/${jobId}/notifications`, {method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({userName,subject,body})});
        alert(`Notification queued for ${displayName}. Their messaging preferences determine delivery channels.`);
    };
})();
