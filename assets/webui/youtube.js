// NeonSidekick's Web UI, the YouTube tab (2026-10-16): the app's video player on the phone or a browser, and (later that day) its saved list.
// The player is the PC window's own (player-core.js, served as /youtube-core.js); this page is its transport — its own event stream from the
// app (hello, load, cmd, owner, resume, taken, close) and its reports posted back, the same JSON the PC's page posts. A page is this browser
// tab: its id lives in sessionStorage (a reload keeps it, a new tab gets its own), and each script load makes a nonce kept in memory, so the
// app tells a reconnect from a reload from a duplicated tab. Each page says whether it is in view (visible= on its stream, then a seen post on
// every change, as it goes, and on every reconnect, since the browser's own reconnect reuses the first URL): a play goes to the tab shown most
// recently, and while nothing plays the player follows it; a tab that is playing keeps it until a new play or a Play here; another waits with
// Play here, which takes the video with it (the hello says play, and this page resumes it). Leaving the tab stops the video (its place kept by
// the app), and coming back offers Resume. Under the player, the saved videos: play (a tap from a waiting page claims first), filter, move,
// remove, clear all, save the one playing. Every word is the app's.
'use strict';

const hintEl = document.getElementById('hint');
const playHereBtn = document.getElementById('play-here');
const resumeBtn = document.getElementById('resume');
const frameEl = document.querySelector('.yt-frame');
const PageKey = 'neon.youtube.page';
const IdShape = /^[A-Za-z0-9_-]{16,64}$/;

