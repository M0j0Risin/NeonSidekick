using NeonSidekick.Viewer;

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

    /// <summary><c>next</c>: the playlist's next entry (2026-10-08).</summary>
    Next,

    /// <summary><c>previous</c> (or <c>prev</c>): the playlist's entry before.</summary>
    Previous,

    /// <summary><c>entry &lt;n&gt;</c>: the playlist's entry n, 1-based.</summary>
    Entry,

    /// <summary><c>playlist [&lt;id|link&gt;]</c>: a playlist's entries on the picker (needs the key); alone, the one playing.</summary>
    Playlist,

    /// <summary>A verb without the argument it needs (the error is the usage).</summary>
    Unknown,
}

/// <summary>
/// A <c>/youtube</c> line read: the verb, the search's words or the video, the time or the volume, and the error for a line that is no
/// command. A playlist (2026-10-08) adds its id and the entry to start at (0-based; null for the link's own place, or the video's), and a
/// search its switches (<see cref="Choices"/>, checked when it runs).
/// </summary>
public sealed record YouTubeCommandLine(YouTubeVerb Verb, string Text = "", string? VideoId = null, double? Number = null, string? Error = null, string? PlaylistId = null, int? Entry = null, YouTubeSearchChoices? Choices = null);

/// <summary>
/// <c>/youtube</c>'s words (2026-10-05, the YouTube plan), pure: <see cref="Parse"/> the line, <see cref="Words"/> for the input
/// line's argument list. A word is a verb only when what follows fits it, so a search can start with one: <c>/youtube close
/// encounters</c> searches, <c>/youtube close</c> closes; <c>/youtube search &lt;words&gt;</c> searches whatever the words.
/// Since 2026-10-08 (the user's ask) a playlist plays (<c>play &lt;playlist|link&gt; [&lt;entry&gt;]</c>, a playlist link alone),
/// <c>next</c>, <c>previous</c> and <c>entry &lt;n&gt;</c> move through it, <c>playlist</c> lists one, and a search takes switches
/// before its words (<see cref="Switches"/>: <c>/youtube search --playlists --order date lofi</c>, or a line that starts with one).
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
        ("next", "the playlist's next video"),
        ("previous", "the playlist's video before"),
        ("entry", "play the playlist's video n: /youtube entry <n>"),
        ("playlist", "a playlist's videos: /youtube playlist <id|link>; alone, the one playing"),
        ("search", "search, with switches: /youtube search [--playlists|--channels] [--order date] … <words>"),
    ];

    /// <summary>
    /// The search's switches (2026-10-08), with their notes and the words a value takes (empty for a switch alone). Pinned against
    /// <see cref="YouTubeText.SearchSwitchesNote"/>.
    /// </summary>
    public static readonly IReadOnlyList<(string Switch, string Note, IReadOnlyList<string> Values)> Switches =
    [
        ("--playlists", "find playlists", []),
        ("--channels", "find channels", []),
        ("--order", "the order: relevance, date, views, rating, title", YouTubeSearchFilters.Orders),
        ("--length", "videos short (under 4 min), medium or long (over 20)", YouTubeSearchFilters.Lengths),
        ("--since", "published since today, week, month, year or a date", YouTubeSearchFilters.Recent.Select(r => r.Word).ToList()),
        ("--region", "a two-letter country code (US, GB)", []),
        ("--lang", "a language code to favour (en, es)", []),
        ("--live", "live broadcasts only", []),
        ("--captions", "videos with captions only", []),
        ("--safe", "safe search: none, moderate, strict", YouTubeSearchFilters.SafeSearches),
    ];

    private static readonly string[] ValueSwitches = ["--order", "--length", "--since", "--region", "--lang", "--safe"];

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
        if (word.StartsWith("--", StringComparison.Ordinal))
        {
            return Search(text);   // a line that starts with a switch is a search (2026-10-08)
        }

        switch (word)
        {
            case "search":
                return rest.Length == 0 ? Usage() : Search(rest);
            case "play" when rest.Length == 0:
            case "resume" when rest.Length == 0:
                return new YouTubeCommandLine(YouTubeVerb.Resume);
            case "next" when rest.Length == 0:
                return new YouTubeCommandLine(YouTubeVerb.Next);
            case "previous" when rest.Length == 0:
            case "prev" when rest.Length == 0:
                return new YouTubeCommandLine(YouTubeVerb.Previous);
            case "entry" when rest.Length == 0:
                return Usage();
            case "entry" when int.TryParse(rest, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int entry):
                return entry >= 1 ? new YouTubeCommandLine(YouTubeVerb.Entry, rest, Number: entry) : Usage();
            case "playlist" when rest.Length == 0:
                return new YouTubeCommandLine(YouTubeVerb.Playlist);
            case "playlist" when YouTubeIds.TryParsePlaylist(rest, out string listed, out _, out _, out _):
                return new YouTubeCommandLine(YouTubeVerb.Playlist, rest, PlaylistId: listed);
            case "play" when Playlist(rest) is { } playlist:
                return playlist;
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

        // A link alone plays (a playlist's too, 2026-10-08); anything else is words to search for.
        if (text.Contains('/', StringComparison.Ordinal) && !text.Contains(' ', StringComparison.Ordinal) && Playlist(text) is { } list)
        {
            return list;
        }

        return YouTubeIds.TryParse(text, out string linked, out double from) && text.Contains('/', StringComparison.Ordinal)
            ? new YouTubeCommandLine(YouTubeVerb.Play, text, linked, from > 0 ? from : null)
            : new YouTubeCommandLine(YouTubeVerb.Search, text);
    }

    /// <summary>
    /// <c>play</c>'s playlist (2026-10-08): a playlist id, link or channel, alone or with an entry number after it (1-based); null when
    /// <paramref name="rest"/> names no playlist (a plain video link, an 11-character id).
    /// </summary>
    private static YouTubeCommandLine? Playlist(string rest)
    {
        if (VideoRequest.IsVideoId(rest))
        {
            return null;
        }

        if (YouTubeIds.TryParsePlaylist(rest, out string list, out int entry, out string? video, out double start))
        {
            return new YouTubeCommandLine(YouTubeVerb.Play, rest, video, start > 0 ? start : null, PlaylistId: list, Entry: video is null ? entry : null);
        }

        int gap = rest.LastIndexOf(' ');
        return gap > 0 && int.TryParse(rest[(gap + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int number) && number >= 1
            && !VideoRequest.IsVideoId(rest[..gap]) && YouTubeIds.TryParsePlaylist(rest[..gap], out list, out _, out _, out _)
            ? new YouTubeCommandLine(YouTubeVerb.Play, rest, PlaylistId: list, Entry: number - 1)
            : null;
    }

    /// <summary>
    /// A search's switches, then its words (2026-10-08): each switch read into <see cref="YouTubeSearchChoices"/> (its value checked when
    /// the search runs, against the saved choices); a switch it does not know is the error; no words after them is the usage.
    /// </summary>
    private static YouTubeCommandLine Search(string text)
    {
        string[] tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var choices = YouTubeSearchChoices.None;
        int i = 0;
        for (; i < tokens.Length && tokens[i].StartsWith("--", StringComparison.Ordinal); i++)
        {
            string name = tokens[i].ToLowerInvariant();
            if (Array.IndexOf(ValueSwitches, name) >= 0)
            {
                if (i + 1 >= tokens.Length)
                {
                    return Usage();
                }

                string value = tokens[++i];
                choices = name switch
                {
                    "--order" => choices with { Order = value },
                    "--length" => choices with { Length = value },
                    "--since" => choices with { Since = value },
                    "--region" => choices with { Region = value },
                    "--lang" => choices with { Language = value },
                    _ => choices with { SafeSearch = value },
                };
                continue;
            }

            YouTubeSearchChoices? known = name switch
            {
                "--playlists" => choices with { Kind = "playlist" },
                "--channels" => choices with { Kind = "channel" },
                "--live" => choices with { Live = true },
                "--captions" => choices with { Captions = true },
                _ => null,
            };
            if (known is null)
            {
                return new YouTubeCommandLine(YouTubeVerb.Unknown, Error: YouTubeText.UnknownSwitch(tokens[i]));
            }

            choices = known;
        }

        string words = string.Join(' ', tokens.Skip(i));
        return words.Length == 0 ? Usage() : new YouTubeCommandLine(YouTubeVerb.Search, words, Choices: choices == YouTubeSearchChoices.None ? null : choices);
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

        // A search's switches (2026-10-08): while the word being typed starts one, and a value switch's words after it.
        if (argText.StartsWith("search ", StringComparison.OrdinalIgnoreCase) || argText.StartsWith("--", StringComparison.Ordinal))
        {
            int gap = argText.LastIndexOf(' ');
            string head = gap < 0 ? "" : argText[..(gap + 1)];
            string current = argText[(gap + 1)..];
            string before = head.TrimEnd().Split(' ').LastOrDefault() ?? "";
            if (Switches.FirstOrDefault(s => s.Switch.Equals(before, StringComparison.OrdinalIgnoreCase)) is { Values.Count: > 0 } valued)
            {
                return UI.MentionCompleter.Matches(valued.Values.Select(v => new UI.CompletionItem(head + v, valued.Note)).ToList(), argText);
            }

            return current.StartsWith('-')
                ? UI.MentionCompleter.Matches(Switches.Select(s => new UI.CompletionItem(head + s.Switch, s.Note)).ToList(), argText)
                : [];
        }

        return argText.Contains(' ', StringComparison.Ordinal) ? [] : UI.MentionCompleter.Matches(Words.Select(w => new UI.CompletionItem(w.Word, w.Note)).ToList(), argText);
    }

    private static YouTubeCommandLine Usage() => new(YouTubeVerb.Unknown, Error: YouTubeText.CommandUsage);
}
