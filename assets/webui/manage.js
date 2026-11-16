// NeonSidekick's Web UI, the 🗂️ sheet (2026-11-16, plan lucid-roaming-lantern, the user's pick): one sheet with a tab per thing the phone
// manages at the PC: Sessions (#40), Files (#41) and the Queue (#42). Built like the 📣 sheet in chat.js, with its own
// tabs (ManageTabs, kept in sessionStorage under neon.manageTab), so the two never restore each other's tab. Loaded after chat.js as a classic
// deferred script: chat.js's globals (words, post, el, shown, drawerOpen, promptsOpen, usageCard…) are read at call time, and chat.js reaches
// this sheet through window.NeonManage alone (isOpen, hide, close, open, reset, state, has, paintWords): the history entries, Esc, a tap on the
// transcript, the send reply's open, the feed's reset and each state's queue tag.
// Every row text is user or model text, set with textContent through el(). The Queue tab: the PC's waiting lines and the pages' own, a ✕ that
// takes one back in two taps (a page's line goes to the draft), ➤ Send now and Resume only when they apply, and the two queue modes, the one
// in force marked. It re-reads when the state's queue tag moves.
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
  let queueTag = null;   // the state's queue tag the open Queue tab was drawn at
  let reading = 0;   // a read's serial: an older answer never draws over a newer one

  function setTab(t) {
    tab = t;
    try { sessionStorage.setItem('neon.manageTab', t); } catch { /* private mode */ }
  }

  function tabLabel(t) {
    return { sessions: words.sessionsTab || '💬 Sessions', files: words.filesTab || '📄 Files', queue: words.queueTab || '⏳ Queue' }[t] || t;
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
    const name = words.manageButton || 'Sessions, files and queue';
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

  // ── The Queue tab ──

  let lastQueue = null;   // the view last read: a 304 draws it again

  async function readQueue() {
    const r = await Neon.get('/api/queue', true);
    if (r.ok && r.json) lastQueue = r.json;   // a 304 keeps the view the page holds
    return r.ok && lastQueue ? lastQueue : null;
  }

  // A ✕: two taps, as the 📣 sheet's 🗑️ (its own class, so the 📣 sheet's tests never meet it).
  function removeButton(onConfirm) {
    const b = el('button', 'manage-remove', '✕');
    b.type = 'button';
    Neon.armTwice(b, on => {
      b.classList.toggle('armed', on);
      b.textContent = on ? (words.removeRowArmed || 'Take back?') : '✕';
      const text = on ? (words.removeRowArmed || 'Take back?') : (words.removeRow || 'Take back');
      b.setAttribute('aria-label', text);
      b.title = text;
    }, onConfirm);
    return b;
  }

  function queueRow(text, pictures, remove, chord) {
    const row = el('div', 'manage-row' + (chord ? ' chord' : ''));
    const label = el('div', 'manage-text', text.replace(/\s+/g, ' '));
    label.title = chord ? (words.queueChordLine || '') : text;
    row.appendChild(label);
    if (pictures > 0) row.appendChild(el('span', 'manage-pictures', '🖼️ ' + pictures));
    if (remove) row.appendChild(removeButton(remove));
    return row;
  }

  async function queueAct(path, body) {
    const r = await post(path, body);
    sayNote(r.message || '');
    await refresh();
    return r;
  }

  function choiceRow(label, names, current, path, field) {
    const row = el('div', 'manage-choices');
    row.setAttribute('role', 'group');
    row.setAttribute('aria-label', label);
    row.appendChild(el('span', 'manage-choice-label', label));
    for (const name of names) {
      const b = el('button', 'manage-choice' + (name === current ? ' on' : ''), name);
      b.type = 'button';
      b.setAttribute('aria-pressed', name === current ? 'true' : 'false');
      b.addEventListener('click', () => { if (name !== current) queueAct(path, { [field]: name }); });
      row.appendChild(b);
    }
    return row;
  }

  function drawQueue(view) {
    const parts = [];
    const pc = view.pc || [];
    const page = view.page || [];
    if (!pc.length && !page.length) parts.push(el('div', 'manage-none', words.queueNone || 'Nothing waiting.'));
    if (pc.length) {
      parts.push(el('div', 'manage-head', words.queueAtPc || 'At the PC'));
      for (const line of pc) {
        parts.push(queueRow(line.label, line.pictures || 0, () => queueAct('/api/queue/remove', { index: line.index, label: line.label }), false));
      }
    }
    if (page.length) {
      parts.push(el('div', 'manage-head', words.queueFromPage || 'From this page'));
      for (const line of page) {
        const removable = !line.chord && line.key > 0;
        parts.push(queueRow(line.text, line.pictures || 0, removable ? () => queueAct('/api/queue/remove', { key: line.key }) : null, line.chord));
      }
    }
    const actions = el('div', 'manage-actions');
    if (view.sendOffered) {
      const send = el('button', 'manage-send', words.queueSendNow || '➤ Send now');
      send.type = 'button';
      send.addEventListener('click', () => queueAct('/api/queue/send', {}));
      actions.appendChild(send);
    }
    if (view.held) {
      const resume = el('button', 'manage-resume', words.resume || 'Resume');
      resume.type = 'button';
      resume.addEventListener('click', () => queueAct('/api/queue/resume', {}));
      actions.appendChild(resume);
    }
    if (actions.childElementCount) parts.push(actions);
    parts.push(choiceRow(words.queueModeLabel || 'Queue mode', view.modes || [], view.mode, '/api/queue/mode', 'mode'));
    parts.push(choiceRow(words.queueCancelLabel || 'Queue cancel mode', view.cancelModes || [], view.cancelMode, '/api/queue/cancel', 'cancel'));
    list.replaceChildren(...parts);
  }

  // ── The Sessions tab (#40) ──
  // The 50 newest, the current one marked and disabled; two taps resume one (its own paint: the row keeps its title for a screen reader and
  // says "tap again to resume" on its second line). The filter is instant over the rows shown, and 300 ms after the last key the same words
  // go to the PC, which answers the title matches across every session and, past three letters, what was said: a later ask's answer never
  // loses to an older one. Rows dim while the PC is busy; a refused resume's reason shows in the note.

  let sessionsView = null;
  let searchSerial = 0;
  let searchTimer = null;

  async function readSessions(q) {
    const path = '/api/sessions' + (q ? '?q=' + encodeURIComponent(q) : '');
    const r = await Neon.get(path, true);
    if (r.ok && r.json) {
      if (!r.json.found) r.json.found = [];
      cache.set(path, r.json);
    }
    return r.ok ? cache.get(path) || null : null;
  }
  const cache = new Map();   // per ask: a 304 draws the view it last answered

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
      const serial = ++searchSerial;
      const view = await readSessions(filter.value.trim());
      if (!view || serial !== searchSerial || !open || tab !== 'sessions') return;
      drawSessions(view);
    }, 300);
  });

  // ── The Files tab (#41) ──
  // The files the model made in this conversation and still there, newest first: a kind glyph, the name, the folder and the size the
  // server wrote. A row is a link to the url the server built (never assembled here); it opens in a new tab, or in an installed app's own
  // window, which keeps the app's cookie (an iPhone home-screen app's new tab is a Safari view without it). Re-read on each files tag.

  let lastFiles = null;
  let filesTag = null;   // the state's files tag the open Files tab was drawn at

  async function readFiles() {
    const r = await Neon.get('/api/files', true);
    if (r.ok && r.json) lastFiles = r.json;
    return r.ok && lastFiles ? lastFiles : null;
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
    queue: { read: readQueue, draw: drawQueue },
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
    if (t === 'queue') queueTag = state ? state.queueTag : null;
    if (t === 'files') filesTag = state ? state.filesTag : null;
    Tabs[t].draw(data);
  }

  // The open tab read again and drawn (a change made here, a new queue tag, the feed's reset); an armed ✕ survives only a 304.
  async function refresh() {
    if (!open) return;
    const t = tab;
    const serial = ++reading;
    const data = await read(t);
    if (!data || !open || t !== tab || serial !== reading) return;
    draw(t, data);
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
    const data = await read(t);
    if (!data || !open || t !== tab || serial !== reading) return;
    draw(t, data);
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
    const want = tab;
    const data = await read(want);
    opening = false;
    if (!data || open) return;
    closeUsage();
    sayNote('');
    draw(want, data);
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

  // Gone now, its entry left to the caller. Focus goes back to 🗂️ when asked (Esc, Back, ✕, 🗂️ again), never on a tap outside.
  function hide(toButton) {
    if (!open) return;
    const focused = sheet.contains(document.activeElement);
    show(false);
    sayNote('');
    if (toButton && focused) button.focus({ preventScroll: true });
  }

  // ✕, Esc, 🗂️ again, a tap on the transcript, a question arriving: the sheet goes now, and its own history entry with it.
  function close(toButton) {
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
  // Esc: an armed ✕ first, then the sheet; not under the overlay or the usage card, as the 📣 sheet's.
  document.addEventListener('keydown', ev => {
    if (ev.key !== 'Escape' || ev.defaultPrevented || shown || !open || !usageCard.classList.contains('hidden')) return;
    ev.preventDefault();
    if (Neon.armed()) { Neon.disarm(); return; }
    close(true);
  });

  // The ⏳ lines under the transcript open the Queue tab, by a tap or by Enter or Space.
  const waiting = document.getElementById('queued');
  if (waiting && ManageTabs.includes('queue')) {
    waiting.addEventListener('click', () => openSheet('queue'));
    waiting.addEventListener('keydown', ev => {
      if (ev.key !== 'Enter' && ev.key !== ' ') return;
      ev.preventDefault();
      openSheet('queue');
    });
  }

  paintWords();

  window.NeonManage = {
    isOpen: () => open,
    has: t => ManageTabs.includes(t),
    open: t => openSheet(t),
    hide: toButton => hide(toButton),
    close: toButton => close(toButton),
    // The feed's reset (a new conversation, a reconnect): an open tab is the new conversation's.
    reset: () => refresh(),
    // Each state: the open Queue or Files tab re-reads when its tag moved.
    state: s => {
      if (open && tab === 'queue' && s && s.queueTag !== queueTag) { queueTag = s.queueTag; refresh(); }
      if (open && tab === 'files' && s && s.filesTag !== filesTag) { filesTag = s.filesTag; refresh(); }
      if (open && tab === 'sessions') paintBusy();
    },
    paintWords,
  };
})();
