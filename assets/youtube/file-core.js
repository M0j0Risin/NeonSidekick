// The video-file player both pages share (2026-11-12, the video-files plan): the video window's file page (file.html, inlined by
// Viewer/VideoPage at its marker) and the Web UI's Video tab (youtube.js, served as webui/youtube-file.js). One HTML5 <video>
// playing a video file on the PC, with player-core.js's shape so each page keeps its transport:
//   NeonFile(send, { video, autoplayFallback, onPlaying, onBlocked, keys }) -> { onMessage(m), empty(), active() }
// The protocol is file.html's header: load {file, src, name, start, autoplay, volume?, muted?}, play, pause, stop, seek {t},
// volume {v} (0-100), mute, unmute; next, previous and at are ignored (a file has no list). A load's volume and muted (2026-11-13)
// are the level of the play it replaces, applied before it plays. It reports state {file, title, state, position, duration,
// volume, muted, fixedVolume?} with YouTube's state numbers (-1 unstarted, 0 ended, 1 playing, 2 paused, 3 buffering, 5 cued), and
// error {file, code}: MediaError's 1-4, or 1000 when the file's metadata loaded with no picture (no video track, or an HEVC file on
// a PC without its decoder: sound over black), which the app reads as a note, not a failure. fixedVolume: true says the page can't
// set the volume (an iPhone: the phone's buttons own it; found once at start on a detached <video>, so its volumechange never
// reports). From a load until the new file's loadstart nothing is sent, so the old file's pause or emptied never reports under the
// new token. A seek, or a start, past the end is held to the duration; one asked for before the metadata is applied once it loads;
// with metadata but no known length (a header-less WebM's Infinity) it is set at once, held to the seekable end when there is one.
// autoplayFallback (the Web UI's tab): a play a phone refuses is tried again muted, its reports then carrying gesture: true until
// the sound is back on; one still refused reports blocked (state -1, so the app says to tap) and then calls onBlocked. An autoplay
// off load that has reported nothing after 2 s (a phone that won't fetch metadata without a tap) reports itself ready, state 5.
// Volume and seek reports from a dragged slider come at most every 250 ms; the rest go at once. keys (default true; the tab passes
// a function saying whether the file is the one shown): the keys YouTube's player gives — Space or K pause/play, the arrows 5 s,
// F full screen, M mute — taken in the capture phase, so the element's own controls never act on the same key twice, and never
// from a field being typed in, a focused button or link, or with Ctrl, Alt or Meta held.
function NeonFile(send, hooks) {
  'use strict';
  const opts = hooks || {};
  const video = opts.video || document.querySelector('video');
  const NO_PICTURE = 1000;
  const CUED_AFTER_MS = 2000;
  const COALESCE_MS = 250;
  let token = null;
  let name = null;
  let quiet = true;
  let started = false;
  let waiting = false;
  let held = null;
  let mutedForAutoplay = false;
  let noPicture = false;
  let blocked = false;
  let reported = false;
  let cuedTimer = null;
  let coalesceTimer = null;

  // Whether this page can set a video's volume at all: an iPhone's stays at 1. Asked of a detached element, never the player.
  const fixedVolume = (() => {
    try {
      const probe = document.createElement('video');
      probe.volume = 0.5;
      return Math.abs(probe.volume - 0.5) > 0.01;
    } catch { return false; }
  })();

  video.controls = true;
  video.playsInline = true;
  video.preload = 'metadata';
  video.disablePictureInPicture = true;
  video.setAttribute('controlslist', 'nodownload');
  video.setAttribute('disablepictureinpicture', '');
  video.setAttribute('playsinline', '');

  function finite(n) { return typeof n === 'number' && isFinite(n) && n > 0 ? n : 0; }

  function state() {
    if (video.ended) return 0;
    if (!video.paused) return waiting || video.readyState < 3 ? 3 : 1;
    if (!started) return blocked ? -1 : video.readyState >= 1 ? 5 : -1;
    return 2;
  }

  function report(why, forced) {
    if (!token || quiet) return;
    if (!video.muted) mutedForAutoplay = false;
    const message = {
      ev: 'state', why, file: token, title: name, state: forced === undefined ? state() : forced, position: video.currentTime || 0,
      duration: finite(video.duration), volume: Math.round(video.volume * 100), muted: video.muted
    };
    if (mutedForAutoplay) message.gesture = true;
    if (fixedVolume) message.fixedVolume = true;
    reported = true;
    send(message);
  }

  // A slider's stream of volume or seek changes, reported once at the end of each 250 ms (the report reads the state then).
  function reportSoon(why) {
    if (coalesceTimer !== null) return;
    coalesceTimer = setTimeout(() => { coalesceTimer = null; report(why); }, COALESCE_MS);
  }

  function clearTimers() {
    if (cuedTimer !== null) { clearTimeout(cuedTimer); cuedTimer = null; }
    if (coalesceTimer !== null) { clearTimeout(coalesceTimer); coalesceTimer = null; }
  }

  // A time held to the file's length (a seek past the end ends it); before the metadata, kept until it loads. With metadata but no
  // known length (Infinity) it goes at once, held to the seekable end when the element knows one.
  function seekTo(t) {
    const at = Math.max(0, Number(t) || 0);
    if (video.readyState < 1) { held = at; return; }
    const d = finite(video.duration);
    if (d) { video.currentTime = Math.min(at, d); return; }
    let end = Infinity;
    try {
      const ranges = video.seekable;
      if (ranges && ranges.length) end = ranges.end(ranges.length - 1);
    } catch { /* no ranges yet */ }
    video.currentTime = Math.min(at, end);
  }

  function refused() {
    blocked = true;
    report('blocked');
    if (opts.onBlocked) opts.onBlocked();
  }

  // Each answer is this load's alone (2026-11-14): a file loaded meanwhile is never reported muted or refused by the last one's play,
  // and only a refusal is one (a pause or a new src aborts the play with AbortError).
  function playNow() {
    const mine = token;
    let p;
    try { p = video.play(); } catch { return; }
    if (!p || !p.catch) return;
    p.catch(err => {
      if (!token || token !== mine || !err || err.name !== 'NotAllowedError') return;
      if (!opts.autoplayFallback || video.muted) { refused(); return; }
      mutedForAutoplay = true;
      video.muted = true;
      const again = video.play();
      if (again && again.then) {
        again.then(
          () => { if (token === mine) report('muted'); },
          e => { if (token === mine && e && e.name === 'NotAllowedError') refused(); });
      }
    });
  }

  function load(m) {
    clearTimers();
    if (mutedForAutoplay) { video.muted = false; mutedForAutoplay = false; }
    quiet = true;
    token = m.file || null;
    name = m.name || null;
    started = false;
    waiting = false;
    noPicture = false;
    blocked = false;
    reported = false;
    held = m.start > 0 ? m.start : null;
    if (!token || !m.src) { empty(); return; }
    if (typeof m.volume === 'number' && isFinite(m.volume)) {
      try { video.volume = Math.min(100, Math.max(0, m.volume)) / 100; } catch { /* a fixed volume */ }
    }
    if (typeof m.muted === 'boolean') video.muted = m.muted;
    video.src = m.src;
    if (m.autoplay !== false) { playNow(); return; }
    const loaded = token;
    cuedTimer = setTimeout(() => {
      cuedTimer = null;
      if (token !== loaded || reported) return;
      quiet = false;
      report('cued', 5);
    }, CUED_AFTER_MS);
  }

  // The element emptied (a close, the tab taken): its stream ends, and the token is never asked for again.
  function empty() {
    clearTimers();
    token = null;
    quiet = true;
    try { video.pause(); } catch { /* nothing loaded */ }
    video.removeAttribute('src');
    try { video.load(); } catch { /* nothing to load */ }
  }

  video.addEventListener('loadstart', () => { if (token) quiet = false; });
  video.addEventListener('loadedmetadata', () => {
    if (!token) return;
    if (held !== null) { const at = held; held = null; seekTo(at); }
    if (video.videoWidth === 0 && !noPicture) { noPicture = true; send({ ev: 'error', file: token, code: NO_PICTURE }); }
    report('meta');
  });
  video.addEventListener('playing', () => {
    started = true;
    waiting = false;
    blocked = false;
    if (opts.onPlaying) opts.onPlaying();
    report('change');
  });
  video.addEventListener('waiting', () => { waiting = true; report('change'); });
  video.addEventListener('pause', () => { waiting = false; report('change'); });
  video.addEventListener('ended', () => { waiting = false; report('change'); });
  video.addEventListener('seeked', () => reportSoon('seeked'));
  video.addEventListener('volumechange', () => reportSoon('volume'));
  video.addEventListener('error', () => {
    if (!token || !video.error) return;
    send({ ev: 'error', file: token, code: video.error.code });
  });

  setInterval(() => { if (token && !quiet && !video.paused && !waiting) report('tick'); }, 1000);

  function typing(target) {
    return !!target && (target.isContentEditable || /^(INPUT|TEXTAREA|SELECT)$/.test(target.tagName || ''));
  }

  // A focused button or link keeps its own keys (Space presses it, the arrows scroll): Resume, a saved row, the fill button.
  function onControl(target) {
    return target instanceof Element && !!target.closest('button, a, summary, [role=button]');
  }

  window.addEventListener('keydown', e => {
    if (!token || e.defaultPrevented || e.ctrlKey || e.altKey || e.metaKey || typing(e.target) || onControl(e.target)) return;
    if (typeof opts.keys === 'function' ? !opts.keys() : opts.keys === false) return;
    switch (e.key) {
      case ' ': case 'k': case 'K':
        if (video.paused || video.ended) { if (video.ended) video.currentTime = 0; playNow(); } else video.pause();
        break;
      case 'ArrowLeft': seekTo((video.currentTime || 0) - 5); break;
      case 'ArrowRight': seekTo((video.currentTime || 0) + 5); break;
      case 'f': case 'F':
        if (document.fullscreenElement) document.exitFullscreen(); else if (video.requestFullscreen) video.requestFullscreen();
        break;
      case 'm': case 'M': video.muted = !video.muted; break;
      default: return;
    }
    e.preventDefault();
    e.stopPropagation();
  }, true);

  function onMessage(m) {
    m = m || {};
    if (m.cmd === 'load') { load(m); return; }
    if (!token) return;
    switch (m.cmd) {
      case 'play': if (video.ended) video.currentTime = 0; playNow(); break;
      case 'pause': video.pause(); break;
      case 'stop': video.pause(); seekTo(0); break;
      case 'seek': seekTo(m.t); break;
      case 'volume': {
        const v = Math.min(100, Math.max(0, Number(m.v) || 0));
        video.volume = v / 100;
        if (v > 0) video.muted = false;
        break;
      }
      case 'mute': video.muted = true; break;
      case 'unmute': video.muted = false; break;
      default: return;
    }
    report(m.cmd);
  }

  return { onMessage, empty, active: () => token !== null };
}
