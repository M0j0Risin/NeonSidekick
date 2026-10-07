using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.YouTube;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>youtube_saved()</c> (2026-10-07, the user's ask): the profile's saved videos (<see cref="YouTubeLibrary"/>), numbered, each with
/// where it was left and its id for youtube_play (<see cref="YouTubeText.SavedList"/>). Reads the list only; headless too. A video
/// still untitled is looked up first (2026-10-07, <see cref="YouTubeTitles.FillMissingAsync"/>).
/// </summary>
public sealed class YouTubeSavedTool : AIFunction
{
    public const string ToolName = "youtube_saved";

    private static readonly JsonElement Schema = ToolSchema.Parse("{ \"type\": \"object\", \"properties\": {} }");

    private readonly Func<YouTubeLibrary> _library;
    private readonly YouTubeLookup? _lookup;

    /// <param name="lookup">A video's title by its id, for one still untitled; null lists them as they are.</param>
    public YouTubeSavedTool(Func<YouTubeLibrary> library, YouTubeLookup? lookup = null)
    {
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _lookup = lookup;
    }

    public override string Name => ToolName;

    /// <summary>Pinned.</summary>
    public override string Description =>
        "Lists the user's saved YouTube videos (their bookmarks), numbered, each with its channel, where it was left or whether it was watched, and its id. " +
        YouTubePlayTool.ToolName + " with a saved video's id resumes it where it was left.";

    public override JsonElement JsonSchema => Schema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var library = _library();
        await YouTubeTitles.FillMissingAsync(library, _lookup, cancellationToken).ConfigureAwait(false);
        return YouTubeText.SavedList(library.List());
    }
}
