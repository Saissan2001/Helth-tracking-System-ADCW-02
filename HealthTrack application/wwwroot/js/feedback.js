'use strict';
(() => {
  // MVC summaries may contain an empty placeholder <li>; :empty alone misses it.
  // Hide the whole box only while it has no message, and restore it if errors arrive.
  document.querySelectorAll('[data-valmsg-summary], .validation-summary-valid, .validation-summary-errors, .validation, .auth-validation, .contact-validation').forEach(summary => {
    const update = () => { summary.hidden = !summary.textContent.trim(); };
    update();
    new MutationObserver(update).observe(summary, { childList: true, characterData: true, subtree: true });
  });
  // Native constraints and MVC metadata share the same inline message containers.
  // Server validation remains authoritative when JavaScript is unavailable.
  let sequence = 0;
  document.querySelectorAll('form').forEach(form => {
    const controls = [...form.querySelectorAll('input[name], select[name], textarea[name]')]
      .filter(field => !field.disabled && !['hidden', 'submit', 'button', 'reset', 'checkbox', 'radio'].includes(field.type));
    if (!controls.length) return;
    const messages = new Map();
    controls.forEach(field => {
      if (field.hasAttribute('data-attempted-value')) field.value = field.dataset.attemptedValue;
      let message = [...form.querySelectorAll('[data-valmsg-for]')].find(node => node.dataset.valmsgFor === field.name);
      if (!message) {
        message = document.createElement('span');
        message.dataset.valmsgFor = field.name;
        message.className = 'field-validation-valid';
        (field.closest('.auth-input-wrap') || field).after(message);
      }
      if (!message.id) message.id = 'field-message-' + (++sequence);
      message.setAttribute('aria-live', 'polite');
      const described = new Set((field.getAttribute('aria-describedby') || '').split(/\s+/).filter(Boolean));
      described.add(message.id);
      field.setAttribute('aria-describedby', [...described].join(' '));
      if (message.textContent.trim()) field.setAttribute('aria-invalid', 'true');
      messages.set(field, message);
    });
    const errorFor = field => {
      const value = field.value;
      const data = field.dataset;
      const label = field.labels?.[0]?.textContent.trim() || 'This field';
      if ((field.required || data.valRequired) && !value.trim()) return data.valRequired || 'Please complete ' + label.toLowerCase() + '.';
      if (field.validity.badInput) return 'Enter a valid value.';
      if (!value) return '';
      if (field.validity.typeMismatch || (data.valEmail && !/^[^\s@]+@[^\s@]+$/.test(value))) return data.valEmail || 'Enter a valid email address.';
      const minLength = Number(data.valLengthMin ?? data.valMinlengthMin ?? field.getAttribute('minlength') ?? 0);
      const maxLength = Number(data.valLengthMax ?? data.valMaxlengthMax ?? field.getAttribute('maxlength') ?? Infinity);
      if (value.length < minLength || value.length > maxLength) return data.valLength || data.valMinlength || data.valMaxlength || (value.length < minLength ? 'Use at least ' + minLength + ' characters.' : 'Use no more than ' + maxLength + ' characters.');
      if (data.valEqualtoOther) {
        const otherName = data.valEqualtoOther.startsWith('*.') ? field.name.substring(0, field.name.lastIndexOf('.') + 1) + data.valEqualtoOther.slice(2) : data.valEqualtoOther;
        const other = controls.find(input => input.name === otherName);
        if (other && value !== other.value) return data.valEqualto || 'The passwords do not match.';
      }
      if (data.valRange || data.valNumber || field.type === 'number') {
        const number = Number(value);
        if (!Number.isFinite(number)) return data.valNumber || 'Enter a valid number.';
        const min = Number(data.valRangeMin ?? field.getAttribute('min') ?? -Infinity);
        const max = Number(data.valRangeMax ?? field.getAttribute('max') ?? Infinity);
        if (number < min || number > max) return data.valRange || 'Enter a value between ' + min + ' and ' + max + '.';
      }
      if (!field.validity.valid) return field.validationMessage;
      return '';
    };
    const validate = field => {
      const error = errorFor(field);
      const message = messages.get(field);
      message.textContent = error;
      message.className = error ? 'field-validation-error' : 'field-validation-valid';
      field.classList.toggle('input-validation-error', !!error);
      field.setAttribute('aria-invalid', String(!!error));
      return !error;
    };
    const focusField = field => {
      let details = field.closest('details');
      while (details) { details.open = true; details = details.parentElement?.closest('details'); }
      field.focus();
    };
    form.noValidate = true;
    controls.forEach(field => {
      field.addEventListener('blur', () => { if (field.value || form.dataset.validationAttempted || field.getAttribute('aria-invalid') === 'true') validate(field); });
      field.addEventListener('input', () => {
        if (field.getAttribute('aria-invalid') === 'true' || form.dataset.validationAttempted) validate(field);
        controls.filter(other => other.dataset.valEqualtoOther && other.value).forEach(validate);
      });
      field.addEventListener('change', () => { if (field.value || form.dataset.validationAttempted) validate(field); });
    });
    form.addEventListener('submit', event => {
      if (event.submitter?.formNoValidate) return;
      form.dataset.validationAttempted = 'true';
      const invalid = controls.filter(field => !validate(field));
      if (invalid.length) { event.preventDefault(); event.stopImmediatePropagation(); focusField(invalid[0]); }
    }, true);
    const firstError = controls.find(field => field.getAttribute('aria-invalid') === 'true');
    if (firstError && !document.querySelector('[data-feedback-focused]')) {
      form.dataset.feedbackFocused = 'true';
      focusField(firstError);
    }
    if (form.dataset.invalidForm) {
      const details = form.closest('details');
      if (details) details.open = true;
    }
  });

  document.querySelectorAll('[data-toast]').forEach(toast => {
    const close = toast.querySelector('.toast-close');
    close.hidden = false;
    let timer;
    let dismissed = false;
    const pause = () => clearTimeout(timer);
    const resume = () => {
      pause();
      if (!dismissed && toast.dataset.toast !== 'error' && !document.hidden && !toast.matches(':hover') && !toast.contains(document.activeElement)) timer = setTimeout(dismiss, 10000);
    };
    const dismiss = () => { dismissed = true; pause(); document.removeEventListener('visibilitychange', resume); toast.remove(); };
    close.addEventListener('click', dismiss);
    toast.addEventListener('mouseenter', pause);
    toast.addEventListener('mouseleave', resume);
    toast.addEventListener('focusin', pause);
    toast.addEventListener('focusout', () => setTimeout(resume, 0));
    document.addEventListener('visibilitychange', resume);
    resume();
  });
})();
