using Microsoft.Extensions.AI;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Settings;
using NeonSidekick.UI;
using NeonSidekick.Viewer;
using NeonSidekick.YouTube;

namespace NeonSidekick.App;

// ── YouTube (2026-10-05, the YouTube plan) ─────────────────────────────────

internal sealed partial class ChatScreen
{
    /// <summary>The video window (<see cref="VideoWindow.Player"/> in the app on Windows, a fake in tests), or null with none to have.</summary>
    private readonly IVideoPlayer? _videoPlayer;

    /// <summary>The YouTube tools, built once; offered while <see cref="YouTubeOffered"/> says so, cut by <see cref="YouTubeToolsFor"/>.</summary>
    private readonly IReadOnlyList<AIFunction> _youTubeTools;

    /// <summary>
    /// The YouTube tools (2026-10-05): <c>youtube_search</c> over <paramref name="search"/>, and — with a video window,
    /// <paramref name="player"/> — <c>youtube_play</c>, <c>youtube_control</c> and <c>youtube_status</c>. Headless passes no player:
    /// the search alone. Shared with headless.
    /// </summary>
    public static IReadOnlyList<AIFunction> YouTubeTools(IYouTubeSearch search, IVideoPlayer? player, Func<AppSettingsData> effective, TimeProvider? time = null) =>
        player is null
            ? [new YouTubeSearchTool(search, effective)]
            : [new YouTubeSearchTool(search, effective), new YouTubePlayTool(player, effective, time), new YouTubeControlTool(player, time), new YouTubeStatusTool(player)];

    /// <summary>The YouTube tools a turn may offer under <paramref name="effective"/>: <c>youtube_search</c> only while a key is set (playing needs none).</summary>
    public static IReadOnlyList<AIFunction> YouTubeToolsFor(IReadOnlyList<AIFunction> tools, AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(tools);
        return YouTubeDataApi.Key(effective) is null ? tools.Where(t => t is not YouTubeSearchTool).ToList() : tools;
    }

