'use strict';
(() => {
  const button = document.querySelector('.home-menu-toggle');
  const nav = document.getElementById('home-navigation');
  const close = () => { nav.classList.remove('is-open'); button.setAttribute('aria-expanded', 'false'); button.setAttribute('aria-label', 'Open navigation'); };
  button.addEventListener('click', () => { const open = nav.classList.toggle('is-open'); button.setAttribute('aria-expanded', String(open)); button.setAttribute('aria-label', open ? 'Close navigation' : 'Open navigation'); });
  nav.querySelectorAll('a').forEach(link => link.addEventListener('click', close));
  document.addEventListener('keydown', event => { if (event.key === 'Escape' && nav.classList.contains('is-open')) { close(); button.focus(); } });
  document.addEventListener('click', event => { if (!nav.contains(event.target) && !button.contains(event.target)) close(); });
  window.matchMedia('(min-width: 1001px)').addEventListener('change', close);

  // Progressive enhancement: content stays visible if animation is unavailable.
  const motion = window.matchMedia('(prefers-reduced-motion: reduce)');
  if (!motion.matches && 'IntersectionObserver' in window && Element.prototype.animate) {
    const animations = new Set();
    const observer = new IntersectionObserver(entries => {
      entries.forEach(entry => {
        if (!entry.isIntersecting) return;
        observer.unobserve(entry.target);
        if (motion.matches || entry.target.contains(document.activeElement) || entry.target.matches(':target')) return;
        const animation = entry.target.animate([
          { opacity: 0.8, transform: 'translateY(12px)' },
          { opacity: 1, transform: 'translateY(0)' }
        ], { duration: 420, easing: 'cubic-bezier(.2,.65,.3,1)' });
        animations.add(animation);
        animation.addEventListener('finish', () => animations.delete(animation), { once: true });
      });
    }, { threshold: 0.05 });
    document.querySelectorAll('main > section:not(.fitness-hero):not(.public-page-hero), .policy-content > section')
      .forEach(section => observer.observe(section));
    motion.addEventListener('change', () => {
      if (!motion.matches) return;
      observer.disconnect();
      animations.forEach(animation => animation.cancel());
      animations.clear();
    });
  }
})();
