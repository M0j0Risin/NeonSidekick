using System.Buffers;
using System.Text;
using System.Text.Json;

namespace NeonSidekick.Viewer;

/// <summary>What a message from the video page was (<see cref="VideoMessages.Apply"/>).</summary>
public enum VideoPageEvent
{
    /// <summary>Not one of the page's (unparsable, or a kind it does not send).</summary>
    Other,

    /// <summary>The page's script runs: messages posted from now on reach it.</summary>
    Page,

    /// <summary>The player is made and ready.</summary>
    Ready,

    /// <summary>A state report: a change, after a command, or a second's tick while playing.</summary>
    State,

    /// <summary>YouTube's <c>onError</c> for the video.</summary>
    Error,

    /// <summary>The smoke's echo coming back.</summary>
    Echo,
}

/// <summary>
/// The video page's protocol (<c>assets\youtube\player.html</c>'s header) on the app's side: the JSON the window posts for a
/// <see cref="VideoRequest"/> or a <see cref="VideoCommand"/>, and the page's messages folded into a <see cref="VideoSnapshot"/>.
/// Read by hand over <see cref="JsonDocument"/> (the <c>DockerJson</c> way: no serializer context for a dozen fields), written
/// with <see cref="Utf8JsonWriter"/>, whose numbers are invariant. Pure; the window applies it on its thread.
/// </summary>
public static class VideoMessages
{
    /// <summary><c>{"cmd":"load","id":…,"start":…,"autoplay":…}</c>.</summary>
    public static string Load(VideoRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Write(w =>
        {
            w.WriteString("cmd", "load");
            w.WriteString("id", request.VideoId);
            w.WriteNumber("start", request.Start);
            w.WriteBoolean("autoplay", request.Autoplay);
        });
    }

    /// <summary>The command's message: <c>{"cmd":"play"}</c>, <c>{"cmd":"seek","t":…}</c>, <c>{"cmd":"volume","v":…}</c> and so on.</summary>
    public static string Command(VideoCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return Write(w =>
        {
            switch (command.Kind)
            {
                case VideoCommandKind.Seek:
                    w.WriteString("cmd", "seek");
                    w.WriteNumber("t", command.Value);
                    break;
                case VideoCommandKind.Volume:
                    w.WriteString("cmd", "volume");
                    w.WriteNumber("v", (int)command.Value);
                    break;
                default:
                    w.WriteString("cmd", command.Kind switch
                    {
                        VideoCommandKind.Play => "play",
                        VideoCommandKind.Pause => "pause",
                        VideoCommandKind.Stop => "stop",
                        VideoCommandKind.Mute => "mute",
                        VideoCommandKind.Unmute => "unmute",
                        _ => throw new ArgumentOutOfRangeException(nameof(command), command.Kind, null),
                    });
                    break;
            }
        });
    }

    /// <summary>
    /// A page message read against <paramref name="current"/>: a state report becomes the new snapshot (YouTube's error kept
    /// while it is the same video, dropped for another), an error is set on the current one, and anything else leaves it as
    /// it is. Each change counts up <see cref="VideoSnapshot.Version"/>. Never throws on what the page sends.
    /// </summary>
    public static (VideoPageEvent Event, VideoSnapshot Snapshot) Apply(VideoSnapshot current, string? message)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (string.IsNullOrWhiteSpace(message))
        {
            return (VideoPageEvent.Other, current);
        }

        try
        {
            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (VideoPageEvent.Other, current);
            }

            switch (Text(root, "ev"))
            {
                case "page":
                    return (VideoPageEvent.Page, current);
                case "ready":
                    return (VideoPageEvent.Ready, current);
                case "echo":
                    return (VideoPageEvent.Echo, current);
                case "error":
                    return (VideoPageEvent.Error, current with
                    {
                        VideoId = Text(root, "id") ?? current.VideoId,
                        Error = Number(root, "code") is { } code ? (int)code : -1,
                        Version = current.Version + 1,
                    });
                case "state":
                    string? id = Text(root, "id") ?? current.VideoId;
                    return (VideoPageEvent.State, new VideoSnapshot(
                        StateOf(Number(root, "state")),
                        id,
                        Text(root, "title"),
                        Text(root, "author"),
                        Position: Seconds(Number(root, "position")),
                        Duration: Seconds(Number(root, "duration")),
                        Volume: Number(root, "volume") is { } volume ? (int)Math.Clamp(Math.Round(volume), 0, 100) : current.Volume,
                        Muted: root.TryGetProperty("muted", out var muted) && muted.ValueKind == JsonValueKind.True,
                        Error: id == current.VideoId ? current.Error : null,
                        Version: current.Version + 1));
                default:
                    return (VideoPageEvent.Other, current);
            }
        }
        catch (JsonException)
        {
            return (VideoPageEvent.Other, current);
        }
    }

    /// <summary>YouTube's <c>YT.PlayerState</c> number as a <see cref="VideoState"/>; an unknown one (or none) is unstarted.</summary>
    public static VideoState StateOf(double? state) => state switch
    {
        0 => VideoState.Ended,
        1 => VideoState.Playing,
        2 => VideoState.Paused,
        3 => VideoState.Buffering,
        5 => VideoState.Cued,
        _ => VideoState.Unstarted,
    };

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static double? Number(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double d) ? d : null;

    private static double Seconds(double? value) => value is { } v && double.IsFinite(v) && v > 0 ? v : 0;

    private static string Write(Action<Utf8JsonWriter> body)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            body(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
