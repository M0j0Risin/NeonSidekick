namespace NeonSidekick.Llm.Tools;

/// <summary>
/// What a tool returns when the user should read something other than what the model is told (2026-10-03, the shell police's
/// forbidden strings: the model gets <see cref="Shell.ShellText.Forbidden"/>, which names no string, and the user's 👮 line names
/// it). <see cref="Assistant"/> keeps <paramref name="Text"/> alone in the history and hands <paramref name="Shown"/> on
/// <see cref="TurnEvent.ToolResult.Shown"/>, the <see cref="ToolDiffResult"/> shape — the model never sees it, and a side loop,
/// which has no transcript, gets the text alone.
/// </summary>
public sealed record ToolShownResult(string Text, string Shown);
