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

    /// <summary>
    /// The Web UI's page could not make a player (2026-10-16: YouTube's script never came, a blocker): a failed snapshot with the app's
    /// sentence (<see cref="WebUi.WebUiText.PlayerFailed"/>), never the page's own reason raw.
    /// </summary>
    Failed,

    /// <summary>The Web UI's page started a video by itself (its Resume tap, 2026-10-16): the snapshot is left as it is.</summary>
    Resumed,
}

/// <summary>
/// The video page's protocol (<c>assets\youtube\player.html</c>'s header) on the app's side: the JSON the window posts for a
/// <see cref="VideoRequest"/> or a <see cref="VideoCommand"/>, and the page's messages folded into a <see cref="VideoSnapshot"/>.
/// Read by hand over <see cref="JsonDocument"/> (the <c>DockerJson</c> way: no serializer context for a dozen fields), written
/// with <see cref="Utf8JsonWriter"/>, whose numbers are invariant. Pure; the window applies it on its thread.
/// </summary>
public static class VideoMessages
{
    /// <summary>
    /// <c>{"cmd":"load","id":…,"start":…,"autoplay":…}</c>; a playlist (2026-10-08) adds <c>"list":…,"index":…</c>, its <c>id</c> the
    /// video to start at when one is named (left out otherwise).
    /// </summary>
    public static string Load(VideoRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.IsFile)
        {
            throw new ArgumentException("A video file loads through LoadFile, with its sink's src.", nameof(request));
        }

