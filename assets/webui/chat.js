// NeonSidekick's Web UI, the chat page (2026-10-14): the transcript as the app draws it, streamed over server-sent events (a snapshot on
// every (re)connect, then the changes), the page's own draft line, Stop, and the asks the app's panes put — answered here or at the PC,
// the first answer winning. Every word the asks show comes from the app; text is set with textContent, and the only HTML is a finished
// reply's, which the app renders with raw HTML escaped and the links vetted.
// Pictures (later on 2026-10-14): a picture entry carries the addresses the server built; the grid loads them lazily, three at a time, and a
// tap opens the overlay (the middle size, swipe or ‹ › for the others, Back closes it). The page reloads itself when the app serves other
// files than the ones it runs (an upgrade), once per tag.
// Later on 2026-10-15: the tabs on (this page leaves when Chat is turned off), a ✕ that clears the draft with an Undo, the floating buttons
// (expand or collapse every fold, to the top, to the bottom) and the PC's picture strip in a drawer from a 🎞️ button; the history entries
// say what each holds, so Back, Esc and a late pop close the right thing.
'use strict';

const logEl = document.getElementById('log');
const draft = document.getElementById('draft');
const sendBtn = document.getElementById('send');
const stopBtn = document.getElementById('stop');
const asksEl = document.getElementById('asks');
const waitingEl = document.getElementById('waiting');
const queuedEl = document.getElementById('queued');
const heldEl = document.getElementById('queue-held');
const resumeBtn = document.getElementById('queue-resume');
const completeEl = document.getElementById('complete');
const profileEl = document.getElementById('profile');
const modelEl = document.getElementById('model');

const overlayEl = document.getElementById('overlay');
const overlayImg = document.getElementById('overlay-img');
const overlayName = document.getElementById('overlay-name');
const overlayOpen = document.getElementById('overlay-open');
const overlayPrev = document.getElementById('overlay-prev');
const overlayNext = document.getElementById('overlay-next');
const overlayClose = document.getElementById('overlay-close');
const overlayLive = document.getElementById('overlay-live');
const overlayDelete = document.getElementById('overlay-delete');
const overlayNote = document.getElementById('overlay-note');

const desktop = window.matchMedia('(pointer: fine)').matches;
const entries = new Map();   // seq → { el, body, kind, text, pictures }
let words = {
  pictureGone: 'is no longer there.', pictureTooLarge: 'is too large to show here.', openOriginal: 'Open original',
  sendAsMessage: 'Send as a message', useLine: 'Use', draftFilled: 'Put on the draft.', undo: 'Undo', working: 'working', close: 'Close',
  noReply: '(no reply)', thinkingLive: '💭 thinking…', gone: '(no longer available)',
  clearDraft: 'Clear the draft', draftCleared: 'Draft cleared.', expandAll: 'Expand all', collapseAll: 'Collapse all', toTop: 'To the top',
  toBottom: 'To the bottom', stripButton: 'Pictures', stripOne: '1 picture', stripMany: '{0} pictures', closeStrip: 'Close the pictures',
  live: 'live', liveLabel: 'following new pictures',
};
// Expand all (later on 2026-10-15, the PC's Ctrl+O on the page alone): a mode while on, kept for the tab, so folds that arrive start open.
let expandAll = false;
try { expandAll = sessionStorage.getItem('neon.expandAll') === '1'; } catch { /* private mode */ }
let state = null;
let asks = [];
let source = null;
let pendingId = null;
let pendingText = null;
let lastAskCount = 0;

// The shared helpers (common.js, 2026-10-15): the element maker, the post, the sentence under the composer with its buttons.
const { el, post } = Neon;
const say = text => Neon.say(text);
const sayWith = Neon.say;

function nearBottom() {
  return window.innerHeight + window.scrollY >= document.body.scrollHeight - 80;
}

function keepBottom(was) {
  if (was) window.scrollTo(0, document.body.scrollHeight);
}

function newId() {
  return Date.now().toString(36) + Math.random().toString(36).slice(2, 10);
}

// ── The transcript ────────────────────────────────────────────────────

// What the terminal shows besides (2026-10-15): the thinking (streamed, then folded under its summary), an edit's diff, the tool runs folded
// under their summary past Tool collapse count, a /cmd run's output, and "(no reply)". A fold the page makes on its own keeps the reading place.

function renderEntry(entry) {
  let node;
  const record = { kind: entry.kind, text: entry.text, body: null, el: null };
  switch (entry.kind) {
    case 'separator':
      node = el('hr', 'entry separator');
      break;
    case 'assistant': {
      node = el('div', 'entry assistant');
      // A stretch after a break, once the reply had spoken, reads on with no glyph, as the PC's flush-left text does.
      node.appendChild(el('span', 'glyph' + (entry.continues ? ' none' : ''), entry.glyph || '●'));
      const body = el('div', 'body');
      if (entry.noReply) {
        body.classList.add('noreply');
        body.textContent = words.noReply || '(no reply)';
      } else if (entry.html) {
        body.innerHTML = entry.html;
      } else {
        body.classList.add('streaming');
        body.textContent = entry.text;
      }
      node.appendChild(body);
      record.body = body;
      break;
    }
    case 'thinking':
      node = el('div', 'entry thinking');
      if (entry.ended || entry.summary) {
        node.appendChild(foldedThinking({ ...entry }, record));
      } else {
        node.appendChild(el('div', 'think-head', words.thinkingLive || '💭 thinking…'));
        record.body = el('div', 'think-body', entry.text);
        node.appendChild(record.body);
      }
      break;
    case 'output':
      node = el('pre', 'entry output' + (entry.failed ? ' failed' : ''), entry.text);
      record.body = node;
      break;
    case 'run': {
      node = el('div', 'entry run' + (entry.failed ? ' failed' : ''));
      const head = el('button', 'run-head hidden');
      head.type = 'button';
      const body = el('div', 'run-body');
      node.appendChild(head);
      node.appendChild(body);
      record.run = { head, body, keep: entry.keep || 0, units: [], ended: !!entry.ended, expanded: expandAll, text: entry.text };
      head.addEventListener('click', () => { record.run.expanded = !record.run.expanded; applyRun(record.run); floatSoon(); });
      applyRun(record.run);
      break;
    }
    case 'user': {
      node = el('div', 'entry user');
      node.appendChild(el('span', 'glyph', (state && state.userAvatar) || '›'));
      node.appendChild(document.createTextNode(entry.text));
      break;
    }
    case 'picture':
      if (entry.pictures && entry.pictures.length) {
        // The welcome splash (2026-10-18, the user's ask): first on the page, centered and filling it as on the PC (fitSplash), until the
        // first sent line takes it out; a lone picture at its middle size (the tiled splash, thumbnails, went on 2026-10-20; the robot has no entry).
        node = el('div', 'entry pictures' + (entry.splash ? ' splash' : ''));
        const lone = !!entry.splash && entry.pictures.length === 1;
        const grid = el('div', 'grid' + (lone ? ' lone' : ''));
        entry.pictures.forEach(p => grid.appendChild(pictureTile(p, lone ? p.mid : p.thumb)));
        node.appendChild(grid);
        record.pictures = entry.pictures;
      } else {
        node = el('div', 'entry picture', '🖼️ ' + entry.text);
      }
      break;
    default:
      node = el('div', 'entry ' + entry.kind + (entry.failed ? ' failed' : ''), entry.text);
      if (entry.diff) {
        const block = diffBlock(entry.diff);
        node.appendChild(block);
        // A long folded diff a reconnect left out (2026-10-15): its rows read when it is opened.
        const fold = block.querySelector('details');
        if (entry.deferred && fold) whenOpened(fold, entry.seq, whole => { if (whole.diff) block.replaceWith(diffBlock(whole.diff, true)); });
      }
  }
  record.el = node;
  entries.set(entry.seq, record);
  const run = entry.run !== undefined && entry.run !== null ? entries.get(entry.run) : null;
  if (run && run.run) {
    // A tool line joins its run: a unit of its own, or the last one when it completes the call before it.
    const units = run.run.units;
    let unit = entry.joins && units.length ? units[units.length - 1] : null;
    if (!unit) {
      unit = el('div', 'unit');
      units.push(unit);
      run.run.body.appendChild(unit);
    }
    unit.appendChild(node);
    applyRun(run.run);
  } else {
    logEl.appendChild(node);   // no run, or its head was trimmed away: on its own
  }
}

// A run's head shows once it holds more than keep units. While it runs only the last keep show; once over, a long one folds to its head.
function applyRun(run) {
  const n = run.units.length;
  const long = run.keep > 0 && n > run.keep;
  run.head.classList.toggle('hidden', !long);
  run.head.textContent = (run.expanded ? '▾ ' : '▸ ') + run.text;
  run.head.setAttribute('aria-expanded', run.expanded ? 'true' : 'false');
  run.units.forEach((unit, i) => {
    const hidden = long && !run.expanded && (run.ended || i < n - run.keep);
    unit.classList.toggle('hidden', hidden);
  });
}

function foldedThinking(entry, record) {
  const box = el('details', 'think-fold');
  box.appendChild(el('summary', null, entry.summary || ''));
  const text = el('div', 'think-body');
  text.textContent = entry.text;
  box.appendChild(text);
  record.body = null;
  record.fold = box;
  // A reconnect's snapshot sent the summary alone (2026-10-15): the text read when it is opened, or a line saying it went.
  if (entry.deferred) whenOpened(box, entry.seq, whole => { text.textContent = whole.text; });
  box.open = expandAll;
  return box;
}

// Reads a deferred entry the first time its fold opens; one the transcript let go says so. Two reads at a time, the nearest the view first
// (later on 2026-10-15: expand-all after a reconnect would start dozens at once and a send would queue behind them); a fold closed again
// before its turn leaves the queue and is read when it opens again.
const entryQueue = [];
let entryReads = 0;
const MaxEntryReads = 2;

function whenOpened(details, seq, fill) {
  let asked = false;
  details.addEventListener('toggle', () => {
    if (!details.open || asked) return;
    asked = true;
    entryQueue.push({ details, seq, fill, again: () => { asked = false; } });
    pumpEntries();
  });
}

function distanceFromView(node) {
  const r = node.getBoundingClientRect();
  return r.bottom < 0 ? -r.bottom : r.top > window.innerHeight ? r.top - window.innerHeight : 0;
}

function pumpEntries() {
  while (entryReads < MaxEntryReads && entryQueue.length) {
    entryQueue.sort((a, b) => distanceFromView(a.details) - distanceFromView(b.details));
    const job = entryQueue.shift();
    if (!job.details.isConnected) continue;
    if (!job.details.open) { job.again(); continue; }
    entryReads++;
    Neon.get('/api/entry/' + job.seq, false).then(r => {
      if (r.ok && r.json) job.fill(r.json);
      else job.details.appendChild(el('div', 'gone', words.gone || '(no longer available)'));
    }).finally(() => {
      entryReads--;
      pumpEntries();
    });
  }
}

// An edit's diff as the PC draws it: the head, the rows (a gap between two hunks), the cut's last line, folded past Diff collapse count.
function diffBlock(d, open) {
  const box = el('div', 'diff');
  box.appendChild(el('div', 'diff-head', '└ ' + d.head));
  const rows = el('div', 'diff-rows');
  const width = Math.max(1, ...(d.rows || []).filter(r => r).map(r => String(r.number).length));
  for (const r of d.rows || []) {
    if (!r) { rows.appendChild(el('div', 'diff-gap', '⋮'.padStart(width))); continue; }
    const row = el('div', 'diff-row' + (r.sign === '+' ? ' add' : r.sign === '-' ? ' del' : ''));
    row.appendChild(el('span', 'num', String(r.number).padStart(width)));
    row.appendChild(el('span', 'sign', r.sign));
    row.appendChild(el('span', 'code', r.text));
    rows.appendChild(row);
  }
  if (d.more) rows.appendChild(el('div', 'diff-more', d.more));
  if (d.fold > 0) {
    const fold = el('details', 'diff-fold');
    fold.open = !!open || expandAll;
    fold.appendChild(el('summary', null, d.foldSummary));
    fold.appendChild(rows);
    box.appendChild(fold);
  } else {
    box.appendChild(rows);
  }
  return box;
}

// A change the page makes on its own (a run or a thinking block folding) keeps the reading place: near the bottom it stays at the bottom, and
// a change above the view moves the view by as much (Safari has no scroll anchoring of its own).
function keepPlace(node, change) {
  const was = nearBottom();
  const top = node.getBoundingClientRect().top;
  const height = node.offsetHeight;
  change();
  if (was) keepBottom(true);
  else if (top < 0) window.scrollBy(0, node.offsetHeight - height);
}

function append(entry) {
  if (entries.has(entry.seq)) return;
  const was = nearBottom();
  renderEntry(entry);
  fitSplash();
  keepBottom(was);
  floatSoon();
  if (shown && !shown.fromStrip && entry.pictures && entry.pictures.length) refreshOverlay();
}

