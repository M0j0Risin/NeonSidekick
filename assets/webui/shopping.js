// NeonSidekick's Web UI, the shopping page (2026-10-14): the loaded profile's list walked through its store; a tap ticks or unticks a
// line. The list has no change event, so the page reads it again every five seconds while it is visible, and at once after a tap. The shared
// helpers are common.js's (2026-10-15): the post, the sentence, the dot, the self-reload after an upgrade.
// 2026-10-16 (the user's ask and picks): the /shopping pane's list controls. Over the list, the Add items box (Enter or ⊕), 🕘 for the past
// items' sheet, ☑ all / ☐ none and ⊠ clear ticked (two taps); each row's › (or a long-press) opens the line's sheet: − qty + (shown at once,
// posted once the taps stop), rename with Save, a two-tap 🗑️ remove. The sheets are the Home tab's (a back drop, a history entry, Back, ✕, Esc
// or a tap outside closes one), written here after home.js's. The read is tagged (a 304 keeps what is shown), and every word comes with the list.
'use strict';

const { el, say, post } = Neon;
const listEl = document.getElementById('list');
const headerEl = document.getElementById('header');
const storeBox = document.getElementById('store');
const storeLabel = storeBox.querySelector('span');
const storePick = storeBox.querySelector('select');
const addRow = document.getElementById('add');
const addText = document.getElementById('add-text');
const addGo = document.getElementById('add-go');
const pastButton = document.getElementById('past');
const bulkRow = document.getElementById('bulk');
const tickAll = document.getElementById('tick-all');
const clearButton = document.getElementById('clear');
const sheetBack = document.getElementById('sheet-back');
const sheetTitle = document.getElementById('sheet-title');
const sheetClose = document.getElementById('sheet-close');
const sheetNote = document.getElementById('sheet-note');
const lineBody = document.getElementById('line-body');
const qtyEl = document.getElementById('qty');
const qtyLess = document.getElementById('qty-less');
const qtyMore = document.getElementById('qty-more');
const renameText = document.getElementById('rename-text');
const renameSave = document.getElementById('rename-save');
const removeButton = document.getElementById('remove');
const pastBody = document.getElementById('past-body');
const pastCaption = document.getElementById('past-caption');
const pastFilter = document.getElementById('past-filter');
const pastList = document.getElementById('past-list');
const pastNone = document.getElementById('past-none');

let working = 0;          // posts in flight: the 5 s read waits for them
let pressing = false;     // a finger down on a row: a redraw would tear the long-press away
let pendingQty = null;    // { id, qty, timer }: the sheet's −/+ waiting for the taps to stop
let active = null;
let list = null;          // the last full body (a 304 keeps it)
let drawn = false;
let words = null;
let openSheet = null;     // { kind: 'line', id, qty, mayGo } | { kind: 'past' }
let bought = null;
let adding = false;

function held() {
  return working > 0 || pendingQty !== null || pressing;
}

// The reply's sentence under the page, and in the open sheet, which covers it.
function tell(text) {
  say(text || '');
  sheetNote.textContent = openSheet ? (text || '') : '';
  sheetNote.classList.toggle('hidden', !sheetNote.textContent);
}

// Every post: the poll held while it runs, the sentence shown, the list read again at once.
async function act(path, body) {
  working++;
  let r;
  try {
    r = await post(path, body);
  } finally {
    working--;
  }
  tell(r.message || (r.ok ? '' : 'That did not go through.'));
  drawn = false;
  poller.now();
  return r;
}

// ── The rows ─────────────────────────────────────────────────────────

// A tap ticks; a long-press (500 ms, cancelled by a lift or a move) opens the line's sheet and the click after it ticks nothing. The row's
// context menu is stopped, so a phone shows none.
function longPress(node, onLong) {
  let timer = null;
  let x = 0;
  let y = 0;
  const cancel = () => {
    if (timer) { clearTimeout(timer); timer = null; }
    pressing = false;
  };
  node.addEventListener('pointerdown', ev => {
    node.dataset.long = '';
    x = ev.clientX;
    y = ev.clientY;
    pressing = true;
    timer = setTimeout(() => { timer = null; pressing = false; node.dataset.long = '1'; onLong(); }, 500);
  });
  node.addEventListener('pointermove', ev => { if (Math.abs(ev.clientX - x) > 10 || Math.abs(ev.clientY - y) > 10) cancel(); });
  node.addEventListener('pointerup', cancel);
  node.addEventListener('pointercancel', cancel);
  node.addEventListener('contextmenu', ev => ev.preventDefault());
}

