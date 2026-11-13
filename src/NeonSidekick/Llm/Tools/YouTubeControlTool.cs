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
/// and <c>entry</c> (its value the entry, 1-based) move through it, waiting for the player to show another entry. Since 2026-10-16
/// (<c>YouTube open links in</c>) the player may be the Web UI's Video tab: a video waiting for the tab answers its held words to every
/// action but <c>close</c>, which drops it; a video sent to a browser answers that it can't be controlled.
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
    private readonly Func<VideoHandoff?>? _handoff;
    private readonly Func<YouTubeOpenLinksInKind>? _target;
    private readonly Func<string?>? _videoPointer;

    /// <param name="handoff">The video last sent to a browser (the router's <see cref="RoutedVideoPlayer.LastHandoff"/>); null for none.</param>
    /// <param name="target">Where a play goes now, for the description; null is the video window.</param>
    /// <param name="videoPointer"><c>video_control</c>'s name when it is offered this turn (2026-11-12), for the file-open refusal; null names none.</param>
    public YouTubeControlTool(IVideoPlayer player, TimeProvider? time = null, Func<VideoHandoff?>? handoff = null, Func<YouTubeOpenLinksInKind>? target = null, Func<string?>? videoPointer = null)
    {
        _videoPointer = videoPointer;
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _time = time ?? TimeProvider.System;
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
            "Controls the video playing in the Web UI's Video tab: play, pause, seek to a time, forward or back some seconds, volume 0–100, mute, unmute, " +
            "next, previous or entry in a playlist, or close the player. " + Use,
        YouTubeOpenLinksInKind.BrowserWindow or YouTubeOpenLinksInKind.DefaultBrowser =>
            "Controls a video in the app's own video players: play, pause, seek to a time, forward or back some seconds, volume 0–100, mute, unmute, " +
            "next, previous or entry in a playlist, or close the player. A video opened in a browser can't be controlled from here. " + Use,
        _ =>
            "Controls the video playing in the video window: play, pause, seek to a time, forward or back some seconds, volume 0–100, mute, unmute, " +
            "next, previous or entry in a playlist, or close the window. " + Use,
    };

    private const string Use = "Use it when the user asks; it answers with where the video then is.";

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
            return _handoff?.Invoke() is { } handoff ? YouTubeText.HandedOffControl(handoff) : YouTubeText.NoVideo;
        }

        // A video file open (2026-11-12): video_control's, not this tool's.
        if (current.AsFile() is { } file)
        {
            return VideoText.FileIsOpen(file.FileName, _videoPointer?.Invoke());
        }

        if (action == "close")
        {
            _player.Close();
            return YouTubeText.ClosedIn(current.Place);
        }

        // A video waiting for a Video tab (2026-10-16): nothing to drive yet, so its held words, and nothing sent.
        if (current.Held is { } held)
        {
            return YouTubeText.Held(current, held);
        }

        VideoCommand command;
        Func<VideoSnapshot, bool> shows;
        switch (action)
        {
            case "next":
            case "previous":
            case "entry":
                if (current.PlaylistId is null)
                {
                    return YouTubeText.NoPlaylistIn(current.Place);
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
                if (TryCommand(action, value, current, ValueArgument, out command, out shows) is { } error)
                {
                    return error;
                }

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
        return snapshot is null ? YouTubeText.ClosedMeanwhileIn(current.Place) : YouTubeText.Status(snapshot);
    }

    /// <summary>
    /// The commands both control tools share (2026-11-13, the review's fix: one parser, one set of refusals): play, pause, seek, forward,
    /// back, volume, mute and unmute against <paramref name="current"/>, each with what the player's report shows once it took effect.
    /// Null when <paramref name="command"/> is made, else the refusal: a time or a volume that doesn't read, is out of range, or isn't a
    /// finite number ("NaN" parses as a double). <paramref name="valueArgument"/> names the tool's argument; <paramref name="userCommand"/>
    /// (<c>/video</c>) names the command instead, for the user's own line. Each tool keeps its kind guard and its other actions.
    /// </summary>
    public static string? TryCommand(string action, string value, VideoSnapshot current, string valueArgument, out VideoCommand command,
        out Func<VideoSnapshot, bool> shows, string? userCommand = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(value);
        command = VideoCommand.Play;
        shows = static _ => false;
        switch (action)
        {
            case "play":
                shows = static s => s.State is VideoState.Playing or VideoState.Ended;
                return null;
            case "pause":
                command = VideoCommand.Pause;
                shows = static s => s.State is VideoState.Paused or VideoState.Ended or VideoState.Cued;
                return null;
            case "seek":
            case "forward":
            case "back":
                double? seconds = value.Length == 0 ? (action == "seek" ? null : DefaultSkip) : YouTubeIds.ParseTime(value);
                if (seconds is not { } by || !double.IsFinite(by))
                {
                    return userCommand is null ? YouTubeText.NotATime(valueArgument, value) : YouTubeText.NotATimeForUser(userCommand + " " + action, value);
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
                return null;
            case "volume":
                if (!double.TryParse(value.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out double level) || !double.IsFinite(level) || level is < 0 or > 100)
                {
                    return userCommand is null ? YouTubeText.NotAVolume(value) : YouTubeText.NotAVolumeForUser(userCommand + " volume", value);
                }

                command = VideoCommand.Volume(level);
                int volume = (int)command.Value;
                shows = s => s.Volume == volume;
                return null;
            case "mute":
                command = VideoCommand.Mute;
                shows = static s => s.Muted;
                return null;
            case "unmute":
                command = VideoCommand.Unmute;
                shows = static s => !s.Muted;
                return null;
            default:
                throw new ArgumentOutOfRangeException(nameof(action), action, "not a shared control action");
        }
    }

    // An entry moved to shows once it plays, waits cued or has ended (a short one may be over before the report). Not paused
    // (2026-10-09, seen on the real player): a move's first report is Paused, the new entry's number beside the old video's id,
    // before it buffers and plays; nextVideo and playVideoAt always play, so a pause is never where a move ends.
    private static bool Moved(VideoSnapshot s) => s.State is VideoState.Playing or VideoState.Cued or VideoState.Ended;
}
