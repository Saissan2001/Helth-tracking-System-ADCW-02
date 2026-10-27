'use strict';
(() => {
  const body = document.body;
  const sidebar = document.getElementById('sidebar');
  const collapse = document.querySelector('.sidebar-collapse');
  const mobile = document.querySelector('.mobile-toggle');
  const backdrop = document.querySelector('.sidebar-backdrop');
  const media = window.matchMedia('(max-width: 950px)');
  const setCollapsed = collapsed => {
    body.classList.toggle('sidebar-collapsed', collapsed);
    collapse?.setAttribute('aria-expanded', String(!collapsed));
    collapse?.setAttribute('aria-label', collapsed ? 'Expand sidebar' : 'Collapse sidebar');
  };
  try { setCollapsed(localStorage.getItem('healthtrack.sidebar.collapsed') === 'true'); } catch { }
  collapse?.addEventListener('click', () => {
    const collapsed = !body.classList.contains('sidebar-collapsed');
    setCollapsed(collapsed);
    try { localStorage.setItem('healthtrack.sidebar.collapsed', String(collapsed)); } catch { }
  });
  const closeMobile = (restoreFocus = false) => {
    sidebar?.classList.remove('open');
    body.classList.remove('nav-open');
    mobile?.setAttribute('aria-expanded', 'false');
    if (backdrop) backdrop.hidden = true;
    if (restoreFocus) mobile?.focus();
  };
  // site.js owns the toggle; keep the modal backdrop and focus in sync.
  mobile?.addEventListener('click', () => {
    const open = sidebar.classList.contains('open');
    body.classList.toggle('nav-open', open);
    if (backdrop) backdrop.hidden = !open;
    if (open) sidebar.querySelector('a')?.focus();
  });
  backdrop?.addEventListener('click', () => closeMobile(true));
  media.addEventListener('change', () => closeMobile());
  const search = document.querySelector('.page-search');
  const input = document.getElementById('page-search-input');
  const results = [...document.querySelectorAll('.search-results a')];
  const empty = document.querySelector('.search-empty');
  const filter = () => {
    const query = input.value.trim().toLocaleLowerCase();
    results.forEach(link => { link.hidden = !link.textContent.toLocaleLowerCase().includes(query); });
    empty.hidden = results.some(link => !link.hidden);
  };
  const openSearch = () => {
    closeMobile();
    input.value = '';
    filter();
    if (!search.open) search.showModal();
    input.focus();
  };
  document.querySelectorAll('[data-open-search]').forEach(button => button.addEventListener('click', openSearch));
  document.querySelector('[data-close-search]')?.addEventListener('click', () => search.close());
  search?.addEventListener('close', () => { if (media.matches) mobile?.focus(); });
  input?.addEventListener('input', filter);
  input?.addEventListener('keydown', event => {
    if (event.key === 'ArrowDown') { event.preventDefault(); results.find(link => !link.hidden)?.focus(); }
    if (event.key === 'Enter') { event.preventDefault(); results.find(link => !link.hidden)?.click(); }
  });
  document.addEventListener('keydown', event => {
    if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'k') { event.preventDefault(); openSearch(); }
    if (event.key === 'Escape' && !search.open) { closeMobile(true); document.querySelector('.account-menu')?.removeAttribute('open'); }
    if (event.key === 'Tab' && media.matches && sidebar?.classList.contains('open') && !search.open) {
      const focusable = [...sidebar.querySelectorAll('a,button,summary')].filter(element => element.getClientRects().length);
      const first = focusable[0], last = focusable.at(-1);
      if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last.focus(); }
      else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first.focus(); }
    }
  });
  document.addEventListener('click', event => {
    const menu = document.querySelector('.account-menu');
    if (menu?.open && !menu.contains(event.target)) menu.open = false;
  });
})();
