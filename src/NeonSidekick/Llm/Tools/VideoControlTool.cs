using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Viewer;
using NeonSidekick.YouTube;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>video_control(action, value?)</c> (2026-11-12, the video-files plan): the video file playing played, paused, sought (to a time, or
/// forward and back by seconds, <see cref="YouTubeControlTool.DefaultSkip"/> by default, held to the length), its volume set (0–100), muted
/// or unmuted, the player closed, or its status said (files get no status tool of their own). <see cref="YouTubeControlTool"/>'s body less
/// the playlist actions: it waits for the player to show the change (<see cref="YouTubeWait.Command"/>) and answers with what it then
/// reports (<see cref="VideoText.FileStatus"/>). A file only: with a YouTube video open it points at <c>youtube_control</c> when that is
/// offered; after a file went to its default app it says so (<see cref="VideoText.HandedOffControl"/>); a file waiting for a Video tab
/// answers its held words to everything but <c>close</c> and <c>status</c>. The commands and their refusals are youtube_control's own
/// (<see cref="YouTubeControlTool.TryCommand"/>, 2026-11-13); a file on a page with a fixed volume (an iPhone) answers a volume at once.
/// The screen's instance for <c>/video</c> words its answers for the user (<c>forUser</c>: <c>/video status</c>, <c>/video &lt;path&gt;</c>).
/// Never headless.
/// </summary>
public sealed class VideoControlTool : AIFunction
{
    public const string ToolName = "video_control";
    public const string ActionArgument = "action";
    public const string ValueArgument = "value";

    /// <summary>The actions, as the schema lists them.</summary>
    public static readonly IReadOnlyList<string> Actions = ["play", "pause", "seek", "forward", "back", "volume", "mute", "unmute", "close", "status"];

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "action": { "type": "string", "enum": ["play", "pause", "seek", "forward", "back", "volume", "mute", "unmute", "close", "status"], "description": "What to do to the video file playing; status says where it is." },
            "value": { "type": "string", "description": "seek: where, as seconds (90) or a time (1:30). forward and back: how many seconds (10 if left out). volume: 0 to 100." }
          },
          "required": ["action"]
        }
        """);

    private readonly IVideoPlayer? _player;
    private readonly TimeProvider _time;
    private readonly Func<string?>? _handoffName;
    private readonly Func<VideoFilesPlayInKind>? _target;
    private readonly Func<string?>? _youTubePointer;
    private readonly bool _forUser;

    /// <param name="player">The player (the router); null with no video window, every file then in its default app.</param>
    /// <param name="handoffName">The video file last sent to its default app; null for none.</param>
    /// <param name="target">Where a play goes now, for the description; null is the video window.</param>
    /// <param name="youTubePointer"><c>youtube_control</c>'s name when it is offered this turn, for the YouTube-open refusal; null names none.</param>
    /// <param name="forUser"><c>/video</c>'s instance: its answers name <c>/video</c>'s forms, not the tools'.</param>
    public VideoControlTool(IVideoPlayer? player, TimeProvider? time = null, Func<string?>? handoffName = null, Func<VideoFilesPlayInKind>? target = null,
        Func<string?>? youTubePointer = null, bool forUser = false)
    {
        _player = player;
        _time = time ?? TimeProvider.System;
        _handoffName = handoffName;
        _target = target;
        _youTubePointer = youTubePointer;
        _forUser = forUser;
    }

    public override string Name => ToolName;

    /// <summary>Follows <c>Video files play in</c>. Pinned.</summary>
    public override string Description => DescriptionFor(_target?.Invoke() ?? VideoFilesPlayInKind.VideoWindow);

    /// <summary>The description where a play goes to <paramref name="target"/>.</summary>
    public static string DescriptionFor(VideoFilesPlayInKind target) => target switch
    {
        VideoFilesPlayInKind.WebUiWindow =>
            "Controls the video file " + PlayVideoTool.ToolName + " started in the Web UI's Video tab (not YouTube videos): " + Verbs + " or close the player. " + Use,
        VideoFilesPlayInKind.DefaultApp =>
            "Controls a video file in the app's own players (not YouTube videos): " + Verbs + " or close the player. A file opened in its own app can't be controlled from here. " + Use,
        _ =>
            "Controls the video file " + PlayVideoTool.ToolName + " started in the video window (not YouTube videos): " + Verbs + " or close the window. " + Use,
    };

    private const string Verbs = "play, pause, seek to a time, forward or back some seconds, volume 0–100, mute, unmute, status (where it is),";

    private const string Use = "Use it when the user asks; it answers with where the video then is.";

    public override JsonElement JsonSchema => Schema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        string action = ToolArguments.ReadString(arguments, ActionArgument).Trim().ToLowerInvariant();
        string value = ToolArguments.ReadString(arguments, ValueArgument).Trim();
        return await ControlAsync(action, value, cancellationToken).ConfigureAwait(false);
    }

    /// <summary><paramref name="action"/> with <paramref name="value"/>: the model's call and <c>/video</c>'s verbs, one set of words.</summary>
    public async Task<string> ControlAsync(string action, string value, CancellationToken cancellationToken)
    {
        if (!Actions.Contains(action))
        {
            return YouTubeText.NotAnAction(action, Actions);
        }

        var now = _player?.Snapshot;
        if (now.AsYouTube() is not null)
        {
            return VideoText.YouTubeIsOpen(_youTubePointer?.Invoke());
        }

        if (now.AsFile() is not { } current || _player is null)
        {
            string? handoff = _handoffName?.Invoke();
            if (action == "status")
            {
                return handoff is not null && now is null ? VideoText.HandedOffStatus(handoff) : VideoText.FileStatus(now, _forUser);
            }

            return handoff is not null ? VideoText.HandedOffControl(handoff) : VideoText.NoFileFor(_forUser);
        }

        if (action == "status")
        {
            return VideoText.FileStatus(current, _forUser);
        }

        if (action == "close")
        {
            _player.Close();
            return YouTubeText.ClosedIn(current.Place);
        }

        // A file waiting for a Video tab: nothing to drive yet, so its held words, and nothing sent.
        if (current.Held is { } held)
        {
            return YouTubeText.Held(current, held);
        }

        // A page that can't set the volume (an iPhone) would never show the change: said at once, not after the command's wait.
        if (action == "volume" && current.FixedVolume)
        {
            return VideoText.FixedVolume;
        }

        string? token = current.FileToken;
        if (YouTubeControlTool.TryCommand(action, value, current, ValueArgument, out var command, out var shows, _forUser ? "/video" : null) is { } error)
        {
            return error;
        }

        long before = current.Version;
        if (!_player.Send(command))
        {
            return VideoText.NoFileFor(_forUser);
        }

        var snapshot = await YouTubeWait.UntilAsync(_player, before, s => s.FileToken == token && shows(s), YouTubeWait.Command, _time, cancellationToken, video: token).ConfigureAwait(false);
        return snapshot is null ? YouTubeText.ClosedMeanwhileIn(current.Place) : YouTubeText.Status(snapshot, _forUser);
    }
}
