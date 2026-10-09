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
/// a window: <see cref="YouTubeText.NoVideo"/>. Never headless. While a playlist plays (2026-10-08), <c>next</c>, <c>previous</c>
/// and <c>entry</c> (its value the entry, 1-based) move through it, waiting for the player to show another entry.
/// </summary>
public sealed class YouTubeControlTool : AIFunction
{
    public const string ToolName = "youtube_control";
    public const string ActionArgument = "action";
    public const string ValueArgument = "value";

    /// <summary>The seconds forward or back without a value.</summary>
    public const double DefaultSkip = 10;

    /// <summary>The actions, as the schema lists them.</summary>
    public static readonly IReadOnlyList<string> Actions = ["play", "pause", "seek", "forward", "back", "volume", "mute", "unmute", "next", "previous", "entry", "close"];

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "action": { "type": "string", "enum": ["play", "pause", "seek", "forward", "back", "volume", "mute", "unmute", "next", "previous", "entry", "close"], "description": "What to do to the open video. next, previous and entry move through a playlist." },
            "value": { "type": "string", "description": "seek: where, as seconds (90) or a time (1:30). forward and back: how many seconds (10 if left out). volume: 0 to 100. entry: the playlist entry to play, 1 for the first." }
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
        "Controls the video playing in the video window: play, pause, seek to a time, forward or back some seconds, volume 0–100, mute, unmute, " +
        "next, previous or entry in a playlist, or close the window. " +
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
            case "next":
            case "previous":
            case "entry":
                if (current.PlaylistId is null)
                {
                    return YouTubeText.NoPlaylist;
                }

                if (action == "entry")
                {
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int number) || number < 1 || (current.PlaylistCount is { } count && number > count))
                    {
                        return YouTubeText.NotAnEntry(value, current.PlaylistCount);
                    }

                    command = VideoCommand.PlayAt(number - 1);
                    shows = s => s.PlaylistEntry == number - 1 && Moved(s);
                    break;
                }

                command = action == "next" ? VideoCommand.Next : VideoCommand.Previous;
                int? was = current.PlaylistEntry;
                string? from = current.VideoId;
                shows = s => (s.PlaylistEntry != was || s.VideoId != from) && Moved(s);
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

        // Another entry loads before it plays: the wait for a playlist's move is a play's, not a command's (2026-10-08).
        var timeout = action is "next" or "previous" or "entry" ? YouTubeWait.Play : YouTubeWait.Command;
        var snapshot = await YouTubeWait.UntilAsync(_player, before, shows, timeout, _time, cancellationToken).ConfigureAwait(false);
        return snapshot is null ? YouTubeText.ClosedMeanwhile : YouTubeText.Status(snapshot);
    }

    // An entry moved to shows once it plays, waits cued or has ended (a short one may be over before the report). Not paused
    // (2026-10-09, seen on the real player): a move's first report is Paused, the new entry's number beside the old video's id,
    // before it buffers and plays; nextVideo and playVideoAt always play, so a pause is never where a move ends.
    private static bool Moved(VideoSnapshot s) => s.State is VideoState.Playing or VideoState.Cued or VideoState.Ended;
}
