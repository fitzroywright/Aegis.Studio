(() => {
    const preferences = [
        ['system', 'System'],
        ['light', 'Light'],
        ['dark', 'Dark']
    ];
    const storageKey = 'aegis.studio.theme';
    const media = matchMedia('(prefers-color-scheme: dark)');

    function normalizePreference(value) {
        const theme=String(value||'').trim().toLowerCase();
        if(theme==='office-light') return 'light';
        if(theme==='fluent-dark') return 'dark';
        if(['studio-default','mid-century','steel','sakura','ocean'].includes(theme)) return 'system';
        return preferences.some(([key])=>key===theme) ? theme : 'system';
    }

    function resolvedTheme(preference) {
        const value=normalizePreference(preference);
        if(value==='dark') return 'fluent-dark';
        if(value==='light') return 'office-light';
        return media.matches ? 'fluent-dark' : 'office-light';
    }

    function applyTheme(preference, persistLocal=true) {
        const valid=normalizePreference(preference);
        document.body.dataset.themePreference=valid;
        document.body.dataset.theme=resolvedTheme(valid);
        if(persistLocal)localStorage.setItem(storageKey,valid);
        const quick=document.getElementById('themePicker');
        const profile=document.getElementById('prefTheme');
        if(quick)quick.value=valid;
        if(profile)profile.value=valid;
    }

    function optionsHtml() {
        return preferences.map(([value,label])=>`<option value="${value}">${label}</option>`).join('');
    }

    async function persistTheme(preference) {
        const valid=normalizePreference(preference);
        applyTheme(valid);
        try {
            const response=await fetch('/api/profile/theme',{
                method:'PUT',
                headers:{'Content-Type':'application/json'},
                body:JSON.stringify({themeName:valid})
            });
            if(!response.ok)throw new Error(`HTTP ${response.status}`);
            const profile=await response.json();
            applyTheme(profile.themeName||valid);
        } catch(error) {
            console.warn('Theme was applied locally but could not be saved to the profile.',error);
        }
    }

    function wireSelectors() {
        const quick=document.getElementById('themePicker');
        const profile=document.getElementById('prefTheme');
        if(quick){quick.innerHTML=optionsHtml();quick.addEventListener('change',()=>persistTheme(quick.value));}
        if(profile){profile.innerHTML=optionsHtml();profile.addEventListener('change',()=>applyTheme(profile.value));}
    }

    async function loadProfileTheme() {
        const local=normalizePreference(localStorage.getItem(storageKey));
        try {
            const response=await fetch('/api/profile');
            if(!response.ok){applyTheme(local);return;}
            const profile=await response.json();
            applyTheme(profile.themeName||local);
        } catch {
            applyTheme(local);
        }
    }

    const onSystemThemeChanged=()=>{
        if(normalizePreference(document.body.dataset.themePreference)==='system')applyTheme('system',false);
    };
    media.addEventListener?.('change',onSystemThemeChanged);

    document.addEventListener('DOMContentLoaded',async()=>{
        applyTheme(localStorage.getItem(storageKey)||'system');
        wireSelectors();
        await loadProfileTheme();
        const profileForm=document.getElementById('profileForm');
        if(profileForm)profileForm.addEventListener('submit',()=>{
            const profile=document.getElementById('prefTheme');
            if(profile)persistTheme(profile.value);
        });
    });
})();