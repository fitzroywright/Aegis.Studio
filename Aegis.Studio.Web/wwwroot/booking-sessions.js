/* Owns the editable public session catalog and opening-hours grids. */
window.StudioBookingSessions=(()=>{
    const locations=[['BK','Kings Plaza'],['BE','East Queen Street']];
    const days=['Sunday','Monday','Tuesday','Wednesday','Thursday','Friday','Saturday'];
    const esc=value=>window.StudioPage.escape(value);
    const clone=value=>JSON.parse(JSON.stringify(value));
    function html(options={}){return `<section class="booking-sessions" aria-label="Booking session configuration">${options.embedded?'':`<nav class="booking-tabs" aria-label="Booking settings"><button type="button" data-booking-tab="sessions" aria-pressed="true">Services &amp; Sessions</button><button type="button" data-booking-tab="hours" aria-pressed="false">Opening Hours</button></nav>`}<div class="booking-toolbar"><span data-booking-count></span><button type="button" class="action" data-booking-reload>Reload</button><button type="button" class="action" data-booking-add ${options.embedded?'hidden':''}>＋ Add service</button><button type="button" class="primary" data-booking-save disabled>Save changes</button></div><p class="booking-help" data-booking-help></p><p class="booking-feedback" role="status" data-booking-status>Loading booking settings...</p><div class="booking-catalog-workspace"><div class="booking-grid-scroll" data-booking-grid></div><section class="booking-details" data-booking-editor hidden aria-label="Selected session details"></section></div><footer class="booking-grid-footer" data-booking-footer></footer></section>`;}
    async function mount(host,current,options={}){
        const root=host.querySelector('.booking-sessions');if(!root)return;
        const live=()=>current()&&root.isConnected;
        const field=name=>root.querySelector('[data-booking-'+name+']')||host.closest('.st-page')?.querySelector('[data-booking-'+name+']');
        const state={tab:'sessions',sessions:[],hours:[],workspaces:[],resourceTypes:['Photographer'],loaded:{sessions:false,hours:false},dirty:{sessions:false,hours:false},errors:{},search:'',category:'',location:'',filter:'',selected:null,detailBaseline:null,selectedHour:0,hourLocation:'BK',saving:false,revision:null};
        let loadRevision=0;const persisted=new Map();
        const status=message=>{if(live())field('status').textContent=message;};
        const dirty=()=>{state.dirty[state.tab]=true;controls();status('Unsaved changes');};
        function controls(){
            field('save').disabled=state.saving||!state.loaded[state.tab]||!state.dirty[state.tab];
            const detailSave=field('editor').querySelector('[type=submit]');if(detailSave)detailSave.disabled=field('save').disabled;
            field('add').disabled=state.saving||!state.loaded[state.tab];field('add').textContent=state.tab==='sessions'?'＋ Add service':'＋ Add opening window';
            // Column filters live inside the sessions table and are absent on Opening Hours.
            // Their visibility follows the table; do not access removed toolbar controls.
            root.querySelectorAll('[data-booking-tab]').forEach(button=>button.setAttribute('aria-pressed',String(button.dataset.bookingTab===state.tab)));
            field('count').textContent=state.tab==='sessions'?state.sessions.length+' services · '+state.sessions.filter(row=>row.isActive&&row.isPublished).length+' bookable online':state.hours.length+' opening windows';
            options.onSummary?.({hours:state.hours,hoursLoaded:state.loaded.hours,dirtyHours:state.dirty.hours,dirtySessions:state.dirty.sessions,revision:state.revision});
            field('help').textContent=state.tab==='sessions'?'Online booking switches save immediately. Use Save changes for other catalog edits.':'Weekly opening windows in Jamaica time. Omit closed days. Add another window for a split day; windows at the same location must not overlap. Save hours separately from session types.';
        }
        const number=(key,value,label,min,max,step=1)=>`<input type="number" data-field="${key}" value="${esc(value)}" aria-label="${label}" min="${min}" ${max==null?'':`max="${max}"`} step="${step}" required>`;
        const locationChips=row=>row.locations.map(code=>'<span class="booking-location-chip">'+esc(locations.find(x=>x[0]===code)?.[1]||code)+'</span>').join('')||'<span>Choose locations</span>';
        function paintGrid(){
            if(!live())return;controls();
            if(!state.loaded[state.tab]){field('grid').innerHTML='<p class="booking-empty" role="alert">'+esc(state.errors[state.tab]||'Loading…')+'</p>';field('footer').textContent='';return;}
            if(state.tab==='sessions'){
                const categories=[...new Set(state.sessions.map(row=>row.category||'').filter(Boolean))].sort();
                
                const rows=state.sessions.filter(row=>(row.name+' '+row.description+' '+(row.category||'')).toLowerCase().includes(state.search.toLowerCase())&&
                    (!state.category||row.category===state.category)&&(!state.location||row.locations.includes(state.location))&&
                    (!state.filter||state.filter==='online'&&row.isActive&&row.isPublished||state.filter==='unpublished'&&!row.isPublished||state.filter==='inactive'&&!row.isActive));
                field('grid').innerHTML=`<table class="booking-grid booking-session-grid"><caption class="sr-only">Services and booking sessions. Select a service to edit its details.</caption><thead><tr><th><label class="catalog-column-filter catalog-service-filter"><span>Service</span><input type="search" data-booking-search placeholder="Search services…" aria-label="Search services" value="${esc(state.search)}"></label></th><th><label class="catalog-column-filter catalog-select-only"><select data-booking-category aria-label="Filter service category"><option value="">All categories</option>${categories.map(name=>`<option value="${esc(name)}" ${state.category===name?'selected':''}>${esc(name)}</option>`).join('')}</select></label></th><th>Duration</th><th>Price (JMD)</th><th>Deposit (JMD)</th><th>Online</th><th><label class="catalog-column-filter catalog-select-only"><select data-booking-filter aria-label="Filter service status"><option value="">All statuses</option><option value="online" ${state.filter==='online'?'selected':''}>Bookable online</option><option value="unpublished" ${state.filter==='unpublished'?'selected':''}>Not published</option><option value="inactive" ${state.filter==='inactive'?'selected':''}>Inactive</option></select></label></th><th></th></tr></thead><tbody>${rows.map(row=>`<tr data-booking-session="${esc(row.id)}" aria-selected="${state.selected===row.id}" tabindex="0"><td><strong>${esc(row.name||'New service')}</strong><small>${esc(row.description||'Add a client description')}</small></td><td>${esc(row.category||'—')}</td><td>${esc(row.durationMinutes)} min</td><td>${Number(row.price).toLocaleString('en-JM')}</td><td>${Number(row.deposit).toLocaleString('en-JM')}</td><td><input class="booking-toggle" type="checkbox" data-field="isPublished" role="switch" aria-label="Online booking for ${esc(row.name||'new service')}" title="Saves immediately" ${row.isPublished?'checked':''}></td><td><span class="booking-status-dot ${row.isActive?'is-active':''}"></span>${row.isActive?'Active':'Inactive'}</td><td><details class="booking-row-menu"><summary aria-label="Actions for ${esc(row.name||'new service')}">⋯</summary><div><button type="button" class="action" data-booking-details>Details</button><button type="button" class="booking-remove" data-booking-remove>Remove</button></div></details></td></tr>`).join('')||'<tr><td colspan="8" class="booking-empty">No services match the filters.</td></tr>'}</tbody></table>`;
                field('footer').textContent=rows.length+' of '+state.sessions.length+' services · Prices in Jamaican dollars · Only active, published sessions appear on the API Site.';
                if(state.selected&&!rows.some(row=>row.id===state.selected))field('editor').hidden=true;
                else paintDetails();
                field('save').hidden=!field('editor').hidden;
            }else{
                field('save').hidden=true;
                const code=state.hourLocation;
                const locName=locations.find(x=>x[0]===code)?.[1]||code;
                const rows=state.hours.map((row,index)=>({row,index})).filter(x=>x.row.locationCode===code);
                const displayTime=value=>{if(!value)return 'Closed';const m=/^(\d{2}):(\d{2})/.exec(value);if(!m)return value;const h=Number(m[1]);return String(h%12||12).padStart(2,'0')+':'+m[2]+' '+(h<12?'AM':'PM');};
                field('grid').innerHTML=`<div class="booking-hours-location-bar"><label>Location <select data-hours-location>${locations.map(([value,name])=>`<option value="${esc(value)}" ${code===value?'selected':''}>${esc(name)}</option>`).join('')}</select></label><button type="button" class="action" data-add-location>＋ Add location</button></div><div class="booking-hours-table-wrap"><table class="booking-hours-week-table"><thead><tr><th>Day</th><th>Opens</th><th>Closes</th><th>Slot interval</th><th>Status</th></tr></thead><tbody>${days.map((day,index)=>{const entries=rows.filter(x=>Number(x.row.dayOfWeek)===index).sort((a,b)=>a.row.opensAt.localeCompare(b.row.opensAt));const first=entries[0];return `<tr data-hour-day="${index}" tabindex="0" aria-selected="${first?.index===state.selectedHour}"><td><span class="booking-day-check ${first?'is-open':''}">${first?'✓':''}</span><strong>${day}</strong></td><td>${first?displayTime(first.row.opensAt):'Closed'}</td><td>${first?displayTime(first.row.closesAt):'Closed'}</td><td>${first?esc(first.row.slotStepMinutes)+' min':'—'}</td><td><span class="booking-status-dot ${first?'is-active':''}"></span>${first?'Open':'Closed'}${entries.length>1?' · '+entries.length+' windows':''}</td></tr>`}).join('')}</tbody></table></div>`;
                const current=state.hours[state.selectedHour];
                if(!current||current.locationCode!==code)state.selectedHour=rows.find(x=>Number(x.row.dayOfWeek)===(state.hourDay??0))?.index??-1;
                const selectedHour=state.hours[state.selectedHour],editor=field('editor');
                editor.hidden=false;
                const selectedDay=selectedHour?Number(selectedHour.dayOfWeek):(state.hourDay??0);
                const selectedEntries=rows.filter(x=>Number(x.row.dayOfWeek)===selectedDay);
                editor.innerHTML=`<form class="booking-hour-detail-form"><div class="booking-hours-time-fields"><label>Opens *<input type="time" name="opensAt" value="${esc(selectedHour?.opensAt||'09:00')}" required ${selectedHour?'':'disabled'}></label><label>Closes *<input type="time" name="closesAt" value="${esc(selectedHour?.closesAt||'17:00')}" required ${selectedHour?'':'disabled'}></label></div><label>Slot interval *<select name="slotStepMinutes" ${selectedHour?'':'disabled'}>${[5,10,15,20,30,45,60,90,120,180,240].map(n=>`<option value="${n}" ${Number(selectedHour?.slotStepMinutes||30)===n?'selected':''}>${n} minutes</option>`).join('')}</select></label><div class="booking-hour-options"><strong>Opening windows</strong><small>${selectedEntries.length?selectedEntries.length+' window(s) for this day':'This location is closed on this day.'}</small><div class="booking-hour-window-actions"><button type="button" class="action" data-hour-add-window>＋ Add window</button>${selectedHour?'<button type="button" class="action" data-hour-remove>Remove window</button>':''}</div></div><div class="booking-hour-actions"><button type="button" class="action" data-hour-cancel>Cancel</button><button type="submit" class="primary" ${state.dirty.hours?'':'disabled'}>Save changes</button></div></form>`;
                field('grid').querySelector('[data-hours-location]').onchange=event=>{state.hourLocation=event.target.value;state.selectedHour=-1;state.hourDay=0;paintGrid();};
                field('grid').querySelector('[data-add-location]').onclick=async()=>{const name=window.prompt('New location name');if(name===null)return;const trimmed=name.trim();if(trimmed.length<2||trimmed.length>100){status('Enter a location name between 2 and 100 characters.');return;}try{const location=await json('/api/admin/public-booking/locations',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({code:'',name:trimmed})});if(!live())return;locations.push([location.code,location.name]);locations.sort((a,b)=>a[1].localeCompare(b[1]));state.hourLocation=location.code;state.hourDay=0;state.selectedHour=-1;paintGrid();status('Location created: '+location.name+'. Configure its opening hours.');}catch(error){status(error.message||'Could not create location.');}};
                field('grid').querySelectorAll('[data-hour-day]').forEach(tr=>{const choose=()=>{const day=Number(tr.dataset.hourDay);const found=state.hours.findIndex(x=>x.locationCode===state.hourLocation&&Number(x.dayOfWeek)===day);state.selectedHour=found;state.hourDay=day;paintGrid();};tr.onclick=choose;tr.onkeydown=e=>{if(e.key==='Enter'||e.key===' '){e.preventDefault();choose();}};});
                const hourDay=selectedDay;
                if(!selectedHour&&state.hourDay!=null){editor.querySelector('.booking-editor-heading strong').textContent='▦　'+days[hourDay]+' — '+locName;}
                editor.querySelectorAll('input,select').forEach(input=>input.onchange=()=>{if(!selectedHour)return;selectedHour[input.name]=input.value;dirty();paintGrid();});
                editor.querySelector('form').onsubmit=e=>{e.preventDefault();field('save').click();};
                editor.querySelector('[data-hour-add-window]').onclick=()=>{state.hours.push({locationCode:code,dayOfWeek:hourDay,opensAt:'09:00',closesAt:'17:00',slotStepMinutes:30});state.selectedHour=state.hours.length-1;state.hourDay=hourDay;dirty();paintGrid();};
                editor.querySelector('[data-hour-remove]')?.addEventListener('click',()=>{if(!confirm('Remove this opening window?'))return;state.hours.splice(state.selectedHour,1);state.selectedHour=-1;dirty();paintGrid();});
                editor.querySelector('[data-hour-cancel]').onclick=()=>{if(state.dirty.hours){if(!confirm('Discard unsaved opening-hours changes?'))return;void load();}else paintGrid();};
                field('grid').querySelectorAll('[data-hour-day]').forEach(tr=>{tr.setAttribute('aria-selected',String(Number(tr.dataset.hourDay)===hourDay));});
                field('footer').textContent=state.hours.length+' opening windows · America/Jamaica';
            }
        }
        async function load(){
            const turn=++loadRevision;
            status('Loading booking settings…');state.loaded={sessions:false,hours:false};paintGrid();
            const results=await Promise.allSettled([json('/api/admin/public-booking/session-types/'),json('/api/admin/public-booking/hours'),json('/api/admin/public-booking/session-types/workspaces'),json('/api/schedule/resources'),json('/api/admin/public-booking/locations')]);
            if(!live()||turn!==loadRevision)return;
            const [catalog,hours,workspaces,resources,locationResult]=results;
            if(locationResult.status==='fulfilled'&&Array.isArray(locationResult.value)){locations.splice(0,locations.length,...locationResult.value.map(x=>[x.code,x.name]));if(!locations.some(x=>x[0]===state.hourLocation))state.hourLocation=locations[0]?.[0]||'BK';const filter=field('location');if(filter)filter.innerHTML='<option value="">All locations</option>'+locations.map(([code,name])=>`<option value="${esc(code)}">${esc(name)}</option>`).join('');}
            if(catalog.status==='fulfilled'&&Array.isArray(catalog.value.sessionTypes)){
                persisted.clear();for(const row of catalog.value.sessionTypes)persisted.set(row.id,clone(row));
                state.sessions=catalog.value.sessionTypes.map(row=>({...clone(row),price:String(row.priceMinor/100),deposit:String(row.depositMinor/100)}));state.revision=catalog.value.revision;state.loaded.sessions=true;
            }else state.errors.sessions='Session types could not be loaded. Use Reload to retry.';
            if(hours.status==='fulfilled'&&Array.isArray(hours.value)){state.hours=clone(hours.value);state.loaded.hours=true;}else state.errors.hours='Opening hours could not be loaded. Use Reload to retry.';
            state.workspaces=workspaces.status==='fulfilled'&&Array.isArray(workspaces.value)?workspaces.value:[];
            if(resources.status==='fulfilled'&&Array.isArray(resources.value))state.resourceTypes=[...new Set(['Photographer',...resources.value.filter(row=>row.isActive).map(row=>row.resourceType)])];
            state.dirty={sessions:false,hours:false};state.selected=state.sessions[0]?.id||null;state.detailBaseline=null;paintGrid();status(state.errors[state.tab]&&!state.loaded[state.tab]?state.errors[state.tab]:'');
        }
        function sessionPayload(){
            const names=new Set();
            return state.sessions.map(row=>{
                const {price,deposit,...saved}=row;const p=Math.round(Number(price)*100),d=Math.round(Number(deposit)*100),name=row.name.trim();
                if(!name||names.has(name)||name.length>160)throw Error('Use a unique name for every session (up to 160 characters).');names.add(name);
                if(price===''||deposit===''||!Number.isSafeInteger(p)||!Number.isSafeInteger(d)||p<0||d<0||d>p)throw Error(name+': deposit must be between zero and the JMD price.');
                if(!Number.isInteger(Number(row.durationMinutes))||Number(row.durationMinutes)<15||Number(row.durationMinutes)>1440)throw Error(name+': duration must be 15–1440 whole minutes.');
                if((row.category||'').length>80)throw Error(name+': category must be at most 80 characters.');
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
            let payload;try{if(state.tab==='sessions')readDetails();payload=state.tab==='sessions'?sessionPayload():hoursPayload();}catch(error){status(error.message);return;}
            const tab=state.tab;state.saving=true;root.querySelectorAll('button,input,select,textarea').forEach(control=>control.disabled=true);status('Saving…');
            try{
                const result=await json(tab==='sessions'?'/api/admin/public-booking/session-types/':'/api/admin/public-booking/hours',{method:'PUT',headers:{'Content-Type':'application/json','If-Match':String(state.revision)},body:JSON.stringify(payload)});
                if(!live())return;
                if(tab==='sessions'){if(!Array.isArray(result.sessionTypes))throw Error('Unexpected catalog response. Reload to verify the saved state.');state.sessions=result.sessionTypes.map(row=>({...row,price:String(row.priceMinor/100),deposit:String(row.depositMinor/100)}));persisted.clear();for(const row of result.sessionTypes)persisted.set(row.id,clone(row));}
                state.revision=result.revision;state.dirty[tab]=false;state.detailBaseline=null;status(tab==='sessions'?'Session types saved in Studio. API Site publication will retry when enabled.':'Opening hours saved in Studio.');
            }catch(error){status('Save failed: '+error.message);}
            finally{if(live()){state.saving=false;root.querySelectorAll('button,input,select,textarea').forEach(control=>control.disabled=false);paintGrid();}}
        };
        async function publish(row,control){
            const desired=control.checked;
            if(state.saving){control.checked=row.isPublished;return;}
            if(!persisted.has(row.id)){control.checked=row.isPublished;status('Save this new service before changing online booking.');return;}
            try{readDetails();}catch(error){control.checked=row.isPublished;status(error.message);return;}
            state.saving=true;root.querySelectorAll('button,input,select,textarea').forEach(input=>input.disabled=true);
            status('Saving online booking setting...');
            try{
                const result=await json('/api/admin/public-booking/session-types/'+encodeURIComponent(row.id)+'/publication',{
                    method:'PATCH',headers:{'Content-Type':'application/json'},body:JSON.stringify({isPublished:desired,expectedRevision:state.revision})
                });
                if(!live())return;
                if(result.sessionType?.id!==row.id||result.sessionType.isPublished!==desired||!Number.isSafeInteger(result.revision))throw Error('Unexpected response. Reload to check the saved online booking setting.');
                row.isPublished=desired;persisted.set(row.id,clone(result.sessionType));state.revision=result.revision;
                status('Online booking turned '+(desired?'on':'off')+' in Studio. Website sync runs automatically when enabled. Other unsaved edits are preserved.');
            }catch(error){if(live())status('Online booking could not be confirmed: '+error.message+' Reload to verify before retrying.');}
            finally{if(live()){state.saving=false;root.querySelectorAll('button,input,select,textarea').forEach(input=>input.disabled=false);paintGrid();}}
        }
        const selectTab=tab=>{if(state.saving)return false;try{if(state.tab==='sessions')readDetails();}catch(error){status(error.message);return false;}state.tab=tab==='hours'?'hours':'sessions';paintGrid();status(state.dirty[state.tab]?'Unsaved changes':state.loaded[state.tab]?'':state.errors[state.tab]);return true;};
        root.querySelectorAll('[data-booking-tab]').forEach(button=>button.onclick=()=>selectTab(button.dataset.bookingTab));
        field('grid').addEventListener('input',event=>{
            if(!event.target.matches('[data-booking-search]'))return;
            try{readDetails();}catch(error){status(error.message);return;}
            const caret=event.target.selectionStart;state.search=event.target.value;paintGrid();
            const input=field('search');if(input){input.focus();input.setSelectionRange(caret,caret);}
        });
        field('grid').addEventListener('change',event=>{
            const name=event.target.matches('[data-booking-category]')?'category':event.target.matches('[data-booking-location]')?'location':event.target.matches('[data-booking-filter]')?'filter':null;
            if(!name)return;
            try{readDetails();}catch(error){status(error.message);return;}
            state[name]=event.target.value;paintGrid();
        });
        field('reload').onclick=()=>{if((state.dirty.sessions||state.dirty.hours)&&!confirm('Discard unsaved booking settings and reload?'))return;void load();};
        field('add').onclick=()=>{
            try{if(state.tab==='sessions')readDetails();}catch(error){status(error.message);return;}
            if(state.tab==='sessions'){state.search='';state.category='';state.location='';state.filter='';for(const name of ['search','category','location','filter'])field(name).value='';state.sessions.push({id:crypto.randomUUID(),name:'',description:'',durationMinutes:60,setupBufferMinutes:0,cleanupBufferMinutes:0,price:'0',deposit:'0',priceMinor:0,depositMinor:0,currency:'JMD',locations:['BK'],resourceRequirements:{Photographer:1},isActive:true,isPublished:false,defaultWorkspaceId:null,category:''});state.selected=state.sessions.at(-1).id;state.detailBaseline=null;}
            else{state.hours.push({locationCode:state.hourLocation,dayOfWeek:state.hourDay??1,opensAt:'09:00',closesAt:'17:00',slotStepMinutes:30});state.selectedHour=state.hours.length-1;}
            dirty();paintGrid();const row=field('grid').querySelector('tbody tr:last-child');row?.querySelector('input,select')?.focus();row?.scrollIntoView?.({block:'nearest'});
        };
        field('grid').addEventListener('input',event=>{
            const control=event.target,row=control.closest('tr');if(!row)return;
            const item=row.hasAttribute('data-booking-session')?state.sessions.find(item=>item.id===row.dataset.bookingSession):state.hours[Number(row.dataset.bookingHour)];if(!item)return;
            if(control.dataset.field==='isPublished'){void publish(item,control);return;}
            if(control.dataset.location){item.locations=[...row.querySelectorAll('[data-location]:checked')].map(input=>input.dataset.location);row.querySelector('.booking-location-picker > summary').innerHTML=locationChips(item);}
            else if(control.dataset.field)item[control.dataset.field]=control.type==='checkbox'?control.checked:control.value;
            else return;if(control.dataset.field==='isActive')control.nextElementSibling.textContent=item.isActive?'Active':'Inactive';dirty();
        });
        field('grid').addEventListener('keydown',event=>{if((event.key==='Enter'||event.key===' ')&&event.target.matches('tr[data-booking-session]')){event.preventDefault();event.target.click();}});
        field('grid').addEventListener('click',event=>{
            if(state.saving)return;
            const row=event.target.closest('tr');if(!row)return;
            if(event.target.closest('[data-booking-remove]')){
                if(!confirm('Remove this '+(state.tab==='sessions'?'session type':'opening window')+'? The removal takes effect when you save changes.'))return;
                if(state.tab==='sessions')state.sessions=state.sessions.filter(item=>item.id!==row.dataset.bookingSession);else state.hours.splice(Number(row.dataset.bookingHour),1);dirty();paintGrid();
            }else if(state.tab==='sessions'){
                try{readDetails();}catch(error){status(error.message);return;}
                if(state.selected!==row.dataset.bookingSession){state.selected=row.dataset.bookingSession;state.detailBaseline=null;}
                field('grid').querySelectorAll('[data-booking-session]').forEach(item=>item.setAttribute('aria-selected',String(item.dataset.bookingSession===state.selected)));
                if(!event.target.closest('input,button,details,summary'))paintDetails();
            }
        });
        const advanced=row=>({name:row.name,category:row.category,durationMinutes:row.durationMinutes,price:row.price,deposit:row.deposit,locations:clone(row.locations||[]),isActive:row.isActive,isPublished:row.isPublished,description:row.description,setupBufferMinutes:row.setupBufferMinutes,cleanupBufferMinutes:row.cleanupBufferMinutes,defaultWorkspaceId:row.defaultWorkspaceId,resourceRequirements:clone(row.resourceRequirements||{})});
        function paintDetails(){
            const row=state.sessions.find(item=>item.id===state.selected),editor=field('editor');
            editor.hidden=!row;if(!row)return;
            state.detailBaseline??=advanced(row);
            editor.innerHTML=`<form class="booking-detail-form"><div class="booking-detail-scroll"><section class="booking-detail-basic"><div class="booking-editor-basics"><label class="booking-name-field">Service name<input name="name" maxlength="160" required value="${esc(row.name)}"></label><label class="booking-category-field">Category<input name="category" maxlength="80" value="${esc(row.category||'')}"></label><label class="booking-status-editor">Status<select name="isActive" aria-label="Service status"><option value="true" ${row.isActive?'selected':''}>Active</option><option value="false" ${!row.isActive?'selected':''}>Inactive</option></select></label><label class="booking-online-indicator"><input class="booking-toggle" type="checkbox" role="switch" aria-label="Allow online booking" data-detail-published ${row.isPublished?'checked':''}><span>Online booking</span></label></div></section><div class="booking-detail-content"><div class="booking-detail-description"><label>Client description<textarea name="description" rows="2" maxlength="1000">${esc(row.description)}</textarea></label><section class="booking-detail-section booking-section-resources"><div class="booking-resources"><label class="booking-section-label">Required resources</label><datalist id="bookingResourceTypes">${state.resourceTypes.map(type=>`<option value="${esc(type)}">`).join('')}</datalist><div data-resource-rows></div><button type="button" class="action" data-resource-add>＋ Add requirement</button></div></section></div><div class="booking-detail-flat-grid"><section class="booking-detail-section booking-section-timing"><label>Setup (min)<input name="setup" aria-label="Setup minutes" type="number" min="0" max="480" step="1" value="${row.setupBufferMinutes}" required></label><label>Cleanup (min)<input name="cleanup" aria-label="Cleanup minutes" type="number" min="0" max="480" step="1" value="${row.cleanupBufferMinutes}" required></label><label>Duration (min)<input name="durationMinutes" type="number" min="15" max="1440" step="1" required value="${esc(row.durationMinutes)}"></label></section><section class="booking-detail-section booking-section-locations"><div class="booking-location-token-editor"><div class="booking-location-tokens" data-location-tokens></div><select data-location-add aria-label="Add service location"><option value="">＋ Add location…</option>${locations.filter(([code])=>!row.locations.includes(code)).map(([code,label])=>`<option value="${esc(code)}">${esc(label)}</option>`).join('')}</select></div></section><section class="booking-detail-section booking-section-pricing"><label>Price (JMD)<input name="price" type="number" min="0" step=".01" required value="${esc(row.price)}"></label><label>Deposit (JMD)<input name="deposit" type="number" min="0" step=".01" required value="${esc(row.deposit)}"></label></section><section class="booking-detail-section booking-section-routing"><label>Routing workspace<select name="workspace"><option value="">Unassigned — Front Desk review</option>${state.workspaces.map(item=>`<option value="${esc(item.id)}" ${row.defaultWorkspaceId===item.id?'selected':''}>${esc(item.name)}</option>`).join('')}${row.defaultWorkspaceId&&!state.workspaces.some(item=>item.id===row.defaultWorkspaceId)?`<option value="${esc(row.defaultWorkspaceId)}" selected>Current workspace (details unavailable)</option>`:''}</select></label></section></div></div></div><footer><small>${row.locations.length?'Locations: '+row.locations.map(code=>locations.find(x=>x[0]===code)?.[1]||code).join(' and '):'Choose a location.'} · ${row.isActive&&row.isPublished?'Enabled for online booking':'Not currently bookable online'}. Saves include all session grid edits.</small><button type="button" class="action" data-details-cancel>Cancel</button><button type="submit" class="primary" ${state.saving||!state.dirty.sessions?'disabled':''}>Save changes</button><p role="alert"></p></footer></form>`;
            const form=editor.querySelector('form'),rows=form.querySelector('[data-resource-rows]');
            const selectedLocations=new Set(row.locations||[]);
            const tokens=form.querySelector('[data-location-tokens]'),picker=form.querySelector('[data-location-add]');
            const paintLocations=()=>{
                tokens.innerHTML=[...selectedLocations].map(code=>`<span class="booking-location-token">${esc(locations.find(item=>item[0]===code)?.[1]||code)}<button type="button" data-remove-location="${esc(code)}" aria-label="Remove location">×</button></span>`).join('');
                picker.innerHTML='<option value="">＋ Add location…</option>'+locations.filter(([code])=>!selectedLocations.has(code)).map(([code,label])=>`<option value="${esc(code)}">${esc(label)}</option>`).join('');
            };
            paintLocations();
            picker.onchange=()=>{if(picker.value){selectedLocations.add(picker.value);paintLocations();detailsChanged();}};
            tokens.onclick=event=>{const button=event.target.closest('[data-remove-location]');if(!button)return;selectedLocations.delete(button.dataset.removeLocation);paintLocations();detailsChanged();};
            const add=(type='',count=1)=>{const wrap=document.createElement('div');wrap.className='booking-resource-row';wrap.innerHTML=`<label>Resource type<input data-resource-type list="bookingResourceTypes" value="${esc(type)}" maxlength="80" required></label><label>Count<input data-resource-count type="number" min="0" max="20" value="${count}" required></label><button type="button" class="action">Remove</button>`;wrap.querySelector('button').onclick=()=>{wrap.remove();detailsChanged();};rows.append(wrap);};
            Object.entries(row.resourceRequirements||{}).forEach(([type,count])=>add(type,count));
            form.querySelector('[data-resource-add]').onclick=()=>{add();dirty();};
            function detailsChanged(){try{readDetails();form.querySelector('[role=alert]').textContent='';}catch(error){form.querySelector('[role=alert]').textContent=error.message;}dirty();form.querySelector('[type=submit]').disabled=false;}
            form.addEventListener('input',detailsChanged);form.addEventListener('change',detailsChanged);
            form.onsubmit=event=>{event.preventDefault();field('save').click();};
            form.querySelector('[data-details-cancel]').onclick=()=>{Object.assign(row,clone(state.detailBaseline));state.detailBaseline=null;dirty();paintGrid();};
        }
        function readDetails(){
            const editor=field('editor');if(editor.hidden)return;
            const row=state.sessions.find(item=>item.id===state.selected),form=editor.querySelector('form');if(!row||!form)return;
            const rules={};for(const wrap of form.querySelector('[data-resource-rows]').children){const type=wrap.querySelector('[data-resource-type]').value.trim(),count=Number(wrap.querySelector('[data-resource-count]').value);if(wrap.querySelector('[data-resource-count]').value===''||!/^[A-Za-z0-9._-]{1,80}$/.test(type)||Object.hasOwn(rules,type)||!Number.isInteger(count)||count<0||count>20)throw Error('Use unique resource types and whole-number counts from 0 to 20.');Object.defineProperty(rules,type,{value:count,enumerable:true,writable:true});}
            const setup=Number(form.elements.setup.value),cleanup=Number(form.elements.cleanup.value);
            if(form.elements.setup.value===''||form.elements.cleanup.value===''||!Number.isInteger(setup)||!Number.isInteger(cleanup)||setup<0||cleanup<0||setup>480||cleanup>480)throw Error('Buffers must be whole minutes from 0 to 480.');
            const name=form.elements.name.value.trim(),duration=Number(form.elements.durationMinutes.value),price=form.elements.price.value,deposit=form.elements.deposit.value;if(!name||!Number.isInteger(duration)||duration<15||duration>1440||price===''||deposit==='')throw Error('Enter a service name, valid duration, price and deposit.');const selectedLocations=[...form.querySelectorAll('[data-location-tokens] [data-remove-location]')].map(button=>button.dataset.removeLocation);if(!selectedLocations.length)throw Error('Choose at least one location.');Object.assign(row,{name,category:form.elements.category.value.trim(),durationMinutes:duration,price,deposit,locations:selectedLocations,description:form.elements.description.value,setupBufferMinutes:setup,cleanupBufferMinutes:cleanup,defaultWorkspaceId:form.elements.workspace.value||null,resourceRequirements:rules,isActive:form.elements.isActive.value==='true',isPublished:form.querySelector("[data-detail-published]").checked});
        }
        await load();
        return {selectTab,isSaving:()=>state.saving,add:()=>{if(!state.saving)field('add').click();},reload:()=>field('reload').click()};
    }
    return {html,mount};
})();
