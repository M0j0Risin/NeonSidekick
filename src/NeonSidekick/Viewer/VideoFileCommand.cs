using System.Globalization;

namespace NeonSidekick.Viewer;

/// <summary>What <c>/video</c> was asked to do (2026-11-12, the video-files plan).</summary>
public enum VideoFileVerb
{
    /// <summary>Words it doesn't know: <see cref="VideoFileLine.Error"/> says so.</summary>
    Unknown,

    /// <summary>A video file to play: <see cref="VideoFileLine.Path"/>.</summary>
    Play,
    Resume,
    Pause,
    Seek,
    Volume,
    Mute,
    Unmute,
    Status,
    Close,
}

/// <summary>A parsed <c>/video</c> line: the verb, the path to play, the number a seek or volume takes, or the usage error.</summary>
public sealed record VideoFileLine(VideoFileVerb Verb, string? Path = null, double? Number = null, string? Error = null);

/// <summary>
/// <c>/video</c>'s words (2026-11-12, the video-files plan; <c>YouTube.YouTubeCommand</c>'s shape, and not <see cref="VideoCommand"/>, which is
/// the page's command type): <c>/video &lt;path&gt;</c> plays a file, <c>/video play|resume|pause|seek &lt;time&gt;|volume &lt;n&gt;|mute|unmute|status|close</c>
/// control it; alone, its status. An argument whose whole text (trimmed, inner blanks kept as <c>/view</c> keeps them; no quoting) ends in a
/// video extension is a path whatever its first word, so <c>/video pause clip.mp4</c> plays "pause clip.mp4"; a verb's value never ends in
/// one, so the two never meet. Times parse as <c>/youtube seek</c>'s do (<see cref="YouTube.YouTubeIds.ParseTime"/>). Pure.
/// </summary>
public static class VideoFileCommand
{
    public const string Word = "/video";

    /// <summary>The usage, the answer to words it doesn't know. Pinned.</summary>
    public const string Usage = "Usage: /video <path> plays a video file; /video play|pause|seek <time>|volume <0-100>|mute|unmute|status|close controls it.";

    /// <summary>The verbs in the order the completion offers them, with their notes.</summary>
    public static readonly IReadOnlyList<(string Word, string Note)> Words =
    [
        ("status", "what the video file plays, and where"),
        ("pause", "pause the video file"),
        ("play", "carry on playing"),
        ("seek", "go to a time: /video seek <time>"),
        ("volume", "set the volume: /video volume <0-100>"),
        ("mute", "mute the video file"),
        ("unmute", "unmute the video file"),
        ("close", "close the video file's player"),
    ];

    /// <summary><paramref name="args"/> as a line: a path, a verb and its value, or the usage error.</summary>
    public static VideoFileLine Parse(string? args)
    {
        string text = (args ?? "").Trim();
        if (text.Length == 0)
        {
            return new VideoFileLine(VideoFileVerb.Status);
        }

        if (VideoFile.IsVideoPath(text))
        {
            return new VideoFileLine(VideoFileVerb.Play, text);
        }

        int space = text.IndexOf(' ', StringComparison.Ordinal);
        string word = (space < 0 ? text : text[..space]).ToLowerInvariant();
        string rest = space < 0 ? "" : text[(space + 1)..].Trim();
        return word switch
        {
            "play" or "resume" when rest.Length == 0 => new VideoFileLine(VideoFileVerb.Resume),
            "pause" when rest.Length == 0 => new VideoFileLine(VideoFileVerb.Pause),
            "mute" when rest.Length == 0 => new VideoFileLine(VideoFileVerb.Mute),
            "unmute" when rest.Length == 0 => new VideoFileLine(VideoFileVerb.Unmute),
            "status" when rest.Length == 0 => new VideoFileLine(VideoFileVerb.Status),
            "close" when rest.Length == 0 => new VideoFileLine(VideoFileVerb.Close),
            "seek" when YouTube.YouTubeIds.ParseTime(rest) is { } seconds => new VideoFileLine(VideoFileVerb.Seek, Number: seconds),
            "volume" when double.TryParse(rest.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out double level) && level is >= 0 and <= 100
                => new VideoFileLine(VideoFileVerb.Volume, Number: level),
            _ => new VideoFileLine(VideoFileVerb.Unknown, Error: Usage),
        };
    }

    /// <summary>The control tool's action for <paramref name="verb"/> (<c>video_control</c>'s words); null for a play or an unknown verb.</summary>
    public static string? ActionOf(VideoFileVerb verb) => verb switch
    {
        VideoFileVerb.Resume => "play",
        VideoFileVerb.Pause => "pause",
        VideoFileVerb.Seek => "seek",
        VideoFileVerb.Volume => "volume",
        VideoFileVerb.Mute => "mute",
        VideoFileVerb.Unmute => "unmute",
        VideoFileVerb.Status => "status",
        VideoFileVerb.Close => "close",
        _ => null,
    };

    /// <summary>
    /// Whether <paramref name="argText"/> reads as a verb being typed (nothing yet, one word a verb starts with, or <c>seek</c> or
    /// <c>volume</c> and their value), so the completion lists the verbs rather than the video files. Anything else lists the files,
    /// so <c>pause cl</c> still finds <c>pause clip.mp4</c>.
    /// </summary>
    public static bool TypingVerb(string argText)
    {
        ArgumentNullException.ThrowIfNull(argText);
        int space = argText.IndexOf(' ', StringComparison.Ordinal);
        return space < 0
            ? argText.Length == 0 || Words.Any(w => w.Word.StartsWith(argText, StringComparison.OrdinalIgnoreCase))
            : argText[..space].ToLowerInvariant() is "seek" or "volume";
    }

    /// <summary>The verbs while one word is typed (a verb and its value are free text after); see <see cref="TypingVerb"/>.</summary>
    public static IReadOnlyList<UI.CompletionItem> Complete(string argText)
    {
        ArgumentNullException.ThrowIfNull(argText);
        return argText.Contains(' ', StringComparison.Ordinal) ? [] : UI.MentionCompleter.Matches(Words.Select(w => new UI.CompletionItem(w.Word, w.Note)).ToList(), argText);
    }
}
