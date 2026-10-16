// NeonSidekick's Web UI, the Home tab (2026-10-15): the home bar's items as tiles over Home Assistant's live states, a room's or a device's
// sheet, the scenes, Ask the house, and the cards (the PC's YouTube window, its meters, the model). Read every 1.5 seconds while visible (a 304
// when nothing changed), every 300 ms for a moment after a tap. The tab exists only while Web UI home controls is on at the PC (later on
// 2026-10-15: a read-only tab whose taps did nothing went); a tap that asks (an unlock, a garage, gate or door, a disarm) asks here first.
// Every word is the app's (the tab's words come with each read).
'use strict';

const { el, say, post } = Neon;
const statusEl = document.getElementById('ha-status');
const tilesEl = document.getElementById('tiles');
const scenesEl = document.getElementById('scenes');
const askEl = document.getElementById('ask');
const askText = document.getElementById('ask-text');
const askSend = document.getElementById('ask-send');
const answerEl = document.getElementById('ask-answer');
const videoEl = document.getElementById('video');
const pcEl = document.getElementById('pc');
const modelEl = document.getElementById('model');
const sheetBack = document.getElementById('sheet-back');
const sheetTitle = document.getElementById('sheet-title');
const sheetBody = document.getElementById('sheet-body');
const still = window.matchMedia('(prefers-reduced-motion: reduce)');
let home = null;
let words = null;
let quickUntil = 0;
let openSheet = null;   // { ref, timer }
let videoAt = null;   // { position, at } while playing: the clock runs on between reads

// ── Reading ───────────────────────────────────────────────────────────

async function load() {
  const r = await Neon.get('/api/home', true);
  if (!r.ok || !r.json) return;   // 304: as drawn
  if (Neon.staleAssets(r.json.pulse && r.json.pulse.assets)) return;
  home = r.json;
  words = home.words;
  render();
  if (Neon.pulse) Neon.pulse(home.pulse);
  // After a tap: quick reads until the tile settles, or six seconds pass.
  const pending = home.tiles.some(t => t.mark === 'pending');
  poller.every(Date.now() < quickUntil && pending ? 300 : 1500);
}

function render() {
  statusEl.textContent = home.statusText || '';
  statusEl.classList.toggle('hidden', !home.statusText);
  renderTiles();
  renderScenes();
  renderAsk();
  renderVideo();
  renderPc();
  renderModel();
}

// ── The tiles ─────────────────────────────────────────────────────────

function renderTiles() {
  tilesEl.replaceChildren();
  let group = null;
  let groupEl = null;
  for (const tile of home.tiles) {
    if (tile.group !== group) {
      group = tile.group;
      groupEl = el('div', 'tile-group');
      tilesEl.appendChild(groupEl);
    }
    groupEl.appendChild(tileButton(tile));
  }
}

function tileButton(tile) {
  const b = el('button', 'tile tone-' + tile.tone + (tile.mark ? ' ' + tile.mark : '') + (tile.sheet ? ' has-sheet' : ''));
  b.type = 'button';
  if (tile.color) b.style.setProperty('--tile', tile.color);
  b.appendChild(el('span', 'glyph', tile.glyph));
  const text = el('span', 'text');
  text.appendChild(el('span', 'name', tile.name));
  if (tile.value) text.appendChild(el('span', 'value', tile.value));
  b.appendChild(text);
  if (tile.sheet) b.appendChild(el('span', 'more', '›'));
  b.setAttribute('aria-label', tile.name + (tile.value ? ', ' + tile.value : ''));
  if (tile.tone === 'on' || tile.tone === 'off') b.setAttribute('aria-pressed', tile.tone === 'on' ? 'true' : 'false');
  b.title = tile.note;
  b.disabled = tile.action === 'none' && !tile.sheet;
  longPress(b, () => { if (tile.sheet) showSheet(tile.ref); }, () => tap(tile));
  return b;
}