function newId() {
  const bytes = new Uint8Array(16);
  crypto.getRandomValues(bytes);
  return btoa(String.fromCharCode(...bytes)).replace(/=+$/, '').replace(/\+/g, '-').replace(/\//g, '_');
}

let page = null;
try { page = sessionStorage.getItem(PageKey); } catch { /* private mode: a fresh id for this page's life */ }
if (!page || !IdShape.test(page)) {
  page = newId();
  try { sessionStorage.setItem(PageKey, page); } catch { /* private mode */ }
}
const load = newId();

let words = null;
let owner = false;
let offer = null;
let loaded = false;
let apiWaitMs = 10000;
let apiFailed = false;
let apiTimer = null;
let hello = false;
let source = null;
let tapHint = '';

// A report: the player's own JSON as a string inside the post. A tick that hits the limit costs nothing (the next comes a second later); a
// report that changes who plays or closes a video waits Retry-After and goes once more.
async function report(message) {
  const important = message.ev === 'resumed' || message.ev === 'failed' || message.ev === 'page';
  const body = { page, message: JSON.stringify(message) };
  const r = await Neon.post('/api/youtube/report', body);
  if (r.status === 429 && important) {
    await new Promise(done => setTimeout(done, Math.max(1, r.retryAfter || 1) * 1000));
    await Neon.post('/api/youtube/report', body);
  }
}

// In view or not (later on 2026-10-16): the app plays on the tab shown most recently.
function visible() { return document.visibilityState === 'visible'; }

function seen(keepalive) {
  return Neon.post('/api/youtube/seen', { page, visible: keepalive ? false : visible() }, keepalive ? { keepalive: true } : undefined);
}

document.addEventListener('visibilitychange', () => { seen(false); });
window.addEventListener('pagehide', () => { seen(true); });

function send(message) {
  if (message.ev === 'state') {
    // The phone's autoplay rules (2026-10-16): muted for want of a tap, the sound comes with one.
    tapHint = message.gesture && message.muted ? (words && words.tapForSound) || '' : '';
    if (message.state === 1 || message.state === 3) draw();
  }
  if (owner) report(message);
}

const core = NeonPlayer(send, {
  autoplayFallback: true,
  onPlaying: () => { draw(); savedPoller.now(); },
  onBlocked: () => { tapHint = (words && words.tapToStart) || ''; draw(); },
});

// YouTube's loader, after the player's ready hook is set; a player that never loads is a failure with a sentence, not a silence.
const api = document.createElement('script');
api.src = 'https://www.youtube.com/iframe_api';
document.head.appendChild(api);

function apiCheck() {
  if (core.apiReady()) return;
  apiFailed = true;
  draw();
  if (owner && loaded) report({ ev: 'failed', reason: 'api' });
}

function draw() {
  if (!words) return;
  const waiting = !owner;
  playHereBtn.textContent = words.playHere;
  playHereBtn.classList.toggle('hidden', !waiting);
  const canResume = owner && !loaded && !!offer;
  resumeBtn.textContent = offer ? offer.label : '';
  resumeBtn.classList.toggle('hidden', !canResume);
  hintEl.textContent = waiting ? words.waiting
    : apiFailed ? words.failed
    : tapHint ? tapHint
    : !loaded && !canResume ? (hello ? words.idle : words.loading)
    : '';
}

// The offer resumed on this page: the app told first (its player switches here), then the page's own load.
async function resume(o) {
  offer = null;
  loaded = true;
  tapHint = '';
  Neon.say('');
  draw();
  await report({ ev: 'resumed' });
  core.onMessage({ cmd: 'load', id: o.videoId || undefined, list: o.playlistId || undefined, index: o.entry || 0, start: o.start || 0, autoplay: true });
}

// claim: a fresh id per Play here tap (later on 2026-10-16), so the browser's own reconnect to the same URL never claims again.
function connect(claim) {
  if (source) source.close();
  const url = '/api/youtube/events?page=' + encodeURIComponent(page) + '&load=' + encodeURIComponent(load)
    + '&visible=' + (visible() ? '1' : '0') + (claim ? '&claim=' + encodeURIComponent(claim) : '');
  source = new EventSource(url);
  source.onopen = () => { Neon.connected(true); seen(false); };
  source.onerror = () => {
    Neon.connected(false);
    if (source.readyState !== EventSource.CLOSED) return;   // the browser reconnects by itself
    // Closed for good (a 404 when the tab went off, a 400): the pulse says whether to leave or try again.
    Neon.get('/api/pulse').then(r => {
      if (r.ok && r.json && r.json.youTubeTab !== true) { Neon.leave(); return; }
      setTimeout(() => connect(null), 2000);
    });
  };
  source.addEventListener('hello', async ev => {
    const h = JSON.parse(ev.data);
    const first = !hello;
    hello = true;
    if (h.page && IdShape.test(h.page) && h.page !== page) {
      page = h.page;   // a duplicated tab: its own id from now on
      try { sessionStorage.setItem(PageKey, page); } catch { /* private mode */ }
    }
    words = h.words;
    owner = !!h.owner;
    offer = h.resume || null;
    apiWaitMs = h.apiWaitMs || apiWaitMs;
    if (first) {
      clearTimeout(apiTimer);
      apiTimer = setTimeout(apiCheck, apiWaitMs);
    }
    draw();
    drawSaved();
    // This script ran anew: a player the app thought open here is gone, unless it is the one just given this script's video (its load).
    if (first && owner) await report({ ev: 'page', load });
    // Play here took the video with it: it resumes here at once.
    if (h.play && h.resume && owner) await resume(h.resume);
  });
  source.addEventListener('load', ev => {
    loaded = true;
    offer = null;
    tapHint = '';
    core.onMessage(JSON.parse(ev.data));
    draw();
    if (apiFailed) report({ ev: 'failed', reason: 'api' });
  });
  source.addEventListener('cmd', ev => core.onMessage(JSON.parse(ev.data)));
  source.addEventListener('owner', ev => {
    const o = JSON.parse(ev.data);
    owner = !!o.owner;
    offer = o.resume || offer;
    draw();
  });
  source.addEventListener('resume', ev => { offer = JSON.parse(ev.data); loaded = false; draw(); });
  // Another page took the player (later on 2026-10-16: stopped, not paused, so no paused player here looks live).
  source.addEventListener('taken', () => {
    owner = false;
    loaded = false;
    tapHint = '';
    core.onMessage({ cmd: 'stop' });
    draw();
  });
  source.addEventListener('close', () => {
    loaded = false;
    tapHint = '';
    core.onMessage({ cmd: 'stop' });
    Neon.say(words ? words.closed : '');
    draw();
  });
}

playHereBtn.addEventListener('click', () => { Neon.say(''); connect(newId()); });

resumeBtn.addEventListener('click', () => { if (offer) resume(offer); });

// ── The saved videos (later on 2026-10-16, the user's ask: /youtube list under the player) ──

const savedEl = document.getElementById('saved');
const savedCaption = document.getElementById('saved-caption');
const savedSave = document.getElementById('saved-save');
const savedClear = document.getElementById('saved-clear');
const savedFilter = document.getElementById('saved-filter');
const savedNote = document.getElementById('saved-note');
const savedList = document.getElementById('saved-list');
const openRows = new Set();   // the ids whose details are open, kept across redraws
let saved = null;

async function readSaved() {
  const r = await Neon.get('/api/youtube/saved', true);
  if (r.ok && r.json) {
    saved = r.json;
    drawSaved();
  }
  savedPoller.every(saved && saved.looking ? 1000 : 4000);
}

const savedPoller = Neon.poll(readSaved, 4000);

// The PC's filter: the title, or the channel and the id, case-folded.
function matches(filter, v) {
  if (!filter) return true;
  const f = filter.toLocaleLowerCase();
  return (v.title || '').toLocaleLowerCase().includes(f) || ((v.channel || '') + ' ' + v.id).toLocaleLowerCase().includes(f);
}

async function change(path, body) {
  const r = await Neon.post(path, body);
  Neon.say(r.ok ? (r.message || '') : (r.message || ''));
  savedPoller.now();
  return r;
}

async function playSaved(v) {
  const r = await Neon.post('/api/youtube/saved/play', { page, id: v.id });
  Neon.say(r.message || '');
  if (r.ok && frameEl && frameEl.scrollIntoView) frameEl.scrollIntoView({ behavior: 'smooth', block: 'start' });
  savedPoller.now();
}

function iconButton(cls, glyph, label) {
  const b = Neon.el('button', cls, glyph);
  b.type = 'button';
  b.setAttribute('aria-label', label);
  b.title = label;
  return b;
}

function savedRow(v, sw, moving) {
  const li = Neon.el('li', 'yt-row' + (v.watched ? ' yt-watched' : ''));
  li.dataset.id = v.id;
  const main = Neon.el('div', 'yt-row-main');
  const play = Neon.el('button', 'yt-play');
  play.type = 'button';
  play.setAttribute('aria-label', v.title || v.id);
  const thumb = Neon.el('img', 'yt-thumb');
  thumb.alt = '';
  thumb.loading = 'lazy';
  thumb.addEventListener('error', () => { thumb.classList.add('hidden'); });
  thumb.src = 'https://i.ytimg.com/vi/' + encodeURIComponent(v.id) + '/mqdefault.jpg';
  play.appendChild(thumb);
  const text = Neon.el('span', 'yt-text');
  text.appendChild(Neon.el('span', 'yt-title', v.title || v.id));
  if (v.channel) text.appendChild(Neon.el('span', 'yt-channel', v.channel));
  text.appendChild(Neon.el('span', 'yt-place', v.place));
  play.appendChild(text);
  play.addEventListener('click', () => playSaved(v));
  main.appendChild(play);
  const open = openRows.has(v.id);
  const more = iconButton('yt-more', '⋯', sw.details);
  more.setAttribute('aria-expanded', open ? 'true' : 'false');
  main.appendChild(more);
  li.appendChild(main);

  const details = Neon.el('div', 'yt-details' + (open ? '' : ' hidden'));
  details.appendChild(Neon.el('p', 'yt-heading', v.heading));
  details.appendChild(Neon.el('p', 'yt-facts', v.details));
  const moves = Neon.el('div', 'yt-moves');
  if (moving) {
    for (const [to, glyph, label] of [['up', '▲', sw.moveUp], ['down', '▼', sw.moveDown], ['top', '⤒', sw.moveTop], ['bottom', '⤓', sw.moveBottom]]) {
      const b = iconButton('yt-move', glyph, label);
      b.addEventListener('click', () => change('/api/youtube/saved/move', { id: v.id, to }));
      moves.appendChild(b);
    }
  }
  const del = iconButton('yt-delete', '🗑️', sw.delete);
  Neon.armTwice(del, Neon.paintArmed(del, on => on ? sw.deleteArmed : sw.delete), () => change('/api/youtube/saved/remove', { id: v.id }));
  moves.appendChild(del);
  details.appendChild(moves);
  li.appendChild(details);
  more.addEventListener('click', () => {
    const now = details.classList.toggle('hidden') === false;
    if (now) openRows.add(v.id); else openRows.delete(v.id);
    more.setAttribute('aria-expanded', now ? 'true' : 'false');
  });
  return li;
}

function drawSaved() {
  if (!words || !words.saved || !saved) return;
  const sw = words.saved;
  savedEl.classList.remove('hidden');
  savedCaption.textContent = saved.items.length > 0 ? saved.caption : '';
  savedFilter.placeholder = sw.filter;
  savedFilter.setAttribute('aria-label', sw.filter);
  savedFilter.classList.toggle('hidden', saved.items.length === 0);

  // ☆ for a playing video not saved, ★ Saved (disabled) once it is; nothing with none open.
  const p = saved.playing;
  savedSave.classList.toggle('hidden', !p);
  savedSave.textContent = p && p.saved ? sw.saved : sw.savePlaying;
  savedSave.disabled = !!(p && p.saved);
  savedSave.setAttribute('aria-label', savedSave.textContent);
  savedSave.title = savedSave.textContent;

  savedClear.classList.toggle('hidden', saved.items.length === 0);
  if (!savedClear.classList.contains('armed')) {
    savedClear.textContent = '🗑️ ' + sw.clearAll;
    savedClear.setAttribute('aria-label', sw.clearAll);
    savedClear.title = sw.clearAll;
  }

  const filter = savedFilter.value.trim();
  const shown = saved.items.filter(v => matches(filter, v));
  savedList.replaceChildren(...shown.map(v => savedRow(v, sw, !filter)));
  const note = saved.items.length === 0 ? saved.empty
    : shown.length === 0 ? sw.noMatch.replace('{0}', filter)
    : saved.looking ? sw.looking
    : '';
  savedNote.textContent = note;
  savedNote.classList.toggle('hidden', !note);
}

Neon.armTwice(savedClear, on => {
  savedClear.classList.toggle('armed', on);
  const text = words && words.saved ? (on ? words.saved.clearArmed : '🗑️ ' + words.saved.clearAll) : '🗑️';
  savedClear.textContent = text;
  savedClear.setAttribute('aria-label', text);
  savedClear.title = text;
}, () => change('/api/youtube/saved/clear', {}));
savedSave.addEventListener('click', () => change('/api/youtube/saved/save', {}));
savedFilter.addEventListener('input', drawSaved);

connect(null);

// The tabs, the theme, the files' tag and the Chat badge, as on Shopping.
Neon.poll(async () => { const r = await Neon.get('/api/pulse', true); if (r.ok && r.json) Neon.pulse(r.json); }, 3000);
