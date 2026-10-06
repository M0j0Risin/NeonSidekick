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

    /// <summary>The words in the order the completion offers them, with their notes.</summary>
    public static readonly IReadOnlyList<(string Word, string Note)> Words =
    [
        ("play", "play a video: /youtube play <id|link> [<time>]; alone, carry on playing"),
        ("pause", "pause the video"),
        ("resume", "carry on playing"),
        ("seek", "go to a time: /youtube seek <time>"),
        ("volume", "set the volume: /youtube volume <0-100>"),
        ("mute", "mute the video"),
        ("unmute", "unmute the video"),
        ("close", "close the video window"),
        ("status", "what the video window plays"),
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
        }

        // A link alone plays; anything else is words to search for.
        return YouTubeIds.TryParse(text, out string linked, out double from) && text.Contains('/', StringComparison.Ordinal)
            ? new YouTubeCommandLine(YouTubeVerb.Play, text, linked, from > 0 ? from : null)
            : new YouTubeCommandLine(YouTubeVerb.Search, text);
    }

    /// <summary>The input line's argument list after <c>/youtube </c>: the verbs while the first word is typed; past it, nothing (a search's words are free).</summary>
    public static IReadOnlyList<UI.CompletionItem> Complete(string argText)
    {
        ArgumentNullException.ThrowIfNull(argText);
        return argText.Contains(' ', StringComparison.Ordinal) ? [] : UI.MentionCompleter.Matches(Words.Select(w => new UI.CompletionItem(w.Word, w.Note)).ToList(), argText);
    }

    private static YouTubeCommandLine Usage() => new(YouTubeVerb.Unknown, Error: YouTubeText.CommandUsage);
}
