const studioPeople = new Map();
function studioRememberPeople(people) {
    for (const person of people || []) {
        if (person?.userName) studioPeople.set(String(person.userName).toLowerCase(), person);
    }
}
function studioFirstName(value, people = []) {
    const person = typeof value === 'object' && value !== null ? value :
        people.find(p => String(p.userName || '').toLowerCase() === String(value || '').toLowerCase()) || studioPeople.get(String(value || '').toLowerCase());
    const login = String(person?.userName || (typeof value === 'string' ? value : '')).trim();
    const given = String(person?.firstName || person?.givenName || '').trim();
    const preferred = String(person?.displayName || '').trim();
    const name = given || (preferred && preferred.toLowerCase() !== login.toLowerCase() ? preferred : '');
    return name ? name.split(/\s+/)[0] : (!login.includes('@') && /\s/.test(login) ? login.split(/\s+/)[0] : login);
}
async function loadSignedInUser() {
    const response = await fetch('/api/auth/me');
    if (response.status === 401) {
        window.location.replace('/login');
        return;
    }

    if (!response.ok) {
        return;
    }

    const payload = await response.json();
    const user = payload.user || payload;
    studioRememberPeople([user]);
    const signedInUser = document.getElementById('signedInUser');
    const userAvatar = document.getElementById('userAvatar');
    const signOutButton = document.getElementById('signOutButton');
    const adminMenuButton = document.getElementById('adminMenuButton');

    if (signedInUser) {
        signedInUser.textContent = studioFirstName(user);
        signedInUser.title = [user.title, user.department].filter(Boolean).join(' · ');
    }

    if (userAvatar) {
        const source = studioFirstName(user).trim();
        const parts = source.split(/\s+/).filter(Boolean);
        userAvatar.textContent = parts.length > 1
            ? (parts[0][0] + parts[parts.length - 1][0]).toUpperCase()
            : source.slice(0, 2).toUpperCase();
    }

    const profilePhoto = document.getElementById('studioProfilePhoto');
    const profilePlaceholder = document.getElementById('studioProfilePlaceholder');
    if (profilePhoto && profilePlaceholder) {
        const photoUrl = [user.photoUrl, user.profilePhotoUrl, user.avatarUrl, user.pictureUrl, user.photoUri]
            .find(value => typeof value === 'string' && (/^https:\/\//i.test(value) || /^\/[^/]/.test(value)));
        const showPlaceholder = () => {
            profilePhoto.hidden = true;
            profilePhoto.removeAttribute('src');
            profilePlaceholder.hidden = false;
        };
        profilePhoto.onerror = showPlaceholder;
        if (photoUrl) {
            profilePhoto.onload = () => {
                profilePhoto.hidden = false;
                profilePlaceholder.hidden = true;
            };
            profilePhoto.src = photoUrl;
        } else showPlaceholder();
    }

    if (adminMenuButton) adminMenuButton.hidden = user.isAdministrator !== true;

    if (signOutButton) {
        signOutButton.onclick = async () => {
            await fetch('/api/auth/logout', { method: 'POST' });
            window.location.replace('/login');
        };
    }
}

loadSignedInUser();
