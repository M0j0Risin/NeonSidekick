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
    /// <paramref name="player"/> — <c>youtube_play</c>, <c>youtube_control</c> and <c>youtube_status</c>; with the profile's saved
    /// videos, <paramref name="library"/> (2026-10-07), <c>youtube_saved</c> and <c>youtube_save</c> too, the play resuming a saved one.
    /// Headless passes no player: the search and the saved videos. Shared with headless.
    /// </summary>
    public static IReadOnlyList<AIFunction> YouTubeTools(IYouTubeSearch search, IVideoPlayer? player, Func<AppSettingsData> effective, TimeProvider? time = null, Func<YouTubeLibrary>? library = null)
    {
        var tools = new List<AIFunction> { new YouTubeSearchTool(search, effective) };
        if (player is not null)
        {
            tools.AddRange([new YouTubePlayTool(player, effective, time, library), new YouTubeControlTool(player, time), new YouTubeStatusTool(player)]);
        }

        if (library is not null)
        {
            var lookup = YouTubeTitles.Lookup(search, effective);
            tools.AddRange([new YouTubeSavedTool(library, lookup), new YouTubeSaveTool(library, player, lookup)]);
        }

        return tools;
    }

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

    /// <summary>The loaded profile's saved videos, made again when the profile moves (<see cref="YouTubeLibraryNow"/>).</summary>
    private YouTubeLibrary? _youTubeLibrary;

    /// <summary>
    /// The loaded profile's saved videos (2026-10-07): the one made for its directory, or a new one once a profile switch moved it.
    /// Read from the window's thread too (<see cref="YouTubeResume"/>); two made in a race read and write the same file under its
    /// atomic save, so the loser is merely dropped.
    /// </summary>
    private YouTubeLibrary YouTubeLibraryNow()
    {
        var library = _youTubeLibrary;
        if (library is null || !string.Equals(library.Directory, Path.GetFullPath(_settings.ProfileDirectory), StringComparison.OrdinalIgnoreCase))
        {
            library = new YouTubeLibrary(_settings.ProfileDirectory, _time);
            _youTubeLibrary = library;
        }

        return library;
    }

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

        // The saved videos (2026-10-07): the list needs no window; saving the one playing, and playing from the pane, do.
        switch (line.Verb)
        {
            case YouTubeVerb.Save:
            {
                // A video named by its id or link is looked up for its title first (2026-10-07), under the spinner.
                var save = () => YouTubeSaveTool.AddAsync(YouTubeLibraryNow(), _videoPlayer, line.VideoId ?? "", YouTubeTitles.Lookup(_youTubeSearch, _effective), cancellationToken);
                Show(spinner && line.VideoId is not null ? await _transcript.WithSpinnerAsync(YouTubeText.LookingUp, save).ConfigureAwait(false) : await save().ConfigureAwait(false), sink);
                return;
            }

            case YouTubeVerb.Unsave:
                Show(YouTubeSaveTool.Remove(YouTubeLibraryNow(), _videoPlayer, line.Text), sink);
                return;
            case YouTubeVerb.Saved:
            {
                // Any still untitled are looked up first (2026-10-07), under the spinner.
                var library = YouTubeLibraryNow();
                if (YouTubeTitles.AnyMissing(library))
                {
                    var fill = () => YouTubeTitles.FillMissingAsync(library, YouTubeTitles.Lookup(_youTubeSearch, _effective), cancellationToken);
                    _ = spinner ? await _transcript.WithSpinnerAsync(YouTubeText.LookingUpTitles, fill).ConfigureAwait(false) : await fill().ConfigureAwait(false);
                }

                await ShowSavedVideosAsync(sink, cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        if (_videoPlayer is not { } player)
        {
            sink.Error(YouTubeText.NoWindow);
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

        // Enter plays the hit; s saves it (2026-10-07), the picker kept open with the answer on its status line.
        var rows = hits.Select(YouTubeText.PickRow).ToList();
        int cursor = 0;
        var footers = hits.Select(YouTubeText.PickFooter).ToList();
        while (await _menu.PickVideoAsync(YouTubeText.PickTitle(query), rows, cursor, cancellationToken, footers).ConfigureAwait(false) is { } picked)
        {
            var hit = hits[picked.Row];
            if (!picked.Save)
            {
                Show(await PlayVideoAsync(player, hit.Id, null, cancellationToken).ConfigureAwait(false), sink);
                return;
            }

            var library = YouTubeLibraryNow();
            var added = library.Add(hit.Id, hit.Title, hit.Channel, hit.Duration?.TotalSeconds ?? 0);
            Show(library.Find(hit.Id) is { } saved ? YouTubeText.Saved(saved, added) : "Error: " + YouTubeText.SaveFailed, _menuPane);
            cursor = picked.Row;
        }
    }

    /// <summary>
    /// <c>/youtube saved</c> (2026-10-07): the saved videos on <see cref="YouTubeSavedMenu"/>'s pane, Enter playing one where it was
    /// left; without the pane, the rows as notices through <paramref name="sink"/>.
    /// </summary>
    private Task ShowSavedVideosAsync(INoticeSink sink, CancellationToken cancellationToken)
    {
        if (_pane.Enabled)
        {
            Func<YouTubeSaved, CancellationToken, Task<string>>? play = _videoPlayer is { } player ? (video, ct) => PlayVideoAsync(player, video.Id, null, ct) : null;
            return new YouTubeSavedMenu(YouTubeLibraryNow, sink, _menuPane, play, _time.LocalTimeZone).ShowAsync(cancellationToken);
        }

        var videos = YouTubeLibraryNow().List();
        if (videos.Count == 0)
        {
            sink.Notice(YouTubeText.NoneSaved);
            return Task.CompletedTask;
        }

        sink.Notice(YouTubeText.SavedCaption(videos.Count));
        for (int i = 0; i < videos.Count; i++)
        {
            sink.Notice($"{(i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)}. {YouTubeText.SavedRow(videos[i])} · {videos[i].Id}");
        }

        return Task.CompletedTask;
    }

    private async Task<string> PlayVideoAsync(IVideoPlayer player, string id, double? start, CancellationToken cancellationToken)
    {
        var arguments = new AIFunctionArguments { [YouTubePlayTool.VideoArgument] = id };
        if (start is { } at)
        {
            arguments[YouTubePlayTool.StartArgument] = at.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return (string)(await new YouTubePlayTool(player, _effective, _time, YouTubeLibraryNow).InvokeAsync(arguments, cancellationToken).ConfigureAwait(false))!;
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
        YouTubeSavedTool.ToolName,
        YouTubeSaveTool.ToolName,
    };
}
