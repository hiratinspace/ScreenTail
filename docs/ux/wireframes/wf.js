// Shared wireframe helpers (ST-014).
//  - [data-state-set="x"] buttons switch the page state; #state=x deep-links to one.
//  - [data-when="a b"] shows an element only in those states; [data-unless="a b"] hides it in them.
//  - wf.toast(text, action, onAction, ms) shows a toast inside the first .frame.
//  - "C" toggles the numbered callouts so a screen can be viewed clean.
(function () {
  const root = document.documentElement;
  const listeners = [];

  function setState(state) {
    root.dataset.state = state;
    document.querySelectorAll('[data-state-set]').forEach((b) => {
      b.setAttribute('aria-pressed', String(b.dataset.stateSet === state));
    });
    document.querySelectorAll('[data-when]').forEach((el) => {
      el.hidden = !el.dataset.when.split(' ').includes(state);
    });
    document.querySelectorAll('[data-unless]').forEach((el) => {
      el.hidden = el.dataset.unless.split(' ').includes(state);
    });
    listeners.forEach((fn) => fn(state));
    history.replaceState(null, '', '#state=' + state);
  }

  function toast(text, action, onAction, ms) {
    const host = document.querySelector('.frame .toast-host') || makeHost();
    const el = document.createElement('div');
    el.className = 'toast';
    el.setAttribute('role', 'status');
    el.textContent = text;
    if (action) {
      const b = document.createElement('button');
      b.className = 'btn sm';
      b.textContent = action;
      b.addEventListener('click', () => { el.remove(); if (onAction) onAction(); });
      el.appendChild(b);
    }
    host.appendChild(el);
    setTimeout(() => el.remove(), ms || 6000);
    return el;
  }

  function makeHost() {
    const host = document.createElement('div');
    host.className = 'toast-host';
    (document.querySelector('.frame') || document.body).appendChild(host);
    return host;
  }

  window.wf = {
    setState,
    toast,
    onState(fn) { listeners.push(fn); },
    get state() { return root.dataset.state; },
  };

  document.addEventListener('click', (e) => {
    const b = e.target.closest('[data-state-set]');
    if (b) setState(b.dataset.stateSet);
  });

  document.addEventListener('keydown', (e) => {
    const typing = e.target.closest('input, textarea, select, [contenteditable="true"]');
    if (!typing && !e.ctrlKey && !e.altKey && !e.metaKey && (e.key === 'c' || e.key === 'C')) {
      root.classList.toggle('no-callouts');
    }
  });

  document.addEventListener('DOMContentLoaded', () => {
    const fromHash = new URLSearchParams(location.hash.slice(1)).get('state');
    const first = document.querySelector('[data-state-set]');
    if (fromHash || first) setState(fromHash || first.dataset.stateSet);
  });
})();
