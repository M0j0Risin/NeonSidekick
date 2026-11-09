// NeonSidekick's Web UI, the About page (2026-11-08, the user's ask: the source link off every page, onto a card behind ☰). Nothing of its own to load: the pulse
// keeps the menu, the Chat badge, the theme, a stale page's reload and a queued video's jump to the YouTube tab, as on the other tabs.
Neon.poll(async () => { const r = await Neon.get('/api/pulse', true); if (r.ok && r.json) Neon.pulse(r.json); }, 3000);
