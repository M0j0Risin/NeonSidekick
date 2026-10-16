// NeonSidekick's Web UI, the YouTube tab (2026-10-16, YouTube open links in webui-window): the app's video player on the phone or a browser.
// The player is the PC window's own (player-core.js, served as /youtube-core.js); this page is its transport — its own event stream from the
// app (hello, load, cmd, owner, resume, taken, close) and its reports posted back, the same JSON the PC's page posts. A page is this browser
// tab: its id lives in sessionStorage (a reload keeps it, a new tab gets its own), and each script load makes a nonce kept in memory, so the
// app tells a reconnect from a reload from a duplicated tab. The first page plays; another waits with Play here. Leaving the tab stops the
// video (its place kept by the app), and coming back offers Resume. Every word is the app's.
'use strict';

const hintEl = document.getElementById('hint');
const playHereBtn = document.getElementById('play-here');
const resumeBtn = document.getElementById('resume');
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
  onPlaying: () => draw(),
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

function connect(claim) {
  if (source) source.close();
  const url = '/api/youtube/events?page=' + encodeURIComponent(page) + '&load=' + encodeURIComponent(load) + (claim ? '&claim=1' : '');
  source = new EventSource(url);
  source.onopen = () => Neon.connected(true);
  source.onerror = () => {
    Neon.connected(false);
    if (source.readyState !== EventSource.CLOSED) return;   // the browser reconnects by itself
    // Closed for good (a 404 when the tab went off, a 400): the pulse says whether to leave or try again.
    Neon.get('/api/pulse').then(r => {
      if (r.ok && r.json && r.json.youTubeTab !== true) { Neon.leave(); return; }
      setTimeout(() => connect(false), 2000);
    });
  };
  source.addEventListener('hello', ev => {
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
      if (owner) report({ ev: 'page' });   // this script ran anew: a player the app thought open here is gone
    }
    draw();
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
  source.addEventListener('taken', () => {
    owner = false;
    core.onMessage({ cmd: 'pause' });
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

playHereBtn.addEventListener('click', () => { Neon.say(''); connect(true); });

resumeBtn.addEventListener('click', async () => {
  if (!offer) return;
  const o = offer;
  offer = null;
  loaded = true;
  tapHint = '';
  Neon.say('');
  draw();
  await report({ ev: 'resumed' });   // before the load: the app switches its player here first
  core.onMessage({ cmd: 'load', id: o.videoId || undefined, list: o.playlistId || undefined, index: o.entry || 0, start: o.start || 0, autoplay: true });
});

connect(false);

// The tabs, the theme, the files' tag and the Chat badge, as on Shopping.
Neon.poll(async () => { const r = await Neon.get('/api/pulse', true); if (r.ok && r.json) Neon.pulse(r.json); }, 3000);