function row(item, guessMark) {
  const r = el('div', 'shop-row');
  const b = el('button', 'shop-item' + (item.checked ? ' checked' : ''));
  b.type = 'button';
  b.dataset.id = String(item.id);
  b.appendChild(el('span', 'box', item.checked ? '☑' : '☐'));
  const text = el('span', 'text');
  text.appendChild(el('span', 'name', item.text));
  if (item.note) text.appendChild(el('span', 'note', item.note));
  if (item.guess) text.appendChild(el('span', 'guess', guessMark));
  b.appendChild(text);
  b.addEventListener('click', () => {
    if (b.dataset.long === '1') { b.dataset.long = ''; return; }
    tick(item.id, !item.checked);
  });
  r.appendChild(b);
  if (words && list && list.writable) {
    longPress(b, () => openLine(item.id));
    const more = el('button', 'shop-more', '›');
    more.type = 'button';
    const label = words.options.replace('{0}', item.name || item.text);
    more.setAttribute('aria-label', label);
    more.title = label;
    more.addEventListener('click', () => openLine(item.id));
    r.appendChild(more);
  }
  return r;
}

// The store picker (2026-10-15): with two or more stores, the phone's own picker; a change walks the list through the new one.
function renderStores(l) {
  const stores = l.stores || [];
  storeBox.classList.toggle('hidden', stores.length < 2);
  if (stores.length < 2) return;
  storeLabel.textContent = l.storeLabel || 'Store';
  const key = stores.map(s => s.id + ':' + s.name).join('|');
  if (storePick.dataset.key !== key) {
    storePick.replaceChildren(...stores.map(s => { const o = el('option', null, s.name); o.value = String(s.id); return o; }));
    storePick.dataset.key = key;
  }
  active = l.active === undefined || l.active === null ? null : String(l.active);
  if (active !== null && document.activeElement !== storePick) storePick.value = active;
}

async function pickStore() {
  const picked = storePick.value;
  if (picked === active) return;
  working++;
  storePick.disabled = true;
  say('');
  try {
    const r = await post('/api/shopping/store', { id: Number(picked) });
    if (!r.ok) {
      storePick.value = active;   // back to the store the list is walked through
      say(r.message || 'That did not go through.');
    }
  } finally {
    working--;
    storePick.disabled = false;
    drawn = false;
    poller.now();
  }
}

storePick.addEventListener('change', pickStore);

function allItems(l) {
  const items = [];
  for (const g of l.groups || []) items.push(...g.items);
  items.push(...(l.ticked || []));
  return items;
}

// The controls over the list: none on the unavailable list (writable false) or from a server that sends no words; ⊠ only while a line is ticked.
function renderControls(l) {
  const on = !!(words && l.writable);
  addRow.classList.toggle('hidden', !on);
  const any = allItems(l).length > 0;
  bulkRow.classList.toggle('hidden', !on || !any);
  if (!on) return;
  addText.placeholder = words.addPlaceholder;
  addText.setAttribute('aria-label', words.addPlaceholder);
  addGo.setAttribute('aria-label', words.add);
  addGo.title = words.add;
  pastButton.setAttribute('aria-label', words.past);
  pastButton.title = words.past;
  tickAll.textContent = l.anyOpen ? words.tickAll : words.untickAll;
  const ticked = (l.ticked || []).length > 0;
  if (!ticked && clearButton.classList.contains('armed')) Neon.disarm();
  clearButton.classList.toggle('hidden', !ticked);
  if (!clearButton.classList.contains('armed')) paintClear(false);
}

function render(l) {
  words = l.words || null;
  renderStores(l);
  renderControls(l);
  headerEl.textContent = l.note || l.header || '';
  listEl.replaceChildren();
  for (const group of l.groups || []) {
    const g = el('section', 'shop-group');
    g.appendChild(el('h2', null, group.heading));
    for (const item of group.items) g.appendChild(row(item, l.guessMark));
    listEl.appendChild(g);
  }
  if ((l.ticked || []).length > 0) {
    const g = el('section', 'shop-group');
    g.appendChild(el('h2', null, l.tickedHeading));
    for (const item of l.ticked) g.appendChild(row(item, l.guessMark));
    listEl.appendChild(g);
  }
  followLine(l);
}

async function load() {
  if (held()) return;
  const r = await Neon.get('/api/shopping', true);
  if (!r.ok) return;
  if (r.json) {
    if (Neon.staleAssets(r.json.assets)) return;
    list = r.json;
    drawn = false;
  }
  if (list && !drawn && !held()) {
    render(list);
    drawn = true;
  }
}

async function tick(id, on) {
  say('');
  await act('/api/shopping/check', { id: id, on: on });
}

// ── Over the list ────────────────────────────────────────────────────

function syncAdd() {
  addGo.disabled = adding || addText.value.trim() === '';
}

async function addItems() {
  const text = addText.value;
  if (adding || text.trim() === '') return;
  adding = true;
  syncAdd();
  try {
    const r = await act('/api/shopping/add', { text });
    if (r.ok) addText.value = '';   // a refusal keeps the text to fix
  } finally {
    adding = false;
    syncAdd();
  }
}

