(() => {
    const entries = [
        {view:'opening-hours',icon:'i-calendar',label:'Opening Hours'},
        {view:'staff-roles',icon:'i-people',label:'Staff and Roles'},
        {view:'catalog',icon:'i-pos',label:'Products and Services'},
        {view:'workspace-management',icon:'i-grid',label:'Workspaces',staff:true},
        {view:'templates',icon:'i-jobs',label:'Job Templates',parent:'templates',kind:'Job'},
        {view:'work-order-templates',icon:'i-jobs',label:'Work Order Templates',parent:'templates'},
        {view:'session-plan-templates',icon:'i-calendar',label:'Session Plan Templates',parent:'templates'},
        {view:'workflows-admin',icon:'i-grid',label:'Workflow Automation'},
        {view:'about',icon:'i-info',label:'About',staff:true,separatorBefore:true}
    ];
    function mountMenu(host){
        const menu=document.createElement('details');
        menu.className='studio-admin-menu';
        menu.innerHTML='<summary aria-label="Administration menu"><svg class="studio-navigation-icon" aria-hidden="true"><use href="#i-settings"></use></svg>Admin <span aria-hidden="true">⌄</span></summary><div class="studio-admin-popover" aria-label="Administration"></div>';
        const body=menu.querySelector('div');
        const templates=document.createElement('details');templates.className='studio-admin-submenu';templates.hidden=true;
        templates.innerHTML='<summary class="studio-admin-item"><svg class="studio-navigation-icon" aria-hidden="true"><use href="#i-jobs"></use></svg><span>Templates</span><span class="studio-admin-chevron" aria-hidden="true">⌄</span></summary><div aria-label="Template types"></div>';
        const templateItems=templates.querySelector('div');
        const separator=document.createElement('hr');separator.className='studio-admin-separator';separator.hidden=true;
        const buttons=new Map();
        const close=()=>{templates.open=false;menu.open=false;};
        for(const entry of entries){
            if(entry.separatorBefore)body.append(separator);
            const button=document.createElement('button');button.type='button';button.className='studio-admin-item';const icon=document.createElementNS('http://www.w3.org/2000/svg','svg');icon.setAttribute('class','studio-navigation-icon');icon.setAttribute('viewBox','0 0 24 24');icon.setAttribute('fill','none');icon.setAttribute('stroke','currentColor');icon.setAttribute('stroke-width','1.6');icon.setAttribute('stroke-linecap','round');icon.setAttribute('stroke-linejoin','round');icon.setAttribute('aria-hidden','true');icon.setAttribute('focusable','false');const use=document.createElementNS('http://www.w3.org/2000/svg','use');use.setAttribute('href','#'+entry.icon);icon.append(use);const label=document.createElement('span');label.textContent=entry.label;button.append(icon,label);button.dataset.studioAdminView=entry.view;button.hidden=!entry.staff;
            button.onclick=()=>{close();window.studioNavigate?.(entry.view,entry.kind?{kind:entry.kind}:{});};
            if(entry.parent==='templates'){if(!templates.parentElement)body.append(templates);templateItems.append(button);}else body.append(button);
            buttons.set(entry.view,button);
        }
        const authorize=payload=>{
            const admin=(payload?.user||payload)?.isAdministrator===true,pending=window.StudioAccess?.pending();
            for(const entry of entries)buttons.get(entry.view).hidden=pending?entry.view!=='about':!admin&&!entry.staff;
            templates.hidden=!entries.some(entry=>entry.parent==='templates'&&!buttons.get(entry.view).hidden);
            separator.hidden=buttons.get('about').hidden||!entries.some(entry=>entry.view!=='about'&&!buttons.get(entry.view).hidden);
            if(templates.hidden)templates.open=false;
        };
        Promise.resolve(window.studioSignedInUserReady||window.json?.('/api/auth/me')).then(authorize).catch(()=>{});
        menu.addEventListener('keydown',event=>{
            const inside=templates.contains(document.activeElement),summary=templates.querySelector('summary');
            if(event.key==='Escape'){event.preventDefault();if(inside&&templates.open){templates.open=false;summary.focus();}else{close();menu.querySelector('summary').focus();}}
            if(event.key==='ArrowRight'&&document.activeElement===summary){event.preventDefault();templates.open=true;templateItems.querySelector('button:not([hidden])')?.focus();}
            if(event.key==='ArrowLeft'&&inside){event.preventDefault();templates.open=false;summary.focus();}
            if(event.key==='ArrowDown'||event.key==='ArrowUp'){
                event.preventDefault();menu.open=true;
                const visible=[...body.querySelectorAll('button,summary')].filter(el=>!el.hidden&&!el.closest('[hidden]')&&(!templateItems.contains(el)||templates.open));
                const index=visible.indexOf(document.activeElement),direction=event.key==='ArrowDown'?1:-1;
                visible[index<0?(direction>0?0:visible.length-1):(index+direction+visible.length)%visible.length]?.focus();
            }
        });
        menu.addEventListener('toggle',()=>{if(!menu.open)templates.open=false;});
        document.addEventListener('click',event=>{if(!menu.contains(event.target))close();});
        window.addEventListener('studio-view-changed',close);
        host.append(menu);
    }
    const scopes=['intake:pull','intake:ack','gallery:access:pull','gallery:access:ack','booking:catalog:publish','booking:availability:publish','gallery:publish','gallery:selections:pull','gallery:selections:ack','client:link'];
    function keyForm(){return `<section class="api-key-form"><p class="api-key-purpose"><strong>What this is for:</strong> a server-to-server credential that lets the API Site securely exchange bookings, uploads and gallery data with Studio.</p><p class="api-key-how"><strong>How it is used:</strong> copy the generated values into the API Site’s <code>PUBLIC_API_KEYS_JSON</code> and Studio’s <code>PublicSite__IntegrationToken</code>. It is sent only in HTTPS requests—never browser code.</p><label>Integration ID<input data-api-key-id value="studio" maxlength="100" autocomplete="off"></label><button type="button" class="primary" data-api-key-generate>Generate key</button><p data-api-key-status role="status"></p><div data-api-key-result hidden><label>API Site credential<textarea data-api-key-credential readonly rows="4" spellcheck="false" autocomplete="off"></textarea></label><div class="api-key-actions"><button type="button" class="action" data-api-key-copy="credential">Copy API Site credential</button><button type="button" class="action" data-api-key-copy="setting">Copy Studio setting</button></div><label>Studio deployment setting<textarea data-api-key-setting readonly rows="1" spellcheck="false" autocomplete="off"></textarea></label><p class="api-key-warning">The secret is shown once and is not saved in Studio. Store it in both services, reload them, and keep separate development and production keys.</p></div></section>`;}
    function bindKeyForm(root){
        const field=name=>root.querySelector('[data-api-key-'+name+']'),status=field('status');
        field('generate').onclick=()=>{
            const id=field('id').value.trim();if(!/^[a-zA-Z0-9_-]{1,100}$/.test(id)){status.textContent='Use an ID with letters, numbers, hyphens or underscores.';return;}
            if(!window.crypto?.getRandomValues){status.textContent='Secure key generation is unavailable in this browser.';return;}
            const bytes=new Uint8Array(32);window.crypto.getRandomValues(bytes);const secret=Array.from(bytes,byte=>byte.toString(16).padStart(2,'0')).join('');bytes.fill(0);
            field('credential').value=JSON.stringify({id,secret,scopes},null,2);field('setting').value='PublicSite__IntegrationToken='+secret;field('result').hidden=false;status.textContent='Generated. Copy these values now; they are not saved in Studio. Provision both services to activate the key.';
        };
        root.querySelectorAll('[data-api-key-copy]').forEach(button=>button.onclick=async()=>{try{await navigator.clipboard.writeText(field(button.dataset.apiKeyCopy).value);status.textContent='Copied. Store this value securely in the deployment configuration.';}catch{status.textContent='Copy unavailable. Select and copy the value from the field.';}});
    }
    window.StudioAdmin={entries,mountMenu,keyForm,bindKeyForm};
})();


