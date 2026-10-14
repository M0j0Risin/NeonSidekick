// NeonSidekick's Web UI, the shopping page (2026-10-14): the loaded profile's list walked through its store; a tap ticks or unticks a
// line. The list has no change event, so the page reads it again every five seconds while it is visible, and at once after a tap.
'use strict';

const listEl = document.getElementById('list');
const headerEl = document.getElementById('header');
const messageEl = document.getElementById('message');
const dot = document.getElementById('dot');
let busy = false;

function el(tag, cls, text) {
  const e = document.createElement(tag);
  if (cls) e.className = cls;
  if (text !== undefined && text !== null) e.textContent = text;
  return e;
}

function say(text) {
  messageEl.textContent = text || '';
  messageEl.classList.toggle('hidden', !text);
}

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

function render(list) {
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
  try {
    const r = await fetch('/api/shopping', { credentials: 'same-origin', cache: 'no-store' });
    if (r.status === 401) { location.reload(); return; }
    dot.classList.toggle('on', r.ok);
    if (r.ok) render(await r.json());
  } catch {
    dot.classList.remove('on');
  }
}

async function tick(id, on) {
  busy = true;
  say('');
  try {
    const r = await fetch('/api/shopping/check', {
      method: 'POST',
      credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json', 'X-Neon': '1' },
      body: JSON.stringify({ id: id, on: on }),
    });
    if (r.status === 401) { location.reload(); return; }
    if (!r.ok) {
      let j = null;
      try { j = await r.json(); } catch { /* none */ }
      say(j && j.message ? j.message : 'That did not go through.');
    }
  } catch {
    say('The app could not be reached.');
  } finally {
    busy = false;
    load();
  }
}

load();
setInterval(() => { if (document.visibilityState === 'visible') load(); }, 5000);
document.addEventListener('visibilitychange', () => { if (document.visibilityState === 'visible') load(); });
