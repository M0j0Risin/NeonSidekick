using System.Text.RegularExpressions;
using NeonSidekick.Files;
using NeonSidekick.Hotkeys;

namespace NeonSidekick.UI;

/// <summary>
/// Clickable links and paths in the transcript (2026-11-01, plan memoized-dancing-horizon item #2, feature idea #10): the one rule for
/// what may be a link, the bare-URL finder, the <c>neon-path:</c> carrier and the hover hint's texts. A web link (<c>http</c>,
/// <c>https</c>, <c>mailto</c>) rides a segment's <see cref="Spectre.Console.Rendering.Segment.Link"/> and may reach the terminal as OSC 8; a path
/// rides it as <see cref="PathScheme"/> and never does (<see cref="LinkFilter"/>), so a model-named file is never one click from running.
/// </summary>
public static partial class LinkText
{
    /// <summary>A path's carrier in a segment's link (2026-11-01): only the app's resolver makes one, and the pane's filter keeps it from the terminal.</summary>
    public const string PathScheme = "neon-path:";

    /// <summary>The longest web link given an OSC 8 sequence (plan review 13): past it, plain text. <see cref="SafeText"/>'s, the one rule.</summary>
    public const int MaxWebLinkLength = SafeText.MaxWebLinkLength;

    /// <summary>
    /// The chord the hover hint names (2026-11-01): Ctrl+click on Windows, Option+click or Shift+click on a Mac by terminal
    /// (<see cref="ChordFor"/>), set once by <c>Program</c> at startup as <c>MicrophoneText.Terminal</c> is.
    /// </summary>
    public static string Chord { get; set; } = ChordFor(OperatingSystem.IsMacOS(), null);

    /// <summary>
    /// The link chord the terminal lets through to the app (plan review D4/D42): Ctrl+click on Windows; on a Mac Shift+click in iTerm2
    /// (it keeps Option+click for itself) and Option+click in any other terminal (WezTerm and Ghostty keep Shift+click). Pure.
    /// </summary>
    public static string ChordFor(bool mac, string? termProgram) =>
        !mac ? "Ctrl+click"
        : string.Equals(termProgram, MacHotkeyRules.Iterm, StringComparison.Ordinal) ? "Shift+click"
        : "Option+click";

    /// <summary>Whether <paramref name="target"/> is a web link: <c>http</c>, <c>https</c> or <c>mailto</c>, the only schemes a terminal may open.</summary>
    public static bool IsWeb(string? target) => target is not null && SafeText.HasWebScheme(target);

    /// <summary>Whether <paramref name="target"/> is a path the app's resolver made (<see cref="PathScheme"/>).</summary>
    public static bool IsPath(string? target) => target is not null && target.StartsWith(PathScheme, StringComparison.Ordinal) && target.Length > PathScheme.Length;

    /// <summary>The full path a <see cref="PathScheme"/> link carries, or null for any other.</summary>
    public static string? PathOf(string? target) => IsPath(target) ? target![PathScheme.Length..] : null;

    /// <summary>A path's link (<see cref="PathScheme"/> and the full path, as it is: a space needs no escape, the link is a style, never markup).</summary>
    public static string PathLink(string fullPath) => PathScheme + fullPath;

