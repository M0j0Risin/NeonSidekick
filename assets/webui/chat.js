// NeonSidekick's Web UI, the chat page (2026-10-14): the transcript as the app draws it, streamed over server-sent events (a snapshot on
// every (re)connect, then the changes), the page's own draft line, Stop, and the asks the app's panes put — answered here or at the PC,
// the first answer winning. Every word the asks show comes from the app; text is set with textContent, and the only HTML is a finished
// reply's, which the app renders with raw HTML escaped and the links vetted.
// Pictures (later on 2026-10-14): a picture entry carries the addresses the server built; the grid loads them lazily, three at a time, and a
// tap opens the overlay (the middle size, swipe or ‹ › for the others, Back closes it). The page reloads itself when the app serves other
// files than the ones it runs (an upgrade), once per tag.
'use strict';

const logEl = document.getElementById('log');
const draft = document.getElementById('draft');
const sendBtn = document.getElementById('send');
const stopBtn = document.getElementById('stop');
const asksEl = document.getElementById('asks');
const waitingEl = document.getElementById('waiting');
const loadingEl = document.getElementById('loading');
const queuedEl = document.getElementById('queued');
const messageEl = document.getElementById('message');
const completeEl = document.getElementById('complete');
const profileEl = document.getElementById('profile');
const modelEl = document.getElementById('model');
const dot = document.getElementById('dot');

const overlayEl = document.getElementById('overlay');
const overlayImg = document.getElementById('overlay-img');
const overlayName = document.getElementById('overlay-name');
const overlayOpen = document.getElementById('overlay-open');
const overlayPrev = document.getElementById('overlay-prev');
const overlayNext = document.getElementById('overlay-next');
const overlayClose = document.getElementById('overlay-close');

const desktop = window.matchMedia('(pointer: fine)').matches;
const entries = new Map();   // seq → { el, body, kind, text, pictures }
let words = {
  pictureGone: 'is no longer there.', pictureTooLarge: 'is too large to show here.', openOriginal: 'Open original',
  sendAsMessage: 'Send as a message', useLine: 'Use', draftFilled: 'Put on the draft.', undo: 'Undo',
};
let assetsTag = null;
let state = null;
let asks = [];
let source = null;
let pendingId = null;
let pendingText = null;
let lastAskCount = 0;

function el(tag, cls, text) {
  const e = document.createElement(tag);
  if (cls) e.className = cls;
  if (text !== undefined && text !== null) e.textContent = text;
  return e;
}

function say(text) {
  messageEl.replaceChildren(document.createTextNode(text || ''));
  messageEl.classList.toggle('hidden', !text);
}

function nearBottom() {
  return window.innerHeight + window.scrollY >= document.body.scrollHeight - 80;
}

function keepBottom(was) {
  if (was) window.scrollTo(0, document.body.scrollHeight);
}

function newId() {
  return Date.now().toString(36) + Math.random().toString(36).slice(2, 10);
}

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

// A sentence under the composer, with buttons the app offered (later on 2026-10-14): a refused command's Send as a message and Use …, a filled
// draft's Undo.
function sayWith(text, actions) {
  messageEl.replaceChildren(document.createTextNode(text || ''));
  for (const a of actions || []) {
    const b = el('button', 'inline', a.label);
    b.type = 'button';
    b.addEventListener('click', () => { say(''); a.run(); });
    messageEl.appendChild(b);
  }
  messageEl.classList.toggle('hidden', !text);
}

// ── The transcript ────────────────────────────────────────────────────

function renderEntry(entry) {
  let node;
  const record = { kind: entry.kind, text: entry.text, body: null, el: null };
  switch (entry.kind) {
    case 'separator':
      node = el('hr', 'entry separator');
      break;
    case 'assistant': {
      node = el('div', 'entry assistant');
      node.appendChild(el('span', 'glyph', entry.glyph || '●'));
      const body = el('div', 'body');
      if (entry.html) {
        body.innerHTML = entry.html;
      } else {
        body.classList.add('streaming');
        body.textContent = entry.text;
      }
      node.appendChild(body);
      record.body = body;
      break;
    }
    case 'user': {
      node = el('div', 'entry user');
      node.appendChild(el('span', 'glyph', (state && state.userAvatar) || '›'));
      node.appendChild(document.createTextNode(entry.text));
      break;
    }
    case 'working':
      node = el('div', 'entry working', '⏳ ' + entry.text);
      break;
    case 'picture':
      if (entry.pictures && entry.pictures.length) {
        node = el('div', 'entry pictures');
        const grid = el('div', 'grid');
        entry.pictures.forEach(p => grid.appendChild(pictureTile(p)));
        node.appendChild(grid);
        record.pictures = entry.pictures;
      } else {
        node = el('div', 'entry picture', '🖼️ ' + entry.text);
      }
      break;
    default:
      node = el('div', 'entry ' + entry.kind + (entry.failed ? ' failed' : ''), entry.text);
  }
  record.el = node;
  entries.set(entry.seq, record);
  logEl.appendChild(node);
}

