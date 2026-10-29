function workspaceAdminPanel(id){let panel=document.getElementById(id);if(!panel){const dialog=studioDialog(id==='workspaceImportPanel'?'Import old email':'Managers, members & permissions','<section class="job-section"></section>');panel=dialog.querySelector('section');panel.id=id;}return panel;}
let workspaceAccessCache = [];
let studioRolesCache = [];

async function showWorkspaces(){return window.studioNavigate('workspace-management');}

async function createWorkspace() {
    const name = document.getElementById('workspaceName').value.trim();
    const managerUser=document.getElementById('workspaceManagerUser').value.trim();
    const managerDisplay=document.getElementById('workspaceManagerDisplay').value.trim()||managerUser;
    if (!name) return alert('Workspace name is required.');
    if (!managerUser) return alert('A Workspace Manager user name is required.');
    const workspace = await json('/api/admin/workspaces/', { method:'POST', headers:{'Content-Type':'application/json'}, body:JSON.stringify({ name, emailAddress:document.getElementById('workspaceEmail').value.trim()||null, routeKey:document.getElementById('workspaceRouteKey')?.value.trim()||null, isEnabled:true, sortOrder:0, defaultJobLead:document.getElementById('workspaceDefaultLead')?.value||managerUser }) });
    try{
        await json('/api/workspaces/'+workspace.id+'/managers/'+encodeURIComponent(managerUser),{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify({userName:managerUser,displayName:managerDisplay,isActive:true,permissions:null})});
    }catch(error){
        alert('Workspace was created, but its manager could not be assigned: '+error.message+' Assign the manager before using this workspace.');
    }
    await loadWorkspaces(); await showWorkspaces(); await manageWorkspaceMembers(workspace.id, workspace.name);
}

async function hydrateWorkspaceManagers(items){
    await Promise.all(items.map(async item=>{
        const target=[...genericView.querySelectorAll('[data-workspace-managers]')].find(node=>node.dataset.workspaceManagers===String(item.workspace.id));if(!target)return;
        const pencil=target.parentElement.querySelector('[data-workspace-pencil]');if(pencil)pencil.disabled=true;
        try{
            const managers=await json('/api/workspaces/'+encodeURIComponent(item.workspace.id)+'/managers');
            item.managers=managers;
            target.textContent=managers.length?managers.map(m=>m.displayName||m.userName).join(', '):'Manager required';target.classList.toggle('workspace-value-warning',!managers.length);
            target.dataset.empty=String(!managers.length);target.classList.toggle('risk',!managers.length);
            if(pencil)pencil.disabled=false;
        }catch{target.textContent='Manager status unavailable';}
    }));
}