    /// <summary>
    /// The one rule for a link's target (plan review 13): no C0 or C1 control character or DEL anywhere (it would go inside an OSC 8
    /// sequence), and for a web link no space and at most <see cref="MaxWebLinkLength"/> characters, and no bidi or zero-width character
    /// (2026-11-02, review P1-9: a U+202E or a U+200B made the address shown differ from the one opened). Anything else gets no link at
    /// all. A character scan, cheap enough for every link of every layout; the parse is <see cref="SafeText.WebLink"/>'s, where a link
    /// is opened, hovered or written out.
    /// </summary>
    public static bool IsLinkable(string? target, bool web)
    {
        if (string.IsNullOrEmpty(target))
        {
            return false;
        }

        if (web)
        {
            return SafeText.IsSafeWebChars(target);
        }

        foreach (char c in target)
        {
            if (c < 0x20 || (c >= 0x7F && c <= 0x9F))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// A link's target as a reply's link gives it, or null for one that gets none: a web link as written; a bare address holding an
    /// <c>@</c> (an email autolink) as <c>mailto:</c>; any other scheme (<c>file:</c>, <c>javascript:</c>, a reply's own <c>neon-path:</c>) none.
    /// </summary>
    public static string? WebTarget(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        string target = IsWeb(url) ? url
            : url.Contains('@', StringComparison.Ordinal) && !url.Contains(':', StringComparison.Ordinal) && !url.Contains('/', StringComparison.Ordinal) ? "mailto:" + url
            : "";
        return IsLinkable(target, web: true) ? target : null;
    }

    /// <summary>
    /// The bare web addresses in <paramref name="text"/>, start and length (2026-11-01): <c>http://</c> or <c>https://</c> to the next space
    /// or bracket, trailing <c>.,;:!?)]'"</c> cut, but a closing parenthesis kept while it balances one in the address
    /// (<c>https://en.wikipedia.org/wiki/Foo_(bar)</c>). Pure.
    /// </summary>
    public static IReadOnlyList<(int Start, int Length)> BareUrls(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var found = new List<(int, int)>();
        foreach (Match match in BareUrl().Matches(text))
        {
            string url = match.Value;
            while (url.Length > 0)
            {
                char last = url[^1];
                if (".,;:!?]'\"".Contains(last, StringComparison.Ordinal))
                {
                    url = url[..^1];
                }
                else if (last == ')' && url.Count(c => c == '(') < url.Count(c => c == ')'))
                {
                    url = url[..^1];
                }
                else
                {
                    break;
                }
            }

            int separator = url.IndexOf("://", StringComparison.Ordinal);
            if (separator >= 0 && separator + 3 < url.Length && IsLinkable(url, web: true))
            {
                found.Add((match.Index, url.Length));
            }
        }

        return found;
    }

    /// <summary>
    /// An inline code span read as a path (2026-11-01): the text with a trailing <c>:line</c> or <c>:line:col</c> cut (never a drive's
    /// <c>C:</c>, which no digits follow), trimmed; empty when nothing is left.
    /// </summary>
    public static string PathCandidate(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        return LineSuffix().Replace(code.Trim(), "");
    }

    /// <summary>
    /// The hover hint for a link's <paramref name="target"/> (2026-11-01): <c>Ctrl+click opens https://…</c> for a web link, cut to
    /// <paramref name="width"/> cells; for a path, <paramref name="shownPath"/> (the path relative to the working folder) shown in
    /// <c>/browse</c>, or the full path "outside the working folder" when <paramref name="outside"/>. Null for anything else. A web link
    /// shows <see cref="SafeWebLink.Hint"/>, the host first in its ASCII form and a userinfo as <c>…@</c>, and a path is scrubbed
    /// (<see cref="SafeText.ShownPath"/>) (2026-11-02, review P12-3/P2-3: a long userinfo cut the real host off the end, an IDN host showed
    /// unconverted, and a bidi character in a path drew it reversed).
    /// </summary>
    public static string? HoverHint(string target, string? shownPath, bool outside, int width)
    {
        ArgumentNullException.ThrowIfNull(target);
        string? hint = IsWeb(target) ? SafeText.WebLink(target) is { } link ? $"{Chord} opens {link.Hint}" : null
            : PathOf(target) is { } full ? outside ? $"{SafeText.ShownPath(full)} is outside the working folder" : $"{Chord} shows {SafeText.ShownPath(shownPath ?? full)} in /browse"
            : null;
        return hint is null ? null : Cut(hint, Math.Max(1, width));
    }

    /// <summary>The widest a hover hint is drawn (one less than a 120-column window, the hints' budget).</summary>
    public const int HintWidth = 119;

    /// <summary><paramref name="text"/> cut to <paramref name="width"/> cells with an ellipsis (<see cref="TextCells.Cut"/>, 2026-11-02, review P2-9).</summary>
    private static string Cut(string text, int width) => TextCells.Cut(text, width);

    [GeneratedRegex(@"https?://[^\s<>""`\[\]{}|\\^]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BareUrl();

    [GeneratedRegex(@"(?<=\S):\d+(?::\d+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex LineSuffix();
}
