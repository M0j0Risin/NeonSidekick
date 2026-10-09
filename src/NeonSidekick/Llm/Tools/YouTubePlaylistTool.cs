using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Settings;
using NeonSidekick.Viewer;
using NeonSidekick.YouTube;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>youtube_playlist(playlist?, max?)</c> (2026-10-08, the user's ask): a public playlist's entries through the Data API with the user's
/// key (<see cref="IYouTubeSearch.PlaylistAsync"/>: one unit for the playlist, one per 50 entries), numbered as <c>youtube_play</c>'s
/// <c>entry</c> counts them, each with its title, channel and id; a deleted or private entry said so. Named by an id, a link with
/// <c>list=</c> or a channel (its uploads), or left out for the playlist the video window is playing. Reads only; offered while YouTube
/// tools is on and a key is set, headless too (where nothing plays, so a playlist must be named).
/// </summary>
public sealed class YouTubePlaylistTool : AIFunction
{
    public const string ToolName = "youtube_playlist";
    public const string PlaylistArgument = "playlist";
    public const string MaxArgument = "max";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "playlist": { "type": "string", "description": "The playlist: its id (PL…), a link with list=, or a channel's id (UC…) for its uploads. Leave it out for the one playing." },
            "max": { "type": "integer", "description": "How many entries to list, 1 to 200 (50 if left out)." }
          }
        }
        """);

    private readonly IYouTubeSearch _search;
    private readonly Func<AppSettingsData> _effective;
    private readonly IVideoPlayer? _player;

    /// <param name="player">The video window, whose playing playlist is the one listed when none is named; null headless.</param>
    public YouTubePlaylistTool(IYouTubeSearch search, Func<AppSettingsData> effective, IVideoPlayer? player = null)
    {
        _search = search ?? throw new ArgumentNullException(nameof(search));
        _effective = effective ?? throw new ArgumentNullException(nameof(effective));
        _player = player;
    }

    public override string Name => ToolName;

    /// <summary>Pinned.</summary>
    public override string Description =>
        "Lists a public YouTube playlist's videos, numbered, each with its title, channel and id: the one named, or the one playing. " +
        "Use it when the user asks what is in a playlist or wants one of its videos; " + YouTubePlayTool.ToolName + " with the playlist and an entry number plays from it.";

    public override JsonElement JsonSchema => Schema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!ToolArguments.TryReadInt32(arguments, MaxArgument, out int? max, out string raw))
        {
            return ClockText.BadInteger(MaxArgument, raw);
        }

        if (max is < 1 or > YouTubeDataApi.MaxPlaylistEntries)
        {
            return YouTubeText.BadPlaylistMax;
        }

        string named = ToolArguments.ReadString(arguments, PlaylistArgument).Trim();
        string list;
        if (named.Length == 0)
        {
            if (_player?.Snapshot?.PlaylistId is not { } playing)
            {
                return YouTubeText.NoPlaylistNamed;
            }

            list = playing;
        }
        else if (!YouTubeIds.TryParsePlaylist(named, out list, out _, out _, out _))
        {
            return YouTubeText.NotAPlaylist(named);
        }

        var outcome = await _search.PlaylistAsync(list, max ?? YouTubeDataApi.DefaultPlaylistEntries, YouTubeDataApi.Key(_effective()) ?? "", cancellationToken).ConfigureAwait(false);
        return outcome.Ok ? YouTubeText.PlaylistEntries(outcome.Playlist!, outcome.Entries) : YouTubeText.Failure(outcome.Failure, outcome.Detail);
    }
}
