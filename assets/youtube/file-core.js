// The video-file player both pages share (2026-11-12, the video-files plan): the video window's file page (file.html, inlined by
// Viewer/VideoPage at its marker) and the Web UI's YouTube tab (youtube.js, served as webui/youtube-file.js). One HTML5 <video>
// playing a video file on the PC, with player-core.js's shape so each page keeps its transport:
//   NeonFile(send, { video, autoplayFallback, onPlaying, onBlocked, keys }) -> { onMessage(m), empty(), active() }
// The protocol is file.html's header: load {file, src, name, start, autoplay}, play, pause, stop, seek {t}, volume {v} (0-100),
// mute, unmute; next, previous and at are ignored (a file has no list). It reports state {file, title, state, position, duration,
// volume, muted} with YouTube's state numbers (-1 unstarted, 0 ended, 1 playing, 2 paused, 3 buffering, 5 cued), and error
// {file, code}: MediaError's 1-4, or 1000 when the file's metadata loaded with no picture (an HEVC file on a PC without its
// decoder plays sound over black). From a load until the new file's loadstart nothing is sent, so the old file's pause or
// emptied never reports under the new token. A seek, or a start, past the end is held to the duration; one asked for before the
// metadata is applied once it loads. autoplayFallback (the Web UI's tab): a play a phone refuses is tried again muted, its reports
// then carrying gesture: true until the sound is back on; one still refused calls onBlocked. keys (default true; the tab passes a
// function saying whether the file is the one shown): the keys YouTube's player gives — Space or K pause/play, the arrows 5 s,
// F full screen, M mute — taken in the capture phase, so the element's own controls never act on the same key twice, and never
// from a field being typed in or with Ctrl, Alt or Meta held.
function NeonFile(send, hooks) {
  'use strict';
  const opts = hooks || {};
  const video = opts.video || document.querySelector('video');
  const NO_PICTURE = 1000;
  let token = null;
  let name = null;
  let quiet = true;
  let started = false;
  let waiting = false;
  let held = null;
  let mutedForAutoplay = false;
  let noPicture = false;

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
    if (!started) return video.readyState >= 1 ? 5 : -1;
    return 2;
  }

  function report(why) {
    if (!token || quiet) return;
    if (!video.muted) mutedForAutoplay = false;
    const message = {
      ev: 'state', why, file: token, title: name, state: state(), position: video.currentTime || 0,
      duration: finite(video.duration), volume: Math.round(video.volume * 100), muted: video.muted
    };
    if (mutedForAutoplay) message.gesture = true;
    send(message);
  }

  // A time held to the file's length (a seek past the end ends it); before the metadata, kept until it loads.
  function seekTo(t) {
    const at = Math.max(0, Number(t) || 0);
    const d = finite(video.duration);
    if (video.readyState < 1 || !d) { held = at; return; }
    video.currentTime = Math.min(at, d);
  }

  function playNow() {
    let p;
    try { p = video.play(); } catch { return; }
    if (!p || !p.catch) return;
    p.catch(err => {
      if (!token || !err || err.name !== 'NotAllowedError') return;
      if (!opts.autoplayFallback || video.muted) { if (opts.onBlocked) opts.onBlocked(); report('blocked'); return; }
      mutedForAutoplay = true;
      video.muted = true;
      const again = video.play();
      if (again && again.then) again.then(() => report('muted'), () => { if (opts.onBlocked) opts.onBlocked(); report('blocked'); });
    });
  }

  function load(m) {
    if (mutedForAutoplay) { video.muted = false; mutedForAutoplay = false; }
    quiet = true;
    token = m.file || null;
    name = m.name || null;
    started = false;
    waiting = false;
    noPicture = false;
    held = m.start > 0 ? m.start : null;
    if (!token || !m.src) { empty(); return; }
    video.src = m.src;
    if (m.autoplay !== false) playNow();
  }

  // The element emptied (a close, the tab taken): its stream ends, and the token is never asked for again.
  function empty() {
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
    if (opts.onPlaying) opts.onPlaying();
    report('change');
  });
  video.addEventListener('waiting', () => { waiting = true; report('change'); });
  video.addEventListener('pause', () => { waiting = false; report('change'); });
  video.addEventListener('ended', () => { waiting = false; report('change'); });
  video.addEventListener('seeked', () => report('seeked'));
  video.addEventListener('volumechange', () => report('volume'));
  video.addEventListener('error', () => {
    if (!token || !video.error) return;
    send({ ev: 'error', file: token, code: video.error.code });
  });

  setInterval(() => { if (token && !quiet && !video.paused && !waiting) report('tick'); }, 1000);

  function typing(target) {
    return !!target && (target.isContentEditable || /^(INPUT|TEXTAREA|SELECT)$/.test(target.tagName || ''));
  }

  window.addEventListener('keydown', e => {
    if (!token || e.defaultPrevented || e.ctrlKey || e.altKey || e.metaKey || typing(e.target)) return;
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