function delta(d) {
  const record = entries.get(d.seq);
  if (!record || !record.body) { reconnect(); return; }
  if (d.at === record.text.length) {
    record.text += d.text;
    const was = nearBottom();
    record.body.textContent = record.text;
    if (record.kind === 'thinking') record.body.scrollTop = record.body.scrollHeight;   // the box follows the stream
    keepBottom(was);
    floatSoon();
  } else if (d.at + d.text.length > record.text.length) {
    reconnect();   // a gap: start again from a snapshot
  }
}

function end(e) {
  const record = entries.get(e.seq);
  if (!record) return;
  floatSoon();
  if (record.run) {
    record.run.ended = true;
    keepPlace(record.el, () => applyRun(record.run));
    return;
  }
  if (record.kind === 'thinking') {
    keepPlace(record.el, () => record.el.replaceChildren(foldedThinking({ text: record.text, summary: e.summary }, record)));
    return;
  }
  if (record.kind !== 'assistant' || !record.body) return;
  const was = nearBottom();
  record.body.classList.remove('streaming');
  record.body.innerHTML = e.html;
  keepBottom(was);
}

// A run's summary rewritten: a call counted, a result failed.
function replaceRun(r) {
  const record = entries.get(r.seq);
  if (!record || !record.run) return;
  record.run.text = r.text;
  record.el.classList.toggle('failed', !!r.failed);
  applyRun(record.run);
}

function reset() {
  entries.clear();
  logEl.replaceChildren();
  loadQueue.length = 0;
  entryQueue.length = 0;
  if (shown && !shown.fromStrip) closeOverlay();
  floatSoon();
}

function removeEntry(seq) {
  const record = entries.get(seq);
  if (!record) return;
  record.el.remove();
  entries.delete(seq);
  fitSplash();
  floatSoon();
  if (shown && !shown.fromStrip && record.pictures && record.pictures.length) refreshOverlay();
}

// ── Pictures ──────────────────────────────────────────────────────────

const loadQueue = [];
let loadingPictures = 0;
const MaxLoads = 3;   // Send and Stop keep a connection of the browser's six

const seen = 'IntersectionObserver' in window
  ? new IntersectionObserver(items => {
    for (const item of items) {
      if (!item.isIntersecting) continue;
      seen.unobserve(item.target);
      loadQueue.push(item.target);
    }
    pump();
  }, { rootMargin: '600px 0px' })
  : null;

function pump() {
  while (loadingPictures < MaxLoads && loadQueue.length) {
    const img = loadQueue.shift();
    if (!img.isConnected || img.src) continue;
    loadingPictures++;
    let done = false;
    const finish = failed => {
      if (done) return;
      done = true;
      loadingPictures--;
      if (failed) pictureFailed(img);
      pump();
    };
    img.addEventListener('load', () => finish(false), { once: true });
    img.addEventListener('error', () => finish(true), { once: true });
    img.src = img.dataset.src;
  }
}

function pictureTile(p, src) {
  const tile = el('button', 'tile');
  tile.type = 'button';
  tile.title = p.name;
  if (p.key) tile.dataset.key = p.key;   // the 🗑️ finds a file's tiles by it
  if (p.tooLarge) {
    tile.classList.add('fallback');
    tile.textContent = p.name + ' ' + words.pictureTooLarge;
    tile.addEventListener('click', () => window.open(p.full, '_blank', 'noopener'));
    return tile;
  }
  const img = el('img');
  img.alt = p.name;
  img.loading = 'lazy';
  img.decoding = 'async';
  if (p.width > 0 && p.height > 0) {
    // The room kept before it loads, so the scroll stays at the bottom.
    img.width = p.width;
    img.height = p.height;
  }
  img.dataset.src = src || p.thumb;
  img.dataset.full = p.full;
  tile.appendChild(img);
  if (src && src !== p.thumb) img.addEventListener('load', fitSplash, { once: true });   // the splash's picture, fitted once its size is known
  tile.addEventListener('click', () => openOverlay(p.id));
  if (seen) seen.observe(img); else { loadQueue.push(img); pump(); }
  return tile;
}

// A picture that did not load: once more as its original (later on 2026-10-15: a Mac's server makes no thumbnails, and every browser draws
// ComfyUI's PNGs), then its name.
function pictureFailed(img) {
  const tile = img.closest('.tile, .strip-tile');
  if (!tile) return;
  if (img.dataset.full && !img.dataset.triedFull) {
    img.dataset.triedFull = '1';
    img.classList.add('original');
    img.addEventListener('error', () => pictureFailed(img), { once: true });
    img.src = img.dataset.full;
    return;
  }
  tile.classList.add('fallback');
  tile.replaceChildren(document.createTextNode(img.alt + (tile.classList.contains('tile') ? ' ' + words.pictureGone : '')));
}

// ── The overlay ───────────────────────────────────────────────────────

let shown = null;   // { list, at, fromStrip }

// ── The PC's point (later on 2026-10-15, the user's ask) ──
// The page's selection follows the PC's as the PC's follows the page's: a point (a click or an arrow on the PC's strip, its viewer's keys, a pick
// in its thumbnail browser, the viewer opened on a picture) moves an open full view to the picture with that file's key, as a step would; a view
// on the newest moved to an older one holds there, and a point at the newest makes it live again (isLive reads the slot). A pick still pending
// from a swipe is dropped, so the page never sends the PC back. A closed view, or a file the view doesn't hold, moves nothing. The sequence
// only rises: a fresh page takes the snapshot's without moving; a reconnect follows one newer than it had.
let lastPointSeq = null;

function applyPoint(p) {
  if (!p || (lastPointSeq !== null && p.seq <= lastPointSeq)) return;
  lastPointSeq = p.seq;
  if (!shown) return;
  const at = shown.list.findIndex(x => x.key === p.key);
  if (at < 0) return;
  clearTimeout(selectTimer);
  if (at === shown.at) return;
  shown.at = at;
  showPicture();
}

function allPictures() {
  const list = [];
  for (const record of entries.values()) for (const p of record.pictures || []) list.push(p);
  return list;
}

// The transcript's pictures newest first (later on 2026-10-15, the user's report: the view opened from the transcript ran oldest first, so its
// live picture sat at the far right while the strip's and the PC's viewer keep it at the far left): ‹ newer, › older, live always the first.
function transcriptPictures() {
  return allPictures().filter(p => !p.tooLarge && !(p.key && goneKeys.has(p.key))).reverse();
}

// The strip's pictures the overlay can show: none over the decode, none the page deleted.
function stripPictures() {
  return strip ? strip.pictures.filter(p => !p.tooLarge && !(p.key && goneKeys.has(p.key))) : [];
}

function openOverlay(id) {
  openOverlayOn(transcriptPictures(), id, false);
}

// The overlay over a list of pictures: the transcript's, or the strip's (fromStrip: each step moves the PC's viewer too). Its history entry
// says the drawer is open under it, so a Back lands on the drawer and a late pop closes nothing else.
function openOverlayOn(list, id, fromStrip) {
  const at = list.findIndex(p => p.id === id);
  if (at < 0) return;
  const wasOpen = shown !== null;
  shown = { list, at, fromStrip };
  showPicture();
  overlayEl.classList.remove('hidden');
  if (!wasOpen) history.pushState({ overlay: true, drawer: drawerOpen, prompts: promptsOpen }, '');
  floatSoon();
}

function showPicture() {
  const p = shown.list[shown.at];
  if (armed && armed.id !== p.id) disarmDelete();   // a step, a swipe, a live swap, the PC's point: never a second tap on another picture
  overlayImg.alt = p.name;
  delete overlayImg.dataset.tried;
  overlayImg.src = p.mid;
  overlayName.textContent = p.name;
  overlayOpen.textContent = words.openOriginal;
  overlayOpen.href = p.full;
  paintOverlayBar();
}

// The arrows and the live tag, for the picture shown now (a refresh that kept its picture repaints this alone).
function paintOverlayBar() {
  overlayPrev.disabled = shown.at === 0;
  overlayNext.disabled = shown.at === shown.list.length - 1;
  overlayLive.textContent = words.live || 'live';
  overlayLive.classList.toggle('hidden', !isLive());
  paintDelete();
}

// ── The 🗑️ (later on 2026-10-15, the user's ask) ──
// The PC viewer's double-Del, on a phone: a picture with a file of its own shows 🗑️; the first tap arms it (red, its label says so) for 3 s, the
// second on the same picture deletes it as File deletes to Recycle bin says. A step, a swipe, a live swap, the PC's point or the overlay closing
// disarms it. Deleted, the view moves to the next older picture (the newer when it was the oldest) or closes; every tile of that file shows its
// fallback, and the page keeps the file's key so no refresh puts it back (a snapshot starts the page over). Delete does it twice on a desktop.
const goneKeys = new Set();
let armed = null;   // { id, timer }
let noteTimer = null;

function paintDelete() {
  const p = shown && shown.list[shown.at];
  overlayDelete.classList.toggle('hidden', !(p && p.deletable));
  const on = !!(armed && p && armed.id === p.id);
  overlayDelete.classList.toggle('armed', on);
  const label = on ? (words.deleteArmed || 'Tap again to delete') : (words.deletePicture || 'Delete');
  overlayDelete.setAttribute('aria-label', label);
  overlayDelete.title = label;
}

function disarmDelete() {
  if (!armed) return;
  clearTimeout(armed.timer);
  armed = null;
  if (shown) paintDelete();
}

function overlayNoteSay(text) {
  clearTimeout(noteTimer);
  overlayNote.textContent = text || '';
  overlayNote.classList.toggle('hidden', !text);
  if (text) noteTimer = setTimeout(() => overlayNote.classList.add('hidden'), 3000);
}

async function pressDelete() {
  if (!shown) return;
  const p = shown.list[shown.at];
  if (!p || !p.deletable) return;
  if (!armed || armed.id !== p.id) {
    disarmDelete();
    armed = { id: p.id, timer: setTimeout(disarmDelete, 3000) };
    paintDelete();
    return;
  }
  disarmDelete();
  const r = await post('/api/picture/delete', { id: p.id });
  overlayNoteSay(r.message);
  if (!r.ok) return;
  if (p.key) {
    goneKeys.add(p.key);
    for (const tile of document.querySelectorAll('.tile[data-key="' + p.key + '"]')) {
      tile.classList.add('fallback');
      tile.replaceChildren(document.createTextNode(p.name + ' ' + words.pictureGone));
    }
  }
  if (!shown) return;
  const list = shown.list.filter(x => x.id !== p.id && !(p.key && x.key === p.key));
  if (!list.length) { closeOverlay(); return; }
  shown.list = list;
  shown.at = Math.min(shown.at, list.length - 1);
  showPicture();
}

overlayDelete.addEventListener('click', ev => { ev.stopPropagation(); pressDelete(); });

// ── Live (later on 2026-10-15) ──
// As the PC's viewer: on the newest slot, the first (the far left, in the strip's order and the transcript's alike), the overlay follows new
// pictures; a step off it holds the shown picture, and a step back onto it follows again. A live swap sends the PC no pick (its viewer follows on
// its own) and drops a pick still pending from a step, which would move the PC to a picture the page no longer shows.
function isLive() {
  return !!shown && shown.at === 0;
}

let liveWait = 0;

function refreshOverlay(waited) {
  if (!shown) return;
  const list = shown.fromStrip ? stripPictures() : transcriptPictures();
  if (!list.length) {
    closeOverlay();
    return;
  }
  const current = shown.list[shown.at];
  const live = isLive();
  let at;
  if (live) {
    at = 0;
    // The shown picture removed (the splash cycling: a remove, then an append): a frame's wait, so the append lands first and the one before
    // never flashes.
    if (!waited && !shown.fromStrip && list.findIndex(p => p.id === current.id) < 0) {
      if (!liveWait) {
        liveWait = requestAnimationFrame(() => {
          liveWait = 0;
          refreshOverlay(true);
        });
      }
      return;   // nothing moves until then
    }
  } else {
    at = list.findIndex(p => p.id === current.id);
    if (at < 0) at = Math.min(shown.at, list.length - 1);
  }
  shown.list = list;
  shown.at = at;
  if (list[at].id !== current.id) {
    if (live) clearTimeout(selectTimer);
    showPicture();
  } else {
    paintOverlayBar();
  }
}

function step(by) {
  if (!shown) return;
  const at = shown.at + by;
  if (at < 0 || at >= shown.list.length) return;
  shown.at = at;
  showPicture();
  if (shown.fromStrip) selectOnPc(shown.list[at].id, false);
}

// Every close goes through the history entry the open pushed, so Back, ✕, Esc and a tap end the same way.
function closeOverlay() {
  if (!shown) return;
  if (history.state && history.state.overlay) history.back(); else hideOverlay();
}

function hideOverlay() {
  disarmDelete();
  overlayNoteSay('');
  shown = null;
  overlayEl.classList.add('hidden');
  overlayImg.removeAttribute('src');
  floatSoon();
}