    /// <summary>Whether the YouTube group is offered: <c>YouTube tools</c> on and a tool left after the cut (a key, or a window to play in).</summary>
    public static bool YouTubeOffered(AppSettingsData effective, IReadOnlyList<AIFunction> tools)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return effective.YouTubeTools && YouTubeToolsFor(tools, effective).Count > 0;
    }

    /// <summary>The video's pause while the app speaks or listens (<c>YouTube while speaking</c>); null with no window.</summary>
    private readonly VideoVoicePause? _videoPause;

    /// <summary>The Data API client the search tool and <c>/youtube</c> share.</summary>
    private readonly IYouTubeSearch _youTubeSearch;

    /// <summary>
    /// <c>/youtube</c> (2026-10-05): a search and its picker, or a verb for the video window — the user's own hand, so neither
    /// <c>YouTube tools</c> nor plan mode judges it. Play and the controls go through the model's tools, so the checks, the wait
    /// for the player and the wording are theirs; a line starting <c>Error: </c> is an error, anything else a notice, through
    /// <paramref name="sink"/> (the transcript, or the flow sink under a reply, <see cref="MidTurnPolicy(SlashCommand, bool)"/>).
    /// </summary>
    private async Task HandleYouTubeAsync(string args, INoticeSink sink, bool spinner, CancellationToken cancellationToken)
    {
        var line = YouTubeCommand.Parse(args);
        if (line.Error is { } error)
        {
            sink.Error(error);
            return;
        }

        if (line.Verb == YouTubeVerb.Search)
        {
            await SearchYouTubeAsync(line.Text, sink, spinner, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (_videoPlayer is not { } player)
        {
            sink.Error(OperatingSystem.IsMacOS() ? YouTubeText.NoWindowMac : YouTubeText.NoWindow);
            return;
        }

        string result = line.Verb switch
        {
            YouTubeVerb.Status => YouTubeText.Status(player.Snapshot),
            YouTubeVerb.Play => await PlayVideoAsync(player, line.VideoId!, line.Number, cancellationToken).ConfigureAwait(false),
            _ => await ControlVideoAsync(player, line.Verb switch
            {
                YouTubeVerb.Resume => "play",
                YouTubeVerb.Pause => "pause",
                YouTubeVerb.Seek => "seek",
                YouTubeVerb.Volume => "volume",
                YouTubeVerb.Mute => "mute",
                YouTubeVerb.Unmute => "unmute",
                _ => "close",
            }, line.Number, cancellationToken).ConfigureAwait(false),
        };
        Show(result, sink);
    }

    // The search: the key's refusal, the hits on the picker (Enter plays), or listed where there is no pane or no window.
    private async Task SearchYouTubeAsync(string query, INoticeSink sink, bool spinner, CancellationToken cancellationToken)
    {
        var effective = _effective();
        if (YouTubeDataApi.Key(effective) is not { } key)
        {
            sink.Error(YouTubeText.NoKeyForUser);
            return;
        }

        int count = Math.Clamp(effective.YouTubeSearchMaxResults, AppSettingsData.MinYouTubeSearchMaxResults, AppSettingsData.MaxYouTubeSearchMaxResults);
        var search = () => _youTubeSearch.SearchAsync(query, count, key, cancellationToken);
        var outcome = spinner ? await _transcript.WithSpinnerAsync(YouTubeText.Searching, search).ConfigureAwait(false) : await search().ConfigureAwait(false);
        if (!outcome.Ok)
        {
            Show(YouTubeText.Failure(outcome.Failure, outcome.Detail), sink);
            return;
        }

        var hits = outcome.Hits.Take(count).ToList();
        if (hits.Count == 0 || !_pane.Enabled || _videoPlayer is not { } player)
        {
            foreach (string row in YouTubeText.Results(query, hits).Split('\n'))
            {
                sink.Notice(row);
            }

            return;
        }

        if (await _menu.PickVideoAsync(YouTubeText.PickTitle(query), hits.Select(YouTubeText.PickRow).ToList(), cancellationToken).ConfigureAwait(false) is { } picked)
        {
            Show(await PlayVideoAsync(player, hits[picked].Id, null, cancellationToken).ConfigureAwait(false), sink);
        }
    }

    private async Task<string> PlayVideoAsync(IVideoPlayer player, string id, double? start, CancellationToken cancellationToken)
    {
        var arguments = new AIFunctionArguments { [YouTubePlayTool.VideoArgument] = id };
        if (start is { } at)
        {
            arguments[YouTubePlayTool.StartArgument] = at.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return (string)(await new YouTubePlayTool(player, _effective, _time).InvokeAsync(arguments, cancellationToken).ConfigureAwait(false))!;
    }

    private async Task<string> ControlVideoAsync(IVideoPlayer player, string action, double? value, CancellationToken cancellationToken)
    {
        var arguments = new AIFunctionArguments { [YouTubeControlTool.ActionArgument] = action };
        if (value is { } number)
        {
            arguments[YouTubeControlTool.ValueArgument] = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return (string)(await new YouTubeControlTool(player, _time).InvokeAsync(arguments, cancellationToken).ConfigureAwait(false))!;
    }

    private static void Show(string result, INoticeSink sink)
    {
        const string Prefix = "Error: ";
        if (result.StartsWith(Prefix, StringComparison.Ordinal))
        {
            sink.Error(result[Prefix.Length..]);
        }
        else
        {
            sink.Notice(result);
        }
    }

    /// <summary>The YouTube tools' names (the plan-mode and <c>/tools</c> lists check them against <see cref="Plans.PlanTools"/>).</summary>
    public static readonly IReadOnlySet<string> YouTubeToolNames = new HashSet<string>(StringComparer.Ordinal)
    {
        YouTubeSearchTool.ToolName,
        YouTubePlayTool.ToolName,
        YouTubeControlTool.ToolName,
        YouTubeStatusTool.ToolName,
    };
}
