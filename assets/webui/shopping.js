// NeonSidekick's Web UI, the shopping page (2026-10-14): the loaded profile's list walked through its store; a tap ticks or unticks a
// line. The list has no change event, so the page reads it again every five seconds while it is visible, and at once after a tap. The shared
// helpers are common.js's (2026-10-15): the post, the sentence, the dot, the self-reload after an upgrade.
'use strict';

const { el, say, post } = Neon;
const listEl = document.getElementById('list');
const headerEl = document.getElementById('header');
const storeBox = document.getElementById('store');
const storeLabel = storeBox.querySelector('span');
const storePick = storeBox.querySelector('select');
let busy = false;
let active = null;

function row(item, guessMark) {
  const b = el('button', 'shop-item' + (item.checked ? ' checked' : ''));
  b.type = 'button';
  b.appendChild(el('span', 'box', item.checked ? '☑' : '☐'));
  const text = el('span', 'text');
  text.appendChild(el('span', 'name', item.text));
  if (item.note) text.appendChild(el('span', 'note', item.note));
  if (item.guess) text.appendChild(el('span', 'guess', guessMark));
  b.appendChild(text);
  b.addEventListener('click', () => tick(item.id, !item.checked));
  return b;
}

// The store picker (2026-10-15): with two or more stores, the phone's own picker; a change walks the list through the new one.
function renderStores(list) {
  const stores = list.stores || [];
  storeBox.classList.toggle('hidden', stores.length < 2);
  if (stores.length < 2) return;
  storeLabel.textContent = list.storeLabel || 'Store';
  const key = stores.map(s => s.id + ':' + s.name).join('|');
  if (storePick.dataset.key !== key) {
    storePick.replaceChildren(...stores.map(s => { const o = el('option', null, s.name); o.value = String(s.id); return o; }));
    storePick.dataset.key = key;
  }
  active = list.active === undefined || list.active === null ? null : String(list.active);
  if (active !== null && document.activeElement !== storePick) storePick.value = active;
}

async function pickStore() {
  const picked = storePick.value;
  if (picked === active) return;
  busy = true;
  storePick.disabled = true;
  say('');
  try {
    const r = await post('/api/shopping/store', { id: Number(picked) });
    if (!r.ok) {
      storePick.value = active;   // back to the store the list is walked through
      say(r.message || 'That did not go through.');
    }
  } finally {
    busy = false;
    storePick.disabled = false;
    poller.now();
  }
}

storePick.addEventListener('change', pickStore);

function render(list) {
  renderStores(list);
  headerEl.textContent = list.note || list.header || '';
  listEl.replaceChildren();
  for (const group of list.groups || []) {
    const g = el('section', 'shop-group');
    g.appendChild(el('h2', null, group.heading));
    for (const item of group.items) g.appendChild(row(item, list.guessMark));
    listEl.appendChild(g);
  }
  if ((list.ticked || []).length > 0) {
    const g = el('section', 'shop-group');
    g.appendChild(el('h2', null, list.tickedHeading));
    for (const item of list.ticked) g.appendChild(row(item, list.guessMark));
    listEl.appendChild(g);
  }
}

async function load() {
  if (busy) return;
  const r = await Neon.get('/api/shopping', false);
  if (r.ok && r.json) {
    if (Neon.staleAssets(r.json.assets)) return;
    render(r.json);
  }
}

async function tick(id, on) {
  busy = true;
  say('');
  try {
    const r = await post('/api/shopping/check', { id: id, on: on });
    if (!r.ok) say(r.message || 'That did not go through.');
  } finally {
    busy = false;
    poller.now();
  }
}

const poller = Neon.poll(load, 5000);
// The Chat badge (2026-10-15): a reply running, a question waiting.
Neon.poll(async () => { const r = await Neon.get('/api/pulse', true); if (r.ok && r.json) Neon.pulse(r.json); }, 3000);