addText.addEventListener('input', syncAdd);
addText.addEventListener('keydown', ev => {
  if (ev.key === 'Enter' && !ev.isComposing) {
    ev.preventDefault();
    addItems();
  }
});
addGo.addEventListener('click', addItems);
tickAll.addEventListener('click', () => { if (list) act('/api/shopping/checkall', { on: !!list.anyOpen }); });

function paintClear(on) {
  clearButton.classList.toggle('armed', on);
  const text = words ? (on ? words.clearArmed : words.clear) : '⊠';
  clearButton.textContent = text;
  clearButton.setAttribute('aria-label', text);
  clearButton.title = text;
}

Neon.armTwice(clearButton, paintClear, () => act('/api/shopping/clear', {}));
pastButton.addEventListener('click', openPast);

// ── The sheets ───────────────────────────────────────────────────────
// One at a time: a line's or the past items'. It pushes a history entry, so Back, ✕, Esc or a tap outside closes it; an installed iPhone app
// has no back swipe, so the ✕ always shows. The 5 s read redraws the list alone; the open line's sheet follows its line by id.

function showSheet(sheet) {
  const wasOpen = openSheet !== null;
  openSheet = sheet;
  sheetNote.textContent = '';
  sheetNote.classList.add('hidden');
  sheetClose.setAttribute('aria-label', words ? words.close : 'Close');
  sheetClose.title = words ? words.close : 'Close';
  lineBody.classList.toggle('hidden', sheet.kind !== 'line');
  pastBody.classList.toggle('hidden', sheet.kind !== 'past');
  sheetBack.classList.remove('hidden');
  if (!wasOpen) history.pushState({ sheet: true }, '');
  sheetClose.focus();
}

function closeSheet() {
  if (!openSheet) return;
  if (history.state && history.state.sheet) history.back(); else hideSheet();
}

function hideSheet() {
  Neon.disarm();
  if (pendingQty) flushQty();   // a sheet closed inside the wait posts at once
  openSheet = null;
  sheetBack.classList.add('hidden');
}

window.addEventListener('popstate', () => { if (openSheet) hideSheet(); });
sheetClose.addEventListener('click', closeSheet);
sheetBack.addEventListener('click', ev => { if (ev.target === sheetBack) closeSheet(); });
document.addEventListener('keydown', ev => {
  if (ev.key !== 'Escape') return;
  if (Neon.armed()) { ev.preventDefault(); Neon.disarm(); return; }
  if (openSheet) { ev.preventDefault(); closeSheet(); }
});

// ── The line's sheet ─────────────────────────────────────────────────

function lineOf(id) {
  return list ? allItems(list).find(i => i.id === id) || null : null;
}

function openLine(id) {
  const item = lineOf(id);
  if (!item || !words) return;
  showSheet({ kind: 'line', id, qty: item.qty || 1, mayGo: false });
  sheetTitle.textContent = item.name || item.text;
  qtyEl.textContent = String(item.qty || 1);
  qtyLess.setAttribute('aria-label', words.less);
  qtyLess.title = words.less;
  qtyMore.setAttribute('aria-label', words.more);
  qtyMore.title = words.more;
  qtyEl.title = words.quantity;
  renameText.value = item.name || '';
  renameText.setAttribute('aria-label', words.rename);
  renameSave.textContent = words.save;
  syncRename();
  paintRemove(false);
}

// The 5 s read: the open sheet's quantity and name follow the line unless a change waits or the rename box has focus; a line gone meanwhile
// closes the sheet with the sentence (quietly after a rename that merged it into another line, whose sentence is shown already).
function followLine(l) {
  if (!openSheet || openSheet.kind !== 'line') return;
  const item = allItems(l).find(i => i.id === openSheet.id);
  if (!item) {
    const quiet = openSheet.mayGo;
    closeSheet();
    if (!quiet && words) say(words.gone);
    return;
  }
  sheetTitle.textContent = item.name || item.text;
  if (!pendingQty) {
    openSheet.qty = item.qty || 1;
    qtyEl.textContent = String(openSheet.qty);
  }
  if (document.activeElement !== renameText) {
    renameText.value = item.name || '';
    syncRename();
  }
}

// − and + show the new quantity at once and post it 350 ms after the last tap: one request for a burst, the absolute quantity. The poll is held
// from the first tap, so a read can't draw the old quantity back while a change waits.
function stepQty(delta) {
  if (!openSheet || openSheet.kind !== 'line') return;
  const qty = Math.max(1, openSheet.qty + delta);
  openSheet.qty = qty;
  qtyEl.textContent = String(qty);
  if (pendingQty) clearTimeout(pendingQty.timer);
  pendingQty = { id: openSheet.id, qty, timer: setTimeout(flushQty, 350) };
}