// The overlay's middle size is a re-encode the server can't make on a Mac: once more as the original.
overlayImg.addEventListener('error', () => {
  if (!shown || overlayImg.dataset.tried) return;
  overlayImg.dataset.tried = '1';
  overlayImg.src = shown.list[shown.at].full;
});

// Back, and every close that goes through the history: the page set to what the entry says it holds (later on 2026-10-15). An entry whose
// panel went meanwhile (a new chat, a question arriving, the strip gone) is stepped over, so the next Back leaves the page as it should.
window.addEventListener('popstate', settleHistory);
function settleHistory() {
  const st = history.state || {};
  if (!st.overlay && shown) hideOverlay();
  if (!st.drawer && drawerOpen) hideDrawer();
  if (!st.prompts && promptsOpen) hidePrompts(false);
  if (!shown && (st.overlay || (st.drawer && !drawerOpen) || (st.prompts && !promptsOpen))) history.back();
}
overlayClose.addEventListener('click', closeOverlay);
overlayPrev.addEventListener('click', ev => { ev.stopPropagation(); step(-1); });
overlayNext.addEventListener('click', ev => { ev.stopPropagation(); step(1); });
overlayEl.addEventListener('click', ev => { if (ev.target === overlayEl || ev.target === overlayImg) closeOverlay(); });
overlayOpen.addEventListener('click', ev => ev.stopPropagation());
document.addEventListener('keydown', ev => {
  if (!shown) return;
  if (ev.key === 'Escape' && armed) { ev.preventDefault(); disarmDelete(); }   // an armed 🗑️ first, then the overlay
  else if (ev.key === 'Escape') { ev.preventDefault(); closeOverlay(); }
  else if (ev.key === 'ArrowLeft') step(-1);
  else if (ev.key === 'ArrowRight') step(1);
  else if (ev.key === 'Delete') { ev.preventDefault(); pressDelete(); }
});
let touchX = null;
overlayEl.addEventListener('touchstart', ev => { touchX = ev.touches.length === 1 ? ev.touches[0].clientX : null; }, { passive: true });
overlayEl.addEventListener('touchend', ev => {
  if (touchX === null || !ev.changedTouches.length) return;
  const dx = ev.changedTouches[0].clientX - touchX;
  touchX = null;
  if (Math.abs(dx) > 50) step(dx < 0 ? 1 : -1);
}, { passive: true });

// ── The self-reload ───────────────────────────────────────────────────

// The app serves other files than the ones this page runs (an upgrade while the phone kept the tab): reload, the draft kept, once per tag.
function staleAssets(tag) {
  return Neon.staleAssets(tag, saveDraft);
}

// ── The state ─────────────────────────────────────────────────────────

function applyState(s) {
  if (Neon.tabs(s.chatTab, s.homeTab, s.shoppingTab, s.youTubeTab)) return;   // Chat turned off at the PC: on to a tab that is on
  state = s;
  Neon.theme(s.theme);
  profileEl.textContent = (s.modelAvatar ? s.modelAvatar + ' ' : '') + s.profile + (s.planMode ? ' · plan' : '');
  modelEl.textContent = s.model + (s.title ? ' · ' + s.title : '');
  stopBtn.classList.toggle('hidden', !s.busy);
  drawStatus();   // a busy PC with no label to show still spins
  waitingEl.textContent = s.waitingAtPc ? '⏸ Waiting for an answer at the PC: ' + s.waitingAtPc : '';
  waitingEl.classList.toggle('hidden', !s.waitingAtPc);
  // The held queue (2026-10-17): the banner while a cancel holds the waiting lines, its Resume sending them.
  heldEl.querySelector('.queue-held-text').textContent = s.queueHeld || '';
  resumeBtn.textContent = words.resume || 'Resume';
  heldEl.classList.toggle('hidden', !s.queueHeld);
  queuedEl.replaceChildren();
  for (const line of s.queued || []) queuedEl.appendChild(el('div', null, '⏳ ' + line.replace(/\s+/g, ' ')));
  queuedEl.classList.toggle('hidden', !(s.queued || []).length);
  updateTitle();
}

// ── The status row (2026-10-15) ───────────────────────────────────────
// The PC's spinner as its busy row shows it (the turn's stage, a fun verb, a tool, a model's load) with its clocks, and the usage on the right.
// The app sends the elapsed times when the label changes; the page runs the clocks from there. A narrow phone drops the token count, then the
// speed. A tap on the usage opens the full /usage figures, on the page alone.

const statusEl = document.getElementById('status');
const spinEl = statusEl.querySelector('.spin');
const frameEl = spinEl.querySelector('.frame');
const labelEl = spinEl.querySelector('.label');
const clockEl = spinEl.querySelector('.clock');
const usageBtn = document.getElementById('usage');
const learnBtn = document.getElementById('learn');
const loopEl = document.getElementById('loop');
const usageCard = document.getElementById('usage-card');
const Frames = ['⠋', '⠙', '⠸', '⠴', '⠧', '⠇', '⠏'];   // the PC's (Theme.SpinnerFrames)
const still = window.matchMedia('(prefers-reduced-motion: reduce)');
const narrow = window.matchMedia('(max-width: 420px)');
const tiny = window.matchMedia('(max-width: 340px)');
let status = null;
let started = 0;
let stepStarted = null;
let spinTimer = null;
let frame = 0;

function two(n) { return String(n).padStart(2, '0'); }

// ElapsedText.Countdown's shape: 00:12, 01:02:03 past an hour.
function countdown(ms) {
  const t = Math.max(0, Math.round(ms / 1000));
  const h = Math.floor(t / 3600);
  return (h > 0 ? two(h) + ':' : '') + two(Math.floor(t % 3600 / 60)) + ':' + two(t % 60);
}

function applyStatus(s) {
  status = s || null;
  const now = performance.now();
  if (status && status.label) {
    started = now - (status.elapsedMs || 0);
    stepStarted = status.stepMs !== undefined && status.stepMs !== null ? now - status.stepMs : null;
  }
  drawStatus();
  usageChanged(status);
}

function usageText(u) {
  if (!u) return '';
  const parts = [];
  if (u.tokens && !(narrow.matches && u.percent)) parts.push(u.tokens);
  if (u.percent) parts.push(u.percent);
  if (u.speed && !tiny.matches) parts.push(u.speed);
  return parts.join(' · ');
}

resumeBtn.addEventListener('click', async () => {
  resumeBtn.disabled = true;
  const r = await post('/api/queue/resume', {});
  resumeBtn.disabled = false;
  if (r.ok) heldEl.classList.add('hidden');
  else say(r.message || '');
});

// The reflection's 🧠 (2026-10-17, the user's ask): shown while the PC's strip shows it; two taps cancel the reflection, as the PC's
// double-click or Ctrl+L does. The redraws only show or hide it, so an armed tap survives them; a reflection ending while it is armed
// disarms it, so a tap on a later 🧠 is never the second of an old pair.
let learnArmed = false;
const paintLearn = Neon.paintArmed(learnBtn, on => on ? (words.learnCancelArmed || 'Tap again to cancel the reflection') : (words.learnCancel || 'A reflection is running: tap twice to cancel it'));
Neon.armTwice(learnBtn, on => { learnArmed = on; paintLearn(on); }, async () => {
  const r = await post('/api/learn/cancel', {});
  if (!r.ok) say(r.message || '');
});

function drawStatus() {
  const label = status && status.label;
  const spinning = !!label || !!(state && state.busy);
  const learning = !!(status && status.learning);
  // A Ralph loop's place (2026-10-18): the PC's strip, 🔁 step 4/9 · pass 6, while a loop runs.
  const loop = (status && status.loop) || '';
  loopEl.textContent = loop;
  loopEl.classList.toggle('hidden', !loop);
  if (!learning && learnArmed) Neon.disarm();
  learnBtn.classList.toggle('hidden', !learning);
  spinEl.classList.toggle('hidden', !spinning);
  if (spinning) {
    frameEl.textContent = still.matches ? '⏳' : Frames[frame % Frames.length];
    if (label) {
      const total = countdown(performance.now() - started);
      labelEl.textContent = (stepStarted !== null ? '🛠️ ' : '') + label;
      clockEl.textContent = stepStarted !== null ? countdown(performance.now() - stepStarted) + ' · ' + total : total;
    } else {
      labelEl.textContent = words.working || 'working';
      clockEl.textContent = '';
    }
  }
  const usage = usageText(status && status.usage);
  usageBtn.textContent = usage;
  usageBtn.classList.toggle('hidden', !usage);
  statusEl.classList.toggle('hidden', !spinning && !usage && !learning && !loop);
  if (spinning && !spinTimer) {
    spinTimer = setInterval(() => { frame++; drawStatus(); }, 100);
  } else if (!spinning && spinTimer) {
    clearInterval(spinTimer);
    spinTimer = null;
  }
}

narrow.addEventListener('change', drawStatus);
tiny.addEventListener('change', drawStatus);

// The usage card (2026-10-15; a meter and tiles since 2026-10-16, the user's ask: the lines in a text box read as amateurish): the PC pane's
// own sections, the Context one as a meter (amber past 80%, red past 95%; the fill capped at full while the number says past 100 after a
// server-side trim) and the others as tiles. Open, it follows a reply: a status whose usage changed reads it again, at most once a second,
// the last change always read (an unchanged card is a 304).
const usageTitle = document.getElementById('usage-title');
const usageContext = document.getElementById('usage-context');
const usageTiles = document.getElementById('usage-tiles');
const usageEmpty = document.getElementById('usage-empty');
let usageKey = '';
let usageTimer = null;
let usageAgain = false;

function renderUsage(card) {
  usageTitle.textContent = card.title;
  usageContext.replaceChildren();
  const c = card.context;
  usageContext.classList.toggle('hidden', !c);
  if (c) {
    const head = el('div', 'usage-head');
    head.appendChild(el('span', 'usage-heading', c.heading));
    const figures = c.inUse && c.window ? c.inUse + ' / ' + c.window : c.inUse || c.window || '';
    head.appendChild(el('span', 'usage-figures', figures));
    if (c.percent !== undefined && c.percent !== null) head.appendChild(el('span', 'usage-share', c.percent + '%'));
    usageContext.appendChild(head);
    if (c.percent !== undefined && c.percent !== null) {
      const meter = el('div', 'usage-meter' + (c.percent >= 95 ? ' full' : c.percent >= 80 ? ' warn' : ''));
      meter.setAttribute('role', 'meter');
      meter.setAttribute('aria-valuemin', '0');
      meter.setAttribute('aria-valuemax', '100');
      meter.setAttribute('aria-valuenow', String(Math.min(c.percent, 100)));
      meter.setAttribute('aria-label', c.heading + ' ' + c.percent + '%');
      const fill = el('div', 'usage-meter-fill');
      fill.style.width = Math.min(c.percent, 100) + '%';
      meter.appendChild(fill);
      usageContext.appendChild(meter);
    }
    usageContext.appendChild(el('div', 'usage-source', c.source));
  }
  usageEmpty.textContent = card.empty || '';
  usageEmpty.classList.toggle('hidden', !card.empty);
  usageTiles.replaceChildren(...(card.sections || []).map(section => {
    const tile = el('section', 'usage-tile');
    const head = el('h3', null, section.heading);
    if (section.note) head.appendChild(el('span', 'usage-note', ' ' + section.note));
    tile.appendChild(head);
    const list = el('dl');
    for (const row of section.rows || []) {
      const line = el('div', 'usage-row' + (row.value.length > 18 ? ' long' : ''));
      line.appendChild(el('dt', null, row.label));
      line.appendChild(el('dd', null, row.value));
      list.appendChild(line);
    }
    tile.appendChild(list);
    return tile;
  }));
}

async function readUsage() {
  const r = await Neon.get('/api/usage', true);
  if (r.ok && r.json) renderUsage(r.json);   // a 304: the card as drawn
  return r.ok;
}

// A status whose usage changed, while the card is open: read again, once a second at most, the last change never dropped.
function usageChanged(s) {
  const key = JSON.stringify(s && s.usage || null);
  if (key === usageKey) return;
  usageKey = key;
  if (usageCard.classList.contains('hidden')) return;
  if (usageTimer) { usageAgain = true; return; }
  readUsage();
  usageTimer = setTimeout(function tick() {
    if (usageAgain && !usageCard.classList.contains('hidden')) {
      usageAgain = false;
      readUsage();
      usageTimer = setTimeout(tick, 1000);
      return;
    }
    usageAgain = false;
    usageTimer = null;
  }, 1000);
}

async function toggleUsage() {
  if (!usageCard.classList.contains('hidden')) { closeUsage(); return; }
  if (!await readUsage()) return;
  document.getElementById('usage-close').textContent = words.close || 'Close';
  closeDrawer();   // one footer panel at a time
  closePrompts(false);
  usageCard.classList.remove('hidden');
  floatSoon();
}

function closeUsage() {
  usageCard.classList.add('hidden');
  floatSoon();
}

