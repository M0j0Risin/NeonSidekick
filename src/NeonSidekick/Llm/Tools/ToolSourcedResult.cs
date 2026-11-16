namespace NeonSidekick.Llm.Tools;

/// <summary>What a <see cref="WebSource"/> records: a page read (<c>web_fetch</c>), a file saved (<c>download_file</c>), or a search.</summary>
public enum WebSourceKind
{
    Page,
    Download,

    /// <summary>A <c>web_search</c> that answered (2026-11-15): counted under a reply, never listed, so stored with no URL or title.</summary>
    Search,
}

/// <summary>
/// One thing a turn took from the web (2026-11-15, the web-tools plan's §2, feature idea #27): the address read or saved and the page's
/// title (a download's file name), for the 📚 line under the reply, the Web UI and the session's export.
/// </summary>
public sealed record WebSource(WebSourceKind Kind, string Url, string Title);

/// <summary>
/// What a tool returns when its answer came from the web (2026-11-15, the <see cref="ToolShownResult"/> and <see cref="ToolDiffResult"/>
/// pattern): <see cref="Assistant"/> keeps <paramref name="Text"/> alone in the history and hands <paramref name="Source"/> on
/// <see cref="TurnEvent.ToolResult.Source"/>, so the model never sees it and a side loop gets the text alone.
/// </summary>
public sealed record ToolSourcedResult(string Text, WebSource Source);