// Each pencil edits the existing value node; saving changes only that field.
function bindWorkspaceCardEditing(card,item){
    if(!card)return;
    const workspace=item.workspace,id=encodeURIComponent(workspace.id),status=card.querySelector('[role=status]');
    let editing=null,saving=false;
    const display=(key)=>key==='managers'?(item.managers||[]).map(m=>m.displayName||m.userName).join(', '):key==='isEnabled'?(workspace.isEnabled?'Yes':'No'):key==='icon'?(workspace.iconName||'folder'):key==='color'?(workspace.accentColor||'#b99750'):workspace[key]||'';
    const raw=(key)=>key==='managers'?(item.managers||[]).map(m=>m.userName).join(', '):display(key);
    const paint=(key,node)=>{
        const value=display(key),missing=!value||key==='managers'&&!(item.managers||[]).length;
        node.replaceChildren();node.dataset.empty=String(missing);
        node.classList.remove('workspace-value-danger','workspace-value-warning','workspace-value-caution','workspace-value-success');
        if(key==='color'){
            const swatch=document.createElement('span');swatch.className='workspace-accent-swatch';swatch.style.backgroundColor=/^#[0-9a-f]{6}$/i.test(value)?value:'#b99750';swatch.title=value;swatch.setAttribute('aria-label','Accent color '+value);swatch.setAttribute('role','img');node.append(swatch);return;
        }
        if(key==='icon'){
            const icons={folder:'📁',camera:'📷',portrait:'🖼️',wedding:'💐',id:'🪪'};
            const symbol=document.createElement('span');symbol.className='workspace-icon-symbol';symbol.textContent=icons[value]||'📁';symbol.title=value;symbol.setAttribute('role','img');symbol.setAttribute('aria-label',value+' icon');node.append(symbol);return;
        }
        if(key==='managers'&&missing){node.textContent='Manager required';node.classList.add('workspace-value-warning');}
        else if(key==='emailAddress'){node.textContent=value||'Routing email required';node.classList.add(value?'workspace-value-success':'workspace-value-danger');}
        else if(key==='defaultJobLead'){node.textContent=value||'Job lead not set';if(!value)node.classList.add('workspace-value-caution');}
        else if(key==='isEnabled'){node.textContent=value;node.classList.add(workspace.isEnabled?'workspace-value-success':'workspace-value-danger');}
        else node.textContent=value||'Not set';
    };
    function finish(){
        if(!editing)return;const {key,node,button,cancel}=editing;editing.control?.remove();paint(key,node);node.removeAttribute('contenteditable');node.removeAttribute('role');node.removeAttribute('aria-label');node.removeAttribute('aria-invalid');node.onkeydown=node.onpaste=null;
        button.textContent='✎';button.setAttribute('aria-label','Edit '+button.dataset.label);button.title='Edit '+button.dataset.label;cancel.remove();editing=null;button.focus();
    }
    async function save(){
        if(!editing||saving)return;const {key,node}=editing,value=editing.control?editing.control.value:node.textContent.trim();
        if(value===raw(key)){finish();status.textContent='';return;}
        const error=message=>{status.textContent=message;node.setAttribute('aria-invalid','true');node.focus();};
        if(key==='name'&&(!value||value.length>200))return error('Enter a workspace name of 1 to 200 characters.');
        if(key==='emailAddress'&&value&&!/^[^\s@]+@[^\s@]+$/.test(value))return error('Enter a valid routing email address.');
        if(key==='isEnabled'&&!/^(yes|no)$/i.test(value))return error('Use Yes or No for Enabled.');
        if(key==='icon'&&!['folder','camera','portrait','wedding','id'].includes(value))return error('Use folder, camera, portrait, wedding or id.');
        if(key==='color'&&!/^#[0-9a-f]{6}$/i.test(value))return error('Use a six-digit hex color, such as #b99750.');
        if(key==='managers'&&!value)return error('At least one Workspace Manager is required.');
        saving=true;node.setAttribute('contenteditable','false');card.querySelectorAll('[data-workspace-pencil],[data-workspace-cancel]').forEach(button=>button.disabled=true);status.textContent='Saving…';
        try{
            if(key==='icon'||key==='color'){
                const appearance=await json('/api/workspaces/'+id+'/appearance');
                const payload={icon:appearance.icon,color:appearance.color,[key]:value};
                await json('/api/workspaces/'+id+'/appearance',{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify(payload)});
                workspace.iconName=payload.icon;workspace.accentColor=payload.color;
            }else if(key==='managers'){
                const employees=await json('/api/studio-employees?assignable=true'),previous=await json('/api/workspaces/'+id+'/managers');
                const users=[...new Set(value.split(',').map(name=>name.trim()).filter(Boolean).map(name=>name.toLowerCase()))];
                if(!users.length)throw Error('At least one Workspace Manager is required.');
                const people=users.map(user=>employees.find(person=>person.userName.toLowerCase()===user)||previous.find(person=>person.userName.toLowerCase()===user));
                if(people.some(person=>!person))throw Error('Enter existing staff user names separated by commas.');
                // Add replacements before reducing prior manager permissions; retain their membership.
                for(const person of people)if(!previous.some(prior=>prior.userName.toLowerCase()===person.userName.toLowerCase()))await json('/api/workspaces/'+id+'/managers/'+encodeURIComponent(person.userName),{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify({userName:person.userName,displayName:person.displayName||person.userName,isActive:true,permissions:null})});
                for(const person of previous)if(!users.includes(person.userName.toLowerCase()))await json('/api/workspaces/'+id+'/members/'+encodeURIComponent(person.userName),{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify({userName:person.userName,displayName:person.displayName,isActive:true,permissions:{...person.permissions,manageMembers:false}})});
                item.managers=people;
            }else{
                const latest=await json('/api/workspaces/'+id);
                if(key==='defaultJobLead'&&value&&value!==latest.defaultJobLead){const employees=await json('/api/studio-employees?assignable=true');if(!employees.some(person=>person.userName.toLowerCase()===value.toLowerCase()))throw Error('Enter an assignable staff user name for Default Job Lead.');}
                const payload={name:latest.name,emailAddress:latest.emailAddress,routeKey:latest.routeKey,isEnabled:latest.isEnabled,sortOrder:latest.sortOrder,defaultJobLead:latest.defaultJobLead,[key]:key==='isEnabled'?value.toLowerCase()==='yes':value||null};
                const saved=await json('/api/workspaces/'+id,{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify(payload)});Object.assign(workspace,payload,saved||{});
            }
            if(card.isConnected){finish();status.textContent='Saved';}
            // Refresh shared navigation without replacing any card or another active edit.
            void loadWorkspaces().then(()=>renderWorkspaceMenu()).catch(()=>{});
        }catch(cause){if(card.isConnected){if(!editing?.control)node.setAttribute('contenteditable','true');error(cause.message||'Could not save. Your edit is retained.');}}
        finally{saving=false;card.querySelectorAll('[data-workspace-pencil],[data-workspace-cancel]').forEach(button=>button.disabled=button.dataset.workspacePencil==='managers'&&!item.managers);}
    }
    card.querySelectorAll('[data-workspace-value]').forEach(node=>paint(node.dataset.workspaceValue,node));
    card.querySelectorAll('[data-workspace-pencil]').forEach(button=>{
        button.dataset.label=button.title.replace(/^Edit /,'');
        button.onclick=()=>{
            if(saving)return;if(editing?.button===button){void save();return;}if(editing){status.textContent='Save or cancel the current edit first.';editing.node.focus();return;}
            const key=button.dataset.workspacePencil,node=card.querySelector('[data-workspace-value="'+key+'"]');
            const cancel=document.createElement('button');cancel.type='button';cancel.className='workspace-field-pencil';cancel.dataset.workspaceCancel='';cancel.textContent='×';cancel.title='Cancel edit';cancel.setAttribute('aria-label','Cancel '+button.dataset.label+' edit');cancel.onclick=()=>{if(!saving){finish();status.textContent='';}};button.after(cancel);
            editing={key,node,button,cancel};
            if(key==='color'||key==='icon'){
                const control=document.createElement(key==='color'?'input':'select');
                control.className='workspace-appearance-picker';
                control.setAttribute('aria-label',button.dataset.label);
                if(key==='color'){
                    control.type='color';control.value=/^#[0-9a-f]{6}$/i.test(raw(key))?raw(key):'#b99750';
                }else{
                    const icons=[['folder','▣ Folder'],['camera','📷 Camera'],['portrait','▤ Portrait'],['wedding','♡ Wedding'],['id','▦ ID']];
                    for(const [value,label] of icons){const option=document.createElement('option');option.value=value;option.textContent=label;control.append(option);}
                    control.value=raw(key);
                }
                editing.control=control;node.textContent='';node.append(control);
                button.textContent='✓';button.title='Save '+button.dataset.label;button.setAttribute('aria-label',button.title);
                status.textContent='Choose an option, then select ✓ to save.';
                control.onkeydown=event=>{if(event.key==='Escape'){event.preventDefault();finish();status.textContent='';}else if(event.key==='Enter'){event.preventDefault();void save();}};
                control.focus();return;
            }
            node.textContent=raw(key);node.setAttribute('contenteditable','true');node.setAttribute('role','textbox');node.setAttribute('aria-label',button.dataset.label);button.textContent='✓';button.title='Save '+button.dataset.label;button.setAttribute('aria-label',button.title);status.textContent=key==='managers'?'Enter staff user names, separated by commas. Enter to save; Escape to cancel.':key==='defaultJobLead'?'Enter a staff user name. Enter to save; Escape to cancel.':'Enter to save; Escape to cancel.';
            node.onkeydown=event=>{if(saving){event.preventDefault();return;}if(event.key==='Enter'){event.preventDefault();void save();}if(event.key==='Escape'){event.preventDefault();finish();status.textContent='';}};
            node.onpaste=event=>{event.preventDefault();const text=event.clipboardData?.getData('text/plain')||'';const selection=window.getSelection();if(selection.rangeCount&&node.contains(selection.anchorNode)&&node.contains(selection.focusNode)){const range=selection.getRangeAt(0);range.deleteContents();const inserted=document.createTextNode(text.replace(/[\r\n]+/g,' '));range.insertNode(inserted);range.setStartAfter(inserted);range.collapse(true);selection.removeAllRanges();selection.addRange(range);}};
            node.focus();const selection=window.getSelection(),range=document.createRange();range.selectNodeContents(node);selection.removeAllRanges();selection.addRange(range);
        };
    });
}

async function editWorkspace(id){
    let card=[...genericView.querySelectorAll('[data-workspace-card]')].find(node=>node.dataset.workspaceCard===String(id));
    if(!card){await showWorkspaces();card=[...genericView.querySelectorAll('[data-workspace-card]')].find(node=>node.dataset.workspaceCard===String(id));}
    card?.querySelector('[data-workspace-pencil="name"]')?.click();
}

const permissionLabels = [
    ['accessWorkspace','Access workspace'],['view','View work'],['replyRequester','Reply to requester'],['viewAttachments','View attachments'],['addAttachments','Add attachments'],['createJob','Create jobs'],['editJob','Edit jobs'],['manageSchedule','Manage schedule'],['manageProduction','Manage production'],['notifyMembers','Notify members'],['editWorkspace','Edit workspace'],['manageMembers','Manage members'],['sendAsSelf','Reply as Myself'],['adminWorkspace','Workspace administrator']
];

function permissionsForPreset(preset) {
    const none = Object.fromEntries(permissionLabels.map(([key]) => [key,false]));
    if (preset === 'view') return {...none,accessWorkspace:true,view:true,viewAttachments:true};
    if (preset === 'reply') return {...none,accessWorkspace:true,view:true,replyRequester:true,viewAttachments:true,addAttachments:true,createJob:true};
    if (preset === 'editor') return {...none,accessWorkspace:true,view:true,replyRequester:true,viewAttachments:true,addAttachments:true,createJob:true,editJob:true,manageSchedule:true,manageProduction:true,notifyMembers:true,sendAsSelf:true};
    if (preset === 'manager') return {...none,accessWorkspace:true,view:true,replyRequester:true,viewAttachments:true,addAttachments:true,createJob:true,editJob:true,manageSchedule:true,manageProduction:true,notifyMembers:true,editWorkspace:true,manageMembers:true,sendAsSelf:true};
    return Object.fromEntries(permissionLabels.map(([key]) => [key,true]));
}

async function manageWorkspaceMembers(workspaceId, workspaceName) {
    const panel = workspaceAdminPanel('workspaceMemberPanel');
    const members = await json(`/api/workspaces/${workspaceId}/members`);
    const roleOptions = studioRolesCache.filter(x => x.scope === 'Workspace' && x.isActive).map(x => `<option value="${x.id}">${esc(x.name)}</option>`).join('');
    panel.classList.remove('hidden');
    panel.innerHTML = `<h2>${esc(workspaceName)} — Members & permissions</h2><p class="muted">Assign a reusable role or a one-off permission preset.</p><div>${members.length ? members.map(member => renderMember(workspaceId, member)).join('') : '<p class="muted">No members yet.</p>'}</div><h3>Add or update member</h3><div class="pos-entry"><label>User name<input id="memberUserName" placeholder="domain user name"></label><label>Display name<input id="memberDisplayName"></label><label>Preset<select id="memberPreset"><option value="view">View only</option><option value="reply">Request handler</option><option value="editor">Workspace editor</option><option value="manager">Workspace Manager</option><option value="admin">Workspace admin</option></select></label><button class="primary" onclick="saveWorkspaceMember('${workspaceId}','${esc(workspaceName)}')">Save member</button></div>${roleOptions ? `<h3>Assign reusable role</h3><div class="pos-entry"><label>User name<input id="roleMemberUser"></label><label>Role<select id="workspaceRoleSelect">${roleOptions}</select></label><button class="primary" onclick="assignWorkspaceRole('${workspaceId}','${esc(workspaceName)}')">Assign role</button></div>` : ''}`;

}

function renderMember(workspaceId, member) {
    const permissions = permissionLabels.filter(([key]) => member.permissions[key]).map(([,label]) => label).join(', ');
    return `<div class="job-row"><div><b>${esc(studioFirstName(member))}</b><div class="meta">${member.isActive ? 'Active' : 'Inactive'}</div><div class="meta">${esc(permissions || 'No permissions')}</div></div><div><button class="action" onclick="notifyWorkspaceMember('${workspaceId}','${esc(member.userName)}','${esc(studioFirstName(member))}')">Notify</button><button class="action" onclick="editWorkspaceMember('${workspaceId}','${esc(member.userName)}','${esc(studioFirstName(member))}')">Permissions</button><button class="action" onclick="removeWorkspaceMember('${workspaceId}','${esc(member.userName)}')">Remove</button></div></div>`;
}

async function saveWorkspaceMember(workspaceId, workspaceName) {
    const userName = document.getElementById('memberUserName').value.trim(); if (!userName) return alert('User name is required.');
    const displayName = document.getElementById('memberDisplayName').value.trim() || userName;
    await putWorkspaceMember(workspaceId,userName,displayName,permissionsForPreset(document.getElementById('memberPreset').value));
    await manageWorkspaceMembers(workspaceId,workspaceName);
}

async function assignWorkspaceRole(workspaceId, workspaceName) {
    const userName = document.getElementById('roleMemberUser').value.trim(); if (!userName) return alert('User name is required.');
    const roleId = document.getElementById('workspaceRoleSelect').value;
    await json(`/api/security/workspaces/${workspaceId}/members/${encodeURIComponent(userName)}/role/${roleId}`, {method:'PUT'});
    await manageWorkspaceMembers(workspaceId,workspaceName);
}

async function editWorkspaceMember(workspaceId, userName, displayName) {
    const members=await json('/api/workspaces/'+encodeURIComponent(workspaceId)+'/members');
    const member=members.find(person=>person.userName.toLowerCase()===userName.toLowerCase());if(!member)return;
    const escape=StudioPage.escape;
    const dialog=studioDialog('Permissions — '+displayName,`<form class="subject-form"><fieldset><legend>Workspace permissions</legend>${permissionLabels.map(([key,label])=>`<label><input type="checkbox" name="${key}" ${member.permissions[key]?'checked':''}>${escape(label)}</label>`).join('')}</fieldset><p role="alert"></p><div class="dialog-actions"><button type="button" class="action" data-cancel>Cancel</button><button type="submit" class="primary">Save permissions</button></div></form>`);
    const form=dialog.querySelector('form');let saving=false;form.querySelector('[data-cancel]').onclick=()=>dialog.close();
    form.onsubmit=async event=>{
        event.preventDefault();if(saving)return;saving=true;form.querySelectorAll('button').forEach(button=>button.disabled=true);
        const permissions=Object.fromEntries(permissionLabels.map(([key])=>[key,form.elements.namedItem(key).checked]));
        try{await putWorkspaceMember(workspaceId,member.userName,member.displayName,permissions);dialog.close();const item=workspaceAccessCache.find(item=>String(item.workspace.id)===String(workspaceId));await manageWorkspaceMembers(workspaceId,item?.workspace.name||'Workspace');}
        catch(error){if(form.isConnected)form.querySelector('[role=alert]').textContent=error.message||'Could not save permissions.';}
        finally{saving=false;form.querySelectorAll('button').forEach(button=>button.disabled=false);}
    };
}

async function putWorkspaceMember(workspaceId,userName,displayName,permissions) {
    await json(`/api/workspaces/${workspaceId}/members/${encodeURIComponent(userName)}`, { method:'PUT', headers:{'Content-Type':'application/json'}, body:JSON.stringify({ userName,displayName,isActive:true,permissions }) });
}

async function removeWorkspaceMember(workspaceId,userName) {
    if (!confirm(`Remove ${userName} from this workspace?`)) return;
    const response = await fetch(`/api/workspaces/${workspaceId}/members/${encodeURIComponent(userName)}`, {method:'DELETE'}); if (!response.ok) throw new Error(await response.text());
    const item = workspaceAccessCache.find(x => x.workspace.id === workspaceId); await manageWorkspaceMembers(workspaceId,item?.workspace?.name || 'Workspace');
}

async function notifyWorkspaceMember(workspaceId,userName,displayName) {
    const subject = prompt(`Notification subject for ${displayName}`, 'Workspace notification'); if (!subject) return;
    const body = prompt('Message'); if (!body) return;
    await json(`/api/workspaces/${workspaceId}/notifications`, { method:'POST', headers:{'Content-Type':'application/json'}, body:JSON.stringify({userName,subject,body}) });
    alert(`Notification queued for ${displayName}. Their messaging preferences determine which enabled channels receive it.`);
}

function showMailboxImport(workspaceId, workspaceName) {
    const panel = workspaceAdminPanel('workspaceImportPanel');
    panel.innerHTML = `<h2>${esc(workspaceName)} — Import historical mailbox</h2><p class="muted">Imports messages and attachments directly into this workspace. Gmail, Yahoo and Hotmail/Outlook normally require an app password or provider token rather than the user's normal password.</p><div class="pos-entry"><label>Provider<select id="importProvider"><option>Gmail</option><option>Hotmail</option><option>Yahoo</option></select></label><label>Mailbox address<input id="importUser" type="email"></label><label>App password / token<input id="importSecret" type="password"></label><label>Folder<input id="importFolder" value="INBOX"></label><label>From date<input id="importFrom" type="date"></label><label>Maximum messages<input id="importMax" type="number" value="5000" min="1" max="50000"></label><button class="primary" onclick="runMailboxImport('${workspaceId}')">Import mailbox</button></div><div id="importResult" class="meta"></div>`;

}

async function runMailboxImport(workspaceId) {
    const result = document.getElementById('importResult'); result.textContent = 'Importing… this can take a while for a large mailbox.';
    try {
        const fromValue = document.getElementById('importFrom').value;
        const data = await json(`/api/workspaces/${workspaceId}/email-import`, {method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({provider:document.getElementById('importProvider').value,userName:document.getElementById('importUser').value.trim(),secret:document.getElementById('importSecret').value,folder:document.getElementById('importFolder').value.trim()||null,fromUtc:fromValue ? new Date(fromValue).toISOString() : null,toUtc:null,maximumMessages:Number(document.getElementById('importMax').value)||5000})});
        result.textContent = `Imported ${data.importedMessages} messages and ${data.importedAttachments} attachments. ${data.duplicateMessages} duplicates skipped; ${data.failedMessages} failed.`;
    } catch (error) { result.textContent = error.message || error; }
}

function renderEmployeeDirectorySection() {
    return `<section class="staff-administration"><div class="staff-editors"><section class="staff-editor"><h2>Add staff member</h2><div class="staff-entry"><label>User name<input id="employeeUserName" autocomplete="off"></label><label>Display name<input id="employeeDisplayName"></label><label>Email<input id="employeeEmail" type="email"></label><label>Assignable<select id="employeeAssignable"><option value="true">Yes</option><option value="false">No</option></select></label><button type="button" class="primary" onclick="saveStudioEmployee()">Save employee</button></div></section>${renderStudioRoleEditor()}<div class="staff-global-roles" data-staff-global-roles></div></div><section class="staff-grid-section"><header><h2>Staff directory</h2><span>Edit a row, then Save. User names identify existing accounts.</span></header><div id="studioEmployeeList" class="staff-grid-scroll"><p role="status">Loading…</p></div></section>${renderStudioRoleGrid()}</section>`;
}
async function refreshStudioEmployees(){
    const holder=document.getElementById('studioEmployeeList');if(!holder)return;
    const [employees,photographers]=await Promise.all([json('/api/admin/studio-employees/'),json('/api/photographers')]);
    if(!holder.isConnected)return;
    const registered=new Set(photographers.filter(p=>p.isActive).map(p=>String(p.userName).toLowerCase()));
    holder.innerHTML=`<table class="staff-editable-grid"><caption class="sr-only">Editable staff directory</caption><thead><tr><th>User name</th><th>Display name</th><th>Email</th><th>Active</th><th>Assignable</th><th>Roles / source</th><th>Actions</th></tr></thead><tbody>${employees.map((e,i)=>`<tr data-employee-row="${i}"><td>${esc(e.userName)}</td><td><input data-employee-name aria-label="Display name for ${esc(e.userName)}" required value="${esc(e.displayName)}"></td><td><input data-employee-email type="email" aria-label="Email for ${esc(e.userName)}" value="${esc(e.emailAddress||'')}"></td><td><input data-employee-active type="checkbox" aria-label="Active: ${esc(e.userName)}" ${e.isActive?'checked':''}></td><td><input data-employee-assignable type="checkbox" aria-label="Assignable: ${esc(e.userName)}" ${e.isAssignable?'checked':''}></td><td>${esc((e.roles||[]).join(', ')||e.source||'Studio')}</td><td><div class="staff-row-actions"><button type="button" class="action" data-employee-save disabled>Save</button><button type="button" class="action" data-employee-undo disabled>Undo</button>${registered.has(String(e.userName).toLowerCase())?'<span class="studio-status-pill">Photographer</span>':`<button type="button" class="action" data-register-photographer ${!e.isActive||!e.isAssignable?'disabled':''}>Register photographer</button>`}</div><span data-row-status role="status"></span></td></tr>`).join('')||'<tr><td colspan="7">No staff members yet.</td></tr>'}</tbody></table>`;
    holder.querySelectorAll('[data-employee-row]').forEach(row=>{
        const employee=employees[Number(row.dataset.employeeRow)],save=row.querySelector('[data-employee-save]'),undo=row.querySelector('[data-employee-undo]'),status=row.querySelector('[data-row-status]');
        const dirty=()=>{save.disabled=false;undo.disabled=false;status.textContent='Unsaved';};row.querySelectorAll('input').forEach(input=>input.oninput=dirty);
        undo.onclick=()=>{row.querySelector('[data-employee-name]').value=employee.displayName||'';row.querySelector('[data-employee-email]').value=employee.emailAddress||'';row.querySelector('[data-employee-active]').checked=employee.isActive;row.querySelector('[data-employee-assignable]').checked=employee.isAssignable;save.disabled=undo.disabled=true;status.textContent='';};
        save.onclick=async()=>{
            if([...row.querySelectorAll('input')].some(input=>!input.reportValidity()))return;
            const payload={userName:employee.userName,displayName:row.querySelector('[data-employee-name]').value.trim(),emailAddress:row.querySelector('[data-employee-email]').value.trim()||null,isActive:row.querySelector('[data-employee-active]').checked,isAssignable:row.querySelector('[data-employee-assignable]').checked,source:employee.source||'Manual',externalId:employee.externalId||null,skills:employee.skills||[],workDayUserName:employee.workDayUserName||null,lastSyncedAtUtc:employee.lastSyncedAtUtc||null};
            if(!payload.displayName){status.textContent='Display name is required.';return;}
            row.querySelectorAll('input,button').forEach(el=>el.disabled=true);status.textContent='Saving…';let saved=false;
            try{const latest=(await json('/api/admin/studio-employees/')).find(e=>String(e.userName).toLowerCase()===String(employee.userName).toLowerCase());if(!latest)throw Error('Staff member no longer exists. Reload the directory.');for(const key of ['source','externalId','skills','workDayUserName','lastSyncedAtUtc'])if(key in latest)payload[key]=latest[key];await json('/api/admin/studio-employees/'+encodeURIComponent(employee.userName),{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify(payload)});Object.assign(employee,payload);status.textContent='Saved';saved=true;}catch(error){status.textContent=error.message||'Could not save. Your edits are retained.';}
            finally{row.querySelectorAll('input,button').forEach(el=>el.disabled=false);save.disabled=undo.disabled=saved;const register=row.querySelector('[data-register-photographer]');if(register)register.disabled=!employee.isActive||!employee.isAssignable;}
        };
        const register=row.querySelector('[data-register-photographer]');if(register)register.onclick=()=>registerStudioPhotographer(employee);
    });
}
function registerStudioPhotographer(employee){
    if(!employee.isActive||!employee.isAssignable)return;
    const d=studioDialog('Register photographer',`<form class="request-form"><p><strong>${esc(employee.displayName)}</strong> · ${esc(employee.userName)}</p><p class="muted">This uses the existing Studio employee identity for assignments and WorkDay.</p><label>Employment type<select name="employmentType"><option>Employee</option><option>PartTime</option><option>Contractor</option><option>Freelance</option></select></label><label>Skills (comma separated)<input name="skills" maxlength="1000" value="${esc((employee.skills||[]).join(', '))}"></label><label>Default equipment notes<textarea name="equipment" maxlength="4000"></textarea></label><p role="alert"></p><div class="dialog-actions"><button type="button" data-cancel>Cancel</button><button class="primary" type="submit">Register photographer</button></div></form>`);
    d.querySelector('[data-cancel]').onclick=()=>d.close();
    d.querySelector('form').onsubmit=async event=>{
        event.preventDefault();const form=event.currentTarget,save=form.querySelector('[type=submit]');if(save.disabled)return;save.disabled=true;
        try{
            await json('/api/photographers',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({userName:employee.userName,displayName:employee.displayName,employmentType:form.elements.employmentType.value,isActive:true,skills:form.elements.skills.value.split(',').map(x=>x.trim()).filter(Boolean),defaultEquipmentNotes:form.elements.equipment.value.trim()||null,scheduleResourceId:null})});
            d.close();await refreshStudioEmployees();
        }catch(error){if(d.isConnected)form.querySelector('[role=alert]').textContent=error.message||String(error);else alert(error.message||error);}
        finally{if(save.isConnected)save.disabled=false;}
    };
}
async function saveStudioEmployee(){
    const userName=document.getElementById('employeeUserName').value.trim();if(!userName)return alert('User name is required.');
    const existing=(await json('/api/admin/studio-employees/')).find(e=>String(e.userName).toLowerCase()===userName.toLowerCase());
    const payload={userName,displayName:document.getElementById('employeeDisplayName').value.trim()||userName,emailAddress:document.getElementById('employeeEmail').value.trim()||null,isActive:existing?.isActive!==false,isAssignable:document.getElementById('employeeAssignable').value==='true',source:existing?.source||'Manual',externalId:existing?.externalId||null,skills:existing?.skills||[],workDayUserName:existing?.workDayUserName||null,lastSyncedAtUtc:existing?.lastSyncedAtUtc||null};
    await json('/api/admin/studio-employees/'+encodeURIComponent(userName),{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify(payload)});
    await refreshStudioEmployees();
}

