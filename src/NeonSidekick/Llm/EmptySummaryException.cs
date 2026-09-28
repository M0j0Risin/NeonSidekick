namespace NeonSidekick.Llm;

/// <summary>
/// What the summariser throws when the server's reply held no text (2026-09-28, the user's report of
/// <c>🗜️ Compact failed: InvalidOperationException: The server returned an empty summary.</c> in a long tool turn):
/// the message says why — the length limit, thinking alone, a tool call — or <see cref="Assistant.EmptySummaryError"/>
/// when the reply gave no clue. <see cref="Assistant.Explain"/> shows the message without the type's name.
/// </summary>
public sealed class EmptySummaryException(string message) : InvalidOperationException(message);
