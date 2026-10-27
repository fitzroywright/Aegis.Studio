/* Owns the editable public session catalog and opening-hours grids. */
window.StudioBookingSessions=(()=>{
    const locations=[['BK','Kings Plaza'],['BE','East Queen Street']];
    const days=['Sunday','Monday','Tuesday','Wednesday','Thursday','Friday','Saturday'];
    const esc=value=>window.StudioPage.escape(value);
    const clone=value=>JSON.parse(JSON.stringify(value));
    function html(){return `<section class="booking-sessions" aria-label="Booking session configuration"><nav class="booking-tabs" aria-label="Booking settings"><button type="button" data-booking-tab="sessions" aria-pressed="true">Session Types</button><button type="button" data-booking-tab="hours" aria-pressed="false">Opening Hours</button></nav><div class="booking-toolbar"><label><span class="sr-only">Search session types</span><input type="search" data-booking-search placeholder="Find a session…"></label><span data-booking-revision></span><button type="button" class="action" data-booking-reload>Reload</button><button type="button" class="action" data-booking-add>＋ Add session</button><button type="button" class="primary" data-booking-save disabled>Save changes</button></div><p class="booking-help" data-booking-help></p><p class="booking-feedback" role="status" data-booking-status>Loading booking settings…</p><div class="booking-grid-scroll" data-booking-grid></div><footer class="booking-grid-footer" data-booking-footer></footer></section>`;}
    async function mount(host,current){
        const root=host.querySelector('.booking-sessions');if(!root)return;
        const live=()=>current()&&root.isConnected;
        const field=name=>root.querySelector('[data-booking-'+name+']');
        const state={tab:'sessions',sessions:[],hours:[],workspaces:[],resourceTypes:['Photographer'],loaded:{sessions:false,hours:false},dirty:{sessions:false,hours:false},errors:{},search:'',saving:false,revision:null};
        let dialog=null,loadRevision=0;
        const status=message=>{if(live())field('status').textContent=message;};
        const dirty=()=>{state.dirty[state.tab]=true;field('save').disabled=false;status('Unsaved changes');};
        function controls(){
            field('save').disabled=state.saving||!state.loaded[state.tab]||!state.dirty[state.tab];
            field('add').disabled=state.saving||!state.loaded[state.tab];field('add').textContent=state.tab==='sessions'?'＋ Add session':'＋ Add opening window';
            field('search').hidden=state.tab!=='sessions';
            root.querySelectorAll('[data-booking-tab]').forEach(button=>button.setAttribute('aria-pressed',String(button.dataset.bookingTab===state.tab)));
            field('revision').textContent=state.revision==null?'':'Catalog revision '+state.revision;
            field('help').textContent=state.tab==='sessions'?'Edit session names, minutes, JMD prices and deposits directly. Details contains descriptions, buffers, routing and resource requirements. Save changes to update Studio; external publication retries automatically when enabled.':'Weekly opening windows in Jamaica time. Omit closed days. Add another window for a split day; windows at the same location must not overlap. Save hours separately from session types.';
        }
        const number=(key,value,label,min,max,step=1)=>`<input type="number" data-field="${key}" value="${esc(value)}" aria-label="${label}" min="${min}" ${max==null?'':`max="${max}"`} step="${step}" required>`;
        function paintGrid(){
            if(!live())return;controls();
            if(!state.loaded[state.tab]){field('grid').innerHTML='<p class="booking-empty" role="alert">'+esc(state.errors[state.tab]||'Loading…')+'</p>';field('footer').textContent='';return;}
            if(state.tab==='sessions'){
                const rows=state.sessions.filter(row=>(row.name+' '+row.description).toLowerCase().includes(state.search.toLowerCase()));
                field('grid').innerHTML=`<table class="booking-grid booking-session-grid"><caption class="sr-only">Editable booking session types. All amounts in JMD.</caption><thead><tr><th scope="col">Session</th><th scope="col">Minutes</th><th scope="col">Price · JMD</th><th scope="col">Deposit · JMD</th><th scope="col">Locations</th><th scope="col">Active</th><th scope="col">Publish</th><th scope="col">Settings</th></tr></thead><tbody>${rows.map(row=>`<tr data-booking-session="${esc(row.id)}"><td><input data-field="name" value="${esc(row.name)}" aria-label="Session name" maxlength="160" required><small>${esc(row.description||'No public description')}</small></td><td>${number('durationMinutes',row.durationMinutes,'Session duration in minutes',15,1440)}</td><td>${number('price',row.price,'Session price in JMD',0,null,'.01')}</td><td>${number('deposit',row.deposit,'Session deposit in JMD',0,null,'.01')}</td><td><div class="booking-locations">${locations.map(([code,label])=>`<label><input type="checkbox" data-location="${code}" ${row.locations.includes(code)?'checked':''}>${label}</label>`).join('')}</div></td><td><input type="checkbox" data-field="isActive" aria-label="Session active" ${row.isActive?'checked':''}></td><td><input type="checkbox" data-field="isPublished" aria-label="Publish session" ${row.isPublished?'checked':''}></td><td><div class="booking-row-actions"><button type="button" class="action" data-booking-details>Details</button><button type="button" class="booking-remove" data-booking-remove aria-label="Remove ${esc(row.name||'new session')}">Remove</button></div></td></tr>`).join('')||'<tr><td colspan="8" class="booking-empty">'+(state.sessions.length?'No sessions match your search.':'No session types yet. Add your first session to get started.')+'</td></tr>'}</tbody></table>`;
                field('footer').textContent=rows.length+' of '+state.sessions.length+' session types · Only active, published sessions appear on the API Site.';
            }else{
                field('grid').innerHTML=`<table class="booking-grid booking-hours-grid"><caption class="sr-only">Editable opening hours in Jamaica time</caption><thead><tr><th scope="col">Location</th><th scope="col">Day</th><th scope="col">Opens</th><th scope="col">Closes</th><th scope="col">Slot interval · min</th><th scope="col">Action</th></tr></thead><tbody>${state.hours.map((row,index)=>`<tr data-booking-hour="${index}"><td><select data-field="locationCode" aria-label="Opening location">${locations.map(([code,label])=>`<option value="${code}" ${row.locationCode===code?'selected':''}>${label}</option>`).join('')}</select></td><td><select data-field="dayOfWeek" aria-label="Opening day">${days.map((label,value)=>`<option value="${value}" ${Number(row.dayOfWeek)===value?'selected':''}>${label}</option>`).join('')}</select></td><td><input data-field="opensAt" type="time" value="${esc(row.opensAt)}" aria-label="Opening time" required></td><td><input data-field="closesAt" type="time" value="${esc(row.closesAt)}" aria-label="Closing time" required></td><td>${number('slotStepMinutes',row.slotStepMinutes,'Slot interval in minutes',5,240)}</td><td><button type="button" class="booking-remove" data-booking-remove>Remove</button></td></tr>`).join('')||'<tr><td colspan="6" class="booking-empty">No opening hours configured. Add an opening window for each working day.</td></tr>'}</tbody></table>`;
                field('footer').textContent=state.hours.length+' opening windows · America/Jamaica';
            }
        }
        async function load(){
            const turn=++loadRevision;
            status('Loading booking settings…');state.loaded={sessions:false,hours:false};paintGrid();
            const results=await Promise.allSettled([json('/api/admin/public-booking/session-types/'),json('/api/admin/public-booking/hours'),json('/api/admin/public-booking/session-types/workspaces'),json('/api/schedule/resources')]);
            if(!live()||turn!==loadRevision)return;
            const [catalog,hours,workspaces,resources]=results;
            if(catalog.status==='fulfilled'&&Array.isArray(catalog.value.sessionTypes)){
                state.sessions=catalog.value.sessionTypes.map(row=>({...clone(row),price:String(row.priceMinor/100),deposit:String(row.depositMinor/100)}));state.revision=catalog.value.revision;state.loaded.sessions=true;
            }else state.errors.sessions='Session types could not be loaded. Use Reload to retry.';
            if(hours.status==='fulfilled'&&Array.isArray(hours.value)){state.hours=clone(hours.value);state.loaded.hours=true;}else state.errors.hours='Opening hours could not be loaded. Use Reload to retry.';
            state.workspaces=workspaces.status==='fulfilled'&&Array.isArray(workspaces.value)?workspaces.value:[];
            if(resources.status==='fulfilled'&&Array.isArray(resources.value))state.resourceTypes=[...new Set(['Photographer',...resources.value.filter(row=>row.isActive).map(row=>row.resourceType)])];
            state.dirty={sessions:false,hours:false};paintGrid();status(state.errors[state.tab]&&!state.loaded[state.tab]?state.errors[state.tab]:'');
        }
        function sessionPayload(){
            const names=new Set();
            return state.sessions.map(row=>{
                const {price,deposit,...saved}=row;const p=Math.round(Number(price)*100),d=Math.round(Number(deposit)*100),name=row.name.trim();
                if(!name||names.has(name)||name.length>160)throw Error('Use a unique name for every session (up to 160 characters).');names.add(name);
                if(price===''||deposit===''||!Number.isSafeInteger(p)||!Number.isSafeInteger(d)||p<0||d<0||d>p)throw Error(name+': deposit must be between zero and the JMD price.');
                if(!Number.isInteger(Number(row.durationMinutes))||Number(row.durationMinutes)<15||Number(row.durationMinutes)>1440)throw Error(name+': duration must be 15–1440 whole minutes.');
                if(!row.locations.length)throw Error(name+': select at least one location.');
                return {...saved,name,durationMinutes:Number(row.durationMinutes),priceMinor:p,depositMinor:d,currency:'JMD'};
            });
        }
        const clock=value=>{const parts=/^(\d{2}):(\d{2})(?::(\d{2}))?$/.exec(value);return parts&&Number(parts[1])<24&&Number(parts[2])<60&&Number(parts[3]||0)<60?Number(parts[1])*3600+Number(parts[2])*60+Number(parts[3]||0):NaN;};
        function hoursPayload(){
            const rows=state.hours.map(row=>({...row,dayOfWeek:Number(row.dayOfWeek),slotStepMinutes:Number(row.slotStepMinutes)}));
            for(const row of rows)if(!Number.isFinite(clock(row.opensAt))||!Number.isFinite(clock(row.closesAt))||clock(row.closesAt)<=clock(row.opensAt)||!Number.isInteger(row.slotStepMinutes)||row.slotStepMinutes<5||row.slotStepMinutes>240)throw Error('Each opening window needs a closing time after opening and a slot interval of 5–240 whole minutes.');
            const ordered=[...rows].sort((a,b)=>(a.locationCode+a.dayOfWeek).localeCompare(b.locationCode+b.dayOfWeek)||clock(a.opensAt)-clock(b.opensAt));
            for(let i=1;i<ordered.length;i++){const a=ordered[i-1],b=ordered[i];if(a.locationCode===b.locationCode&&a.dayOfWeek===b.dayOfWeek&&clock(b.opensAt)<clock(a.closesAt))throw Error('Opening windows for the same location and weekday cannot overlap.');}
            return rows;
        }
        field('save').onclick=async()=>{
            if(state.saving||!state.loaded[state.tab]||!state.dirty[state.tab])return;
            let payload;try{payload=state.tab==='sessions'?sessionPayload():hoursPayload();}catch(error){status(error.message);return;}
            const tab=state.tab;state.saving=true;root.querySelectorAll('button,input,select').forEach(control=>control.disabled=true);status('Saving…');
            try{
                const result=await json(tab==='sessions'?'/api/admin/public-booking/session-types/':'/api/admin/public-booking/hours',{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify(payload)});
                if(!live())return;
                if(tab==='sessions'){if(!Array.isArray(result.sessionTypes))throw Error('Unexpected catalog response. Reload to verify the saved state.');state.sessions=result.sessionTypes.map(row=>({...row,price:String(row.priceMinor/100),deposit:String(row.depositMinor/100)}));}
                state.revision=result.revision;state.dirty[tab]=false;status(tab==='sessions'?'Session types saved in Studio. API Site publication will retry when enabled.':'Opening hours saved in Studio.');
            }catch(error){status('Save failed: '+error.message);}
            finally{if(live()){state.saving=false;root.querySelectorAll('button,input,select').forEach(control=>control.disabled=false);paintGrid();}}
        };
        root.querySelectorAll('[data-booking-tab]').forEach(button=>button.onclick=()=>{state.tab=button.dataset.bookingTab;paintGrid();status(state.dirty[state.tab]?'Unsaved changes':state.loaded[state.tab]?'':state.errors[state.tab]);});
        field('reload').onclick=()=>{if((state.dirty.sessions||state.dirty.hours)&&!confirm('Discard unsaved booking settings and reload?'))return;void load();};
        field('search').oninput=event=>{state.search=event.target.value;paintGrid();};
        field('add').onclick=()=>{
            if(state.tab==='sessions'){state.search='';field('search').value='';state.sessions.push({id:crypto.randomUUID(),name:'',description:'',durationMinutes:60,setupBufferMinutes:0,cleanupBufferMinutes:0,price:'0',deposit:'0',priceMinor:0,depositMinor:0,currency:'JMD',locations:['BK'],resourceRequirements:{Photographer:1},isActive:true,isPublished:false,defaultWorkspaceId:null});}
            else state.hours.push({locationCode:'BK',dayOfWeek:1,opensAt:'09:00',closesAt:'17:00',slotStepMinutes:30});
            dirty();paintGrid();const row=field('grid').querySelector('tbody tr:last-child');row?.querySelector('input,select')?.focus();row?.scrollIntoView?.({block:'nearest'});
        };
        field('grid').addEventListener('input',event=>{
            const control=event.target,row=control.closest('tr');if(!row)return;
            const item=row.hasAttribute('data-booking-session')?state.sessions.find(item=>item.id===row.dataset.bookingSession):state.hours[Number(row.dataset.bookingHour)];if(!item)return;
            if(control.dataset.location){item.locations=[...row.querySelectorAll('[data-location]:checked')].map(input=>input.dataset.location);}
            else if(control.dataset.field)item[control.dataset.field]=control.type==='checkbox'?control.checked:control.value;
            else return;dirty();
        });
        field('grid').addEventListener('click',event=>{
            const row=event.target.closest('tr');if(!row)return;
            if(event.target.closest('[data-booking-remove]')){
                if(!confirm('Remove this '+(state.tab==='sessions'?'session type':'opening window')+'? The removal takes effect when you save changes.'))return;
                if(state.tab==='sessions')state.sessions=state.sessions.filter(item=>item.id!==row.dataset.bookingSession);else state.hours.splice(Number(row.dataset.bookingHour),1);dirty();paintGrid();
            }else if(event.target.closest('[data-booking-details]'))editDetails(state.sessions.find(item=>item.id===row.dataset.bookingSession));
        });
        function editDetails(row){
            const resourceRows=Object.entries(row.resourceRequirements||{});
            dialog=studioDialog((row.name||'New session')+' · Details',`<form class="admin-form-stack"><label>Public description<textarea name="description" rows="3" maxlength="1000">${esc(row.description)}</textarea></label><div class="admin-form-row"><label>Setup minutes<input name="setup" type="number" min="0" max="480" value="${row.setupBufferMinutes}" required></label><label>Cleanup minutes<input name="cleanup" type="number" min="0" max="480" value="${row.cleanupBufferMinutes}" required></label></div><label>Default Workspace<select name="workspace"><option value="">Unassigned — Front Desk review</option>${state.workspaces.map(item=>`<option value="${esc(item.id)}" ${row.defaultWorkspaceId===item.id?'selected':''}>${esc(item.name)}</option>`).join('')}${row.defaultWorkspaceId&&!state.workspaces.some(item=>item.id===row.defaultWorkspaceId)?`<option value="${esc(row.defaultWorkspaceId)}" selected>Current workspace (details unavailable)</option>`:''}</select></label><p>Clients choose a session and location. Workspace is internal routing only.</p><h3>Required resources</h3><p>Use location-specific types such as Room-BK for rooms kept at one location.</p><datalist id="bookingResourceTypes">${state.resourceTypes.map(type=>`<option value="${esc(type)}">`).join('')}</datalist><div data-resource-rows></div><button type="button" class="action" data-resource-add>＋ Add resource requirement</button><p role="alert"></p><button type="submit" class="primary">Apply to session</button><small>Apply updates the draft. Use Save changes on the grid to save to Studio.</small></form>`);
            const form=dialog.querySelector('form'),rows=form.querySelector('[data-resource-rows]');
            const add=(type='',count=1)=>{const wrap=document.createElement('div');wrap.className='booking-resource-row';wrap.innerHTML=`<label>Resource type<input data-resource-type list="bookingResourceTypes" value="${esc(type)}" maxlength="80" required></label><label>Count<input data-resource-count type="number" min="0" max="20" value="${count}" required></label><button type="button" class="action">Remove</button>`;wrap.querySelector('button').onclick=()=>wrap.remove();rows.append(wrap);};
            resourceRows.forEach(([type,count])=>add(type,count));form.querySelector('[data-resource-add]').onclick=()=>add();
            form.onsubmit=event=>{
                event.preventDefault();if(!live())return;
                const rules={};for(const wrap of rows.children){const type=wrap.querySelector('[data-resource-type]').value.trim(),count=Number(wrap.querySelector('[data-resource-count]').value);if(!/^[A-Za-z0-9._-]{1,80}$/.test(type)||Object.hasOwn(rules,type)||!Number.isInteger(count)||count<0||count>20){form.querySelector('[role=alert]').textContent='Use unique resource types and whole-number counts from 0 to 20.';return;}Object.defineProperty(rules,type,{value:count,enumerable:true,writable:true});}
                const setup=Number(form.elements.setup.value),cleanup=Number(form.elements.cleanup.value);if(!Number.isInteger(setup)||!Number.isInteger(cleanup)||setup<0||cleanup<0||setup>480||cleanup>480){form.querySelector('[role=alert]').textContent='Buffers must be whole minutes from 0 to 480.';return;}
                Object.assign(row,{description:form.elements.description.value,setupBufferMinutes:setup,cleanupBufferMinutes:cleanup,defaultWorkspaceId:form.elements.workspace.value||null,resourceRequirements:rules});dialog.close();dirty();paintGrid();
            };
        }
        // Close page-owned dialogs when the route is invalidated by the shared shell.
        const observer=new MutationObserver(()=>{if(!live()){dialog?.close();observer.disconnect();}});observer.observe(document.body,{subtree:true,childList:true});
        await load();
    }
    return {html,mount};
})();