function renderStudioRoleEditor(){
    return `<section class="staff-editor"><h2>Create reusable role</h2><div class="staff-entry"><label>Name<input id="roleName"></label><label>Scope<select id="roleScope"><option>Job</option><option>Workspace</option></select></label><label>Description<input id="roleDescription"></label><label>Template<select id="roleTemplate"><option value="viewer">Viewer</option><option value="worker">Worker</option><option value="manager">Manager</option><option value="admin">Administrator</option></select></label><button type="button" class="primary" onclick="createStudioRole()">Create role</button></div></section>`;
}
function renderStudioRoleGrid(){
    return `<section class="staff-grid-section"><header><h2>Reusable security roles</h2><span>Name and description edits preserve existing permissions.</span></header><div id="securityRoleList" class="staff-grid-scroll"><table class="staff-editable-grid"><caption class="sr-only">Editable reusable security roles</caption><thead><tr><th>Name</th><th>Scope</th><th>Description</th><th>Active</th><th>Actions</th></tr></thead><tbody>${studioRolesCache.filter(x=>['Job','Workspace'].includes(x.scope)).map(x=>`<tr data-role-row="${esc(x.id)}"><td><input data-role-name aria-label="Name for role ${esc(x.name)}" value="${esc(x.name)}" required></td><td>${esc(x.scope)}</td><td><input data-role-description aria-label="Description for role ${esc(x.name)}" value="${esc(x.description||'')}"></td><td><input data-role-active type="checkbox" aria-label="Active role ${esc(x.name)}" ${x.isActive?'checked':''}></td><td><div class="staff-row-actions"><button type="button" class="action" onclick="saveStudioRoleRow(this)">Save</button><button type="button" class="action" onclick="deleteStudioRole(this.closest('[data-role-row]').dataset.roleRow)">Delete</button></div><span data-row-status role="status"></span></td></tr>`).join('')||'<tr><td colspan="5">No reusable roles yet.</td></tr>'}</tbody></table></div></section>`;
}

