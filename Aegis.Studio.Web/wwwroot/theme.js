(() => {
    const themes = [
        ['studio-default', 'Studio Default'],
        ['office-light', 'Office Light'],
        ['fluent-dark', 'Fluent Dark'],
        ['mid-century', 'Mid Century'],
        ['steel', 'Steel'],
        ['sakura', 'Sakura'],
        ['ocean', 'Ocean']
    ];

    const storageKey = 'aegis.studio.theme';

    function applyTheme(themeName) {
        const valid = themes.some(([value]) => value === themeName) ? themeName : 'studio-default';
        document.body.dataset.theme = valid;
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
            profile.addEventListener('change', () => applyTheme(profile.value));
        }
    }

    async function loadProfileTheme() {
        try {
            const response = await fetch('/api/profile');
            if (!response.ok) return;
            const profile = await response.json();
            applyTheme(profile.themeName || localStorage.getItem(storageKey) || 'studio-default');
        } catch {
            applyTheme(localStorage.getItem(storageKey) || 'studio-default');
        }
    }

    document.addEventListener('DOMContentLoaded', async () => {
        applyTheme(localStorage.getItem(storageKey) || 'studio-default');
        wireSelectors();
        await loadProfileTheme();

        const profileForm = document.getElementById('profileForm');
        if (profileForm) {
            profileForm.addEventListener('submit', () => {
                const profile = document.getElementById('prefTheme');
                if (profile) persistTheme(profile.value);
            });
        }
    });
})();
