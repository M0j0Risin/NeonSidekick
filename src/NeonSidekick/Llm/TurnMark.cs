namespace NeonSidekick.Llm;

/// <summary>
/// What a turn's user message carries beside its text (2026-10-31, review P1-5 and P8-1): <paramref name="Typed"/>, the line the user typed
/// when it differs from the text sent (a custom command's <c>/review x</c>), and <paramref name="AppWritten"/>, set when the app wrote the
/// message itself. Set on <see cref="Assistant.TurnMark"/> before a turn, stamped by <see cref="ConversationHistory.SetTurnMark"/>.
/// </summary>
public sealed record TurnMark(string? Typed, bool AppWritten)
{
    /// <summary>The mark of a message the app wrote.</summary>
    public static TurnMark App { get; } = new(null, true);

    /// <summary>The mark of a typed line sent as other text; null when the two are one.</summary>
    public static TurnMark? TypedAs(string typed, string sent) => string.Equals(typed, sent, StringComparison.Ordinal) ? null : new(typed, false);
}