async function flushQty() {
  if (!pendingQty) return;
  const p = pendingQty;
  clearTimeout(p.timer);
  pendingQty = null;
  await act('/api/shopping/quantity', { id: p.id, qty: p.qty });
}

qtyLess.addEventListener('click', () => stepQty(-1));
qtyMore.addEventListener('click', () => stepQty(1));

function syncRename() {
  renameSave.disabled = renameText.value.trim() === '';
}

async function saveRename() {
  const s = openSheet;
  const name = renameText.value.trim();
  if (!s || s.kind !== 'line' || name === '') return;
  if (pendingQty) await flushQty();
  const r = await act('/api/shopping/rename', { id: s.id, name });
  if (r.ok) {
    s.mayGo = true;   // a merge takes this line away: the sheet then closes without a second sentence
    renameText.blur();
  } else if (r.status === 404 && openSheet === s) {
    closeSheet();
  }
}

renameText.addEventListener('input', syncRename);
renameText.addEventListener('keydown', ev => {
  if (ev.key === 'Enter' && !ev.isComposing) {
    ev.preventDefault();
    saveRename();
  }
});
renameSave.addEventListener('click', saveRename);

function paintRemove(on) {
  removeButton.classList.toggle('armed', on);
  const text = words ? (on ? words.removeArmed : words.remove) : '🗑️';
  removeButton.textContent = text;
  removeButton.setAttribute('aria-label', text);
  removeButton.title = text;
}

Neon.armTwice(removeButton, paintRemove, () => {
  if (!openSheet || openSheet.kind !== 'line') return;
  const id = openSheet.id;
  if (pendingQty && pendingQty.id === id) { clearTimeout(pendingQty.timer); pendingQty = null; }   // it goes: no quantity after it
  openSheet.mayGo = true;
  closeSheet();
  act('/api/shopping/remove', { id });
});

// ── The past items' sheet ────────────────────────────────────────────
// What was bought before and is not on the list now, the PC's add page: a tap adds one again at its last quantity (the row leaves, the sheet
// stays for the next), a two-tap 🗑️ forgets it. A filter over 8 rows, as the prompts sheet's.

async function openPast() {
  if (!words) return;
  showSheet({ kind: 'past' });
  sheetTitle.textContent = words.pastTitle;
  pastFilter.value = '';
  pastFilter.placeholder = words.filter;
  pastFilter.setAttribute('aria-label', words.filter);
  bought = null;
  pastList.replaceChildren();
  pastCaption.textContent = '';
  await readPast();
}

async function readPast() {
  const r = await Neon.get('/api/shopping/bought', false);
  if (!openSheet || openSheet.kind !== 'past') return;
  if (!r.ok || !r.json) return;
  bought = r.json;
  drawPast();
}

function drawPast() {
  if (!bought || !words) return;
  Neon.disarm();
  pastCaption.textContent = bought.caption || '';
  const items = bought.items || [];
  pastFilter.classList.toggle('hidden', items.length <= 8);
  const q = pastFilter.classList.contains('hidden') ? '' : pastFilter.value.trim().toLowerCase();
  const shown = items.filter(i => !q || i.name.toLowerCase().includes(q));
  pastList.replaceChildren(...shown.map(pastRow));
  const none = items.length > 0 && shown.length === 0 ? words.noMatch.replace('{0}', pastFilter.value.trim()) : '';
  pastNone.textContent = none;
  pastNone.classList.toggle('hidden', !none);
}

function pastRow(item) {
  const r = el('div', 'shop-past-row');
  const add = el('button', 'shop-past-add', item.text);
  add.type = 'button';
  add.addEventListener('click', () => reAdd(item));
  r.appendChild(add);
  const forget = el('button', 'shop-forget', '🗑️');
  forget.type = 'button';
  Neon.armTwice(forget, Neon.paintArmed(forget, on => on ? words.forgetArmed : words.forget), () => forgetPast(item));
  r.appendChild(forget);
  return r;
}

async function reAdd(item) {
  if (bought) {
    bought.items = bought.items.filter(i => i.id !== item.id);   // the row leaves at once: a second tap finds nothing to add
    drawPast();
  }
  await act('/api/shopping/readd', { id: item.id });
  if (openSheet && openSheet.kind === 'past') await readPast();
}

async function forgetPast(item) {
  await act('/api/shopping/forget', { id: item.id });
  if (openSheet && openSheet.kind === 'past') await readPast();
}

pastFilter.addEventListener('input', drawPast);

const poller = Neon.poll(load, 5000);
// The Chat badge (2026-10-15): a reply running, a question waiting.
Neon.poll(async () => { const r = await Neon.get('/api/pulse', true); if (r.ok && r.json) Neon.pulse(r.json); }, 3000);