function append(entry) {
  if (entries.has(entry.seq)) return;
  const was = nearBottom();
  renderEntry(entry);
  keepBottom(was);
}

function delta(d) {
  const record = entries.get(d.seq);
  if (!record || !record.body) { reconnect(); return; }
  if (d.at === record.text.length) {
    record.text += d.text;
    const was = nearBottom();
    record.body.textContent = record.text;
    keepBottom(was);
  } else if (d.at + d.text.length > record.text.length) {
    reconnect();   // a gap: start again from a snapshot
  }
}

function end(e) {
  const record = entries.get(e.seq);
  if (!record || !record.body) return;
  const was = nearBottom();
  record.body.classList.remove('streaming');
  record.body.innerHTML = e.html;
  keepBottom(was);
}

function reset() {
  entries.clear();
  logEl.replaceChildren();
  loadQueue.length = 0;
  closeOverlay();
}

function removeEntry(seq) {
  const record = entries.get(seq);
  if (!record) return;
  record.el.remove();
  entries.delete(seq);
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

function pictureTile(p) {
  const tile = el('button', 'tile');
  tile.type = 'button';
  tile.title = p.name;
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
  img.dataset.src = p.thumb;
  tile.appendChild(img);
  tile.addEventListener('click', () => openOverlay(p.id));
  if (seen) seen.observe(img); else { loadQueue.push(img); pump(); }
  return tile;
}

function pictureFailed(img) {
  const tile = img.closest('.tile');
  if (!tile) return;
  tile.classList.add('fallback');
  tile.replaceChildren(document.createTextNode(img.alt + ' ' + words.pictureGone));
}

// ── The overlay ───────────────────────────────────────────────────────

let shown = null;   // { list, at }

function allPictures() {
  const list = [];
  for (const record of entries.values()) for (const p of record.pictures || []) list.push(p);
  return list;
}

function openOverlay(id) {
  const list = allPictures().filter(p => !p.tooLarge);
  const at = list.findIndex(p => p.id === id);
  if (at < 0) return;
  const wasOpen = shown !== null;
  shown = { list, at };
  showPicture();
  overlayEl.classList.remove('hidden');
  if (!wasOpen) history.pushState({ overlay: true }, '');
}

function showPicture() {
  const p = shown.list[shown.at];
  overlayImg.alt = p.name;
  overlayImg.src = p.mid;
  overlayName.textContent = p.name;
  overlayOpen.textContent = words.openOriginal;
  overlayOpen.href = p.full;
  overlayPrev.disabled = shown.at === 0;
  overlayNext.disabled = shown.at === shown.list.length - 1;
}

function step(by) {
  if (!shown) return;
  const at = shown.at + by;
  if (at < 0 || at >= shown.list.length) return;
  shown.at = at;
  showPicture();
}

// Every close goes through the history entry the open pushed, so Back, ✕, Esc and a tap end the same way.
function closeOverlay() {
  if (!shown) return;
  if (history.state && history.state.overlay) history.back(); else hideOverlay();
}

function hideOverlay() {
  shown = null;
  overlayEl.classList.add('hidden');
  overlayImg.removeAttribute('src');
}

window.addEventListener('popstate', () => { if (shown) hideOverlay(); });
overlayClose.addEventListener('click', closeOverlay);
overlayPrev.addEventListener('click', ev => { ev.stopPropagation(); step(-1); });
overlayNext.addEventListener('click', ev => { ev.stopPropagation(); step(1); });
overlayEl.addEventListener('click', ev => { if (ev.target === overlayEl || ev.target === overlayImg) closeOverlay(); });
overlayOpen.addEventListener('click', ev => ev.stopPropagation());
document.addEventListener('keydown', ev => {
  if (!shown) return;
  if (ev.key === 'Escape') { ev.preventDefault(); closeOverlay(); }
  else if (ev.key === 'ArrowLeft') step(-1);
  else if (ev.key === 'ArrowRight') step(1);
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
  if (!tag) return false;
  if (assetsTag === null) { assetsTag = tag; return false; }
  if (tag === assetsTag) return false;
  let tried = null;
  try { tried = sessionStorage.getItem('neon.reloaded'); } catch { /* private mode */ }
  if (tried === tag) return false;
  try { sessionStorage.setItem('neon.reloaded', tag); } catch { /* private mode */ }
  saveDraft();
  location.reload();
  return true;
}

// ── The state ─────────────────────────────────────────────────────────

function applyState(s) {
  state = s;
  profileEl.textContent = (s.modelAvatar ? s.modelAvatar + ' ' : '') + s.profile + (s.planMode ? ' · plan' : '');
  modelEl.textContent = s.model + (s.title ? ' · ' + s.title : '');
  stopBtn.classList.toggle('hidden', !s.busy);
  loadingEl.textContent = s.loading ? 'Loading the model…' : '';
  loadingEl.classList.toggle('hidden', !s.loading);
  waitingEl.textContent = s.waitingAtPc ? '⏸ Waiting for an answer at the PC: ' + s.waitingAtPc : '';
  waitingEl.classList.toggle('hidden', !s.waitingAtPc);
  queuedEl.replaceChildren();
  for (const line of s.queued || []) queuedEl.appendChild(el('div', null, '⏳ ' + line.replace(/\s+/g, ' ')));
  queuedEl.classList.toggle('hidden', !(s.queued || []).length);
  updateTitle();
}

function updateTitle() {
  const name = state ? state.profile : 'NeonSidekick';
  document.title = (asks.length ? '(' + asks.length + ') ' : '') + name;
}

// ── The asks ──────────────────────────────────────────────────────────

function renderAsks(list) {
  asks = list || [];
  asksEl.replaceChildren();
  for (const ask of asks) asksEl.appendChild(askCard(ask));
  if (asks.length > lastAskCount && navigator.vibrate) {
    try { navigator.vibrate(200); } catch { /* not allowed */ }
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
  return card;
}

// A pick card (later on 2026-10-14: /model, /reasoning, /rewind from the page): a filter over the rows, the one in use marked and in view.
function pickList(card, ask) {
  const box = el('div', 'pick');
  const filter = el('input');
  filter.type = 'search';
  filter.placeholder = 'Filter';
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

function grow() {
  draft.style.height = 'auto';
  draft.style.height = Math.min(draft.scrollHeight, window.innerHeight * 0.4) + 'px';
}

function putBack(text) {
  draft.value = draft.value ? text + '\n' + draft.value : text;
  saveDraft();
  grow();
}

async function send(asText) {
  const text = draft.value;
  if (!text.trim()) return;
  if (pendingText !== text) { pendingId = newId(); pendingText = text; }
  sendBtn.disabled = true;
  const r = await post('/api/send', { id: pendingId, text: text, asText: !!asText });
  sendBtn.disabled = false;
  if (r.ok) {
    if (draft.value === text) draft.value = '';
    pendingId = null;
    pendingText = null;
    hideList();
    saveDraft();
    grow();
    say('');
    return;
  }
  // A refused command (later on 2026-10-14): the app's reason, and what it offers instead.
  const actions = [];
  if (r.use) actions.push({ label: words.useLine + ' ' + r.use, run: () => { draft.value = r.use + ' '; saveDraft(); grow(); draft.focus(); } });
  if (r.asText) actions.push({ label: words.sendAsMessage, run: () => send(true) });
  sayWith(r.message || 'That message did not go through.', actions);
}

// A text a command put on the draft (/prompts <n>, /rewind): ahead of what was there, with an undo.
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

// ── The stream ────────────────────────────────────────────────────────

function connect() {
  source = new EventSource('/api/events');
  source.onopen = () => dot.classList.add('on');
  source.onerror = () => {
    dot.classList.remove('on');
    // Signed out (a new token, another profile): the page itself says how to sign in again.
    fetch('/api/shopping', { credentials: 'same-origin', cache: 'no-store' }).then(r => { if (r.status === 401) location.reload(); }).catch(() => {});
  };
  source.addEventListener('snapshot', ev => {
    const s = JSON.parse(ev.data);
    if (staleAssets(s.state.assets)) return;   // before drawing it
    if (s.words) words = s.words;
    applyState(s.state);
    reset();
    for (const entry of s.entries) renderEntry(entry);
    renderAsks(s.asks);
    for (const text of s.drafts || []) takeDraft(text);
    window.scrollTo(0, document.body.scrollHeight);
  });
  source.addEventListener('append', ev => append(JSON.parse(ev.data)));
  source.addEventListener('delta', ev => delta(JSON.parse(ev.data)));
  source.addEventListener('end', ev => end(JSON.parse(ev.data)));
  source.addEventListener('reset', () => reset());
  source.addEventListener('asks', ev => renderAsks(JSON.parse(ev.data).asks));
  source.addEventListener('state', ev => {
    const s = JSON.parse(ev.data);
    if (!staleAssets(s.assets)) applyState(s);
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
draft.addEventListener('blur', () => setTimeout(hideList, 150));

sendBtn.addEventListener('click', () => send(false));
stopBtn.addEventListener('click', stop);
connect();
