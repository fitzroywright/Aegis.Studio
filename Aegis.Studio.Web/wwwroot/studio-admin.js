(() => {
    const entries = [
        {view:'catalog',icon:'i-pos',label:'Products and Services'},
        {view:'templates',icon:'i-jobs',label:'Templates'},
        {view:'staff-roles',icon:'i-people',label:'Staff and Roles'},
        {view:'workspace-management',icon:'i-grid',label:'Workspaces',staff:true},
        {view:'about',icon:'i-info',label:'About',staff:true}
    ];
    function mountMenu(host){
        const menu=document.createElement('details');
        menu.className='studio-admin-menu';
        menu.innerHTML='<summary aria-label="Administration menu"><svg class="studio-navigation-icon" aria-hidden="true"><use href="#i-settings"></use></svg>Admin <span aria-hidden="true">⌄</span></summary><div class="studio-admin-popover" aria-label="Administration"></div>';
        const body=menu.querySelector('div');
        for(const entry of entries){
            const button=document.createElement('button');button.type='button';button.className='studio-admin-item';const icon=document.createElementNS('http://www.w3.org/2000/svg','svg');icon.setAttribute('class','studio-navigation-icon');icon.setAttribute('viewBox','0 0 24 24');icon.setAttribute('fill','none');icon.setAttribute('stroke','currentColor');icon.setAttribute('stroke-width','1.6');icon.setAttribute('stroke-linecap','round');icon.setAttribute('stroke-linejoin','round');icon.setAttribute('aria-hidden','true');icon.setAttribute('focusable','false');const use=document.createElementNS('http://www.w3.org/2000/svg','use');use.setAttribute('href','#'+entry.icon);icon.append(use);const label=document.createElement('span');label.textContent=entry.label;button.append(icon,label);button.dataset.studioAdminView=entry.view;button.hidden=!entry.staff;
            button.onclick=()=>{menu.open=false;window.studioNavigate?.(entry.view);};body.append(button);
        }
        const authorize=payload=>{const admin=(payload?.user||payload)?.isAdministrator===true;for(const button of body.children)button.hidden=window.StudioAccess?.pending()?button.dataset.studioAdminView!=='about':!admin&&!entries.find(entry=>entry.view===button.dataset.studioAdminView).staff;};
        Promise.resolve(window.studioSignedInUserReady||window.json?.('/api/auth/me')).then(authorize).catch(()=>{});
        menu.addEventListener('keydown',event=>{
            if(event.key==='Escape'){menu.open=false;menu.querySelector('summary').focus();}
            if(event.key==='ArrowDown'||event.key==='ArrowUp'){
                event.preventDefault();menu.open=true;const buttons=[...body.children].filter(button=>!button.hidden),index=buttons.indexOf(document.activeElement),direction=event.key==='ArrowDown'?1:-1;
                buttons[(index+direction+buttons.length)%buttons.length]?.focus();
            }
        });
        document.addEventListener('click',event=>{if(!menu.contains(event.target))menu.open=false;});
        window.addEventListener('studio-view-changed',()=>{menu.open=false;});
        host.append(menu);
    }
    const scopes=['intake:pull','intake:ack','gallery:access:pull','gallery:access:ack','booking:catalog:publish','booking:availability:publish','gallery:publish','gallery:selections:pull','gallery:selections:ack','client:link'];
    function keyForm(){return `<section class="admin-card"><h2>API Site key</h2><p>Generate a credential for Studio’s importer and publisher on the API Site. Keep it in the server deployment secret stores.</p><p>This generates setup values only. It does not install, activate or replace a key on either service.</p><div class="admin-form-stack"><label>Integration ID<input data-api-key-id value="studio" maxlength="100" autocomplete="off"></label><button type="button" class="primary" data-api-key-generate>Generate key</button><p data-api-key-status role="status"></p><div data-api-key-result hidden><label>API Site credential — add to PUBLIC_API_KEYS_JSON<textarea data-api-key-credential readonly rows="8" spellcheck="false" autocomplete="off"></textarea></label><button type="button" class="action" data-api-key-copy="credential">Copy API Site credential</button><label>Studio deployment setting<textarea data-api-key-setting readonly rows="2" spellcheck="false" autocomplete="off"></textarea></label><button type="button" class="action" data-api-key-copy="setting">Copy Studio setting</button><p>Add the credential to the API Site’s existing PUBLIC_API_KEYS_JSON array, preserving other integrations. Set PublicSite__IntegrationToken on Studio to the same secret. Reload the services and verify synchronization before removing an old credential. Preserve the integration ID when rotating.</p></div></div></section>`;}
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