usageBtn.addEventListener('click', toggleUsage);
document.getElementById('usage-close').addEventListener('click', closeUsage);
document.addEventListener('keydown', ev => {
  if (ev.key === 'Escape' && !ev.defaultPrevented && !shown && !usageCard.classList.contains('hidden')) { ev.preventDefault(); closeUsage(); }
});

function updateTitle() {
  const name = state ? state.profile : 'NeonSidekick';
  document.title = (asks.length ? '(' + asks.length + ') ' : '') + name;
}

// ── The asks ──────────────────────────────────────────────────────────

// The asks' folded details opened by the user (2026-10-17: the reflection's proposal under View), kept by ask id across the rebuilds below.
const openDetails = new Set();

function renderAsks(list) {
  asks = list || [];
  asksEl.replaceChildren();
  for (const ask of asks) asksEl.appendChild(askCard(ask));
  stopBotViews(new Set(asks.filter(a => a.live).map(a => a.id)));
  const live = new Set(asks.map(a => a.id));
  for (const id of [...openDetails]) if (!live.has(id)) openDetails.delete(id);
  if (asks.length > lastAskCount) {
    // One footer panel at a time (later on 2026-10-15): the question the PC waits on closes the drawer. Under the overlay the drawer goes at
    // once (it can't be seen) and its entry is stepped over when the overlay closes, so the picture isn't pulled away.
    if (drawerOpen) { if (shown) hideDrawer(); else closeDrawer(); }
    if (promptsOpen) { if (shown) hidePrompts(false); else closePrompts(false); }
    if (navigator.vibrate) {
      try { navigator.vibrate(200); } catch { /* not allowed */ }
    }
  }
  lastAskCount = asks.length;
  updateTitle();
}

async function answer(card, body) {
  for (const b of card.querySelectorAll('button')) b.disabled = true;
  const r = await post('/api/answer', body);
  if (!r.ok) {
    say(r.message || 'That answer did not go through.');
    if (r.status !== 409) for (const b of card.querySelectorAll('button')) b.disabled = false;
  } else {
    say('');
  }
}

function askCard(ask) {
  const card = el('section', 'ask');
  card.appendChild(el('h2', null, ask.title));
  if (ask.body) card.appendChild(el('p', 'text', ask.body));
  if (ask.lines && ask.lines.length) card.appendChild(el('pre', null, ask.lines.join('\n')));
  if (ask.bodyHtml) {
    const plan = el('div', 'plan body');
    plan.innerHTML = ask.bodyHtml;
    card.appendChild(plan);
  }
  if (ask.questions && ask.questions.length) {
    card.appendChild(questionForm(card, ask));
    return card;
  }
  if (ask.items && ask.items.length) card.appendChild(pickList(card, ask));
  if (ask.live) card.appendChild(botCheckView(ask));
  const choices = el('div', 'choices');
  for (const choice of ask.choices) {
    const b = el('button', null, choice.label);
    b.type = 'button';
    if (choice.id === ask.textChoice) {
      b.addEventListener('click', () => textAnswer(card, ask, choice));
    } else {
      b.addEventListener('click', () => answer(card, { askId: ask.id, choice: choice.id }));
    }
    choices.appendChild(b);
  }
  card.appendChild(choices);
  // A long text folded under the buttons (2026-10-17, the user's question: the reflection's proposal pushed Keep and Discard far down on a
  // phone): the body is the summary, the whole proposal behind View; an open fold stays open when another ask redraws the cards.
  if (ask.detail) {
    const fold = el('details', 'detail');
    fold.appendChild(el('summary', null, ask.detailLabel || 'View'));
    fold.appendChild(el('pre', null, ask.detail));
    fold.open = openDetails.has(ask.id);
    fold.addEventListener('toggle', () => { if (fold.open) openDetails.add(ask.id); else openDetails.delete(ask.id); });
    card.appendChild(fold);
  }
  return card;
}

// ── A bot check's card (2026-10-17): the PC's Browser window, live, driven from here ──────────
// While a bot check runs in the PC's Browser window (Web UI bot check control on), its card shows the window — a picture read about three
// times a second, nothing sent while it is unchanged (the ETag) — and sends taps, holds, scrolls and typing into it, so a check met away
// from home can be passed from the phone. The view outlives renderAsks' rebuilds: kept by its ask's id and moved into the new card, stopped
// once its ask is gone. Its picture's box is exactly the picture (no letterbox), so a tap's place is a fraction of the window's view.
const botViews = new Map();   // ask id → { el, stop }

function botCheckView(ask) {
  let view = botViews.get(ask.id);
  if (!view) {
    view = makeBotView(ask.id);
    botViews.set(ask.id, view);
  }
  return view.el;
}

function stopBotViews(live) {
  for (const [id, view] of botViews) {
    if (!live.has(id)) {
      view.stop();
      botViews.delete(id);
    }
  }
}

function makeBotView(askId) {
  const root = el('div', 'botcheck-view');
  const box = el('div', 'botcheck-box');
  const img = el('img', 'botcheck-picture');
  img.alt = words.botCheckPicture || "The PC's Browser window";
  img.draggable = false;
  box.appendChild(img);
  const stalled = el('p', 'botcheck-stalled hidden', words.botCheckStalled || "The window isn't drawing right now.");
  const hint = el('p', 'botcheck-hint', words.botCheckHint || 'Tap to click; touch and hold to press and hold.');
  const zooms = [1, 2, 3];
  let zoom = 0;
  function button(label, name, run) {
    const b = el('button', null, label);
    b.type = 'button';
    b.title = name;
    b.setAttribute('aria-label', name);
    b.addEventListener('click', run);
    return b;
  }
  const bar = el('div', 'botcheck-bar');
  bar.append(
    button('▲', words.botCheckScrollUp || 'Scroll up', () => send({ kind: 'scroll', x: 0.5, y: 0.5, deltaY: -600 })),
    button('▼', words.botCheckScrollDown || 'Scroll down', () => send({ kind: 'scroll', x: 0.5, y: 0.5, deltaY: 600 })),
    button('🔍', words.botCheckZoom || 'Zoom', () => {
      zoom = (zoom + 1) % zooms.length;
      img.style.width = (100 * zooms[zoom]) + '%';
      read();
    }),
    button('⏎', words.botCheckEnter || 'Enter', () => send({ kind: 'key', key: 'Enter' })),
    button('⌫', words.botCheckBackspace || 'Backspace', () => send({ kind: 'key', key: 'Backspace' })),
    button('⇥', words.botCheckTab || 'Tab', () => send({ kind: 'key', key: 'Tab' })));
  const typing = el('div', 'botcheck-type');
  const field = el('input');
  field.type = 'text';
  field.autocomplete = 'off';
  field.placeholder = words.botCheckTypeHere || 'Type into the window';
  // Enter in the box is its Type: the text goes in, no Enter key with it (⏎ sends that alone).
  field.addEventListener('keydown', ev => {
    if (ev.key === 'Enter') {
      ev.preventDefault();
      typeNow();
    }
  });
  function typeNow() {
    const text = field.value;
    if (!text) return;
    field.value = '';
    send({ kind: 'text', text });
  }
  typing.append(field, button(words.botCheckSend || 'Type', words.botCheckSend || 'Type', typeNow));
  root.append(box, stalled, hint, bar, typing);

  // The pictures: one read at a time, the next ~330 ms after the last lands; none while the tab is hidden or the card is out of view.
  let stopped = false;
  let tag = null;
  let timer = null;
  let reading = false;
  let again = false;
  let inView = true;
  const observer = 'IntersectionObserver' in window
    ? new IntersectionObserver(seen => { inView = seen.some(s => s.isIntersecting); if (inView) read(); })
    : null;
  if (observer) observer.observe(root);
  const onVisible = () => { if (!document.hidden) read(); };
  document.addEventListener('visibilitychange', onVisible);

  function soon() {
    clearTimeout(timer);
    if (!stopped) timer = setTimeout(read, 330);
  }

  function dataUrl(blob) {
    return new Promise((done, failed) => {
      const reader = new FileReader();
      reader.onload = () => done(reader.result);
      reader.onerror = failed;
      reader.readAsDataURL(blob);
    });
  }

  function over() {
    // The check is over, or the PC turned the control off: the card keeps Give up until its ask closes.
    stop();
    box.classList.add('hidden');
    stalled.classList.add('hidden');
    hint.classList.add('hidden');
    bar.classList.add('hidden');
    typing.classList.add('hidden');
  }

  async function read() {
    if (stopped) return;
    clearTimeout(timer);
    if (document.hidden || !inView) return;
    if (reading) { again = true; return; }
    reading = true;
    try {
      const width = Math.round(Math.max(320, (box.clientWidth || 360) * (window.devicePixelRatio || 1) * zooms[zoom]));
      const headers = {};
      if (tag) headers['If-None-Match'] = tag;
      const r = await fetch('/api/botcheck/frame?ask=' + encodeURIComponent(askId) + '&w=' + width, { credentials: 'same-origin', cache: 'no-store', headers });
      if (r.status === 401) { location.reload(); return; }
      if (r.status === 200) {
        const src = await dataUrl(await r.blob());
        tag = r.headers.get('ETag');
        if (!stopped) img.src = src;
        stalled.classList.add('hidden');
      } else if (r.status === 304) {
        stalled.classList.add('hidden');
      } else if (r.status === 503) {
        stalled.classList.remove('hidden');
      } else if (r.status === 403 || r.status === 404) {
        let j = null;
        try { j = await r.json(); } catch { /* none */ }
        if (j && j.tabOff) { Neon.leave(); return; }
        over();
        return;
      }
    } catch { /* the next read tries again */ } finally {
      reading = false;
    }
    if (again) { again = false; read(); } else soon();
  }

  // The input: one post at a time, so an up never overtakes its down; each asks for a fresh picture.
  const queue = [];
  let sending = false;
  function send(input) {
    if (stopped) return;
    queue.push(input);
    pump();
  }
  async function pump() {
    if (sending) return;
    sending = true;
    while (queue.length && !stopped) {
      const r = await post('/api/botcheck/input', Object.assign({ askId }, queue.shift()));
      if (r.status === 403 || r.status === 404) { queue.length = 0; over(); break; }
      if (!r.ok && r.message) say(r.message);
      read();
    }
    sending = false;
  }

  // The pointer: a quick tap clicks; one held still past 150 ms presses, and lets go where it pressed; one that moves first pans the zoomed box.
  let press = null;
  const clamp = v => Math.min(1, Math.max(0, v));
  function at(ev) {
    const r = img.getBoundingClientRect();
    return { x: clamp((ev.clientX - r.left) / r.width), y: clamp((ev.clientY - r.top) / r.height) };
  }
  img.addEventListener('pointerdown', ev => {
    if (ev.button !== 0 || !img.src || press) return;
    const p = at(ev);
    const held = { id: ev.pointerId, x: p.x, y: p.y, sx: ev.clientX, sy: ev.clientY, down: false, timer: 0 };
    held.timer = setTimeout(() => {
      if (press !== held) return;
      held.down = true;
      send({ kind: 'down', x: held.x, y: held.y });
    }, 150);
    press = held;
  });
  img.addEventListener('pointermove', ev => {
    if (!press || press.id !== ev.pointerId || press.down) return;
    if (Math.hypot(ev.clientX - press.sx, ev.clientY - press.sy) > 10) {
      clearTimeout(press.timer);
      press = null;
    }
  });
  function release(ev, cancelled) {
    if (!press || press.id !== ev.pointerId) return;
    clearTimeout(press.timer);
    if (press.down) send({ kind: 'up', x: press.x, y: press.y });
    else if (!cancelled) send({ kind: 'tap', x: press.x, y: press.y });
    press = null;
  }
  img.addEventListener('pointerup', ev => release(ev, false));
  img.addEventListener('pointercancel', ev => release(ev, true));
  img.addEventListener('contextmenu', ev => ev.preventDefault());
  // A desktop wheel over the picture scrolls the window there, its turns gathered into one post every 150 ms.
  let wheel = null;
  img.addEventListener('wheel', ev => {
    ev.preventDefault();
    const p = at(ev);
    const lines = ev.deltaMode === 1 ? 40 : ev.deltaMode === 2 ? 600 : 1;
    if (wheel) {
      wheel.deltaY += ev.deltaY * lines;
      return;
    }
    wheel = { x: p.x, y: p.y, deltaY: ev.deltaY * lines };
    setTimeout(() => {
      const w = wheel;
      wheel = null;
      if (w.deltaY) send({ kind: 'scroll', x: w.x, y: w.y, deltaY: Math.round(w.deltaY) });
    }, 150);
  }, { passive: false });

  function stop() {
    if (stopped) return;
    stopped = true;
    clearTimeout(timer);
    if (press) clearTimeout(press.timer);
    if (observer) observer.disconnect();
    document.removeEventListener('visibilitychange', onVisible);
  }

  read();
  return { el: root, stop };
}

