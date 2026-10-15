// NeonSidekick's Web UI, what every page shares (2026-10-15): the element maker, the sentence under the page, the JSON post (the header no
// cross-site form can send; a 401 signs in again, a 429's sentence is the app's), the connection dot, the poll that rests while the page is
// hidden, the self-reload after an upgrade (once per tag), the Chat badge on the other tabs and the theme's re-link. Loaded first, before the
// page's own script; nothing here runs on its own but the theme's and the badge's wiring the page asks for.
'use strict';

const Neon = (() => {
  const messageEl = document.getElementById('message');
  const dotEl = document.getElementById('dot');
  let assetsTag = null;

  function el(tag, cls, text) {
    const e = document.createElement(tag);
    if (cls) e.className = cls;
    if (text !== undefined && text !== null) e.textContent = text;
    return e;
  }

  // A sentence under the page, with buttons the app offered (a refused command's Send as a message, a filled draft's Undo).
  function say(text, actions) {
    if (!messageEl) return;
    messageEl.replaceChildren(document.createTextNode(text || ''));
    for (const a of actions || []) {
      const b = el('button', 'inline', a.label);
      b.type = 'button';
      b.addEventListener('click', () => { say(''); a.run(); });
      messageEl.appendChild(b);
    }
    messageEl.classList.toggle('hidden', !text);
  }

  // A JSON post the app takes from its own page only. The answer: ok, the status, the app's sentence, and the body for what else it carries.
  async function post(path, body) {
    try {
      const r = await fetch(path, {
        method: 'POST',
        credentials: 'same-origin',
        headers: { 'Content-Type': 'application/json', 'X-Neon': '1' },
        body: JSON.stringify(body || {}),
      });
      let j = null;
      try { j = await r.json(); } catch { /* none */ }
      if (r.status === 401) location.reload();
      return { ok: r.ok, status: r.status, message: j && j.message, asText: !!(j && j.asText), use: j && j.use, json: j };
    } catch {
      return { ok: false, status: 0, message: 'The app could not be reached.' };
    }
  }

  // A JSON read: the body, null for a 304 (nothing new) or a failure; a 401 signs in again. The ETag is kept per path for the next ask.
  const etags = new Map();
  async function get(path, useEtag) {
    try {
      const headers = {};
      if (useEtag && etags.has(path)) headers['If-None-Match'] = etags.get(path);
      const r = await fetch(path, { credentials: 'same-origin', cache: 'no-store', headers });
      if (r.status === 401) { location.reload(); return { ok: false, status: 401 }; }
      connected(r.ok || r.status === 304);
      if (r.status === 304) return { ok: true, status: 304, json: null };
      if (useEtag && r.headers.get('ETag')) etags.set(path, r.headers.get('ETag'));
      let j = null;
      try { j = await r.json(); } catch { /* none */ }
      return { ok: r.ok, status: r.status, json: j };
    } catch {
      connected(false);
      return { ok: false, status: 0, json: null };
    }
  }

  function connected(on) {
    if (dotEl) dotEl.classList.toggle('on', !!on);
  }

  // Calls fn now and every ms while the page is visible; a hidden page rests, and is read at once when it shows again. The answer's every()
  // changes the pace (a tap's quick reads), now() reads at once.
  function poll(fn, ms) {
    let pace = ms;
    let timer = null;
    let running = false;
    async function tick() {
      clearTimeout(timer);
      timer = null;
      if (document.visibilityState !== 'visible') return;
      if (!running) {
        running = true;
        try { await fn(); } finally { running = false; }
      }
      timer = setTimeout(tick, pace);
    }
    document.addEventListener('visibilitychange', () => { if (document.visibilityState === 'visible') tick(); });
    tick();
    return { now: tick, every(next) { pace = next; if (timer) { clearTimeout(timer); timer = setTimeout(tick, pace); } } };
  }

  // The app serves other files than the ones this page runs (an upgrade while the phone kept the tab): reload, once per tag. True when it
  // reloads, so the caller draws nothing more. beforeReload keeps what the page must (the chat's draft).
  function staleAssets(tag, beforeReload) {
    if (!tag) return false;
    if (assetsTag === null) { assetsTag = tag; return false; }
    if (tag === assetsTag) return false;
    let tried = null;
    try { tried = sessionStorage.getItem('neon.reloaded'); } catch { /* private mode */ }
    if (tried === tag) return false;
    try { sessionStorage.setItem('neon.reloaded', tag); } catch { /* private mode */ }
    if (beforeReload) beforeReload();
    location.reload();
    return true;
  }

  // The theme's sheet re-linked when the PC's theme changes (2026-10-15, E10): the page loaded it plainly, revalidated on each load.
  let themeName = null;
  function theme(name) {
    if (name === undefined || name === null) return;
    if (themeName === null) { themeName = name; return; }
    if (name === themeName) return;
    themeName = name;
    const link = document.getElementById('theme-css');
    if (link) link.href = '/theme.css?v=' + encodeURIComponent(name || 'default');
  }

  // The Chat badge on the other tabs (2026-10-15, E2): a dot while a reply runs, a count while the PC waits on an answer; (n) in the title; one
  // vibration on a new ask where the phone can (Android: iOS Safari has none, so the badge and the title carry it there).
  const baseTitle = document.title;
  let lastAsks = 0;
  function pulse(p) {
    if (!p) return;
    if (staleAssets(p.assets)) return;
    theme(p.theme);
    const waiting = (p.asks || 0) + (p.waitingAtPc ? 1 : 0);
    const link = document.getElementById('chat-link');
    if (link) {
      link.classList.toggle('busy', !!p.busy && waiting === 0);
      link.classList.toggle('asking', waiting > 0);
      link.dataset.badge = waiting > 0 ? String(waiting) : '';
    }
    document.title = (waiting > 0 ? '(' + waiting + ') ' : '') + baseTitle;
    if (waiting > lastAsks && navigator.vibrate) {
      try { navigator.vibrate(200); } catch { /* not allowed */ }
    }
    lastAsks = waiting;
  }

  return { el, say, post, get, connected, poll, staleAssets, theme, pulse };
})();
