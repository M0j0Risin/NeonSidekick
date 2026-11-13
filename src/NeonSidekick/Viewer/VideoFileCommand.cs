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

/// <summary>
/// A parsed <c>/video</c> line: the verb, the path to play, the text a seek or volume takes (as typed: <c>video_control</c>'s parser
/// judges it, 2026-11-13), or the usage error.
/// </summary>
public sealed record VideoFileLine(VideoFileVerb Verb, string? Path = null, string? Value = null, string? Error = null);

/// <summary>
/// <c>/video</c>'s words (2026-11-12, the video-files plan; <c>YouTube.YouTubeCommand</c>'s shape, and not <see cref="VideoCommand"/>, which is
/// the page's command type): <c>/video &lt;path&gt;</c> plays a file, <c>/video play|resume|pause|seek &lt;time&gt;|volume &lt;n&gt;|mute|unmute|status|close</c>
/// control it; alone, its status. An argument whose whole text (trimmed, inner blanks kept as <c>/view</c> keeps them) ends in a video
/// extension is a path whatever its first word, so <c>/video pause clip.mp4</c> plays "pause clip.mp4"; a verb's value never ends in one,
/// so the two never meet. Since 2026-11-13 (the review's fixes): a quoted argument is always a path (<c>/video "pause"</c> plays a file
/// named pause), text that isn't a verb but looks like a file (an extension, a <c>/</c> or <c>\</c>) is a play, so the tool says what is
/// wrong with it, and a seek's or volume's text is passed on as typed. Its words live in <see cref="VideoText"/>. Pure.
/// </summary>
public static class VideoFileCommand
{
    public const string Word = "/video";

    /// <summary>The verbs in the order the completion offers them, with their notes.</summary>
    public static IReadOnlyList<(string Word, string Note)> Words => VideoText.CommandWords;

    /// <summary><paramref name="args"/> as a line: a path, a verb and its value, or the usage error.</summary>
    public static VideoFileLine Parse(string? args)
    {
        string text = (args ?? "").Trim();
        if (text.Length == 0)
        {
            return new VideoFileLine(VideoFileVerb.Status);
        }

        if (text.Length >= 2 && text[0] is '"' or '\'' && text[^1] == text[0])
        {
            string unquoted = App.SettingsMenu.UnquotePath(text);
            return unquoted.Length == 0 ? new VideoFileLine(VideoFileVerb.Unknown, Error: VideoText.CommandUsage) : new VideoFileLine(VideoFileVerb.Play, unquoted);
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
            "seek" when rest.Length > 0 => new VideoFileLine(VideoFileVerb.Seek, Value: rest),
            "volume" when rest.Length > 0 => new VideoFileLine(VideoFileVerb.Volume, Value: rest),
            _ when LooksLikeFile(text) => new VideoFileLine(VideoFileVerb.Play, text),
            _ => new VideoFileLine(VideoFileVerb.Unknown, Error: VideoText.CommandUsage),
        };
    }

    // Text that names a file rather than a mistyped verb: a folder in it, or an extension (movie.avi), so the play says what is wrong.
    private static bool LooksLikeFile(string text) =>
        text.Contains('/', StringComparison.Ordinal) || text.Contains('\\', StringComparison.Ordinal) || Path.HasExtension(text);

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
    /// <c>volume</c> and their value), so the completion lists the verbs (with the files a one-word prefix also starts, 2026-11-13)
    /// rather than the video files alone. Anything else lists the files, so <c>pause cl</c> still finds <c>pause clip.mp4</c>.
    /// </summary>
    public static bool TypingVerb(string argText)
    {
        ArgumentNullException.ThrowIfNull(argText);
        int space = argText.IndexOf(' ', StringComparison.Ordinal);
        return space < 0
            ? argText.Length == 0 || VerbsStarting(argText).Count > 0
            : argText[..space].ToLowerInvariant() is "seek" or "volume";
    }

    /// <summary>The verbs <paramref name="prefix"/> starts, in <see cref="Words"/>' order.</summary>
    public static IReadOnlyList<string> VerbsStarting(string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        return [.. Words.Where(w => w.Word.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Select(w => w.Word)];
    }

    /// <summary>The verbs while one word is typed (a verb and its value are free text after); see <see cref="TypingVerb"/>.</summary>
    public static IReadOnlyList<UI.CompletionItem> Complete(string argText)
    {
        ArgumentNullException.ThrowIfNull(argText);
        return argText.Contains(' ', StringComparison.Ordinal) ? [] : UI.MentionCompleter.Matches(Words.Select(w => new UI.CompletionItem(w.Word, w.Note)).ToList(), argText);
    }
}
