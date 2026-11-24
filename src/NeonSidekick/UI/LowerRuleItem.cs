namespace NeonSidekick.UI;

/// <summary>
/// What an item on the lower rule stands for (2026-11-23, the user's ask: the jobs group). The values are pinned: each kind's double-click
/// pair key is <c>-500_000 - kind</c> (<see cref="InputLine.LowerRulePairKey"/>), the processes' and the update's keeping the keys they had.
/// </summary>
public enum LowerRuleKind
{
    /// <summary>The running processes' count, <c>▶ n</c>: <c>/process</c>. Never dropped.</summary>
    Processes = 1,

    /// <summary>The update's marker, <c>📦 0.5.9 at quit</c>: <c>/update</c>. Dropped first.</summary>
    Update = 2,

    /// <summary>The loop under way: 🔁 a Ralph loop, 🔎 a code audit, ⟳ a <c>/loop</c>; a double-click ends it after the pass in progress.</summary>
    Loop = 3,

    /// <summary>An embedded model's download, <c>📥 ⠋ 42%</c>: a double-click cancels it.</summary>
    Download = 4,

    /// <summary>ComfyUI pictures still rendering, <c>🖼️ 2</c> / <c>🎨</c>: a double-click cancels them.</summary>
    Pictures = 5,
}

/// <summary>One item on the lower rule (2026-11-23): its kind and its text, drawn whole or not at all (<see cref="ScreenPane.LowerRuleFit"/>).</summary>
public readonly record struct LowerRuleItem(LowerRuleKind Kind, string Text);