// A pick card (later on 2026-10-14: /model, /reasoning, /rewind from the page): a filter over the rows, the one in use marked and in view.
function pickList(card, ask) {
  const box = el('div', 'pick');
  const filter = el('input');
  filter.type = 'search';
  filter.placeholder = words.filter || 'Filter';
  filter.autocomplete = 'off';
  const list = el('div', 'pick-list');
  const rows = [];
  for (const item of ask.items) {
    const b = el('button', 'pick-item' + (item.id === ask.current ? ' current' : ''));
    b.type = 'button';
    b.appendChild(el('span', 'label', item.label));
    if (item.note) b.appendChild(el('span', 'note', item.note));
    b.addEventListener('click', () => answer(card, { askId: ask.id, choice: item.id }));
    list.appendChild(b);
    rows.push({ b, text: (item.label + ' ' + (item.note || '')).toLowerCase() });
  }
  filter.addEventListener('input', () => {
    const q = filter.value.trim().toLowerCase();
    for (const r of rows) r.b.classList.toggle('hidden', q.length > 0 && !r.text.includes(q));
  });
  if (ask.items.length > 8) box.appendChild(filter);
  box.appendChild(list);
  requestAnimationFrame(() => { const c = list.querySelector('.current'); if (c) c.scrollIntoView({ block: 'center' }); });
  return box;
}

function textAnswer(card, ask, choice) {
  if (card.querySelector('textarea')) return;
  const box = el('textarea');
  box.placeholder = choice.label;
  const go = el('button', 'primary', choice.label);
  go.type = 'button';
  go.addEventListener('click', () => answer(card, { askId: ask.id, choice: choice.id, text: box.value }));
  card.appendChild(box);
  card.appendChild(go);
  box.focus();
}

function questionForm(card, ask) {
  const form = el('form');
  const parts = [];
  ask.questions.forEach((q, qi) => {
    const set = el('fieldset');
    set.appendChild(el('legend', null, q.title ? q.title + ': ' + q.question : q.question));
    const name = ask.id + '-' + qi;
    const inputs = [];
    q.options.forEach((option, oi) => {
      const label = el('label');
      const input = el('input');
      input.type = q.multi ? 'checkbox' : 'radio';
      input.name = name;
      input.value = String(oi);
      label.appendChild(input);
      label.appendChild(document.createTextNode(option));
      set.appendChild(label);
      inputs.push(input);
    });
    const otherLabel = el('label');
    const otherPick = el('input');
    otherPick.type = q.multi ? 'checkbox' : 'radio';
    otherPick.name = name;
    otherPick.value = 'other';
    const other = el('input');
    other.type = 'text';
    other.placeholder = 'Other';
    other.addEventListener('input', () => { if (other.value.trim()) otherPick.checked = true; update(); });
    otherLabel.appendChild(otherPick);
    otherLabel.appendChild(other);
    set.appendChild(otherLabel);
    form.appendChild(set);
    parts.push({ inputs, otherPick, other });
  });
  const submit = el('button', 'primary', ask.submitLabel || 'Submit');
  submit.type = 'submit';
  form.appendChild(submit);

  function collect() {
    return parts.map(p => ({
      picked: p.inputs.filter(i => i.checked).map(i => Number(i.value)),
      other: p.otherPick.checked ? p.other.value.trim() : '',
    }));
  }
  function update() {
    submit.disabled = !collect().every(a => a.picked.length > 0 || a.other.length > 0);
  }
  form.addEventListener('change', update);
  form.addEventListener('submit', ev => {
    ev.preventDefault();
    answer(card, { askId: ask.id, questions: collect() });
  });
  update();
  return form;
}

// ── Sending ───────────────────────────────────────────────────────────

function saveDraft() {
  try { sessionStorage.setItem('neon.draft', draft.value); } catch { /* private mode */ }
}

// The box's height to its text (2026-10-16, the user's screenshot: a one-line draft showed a scrollbar under the ✕): the page sizes boxes by
// their border, so the border goes on top of scrollHeight, which leaves it out; the box scrolls only past its cap, and the ✕ keeps clear of
// the scrollbar then.
function grow() {
  draft.style.height = 'auto';
  const border = draft.offsetHeight - draft.clientHeight;
  const cap = window.innerHeight * 0.4;
  const want = draft.scrollHeight + border;
  draft.style.height = Math.min(want, cap) + 'px';
  draft.style.overflowY = want > cap ? 'auto' : 'hidden';
  const bar = draft.offsetWidth - draft.clientWidth - border;
  draft.parentElement.style.setProperty('--sb', Math.max(0, bar) + 'px');
  syncClear();   // every change to the box, the script's too, passes here
}

function putBack(text) {
  draft.value = draft.value ? text + '\n' + draft.value : text;
  saveDraft();
  grow();
}

function send(asText) {
  return sendText(draft.value, { fromDraft: true, asText: asText });
}

// One send (later on 2026-10-15, out of send for the saved prompts' ➤): the pending id kept per text so a retry is one message; the draft
// emptied only when the text came from it; a refusal's Send as a message re-sends the text refused, not whatever the box holds by then.
async function sendText(text, opts) {
  const fromDraft = !!(opts && opts.fromDraft);
  const asText = !!(opts && opts.asText);
  if (!text.trim()) return;
  if (pendingText !== text) { pendingId = newId(); pendingText = text; }
  sendBtn.disabled = true;
  const r = await post('/api/send', { id: pendingId, text: text, asText: asText });
  sendBtn.disabled = false;
  if (r.ok) {
    if (fromDraft && draft.value === text) draft.value = '';
    remembered(text);   // the app has it in the history now; the page's copy at once
    pendingId = null;
    pendingText = null;
    if (fromDraft) {
      hideList();
      saveDraft();
      grow();
    }
    say('');
    if (r.json && r.json.open === 'prompts') openPrompts('prompts');   // bare /prompts: the page's own sheet, on its Prompts tab
    return;
  }
  // A refused command (later on 2026-10-14): the app's reason, and what it offers instead.
  const actions = [];
  if (r.use) actions.push({ label: words.useLine + ' ' + r.use, run: () => { draft.value = r.use + ' '; saveDraft(); grow(); draft.focus(); } });
  if (r.asText) actions.push({ label: words.sendAsMessage, run: () => sendText(text, { fromDraft: fromDraft, asText: true }) });
  sayWith(r.message || 'That message did not go through.', actions);
}

// ── The sheet: saved prompts, memories, the message history (later on 2026-10-15; three tabs since 2026-10-16, the user's ask) ──
// 📣 at the box's left, and bare /prompts sent from the page, open the sheet as a footer panel, on the tab used last (bare /prompts on Prompts);
// the button shows that tab's glyph, 📣, 🕘 or 💾 (2026-10-17, the user's ask). Each tab is read from the app as it shows, in the order
// Prompts, History, Memory (2026-10-17; Memory second until then). 📣 Prompts: a tap on a row sends the prompt at once, as typed (a command
// meets the page's command rules; 2026-10-16, the user's ask), its ✎ puts it on the draft (ahead of what's there, with Undo; the tap's until
// then, ➤ sending), its 🗑️ deletes it; ⇪ saves the draft as one. 🕘 History: the message history, newest first, a row and its ➤ as a
// prompt's; clear all empties it. 💾 Memory: the profile's memories, a turned-off one marked, each with its 🗑️; ⇪ saves the draft as one
// (refused while Memory mode is off, which the tab says). A 🗑️ or clear all is two taps, the
// picture 🗑️'s way: the first arms it for 3 s, the second acts; Esc disarms first. ⇪ keeps the draft. One footer panel at a time: the sheet,
// the drawer and the usage card replace each other — the sheet and the drawer swap their history entry (replaceState), never back-then-push,
// whose late pop would take the new entry with it. A tab change keeps the entry. Esc, Back, ✕, a tap on the transcript or a question arriving
// close it; a picture tapped under it opens the overlay over it.

const promptsBtn = document.getElementById('prompts-button');
const promptsEl = document.getElementById('prompts');
const promptsTabs = [...document.querySelectorAll('#prompts .sheet-tab')];
const promptsAction = document.getElementById('prompts-action');
const promptsClose = document.getElementById('prompts-close');
const promptsNote = document.getElementById('prompts-note');
const promptsFilter = document.getElementById('prompts-filter');
const promptsList = document.getElementById('prompts-list');
const SheetTabs = ['prompts', 'history', 'memory'];   // index.html's order: ←/→ step through this and focus promptsTabs at the same index
let promptsOpen = false;
let promptsOpening = false;
let sheetTab = 'prompts';
try { if (SheetTabs.includes(sessionStorage.getItem('neon.sheetTab'))) sheetTab = sessionStorage.getItem('neon.sheetTab'); } catch { /* private mode */ }
let promptsActionNow = promptsAction;   // the bar's action button as it stands (each tab's is a fresh one)

// A tab's label as the words give it ('📣 Prompts'), and its glyph alone, the label's first word.
function tabLabel(tab) {
  return { prompts: words.promptsTab || '📣 Prompts', history: words.historyTab || '🕘 History', memory: words.memoryTab || '💾 Memory' }[tab];
}

function tabGlyph(tab) { return tabLabel(tab).split(' ')[0]; }

// 📣 shows the glyph of the tab the sheet opens on (2026-10-17); its name stays the sheet's.
function paintPromptsButton() { promptsBtn.textContent = tabGlyph(sheetTab); }

// The tab chosen, remembered for this browser tab, and shown on the button.
function setSheetTab(tab) {
  sheetTab = tab;
  try { sessionStorage.setItem('neon.sheetTab', tab); } catch { /* private mode */ }
  paintPromptsButton();
}

paintPromptsButton();

function showPrompts(on) {
  const was = nearBottom();
  promptsOpen = on;
  promptsEl.classList.toggle('hidden', !on);
  if (!on) { Neon.disarm(); closeMemoryEditor(false); }
  keepBottom(was);
  floatSoon();
}

// A 🗑️ or clear all: two taps (Neon.armTwice, common.js since later on 2026-10-16, shared with the YouTube tab's saved list).
function rowDelete(onConfirm) {
  const del = el('button', 'row-delete', '🗑️');
  del.type = 'button';
  Neon.armTwice(del, Neon.paintArmed(del, on => on ? (words.deleteRowArmed || 'Delete?') : (words.deleteRow || 'Delete')), onConfirm);
  return del;
}

function paintTabs() {
  for (const tab of promptsTabs) {
    const on = tab.dataset.tab === sheetTab;
    tab.setAttribute('aria-selected', on ? 'true' : 'false');
    tab.tabIndex = on ? 0 : -1;
    tab.classList.toggle('on', on);
  }
}

// The bar's action: ⇪ save draft on the prompts and memory tabs, a two-tap clear all on the history's.
function paintAction() {
  Neon.disarm();
  const fresh = promptsActionNow.cloneNode(false);   // its listeners gone with the old one
  promptsActionNow.replaceWith(fresh);
  promptsActionNow = fresh;
  if (sheetTab === 'history') {
    Neon.armTwice(fresh, on => {
      fresh.classList.toggle('armed', on);
      actionLabel(fresh, on ? (words.clearAllArmed || 'Clear all?') : (words.clearAll || '🗑️ clear all'));
    }, clearHistoryNow);
  } else {
    actionLabel(fresh, words.saveDraft || '⇪ save draft');
    fresh.addEventListener('click', saveDraftToSheet);
  }
}

// The action's label as its glyph and its words, so a narrow phone shows the glyph alone (the whole label kept for a screen reader).
function actionLabel(button, text) {
  const space = text.indexOf(' ');
  const glyph = space > 0 && !/^[A-Za-z]/.test(text) ? text.slice(0, space) : '';
  button.replaceChildren(el('span', 'tab-glyph', glyph), el('span', 'tab-word', glyph ? text.slice(space + 1) : text));
  button.classList.toggle('has-glyph', !!glyph);
  button.setAttribute('aria-label', text);
  button.title = text;
}

async function readSheet(tab) {
  const path = tab === 'memory' ? '/api/memory' : tab === 'history' ? '/api/history' : '/api/prompts';
  const r = await Neon.get(path, tab === 'history');
  if (tab === 'history') {
    if (r.ok && r.json) takeLines(r.json.lines || []);   // a 304 keeps the lines the page holds
    return r.ok ? { lines: msgHistory.lines } : null;
  }
  return r.ok && r.json ? r.json : null;
}

function renderSheet(tab, sheet) {
  memoryEditor = null;   // the list is drawn afresh, an open editor with it
  paintTabs();
  paintAction();
  promptsFilter.value = '';
  promptsFilter.placeholder = words.filter || 'Filter';
  promptsFilter.setAttribute('aria-label', words.filter || 'Filter');
  promptsNote.textContent = tab === 'memory' && sheet.modeOff ? (words.memoryModeOff || 'Memory is off.') : '';
  promptsNote.classList.toggle('hidden', !promptsNote.textContent);
  const rows = tab === 'memory' ? memoryRows(sheet) : tab === 'history' ? historyRows(sheet) : promptRows(sheet);
  promptsFilter.classList.toggle('hidden', rows.length <= 8);
  if (!rows.length) {
    const none = tab === 'memory' ? words.memoryNone : tab === 'history' ? words.historyNone : words.promptsNone;
    promptsList.replaceChildren(el('div', 'prompts-none', none || ''));
    return;
  }
  promptsList.replaceChildren(...rows.map(r => r.row));
  promptsFilter.oninput = () => {
    const q = promptsFilter.value.trim().toLowerCase();
    for (const r of rows) r.row.classList.toggle('hidden', q.length > 0 && !r.text.includes(q));
  };
}

