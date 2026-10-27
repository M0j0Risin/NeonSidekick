// NeonSidekick's Web UI, what every page shares (2026-10-15): the element maker, the sentence under the page, the JSON post (the header no
// cross-site form can send; a 401 signs in again, a 429's sentence is the app's), the connection dot, the poll that rests while the page is
// hidden, the self-reload after an upgrade (once per tag), the Chat badge on the other tabs and the theme's re-link. Loaded first, before the
// page's own script; nothing here runs on its own but the theme's and the badge's wiring the page asks for. Later on 2026-10-15: the tabs on
// (a tab exists only while its controls are on at the PC), so the nav hides the others and a page whose tab went off goes to /. 2026-10-16: the
// YouTube tab (on only while the PC says so: Web UI YouTube controls since later that day), and the jump to it while a video waits (watch).
// Later on 2026-10-16: post's keepalive (a page leaving still says so) and the two-tap buttons (armTwice), shared by Chat's sheet and the
// YouTube tab's saved list. 2026-10-27: the picture read (picture), for the Home tab's camera stills.
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
  // options.keepalive (later on 2026-10-16): the post outlives the page (a YouTube tab saying it is hidden as it goes).
  async function post(path, body, options) {
    try {
      const r = await fetch(path, {
        method: 'POST',
        credentials: 'same-origin',
        headers: { 'Content-Type': 'application/json', 'X-Neon': '1' },
        body: JSON.stringify(body || {}),
        keepalive: !!(options && options.keepalive),
      });
      let j = null;
      try { j = await r.json(); } catch { /* none */ }
      if (r.status === 401) location.reload();
      if (r.status === 404 && j && j.tabOff) leave();
      return { ok: r.ok, status: r.status, message: j && j.message, asText: !!(j && j.asText), use: j && j.use, json: j, retryAfter: Number(r.headers.get('Retry-After')) || null };
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
      if (r.status === 404 && j && j.tabOff) leave();
      return { ok: r.ok, status: r.status, json: j };
    } catch {
      connected(false);
      return { ok: false, status: 0, json: null };
    }
  }

  // A picture only the page's own script may ask for (2026-10-27, a camera's still): sent with the header no planted <img> can send, and
  // handed back as a data: URL, which the page's policy lets in (img-src 'self' data:). The answer: ok, the status, the picture or the app's
  // sentence, a 429's wait in seconds; aborted when the signal ended it. Any answer from the app lights the dot: a camera that failed is
  // Home Assistant's trouble, not the connection's.
  async function picture(path, signal) {
    try {
      const r = await fetch(path, { credentials: 'same-origin', cache: 'no-store', headers: { 'X-Neon': '1' }, signal });
      if (r.status === 401) { location.reload(); return { ok: false, status: 401 }; }
      connected(true);
      if (r.ok) {
        const blob = await r.blob();
        const dataUrl = await new Promise((resolve, reject) => {
          const reader = new FileReader();
          reader.onload = () => resolve(reader.result);
          reader.onerror = () => reject(reader.error);
          reader.readAsDataURL(blob);
        });
        return { ok: true, status: r.status, dataUrl };
      }
      let j = null;
      try { j = await r.json(); } catch { /* none */ }
      if (r.status === 404 && j && j.tabOff) leave();
      return { ok: false, status: r.status, message: j && j.message, retryAfter: Number(r.headers.get('Retry-After')) || null };
    } catch (e) {
      if (signal && signal.aborted) return { ok: false, status: 0, aborted: true };
      connected(false);
      return { ok: false, status: 0, message: 'The app could not be reached.' };
    }
  }

  // ── Two-tap buttons (later on 2026-10-16, moved here from Chat's sheet) ──
  // A 🗑️ or clear all: the first tap arms it (red, its label says so) for 3 s, the second acts. One armed at a time on the page.
  let armedNow = null;   // { button, timer, paint }

  function disarm() {
    if (!armedNow) return;
    clearTimeout(armedNow.timer);
    const was = armedNow;
    armedNow = null;
    was.paint(false);
  }

  function armed() { return !!armedNow; }

  function armTwice(button, paint, act) {
    paint(false);
    button.addEventListener('click', ev => {
      ev.stopPropagation();
      if (armedNow && armedNow.button === button) {
        disarm();
        act();
        return;
      }
      disarm();
      armedNow = { button, paint, timer: setTimeout(disarm, 3000) };
      paint(true);
    });
  }

  // The usual paint: the armed class, and the label (aria-label and title) from label(on).
  function paintArmed(button, label) {
    return on => {
      button.classList.toggle('armed', on);
      const text = label(on);
      button.setAttribute('aria-label', text);
      button.title = text;
    };
  }

  // ── The tabs (later on 2026-10-15) ──
  // Each tab exists only while its controls are on at the PC: the nav shows the ones on, and a page whose own tab went off (its body's
  // data-tab) goes to /, which the server sends on to a tab that is on. The nav starts hidden so a link to a tab that is off never flashes;
  // it shows once the tabs are known, or after 2 seconds whatever happens, so a page that can't reach the PC still has its links.
  let leaving = false;
  function leave() {
    if (leaving) return;
    leaving = true;
    location.replace('/');
  }

  function navReady() {
    const nav = document.querySelector('header.top nav');
    if (nav) nav.classList.add('ready');
  }
  setTimeout(navReady, 2000);

  function tabOf(href) {
    return href === '/' ? 'chat' : href === '/home' ? 'home' : href === '/shopping' ? 'shopping' : href === '/youtube' ? 'youtube' : null;
  }

  // True when the page leaves (its own tab is off), so the caller draws nothing more. YouTube is on only when the PC says it is (=== true), so a
  // page from before it, or a pulse that says nothing of it, never shows the link; the other three are on unless === false.
  function tabs(chat, home, shopping, youtube) {
    const on = { chat: chat !== false, home: home !== false, shopping: shopping !== false, youtube: youtube === true };
    for (const a of document.querySelectorAll('header.top nav a')) {
      const tab = tabOf(a.getAttribute('href'));
      if (tab) a.classList.toggle('hidden', !on[tab]);
    }
    // The menu goes with its last tab (2026-10-16): Chat alone is left.
    if (tabMenu) tabMenu.classList.toggle('hidden', menuItems().length === 0);
    labelMenu();
    navReady();
    const here = document.body.dataset.tab;
    if (here && on[here] === false) {
      leave();
      return true;
    }
    return false;
  }

  // ── The tab menu (2026-10-16, the user's pick) ──
  // Chat stays a link (it carries the busy dot and the questions' badge); Home, Shopping and YouTube sit behind one button, ☰ on Chat and the
  // open tab's glyph and name with ▾ on the others, so a phone's header keeps the profile's name readable. Wired as this script loads, never
  // only from tabs(): a page that can't reach the PC still gets its nav after navReady's 2 s, and its menu must work then too. It closes on a
  // pick, a press outside (pointerdown: iOS fires no click on a tap over the transcript), Esc (focus back on the button) or a scroll; ↑/↓ walk
  // it while open. Its keys act only while it is open, so the draft's history walk, the dropdown and the sheet's Esc are untouched otherwise.
  const tabMenu = document.querySelector('header.top .tab-menu');
  const tabMenuButton = tabMenu && tabMenu.querySelector('.tab-menu-button');
  const tabMenuList = tabMenu && tabMenu.querySelector('.tab-menu-list');

  function menuItems() {
    return tabMenuList ? [...tabMenuList.querySelectorAll('a')].filter(a => !a.classList.contains('hidden')) : [];
  }

  function menuOpen() {
    return !!tabMenuList && !tabMenuList.classList.contains('hidden');
  }

  function labelMenu() {
    if (!tabMenuButton) return;
    const here = tabMenuList.querySelector('a.here');
    tabMenuButton.textContent = here ? here.textContent + ' ▾' : '☰';
    tabMenuButton.classList.toggle('here', !!here);
    const names = menuItems().map(a => a.textContent.replace(/^\S+\s+/u, ''));
    const label = 'Tabs: ' + names.join(', ');
    tabMenuButton.setAttribute('aria-label', label);
    tabMenuButton.title = label;
  }

  function pressedOutside(ev) {
    if (!tabMenu.contains(ev.target)) closeMenu(false);
  }

  function scrolled() {
    closeMenu(false);
  }

  function openMenu() {
    tabMenuList.classList.remove('hidden');
    tabMenuButton.setAttribute('aria-expanded', 'true');
    document.addEventListener('pointerdown', pressedOutside, true);
    document.addEventListener('scroll', scrolled, true);
  }

  function closeMenu(focus) {
    if (!menuOpen()) return;
    tabMenuList.classList.add('hidden');
    tabMenuButton.setAttribute('aria-expanded', 'false');
    document.removeEventListener('pointerdown', pressedOutside, true);
    document.removeEventListener('scroll', scrolled, true);
    if (focus) tabMenuButton.focus();
  }

  if (tabMenu) {
    labelMenu();
    tabMenuButton.addEventListener('click', () => { if (menuOpen()) closeMenu(false); else openMenu(); });
    tabMenuList.addEventListener('click', ev => { if (ev.target.closest('a')) closeMenu(false); });
    document.addEventListener('keydown', ev => {
      if (!menuOpen()) return;
      if (ev.key === 'Escape') {
        ev.preventDefault();
        ev.stopPropagation();
        closeMenu(true);
      } else if (ev.key === 'ArrowDown' || ev.key === 'ArrowUp') {
        ev.preventDefault();
        ev.stopPropagation();
        const items = menuItems();
        if (!items.length) return;
        const at = items.indexOf(document.activeElement);
        const step = ev.key === 'ArrowDown' ? 1 : -1;
        items[at < 0 ? (step > 0 ? 0 : items.length - 1) : (at + step + items.length) % items.length].focus();
      }
    }, true);
  }

  // The phone's keyboard over the page's bottom (later on 2026-10-15): an iPhone doesn't shrink the layout viewport for it, so what is fixed
  // to the bottom moves up by this much. Pure, so a test can give it a viewport of its own.
  function keyboardInset(innerHeight, viewport) {
    if (!viewport) return 0;
    return Math.max(0, Math.round(innerHeight - viewport.height - (viewport.offsetTop || 0)));
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

  // ── A video waiting for the YouTube tab (2026-10-16) ──
  // Every page jumps to /youtube while the PC holds a video for it, unless the page says not now (watchBlocked: by default a text field with
  // focus; Chat sets its own: a draft, an ask card, the sheet), when it shows a banner instead, a link to the tab. A page that showed the banner
  // for a waiting video never jumps for that one by itself; a new one (the flag cleared and set again) gets the usual check. A hidden page waits
  // until it shows. The YouTube page itself does nothing.
  const api = {};
  let bannerShown = false;
  let watchWords = null;
  let watchDeferred = false;
  function blockedByDefault() {
    const a = document.activeElement;
    return !!a && (a.tagName === 'TEXTAREA' || a.tagName === 'INPUT' || a.isContentEditable);
  }

  function banner(words) {
    let b = document.getElementById('watch-banner');
    if (!words) { if (b) b.remove(); return; }
    if (!b) {
      b = el('a', 'watch-banner');
      b.id = 'watch-banner';
      b.href = '/youtube';
      const top = document.querySelector('header.top');
      if (top && top.parentNode) top.parentNode.insertBefore(b, top.nextSibling); else document.body.prepend(b);
    }
    b.textContent = words;
  }

  function watch(words) {
    if (document.body.dataset.tab === 'youtube') return;
    watchWords = words || null;
    if (!watchWords) { bannerShown = false; banner(null); return; }
    if (bannerShown) { banner(watchWords); return; }
    if (document.visibilityState !== 'visible') {
      if (!watchDeferred) {
        watchDeferred = true;
        const shown = () => {
          if (document.visibilityState !== 'visible') return;
          document.removeEventListener('visibilitychange', shown);
          watchDeferred = false;
          watch(watchWords);
        };
        document.addEventListener('visibilitychange', shown);
      }
      return;
    }
    const blocked = api.watchBlocked ? api.watchBlocked() : blockedByDefault();
    if (blocked) { bannerShown = true; banner(watchWords); return; }
    location.assign('/youtube');
  }

  // The Chat badge on the other tabs (2026-10-15, E2): a dot while a reply runs, a count while the PC waits on an answer; (n) in the title; one
  // vibration on a new ask where the phone can (Android: iOS Safari has none, so the badge and the title carry it there).
  const baseTitle = document.title;
  let lastAsks = 0;
  function pulse(p) {
    if (!p) return;
    if (staleAssets(p.assets)) return;
    theme(p.theme);
    if (tabs(p.chatTab, p.homeTab, p.shoppingTab, p.youTubeTab)) return;
    watch(p.youTubeTab === true ? (p.watch || null) : null);
    // With Chat off the PC's questions are the PC's alone: no dot, no count, no (n), no vibration.
    const chatOn = p.chatTab !== false;
    const waiting = chatOn ? (p.asks || 0) + (p.waitingAtPc ? 1 : 0) : 0;
    const link = document.getElementById('chat-link');
    if (link) {
      link.classList.toggle('busy', chatOn && !!p.busy && waiting === 0);
      link.classList.toggle('asking', waiting > 0);
      link.dataset.badge = waiting > 0 ? String(waiting) : '';
    }
    document.title = (waiting > 0 ? '(' + waiting + ') ' : '') + baseTitle;
    if (waiting > lastAsks && navigator.vibrate) {
      try { navigator.vibrate(200); } catch { /* not allowed */ }
    }
    lastAsks = waiting;
  }

  return Object.assign(api, { el, say, post, get, picture, connected, poll, staleAssets, theme, pulse, tabs, leave, keyboardInset, watch, watchBlocked: null, armTwice, paintArmed, disarm, armed });
})();
