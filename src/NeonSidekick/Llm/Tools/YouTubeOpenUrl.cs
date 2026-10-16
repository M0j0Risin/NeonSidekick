using Microsoft.Extensions.AI;
using NeonSidekick.Diagnostics;
using NeonSidekick.Viewer;
using NeonSidekick.YouTube;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>open_url</c>'s YouTube hook (2026-10-16, <c>YouTube open links in</c>; <c>ChatScreen.OpenYouTubeForToolAsync</c> calls it): a video or
/// <c>list=</c> link played through youtube_play's own tool — so a saved video resumes, a <c>list=</c> link plays as a playlist, a mix's link as
/// its video, and the wait and the words are youtube_play's — its answer the link's line. Passed on with no note while YouTube tools are off,
/// with no player, for anything not playable (<see cref="YouTubeIds.IsPlayable"/>: a channel's page is a page to look at), and for an id the
/// player already refused (the loop guard: the refusal itself names open_url); passed on with a note when YouTube refuses it now (101, 150,
/// 152, 153) or no player takes it. Pure but for the tool it is handed.
/// </summary>
public static class YouTubeOpenUrl
{
    private const string Category = "YouTube";

    /// <param name="youTubeTools">Whether <c>YouTube tools</c> is on.</param>
    /// <param name="player">The router, or null with no video window.</param>
    /// <param name="play">The routed youtube_play to run the link through.</param>
    /// <param name="target">Where a play goes now (<c>ChatScreen.PlayTarget</c>): one player but for the default browser.</param>
    public static async Task<YouTubeLinkOutcome> OpenAsync(Uri url, bool youTubeTools, IVideoPlayer? player, Func<YouTubePlayTool> play, YouTubeOpenLinksInKind target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(play);
        string link = url.AbsoluteUri;
        if (!youTubeTools || player is null || !YouTubeIds.IsPlayable(link))
        {
            return YouTubeLinkOutcome.PassOn;
        }

        string? id = YouTubeIds.TryParse(link, out string parsed, out _) ? parsed : null;
        if (id is not null && player.Snapshot is { Error: not null } refused && refused.VideoId == id)
        {
            return YouTubeLinkOutcome.PassOn;
        }

        var arguments = new AIFunctionArguments { [YouTubePlayTool.VideoArgument] = link };
        string answer = (string)(await play().InvokeAsync(arguments, cancellationToken).ConfigureAwait(false))!;
        if (!answer.StartsWith("Error: ", StringComparison.Ordinal))
        {
            return new YouTubeLinkOutcome(answer, OnePlayer: target != YouTubeOpenLinksInKind.DefaultBrowser);
        }

        bool wontEmbed = player.Snapshot is { Error: 101 or 150 or 152 or 153 } now && (id is null || now.VideoId == id);
        DiagnosticLog.Info(Category, $"open_url's {link} did not play ({answer}); passed on to Web open links in.");
        return new YouTubeLinkOutcome(null, wontEmbed ? YouTubeText.WontEmbedHere : YouTubeText.CouldNotPlay(answer["Error: ".Length..]));
    }
}
