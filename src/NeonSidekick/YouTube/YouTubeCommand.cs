namespace NeonSidekick.YouTube;

/// <summary>A <c>/youtube</c> verb.</summary>
public enum YouTubeVerb
{
    /// <summary>The bare word, or <c>status</c>: what the window plays.</summary>
    Status,

    /// <summary>Words to search for: the picker of what YouTube found.</summary>
    Search,

    /// <summary><c>play &lt;id|link&gt; [&lt;time&gt;]</c>.</summary>
    Play,

    /// <summary><c>play</c> alone, or <c>resume</c>.</summary>
    Resume,
    Pause,
    Seek,
    Volume,
    Mute,
    Unmute,
    Close,

    /// <summary><c>save</c> (the video in the window) or <c>save &lt;id|link&gt;</c> (2026-10-07).</summary>
    Save,

    /// <summary>
    /// <c>list</c>: the saved videos' pane (2026-10-07). The word was <c>saved</c> until 2026-10-08 (the user's call), which is now
    /// words to search for like any other.
    /// </summary>
    Saved,

    /// <summary><c>unsave &lt;n|id|link&gt;</c>: a saved video taken off the list (2026-10-07).</summary>
    Unsave,

    /// <summary><c>list --clear</c>: every saved video taken off the list, after a yes (2026-10-08, the user's ask).</summary>
    ClearSaved,

    /// <summary>A verb without the argument it needs (the error is the usage).</summary>
    Unknown,
}

/// <summary>A <c>/youtube</c> line read: the verb, the search's words or the video, the time or the volume, and the error for a line that is no command.</summary>
public sealed record YouTubeCommandLine(YouTubeVerb Verb, string Text = "", string? VideoId = null, double? Number = null, string? Error = null);

/// <summary>
/// <c>/youtube</c>'s words (2026-10-05, the YouTube plan), pure: <see cref="Parse"/> the line, <see cref="Words"/> for the input
/// line's argument list. A word is a verb only when what follows fits it, so a search can start with one: <c>/youtube close
/// encounters</c> searches, <c>/youtube close</c> closes; <c>/youtube search &lt;words&gt;</c> searches whatever the words.
/// </summary>
public static class YouTubeCommand
{
    public const string Word = "/youtube";

    /// <summary>The switch after <c>list</c> that empties the list (2026-10-08, the user's word).</summary>
    public const string ClearSwitch = "--clear";

    /// <summary>The completion's note for <c>list --clear</c>.</summary>
    public const string ClearNote = "take every saved video off the list, after a yes";

    /// <summary>The words in the order the completion offers them, with their notes; save and list first (2026-10-08, the user's ask).</summary>
    public static readonly IReadOnlyList<(string Word, string Note)> Words =
    [
        ("save", "save the video playing, or /youtube save <id|link>; it resumes where it is left"),
        ("list", "the saved videos: Enter plays one where it was left; /youtube list --clear empties it"),
        ("play", "play a video: /youtube play <id|link> [<time>]; alone, carry on playing"),
        ("pause", "pause the video"),
        ("resume", "carry on playing"),
        ("seek", "go to a time: /youtube seek <time>"),
        ("volume", "set the volume: /youtube volume <0-100>"),
        ("mute", "mute the video"),
        ("unmute", "unmute the video"),
        ("close", "close the video window"),
        ("status", "what the video window plays"),
        ("unsave", "take a saved video off the list: /youtube unsave <n|id|link>"),
        ("search", "search for words that start with one of these: /youtube search <words>"),
    ];