// A row of the sheet: a saved prompt's, a history line's. A history line's text fills the draft and its ➤ sends it. A saved prompt's
// (tapSends, 2026-10-16, the user's ask) is the other way round: a tap on its text sends it, as typed (a command meets the page's command
// rules, refused with sendText's own message), and its ✎ puts it on the draft ahead of what's there, with Undo.
function sendableRow(text, number, extra, tapSends) {
  const row = el('div', 'prompt-row');
  const send = () => { closePrompts(false); sendText(text, { fromDraft: false }); };
  const fill = () => { closePrompts(true); takeDraft(text); draft.focus(); };
  const tap = el('button', 'prompt-text');
  tap.type = 'button';
  tap.title = text;
  if (number) tap.appendChild(el('span', 'num', String(number)));
  tap.appendChild(document.createTextNode(text));
  const named = (number ? number + ' ' : '') + text;
  tap.setAttribute('aria-label', tapSends ? (words.promptsSend || 'Send this prompt') + ': ' + named : named);
  if (tapSends) tap.title = (words.promptsSend || 'Send this prompt') + ': ' + text;
  tap.addEventListener('click', tapSends ? send : fill);
  const other = tapSends ? el('button', 'prompt-draft', '✎') : el('button', 'prompt-send', '➤');
  other.type = 'button';
  const otherLabel = tapSends ? (words.promptsToDraft || 'Put on the message line') : (words.promptsSend || 'Send this prompt');
  other.setAttribute('aria-label', otherLabel);
  other.title = otherLabel;
  other.addEventListener('click', tapSends ? fill : send);
  row.appendChild(tap);
  if (extra) row.appendChild(extra);
  row.appendChild(other);
  return { row, text: text.toLowerCase() };
}

function promptRows(sheet) {
  return (sheet.items || []).map(item => sendableRow(item.text, item.number, rowDelete(() => sheetChange('/api/prompts/delete', { id: item.id })), true));
}

// A memory's row (2026-10-18, the user's ask: edit one from the page): a tap on its text opens its editor in its place, the 🗑️ beside it.
function memoryRows(sheet) {
  const max = sheet.maxChars || 0;
  return (sheet.items || []).map(item => {
    const row = el('div', 'prompt-row memory-row' + (item.off ? ' off' : ''));
    const text = el('button', 'prompt-text');
    text.type = 'button';
    text.appendChild(document.createTextNode(item.text));
    if (item.off) text.appendChild(el('span', 'off-mark', ' · ' + (words.memoryOff || 'off')));
    const label = (words.memoryEdit || 'Edit this memory') + ': ' + item.text;
    text.setAttribute('aria-label', label);
    text.title = label;
    text.addEventListener('click', () => openMemoryEditor(text, item, max));
    row.appendChild(text);
    row.appendChild(rowDelete(() => sheetChange('/api/memory/delete', { text: item.text })));
    return { row, text: item.text.toLowerCase() };
  });
}

// The open memory editor, one at a time: { box, text } — the editor and the text button it stands in for.
let memoryEditor = null;

function closeMemoryEditor(refocus) {
  if (!memoryEditor) return;
  const { box, text } = memoryEditor;
  memoryEditor = null;
  if (box.isConnected) box.replaceWith(text);
  if (refocus) text.focus({ preventScroll: true });
}

function editorButton(label, cls) {
  const b = el('button', cls, label);
  b.type = 'button';
  return b;
}

// A memory's editor (2026-10-18): its text to change (Enter or Save saves, Esc or Cancel keeps it), and Turn off / Turn on at once, as the
// PC pane's Edit and Toggle. Saving re-reads the tab, which closes it.
function openMemoryEditor(text, item, max) {
  closeMemoryEditor(false);
  Neon.disarm();
  const box = el('div', 'memory-edit');
  const area = el('textarea', 'memory-edit-text');
  area.value = item.text;
  area.rows = 3;
  if (max > 0) area.maxLength = max;
  area.setAttribute('aria-label', words.memoryEdit || 'Edit this memory');
  const bar = el('div', 'memory-edit-bar');
  const save = editorButton(words.memoryEditSave || 'Save', 'memory-edit-save');
  const toggle = editorButton(item.off ? (words.memoryTurnOn || 'Turn on') : (words.memoryTurnOff || 'Turn off'), 'memory-edit-toggle');
  const cancel = editorButton(words.memoryEditCancel || 'Cancel', 'memory-edit-cancel');
  const paint = () => { const v = area.value.trim(); save.disabled = !v || v === item.text; };
  area.addEventListener('input', paint);
  area.addEventListener('keydown', ev => {
    if (ev.key === 'Enter' && !ev.isComposing) {
      ev.preventDefault();   // one line: a memory's whitespace runs into one space anyway
      if (!save.disabled) save.click();
    } else if (ev.key === 'Escape') {
      ev.preventDefault();   // the editor alone: the sheet's Esc reads defaultPrevented
      closeMemoryEditor(true);
    }
  });
  save.addEventListener('click', () => { const value = area.value; memoryEditor = null; sheetChange('/api/memory/edit', { old: item.text, text: value }); });
  toggle.addEventListener('click', () => { memoryEditor = null; sheetChange('/api/memory/toggle', { text: item.text, off: !item.off }); });
  cancel.addEventListener('click', () => closeMemoryEditor(true));
  bar.append(save, toggle, cancel);
  box.append(area, bar);
  text.replaceWith(box);
  memoryEditor = { box, text };
  paint();
  area.focus({ preventScroll: true });
  area.setSelectionRange(area.value.length, area.value.length);
}

function historyRows(sheet) {
  return [...(sheet.lines || [])].reverse().map(line => sendableRow(line, 0, null));
}

// A change made on the sheet: posted, its sentence said, the tab read again.
async function sheetChange(path, body) {
  const r = await post(path, body);
  say(r.message || '');
  await refreshSheet();
  return r;
}

async function refreshSheet() {
  const tab = sheetTab;
  const sheet = await readSheet(tab);
  if (sheet && promptsOpen && tab === sheetTab) renderSheet(tab, sheet);
}

function saveDraftToSheet() {
  if (!draft.value.trim()) { say(words.draftEmpty || 'The draft is empty.'); return; }
  sheetChange(sheetTab === 'memory' ? '/api/memory/save' : '/api/prompts/save', { text: draft.value });   // the draft stays
}

async function clearHistoryNow() {
  const r = await sheetChange('/api/history/clear', {});
  if (r.ok) { msgHistory.lines = []; msgHistory.cleared = []; historyReset(); }
}

function focusSheet() {
  const first = !promptsFilter.classList.contains('hidden') ? promptsFilter : promptsList.querySelector('button') || promptsActionNow;
  if (first) first.focus({ preventScroll: true });
}

async function selectTab(tab) {
  if (!SheetTabs.includes(tab)) return;
  setSheetTab(tab);
  paintTabs();
  const sheet = await readSheet(tab);
  if (!sheet || !promptsOpen || tab !== sheetTab) return;
  renderSheet(tab, sheet);
  focusSheet();
}

async function openPrompts(tab) {
  if (promptsOpen || promptsOpening) return;
  if (tab) setSheetTab(tab);
  promptsOpening = true;
  const want = sheetTab;
  const sheet = await readSheet(want);
  promptsOpening = false;
  if (!sheet || promptsOpen) return;
  closeUsage();
  renderSheet(want, sheet);
  if (drawerOpen && !shown) {
    hideDrawer();
    history.replaceState({ prompts: true }, '');   // the drawer's entry becomes the sheet's
  } else {
    history.pushState({ prompts: true }, '');
  }
  showPrompts(true);
  focusSheet();
}

function hidePrompts(toBox) {
  if (!promptsOpen) return;
  const focused = promptsEl.contains(document.activeElement);
  showPrompts(false);
  if (focused && !toBox) promptsBtn.focus({ preventScroll: true });
}

// ✕, Esc, a tap on the transcript, a row's tap or ➤, a question arriving: the sheet goes now, and its own history entry with it.
function closePrompts(toBox) {
  if (!promptsOpen) return;
  hidePrompts(toBox);
  if (history.state && history.state.prompts && !history.state.overlay) history.back();
}

promptsBtn.addEventListener('click', () => { if (promptsOpen) closePrompts(false); else openPrompts(); });
promptsClose.addEventListener('click', () => closePrompts(false));
for (const tab of promptsTabs) {
  tab.addEventListener('click', () => { if (tab.dataset.tab !== sheetTab) selectTab(tab.dataset.tab); });
  // The ARIA tabs' keys: ← and → move between them.
  tab.addEventListener('keydown', ev => {
    if (ev.key !== 'ArrowLeft' && ev.key !== 'ArrowRight') return;
    ev.preventDefault();
    const at = (SheetTabs.indexOf(sheetTab) + (ev.key === 'ArrowRight' ? 1 : SheetTabs.length - 1)) % SheetTabs.length;
    selectTab(SheetTabs[at]).then(() => promptsTabs[at].focus({ preventScroll: true }));
  });
}
document.addEventListener('keydown', ev => {
  if (ev.key !== 'Escape' || ev.defaultPrevented || shown || !promptsOpen || !usageCard.classList.contains('hidden')) return;
  ev.preventDefault();
  if (Neon.armed()) { Neon.disarm(); return; }   // an armed 🗑️ first, then the sheet
  closePrompts(false);
});

// A text for the draft (/rewind, a saved prompt's ✎; /prompts <n> until 2026-10-16, which sends now): ahead of what was there, with an undo.
function takeDraft(text) {
  const before = draft.value;
  draft.value = before ? text + '\n' + before : text;
  saveDraft();
  grow();
  sayWith(words.draftFilled, [{ label: words.undo, run: () => { draft.value = before; saveDraft(); grow(); } }]);
}

async function stop() {
  const r = await post('/api/stop', {});
  if (!r.ok) say(r.message);
}

// ── Clearing the draft (later on 2026-10-15) ──────────────────────────
// A ✕ inside the box while it holds text; a tap empties it (its saved copy too) and offers Undo, which puts the text back only into an empty
// box. The PC's draft is its own; nothing goes to the app.

const clearBtn = document.getElementById('clear-draft');

function syncClear() {
  clearBtn.classList.toggle('hidden', !draft.value.trim());
}

clearBtn.addEventListener('pointerdown', ev => ev.preventDefault());   // the box keeps its focus, and the phone its keyboard
clearBtn.addEventListener('click', () => {
  const before = draft.value;
  if (!before) return;
  draft.value = '';
  // To the walk's end, in this tab alone (2026-10-16), as ESC's draft at the PC: ↑ brings it back.
  if (before.trim() && !msgHistory.cleared.includes(before)) msgHistory.cleared.push(before);
  historyReset();
  saveDraft();
  grow();
  hideList();
  draft.focus();
  sayWith(words.draftCleared || 'Draft cleared.', [{
    label: words.undo || 'Undo',
    run: () => {
      if (draft.value) return;   // typed since: never overwritten
      draft.value = before;
      saveDraft();
      grow();
      draft.focus();
    },
  }]);
});

// ── The floating buttons (later on 2026-10-15) ────────────────────────
// ↕ opens or closes every fold (a mode while on, as Ctrl+O at the PC), ⤒ and ⤓ go to the ends and hide at their end, 🎞️ opens the picture
// strip. They sit at the transcript's right edge, the bottom pair above the footer and the phone's keyboard, and hide under the overlay and
// the usage card.

const rootEl = document.documentElement;
const headerEl = document.querySelector('header.top');
const footerEl = document.querySelector('footer.bottom');
const expandBtn = document.getElementById('expand-all');
const toTopBtn = document.getElementById('to-top');
const toBottomBtn = document.getElementById('to-bottom');
const stripBtn = document.getElementById('strip-button');
const stripNew = document.getElementById('strip-new');
const calm = window.matchMedia('(prefers-reduced-motion: reduce)');
let floatQueued = false;

function measure() {
  rootEl.style.setProperty('--head-h', headerEl.offsetHeight + 'px');
  rootEl.style.setProperty('--foot-h', footerEl.offsetHeight + 'px');
  rootEl.style.setProperty('--kb', Neon.keyboardInset(window.innerHeight, window.visualViewport) + 'px');
  fitSplash();
  floatSoon();
}

