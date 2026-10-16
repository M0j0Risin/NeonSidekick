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
/// </summary>
public sealed class YouTubeSaveTool : AIFunction
{
    public const string ToolName = "youtube_save";
    public const string ActionArgument = "action";
    public const string VideoArgument = "video";

    /// <summary>The word for the video in the window.</summary>
    public const string Current = "current";

    private static readonly JsonElement Schema = Build("the one in the video window");

    // The schema per YouTube open links in (2026-10-16): only current's words change, the window's byte for byte.
    private static readonly JsonElement WebSchema = Build("the one in the Web UI's YouTube tab");

    private static readonly JsonElement BrowserSchema = Build("the one in the app's own video players (never one opened in a browser)");

    private static JsonElement Build(string current) => ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "action": { "type": "string", "enum": ["add", "remove"], "description": "add saves the video; remove takes it off the saved list." },
            "video": { "type": "string", "description": "The video's 11-character id or a YouTube link; \"current\" for CURRENT; for remove, also its number in youtube_saved's list." }
          },
          "required": ["action", "video"]
        }
        """.Replace("CURRENT", current, StringComparison.Ordinal));

    private readonly Func<YouTubeLibrary> _library;
    private readonly IVideoPlayer? _player;
    private readonly YouTubeLookup? _lookup;
    private readonly Func<YouTubeOpenLinksInKind>? _target;

    /// <param name="player">The video window, for <c>current</c>; null headless.</param>
    /// <param name="lookup">A video's title by its id, for one saved by id or link; null saves it untitled.</param>
    /// <param name="target">Where a play goes now, for <c>current</c>'s words in the schema (2026-10-16); null is the video window.</param>
    public YouTubeSaveTool(Func<YouTubeLibrary> library, IVideoPlayer? player, YouTubeLookup? lookup = null, Func<YouTubeOpenLinksInKind>? target = null)
    {
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _player = player;
        _lookup = lookup;
        _target = target;
    }

    public override string Name => ToolName;

    /// <summary>Pinned.</summary>
    public override string Description =>
        "Adds a YouTube video to the user's saved videos (their bookmarks), or removes one. A saved video resumes where it was left the next time it plays. " +
        "Save only what the user asked to keep.";

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
        return action switch
        {
            "add" => await AddAsync(_library(), _player, video, _lookup, cancellationToken).ConfigureAwait(false),
            "remove" => Remove(_library(), _player, video),
            _ => YouTubeText.NotASaveAction(action),
        };
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
