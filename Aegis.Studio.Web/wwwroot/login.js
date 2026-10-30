const loginForm = document.getElementById('loginForm');
const userName = document.getElementById('userName');
const password = document.getElementById('password');
const signInButton = document.getElementById('signInButton');
const error = document.getElementById('error');
const source = document.getElementById('authenticationSource');
const recoveryForm = document.getElementById('recoveryForm');
let authenticationOptions = null;
const sourceLabels = { ActiveDirectory: 'Active Directory', LocalDatabase: 'Local account', Mock: 'Isolated demonstration' };

function updateSignInMethod() {
    const mock = source.value === 'Mock';
    password.required = !mock || authenticationOptions?.mockPasswordRequired === true;
    password.disabled = mock && !password.required;
    document.getElementById('passwordLabel').hidden = password.disabled;
    if (mock && authenticationOptions?.defaultUserName) userName.value = authenticationOptions.defaultUserName;
    password.value = '';
}
source.addEventListener('change', updateSignInMethod);
document.getElementById('togglePassword').addEventListener('click', () => {
    const showing = password.type === 'text';
    password.type = showing ? 'password' : 'text';
    document.getElementById('togglePassword').setAttribute('aria-pressed', String(!showing));
    document.getElementById('togglePassword').setAttribute('aria-label', showing ? 'Show password' : 'Hide password');
});

(async () => {
    try {
        const response = await fetch('/api/auth/options', { cache: 'no-store' });
        if (!response.ok) throw Error('Could not load sign-in methods. Reload this page to try again.');
        authenticationOptions = await response.json();
        for (const method of authenticationOptions.sources || []) {
            const option = document.createElement('option'); option.value = method;
            option.textContent = sourceLabels[method] || method; source.append(option);
        }
        source.value = authenticationOptions.defaultSource;
        document.getElementById('entraSignIn').hidden = !authenticationOptions.entraEnabled;
        if (authenticationOptions.mockAuthentication) {
            const environment = document.getElementById('environment'); environment.hidden = false;
            environment.textContent = 'Isolated demonstration sign-in';
        }
        updateSignInMethod(); signInButton.disabled = false;
        if (new URLSearchParams(location.search).has('pending')) error.textContent = 'Pending Access. Your administrator must approve access and assign a role.';
    } catch (failure) { error.textContent = failure.message; }
})();

loginForm.addEventListener('submit', async event => {
    event.preventDefault(); error.textContent = ''; signInButton.disabled = true;
    signInButton.textContent = 'Signing in…';
    try {
        const response = await fetch('/api/auth/login', {
            method: 'POST', headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ userName: userName.value.trim(), password: password.disabled ? '' : password.value, source: source.value })
        });
        if (response.status === 401) throw Error('The user name or password was not accepted.');
        if (response.status === 429) throw Error('Too many sign-in attempts. Wait a minute and try again.');
        const result = await response.json();
        if (!response.ok) throw Error(result.error || 'Sign-in could not be completed.');
        password.value = '';
        if (result.accountState === 'PendingAccess') {
            error.textContent = 'Your account is created and is Pending Access. An administrator must approve access and assign a role.';
            return;
        }
        sessionStorage.removeItem('studio-announcements-login'); location.replace('/');
    } catch (failure) { password.value = ''; error.textContent = failure.message || 'Could not reach the sign-in service.'; }
    finally { signInButton.disabled = !authenticationOptions; signInButton.textContent = 'Sign in →'; }
});

document.getElementById('recoveryToggle').addEventListener('click', event => {
    recoveryForm.hidden = !recoveryForm.hidden; loginForm.hidden = !recoveryForm.hidden;
    event.currentTarget.textContent = recoveryForm.hidden ? 'Recover a local account' : 'Back to sign in';
    error.textContent = '';
});
recoveryForm.addEventListener('submit', async event => {
    event.preventDefault(); const button = document.getElementById('recoverButton'); button.disabled = true;
    try {
        const response = await fetch('/api/auth/recover', { method: 'POST', headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ token: document.getElementById('recoveryToken').value.trim(), newPassword: document.getElementById('recoveryPassword').value }) });
        const result = await response.json();
        if (!response.ok) throw Error(result.error || 'Account recovery could not be completed.');
        recoveryForm.reset(); error.textContent = 'Password reset. Return to sign in and choose Local account.';
    } catch (failure) { error.textContent = failure.message; }
    finally { button.disabled = false; }
});
