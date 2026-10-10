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
    /// <c>youtube_playlist</c> (2026-10-08) beside the search, listing the playing playlist when there is a player. Headless passes no
    /// player: the search, the playlist listing and the saved videos. Shared with headless.
    /// </summary>
    public static IReadOnlyList<AIFunction> YouTubeTools(IYouTubeSearch search, IVideoPlayer? player, Func<AppSettingsData> effective, TimeProvider? time = null, Func<YouTubeLibrary>? library = null)
    {
        var tools = new List<AIFunction> { new YouTubeSearchTool(search, effective, time), new YouTubePlaylistTool(search, effective, player) };
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

    /// <summary>
    /// The YouTube tools a turn may offer under <paramref name="effective"/>: <c>youtube_search</c> and <c>youtube_playlist</c> only while a
    /// key is set (playing needs none).
    /// </summary>
    public static IReadOnlyList<AIFunction> YouTubeToolsFor(IReadOnlyList<AIFunction> tools, AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(tools);
        return YouTubeDataApi.Key(effective) is null ? tools.Where(t => t is not (YouTubeSearchTool or YouTubePlaylistTool)).ToList() : tools;
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
            await SearchYouTubeAsync(line.Text, line.Choices ?? YouTubeSearchChoices.None, sink, spinner, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (line.Verb == YouTubeVerb.Playlist)
        {
            await ShowPlaylistAsync(line.PlaylistId, sink, spinner, cancellationToken).ConfigureAwait(false);
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
            case YouTubeVerb.ClearSaved:
                await ClearSavedVideosAsync(sink, cancellationToken).ConfigureAwait(false);
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
            sink.Error(OperatingSystem.IsMacOS() ? YouTubeText.NoWindowMac : YouTubeText.NoWindow);
            return;
        }

        if (line.Verb == YouTubeVerb.Status)
        {
            Show(YouTubeText.Status(player.Snapshot), sink);
            return;
        }

        ShowWatched(await WatchPlayerAsync(token => line.Verb switch
        {
            YouTubeVerb.Play when line.PlaylistId is { } list => PlayPlaylistAsync(player, list, line.Entry, line.VideoId, line.Number, token),
            YouTubeVerb.Play => PlayVideoAsync(player, line.VideoId!, line.Number, token),
            _ => ControlVideoAsync(player, line.Verb switch
            {
                YouTubeVerb.Resume => "play",
                YouTubeVerb.Pause => "pause",
                YouTubeVerb.Seek => "seek",
                YouTubeVerb.Volume => "volume",
                YouTubeVerb.Mute => "mute",
                YouTubeVerb.Unmute => "unmute",
                YouTubeVerb.Next => "next",
                YouTubeVerb.Previous => "previous",
                YouTubeVerb.Entry => "entry",
                _ => "close",
            }, line.Number, token),
        }, spinner, cancellationToken).ConfigureAwait(false), sink, cancellationToken);
    }

    // The search: the key's refusal, the hits on the picker (Enter plays), or listed where there is no pane or no window. Its switches
    // (2026-10-08) are checked over the saved choices first; a playlist plays from its first entry and a channel its uploads.
    private async Task SearchYouTubeAsync(string query, YouTubeSearchChoices choices, INoticeSink sink, bool spinner, CancellationToken cancellationToken)
    {
        var effective = _effective();
        if (YouTubeDataApi.Key(effective) is not { } key)
        {
            sink.Error(YouTubeText.NoKeyForUser);
            return;
        }

        var (filters, error) = choices.Resolve(effective, _time.GetUtcNow());
        if (filters is null)
        {
            Show(error!, sink);
            return;
        }

        int count = YouTubeDataApi.DefaultCount(effective);
        var search = () => _youTubeSearch.SearchAsync(query, count, filters, key, cancellationToken);
        var outcome = spinner ? await _transcript.WithSpinnerAsync(YouTubeText.Searching, search).ConfigureAwait(false) : await search().ConfigureAwait(false);
        if (!outcome.Ok)
        {
            Show(YouTubeText.Failure(outcome.Failure, outcome.Detail), sink);
            return;
        }

        var hits = outcome.Hits.Take(count).ToList();
        if (hits.Count == 0 || !_pane.Enabled || _videoPlayer is not { } player)
        {
            foreach (string row in YouTubeText.Results(query, hits, filters.Kind).Split('\n'))
            {
                sink.Notice(row);
            }

            return;
        }

        // Enter plays the hit; s saves it (2026-10-07), the picker kept open with the answer on its status line. The pane's pick leaves
        // it open for the next page, so it is closed here: as Enter plays (before the wait, whose spinner takes its place) and after
        // ESC (2026-10-09, the user's report: since 2026-10-07's save button the picker stayed drawn over the input row after either,
        // the keys reaching a line no one could see).
        var rows = YouTubeText.PickRows(hits);
        int cursor = 0;
        var footers = hits.Select(YouTubeText.PickFooter).ToList();
        try
        {
            while (await _menu.PickVideoAsync(YouTubeText.PickTitle(query), rows, cursor, cancellationToken, footers).ConfigureAwait(false) is { } picked)
            {
                var hit = hits[picked.Row];
                if (!picked.Save)
                {
                    _menuPane.Close();
                    ShowWatched(await WatchPlayerAsync(token => hit.Kind switch
                    {
                        YouTubeKind.Playlist => PlayPlaylistAsync(player, hit.Id, null, null, null, token),
                        YouTubeKind.Channel => PlayPlaylistAsync(player, YouTubeIds.Uploads(hit.Id) ?? hit.Id, null, null, null, token),
                        _ => PlayVideoAsync(player, hit.Id, null, token),
                    }, spinner, cancellationToken).ConfigureAwait(false), sink, cancellationToken);
                    return;
                }

                cursor = picked.Row;
                if (hit.Kind != YouTubeKind.Video)
                {
                    _menuPane.Notice(YouTubeText.SaveVideosOnly(hit.Kind));
                    continue;
                }

                var library = YouTubeLibraryNow();
                var added = library.Add(hit.Id, hit.Title, hit.Channel, hit.Duration?.TotalSeconds ?? 0);
                Show(library.Find(hit.Id) is { } saved ? YouTubeText.Saved(saved, added) : "Error: " + YouTubeText.SaveFailed, _menuPane);
            }
        }
        finally
        {
            _menuPane.Close();
        }
    }

    /// <summary>
    /// <c>/youtube playlist [&lt;id|link&gt;]</c> (2026-10-08): the playlist named (or the one playing) read with the key, its entries on the
    /// picker — Enter plays the playlist from the highlighted one, <c>s</c> saves that video — or listed where there is no pane or no window.
    /// </summary>
    private async Task ShowPlaylistAsync(string? named, INoticeSink sink, bool spinner, CancellationToken cancellationToken)
    {
        var effective = _effective();
        if (YouTubeDataApi.Key(effective) is not { } key)
        {
            sink.Error(YouTubeText.NoKeyForPlaylist);
            return;
        }

        if ((named ?? _videoPlayer?.Snapshot?.PlaylistId) is not { } list)
        {
            Show(YouTubeText.NoPlaylistNamed, sink);
            return;
        }

        var read = () => _youTubeSearch.PlaylistAsync(list, YouTubeDataApi.MaxPlaylistEntries, key, cancellationToken);
        var outcome = spinner ? await _transcript.WithSpinnerAsync(YouTubeText.ReadingPlaylist, read).ConfigureAwait(false) : await read().ConfigureAwait(false);
        if (!outcome.Ok)
        {
            Show(YouTubeText.Failure(outcome.Failure, outcome.Detail), sink);
            return;
        }

        var playlist = outcome.Playlist!;
        var entries = outcome.Entries;
        if (entries.Count == 0 || !_pane.Enabled || _videoPlayer is not { } player)
        {
            foreach (string row in YouTubeText.PlaylistEntries(playlist, entries).Split('\n'))
            {
                sink.Notice(row);
            }

            return;
        }

        var rows = YouTubeText.PlaylistPickRows(entries);
        var footers = entries.Select(e => YouTubeText.PlaylistPickFooter(playlist, e)).ToList();
        int cursor = player.Snapshot is { PlaylistId: { } playing, PlaylistEntry: { } at } && playing == list ? Math.Clamp(at, 0, entries.Count - 1) : 0;
        // Closed as Enter plays and after ESC, the search picker's way (2026-10-09).
        try
        {
            while (await _menu.PickVideoAsync(YouTubeText.PlaylistPickTitle(playlist), rows, cursor, cancellationToken, footers).ConfigureAwait(false) is { } picked)
            {
                var entry = entries[picked.Row];
                if (!picked.Save)
                {
                    _menuPane.Close();
                    ShowWatched(await WatchPlayerAsync(token => PlayPlaylistAsync(player, list, entry.Position, null, null, token), spinner, cancellationToken).ConfigureAwait(false), sink, cancellationToken);
                    return;
                }

                cursor = picked.Row;
                var library = YouTubeLibraryNow();
                var added = library.Add(entry.VideoId, entry.Title, entry.Channel, 0);
                Show(library.Find(entry.VideoId) is { } saved ? YouTubeText.Saved(saved, added) : "Error: " + YouTubeText.SaveFailed, _menuPane);
            }
        }
        finally
        {
            _menuPane.Close();
        }
    }

    /// <summary>
    /// <c>/youtube list</c> (2026-10-07): the saved videos on <see cref="YouTubeSavedMenu"/>'s pane, Enter playing one where it was
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

    /// <summary>
    /// <c>/youtube list --clear</c> (2026-10-08, the user's ask): one yes/no (<see cref="ConfirmAsync"/>, <c>/msgclear</c>'s), then every
    /// saved video taken off the list; nothing saved is <see cref="YouTubeText.NoneSaved"/>, asked nothing.
    /// </summary>
    private async Task ClearSavedVideosAsync(INoticeSink sink, CancellationToken cancellationToken)
    {
        var library = YouTubeLibraryNow();
        int count = library.List().Count;
        if (count == 0)
        {
            sink.Notice(YouTubeText.NoneSaved);
            return;
        }

        if (!await ConfirmAsync(YouTubeText.ClearPrompt(count), cancellationToken).ConfigureAwait(false))
        {
            sink.Notice(KeptNotice);
            return;
        }

        Show(YouTubeText.ClearAnswer(library.Clear()), sink);
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

    // A playlist through youtube_play (2026-10-08): from entry (0-based here, 1-based on the tool), or the video named in it.
    private async Task<string> PlayPlaylistAsync(IVideoPlayer player, string list, int? entry, string? video, double? start, CancellationToken cancellationToken)
    {
        var arguments = new AIFunctionArguments { [YouTubePlayTool.PlaylistArgument] = list };
        if (entry is { } at)
        {
            arguments[YouTubePlayTool.EntryArgument] = at + 1;
        }

        if (video is not null)
        {
            arguments[YouTubePlayTool.VideoArgument] = video;
        }

        if (start is { } seconds)
        {
            arguments[YouTubePlayTool.StartArgument] = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
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

    /// <summary>
    /// A play, or a move through the window's video, waited for under a spinner that ESC or Ctrl+C cancels (2026-10-09, the user's
    /// report: a channel picked from a search opened a window the page never started, and the screen sat on the picker, keys dead,
    /// for the whole <see cref="YouTubeWait.Play"/>). Only the wait stops; the window carries on. Under a reply's own watch, or with
    /// no spinner (a line run mid-turn), the plain wait. Null when a key stopped it.
    /// </summary>
    private async Task<string?> WatchPlayerAsync(Func<CancellationToken, Task<string>> wait, bool spinner, CancellationToken cancellationToken)
    {
        if (!spinner || _turnRunning)
        {
            return await wait(cancellationToken).ConfigureAwait(false);
        }

        string? answer = null;
        await UnderWatchAsync(YouTubeText.WaitingForPlayer, async token => answer = await wait(token).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        return answer;
    }

    // WatchPlayerAsync's answer: the player's, or the wait stopped by a key (nothing when the app itself is stopping).
    private static void ShowWatched(string? result, INoticeSink sink, CancellationToken cancellationToken)
    {
        if (result is not null)
        {
            Show(result, sink);
        }
        else if (!cancellationToken.IsCancellationRequested)
        {
            sink.Notice(YouTubeText.StoppedWaiting);
        }
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
        YouTubePlaylistTool.ToolName,
        YouTubePlayTool.ToolName,
        YouTubeControlTool.ToolName,
        YouTubeStatusTool.ToolName,
        YouTubeSavedTool.ToolName,
        YouTubeSaveTool.ToolName,
    };
}
