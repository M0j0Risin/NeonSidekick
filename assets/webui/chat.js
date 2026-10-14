// NeonSidekick's Web UI, the chat page (2026-10-14): the transcript as the app draws it, streamed over server-sent events (a snapshot on
// every (re)connect, then the changes), the page's own draft line, Stop, and the asks the app's panes put — answered here or at the PC,
// the first answer winning. Every word the asks show comes from the app; text is set with textContent, and the only HTML is a finished
// reply's, which the app renders with raw HTML escaped and the links vetted.
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
const profileEl = document.getElementById('profile');
const modelEl = document.getElementById('model');
const dot = document.getElementById('dot');

const desktop = window.matchMedia('(pointer: fine)').matches;
const entries = new Map();   // seq → { el, body, kind, text }
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
  messageEl.textContent = text || '';
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
    return { ok: r.ok, status: r.status, message: j && j.message };
  } catch {
    return { ok: false, status: 0, message: 'The app could not be reached.' };
  }
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
    case 'picture':
      node = el('div', 'entry picture', '🖼️ ' + entry.text);
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

async function send() {
  const text = draft.value;
  if (!text.trim()) return;
  if (pendingText !== text) { pendingId = newId(); pendingText = text; }
  sendBtn.disabled = true;
  const r = await post('/api/send', { id: pendingId, text: text });
  sendBtn.disabled = false;
  if (r.ok) {
    if (draft.value === text) draft.value = '';
    pendingId = null;
    pendingText = null;
    saveDraft();
    grow();
    say('');
  } else {
    say(r.message || 'That message did not go through.');
  }
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
    applyState(s.state);
    reset();
    for (const entry of s.entries) renderEntry(entry);
    renderAsks(s.asks);
    window.scrollTo(0, document.body.scrollHeight);
  });
  source.addEventListener('append', ev => append(JSON.parse(ev.data)));
  source.addEventListener('delta', ev => delta(JSON.parse(ev.data)));
  source.addEventListener('end', ev => end(JSON.parse(ev.data)));
  source.addEventListener('reset', () => reset());
  source.addEventListener('asks', ev => renderAsks(JSON.parse(ev.data).asks));
  source.addEventListener('state', ev => applyState(JSON.parse(ev.data)));
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
  if (ev.key === 'Enter' && desktop && !ev.shiftKey && !ev.isComposing) {
    ev.preventDefault();
    send();
  }
});
sendBtn.addEventListener('click', send);
stopBtn.addEventListener('click', stop);
connect();