        return Write(w =>
        {
            w.WriteString("cmd", "load");
            if (request.VideoId is { } id)
            {
                w.WriteString("id", id);
            }

            w.WriteNumber("start", request.Start);
            w.WriteBoolean("autoplay", request.Autoplay);
            if (request.PlaylistId is { } list)
            {
                w.WriteString("list", list);
                w.WriteNumber("index", request.Entry);
            }
        });
    }

    /// <summary>
    /// A video file's load (2026-11-12, the video-files plan, <c>assets\youtube\file.html</c>'s header):
    /// <c>{"cmd":"load","file":token,"src":…,"name":…,"start":…,"autoplay":…}</c>. The sink supplies <paramref name="src"/>: the
    /// window the file's own URI (<see cref="VideoPage.FileSrc"/>), the Web UI its endpoint's address.
    /// </summary>
    public static string LoadFile(VideoRequest request, string src)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(src);
        if (!request.IsFile)
        {
            throw new ArgumentException("A YouTube video loads through Load.", nameof(request));
        }

        return Write(w =>
        {
            w.WriteString("cmd", "load");
            w.WriteString("file", request.FileToken);
            w.WriteString("src", src);
            w.WriteString("name", request.FileName);
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
                case VideoCommandKind.PlayAt:
                    w.WriteString("cmd", "at");
                    w.WriteNumber("i", (int)command.Value);
                    break;
                default:
                    w.WriteString("cmd", command.Kind switch
                    {
                        VideoCommandKind.Play => "play",
                        VideoCommandKind.Pause => "pause",
                        VideoCommandKind.Stop => "stop",
                        VideoCommandKind.Mute => "mute",
                        VideoCommandKind.Unmute => "unmute",
                        VideoCommandKind.Next => "next",
                        VideoCommandKind.Previous => "previous",
                        _ => throw new ArgumentOutOfRangeException(nameof(command), command.Kind, null),
                    });
                    break;
            }
        });
    }

    /// <summary>
    /// A page message read against <paramref name="current"/>: a state report becomes the new snapshot (YouTube's error kept
    /// while it is the same video, dropped for another), an error is set on the current one, and anything else leaves it as
    /// it is. Each change counts up <see cref="VideoSnapshot.Version"/>. Never throws on what the page sends. A playlist's
    /// report (2026-10-08) carries <c>list</c>, <c>index</c> and <c>count</c>, and an error the page moves past <c>skip</c>. The Web UI's page (2026-10-16)
    /// adds <c>failed</c>, <c>resumed</c> and a state report's <c>gesture</c> (<see cref="VideoSnapshot.NeedsTap"/>); the PC's page sends none of them.
    ///
    /// <para>A video file (2026-11-12): its <c>state</c> and <c>error</c> carry <c>file</c>, the play's token, matched instead of the
    /// id; the file's name and token are carried from <paramref name="current"/> into the rebuilt snapshot, and its error kept while
    /// the token is the same. A report of the other kind is dropped (<see cref="VideoPageEvent.Other"/>, the snapshot unchanged): while
    /// a file is current, a <c>state</c> or <c>error</c> without its token; while YouTube is, one carrying a <c>file</c>. So a late
    /// message from the page the window just left, or from the tab's hidden YouTube player, never overwrites the other kind's snapshot.
    /// A file's <c>code</c> is <c>MediaError</c>'s 1-4, which overlap YouTube's, so a reader words it by the snapshot's kind; the
    /// no-picture check sends <see cref="VideoFile.NoPictureCode"/>. A <c>src</c> in a report is never read.</para>
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
                case "resumed":
                    return (VideoPageEvent.Resumed, current);
                case "failed":
                    return (VideoPageEvent.Failed, current with
                    {
                        State = VideoState.Failed,
                        Failure = WebUi.WebUiText.PlayerFailed(Text(root, "reason")),
                        Version = current.Version + 1,
                    });
                case "error":
                    if (!SameKind(current, root))
                    {
                        return (VideoPageEvent.Other, current);
                    }

                    return (VideoPageEvent.Error, current with
                    {
                        VideoId = current.IsFile ? null : Text(root, "id") ?? current.VideoId,
                        Error = Number(root, "code") is { } code ? (int)code : -1,
                        Version = current.Version + 1,
                        Skipping = !current.IsFile && root.TryGetProperty("skip", out var skip) && skip.ValueKind == JsonValueKind.True,
                    });
                case "state" when current.IsFile:
                    if (!SameKind(current, root))
                    {
                        return (VideoPageEvent.Other, current);
                    }

                    // The token matched, so it is the same file: its name, token and error carry over.
                    return (VideoPageEvent.State, new VideoSnapshot(
                        StateOf(Number(root, "state")),
                        null,
                        Text(root, "title") ?? current.FileName,
                        Position: Seconds(Number(root, "position")),
                        Duration: Seconds(Number(root, "duration")),
                        Volume: Number(root, "volume") is { } fileVolume ? (int)Math.Clamp(Math.Round(fileVolume), 0, 100) : current.Volume,
                        Muted: root.TryGetProperty("muted", out var fileMuted) && fileMuted.ValueKind == JsonValueKind.True,
                        Error: current.Error,
                        Version: current.Version + 1)
                    {
                        FileName = current.FileName,
                        FileToken = current.FileToken,
                        NeedsTap = root.TryGetProperty("gesture", out var fileGesture) && fileGesture.ValueKind == JsonValueKind.True,
                    });
                case "state":
                    if (!SameKind(current, root))
                    {
                        return (VideoPageEvent.Other, current);
                    }

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
                        Version: current.Version + 1,
                        PlaylistId: Text(root, "list"),
                        PlaylistEntry: Number(root, "index") is { } index and >= 0 ? (int)index : null,
                        PlaylistCount: Number(root, "count") is { } count and > 0 ? (int)count : null,
                        Skipping: id == current.VideoId && current.Skipping)
                    {
                        NeedsTap = root.TryGetProperty("gesture", out var gesture) && gesture.ValueKind == JsonValueKind.True,
                    });
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

    /// <summary>
    /// A <c>page</c> message's <c>load</c>, the Web UI's script nonce (later on 2026-10-16, Fix 3: the player tells the script a video was
    /// just given to from a reload); null for another message, one without it (the first script) or one that doesn't parse. Never throws.
    /// </summary>
    public static string? PageLoad(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object && Text(root, "ev") == "page" ? Text(root, "load") : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// A <c>page</c> message's <c>kind</c> (2026-11-12): <c>file</c> from the window's file page, null from the YouTube page (which names
    /// none), another message, or one that doesn't parse. The window takes a <c>page</c> only from the kind it navigated to. Never throws.
    /// </summary>
    public static string? PageKind(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object && Text(root, "ev") == "page" ? Text(root, "kind") : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The file page's <c>kind</c> (<see cref="PageKind"/>).</summary>
    public const string FileKind = "file";

    // A report of the snapshot's own kind: a file's carrying its token, YouTube's carrying no file.
    private static bool SameKind(VideoSnapshot current, JsonElement root)
    {
        string? file = Text(root, "file");
        return current.FileToken is { } token ? string.Equals(file, token, StringComparison.Ordinal) : file is null;
    }

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
