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
    /// <summary>
    /// The video player every YouTube path reads (2026-10-16: the router, <see cref="_routedVideo"/>, over the video window —
    /// <see cref="VideoWindow.Player"/> in the app on Windows, a fake in tests — and the Web UI's player), or null with no window to have.
    /// </summary>
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
    /// <remarks>
    /// <paramref name="route"/>, <paramref name="handoff"/> and <paramref name="target"/> (2026-10-16, <c>YouTube open links in</c>): where a play
    /// goes, the video last sent to a browser, and where a play would go now for the descriptions. Headless passes none: the window's wording.
    /// </remarks>
    public static IReadOnlyList<AIFunction> YouTubeTools(IYouTubeSearch search, IVideoPlayer? player, Func<AppSettingsData> effective, TimeProvider? time = null, Func<YouTubeLibrary>? library = null,
        Func<VideoRequest, VideoRouted>? route = null, Func<VideoHandoff?>? handoff = null, Func<YouTubeOpenLinksInKind>? target = null)
    {
        var tools = new List<AIFunction> { new YouTubeSearchTool(search, effective, time), new YouTubePlaylistTool(search, effective, player) };
        if (player is not null)
        {
            tools.AddRange([new YouTubePlayTool(player, effective, time, library, route, target), new YouTubeControlTool(player, time, handoff, target), new YouTubeStatusTool(player, handoff, target)]);
        }

        if (library is not null)
        {
            var lookup = YouTubeTitles.Lookup(search, effective);
            var playlistLookup = YouTubeTitles.PlaylistLookup(search, effective);
            tools.AddRange([new YouTubeSavedTool(library, lookup, playlistLookup), new YouTubeSaveTool(library, player, lookup, target, playlistLookup)]);
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

    /// <summary><c>/youtube</c>'s argument list (2026-10-27): the verbs, and the saved videos after <c>unsave </c> and <c>play </c>.</summary>
    private IReadOnlyList<CompletionItem> YouTubeChoices(string argText) => YouTubeCommand.Complete(argText, () =>
    {
        var library = YouTubeLibraryNow();
        return CompletionRead(library.FilePath, library.List);
    });

    /// <summary>
    /// The loaded profile's saved videos (2026-10-07): the one made for its directory, or a new one once a profile switch moved it.
    /// Read from the window's thread too (<see cref="YouTubeResume"/>) and (later on 2026-10-16) from Kestrel's, the Web UI's saved list:
    /// the field read and swapped atomically, and two made in a race share the library's one lock over the same file, so neither loses a
    /// change; the one that lost the swap is simply not kept.
    /// </summary>
    private YouTubeLibrary YouTubeLibraryNow()
    {
        var library = Volatile.Read(ref _youTubeLibrary);
        if (library is null || !string.Equals(library.Directory, Path.GetFullPath(_settings.ProfileDirectory), StringComparison.OrdinalIgnoreCase))
        {
            var made = new YouTubeLibrary(_settings.ProfileDirectory, _time);
            var seen = Interlocked.CompareExchange(ref _youTubeLibrary, made, library);
            library = ReferenceEquals(seen, library) ? made
                : seen is not null && string.Equals(seen.Directory, made.Directory, StringComparison.OrdinalIgnoreCase) ? seen
                : made;
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
                // A video named by its id or link is looked up for its title first (2026-10-07), under the spinner; a playlist too (2026-11-08),
                // by the text typed rather than its bare id, so a link's index=, v= and t= are where it is saved at (the review).
                Func<Task<string>> save = line.PlaylistId is { } list
                    ? () => YouTubeSaveTool.AddPlaylistAsync(YouTubeLibraryNow(), _videoPlayer, list == YouTubeSaveTool.Current ? list : line.Text, YouTubeTitles.PlaylistLookup(_youTubeSearch, _effective), cancellationToken)
                    : () => YouTubeSaveTool.AddAsync(YouTubeLibraryNow(), _videoPlayer, line.VideoId ?? "", YouTubeTitles.Lookup(_youTubeSearch, _effective), cancellationToken);
                bool named = line.VideoId is not null || line.PlaylistId is not null;
                string label = line.PlaylistId is null ? YouTubeText.LookingUp : YouTubeText.LookingUpPlaylist;
                Show(spinner && named ? await _transcript.WithSpinnerAsync(label, save).ConfigureAwait(false) : await save().ConfigureAwait(false), sink);
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
                    var fill = () => YouTubeTitles.FillMissingAsync(library, YouTubeTitles.Lookup(_youTubeSearch, _effective), YouTubeTitles.PlaylistLookup(_youTubeSearch, _effective), cancellationToken);
                    _ = spinner ? await _transcript.WithSpinnerAsync(YouTubeText.LookingUpTitles, fill).ConfigureAwait(false) : await fill().ConfigureAwait(false);
                }

                await ShowSavedVideosAsync(sink, cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        if (_videoPlayer is not { } player)
        {
            sink.Error(YouTubeText.NoWindowHere);
            return;
        }

        if (line.Verb == YouTubeVerb.Status)
        {
            Show(YouTubeText.StatusOrHandoff(player.Snapshot, _routedVideo?.LastHandoff), sink);
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
        }, spinner, cancellationToken, play: line.Verb == YouTubeVerb.Play).ConfigureAwait(false), sink, cancellationToken);
    }

    // The search: the key's refusal, the hits on the picker (Enter plays), or listed where there is no pane or no window. Its switches
    // (2026-10-08) are checked over the saved choices first; a playlist plays from its first entry and a channel its uploads, unless it is
    // saved: then it resumes where it was left, as a saved video does (youtube_play's resume, 2026-11-08).
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

        // Enter plays the hit; s saves it (2026-10-07; a playlist, or a channel as its uploads, since 2026-11-08), the picker kept open
        // with the answer on its status line. The pane's pick leaves
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
                if (picked.Button < 0)
                {
                    _menuPane.Close();
                    ShowWatched(await WatchPlayerAsync(token => hit.Kind switch
                    {
                        YouTubeKind.Playlist => PlayPlaylistAsync(player, hit.Id, null, null, null, token),
                        YouTubeKind.Channel => PlayPlaylistAsync(player, YouTubeIds.Uploads(hit.Id) ?? hit.Id, null, null, null, token),
                        _ => PlayVideoAsync(player, hit.Id, null, token),
                    }, spinner, cancellationToken, play: true).ConfigureAwait(false), sink, cancellationToken);
                    return;
                }

                cursor = picked.Row;
                var library = YouTubeLibraryNow();
                string id = hit.Kind switch
                {
                    YouTubeKind.Channel => YouTubeIds.Uploads(hit.Id) ?? hit.Id,
                    _ => hit.Id,
                };
                if (hit.Kind is YouTubeKind.Playlist or YouTubeKind.Channel && !YouTubeLibrary.CanSavePlaylist(id))
                {
                    Show(YouTubeText.CannotSaveHit, _menuPane);   // nothing was written: no write failure to report (2026-11-08, the review)
                    continue;
                }

                int entries = hit.Count is { } n ? (int)Math.Clamp(n, 0, int.MaxValue) : 0;
                var added = hit.Kind switch
                {
                    // A channel's uploads are named after the channel (2026-11-08): the hit's title is its name, so its channel, the same
                    // name again, is left out (the review).
                    YouTubeKind.Channel => library.AddPlaylist(id, hit.Title, hit.Channel == hit.Title ? null : hit.Channel, entries),
                    YouTubeKind.Playlist => library.AddPlaylist(id, hit.Title, hit.Channel, entries),
                    _ => library.Add(id, hit.Title, hit.Channel, hit.Duration?.TotalSeconds ?? 0),
                };
                Show(library.Find(id) is { } saved ? YouTubeText.Saved(saved, added) : "Error: " + YouTubeText.SaveFailed, _menuPane);
            }
        }
        finally
        {
            _menuPane.Close();
        }
    }

    /// <summary>
    /// <c>/youtube playlist [&lt;id|link&gt;]</c> (2026-10-08): the playlist named (or the one playing) read with the key, its entries on the
    /// picker — Enter plays the playlist from the highlighted one, <c>s</c> saves that video, <c>l</c> the whole playlist (2026-11-08) — or
    /// listed where there is no pane or no window.
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
            while (await _menu.PickVideoAsync(YouTubeText.PlaylistPickTitle(playlist), rows, cursor, cancellationToken, footers, saveList: true).ConfigureAwait(false) is { } picked)
            {
                var entry = entries[picked.Row];
                if (picked.Button < 0)
                {
                    _menuPane.Close();
                    ShowWatched(await WatchPlayerAsync(token => PlayPlaylistAsync(player, list, entry.Position, null, null, token), spinner, cancellationToken, play: true).ConfigureAwait(false), sink, cancellationToken);
                    return;
                }

                cursor = picked.Row;
                var library = YouTubeLibraryNow();
                if (picked.Button == 1)
                {
                    if (!YouTubeLibrary.CanSavePlaylist(list))
                    {
                        Show(YouTubeText.CannotSaveHit, _menuPane);
                        continue;
                    }

                    // The whole playlist (2026-11-08), from its start: its first listed entry, at that entry's own place (one before it may be
                    // private or gone, 2026-11-08, the review). The count only when playlists.list listed the list (it has a title then);
                    // otherwise it is only the entries read, and the player's own count fills it in at the first play.
                    var first = entries[0];
                    int count = playlist.Title.Length > 0 ? playlist.Count : 0;
                    var listed = library.AddPlaylist(list, playlist.Title, playlist.Channel, count, first.Position, first.VideoId);
                    Show(library.Find(list) is { } savedList ? YouTubeText.Saved(savedList, listed) : "Error: " + YouTubeText.SaveFailed, _menuPane);
                    continue;
                }

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
            // A saved playlist plays through youtube_play's resume (2026-11-08): named alone, it starts at its entry and place.
            Func<YouTubeSaved, CancellationToken, Task<string>>? play = _videoPlayer is { } player
                ? (video, ct) => video.Playlist ? PlayPlaylistAsync(player, video.Id, null, null, null, ct) : PlayVideoAsync(player, video.Id, null, ct)
                : null;
            return new YouTubeSavedMenu(YouTubeLibraryNow, sink, _menuPane, play, _time.LocalTimeZone, _time).ShowAsync(cancellationToken);
        }

        var videos = YouTubeLibraryNow().List();
        if (videos.Count == 0)
        {
            sink.Notice(YouTubeText.NoneSaved);
            return Task.CompletedTask;
        }

        sink.Notice(YouTubeText.SavedCaption(videos));
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

        return (string)(await PlayToolFor(player).InvokeAsync(arguments, cancellationToken).ConfigureAwait(false))!;
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

        return (string)(await PlayToolFor(player).InvokeAsync(arguments, cancellationToken).ConfigureAwait(false))!;
    }

    private async Task<string> ControlVideoAsync(IVideoPlayer player, string action, double? value, CancellationToken cancellationToken)
    {
        var arguments = new AIFunctionArguments { [YouTubeControlTool.ActionArgument] = action };
        if (value is { } number)
        {
            arguments[YouTubeControlTool.ValueArgument] = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return (string)(await ControlToolFor(player).InvokeAsync(arguments, cancellationToken).ConfigureAwait(false))!;
    }

    /// <summary>
    /// A play, or a move through the window's video, waited for under a spinner that ESC or Ctrl+C cancels (2026-10-09, the user's
    /// report: a channel picked from a search opened a window the page never started, and the screen sat on the picker, keys dead,
    /// for the whole <see cref="YouTubeWait.Play"/>). Only the wait stops; the window carries on. Under a reply's own watch, or with
    /// no spinner (a line run mid-turn), the plain wait. Null when a key stopped it.
    /// </summary>
    private async Task<string?> WatchPlayerAsync(Func<CancellationToken, Task<string>> wait, bool spinner, CancellationToken cancellationToken, bool play = false)
    {
        if (!spinner || _turnRunning)
        {
            return await wait(cancellationToken).ConfigureAwait(false);
        }

        // The spinner names where a play is going (2026-10-16), worked out by PlayTarget as the play itself does; a command the player in use.
        string label = play ? YouTubeText.WaitingFor(PlayTarget().Kind)
            : _videoPlayer?.Place == VideoPlace.WebTab ? YouTubeText.WaitingFor(YouTubeOpenLinksInKind.WebUiWindow) : YouTubeText.WaitingForPlayer;
        string? answer = null;
        await UnderWatchAsync(label, async token => answer = await wait(token).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        return answer;
    }

    // WatchPlayerAsync's answer: the player's, or the wait stopped by a key (nothing when the app itself is stopping), worded for what plays now.
    private void ShowWatched(string? result, INoticeSink sink, CancellationToken cancellationToken)
    {
        if (result is not null)
        {
            Show(result, sink);
        }
        else if (!cancellationToken.IsCancellationRequested)
        {
            sink.Notice(YouTubeText.StoppedWaitingFor(_videoPlayer?.Snapshot));
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
