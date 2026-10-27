(() => {
    const themes = [
        ['standard', 'Standard'],
        ['comfort', 'Comfort'],
        ['light', 'Light'],
        ['dark', 'Dark']
    ];

    const storageKey = 'aegis.studio.theme';
    let userChangedTheme = false;

    function normalizeTheme(themeName) {
        const value = String(themeName || '').trim().toLowerCase();
        if (value === 'comfort') return 'comfort';
        if (value === 'light' || value === 'office-light') return 'light';
        if (value === 'dark' || value === 'fluent-dark') return 'dark';
        return 'standard';
    }

    function effectiveTheme(themeName) {
        const normalized = normalizeTheme(themeName);
        if (normalized === 'comfort') return 'studio-comfort';
        if (normalized === 'light') return 'office-light';
        if (normalized === 'dark') return 'fluent-dark';
        return 'studio-default';
    }

    function applyTheme(themeName) {
        const valid = normalizeTheme(themeName);
        document.body.dataset.theme = effectiveTheme(valid);
        localStorage.setItem(storageKey, valid);
        const quick = document.getElementById('themePicker');
        const profile = document.getElementById('prefTheme');
        if (quick) quick.value = valid;
        if (profile) profile.value = valid;
    }

    function optionsHtml() {
        return themes.map(([value, label]) => `<option value="${value}">${label}</option>`).join('');
    }

    async function persistTheme(themeName) {
        userChangedTheme = true;
        applyTheme(themeName);
        try {
            const response = await fetch('/api/profile/theme', {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ themeName })
            });
            if (!response.ok) throw new Error(`HTTP ${response.status}`);
            const profile = await response.json();
            if (normalizeTheme(localStorage.getItem(storageKey)) === normalizeTheme(themeName)) applyTheme(profile.themeName || themeName);
        } catch (error) {
            console.warn('Theme was applied locally but could not be saved to the profile.', error);
        }
    }

    function wireSelectors() {
        const quick = document.getElementById('themePicker');
        const profile = document.getElementById('prefTheme');
        if (quick) {
            quick.innerHTML = optionsHtml();
            quick.addEventListener('change', () => persistTheme(quick.value));
        }
        if (profile) {
            profile.innerHTML = optionsHtml();
            profile.addEventListener('change', () => persistTheme(profile.value));
        }
    }

    async function loadProfileTheme() {
        try {
            const response = await fetch('/api/profile');
            if (!response.ok) return;
            const profile = await response.json();
            if (!userChangedTheme) applyTheme(profile.themeName || localStorage.getItem(storageKey) || 'standard');
        } catch {
            if (!userChangedTheme) applyTheme(localStorage.getItem(storageKey) || 'standard');
        }
    }

    document.addEventListener('DOMContentLoaded', async () => {
        applyTheme(localStorage.getItem(storageKey) || 'standard');
        wireSelectors();
        await loadProfileTheme();

        // Theme selection persists immediately; the profile form saves other preferences.
    });
})();