async function saveStudioRoleRow(button){
    const row=button.closest('[data-role-row]'),role=studioRolesCache.find(r=>String(r.id)===row.dataset.roleRow),status=row.querySelector('[data-row-status]');if(!role)return;
    const name=row.querySelector('[data-role-name]');if(!name.reportValidity()||!name.value.trim()){status.textContent='Role name is required.';return;}
    const payload={name:name.value.trim(),scope:role.scope,description:row.querySelector('[data-role-description]').value.trim()||null,isActive:row.querySelector('[data-role-active]').checked,permissionsJson:role.permissionsJson};
    row.querySelectorAll('input,button').forEach(el=>el.disabled=true);status.textContent='Saving…';
    try{const latest=(await json('/api/security/roles')).find(r=>String(r.id)===String(role.id));if(!latest)throw Error('Role no longer exists. Reload the roles.');payload.scope=latest.scope;payload.permissionsJson=latest.permissionsJson;await json('/api/security/roles/'+encodeURIComponent(role.id),{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify(payload)});Object.assign(role,payload);status.textContent='Saved';}catch(error){status.textContent=error.message||'Could not save. Your edits are retained.';}finally{row.querySelectorAll('input,button').forEach(el=>el.disabled=false);}
}

function roleTemplate(scope, template) {
    if (scope === 'Workspace') {
        if (template === 'viewer') return permissionsForPreset('view'); if (template === 'worker') return permissionsForPreset('reply'); if (template === 'manager') return permissionsForPreset('editor'); return permissionsForPreset('admin');
    }
    const keys=['accessJob','viewJob','viewConversation','replyRequester','viewAttachments','addAttachments','editJob','changeStatus','assignStaff','manageSchedule','manageProduction','viewFinancials','recordFinancialActivity','usePos','viewPosTransactions','refundVoidPos','notifyJobMembers','manageJobMembers','adminJob'];
    const p=Object.fromEntries(keys.map(k=>[k,false])); p.accessJob=p.viewJob=p.viewConversation=p.viewAttachments=true;
    if(template!=='viewer'){p.addAttachments=p.manageSchedule=p.manageProduction=p.notifyJobMembers=true;}
    if(template==='manager'||template==='admin'){Object.assign(p,{replyRequester:true,editJob:true,changeStatus:true,assignStaff:true,viewFinancials:true,recordFinancialActivity:true,usePos:true,viewPosTransactions:true,manageJobMembers:true});}
    if(template==='admin') Object.keys(p).forEach(k=>p[k]=true); return p;
}