// A tap acts; a long-press (or the ›) opens the sheet. A pointer-event timer: iOS sends no contextmenu, and the tile's CSS stops its text
// selection and its link menu.
function longPress(node, onLong, onTap) {
  let timer = null;
  let fired = false;
  let x = 0;
  let y = 0;
  node.addEventListener('pointerdown', ev => {
    fired = false;
    x = ev.clientX;
    y = ev.clientY;
    timer = setTimeout(() => { timer = null; fired = true; onLong(); }, 500);
  });
  const cancel = () => { if (timer) { clearTimeout(timer); timer = null; } };
  node.addEventListener('pointermove', ev => { if (Math.abs(ev.clientX - x) > 10 || Math.abs(ev.clientY - y) > 10) cancel(); });
  node.addEventListener('pointerup', cancel);
  node.addEventListener('pointercancel', cancel);
  node.addEventListener('click', ev => {
    if (fired) { fired = false; return; }
    if (ev.target.classList && ev.target.classList.contains('more')) { onLong(); return; }
    onTap();
  });
}

async function tap(tile, confirmed) {
  if (tile.action === 'detail') { if (tile.sheet) showSheet(tile.ref); return; }
  if (tile.action !== 'call' && tile.action !== 'confirm') return;
  say('');
  const r = await post('/api/home/tap', { ref: tile.ref, confirmed: !!confirmed });
  if (r.json && r.json.needsConfirm) { askFirst(r.json.prompt, () => tap(tile, true)); return; }
  if (!r.ok) { say(r.message || ''); return; }
  quickUntil = Date.now() + 6000;
  poller.every(300);
  poller.now();
}

// ── The sheet ─────────────────────────────────────────────────────────
// A room's devices or a device's actions, slid up from the bottom. It pushes a history entry, so Back, ✕ or a tap outside closes it; an
// installed iPhone app has no back swipe, so the ✕ always shows.

async function showSheet(ref) {
  const wasOpen = openSheet !== null;
  if (openSheet) clearInterval(openSheet.timer);
  openSheet = { ref, timer: setInterval(() => readSheet(ref), 1000) };
  sheetBack.classList.remove('hidden');
  if (!wasOpen) history.pushState({ sheet: true }, '');
  await readSheet(ref);
  document.getElementById('sheet-close').focus();
}

async function readSheet(ref) {
  const r = await Neon.get('/api/home/sheet?ref=' + encodeURIComponent(ref), false);
  if (!openSheet || openSheet.ref !== ref || openSheet.asking) return;
  if (!r.ok || !r.json) { if (r.status === 404) closeSheet(); return; }
  drawSheet(r.json);
}

function drawSheet(sheet) {
  sheetTitle.textContent = sheet.title + (sheet.state ? ' · ' + sheet.state : '');
  sheetBody.replaceChildren();
  for (const row of sheet.rows) {
    const line = el('div', 'sheet-row');
    const head = el('div', 'sheet-head');
    head.appendChild(el('span', 'glyph', row.glyph));
    head.appendChild(el('span', 'name', row.name));
    if (row.entity) head.appendChild(el('span', 'state', row.state));
    line.appendChild(head);
    if (row.actions.length) {
      const acts = el('div', 'sheet-actions');
      for (const a of row.actions) {
        const b = el('button', null, a.label);
        b.type = 'button';
        b.addEventListener('click', () => act(sheet.ref, row.entity, a));
        acts.appendChild(b);
      }
      line.appendChild(acts);
    }
    sheetBody.appendChild(line);
  }
}

async function act(ref, entity, action, confirmed) {
  say('');
  const r = await post('/api/home/act', { ref, entity, index: action.index, label: action.label, confirmed: !!confirmed });
  if (r.json && r.json.needsConfirm) { askFirst(r.json.prompt, () => act(ref, entity, action, true)); return; }
  if (!r.ok) say(r.message || '');
  quickUntil = Date.now() + 6000;
  poller.now();
  if (openSheet) readSheet(openSheet.ref);
}

