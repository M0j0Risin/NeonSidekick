using System.Globalization;
using System.Text;

namespace NeonSidekick.Files;

/// <summary>A web link that passed <see cref="SafeText.WebLink"/>: the parsed address, the target to open or write, and the hint to show.</summary>
/// <param name="Uri">The address as parsed.</param>
/// <param name="Target">What is opened or written: <see cref="Uri.AbsoluteUri"/> with the host in its ASCII (<c>xn--</c>) form.</param>
/// <param name="Hint">What a person is shown: the scheme and the ASCII host first, a userinfo flagged as <c>…@host</c>, then the rest.</param>
public sealed record SafeWebLink(Uri Uri, string Target, string Hint);

/// <summary>
/// The one vetting of what the app hands on — a link to open or write, a path to show, text for the clipboard (2026-11-02, review P1-9,
/// P2-3, P12-3, P12-4, P12-8): the http/https/mailto rule was written four ways (the transcript's prefix test, the Web UI's and Word's
/// parses, the PDF sanitizer's), already differing, and none refused a bidi or zero-width character, so what a person read wasn't what
/// opened or pasted. Built on <see cref="NameScrub"/>'s <see cref="NameScrub.Hides"/>. Two levels: <see cref="IsSafeWebText"/> is a
/// character scan for the hot path (every link of every layout), <see cref="WebLink"/> the full parse where a link is opened, hovered or
/// written out. Here rather than in <c>UI/</c>, since Office, the Web UI and the session export use it too. Pure.
/// </summary>
public static class SafeText
{
    /// <summary>The longest web link given a link at all (plan review 13): past it, plain text.</summary>
    public const int MaxWebLinkLength = 2000;

    /// <summary>
    /// The cheap level: an <c>http://</c>, <c>https://</c> or <c>mailto:</c> prefix (any case) and <see cref="IsSafeWebChars"/> — one loop,
    /// no parse.
    /// </summary>
    public static bool IsSafeWebText(string? text) => text is not null && HasWebScheme(text) && IsSafeWebChars(text);

    /// <summary>
    /// A web link's characters: none <see cref="NameScrub.Hides"/> (controls, bidi, zero-width), no space or C1 control, and at most
    /// <see cref="MaxWebLinkLength"/> of them. With <paramref name="spaces"/> a plain space is allowed (a written link escapes it; an OSC 8
    /// sequence can't hold one), never another kind.
    /// </summary>
    public static bool IsSafeWebChars(string text, bool spaces = false)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaxWebLinkLength)
        {
            return false;
        }

        foreach (char c in text)
        {
            if (NameScrub.Hides(c) || (char.IsWhiteSpace(c) && !(spaces && c == ' ')) || (c >= 0x7F && c <= 0x9F))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether <paramref name="text"/> starts with one of the three schemes a terminal or a page may open.</summary>
    public static bool HasWebScheme(string text) =>
        text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        || text.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The full level: <see cref="IsSafeWebText"/> on the trimmed text (a plain space allowed, escaped in the target), then an absolute address of the three schemes that parses, the
    /// target with the host swapped for its ASCII form (<see cref="Uri.IdnHost"/>, as <c>WordWriter</c> did: a Cyrillic <c>аpple.com</c>
    /// opens as the <c>xn--</c> name it is), and a hint that leads with the host before any cut (a long userinfo can't push the real host
    /// past the edge). Null for anything else.
    /// </summary>
    public static SafeWebLink? WebLink(string? text)
    {
        string? trimmed = text?.Trim();
        if (trimmed is null || !HasWebScheme(trimmed) || !IsSafeWebChars(trimmed, spaces: true) || !Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || !(uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeMailto))
        {
            return null;
        }

        string target = uri.AbsoluteUri;
        if (uri.Scheme == Uri.UriSchemeMailto)
        {
            return new SafeWebLink(uri, target, target);
        }

        string host = uri.IdnHost;
        if (!host.Equals(uri.Host, StringComparison.OrdinalIgnoreCase))
        {
            target = new UriBuilder(uri) { Host = host }.Uri.AbsoluteUri;
        }

        string port = uri.IsDefaultPort ? "" : ":" + uri.Port.ToString(CultureInfo.InvariantCulture);
        string hint = uri.Scheme + "://" + (uri.UserInfo.Length > 0 ? "…@" : "") + host + port + uri.PathAndQuery + uri.Fragment;
        return new SafeWebLink(uri, target, hint);
    }

    /// <summary>A path as the app shows it: <see cref="NameScrub.Display"/> (a bidi or zero-width character escaped, never drawn).</summary>
    public static string ShownPath(string? path) => NameScrub.Display(path);

    /// <summary>
    /// <paramref name="text"/> for the clipboard (2026-11-02, review P12-4): every control and format character dropped — zero-width,
    /// bidi, tag characters, a soft hyphen — but a tab and the line breaks, and a zero-width joiner only between two emoji (a family or a
    /// flame heart keeps its glyph, while one inside a host name goes). What is pasted is then what was read. <paramref name="dropped"/>
    /// counts what was left out.
    /// </summary>
    public static string ForClipboard(string text, out int dropped)
    {
        ArgumentNullException.ThrowIfNull(text);
        dropped = 0;
        var runes = new List<Rune>(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            runes.Add(rune);
        }

        var kept = new StringBuilder(text.Length);
        for (int i = 0; i < runes.Count; i++)
        {
            var rune = runes[i];
            if (Hidden(rune) && !(rune.Value == 0x200D && JoinsEmoji(runes, i)))
            {
                dropped++;
                continue;
            }

            kept.Append(rune.ToString());
        }

        return dropped == 0 ? text : kept.ToString();
    }

    private static bool Hidden(Rune rune)
    {
        if (rune.Value is '\t' or '\n' or '\r')
        {
            return false;
        }

        var category = Rune.GetUnicodeCategory(rune);
        return category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;
    }

    /// <summary>Whether the joiner at <paramref name="at"/> stands between two emoji (a selector or skin tone after the first allowed).</summary>
    private static bool JoinsEmoji(List<Rune> runes, int at)
    {
        int before = at - 1;
        while (before >= 0 && (runes[before].Value is 0xFE0F or 0xFE0E || runes[before].Value is >= 0x1F3FB and <= 0x1F3FF))
        {
            before--;
        }

        return before >= 0 && at + 1 < runes.Count && IsEmoji(runes[before]) && IsEmoji(runes[at + 1]);
    }

    /// <summary>A pictographic emoji base, roughly: the emoji planes, the dingbats and symbols blocks, the arrows and the misc technical.</summary>
    private static bool IsEmoji(Rune rune) =>
        rune.Value is (>= 0x1F000 and <= 0x1FAFF) or (>= 0x2600 and <= 0x27BF) or (>= 0x2300 and <= 0x23FF) or (>= 0x2B00 and <= 0x2BFF)
            or 0x00A9 or 0x00AE or 0x2122 or (>= 0x2190 and <= 0x21FF);
}
