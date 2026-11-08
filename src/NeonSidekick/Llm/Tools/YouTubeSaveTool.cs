using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Viewer;
using NeonSidekick.YouTube;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>youtube_save(action, video)</c> (2026-10-07, the user's ask): a video added to the profile's saved videos or taken off them
/// (<see cref="YouTubeLibrary"/>). <c>video</c> is an id or a link; <c>current</c> the one in the video window (its title, channel,
/// length and place come with it); on remove also its number in <see cref="YouTubeSavedTool"/>'s list. A saved video resumes where
/// it is left (<see cref="YouTubeResume"/>). Headless too, with no window: <c>current</c> is then nothing to save. A video saved by
/// its id or link is looked up first for its title and channel (2026-10-07, <see cref="YouTubeTitles"/>); not found, it is saved by
/// its id, and named the first time it plays.
///
/// <para>A playlist (2026-11-08, the user's ask: saved playlists in the same list): <c>playlist</c> in place of <c>video</c> — an id, a
/// link with <c>list=</c>, or a channel for its uploads; <c>current</c> the list playing, saved at its entry and place — or such a link
/// given as <c>video</c>. Exactly one of the two. It is looked up for its title the same way (oEmbed needs no key), and resumes at the
/// entry and place it is left at. A mix is refused: the player cannot load one.</para>
/// </summary>
public sealed class YouTubeSaveTool : AIFunction
{
    public const string ToolName = "youtube_save";
    public const string ActionArgument = "action";
    public const string VideoArgument = "video";
    public const string PlaylistArgument = "playlist";

    /// <summary>The word for the video in the window.</summary>
    public const string Current = "current";

    private static readonly JsonElement Schema = Build("the one in the video window", "the list in the video window");

    // The schema per YouTube open links in (2026-10-16): only current's words change, the window's byte for byte.
    private static readonly JsonElement WebSchema = Build("the one in the Web UI's YouTube tab", "the list in the Web UI's YouTube tab");

    private static readonly JsonElement BrowserSchema = Build(
        "the one in the app's own video players (never one opened in a browser)", "the list in the app's own video players (never one opened in a browser)");

    // LISTCURRENT goes first: it holds CURRENT.
    private static JsonElement Build(string current, string listCurrent) => ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "action": { "type": "string", "enum": ["add", "remove"], "description": "add saves the video or playlist; remove takes it off the saved list." },
            "video": { "type": "string", "description": "The video's 11-character id or a YouTube link; \"current\" for CURRENT; for remove, also its number in youtube_saved's list." },
            "playlist": { "type": "string", "description": "A playlist to save instead of a video: its id (PL…), a link with list=, or a channel's id (UC…) for its uploads; \"current\" for LISTCURRENT; for remove, also its number in youtube_saved's list." }
          },
          "required": ["action"]
        }
        """.Replace("LISTCURRENT", listCurrent, StringComparison.Ordinal).Replace("CURRENT", current, StringComparison.Ordinal));

    private readonly Func<YouTubeLibrary> _library;
    private readonly IVideoPlayer? _player;
    private readonly YouTubeLookup? _lookup;
    private readonly Func<YouTubeOpenLinksInKind>? _target;
    private readonly YouTubePlaylistLookup? _playlistLookup;

    /// <param name="player">The video window, for <c>current</c>; null headless.</param>
    /// <param name="lookup">A video's title by its id, for one saved by id or link; null saves it untitled.</param>
    /// <param name="target">Where a play goes now, for <c>current</c>'s words in the schema (2026-10-16); null is the video window.</param>
    /// <param name="playlistLookup">A playlist's title by its id (2026-11-08); null saves it untitled.</param>
    public YouTubeSaveTool(Func<YouTubeLibrary> library, IVideoPlayer? player, YouTubeLookup? lookup = null, Func<YouTubeOpenLinksInKind>? target = null,
        YouTubePlaylistLookup? playlistLookup = null)
    {
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _player = player;
        _lookup = lookup;
        _target = target;
        _playlistLookup = playlistLookup;
    }

    public override string Name => ToolName;

    /// <summary>Pinned.</summary>
    public override string Description =>
        "Adds a YouTube video or playlist to the user's saved videos (their bookmarks), or removes one. A saved video resumes where it was left the next time it plays, " +
        "a saved playlist at its entry and place. Save only what the user asked to keep.";

    /// <summary>Its <c>current</c> words follow <c>YouTube open links in</c> (2026-10-16), one cached schema per value; the window's byte for byte.</summary>
    public override JsonElement JsonSchema => SchemaFor(_target?.Invoke() ?? YouTubeOpenLinksInKind.YouTubeWindow);

    /// <summary>The schema where a play goes to <paramref name="target"/>.</summary>
    public static JsonElement SchemaFor(YouTubeOpenLinksInKind target) => target switch
    {
        YouTubeOpenLinksInKind.WebUiWindow => WebSchema,
        YouTubeOpenLinksInKind.BrowserWindow or YouTubeOpenLinksInKind.DefaultBrowser => BrowserSchema,
        _ => Schema,
    };

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        string action = ToolArguments.ReadString(arguments, ActionArgument).Trim().ToLowerInvariant();
        string video = ToolArguments.ReadString(arguments, VideoArgument).Trim();
        string playlist = ToolArguments.ReadString(arguments, PlaylistArgument).Trim();
        if (action is not ("add" or "remove"))
        {
            return YouTubeText.NotASaveAction(action);
        }

        if (video.Length > 0 && playlist.Length > 0)
        {
            return YouTubeText.VideoOrPlaylist;
        }

        // A playlist's link or id given as the video is the playlist (2026-11-08), as youtube_play takes one.
        if (playlist.Length == 0 && NamesPlaylist(video))
        {
            (playlist, video) = (video, "");
        }

        if (playlist.Length > 0)
        {
            return action == "add"
                ? await AddPlaylistAsync(_library(), _player, playlist, _playlistLookup, cancellationToken).ConfigureAwait(false)
                : RemovePlaylist(_library(), _player, playlist);
        }

        return action == "add"
            ? await AddAsync(_library(), _player, video, _lookup, cancellationToken).ConfigureAwait(false)
            : Remove(_library(), _player, video);
    }

    /// <summary>Whether <paramref name="text"/>, given as a video, names a playlist (or a mix) and no video: a <c>/playlist?list=</c> link, a list's id.</summary>
    public static bool NamesPlaylist(string text) =>
        text.Length > 0 && !text.Equals(Current, StringComparison.OrdinalIgnoreCase) && !YouTubeIds.TryParse(text, out _, out _)
        && (YouTubeIds.TryParsePlaylist(text, out _, out _, out _, out _) || YouTubeIds.NamesMix(text));

    /// <summary>
    /// <paramref name="playlist"/> saved (2026-11-08): <see cref="Current"/> (or empty) the list playing, at its entry and place, else an id,
    /// a link or a channel, from its start. Looked up through <paramref name="lookup"/> first when not saved yet. The answer as a sentence;
    /// <c>Error: </c> leads a refusal. Shared with <c>/youtube save</c>, headless and the Web UI.
    /// </summary>
    public static async Task<string> AddPlaylistAsync(YouTubeLibrary library, IVideoPlayer? player, string playlist, YouTubePlaylistLookup? lookup, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(library);
        playlist = (playlist ?? "").Trim();
        if (playlist.Length == 0 || playlist.Equals(Current, StringComparison.OrdinalIgnoreCase))
        {
            if (player?.Snapshot is not { PlaylistId: { } playing } snapshot || snapshot.State is VideoState.Failed)
            {
                return "Error: " + YouTubeText.NoPlaylistToSave;
            }

            var found = library.Find(playing) is null && lookup is not null ? await lookup(playing, cancellationToken).ConfigureAwait(false) : null;
            var added = library.AddPlaylist(playing, found?.Title, found?.Author, snapshot.PlaylistCount ?? found?.Count ?? 0,
                snapshot.PlaylistEntry ?? 0, snapshot.VideoId, snapshot.Position, snapshot.Duration);
            return library.Find(playing) is { } kept ? YouTubeText.Saved(kept, added) : "Error: " + YouTubeText.SaveFailed;
        }

        if (!YouTubeIds.TryParsePlaylist(playlist, out string list, out _, out _, out _))
        {
            return YouTubeIds.NamesMix(playlist) ? YouTubeText.Mix(playlist) : YouTubeText.NotAPlaylist(playlist);
        }

        var info = library.Find(list) is null && lookup is not null ? await lookup(list, cancellationToken).ConfigureAwait(false) : null;
        var outcome = library.AddPlaylist(list, info?.Title, info?.Author, info?.Count ?? 0, 0, info?.FirstVideo);
        return library.Find(list) is { } saved ? YouTubeText.Saved(saved, outcome) : "Error: " + YouTubeText.SaveFailed;
    }

    /// <summary><see cref="AddPlaylistAsync"/> without a lookup (2026-11-08, the Web UI's ☆ list): a playlist saved untitled until the list is next read.</summary>
    public static string AddPlaylist(YouTubeLibrary library, IVideoPlayer? player, string playlist)
    {
        var answer = AddPlaylistAsync(library, player, playlist, null, CancellationToken.None);
        return answer.IsCompleted ? answer.GetAwaiter().GetResult() : throw new InvalidOperationException("A save with no lookup finishes at once.");
    }

    /// <summary>
    /// <paramref name="playlist"/> taken off the list (2026-11-08): <see cref="Current"/> the list playing, else its number, id or link.
    /// Shared with <c>/youtube unsave</c>.
    /// </summary>
    public static string RemovePlaylist(YouTubeLibrary library, IVideoPlayer? player, string playlist)
    {
        ArgumentNullException.ThrowIfNull(library);
        var saved = playlist.Equals(Current, StringComparison.OrdinalIgnoreCase)
            ? player?.Snapshot is { PlaylistId: { } playing } ? library.Find(playing) : null
            : YouTubeIds.TryParsePlaylist(playlist, out string list, out _, out _, out _) && library.Find(list) is { Playlist: true } named ? named
            : library.Resolve(playlist);
        if (saved is null)
        {
            return YouTubeText.NotSaved(playlist);
        }

        return library.Remove(saved.Id) ? YouTubeText.Unsaved(saved) : "Error: " + YouTubeText.SaveFailed;
    }

    /// <summary>
    /// <paramref name="video"/> saved: <see cref="Current"/> (or empty) the window's video with what it reports, else an id or a link,
    /// looked up through <paramref name="lookup"/> first when it is not saved yet. The answer as a sentence; <c>Error: </c> leads a
    /// refusal. Shared with <c>/youtube save</c> and headless.
    /// </summary>
    public static async Task<string> AddAsync(YouTubeLibrary library, IVideoPlayer? player, string video, YouTubeLookup? lookup, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(library);
        if (video.Length > 0 && !video.Equals(Current, StringComparison.OrdinalIgnoreCase) && YouTubeIds.TryParse(video, out string id, out _)
            && lookup is not null && library.Find(id) is null && await lookup(id, cancellationToken).ConfigureAwait(false) is { } info)
        {
            var outcome = library.Add(id, info.Title, info.Author, info.Duration);
            return library.Find(id) is { } saved ? YouTubeText.Saved(saved, outcome) : "Error: " + YouTubeText.SaveFailed;
        }

        return Add(library, player, video);
    }

    /// <summary><see cref="AddAsync"/> without a lookup: a video saved by its id or link is untitled until it plays.</summary>
    public static string Add(YouTubeLibrary library, IVideoPlayer? player, string video)
    {
        ArgumentNullException.ThrowIfNull(library);
        YouTubeSaveOutcome outcome;
        string id;
        if (video.Length == 0 || video.Equals(Current, StringComparison.OrdinalIgnoreCase))
        {
            if (player?.Snapshot is not { VideoId: { } playing } snapshot || snapshot.State is VideoState.Failed)
            {
                return "Error: " + YouTubeText.NothingToSave;
            }

            id = playing;
            outcome = library.Add(id, snapshot.Title, snapshot.Author, snapshot.Duration, snapshot.Position);
        }
        else if (YouTubeIds.TryParse(video, out id, out _))
        {
            outcome = library.Add(id);
        }
        else
        {
            return YouTubeText.NotAVideo(video);
        }

        return library.Find(id) is { } saved ? YouTubeText.Saved(saved, outcome) : "Error: " + YouTubeText.SaveFailed;
    }

    /// <summary>
    /// <paramref name="video"/> taken off the list: <see cref="Current"/> the window's video, else its number, id or link
    /// (<see cref="YouTubeLibrary.Resolve"/>). Shared with <c>/youtube unsave</c>.
    /// </summary>
    public static string Remove(YouTubeLibrary library, IVideoPlayer? player, string video)
    {
        ArgumentNullException.ThrowIfNull(library);
        var saved = video.Equals(Current, StringComparison.OrdinalIgnoreCase)
            ? player?.Snapshot is { VideoId: { } playing } ? library.Find(playing) : null
            : library.Resolve(video);
        if (saved is null)
        {
            return YouTubeText.NotSaved(video);
        }

        return library.Remove(saved.Id) ? YouTubeText.Unsaved(saved) : "Error: " + YouTubeText.SaveFailed;
    }
}