// The question the bar asks at the PC, here: in the sheet (opened for it when none is), Yes or No.
function askFirst(prompt, yes) {
  const opened = openSheet === null;
  if (opened) {
    openSheet = { ref: null, timer: null };
    sheetBack.classList.remove('hidden');
    history.pushState({ sheet: true }, '');
  }
  openSheet.asking = true;
  const back = () => { openSheet.asking = false; if (opened) closeSheet(); else readSheet(openSheet.ref); };
  sheetTitle.textContent = prompt;
  sheetBody.replaceChildren();
  const choices = el('div', 'choices');
  const no = el('button', null, words.no);
  no.type = 'button';
  no.addEventListener('click', back);
  const ok = el('button', 'primary', words.yes);
  ok.type = 'button';
  ok.addEventListener('click', () => { back(); yes(); });
  choices.appendChild(no);
  choices.appendChild(ok);
  sheetBody.appendChild(choices);
  no.focus();
}

function closeSheet() {
  if (!openSheet) return;
  if (history.state && history.state.sheet) history.back(); else hideSheet();
}

function hideSheet() {
  if (openSheet && openSheet.timer) clearInterval(openSheet.timer);
  openSheet = null;
  sheetBack.classList.add('hidden');
}

window.addEventListener('popstate', () => { if (openSheet) hideSheet(); });
document.getElementById('sheet-close').addEventListener('click', closeSheet);
sheetBack.addEventListener('click', ev => { if (ev.target === sheetBack) closeSheet(); });
document.addEventListener('keydown', ev => { if (ev.key === 'Escape' && openSheet) { ev.preventDefault(); closeSheet(); } });

// ── Scenes, Ask the house ─────────────────────────────────────────────

function renderScenes() {
  const has = home.scenes.length > 0;
  scenesEl.classList.toggle('hidden', !has);
  if (!has) return;
  scenesEl.querySelector('summary').textContent = words.scenes;
  const row = scenesEl.querySelector('.scene-row');
  row.replaceChildren();
  for (const scene of home.scenes) {
    const b = el('button', null, scene.name);
    b.type = 'button';
    b.disabled = !home.controls;
    b.addEventListener('click', async () => {
      const r = await post('/api/home/scene', { id: scene.id });
      say(r.ok ? '' : r.message || '');
    });
    row.appendChild(b);
  }
}

function renderAsk() {
  askEl.classList.toggle('hidden', !home.controls || home.status === 'off');
  askEl.querySelector('h2').textContent = words.askTheHouse;
  askSend.textContent = words.ask;
}

async function sendAsk() {
  const text = askText.value.trim();
  if (!text) return;
  askSend.disabled = true;
  answerEl.classList.add('hidden');
  const r = await post('/api/home/say', { text });
  askSend.disabled = false;
  answerEl.textContent = r.message || '';
  answerEl.classList.toggle('hidden', !r.message);
  answerEl.classList.toggle('failed', !r.ok);
  if (r.ok) askText.value = '';
}

askSend.addEventListener('click', sendAsk);
askText.addEventListener('keydown', ev => { if (ev.key === 'Enter' && !ev.isComposing) { ev.preventDefault(); sendAsk(); } });

// ── The cards ─────────────────────────────────────────────────────────

function clock(seconds) {
  const t = Math.max(0, Math.round(seconds));
  const h = Math.floor(t / 3600);
  const two = n => String(n).padStart(2, '0');
  return (h > 0 ? h + ':' + two(Math.floor(t % 3600 / 60)) : Math.floor(t / 60)) + ':' + two(t % 60);
}

const Remote = [
  ['previous', '⏮', 'Previous'], ['back', '⏪', 'Back 10 seconds'], ['playpause', '⏯', 'Play or pause'], ['forward', '⏩', 'Forward 10 seconds'],
  ['next', '⏭', 'Next'], ['quieter', '🔉', 'Quieter'], ['louder', '🔊', 'Louder'], ['mute', '🔇', 'Mute or unmute'],
];

