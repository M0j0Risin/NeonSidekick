using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Viewer;
using NeonSidekick.YouTube;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>youtube_status()</c> (2026-10-05, the YouTube plan): what the video window last reported — the video, its state, where it is
/// and how long it runs, the volume — or that none is open. It reads the window's snapshot and never waits on the window. Since
/// 2026-10-16 (<c>YouTube open links in</c>) the player may be the Web UI's YouTube tab, and a video sent to a browser is named as such
/// (<see cref="YouTubeText.StatusOrHandoff"/>), since nothing can be read from one.
/// </summary>
public sealed class YouTubeStatusTool : AIFunction
{
    public const string ToolName = "youtube_status";

    private static readonly JsonElement Schema = ToolSchema.Parse("{ \"type\": \"object\", \"properties\": {} }");

    private readonly IVideoPlayer _player;
    private readonly Func<VideoHandoff?>? _handoff;
    private readonly Func<YouTubeOpenLinksInKind>? _target;

    /// <param name="handoff">The video last sent to a browser (the router's <see cref="RoutedVideoPlayer.LastHandoff"/>); null for none.</param>
    /// <param name="target">Where a play goes now, for the description; null is the video window.</param>
    public YouTubeStatusTool(IVideoPlayer player, Func<VideoHandoff?>? handoff = null, Func<YouTubeOpenLinksInKind>? target = null)
    {
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _handoff = handoff;
        _target = target;
    }

    public override string Name => ToolName;

    /// <summary>Follows <c>YouTube open links in</c> (2026-10-16); the window's is the text before it, byte for byte. Pinned.</summary>
    public override string Description => DescriptionFor(_target?.Invoke() ?? YouTubeOpenLinksInKind.YouTubeWindow);

    /// <summary>The description where a play goes to <paramref name="target"/>.</summary>
    public static string DescriptionFor(YouTubeOpenLinksInKind target) => target switch
    {
        YouTubeOpenLinksInKind.WebUiWindow =>
            "Says what the Web UI's YouTube tab is playing: the video and channel, playing or paused, the position and length, the volume, or that it waits for the tab to open. " + Use,
        YouTubeOpenLinksInKind.BrowserWindow or YouTubeOpenLinksInKind.DefaultBrowser =>
            "Says what the app's own video players are playing; a video opened in a browser can't be seen, so it only says where that one went. " + Use,
        _ => "Says what the video window is playing: the video and channel, playing or paused, the position and length, the volume. " + Use,
    };

    private const string Use = "Use it before answering a question about the video, or to see whether a video started.";

    public override JsonElement JsonSchema => Schema;

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) =>
        ValueTask.FromResult<object?>(YouTubeText.StatusOrHandoff(_player.Snapshot, _handoff?.Invoke()));
}
