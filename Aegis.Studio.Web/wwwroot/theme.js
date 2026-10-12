(() => {
    const themes = [
        ['standard', 'Standard'],
        ['light', 'Light'],
        ['dark', 'Dark']
    ];

    const storageKey = 'aegis.studio.theme';

    function normalizeTheme(themeName) {
        const value = String(themeName || '').trim().toLowerCase();
        if (value === 'light' || value === 'office-light') return 'light';
        if (value === 'dark' || value === 'fluent-dark') return 'dark';
        return 'standard';
    }

    function effectiveTheme(themeName) {
        const normalized = normalizeTheme(themeName);
        if (normalized === 'light') return 'office-light';
        if (normalized === 'dark') return 'fluent-dark';
        return window.matchMedia?.('(prefers-color-scheme: dark)').matches ? 'fluent-dark' : 'studio-default';
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
        applyTheme(themeName);
        try {
            const response = await fetch('/api/profile/theme', {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ themeName })
            });
            if (!response.ok) throw new Error(`HTTP ${response.status}`);
            const profile = await response.json();
            applyTheme(profile.themeName || themeName);
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
            applyTheme(profile.themeName || localStorage.getItem(storageKey) || 'standard');
        } catch {
            applyTheme(localStorage.getItem(storageKey) || 'standard');
        }
    }

    document.addEventListener('DOMContentLoaded', async () => {
        applyTheme(localStorage.getItem(storageKey) || 'standard');
        const systemTheme = window.matchMedia?.('(prefers-color-scheme: dark)');
        systemTheme?.addEventListener?.('change', () => {
            if (normalizeTheme(localStorage.getItem(storageKey)) === 'standard') applyTheme('standard');
        });
        wireSelectors();
        await loadProfileTheme();

        // Theme selection persists immediately; the profile form saves other preferences.
    });
})();
