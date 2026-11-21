// NeonSidekick's Web UI, the 🗂️ sheet (2026-11-16, plan lucid-roaming-lantern, the user's pick): one sheet with a tab per thing the phone
// manages at the PC: Sessions (#40) and Files (#41); the Queue (#42) was its third tab until 2026-11-21 (the user's ask: the 📣 sheet's first
// tab now, in chat.js, which the ⏳ lines and bare /queue open). Built like the 📣 sheet in chat.js, with its own
// tabs (ManageTabs, kept in sessionStorage under neon.manageTab), so the two never restore each other's tab. Loaded after chat.js as a classic
// deferred script: chat.js's globals (words, post, el, shown, drawerOpen, promptsOpen, usageCard…) are read at call time, and chat.js reaches
// this sheet through window.NeonManage alone (isOpen, hide, close, open, reset, state, has, paintWords): the history entries, Esc, a tap on the
// transcript, the send reply's open, the feed's reset and each state's files and sessions tags.
// Every row text is user or model text, set with textContent through el(). A stored neon.manageTab of queue falls back to Sessions.
'use strict';

(() => {
  const button = document.getElementById('manage-button');
  const sheet = document.getElementById('manage');
  if (!button || !sheet) return;
  const tabEls = [...sheet.querySelectorAll('.manage-tab')];
  const ManageTabs = tabEls.map(t => t.dataset.tab);   // index.html's order: ←/→ step through this
  const closeBtn = document.getElementById('manage-close');
  const note = document.getElementById('manage-note');
  const filter = document.getElementById('manage-filter');
  const list = document.getElementById('manage-list');
  let open = false;
  let opening = false;
  let tab = ManageTabs[0];
  try { if (ManageTabs.includes(sessionStorage.getItem('neon.manageTab'))) tab = sessionStorage.getItem('neon.manageTab'); } catch { /* private mode */ }
  let reading = 0;   // a read's serial, the search's too: an older answer never draws over a newer one (the review's #112)
  let openSerial = 0;   // bumped by hide and close: an open still reading when the sheet was sent away never shows (the review's #132)
  let drawn = null;   // the view last drawn: a 304 bringing it back draws nothing (the review's #91)

  function setTab(t) {
    tab = t;
    try { sessionStorage.setItem('neon.manageTab', t); } catch { /* private mode */ }
  }

  function tabLabel(t) {
    return { sessions: words.sessionsTab || '💬 Sessions', files: words.filesTab || '📄 Files' }[t] || t;
  }

  function paintTabs() {
    for (const t of tabEls) {
      const on = t.dataset.tab === tab;
      t.setAttribute('aria-selected', on ? 'true' : 'false');
      t.tabIndex = on ? 0 : -1;
      t.classList.toggle('on', on);
    }
  }

  function paintWords() {
    const name = words.manageButton || 'Sessions and files';
    button.setAttribute('aria-label', name);
    button.title = name;
    sheet.setAttribute('aria-label', name);
    closeBtn.setAttribute('aria-label', words.closeManage || 'Close');
    closeBtn.title = words.closeManage || 'Close';
    for (const t of tabEls) {
      const label = tabLabel(t.dataset.tab);
      t.querySelector('.tab-glyph').textContent = label.split(' ')[0];
      t.querySelector('.tab-word').textContent = label.split(' ').slice(1).join(' ');
      t.setAttribute('aria-label', label);
      t.title = label;
    }
  }

  function show(on) {
    const was = nearBottom();
    open = on;
    sheet.classList.toggle('hidden', !on);
    if (!on) Neon.disarm();
    keepBottom(was);
    floatSoon();
  }

  function sayNote(text) {
    note.textContent = text || '';
    note.classList.toggle('hidden', !text);
  }

  // ── The Sessions tab (#40) ──
  // The 50 newest, the current one marked and disabled; two taps resume one (its own paint: the row keeps its title for a screen reader and
  // says "tap again to resume" on its second line). The filter is instant over the rows shown, and 300 ms after the last key the same words
  // go to the PC, which answers the title matches across every session and, past three letters, what was said: a later ask's answer never
  // loses to an older one. Rows dim while the PC is busy; a refused resume's reason shows in the note.

  let sessionsView = null;
  let searchTimer = null;
  let lastSessions = null;   // {path, view}: the last answer alone, so a 304 draws it (the review's #29: never one kept per search)

  async function readSessions(q) {
    const path = '/api/sessions' + (q ? '?q=' + encodeURIComponent(q) : '');
    let r = await Neon.get(path, !q);   // a search never asks for a 304: its answer isn't kept
    if (r.ok && !r.json && (!lastSessions || lastSessions.path !== path)) r = await Neon.get(path, false);   // a 304 for a view let go
    if (r.ok && r.json) {
      if (!r.json.found) r.json.found = [];
      lastSessions = { path, view: r.json };
      return { view: r.json, fresh: true };
    }
    return r.ok && lastSessions && lastSessions.path === path ? { view: lastSessions.view, fresh: false } : null;
  }

  function snippetEl(parts) {
    const s = el('div', 'manage-snippet');
    for (const part of parts) s.appendChild(part.match ? el('mark', null, part.text) : document.createTextNode(part.text));
    return s;
  }

  function sessionRow(row, view) {
    const b = el('button', 'manage-row session-row');
    b.type = 'button';
    const title = el('div', 'manage-text', row.title);
    const facts = [row.when, row.turns, row.model].filter(x => x).join(' · ') + (row.current ? ' · ' + (words.sessionsCurrent || 'this conversation') : '');
    const meta = el('div', 'manage-meta', facts);
    b.append(title, meta);
    if (row.snippet && row.snippet.length) b.appendChild(snippetEl(row.snippet));
    b.dataset.text = (row.title + ' ' + row.model).toLowerCase();
    if (row.current) {
      b.disabled = true;
    } else if (view.resumeOffered) {
      Neon.armTwice(b, on => {
        b.classList.toggle('armed', on);
        meta.textContent = on ? (words.sessionsArmed || 'tap again to resume') : facts;
      }, () => resumeSession(row, view));
    } else {
      b.setAttribute('aria-disabled', 'true');
    }
    return b;
  }

  async function resumeSession(row, view) {
    const r = await post('/api/sessions/resume', { id: row.id, profile: view.profile });
    if (r.ok) { close(true); return; }   // the restored conversation replays in view
    sayNote(r.message || '');
  }

  function paintBusy() {
    if (tab !== 'sessions' || !sessionsView) return;
    const busy = state && (state.busy || state.loading);
    list.classList.toggle('dim', !!busy);
    const why = state && state.loading ? (words.sessionsLoading || '') : state && state.busy ? (words.sessionsBusy || '') : '';
    const standing = sessionsView.note || (sessionsView.notSaved ? (words.sessionsNotSaved || '') : '');
    sayNote([why, standing].filter(x => x).join(' '));
  }

  function drawSessions(view) {
    sessionsView = view;
    filter.placeholder = words.sessionsFilter || 'Filter';
    filter.setAttribute('aria-label', words.sessionsFilter || 'Filter');
    const rows = view.rows.map(r => sessionRow(r, view));
    const found = view.found.map(r => sessionRow(r, view));
    filter.classList.toggle('hidden', rows.length + found.length <= 8 && !filter.value);
    const parts = [...rows];
    if (found.length) parts.push(el('div', 'manage-head', words.sessionsFound || 'Found in the conversation'), ...found);
    if (!parts.length) parts.push(el('div', 'manage-none', filter.value.trim() ? (words.sessionsNoMatch || 'Nothing matches.') : (words.sessionsNone || 'No stored sessions.')));
    list.replaceChildren(...parts);
    paintBusy();
  }

  // The instant part: the rows shown, by title and model.
  function filterNow() {
    const q = filter.value.trim().toLowerCase();
    for (const row of list.querySelectorAll('.session-row')) row.classList.toggle('hidden', q.length > 0 && !row.dataset.text.includes(q) && !row.querySelector('.manage-snippet'));
  }

  filter.addEventListener('input', () => {
    if (tab !== 'sessions') return;
    filterNow();
    clearTimeout(searchTimer);
    searchTimer = setTimeout(async () => {
      const serial = ++reading;
      const got = await readSessions(filter.value.trim());
      if (serial !== reading || !open || tab !== 'sessions') return;
      if (!got) { sayNote(words.manageReadFailed || ''); return; }
      draw('sessions', got.view);
    }, 300);
  });

  // ── The Files tab (#41) ──
  // The files the model made in this conversation and still there, newest first: a kind glyph, the name, the folder and the size the
  // server wrote. A row is a link to the url the server built (never assembled here); it opens in a new tab, or in an installed app's own
  // window, which keeps the app's cookie (an iPhone home-screen app's new tab is a Safari view without it). Re-read on each files tag.

  let lastFiles = null;
  let filesTag = null;   // the state's files tag the open Files tab was drawn at
  let sessionsTag = null;   // the state's sessions tag the open Sessions tab was drawn at (the review's #78)

  async function readFiles() {
    const r = await Neon.get('/api/files', true);
    if (r.ok && r.json) { lastFiles = r.json; return { view: lastFiles, fresh: true }; }
    return r.ok && lastFiles ? { view: lastFiles, fresh: false } : null;
  }

  function standalone() {
    try {
      return (window.matchMedia && window.matchMedia('(display-mode: standalone)').matches) || navigator.standalone === true;
    } catch { return false; }
  }

  function fileRow(row) {
    const a = el('a', 'manage-row file-row');
    a.href = row.url;
    if (!standalone()) { a.target = '_blank'; a.rel = 'noopener'; }
    const body = el('div', 'manage-body');
    body.append(el('div', 'manage-text', row.name), el('div', 'manage-meta', [row.folder, row.size].filter(x => x).join(' · ')));
    a.append(el('span', 'manage-glyph', row.glyph), body);
    return a;
  }

  function drawFiles(view) {
    const parts = view.rows.map(fileRow);
    if (!parts.length) parts.push(el('div', 'manage-none', words.filesNone || 'No files made in this conversation yet.'));
    if (view.trimmed) parts.push(el('div', 'manage-head', words.filesTrimmed || ''));
    list.replaceChildren(...parts);
    sayNote('');
    const hint = el('div', 'manage-head files-hint', words.filesHint || '');
    if (view.rows.length && hint.textContent) list.prepend(hint);
  }

  // Each tab's read and draw.
  const Tabs = {
    sessions: { read: () => readSessions(filter.value.trim()), draw: drawSessions },
    files: { read: readFiles, draw: drawFiles },
  };

  async function read(t) {
    const kind = Tabs[t];
    return kind ? await kind.read() : null;
  }

  function draw(t, data) {
    Neon.disarm();
    paintTabs();
    list.classList.remove('dim');
    if (t !== 'sessions') filter.classList.add('hidden');
    drawn = data;
    Tabs[t].draw(data);
  }

  // The state's tags now: each read takes them before it asks, and its tab is stamped with those (the review's #39: a view read before a change
  // was marked current with the change's tag, and never read again).
  function tagsNow() {
    return { files: state ? state.filesTag : null, sessions: state ? state.sessionsTag : null };
  }

  // A read's answer shown: drawn unless a 304 brought back the view drawn already, the tab stamped with the tags taken before the read, and read
  // again (once the caller is done) when the state's moved meanwhile.
  function land(t, got, tags) {
    if (got.fresh || got.view !== drawn) draw(t, got.view);
    if (t === 'files') filesTag = tags.files;
    if (t === 'sessions') sessionsTag = tags.sessions;
    const now = tagsNow();
    if ((t === 'files' && now.files !== filesTag) || (t === 'sessions' && now.sessions !== sessionsTag)) Promise.resolve().then(refresh);
  }

  // The open tab read again and drawn (a new files or sessions tag, the feed's reset).
  async function refresh() {
    if (!open) return;
    const t = tab;
    const serial = ++reading;
    const tags = tagsNow();
    const got = await read(t);
    if (!open || t !== tab || serial !== reading) return;
    if (!got) { sayNote(words.manageReadFailed || ''); return; }   // the rows read last stay (the review's #111)
    land(t, got, tags);
  }

  function focusTab() {
    const at = ManageTabs.indexOf(tab);
    if (at >= 0) tabEls[at].focus({ preventScroll: true });
  }

  async function selectTab(t) {
    if (!ManageTabs.includes(t)) return;
    setTab(t);
    sayNote('');
    filter.value = '';
    paintTabs();
    const serial = ++reading;
    const tags = tagsNow();
    const got = await read(t);
    if (!open || t !== tab || serial !== reading) return;
    if (!got) { drawn = null; sessionsView = null; list.replaceChildren(); filter.classList.add('hidden'); sayNote(words.manageReadFailed || ''); return; }
    land(t, { view: got.view, fresh: true }, tags);   // another tab's rows are showing: always drawn
  }

  // Opens on t, or the tab used last. The 📣 sheet or the drawer, if open, gives its history entry to this one (replaceState), as they
  // swap with each other; anything else pushes a {manage} entry.
  async function openSheet(t) {
    if (t && !ManageTabs.includes(t)) return;
    if (open) {
      if (t && t !== tab) await selectTab(t);
      return;
    }
    if (opening) return;
    if (t) setTab(t);
    filter.value = '';
    opening = true;
    const serial = ++openSerial;
    const want = tab;
    const tags = tagsNow();
    const got = await read(want);
    if (serial !== openSerial) return;   // sent away while it read (an ask, Esc, Back, a tap on the transcript): it never shows
    opening = false;
    if (open) return;
    closeUsage();
    sayNote('');
    if (got) {
      land(want, { view: got.view, fresh: true }, tags);
    } else {
      // Opened all the same, saying why it's empty (the review's #111).
      drawn = null;
      sessionsView = null;
      paintTabs();
      list.replaceChildren();
      filter.classList.add('hidden');
      filesTag = tags.files;
      sessionsTag = tags.sessions;
      sayNote(words.manageReadFailed || '');
    }
    if (promptsOpening && !promptsOpen) hidePrompts(false);   // a 📣 open still reading is called off; it has no entry to hand over
    if ((drawerOpen || promptsOpen) && !shown) {
      hideDrawer();
      hidePrompts(true);
      history.replaceState({ manage: true }, '');
    } else {
      history.pushState({ manage: true }, '');
    }
    show(true);
    focusTab();
  }

  // An open still reading is called off: it pushed no history entry yet, so there is none to take back.
  function stopOpening() {
    if (!opening) return;
    opening = false;
    openSerial++;
  }

  // Gone now, its entry left to the caller. Focus goes back to 🗂️ when asked (Esc, Back, ✕, 🗂️ again), never on a tap outside.
  function hide(toButton) {
    stopOpening();
    if (!open) return;
    const focused = sheet.contains(document.activeElement);
    show(false);
    sayNote('');
    if (toButton && focused) button.focus({ preventScroll: true });
  }

  // ✕, Esc, 🗂️ again, a tap on the transcript, a question arriving: the sheet goes now, and its own history entry with it.
  function close(toButton) {
    stopOpening();
    if (!open) return;
    hide(toButton);
    if (history.state && history.state.manage && !history.state.overlay) history.back();
  }

  button.addEventListener('click', () => { if (open) close(true); else openSheet(); });
  closeBtn.addEventListener('click', () => close(true));
  for (const t of tabEls) {
    t.addEventListener('click', () => { if (t.dataset.tab !== tab) selectTab(t.dataset.tab); });
    // The ARIA tabs' keys: ← and → move between them.
    t.addEventListener('keydown', ev => {
      if (ev.key !== 'ArrowLeft' && ev.key !== 'ArrowRight') return;
      ev.preventDefault();
      const at = (ManageTabs.indexOf(tab) + (ev.key === 'ArrowRight' ? 1 : ManageTabs.length - 1)) % ManageTabs.length;
      selectTab(ManageTabs[at]).then(() => tabEls[at].focus({ preventScroll: true }));
    });
  }
  // Esc: an armed session row first, then the sheet; not under the overlay or the usage card, as the 📣 sheet's.
  document.addEventListener('keydown', ev => {
    if (ev.key !== 'Escape' || ev.defaultPrevented || shown || !(open || opening) || !usageCard.classList.contains('hidden')) return;
    ev.preventDefault();
    if (Neon.armed()) { Neon.disarm(); return; }
    close(true);
  });

  paintWords();

  window.NeonManage = {
    // Open or opening (the review's #85, #86, #129 and #132): a sheet still reading counts, so an ask, Esc or a tap sends it away before it
    // shows. isShown is the sheet on the screen alone, for the history entries: one still reading has none yet.
    isOpen: () => open || opening,
    isShown: () => open,
    has: t => ManageTabs.includes(t),
    open: t => openSheet(t),
    hide: toButton => hide(toButton),
    close: toButton => close(toButton),
    // The feed's reset (a new conversation, a reconnect): an open tab is the new conversation's.
    reset: () => refresh(),
    // Each state: the open Files or Sessions tab re-reads when its tag moved.
    state: s => {
      if (open && tab === 'files' && s && s.filesTag !== filesTag) { filesTag = s.filesTag; refresh(); }
      if (open && tab === 'sessions' && s && s.sessionsTag !== sessionsTag) { sessionsTag = s.sessionsTag; refresh(); }
      if (open && tab === 'sessions') paintBusy();
    },
    paintWords,
  };
})();