async function createStudioRole() {
    const name=document.getElementById('roleName').value.trim(); if(!name)return alert('Role name is required.'); const scope=document.getElementById('roleScope').value; const permissions=roleTemplate(scope,document.getElementById('roleTemplate').value);
    await json('/api/security/roles',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({name,scope,description:document.getElementById('roleDescription').value.trim()||null,permissionsJson:JSON.stringify(permissions),isActive:true})}); await studioNavigate('staff-roles');
}
async function deleteStudioRole(id){if(!confirm('Delete this reusable role? Existing members keep their copied permissions but lose the role link.'))return;const r=await fetch('/api/security/roles/'+id,{method:'DELETE'});if(!r.ok)throw new Error(await r.text());await studioNavigate('staff-roles');}

function renderWorkflowSection() {
    return `<section class="job-section"><h2>Workflow automation</h2><p class="muted">Database-backed event/condition/action workflows. Supported triggers include JobStatusChanged, ScheduleChanged, ProductionChanged and DeliveryChanged. Actions: coordination CreateActivity, NotifyJob, NotifyRequester and WaitMinutes. Job status, ownership, production dispatch and scheduling require authorized Job workflow decisions.</p><div id="workflowList"><p class="muted">Loading…</p></div><h3>Create workflow</h3><div class="pos-entry"><label>Name<input id="workflowName"></label><label>Trigger<select id="workflowTrigger"><option>JobStatusChanged</option><option>ScheduleChanged</option><option>ProductionChanged</option><option>DeliveryChanged</option></select></label><label>Job status condition<input id="workflowStatusCondition" placeholder="optional, e.g. Shoot Complete"></label></div><label style="display:block;margin-top:10px">Actions JSON<textarea id="workflowActions" rows="8" style="width:100%">[{"type":"CreateActivity","activityType":"Other","title":"Review backup verification","dueMinutes":120},{"type":"NotifyJob","subject":"Workflow started","body":"Automated production workflow started."}]</textarea></label><button class="primary" onclick="createWorkflowDefinition()">Create workflow</button><h3>Authorization audit</h3><button class="action" onclick="runAuthorizationAudit()">Run endpoint audit</button><div id="authorizationAuditResult" class="meta"></div></section>`;
}

