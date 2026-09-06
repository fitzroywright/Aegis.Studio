const loginForm = document.getElementById('loginForm');
const userName = document.getElementById('userName');
const password = document.getElementById('password');
const signInButton = document.getElementById('signInButton');
const error = document.getElementById('error');

loginForm.addEventListener('submit', async event => {
    event.preventDefault();
    error.textContent = '';
    signInButton.disabled = true;
    signInButton.textContent = 'Signing in...';

    try {
        const response = await fetch('/api/auth/login', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                userName: userName.value.trim(),
                password: password.value
            })
        });

        if (response.status === 401) {
            error.textContent = 'The user name or password was not accepted.';
            password.value = '';
            password.focus();
            return;
        }

        if (!response.ok) {
            error.textContent = 'Aegis could not complete sign in. Check the server log and Active Directory connection.';
            return;
        }

        window.location.replace('/');
    } catch {
        error.textContent = 'Aegis could not reach the sign-in service.';
    } finally {
        signInButton.disabled = false;
        signInButton.textContent = 'Sign in';
    }
});