// The splash's room (2026-10-18, the user's ask: centered and filling the page, as the PC fills the rows under its banner): the visible
// height less the header, the footer, main's padding and every other entry, rounded down with a pixel to spare so the page never scrolls
// by one; 200 px at the least. Nothing while no splash stands.
function fitSplash() {
  const splash = logEl.querySelector('.entry.pictures.splash');
  if (!splash) return;
  const view = window.visualViewport ? window.visualViewport.height : window.innerHeight;
  const main = logEl.parentElement;
  const style = main ? getComputedStyle(main) : null;
  const pad = style ? (parseFloat(style.paddingTop) || 0) + (parseFloat(style.paddingBottom) || 0) : 0;
  const others = logEl.offsetHeight - splash.offsetHeight;
  const room = Math.max(200, Math.floor(view - headerEl.offsetHeight - footerEl.offsetHeight - pad - others) - 1);
  logEl.style.setProperty('--splash-room', room + 'px');
}

function anyFold() {
  return !!logEl.querySelector('details.think-fold, details.diff-fold, .run-head:not(.hidden)');
}

function floatSoon() {
  if (floatQueued) return;
  floatQueued = true;
  requestAnimationFrame(() => {
    floatQueued = false;
    updateFloaters();
  });
}

function updateFloaters() {
  const blocked = !!shown || !usageCard.classList.contains('hidden') || promptsOpen;
  const any = entries.size > 0;
  const fits = document.body.scrollHeight <= window.innerHeight + 80;
  expandBtn.classList.toggle('hidden', blocked || !any || !(expandAll || anyFold()));
  toTopBtn.classList.toggle('hidden', blocked || !any || fits || window.scrollY <= 80);
  toBottomBtn.classList.toggle('hidden', blocked || !any || fits || nearBottom());
  stripBtn.classList.toggle('hidden', blocked || !strip || drawerOpen);
}

function paintExpand() {
  expandBtn.classList.toggle('on', expandAll);
  expandBtn.setAttribute('aria-pressed', expandAll ? 'true' : 'false');
  const label = expandAll ? words.collapseAll : words.expandAll;
  expandBtn.setAttribute('aria-label', label);
  expandBtn.title = label;
}

// The line at the top of the view, so a change above it moves the view by as much.
function topEntry() {
  const top = headerEl.offsetHeight;
  for (const child of logEl.children) if (child.getBoundingClientRect().bottom > top) return child;
  return null;
}

function setAllFolds(open) {
  for (const d of logEl.querySelectorAll('details.think-fold, details.diff-fold')) d.open = open;
  for (const record of entries.values()) {
    if (!record.run) continue;
    record.run.expanded = open;
    applyRun(record.run);
  }
}

expandBtn.addEventListener('click', () => {
  const was = nearBottom();
  const anchor = topEntry();
  const before = anchor ? anchor.getBoundingClientRect().top : 0;
  expandAll = !expandAll;
  try { sessionStorage.setItem('neon.expandAll', expandAll ? '1' : '0'); } catch { /* private mode */ }
  setAllFolds(expandAll);
  paintExpand();
  if (was) keepBottom(true);
  else if (anchor && anchor.isConnected) window.scrollBy(0, anchor.getBoundingClientRect().top - before);
  floatSoon();
});
toTopBtn.addEventListener('click', () => window.scrollTo({ top: 0, behavior: calm.matches ? 'auto' : 'smooth' }));
toBottomBtn.addEventListener('click', () => window.scrollTo({ top: document.body.scrollHeight, behavior: calm.matches ? 'auto' : 'smooth' }));
window.addEventListener('scroll', floatSoon, { passive: true });
window.addEventListener('resize', measure);
if (window.visualViewport) {
  window.visualViewport.addEventListener('resize', measure);
  window.visualViewport.addEventListener('scroll', measure);
}
if ('ResizeObserver' in window) {
  const sizes = new ResizeObserver(measure);
  sizes.observe(footerEl);
  sizes.observe(headerEl);
}
logEl.addEventListener('toggle', floatSoon, true);

// ── The picture strip (later on 2026-10-15) ───────────────────────────
// The PC's ComfyUI strip, newest first, while ComfyUI picture strip is on: a drawer over the status row from 🎞️ (a dot on it says a picture
// came since it was last open). A tap opens the overlay over the strip's pictures and moves the PC's highlight and viewer there; each step in
// the overlay does too, a fast swipe once. The PC's pick is outlined. One footer panel at a time: a question or the usage card closes it.

const drawerEl = document.getElementById('strip');
const stripHead = document.getElementById('strip-head');
const stripRow = document.getElementById('strip-row');
const stripClose = document.getElementById('strip-close');
let strip = null;   // { pictures, selected }
let drawerOpen = false;
let stripKey = '';
let selectTimer = null;

function stripSeen() {
  try { return sessionStorage.getItem('neon.stripSeen'); } catch { return null; }
}

function markSeen() {
  if (!strip) return;
  try { sessionStorage.setItem('neon.stripSeen', String(strip.pictures[0].id)); } catch { /* private mode */ }
  stripNew.classList.add('hidden');
}

function applyStrip(st) {
  strip = st && st.pictures && st.pictures.length ? st : null;
  if (!strip) {
    try { sessionStorage.removeItem('neon.stripSeen'); } catch { /* private mode */ }
    if (shown && shown.fromStrip) closeOverlay();   // its pictures are gone too; the drawer's entry is stepped over after it
    else if (drawerOpen) closeDrawer();
    if (drawerOpen) hideDrawer();
    stripRow.replaceChildren();
    stripKey = '';
    floatSoon();
    return;
  }
  if (drawerOpen) markSeen();
  stripNew.classList.toggle('hidden', drawerOpen || String(strip.pictures[0].id) === stripSeen());
  renderStrip();
  floatSoon();
  if (shown && shown.fromStrip) refreshOverlay();
}

function renderStrip() {
  const n = strip.pictures.length;
  stripHead.textContent = '🎞️ ' + (n === 1 ? words.stripOne : (words.stripMany || '{0}').replace('{0}', String(n)));
  const key = strip.pictures.map(p => p.id).join(',');
  if (key !== stripKey) {
    const scrolled = stripRow.scrollLeft > 4 && stripKey !== '';
    stripKey = key;
    stripRow.replaceChildren(...strip.pictures.map(stripTile));
    if (!scrolled) stripRow.scrollLeft = 0;
  }
  let outlined = null;
  for (const tile of stripRow.children) {
    const on = Number(tile.dataset.id) === strip.selected;
    tile.classList.toggle('selected', on);
    if (on) { tile.setAttribute('aria-current', 'true'); outlined = tile; } else tile.removeAttribute('aria-current');
  }
  // The PC's pick moved while the drawer is open (later on 2026-10-15): its tile scrolled into view.
  if (drawerOpen && outlined && strip.selected !== lastOutlined) outlined.scrollIntoView({ inline: 'nearest', block: 'nearest', behavior: calm.matches ? 'auto' : 'smooth' });
  lastOutlined = strip.selected;
}

let lastOutlined = null;

// The drawer opening on the PC's pick (later on 2026-10-15): its tile in view at once, with no animation; none outlined opens at the newest.
function scrollToOutlined() {
  const tile = stripRow.querySelector('.strip-tile.selected');
  if (tile) tile.scrollIntoView({ inline: 'nearest', block: 'nearest', behavior: 'instant' });
}

function stripTile(p) {
  const b = el('button', 'strip-tile');
  b.type = 'button';
  b.dataset.id = String(p.id);
  if (p.key) b.dataset.key = p.key;
  b.title = p.name;
  b.setAttribute('aria-label', p.name);
  if (p.tooLarge) {
    b.classList.add('fallback');
    b.textContent = p.name;
  } else {
    const img = el('img');
    img.alt = p.name;
    img.loading = 'lazy';
    img.decoding = 'async';
    img.dataset.src = p.thumb;
    img.dataset.full = p.full;
    img.addEventListener('error', () => pictureFailed(img), { once: true });
    if (drawerOpen) img.src = p.thumb;   // none loads until the drawer first opens
    b.appendChild(img);
  }
  b.addEventListener('click', () => openStripPicture(p.id));
  return b;
}

function openStripPicture(id) {
  if (!strip) return;
  openOverlayOn(stripPictures(), id, true);
  selectOnPc(id, true);
}

// The PC's highlight and its open viewer moved to the picture: a tap at once, an overlay's step after 300 ms so a fast swipe moves it once.
function selectOnPc(id, now) {
  clearTimeout(selectTimer);
  const go = () => post('/api/strip/select', { id: id });
  if (now) go(); else selectTimer = setTimeout(go, 300);
}

function showDrawer(on) {
  const was = nearBottom();
  drawerOpen = on;
  drawerEl.classList.toggle('hidden', !on);
  if (on) for (const img of stripRow.querySelectorAll('img:not([src])')) img.src = img.dataset.src;
  keepBottom(was);   // the footer grew or shrank: the last line stays in view
  updateFloaters();
}

function openDrawer() {
  if (!strip || drawerOpen) return;
  closeUsage();
  if (promptsOpen) {
    hidePrompts(true);
    history.replaceState({ drawer: true }, '');   // the sheet's entry becomes the drawer's
  } else {
    history.pushState({ drawer: true }, '');
  }
  showDrawer(true);
  scrollToOutlined();
  // A tile is as wide as its picture, and the pictures start loading as the drawer opens, so the tiles before the outlined one widen after
  // the scroll and push it out of view (2026-10-19, the CI-like runs: the oldest of 30 out of view every tenth run or so, 12 of the 30
  // pictures still loading 10 s on). Each picture that arrives scrolls to it again, until the row is touched or the drawer goes.
  const loading = [...stripRow.querySelectorAll('img')].filter(img => !img.complete);
  if (loading.length) {
    const touched = new AbortController();
    const again = () => { if (drawerOpen) scrollToOutlined(); else touched.abort(); };
    for (const kind of ['pointerdown', 'wheel', 'touchstart', 'keydown']) {
      stripRow.addEventListener(kind, () => touched.abort(), { once: true, passive: true, signal: touched.signal });
    }
    for (const img of loading) {
      img.addEventListener('load', again, { once: true, signal: touched.signal });
      img.addEventListener('error', again, { once: true, signal: touched.signal });
    }
  }
  markSeen();
  const first = stripRow.querySelector('button');
  if (first) first.focus({ preventScroll: true });
}

function hideDrawer() {
  if (!drawerOpen) return;
  const focused = drawerEl.contains(document.activeElement);
  showDrawer(false);
  if (focused && !stripBtn.classList.contains('hidden')) stripBtn.focus({ preventScroll: true });
}

// ✕, Esc, a tap on the transcript, the usage card, a question: the drawer goes now, and its own history entry with it.
function closeDrawer() {
  if (!drawerOpen) return;
  hideDrawer();
  if (history.state && history.state.drawer && !history.state.overlay) history.back();
}

stripBtn.addEventListener('click', openDrawer);
stripClose.addEventListener('click', closeDrawer);
document.querySelector('main').addEventListener('click', ev => {
  if (drawerOpen && !ev.target.closest('.tile')) closeDrawer();
  if (promptsOpen && !ev.target.closest('.tile')) closePrompts(false);
});
stripRow.addEventListener('keydown', ev => {
  if (shown || (ev.key !== 'ArrowLeft' && ev.key !== 'ArrowRight')) return;
  const tiles = [...stripRow.children];
  const at = tiles.indexOf(document.activeElement);
  if (at < 0) return;
  const next = tiles[at + (ev.key === 'ArrowRight' ? 1 : -1)];
  if (next) { ev.preventDefault(); next.focus(); }
});
// One Esc, the topmost thing: the dropdown stops it on the box, the overlay and the usage card take it first.
document.addEventListener('keydown', ev => {
  if (ev.key !== 'Escape' || ev.defaultPrevented || shown || !drawerOpen || !usageCard.classList.contains('hidden')) return;
  ev.preventDefault();
  closeDrawer();
});

// The app's words on the buttons (sent with every snapshot).
function applyWords() {
  clearBtn.setAttribute('aria-label', words.clearDraft);
  clearBtn.title = words.clearDraft;
  toTopBtn.setAttribute('aria-label', words.toTop);
  toTopBtn.title = words.toTop;
  toBottomBtn.setAttribute('aria-label', words.toBottom);
  toBottomBtn.title = words.toBottom;
  stripBtn.setAttribute('aria-label', words.stripButton);
  stripBtn.title = words.stripButton;
  stripClose.setAttribute('aria-label', words.closeStrip);
  stripClose.title = words.closeStrip;
  drawerEl.setAttribute('aria-label', words.stripButton);
  promptsBtn.setAttribute('aria-label', words.promptsButton || 'Prompts, history and memory');
  promptsBtn.title = words.promptsButton || 'Prompts, history and memory';
  promptsEl.setAttribute('aria-label', words.promptsButton || 'Prompts, history and memory');
  promptsClose.setAttribute('aria-label', words.closePrompts || 'Close');
  promptsClose.title = words.closePrompts || 'Close';
  paintPromptsButton();
  for (const tab of promptsTabs) {
    const label = tabLabel(tab.dataset.tab);
    tab.querySelector('.tab-glyph').textContent = label.split(' ')[0];
    tab.querySelector('.tab-word').textContent = label.split(' ').slice(1).join(' ');
    tab.setAttribute('aria-label', label);
    tab.title = label;
  }
  if (promptsOpen) paintAction();
  overlayLive.setAttribute('aria-label', words.liveLabel || 'following new pictures');
  overlayLive.title = words.liveLabel || 'following new pictures';
  paintExpand();
}

