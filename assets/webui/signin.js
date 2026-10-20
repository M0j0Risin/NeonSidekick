// NeonSidekick's Web UI, the sign-in page (2026-10-15): the link /webui shows signs a browser in when opened; an app added to the Home Screen keeps
// cookies of its own and opens no link, so the link (or its token) is pasted here. Posted as JSON with the header no other site can send; the page
// then reloads where it is, so /home stays /home, except on /signin itself, whose spent code or old token would only be refused again: that
// one goes to / (2026-10-19, the review). The token is never shown back.
'use strict';

const form = document.getElementById('signin-form');
const field = document.getElementById('signin-link');
const message = document.getElementById('message');

form.addEventListener('submit', async ev => {
  ev.preventDefault();
  const link = field.value.trim();
  if (!link) return;
  message.classList.add('hidden');
  try {
    const r = await fetch('/signin', {
      method: 'POST',
      credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json', 'X-Neon': '1' },
      body: JSON.stringify({ link }),
    });
    if (r.ok) {
      if (location.pathname === '/signin') location.replace('/'); else location.reload();
      return;
    }
    let j = null;
    try { j = await r.json(); } catch { /* none */ }
    message.textContent = (j && j.message) || 'That did not sign in.';
  } catch {
    message.textContent = 'The app could not be reached.';
  }
  message.classList.remove('hidden');
});