    public static YouTubeCommandLine Parse(string? args)
    {
        string text = (args ?? "").Trim();
        if (text.Length == 0)
        {
            return new YouTubeCommandLine(YouTubeVerb.Status);
        }

        int space = text.IndexOf(' ', StringComparison.Ordinal);
        string word = (space < 0 ? text : text[..space]).ToLowerInvariant();
        string rest = space < 0 ? "" : text[(space + 1)..].Trim();
        switch (word)
        {
            case "search":
                return rest.Length == 0 ? Usage() : new YouTubeCommandLine(YouTubeVerb.Search, rest);
            case "play" when rest.Length == 0:
            case "resume" when rest.Length == 0:
                return new YouTubeCommandLine(YouTubeVerb.Resume);
            case "play":
            {
                int gap = rest.LastIndexOf(' ');
                if (YouTubeIds.TryParse(rest, out string id, out double start))
                {
                    return new YouTubeCommandLine(YouTubeVerb.Play, rest, id, start > 0 ? start : null);
                }

                if (gap > 0 && YouTubeIds.TryParse(rest[..gap], out id, out _) && YouTubeIds.ParseTime(rest[(gap + 1)..]) is { } at)
                {
                    return new YouTubeCommandLine(YouTubeVerb.Play, rest, id, at);
                }

                break;
            }

            case "seek" when rest.Length == 0:
            case "volume" when rest.Length == 0:
                return Usage();
            case "seek" when YouTubeIds.ParseTime(rest) is { } seconds:
                return new YouTubeCommandLine(YouTubeVerb.Seek, rest, Number: seconds);
            case "volume" when double.TryParse(rest.TrimEnd('%'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double level):
                return level is >= 0 and <= 100 ? new YouTubeCommandLine(YouTubeVerb.Volume, rest, Number: level) : Usage();
            case "pause" when rest.Length == 0:
                return new YouTubeCommandLine(YouTubeVerb.Pause);
            case "mute" when rest.Length == 0:
                return new YouTubeCommandLine(YouTubeVerb.Mute);
            case "unmute" when rest.Length == 0:
                return new YouTubeCommandLine(YouTubeVerb.Unmute);
            case "close" when rest.Length == 0:
                return new YouTubeCommandLine(YouTubeVerb.Close);
            case "status" when rest.Length == 0:
                return new YouTubeCommandLine(YouTubeVerb.Status);
            case "save" when rest.Length == 0:
                return new YouTubeCommandLine(YouTubeVerb.Save);
            case "save" when YouTubeIds.TryParse(rest, out string saving, out _):
                return new YouTubeCommandLine(YouTubeVerb.Save, rest, saving);
            case "list" when rest.Length == 0:
                return new YouTubeCommandLine(YouTubeVerb.Saved);
            case "list" when rest.Equals(ClearSwitch, StringComparison.OrdinalIgnoreCase):
                return new YouTubeCommandLine(YouTubeVerb.ClearSaved);
            case "unsave" when rest.Length == 0:
                return Usage();
            case "unsave" when rest.All(char.IsAsciiDigit) || YouTubeIds.TryParse(rest, out _, out _):
                return new YouTubeCommandLine(YouTubeVerb.Unsave, rest);
        }

        // A link alone plays; anything else is words to search for.
        return YouTubeIds.TryParse(text, out string linked, out double from) && text.Contains('/', StringComparison.Ordinal)
            ? new YouTubeCommandLine(YouTubeVerb.Play, text, linked, from > 0 ? from : null)
            : new YouTubeCommandLine(YouTubeVerb.Search, text);
    }

    /// <summary>
    /// The input line's argument list after <c>/youtube </c>: the verbs while the first word is typed; past it, nothing (a search's
    /// words are free) but <see cref="ClearSwitch"/> after <c>list</c> (2026-10-08).
    /// </summary>
    public static IReadOnlyList<UI.CompletionItem> Complete(string argText)
    {
        ArgumentNullException.ThrowIfNull(argText);
        if (argText.StartsWith("list ", StringComparison.OrdinalIgnoreCase))
        {
            return UI.MentionCompleter.Matches([new("list " + ClearSwitch, ClearNote)], argText);
        }

        return argText.Contains(' ', StringComparison.Ordinal) ? [] : UI.MentionCompleter.Matches(Words.Select(w => new UI.CompletionItem(w.Word, w.Note)).ToList(), argText);
    }

    private static YouTubeCommandLine Usage() => new(YouTubeVerb.Unknown, Error: YouTubeText.CommandUsage);
}
