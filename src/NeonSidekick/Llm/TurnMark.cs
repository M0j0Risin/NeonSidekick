namespace NeonSidekick.Llm;

/// <summary>
/// What a turn's user message carries beside its text (2026-10-31, review P1-5 and P8-1): <paramref name="Typed"/>, the line the user typed
/// when it differs from the text sent (a custom command's <c>/review x</c>), and <paramref name="AppWritten"/>, set when the app wrote the
/// message itself. Set on <see cref="Assistant.TurnMark"/> before a turn, stamped by <see cref="ConversationHistory.SetTurnMark"/>.
/// <paramref name="IsPass"/> (2026-11-24, the session log review) marks an automated run's pass (a gap review's, an audit's, a Ralph loop's,
/// <c>/project-init</c>'s), which a reflection's window leaves out; always app-written.
/// </summary>
public sealed record TurnMark(string? Typed, bool AppWritten, bool IsPass = false)
{
    /// <summary>The mark of a message the app wrote.</summary>
    public static TurnMark App { get; } = new(null, true);

    /// <summary>The mark of an automated run's pass message: app-written and a pass.</summary>
    public static TurnMark Pass { get; } = new(null, true, IsPass: true);

    /// <summary>The mark of a typed line sent as other text; null when the two are one.</summary>
    public static TurnMark? TypedAs(string typed, string sent) => string.Equals(typed, sent, StringComparison.Ordinal) ? null : new(typed, false);
}
