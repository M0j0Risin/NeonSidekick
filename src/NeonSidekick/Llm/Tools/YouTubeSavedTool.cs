using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.YouTube;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>youtube_saved()</c> (2026-10-07, the user's ask): the profile's saved videos and playlists (<see cref="YouTubeLibrary"/>; playlists
/// since 2026-11-08), numbered, each with where it was left and its id for youtube_play (<see cref="YouTubeText.SavedList"/>). Reads the
/// list only; headless too. A video or playlist still untitled is looked up first (2026-10-07, <see cref="YouTubeTitles.FillMissingAsync"/>).
/// </summary>
public sealed class YouTubeSavedTool : AIFunction
{
    public const string ToolName = "youtube_saved";

    private static readonly JsonElement Schema = ToolSchema.Parse("{ \"type\": \"object\", \"properties\": {} }");

    private readonly Func<YouTubeLibrary> _library;
    private readonly YouTubeLookup? _lookup;
    private readonly YouTubePlaylistLookup? _playlistLookup;

    /// <param name="lookup">A video's title by its id, for one still untitled; null lists them as they are.</param>
    /// <param name="playlistLookup">A playlist's title by its id (2026-11-08), the same way.</param>
    public YouTubeSavedTool(Func<YouTubeLibrary> library, YouTubeLookup? lookup = null, YouTubePlaylistLookup? playlistLookup = null)
    {
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _lookup = lookup;
        _playlistLookup = playlistLookup;
    }

    public override string Name => ToolName;

    /// <summary>Pinned.</summary>
    public override string Description =>
        "Lists the user's saved YouTube videos and playlists (their bookmarks), numbered, each with its channel, where it was left or whether it was watched, and its id. " +
        YouTubePlayTool.ToolName + " with a saved video's id resumes it where it was left, and with a saved playlist's id as playlist at its entry and place.";

    public override JsonElement JsonSchema => Schema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var library = _library();
        await YouTubeTitles.FillMissingAsync(library, _lookup, cancellationToken, _playlistLookup).ConfigureAwait(false);
        return YouTubeText.SavedList(library.List());
    }
}
