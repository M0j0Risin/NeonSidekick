// NeonSidekick's Web UI, the page / shows with every tab off (later on 2026-10-15, the user's call: a tab exists only while its controls are on).
// It reads the pulse every 3 seconds while visible and goes to / once any tab is on, which the server sends on to that tab.
'use strict';

Neon.poll(async () => {
  const r = await Neon.get('/api/pulse', true);
  if (!r.ok || !r.json) return;   // 304: nothing changed
  const p = r.json;
  if (Neon.staleAssets(p.assets)) return;
  Neon.theme(p.theme);
  if (p.chatTab !== false || p.homeTab !== false || p.shoppingTab !== false || p.youTubeTab === true) Neon.leave();   // YouTube too (2026-10-16)
}, 3000);
