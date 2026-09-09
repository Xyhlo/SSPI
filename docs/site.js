(() => {
  'use strict';
  document.documentElement.classList.add('js');
  const $ = id => document.getElementById(id);
  const sections = [...document.querySelectorAll('main section[id]')];
  const links = [...document.querySelectorAll('.sidebar nav a')];
  const menu = $('menu-toggle');
  const sidebar = $('sidebar');
  const viewport = $('page-scroll');
  const intro = document.querySelector('.intro-strip');
  const pageTitles = new Map(links.map(link => [link.hash.slice(1), link.textContent.replace(/^\s*\d+\s*/, '').trim()]));
  const scrollPositions = new Map();
  let activeSection = null;
  let currentHash = '';
  let tocItems = [];
  for (const section of sections) {
    section.querySelectorAll('h3').forEach((heading, i) => {
      if (!heading.id) heading.id = section.id + '--' + heading.textContent.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '') + '-' + (i + 1);
    });
  }
  function closeMenu() {
    sidebar.classList.remove('open');
    menu.setAttribute('aria-expanded', 'false');
  }
  menu.addEventListener('click', () => {
    const open = sidebar.classList.toggle('open');
    menu.setAttribute('aria-expanded', String(open));
  });
  links.forEach(link => link.addEventListener('click', closeMenu));
  document.addEventListener('keydown', event => {
    if (event.key === 'Escape' && sidebar.classList.contains('open')) {
      closeMenu(); menu.focus();
    }
  });
  function buildOutline(section) {
    tocItems = [{ id: section.id, title: 'Overview', element: section }];
    section.querySelectorAll('h3, details[id]').forEach(element => {
      tocItems.push({ id: element.id, title: element.matches('details') ? element.querySelector('summary').childNodes[0].textContent.trim() : element.textContent.trim(), element });
    });
    for (const container of [$('page-toc'), $('mobile-toc')]) {
      container.replaceChildren();
      for (const item of tocItems) {
        const link = document.createElement('a'); link.href = '#' + item.id; link.textContent = item.title;
        container.append(link);
      }
    }
  }
  let framePending = false;
  function updateReading() {
    framePending = false;
    if (!activeSection) return;
    const boundary = viewport.getBoundingClientRect().top + 100;
    let active = tocItems[0];
    for (const item of tocItems) if (item.element.getBoundingClientRect().top <= boundary) active = item;
    document.querySelectorAll('#page-toc a, #mobile-toc a').forEach(link => {
      if (link.hash === '#' + active.id) link.setAttribute('aria-current', 'location');
      else link.removeAttribute('aria-current');
    });
    const max = viewport.scrollHeight - viewport.clientHeight;
    $('reading-progress').style.transform = 'scaleX(' + (max > 0 ? Math.min(1, viewport.scrollTop / max) : 1) + ')';
  }
  function navigate(hash, options = {}) {
    let id;
    try { id = decodeURIComponent((hash || '#overview').slice(1)); } catch { id = 'overview'; }
    const requested = $(id);
    const section = requested?.closest('main section[id]') || sections[0];
    const target = section.contains(requested) ? requested : section;
    if (activeSection && activeSection !== section) scrollPositions.set(activeSection.id, viewport.scrollTop);
    activeSection = section;
    sections.forEach(item => item.hidden = item !== section);
    intro.hidden = section.id !== 'overview';
    const pageIndex = sections.indexOf(section);
    const title = pageTitles.get(section.id);
    document.title = section.id === 'overview' ? 'SSPI — The PS4 field guide' : title + ' — SSPI';
    const link = links[pageIndex];
    $('page-group').textContent = link.closest('.nav-group').querySelector('p').textContent.toLowerCase();
    $('page-name').textContent = title;
    $('page-count').textContent = String(pageIndex + 1).padStart(2, '0') + ' / ' + sections.length;
    links.forEach(item => {
      if (item === link) item.setAttribute('aria-current', 'page');
      else item.removeAttribute('aria-current');
    });
    for (const [button, destination] of [[$('previous-page'), sections[pageIndex - 1]], [$('next-page'), sections[pageIndex + 1]]]) {
      button.hidden = !destination;
      if (destination) { button.href = '#' + destination.id; button.querySelector('strong').textContent = pageTitles.get(destination.id); }
    }
    buildOutline(section);
    const detail = target.closest('details'); if (detail) detail.open = true;
    closeMenu(); $('mobile-outline').open = false;
    if (options.push && location.hash !== '#' + target.id) history.pushState(null, '', '#' + target.id);
    currentHash = location.hash;
    if (target === section) viewport.scrollTop = options.restore ? scrollPositions.get(section.id) || 0 : 0;
    else viewport.scrollTop += target.getBoundingClientRect().top - viewport.getBoundingClientRect().top - 24;
    if (options.focus) {
      const focusTarget = target === section ? section.querySelector('h1, h2') : target.matches('details') ? target.querySelector('summary') : target;
      if (focusTarget) { focusTarget.setAttribute('tabindex', '-1'); focusTarget.focus({ preventScroll: true }); }
    }
    updateReading();
  }
  document.addEventListener('click', event => {
    const link = event.target instanceof Element ? event.target.closest('a[href^="#"]') : null;
    if (!link || event.defaultPrevented || event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey || link.hash === '#content') return;
    event.preventDefault(); navigate(link.hash, { push: true, focus: true });
  });
  viewport.addEventListener('scroll', () => {
    if (!framePending) { framePending = true; requestAnimationFrame(updateReading); }
  }, { passive: true });
  window.addEventListener('resize', updateReading);
  window.addEventListener('load', updateReading);
  window.addEventListener('popstate', () => navigate(location.hash, { restore: true }));
  window.addEventListener('hashchange', () => { if (location.hash !== currentHash) navigate(location.hash); });
  navigate(location.hash);

  const checks = [...document.querySelectorAll('[data-check]')];
  const storageKey = 'sspi-guide-checklist-v1';
  try {
    const stored = JSON.parse(localStorage.getItem(storageKey) || '{}');
    checks.forEach(input => input.checked = stored?.[input.dataset.check] === true);
  } catch { /* The guide works when browser storage is unavailable. */ }
  function saveChecks() {
    try { localStorage.setItem(storageKey, JSON.stringify(Object.fromEntries(checks.map(input => [input.dataset.check, input.checked])))); } catch { /* Optional reading aid. */ }
  }
  checks.forEach(input => input.addEventListener('change', saveChecks));
  $('reset-checks').addEventListener('click', () => { checks.forEach(input => input.checked = false); saveChecks(); });

  $('connection-speed').addEventListener('input', event => {
    const value = event.target.valueAsNumber;
    const output = $('speed-result');
    output.replaceChildren();
    if (!Number.isFinite(value) || value < 0 || value > 100000) {
      output.textContent = 'Enter a valid speed'; return;
    }
    output.append(new Intl.NumberFormat('en', { maximumFractionDigits: 3 }).format(value / 8) + ' ');
    const unit = document.createElement('span'); unit.textContent = 'MB/s'; output.append(unit);
  });

  $('copy-report').addEventListener('click', async () => {
    try {
      await navigator.clipboard.writeText($('report-template').textContent.trim());
      $('copy-status').textContent = 'Copied. Fill in your details and remove any secrets before posting.';
    } catch {
      const selection = window.getSelection();
      const range = document.createRange(); range.selectNodeContents($('report-template'));
      selection.removeAllRanges(); selection.addRange(range);
      $('copy-status').textContent = 'Template selected. Copy it using your browser’s copy command.';
    }
  });

  const dialog = $('search-dialog');
  const input = $('search-input');
  const results = $('search-results');
  function searchableText(section) {
    const walker = document.createTreeWalker(section, NodeFilter.SHOW_TEXT);
    const parts = [];
    while (walker.nextNode()) parts.push(walker.currentNode.textContent);
    return parts.join(' ').replace(/\s+/g, ' ').trim();
  }
  const index = sections.map(section => ({
    id: section.id,
    title: section.dataset.title,
    text: searchableText(section)
  }));
  let selected = 0;
  let resultLinks = [];
  function markSelected() {
    resultLinks.forEach((link, i) => link.dataset.selected = String(i === selected));
  }
  function showResults() {
    const query = input.value.trim().toLowerCase();
    const terms = query.split(/\s+/).filter(Boolean);
    results.replaceChildren(); resultLinks = []; selected = 0;
    if (!query) { $('search-summary').textContent = 'Type a word or phrase to find a section.'; return; }
    const matches = index.map(entry => {
      const title = entry.title.toLowerCase();
      const text = entry.text.toLowerCase();
      return { ...entry, score: terms.every(term => text.includes(term)) ? terms.reduce((n, term) => n + (title.includes(term) ? 5 : 1), 0) : 0 };
    }).filter(entry => entry.score > 0).sort((a,b) => b.score - a.score).slice(0,10);
    $('search-summary').textContent = matches.length ? matches.length + ' matching section' + (matches.length === 1 ? '' : 's') + '.' : 'No matching sections. Try a shorter term, such as “source” or “install”.';
    for (const entry of matches) {
      const link = document.createElement('a'); link.href = '#' + entry.id;
      const title = document.createElement('strong'); title.textContent = entry.title;
      const snippet = document.createElement('span');
      const at = Math.max(0, entry.text.toLowerCase().indexOf(terms[0]) - 45);
      snippet.textContent = (at ? '…' : '') + entry.text.slice(at, at + 160) + (at + 160 < entry.text.length ? '…' : '');
      link.append(title, snippet);
      link.addEventListener('click', () => { dialog.close(); closeMenu(); });
      results.append(link); resultLinks.push(link);
    }
    markSelected();
  }
  function openSearch() { closeMenu(); dialog.showModal(); input.focus(); showResults(); }
  $('search-open').addEventListener('click', openSearch);
  $('search-close').addEventListener('click', () => dialog.close());
  input.addEventListener('input', showResults);
  input.addEventListener('keydown', event => {
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      if (!resultLinks.length) return;
      event.preventDefault();
      selected = (selected + (event.key === 'ArrowDown' ? 1 : -1) + resultLinks.length) % resultLinks.length;
      markSelected(); resultLinks[selected].scrollIntoView({ block: 'nearest' });
    } else if (event.key === 'Enter' && resultLinks[selected]) {
      event.preventDefault(); resultLinks[selected].click();
    }
  });
  dialog.addEventListener('click', event => { if (event.target === dialog) {
    const box = dialog.getBoundingClientRect();
    if (event.clientX < box.left || event.clientX > box.right || event.clientY < box.top || event.clientY > box.bottom) dialog.close();
  }});
  document.addEventListener('keydown', event => {
    const editing = event.target instanceof Element && event.target.closest('input, textarea, select, [contenteditable="true"]');
    if ((event.key === '/' && !editing) || ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'k')) {
      event.preventDefault(); if (!dialog.open) openSearch();
    }
  });
})();