// A reload keeps the history's entries but not what they held: the current one is the page's own again.
if (history.state && (history.state.overlay || history.state.drawer || history.state.prompts)) history.replaceState(null, '');

// ── The stream ────────────────────────────────────────────────────────

function connect() {
  source = new EventSource('/api/events');
  source.onopen = () => Neon.connected(true);
  source.onerror = () => {
    Neon.connected(false);
    // Signed out (a new token, another profile): the page itself says how to sign in again. Chat turned off (later on 2026-10-15): on to a
    // tab that is on. The pulse answers whatever tab is on.
    fetch('/api/pulse', { credentials: 'same-origin', cache: 'no-store' })
      .then(async r => {
        if (r.status === 401) { location.reload(); return; }
        const p = r.ok ? await r.json() : null;
        if (p && p.chatTab === false) Neon.leave();
      })
      .catch(() => {});
  };
  source.addEventListener('snapshot', ev => {
    const s = JSON.parse(ev.data);
    if (staleAssets(s.state.assets)) return;   // before drawing it
    if (s.words) words = s.words;
    applyWords();
    applyState(s.state);
    applyStatus(s.status);
    reset();
    goneKeys.clear();   // the server's pictures are what they are now; a gone file's tile shows its fallback anyway
    for (const entry of s.entries) renderEntry(entry);
    fitSplash();
    renderAsks(s.asks);
    for (const text of s.drafts || []) takeDraft(text);
    applyStrip(s.strip);
    // The PC's point: a fresh page takes its sequence without moving; a reconnect follows one newer than it had (the strip's view survives it).
    if (lastPointSeq === null) lastPointSeq = s.point ? s.point.seq : 0; else applyPoint(s.point);
    window.scrollTo(0, document.body.scrollHeight);
    floatSoon();
    // A video waiting for the YouTube tab (2026-10-16): last, once the asks are drawn, so a question is never left behind.
    Neon.watch(s.state.youTubeTab === true ? (s.state.watch || null) : null);
  });
  source.addEventListener('watch', ev => Neon.watch(JSON.parse(ev.data).text || null));
  source.addEventListener('strip', ev => applyStrip(JSON.parse(ev.data)));
  source.addEventListener('point', ev => applyPoint(JSON.parse(ev.data)));
  source.addEventListener('append', ev => append(JSON.parse(ev.data)));
  source.addEventListener('delta', ev => delta(JSON.parse(ev.data)));
  source.addEventListener('end', ev => end(JSON.parse(ev.data)));
  source.addEventListener('reset', () => reset());
  source.addEventListener('status', ev => applyStatus(JSON.parse(ev.data)));
  source.addEventListener('replace', ev => replaceRun(JSON.parse(ev.data)));
  source.addEventListener('asks', ev => renderAsks(JSON.parse(ev.data).asks));
  source.addEventListener('state', ev => {
    const s = JSON.parse(ev.data);
    if (staleAssets(s.assets)) return;
    applyState(s);
    Neon.watch(s.youTubeTab === true ? (s.watch || null) : null);
  });
  source.addEventListener('remove', ev => removeEntry(JSON.parse(ev.data).seq));
  source.addEventListener('draft', ev => takeDraft(JSON.parse(ev.data).text));
  source.addEventListener('withdrawn', ev => putBack(JSON.parse(ev.data).text));
  source.addEventListener('dropped', ev => putBack(JSON.parse(ev.data).text));
}

function reconnect() {
  if (source) source.close();
  connect();
}

try { draft.value = sessionStorage.getItem('neon.draft') || ''; } catch { /* private mode */ }
grow();

// Never jump to the YouTube tab mid-typing or over a question (2026-10-16): a draft with text, an ask card open, the 📣 sheet open (its filter
// or a two-tap delete half done); the banner instead.
Neon.watchBlocked = () => !!draft.value.trim() || asks.length > 0 || !promptsEl.classList.contains('hidden')
  || (document.activeElement && document.activeElement.tagName === 'INPUT');
draft.addEventListener('input', () => { saveDraft(); grow(); });
draft.addEventListener('keydown', ev => {
  if (ev.key === 'Enter' && desktop && !ev.shiftKey && !ev.isComposing && !completion) {
    ev.preventDefault();
    send(false);
  }
});
// ── The dropdown (later on 2026-10-14) ─────────────────────────────────
// What the app completes as the PC's line does: commands and their words (when Web UI commands is on) and the @ # $ % ^ * mentions. The text
// goes up to the end of the word under the cursor; an answer for a text changed since is dropped by its seq.

let completion = null;   // { start, end, items, cursor, truncated }
let completeSeq = 0;
let completeTimer = null;
let composing = false;

function wordEnd(text, at) {
  while (at < text.length && !/\s/.test(text[at])) at++;
  return at;
}

function hideList() {
  completion = null;
  completeEl.replaceChildren();
  completeEl.classList.add('hidden');
}

function queryCompletion() {
  clearTimeout(completeTimer);
  completeTimer = setTimeout(askCompletion, 120);
}

async function askCompletion() {
  if (composing || draft.selectionStart !== draft.selectionEnd) { hideList(); return; }
  const at = draft.selectionStart;
  const end = wordEnd(draft.value, at);
  const sent = draft.value.slice(0, end);
  if (!sent.trim() || sent.length > 4096) { hideList(); return; }
  const seq = ++completeSeq;
  const r = await post('/api/complete', { text: sent, cursor: at, seq: seq });
  const j = r.json;
  if (!r.ok || !j || j.seq !== completeSeq || draft.value.slice(0, end) !== sent || draft.selectionStart !== at) return;
  if (!j.items || !j.items.length) { hideList(); return; }
  completion = { start: j.start, end: j.end, items: j.items, cursor: 0, truncated: j.truncated };
  renderList();
}

function renderList() {
  completeEl.replaceChildren();
  completion.items.forEach((item, i) => {
    const row = el('div', 'option' + (i === completion.cursor ? ' on' : ''));
    row.setAttribute('role', 'option');
    row.appendChild(el('span', 'text', item.text));
    if (item.note) row.appendChild(el('span', 'note', item.note));
    // The tap keeps the draft's focus (and the phone's keyboard) up.
    row.addEventListener('pointerdown', ev => ev.preventDefault());
    row.addEventListener('click', () => applyItem(i));
    completeEl.appendChild(row);
  });
  if (completion.truncated) completeEl.appendChild(el('div', 'more', '…'));
  completeEl.classList.remove('hidden');
  const on = completeEl.querySelector('.on');
  if (on) on.scrollIntoView({ block: 'nearest' });
}

function applyItem(i) {
  const item = completion.items[i];
  const text = draft.value;
  draft.value = text.slice(0, completion.start) + item.insert + text.slice(completion.end);
  const pos = completion.start + item.insert.length;
  draft.setSelectionRange(pos, pos);
  hideList();
  saveDraft();
  grow();
  queryCompletion();   // a folder's list, an argument's after its command
}

// Ahead of the Enter-sends handler: while the list is open its keys are the list's.
draft.addEventListener('keydown', ev => {
  if (!completion || ev.isComposing || composing) return;
  const n = completion.items.length;
  switch (ev.key) {
    case 'ArrowDown': completion.cursor = (completion.cursor + 1) % n; renderList(); break;
    case 'ArrowUp': completion.cursor = (completion.cursor - 1 + n) % n; renderList(); break;
    case 'Tab':
    case 'Enter': applyItem(completion.cursor); break;
    case 'Escape': hideList(); break;
    default: return;
  }
  ev.preventDefault();
  ev.stopImmediatePropagation();
});
draft.addEventListener('compositionstart', () => { composing = true; });
draft.addEventListener('compositionend', () => { composing = false; queryCompletion(); });
draft.addEventListener('input', () => { if (!composing) queryCompletion(); });

// ── The message history (2026-10-16, the user's ask: the PC's ↑/↓ on the page) ──
// One history with the PC: the profile's lines, the page's among them (the app remembers a line as the page sends it). ↑ steps back through
// them, ↓ forward, and past the newest the draft in progress comes back — on a wrapped draft only from its first row (↑) or its end (↓), the
// box moving the caret first, as the PC's row does; a recalled line walks on until a key changes it. Read from the app as the box takes focus
// and as a walk starts (a 304 keeps the lines held); a line sent here joins the page's copy at once. What the ✕ clears goes to the walk's end,
// in this tab alone, as ESC's draft does at the PC: never sent, gone at a reload. With the completion list open its keys are the list's.

const msgHistory = { lines: [], cleared: [], index: -1, saved: '', walking: false };

function walkList() { return msgHistory.lines.concat(msgHistory.cleared); }

function historyReset() {
  msgHistory.index = -1;   // at the bottom
  msgHistory.walking = false;
}

async function readHistory() {
  const r = await Neon.get('/api/history', true);
  if (r.ok && r.json) takeLines(r.json.lines || []);
}

// The app's lines taken in. A walk under way keeps the line it shows (2026-10-19, the code review: the PC's lines added, the oldest dropped
// at the cap, and the walk's place then named another line): found again by its text, the nearest to where it was; gone, the walk goes on
// from the bottom, the draft it set aside kept.
function takeLines(lines) {
  const list = walkList();
  const at = msgHistory.index;
  msgHistory.lines = lines;
  if (at < 0) return;
  if (at >= list.length) { msgHistory.index = walkList().length; return; }   // walked back to the draft: still at the bottom
  const shown = list[at];
  const now = walkList();
  let best = -1;
  now.forEach((t, i) => { if (t === shown && (best < 0 || Math.abs(i - at) < Math.abs(best - at))) best = i; });
  msgHistory.index = best >= 0 ? best : now.length;
}

function remembered(text) {
  const lines = msgHistory.lines;
  if (text && lines[lines.length - 1] !== text) lines.push(text);
  msgHistory.cleared = msgHistory.cleared.filter(t => t !== text);
  historyReset();
}

function recall(text) {
  draft.value = text;   // no input event: the walk goes on, and the dropdown stays shut
  draft.setSelectionRange(text.length, text.length);
  saveDraft();
  grow();
}

// The box fits one row: ↑/↓ have no row to move to, so they walk at once.
function oneRow() {
  const style = getComputedStyle(draft);
  const line = parseFloat(style.lineHeight) || parseFloat(style.fontSize) * 1.4;
  return draft.scrollHeight - parseFloat(style.paddingTop) - parseFloat(style.paddingBottom) < line * 1.5;
}

function walkStep(delta) {
  const list = walkList();
  let at = msgHistory.index < 0 || msgHistory.index > list.length ? list.length : msgHistory.index;   // a list that shrank: clamped
  if (delta < 0) {
    if (at === 0) return;
    if (at === list.length && !msgHistory.walking) msgHistory.saved = draft.value;   // walking, the draft is the one set aside already
    at--;
    recall(list[at]);
  } else {
    if (at >= list.length) return;
    at++;
    recall(at === list.length ? msgHistory.saved : list[at]);
  }
  msgHistory.index = at;
  msgHistory.walking = true;
}

draft.addEventListener('focus', () => { readHistory(); });
draft.addEventListener('input', () => { msgHistory.walking = false; });   // a typed key ends the walk (the PC's "any other key")
draft.addEventListener('keydown', ev => {
  if (ev.key !== 'ArrowUp' && ev.key !== 'ArrowDown') return;
  if (completion || ev.isComposing || composing || ev.shiftKey || ev.ctrlKey || ev.altKey || ev.metaKey) return;
  const up = ev.key === 'ArrowUp';
  const caret = draft.selectionStart;
  const none = caret === draft.selectionEnd;
  const atEnd = none && (up ? caret === 0 : caret === draft.value.length);
  if (!msgHistory.walking && !atEnd && !oneRow()) return;   // the box moves the caret a row
  ev.preventDefault();
  walkKeys = walkKeys.then(() => walkKey(up)).catch(() => {});
});

// The walk's keys one after another (2026-10-19): a second ↑ pressed while the first still read the app's lines started a read of its own,
// then took the first's recalled line for a typed one and was dropped.
let walkKeys = Promise.resolve();

async function walkKey(up) {
  if (!msgHistory.walking && msgHistory.index < 0) {
    const before = draft.value;
    await readHistory();   // a walk starts on the app's lines now
    if (draft.value !== before) return;   // typed into meanwhile: the key is spent
  }
  walkStep(up ? -1 : 1);
}
draft.addEventListener('blur', () => setTimeout(hideList, 150));

sendBtn.addEventListener('click', () => send(false));
stopBtn.addEventListener('click', stop);
applyWords();
measure();
connect();
