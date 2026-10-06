using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Viewer;
using NeonSidekick.YouTube;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>youtube_status()</c> (2026-10-05, the YouTube plan): what the video window last reported — the video, its state, where it is
/// and how long it runs, the volume — or that none is open. It reads the window's snapshot and never waits on the window.
/// </summary>
public sealed class YouTubeStatusTool : AIFunction
{
    public const string ToolName = "youtube_status";

    private static readonly JsonElement Schema = ToolSchema.Parse("{ \"type\": \"object\", \"properties\": {} }");

    private readonly IVideoPlayer _player;

    public YouTubeStatusTool(IVideoPlayer player) => _player = player ?? throw new ArgumentNullException(nameof(player));

    public override string Name => ToolName;

    /// <summary>Pinned.</summary>
    public override string Description =>
        "Says what the video window is playing: the video and channel, playing or paused, the position and length, the volume. " +
        "Use it before answering a question about the video, or to see whether a video started.";

    public override JsonElement JsonSchema => Schema;

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) =>
        ValueTask.FromResult<object?>(YouTubeText.Status(_player.Snapshot));
}
