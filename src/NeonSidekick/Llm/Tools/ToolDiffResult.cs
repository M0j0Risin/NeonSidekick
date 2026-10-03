using NeonSidekick.Files;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// What a file-writing tool returns when its write changed something (2026-10-03, the user's ask: a diff under each edit in
/// the transcript): the text that goes back to the model as it always did, and the <see cref="FileDiff"/> the host draws.
/// <see cref="Assistant"/> keeps the text alone in the history and hands the diff on <see cref="TurnEvent.ToolResult.Diff"/>,
/// the <see cref="ToolImageResult"/> shape — the model never sees it.
/// </summary>
public sealed record ToolDiffResult(string Text, FileDiff Diff)
{
    /// <summary>The tool's answer: <paramref name="text"/> alone when there is no diff, else both.</summary>
    public static object Of(string text, FileDiff? diff) => diff is null ? text : new ToolDiffResult(text, diff);
}