async function refreshWorkflowList(){const holder=document.getElementById('workflowList');if(!holder)return;const workflows=await json('/api/workflows/');holder.innerHTML=workflows.map(x=>`<div class="job-row"><div><b>${esc(x.name)}</b><div class="meta">When ${esc(x.triggerEvent)} · ${x.isEnabled?'Enabled':'Disabled'}</div></div><button class="action" onclick="deleteWorkflowDefinition('${x.id}')">Delete</button></div>`).join('')||'<p class="muted">No workflow definitions yet.</p>';}
async function createWorkflowDefinition(){const name=document.getElementById('workflowName').value.trim();if(!name)return alert('Workflow name is required.');const status=document.getElementById('workflowStatusCondition').value.trim();await json('/api/workflows/',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({name,description:null,triggerEvent:document.getElementById('workflowTrigger').value,conditionsJson:JSON.stringify(status?{jobStatus:status}:{}),actionsJson:document.getElementById('workflowActions').value,isEnabled:true})});await refreshWorkflowList();}
async function deleteWorkflowDefinition(id){if(!confirm('Delete workflow?'))return;const r=await fetch('/api/workflows/'+id,{method:'DELETE'});if(!r.ok)throw new Error(await r.text());await refreshWorkflowList();}
async function runAuthorizationAudit(){const report=await json('/api/security/authorization-audit');document.getElementById('authorizationAuditResult').textContent=`Checked ${report.apiEndpointCount} API endpoints. Unapproved anonymous: ${report.unapprovedAnonymous.length ? report.unapprovedAnonymous.join(', ') : 'none'}.`;}

const workspaceNavigationButton = document.querySelector('.brand-button[data-view="workspaces"]');
if (workspaceNavigationButton) workspaceNavigationButton.onclick = () => showWorkspaces();

async function seedStudioDemoData(button) {
    if (!confirm('Load or upgrade the DEMO workspaces with gallery illustrations, versions, subjects, appointments and scheduled tasks? Existing demo data will be kept and duplicates skipped. The initial demo includes fictional deposits in account totals. Use a demo database.')) return;
    button.disabled = true;
    try {
        const result = await json('/api/admin/demo/seed', { method: 'POST' });
        await loadWorkspaces();
        await showWorkspaces();
        alert(result.message);
    } catch (error) {
        alert('Unable to load demo data: ' + (error.message || error));
    } finally {
        button.disabled = false;
    }
}