function renderVideo() {
  const v = home.video;
  videoEl.classList.toggle('hidden', !v);
  if (!v) { videoAt = null; return; }
  videoAt = v.state === 'playing' ? { position: v.position, at: performance.now() } : null;
  videoEl.replaceChildren();
  videoEl.appendChild(el('h2', null, words.nowPlaying));
  videoEl.appendChild(el('div', 'video-title', v.title || ''));
  // Where it plays (2026-10-16): the PC's window or the YouTube tab, the server's words.
  const meta = [v.author, v.state, v.where, v.count ? v.entry + ' / ' + v.count : null, v.muted ? '🔇' : '🔊 ' + v.volume].filter(x => x).join(' · ');
  videoEl.appendChild(el('div', 'video-meta', meta));
  videoEl.appendChild(el('div', 'video-clock', clock(v.position) + (v.duration ? ' / ' + clock(v.duration) : '')));
  if (v.failure) videoEl.appendChild(el('div', 'failed', v.failure));
  // A video waiting for its YouTube tab has nothing to drive yet (2026-10-16): no remote.
  if (!home.controls || v.state === 'waiting') return;
  const row = el('div', 'remote');
  for (const [action, glyph, label] of Remote) {
    if ((action === 'previous' || action === 'next') && !(v.count > 1)) continue;
    const b = el('button', null, glyph);
    b.type = 'button';
    b.setAttribute('aria-label', label);
    b.title = label;
    b.addEventListener('click', async () => {
      const sent = action === 'playpause' ? (v.state === 'playing' ? 'pause' : 'play') : action === 'mute' ? (v.muted ? 'unmute' : 'mute') : action;
      const r = await post('/api/home/video', { action: sent });
      say(r.ok ? '' : r.message || '');
      poller.now();
    });
    row.appendChild(b);
  }
  videoEl.appendChild(row);
}

// The clock runs on between reads while the video plays.
setInterval(() => {
  if (!videoAt || !home || !home.video) return;
  const c = videoEl.querySelector('.video-clock');
  if (!c) return;
  const at = videoAt.position + (performance.now() - videoAt.at) / 1000;
  c.textContent = clock(home.video.duration ? Math.min(at, home.video.duration) : at) + (home.video.duration ? ' / ' + clock(home.video.duration) : '');
}, 1000);

function meter(label, value) {
  const row = el('div', 'meter');
  row.appendChild(el('span', 'label', label));
  const bar = el('div', 'bar');
  const fill = el('i');
  fill.style.width = Math.max(0, Math.min(100, value)) + '%';
  bar.appendChild(fill);
  row.appendChild(bar);
  row.appendChild(el('span', 'value', value + '%'));
  return row;
}

function renderPc() {
  const p = home.pc;
  pcEl.classList.toggle('hidden', !p);
  if (!p) return;
  pcEl.replaceChildren();
  pcEl.appendChild(el('h2', null, words.thePc));
  for (const [label, value] of [['CPU', p.cpu], ['RAM', p.ram], ['GPU', p.gpu], ['VRAM', p.vram]]) {
    if (value !== undefined && value !== null) pcEl.appendChild(meter(label, value));
  }
  if (p.down || p.up) pcEl.appendChild(el('div', 'net', '↓ ' + (p.down || '–') + ' ' + words.down + ' · ↑ ' + (p.up || '–') + ' ' + words.up));
}

function renderModel() {
  const m = home.model;
  modelEl.replaceChildren();
  modelEl.appendChild(el('h2', null, words.model));
  modelEl.appendChild(el('div', 'model-name', m.model || '–'));
  const facts = [m.server, m.context ? m.context.toLocaleString('en-US') + ' ' + words.window : null, m.loading ? words.loading : m.busy ? words.busy : null].filter(x => x);
  modelEl.appendChild(el('div', 'model-meta', facts.join(' · ')));
  if (m.usage) modelEl.appendChild(el('div', 'model-usage', [m.usage.tokens, m.usage.percent, m.usage.speed].filter(x => x).join(' · ')));
}

const poller = Neon.poll(load, 1500);
if (still.matches) document.documentElement.classList.add('still');
