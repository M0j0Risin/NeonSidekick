namespace NeonSidekick.UI;

/// <summary>
/// What an item on the lower rule stands for (2026-11-23, the user's ask: the jobs group). The values are pinned: each kind's double-click
/// pair key is <c>-600_000 - kind</c> (<see cref="InputLine.LowerRulePairKey"/>, a band of its own since 2026-11-24, the last-7 review's #18).
/// </summary>
public enum LowerRuleKind
{
    /// <summary>
    /// The running processes' count, <c>⚡ n</c> (<c>▶ n</c> until 2026-11-24, <c>⚡n</c> until 2026-11-27): <c>/process</c>. Dropped beside another
    /// item, after the queued count and before the pictures' timer; alone it is cut, never dropped (2026-11-27, the last-3 review's #42: the
    /// doc said never dropped).
    /// </summary>
    Processes = 1,

    /// <summary>The update's marker, <c>📦 0.5.9 at quit</c>: <c>/update</c>. Dropped first.</summary>
    Update = 2,

    /// <summary>The loop under way: 🔁 a Ralph loop, 🔎 a code audit, ⟳ a <c>/loop</c>; a double-click ends it after the pass in progress.</summary>
    Loop = 3,

    /// <summary>An embedded model's download, <c>📥 ⠋ 42%</c>: a double-click cancels it.</summary>
    Download = 4,

    /// <summary>
    /// ComfyUI pictures under way, <c>🖼️  00:38</c> / <c>🖼️  2 · 00:38</c> (🎨 for a rework; <c>🖼️ 2</c> at the right until 2026-11-27): pinned left,
    /// before the queued count. A double-click cancels them.
    /// </summary>
    Pictures = 5,

    /// <summary>The MCP servers connecting behind the line, <c>🔌 1/3</c> (the status bar's until 2026-11-25): a double-click cancels the wave.</summary>
    Mcp = 6,

    /// <summary>Voice input setting up, <c>🎙️ 42%</c> (the status bar's until 2026-11-25): a double-click cancels it.</summary>
    Voice = 7,

    /// <summary>Speech output setting up, <c>🔈</c> (the status bar's until 2026-11-25): a double-click cancels it.</summary>
    Speech = 8,

    /// <summary>A reflection running, <c>🧠</c> (the status bar's until 2026-11-25): a double-click cancels it, as Ctrl+L does.</summary>
    Learning = 9,

    /// <summary>A botchat's extra embedded servers idle, <c>🧟</c> (the status bar's until 2026-11-25): a double-click is <c>/botchat --kill</c>.</summary>
    Extras = 10,

    /// <summary>The camera held open (live view or watch), <c>📷</c> (the status bar's until 2026-11-25): a double-click is <c>/camera off</c>. Never dropped.</summary>
    Camera = 11,

    /// <summary>
    /// The messages waiting to be sent, <c>📤 2 queued</c> (the status bar's until 2026-11-26, the user's ask): pinned at the rule's left end, the
    /// jobs at its right. A double-click is <c>/queue</c>.
    /// </summary>
    Queued = 12,
}

/// <summary>One item on the lower rule (2026-11-23): its kind and its text, drawn whole or not at all (<see cref="ScreenPane.LowerRuleFit"/>).</summary>
public readonly record struct LowerRuleItem(LowerRuleKind Kind, string Text);
