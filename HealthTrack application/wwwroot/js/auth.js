'use strict';
document.querySelectorAll('[data-password-toggle]').forEach(button => {
    button.addEventListener('click', () => {
        const field = document.getElementById(button.dataset.passwordToggle);
        if (!field) return;
        const visible = field.type === 'password';
        field.type = visible ? 'text' : 'password';
        button.setAttribute('aria-pressed', String(visible));
        button.setAttribute('aria-label', visible ? 'Hide password' : 'Show password');
        button.querySelector('img').src = visible ? '/lib/auth-icons/eye.svg' : '/lib/auth-icons/eye-slash.svg';
    });
});
