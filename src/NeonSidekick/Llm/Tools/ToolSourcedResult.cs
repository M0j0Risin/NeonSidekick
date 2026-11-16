using NeonSidekick.Web;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// What a tool returns when its answer came from the web (2026-11-15, the <see cref="ToolShownResult"/> and <see cref="ToolDiffResult"/>
/// pattern): <see cref="Assistant"/> keeps <paramref name="Text"/> alone in the history and hands <paramref name="Source"/> on
/// <see cref="TurnEvent.ToolResult.Source"/>, so the model never sees it and a side loop gets the text alone.
/// </summary>
public sealed record ToolSourcedResult(string Text, WebSource Source)
{
    /// <summary>
    /// <paramref name="text"/> with <paramref name="source"/>, or the text alone when there is none (2026-11-16, the web-tools review's #126,
    /// shaped like <see cref="ToolDiffResult.Of"/>).
    /// </summary>
    public static object Of(string text, WebSource? source) => source is null ? text : new ToolSourcedResult(text, source);
}
