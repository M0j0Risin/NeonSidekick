// The YouTube player both pages share (2026-10-16, YouTube open links in): the video window's page (player.html, inlined by
// Viewer/VideoPage at its marker, so the written file and the Mac's LoadHTMLString stay one page) and the Web UI's YouTube tab
// (youtube.js, served as webui/youtube-core.js). The player alone: made at the first load, loads, commands, reports and the tick.
// Each page keeps its own transport and calls NeonPlayer(send, options) once; it never touches WebView2's bridge. It sets
// window.onYouTubeIframeAPIReady, so the page loads https://www.youtube.com/iframe_api after it.
//   NeonPlayer(send, { autoplayFallback, onPlaying, onBlocked }) -> { onMessage(m), apiReady() }
// The protocol is player.html's header. autoplayFallback (the Web UI's tab, where a phone starts a scripted play only after a
// gesture on the video itself): a play that isn't playing within 1.5 s is tried again muted, its reports then carrying
// gesture: true until the sound is back on; one still blocked calls onBlocked, and onPlaying fires as a video plays.
function NeonPlayer(send, hooks) {
  'use strict';
  const opts = hooks || {};
  let api = false;
  let player = null;
  let ready = false;
  let pending = null;
  let current = null;
  let settled = true;
  let list = null;
  let seek = null;
  let seekStart = 0;
  let seekCue = false;
  let mutedForAutoplay = false;
  let tryTimer = null;

  function videoData() {
    try { return player.getVideoData() || {}; } catch { return {}; }
  }

  function entries() {
    try { return player.getPlaylist() || []; } catch { return []; }
  }

  function entry() {
    try { const i = player.getPlaylistIndex(); return typeof i === 'number' && i >= 0 ? i : null; } catch { return null; }
  }

  // A playlist loaded with a video named: once the player has the list, start at that video (once), at the link's time, cued
  // when the load was (2026-10-09, the review: playVideoAt started the video at 0 and played a cued list; the time had gone to the
  // list's first entry).
  function findSought() {
    if (!seek) return;
    const ids = entries();
    if (ids.length === 0) return;
    const i = ids.indexOf(seek);
    const at = seekStart;
    seek = null;
    seekStart = 0;
    if (i < 0 || (i === entry() && at <= 0)) return;
    const args = { list, listType: 'playlist', index: i, startSeconds: at };
    if (seekCue) player.cuePlaylist(args); else player.loadPlaylist(args);
  }

  function report(why) {
    if (!ready) return;
    const d = videoData();
    const muted = player.isMuted();
    if (!muted) mutedForAutoplay = false;
    const message = {
      ev: 'state', why, id: d.video_id || null, title: d.title || null, author: d.author || null,
      state: player.getPlayerState(), position: player.getCurrentTime(), duration: player.getDuration(),
      volume: player.getVolume(), muted
    };
    if (list) {
      message.list = list;
      message.index = entry();
      message.count = entries().length || null;
    }
    if (mutedForAutoplay) message.gesture = true;
    send(message);
  }

  function failed(code) {
    const id = videoData().video_id || current;
    const i = entry();
    const count = entries().length;
    const skip = list !== null && i !== null && i < count - 1;
    send(skip ? { ev: 'error', code, id, skip: true } : { ev: 'error', code, id });
    if (skip) setTimeout(() => { if (list && entry() === i) player.nextVideo(); }, 1000);
  }

  function playing() {
    try { const s = player.getPlayerState(); return s === YT.PlayerState.PLAYING || s === YT.PlayerState.BUFFERING; } catch { return false; }
  }

  // The Web UI's autoplay fallback: unmuted first, then muted, then the page asks for a tap.
  function tryPlaying() {
    if (!opts.autoplayFallback) return;
    clearTimeout(tryTimer);
    tryTimer = setTimeout(() => {
      if (!ready || playing()) return;
      if (!player.isMuted()) {
        mutedForAutoplay = true;
        player.mute();
        player.playVideo();
        report('muted');
      }
      tryTimer = setTimeout(() => { if (ready && !playing() && opts.onBlocked) opts.onBlocked(); }, 1500);
    }, 1500);
  }

  function create(m) {
    const vars = { playsinline: 1, rel: 0, start: seek ? 0 : Math.floor(m.start || 0), origin: location.origin };
    if (m.list) {
      vars.listType = 'playlist';
      vars.list = m.list;
      vars.index = m.index || 0;
    }
    // A playlist's player has no videoId key at all: the API throws "Invalid video id" on one that is there but undefined
    // (2026-10-09, found playing a channel's uploads: the throw left the page silent, no player and no report).
    const options = {
      playerVars: vars,
      events: {
        onReady: () => {
          ready = true;
          send({ ev: 'ready' });
          if (m.autoplay !== false) { player.playVideo(); tryPlaying(); }
          report('ready');
        },
        onStateChange: () => {
          settled = true;
          findSought();
          if (opts.onPlaying && playing()) opts.onPlaying();
          report('change');
        },
        onError: e => { settled = true; failed(e.data); }
      }
    };
    if (!m.list) options.videoId = m.id;
    player = new YT.Player('player', options);
  }

  function load(m) {
    current = m.id || null;
    list = m.list || null;
    seek = m.list && m.id ? m.id : null;
    seekStart = seek ? (m.start || 0) : 0;
    seekCue = m.autoplay === false;
    if (!api) { pending = m; return; }
    if (!player) { create(m); return; }
    settled = false;
    if (m.autoplay !== false) tryPlaying();
    if (m.list) {
      const args = { list: m.list, listType: 'playlist', index: m.index || 0, startSeconds: seek ? 0 : (m.start || 0) };
      if (m.autoplay === false) player.cuePlaylist(args); else player.loadPlaylist(args);
      return;
    }
    const args = { videoId: m.id, startSeconds: m.start || 0 };
    if (m.autoplay === false) player.cueVideoById(args); else player.loadVideoById(args);
  }

  window.onYouTubeIframeAPIReady = () => {
    api = true;
    if (pending) { const m = pending; pending = null; load(m); }
  };

  setInterval(() => { if (ready && settled && player.getPlayerState() === YT.PlayerState.PLAYING) report('tick'); }, 1000);

  function onMessage(m) {
    m = m || {};
    if (m.cmd === 'load') { load(m); return; }
    if (!ready) return;
    switch (m.cmd) {
      case 'play': player.playVideo(); tryPlaying(); break;
      case 'pause': player.pauseVideo(); break;
      case 'stop': player.stopVideo(); break;
      case 'seek': player.seekTo(m.t, true); break;
      case 'volume': player.setVolume(m.v); if (m.v > 0) player.unMute(); break;
      case 'mute': player.mute(); break;
      case 'unmute': player.unMute(); break;
      case 'next': player.nextVideo(); break;
      case 'previous': player.previousVideo(); break;
      case 'at': player.playVideoAt(m.i); break;
      default: return;
    }
    report(m.cmd);
  }

  return { onMessage, apiReady: () => api };
}
