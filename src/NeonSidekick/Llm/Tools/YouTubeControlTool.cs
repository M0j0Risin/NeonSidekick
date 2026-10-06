using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Viewer;
using NeonSidekick.YouTube;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>youtube_control(action, value?)</c> (2026-10-05, the YouTube plan): the open video played, paused, sought (to a time, or
/// forward and back by seconds, 10 by default), its volume set (0–100), muted or unmuted, or the window closed. It waits for the
/// player to show the change (<see cref="YouTubeWait.Command"/>) and answers with what the window then reports. Nothing without
/// a window: <see cref="YouTubeText.NoVideo"/>. Never headless.
/// </summary>
public sealed class YouTubeControlTool : AIFunction
{
    public const string ToolName = "youtube_control";
    public const string ActionArgument = "action";
    public const string ValueArgument = "value";

    /// <summary>The seconds forward or back without a value.</summary>
    public const double DefaultSkip = 10;

    /// <summary>The actions, as the schema lists them.</summary>
    public static readonly IReadOnlyList<string> Actions = ["play", "pause", "seek", "forward", "back", "volume", "mute", "unmute", "close"];

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "action": { "type": "string", "enum": ["play", "pause", "seek", "forward", "back", "volume", "mute", "unmute", "close"], "description": "What to do to the open video." },
            "value": { "type": "string", "description": "seek: where, as seconds (90) or a time (1:30). forward and back: how many seconds (10 if left out). volume: 0 to 100." }
          },
          "required": ["action"]
        }
        """);

    private readonly IVideoPlayer _player;
    private readonly TimeProvider _time;

    public YouTubeControlTool(IVideoPlayer player, TimeProvider? time = null)
    {
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _time = time ?? TimeProvider.System;
    }

    public override string Name => ToolName;

    /// <summary>Pinned.</summary>
    public override string Description =>
        "Controls the video playing in the video window: play, pause, seek to a time, forward or back some seconds, volume 0–100, mute, unmute, or close the window. " +
        "Use it when the user asks; it answers with where the video then is.";

    public override JsonElement JsonSchema => Schema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        string action = ToolArguments.ReadString(arguments, ActionArgument).Trim().ToLowerInvariant();
        string value = ToolArguments.ReadString(arguments, ValueArgument).Trim();
        if (!Actions.Contains(action))
        {
            return YouTubeText.NotAnAction(action, Actions);
        }

        if (_player.Snapshot is not { State: not VideoState.Failed } current)
        {
            return YouTubeText.NoVideo;
        }

        if (action == "close")
        {
            _player.Close();
            return YouTubeText.Closed;
        }

        VideoCommand command;
        Func<VideoSnapshot, bool> shows;
        switch (action)
        {
            case "play":
                command = VideoCommand.Play;
                shows = s => s.State is VideoState.Playing or VideoState.Ended;
                break;
            case "pause":
                command = VideoCommand.Pause;
                shows = s => s.State is VideoState.Paused or VideoState.Ended or VideoState.Cued;
                break;
            case "seek":
            case "forward":
            case "back":
                double? seconds = value.Length == 0 ? (action == "seek" ? null : DefaultSkip) : YouTubeIds.ParseTime(value);
                if (seconds is not { } by)
                {
                    return YouTubeText.NotATime(ValueArgument, value);
                }

                double to = action switch
                {
                    "forward" => current.Position + by,
                    "back" => current.Position - by,
                    _ => by,
                };
                if (current.Duration > 0)
                {
                    to = Math.Min(to, current.Duration);
                }

                command = VideoCommand.Seek(to);
                double target = command.Value;
                shows = s => Math.Abs(s.Position - target) < 3;
                break;
            case "volume":
                if (!double.TryParse(value.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out double level) || level is < 0 or > 100)
                {
                    return YouTubeText.NotAVolume(value);
                }

                command = VideoCommand.Volume(level);
                int volume = (int)command.Value;
                shows = s => s.Volume == volume;
                break;
            case "mute":
                command = VideoCommand.Mute;
                shows = s => s.Muted;
                break;
            default:
                command = VideoCommand.Unmute;
                shows = s => !s.Muted;
                break;
        }

        long before = current.Version;
        if (!_player.Send(command))
        {
            return YouTubeText.NoVideo;
        }

        var snapshot = await YouTubeWait.UntilAsync(_player, before, shows, YouTubeWait.Command, _time, cancellationToken).ConfigureAwait(false);
        return snapshot is null ? YouTubeText.ClosedMeanwhile : YouTubeText.Status(snapshot);
    }
}
