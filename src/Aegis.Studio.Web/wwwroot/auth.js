async function loadSignedInUser() {
    const response = await fetch('/api/auth/me');
    if (response.status === 401) {
        window.location.replace('/login');
        return;
    }

    if (!response.ok) {
        return;
    }

    const user = await response.json();
    const signedInUser = document.getElementById('signedInUser');
    const userAvatar = document.getElementById('userAvatar');
    const signOutButton = document.getElementById('signOutButton');

    if (signedInUser) {
        signedInUser.textContent = user.displayName || user.userName;
        signedInUser.title = [user.title, user.department].filter(Boolean).join(' · ');
    }

    if (userAvatar) {
        const source = (user.displayName || user.userName || '').trim();
        const parts = source.split(/\s+/).filter(Boolean);
        userAvatar.textContent = parts.length > 1
            ? (parts[0][0] + parts[parts.length - 1][0]).toUpperCase()
            : source.slice(0, 2).toUpperCase();
    }

    if (signOutButton) {
        signOutButton.onclick = async () => {
            await fetch('/api/auth/logout', { method: 'POST' });
            window.location.replace('/login');
        };
    }
}

loadSignedInUser();
