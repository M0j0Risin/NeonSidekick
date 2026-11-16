using System.Globalization;
using System.Text;

namespace NeonSidekick.Files;

/// <summary>
/// A file name made of words that may hold anything (2026-10-29, the Office writing plan; <c>DirectoryCsv.NameFor</c>'s rule of 2026-10-15,
/// lifted so the database exports name their files the same way): lower-cased, a space and every character a file name can't hold on either
/// OS (Windows' set added to the running one's, so <c>server\instance</c> and <c>C:</c> come out alike on a Mac) as <c>-</c>, runs folded,
/// no <c>-</c> or <c>.</c> at the ends, cut to a length without splitting an emoji (2026-10-29, review P3-11). Since 2026-10-30 (review F87) an
/// invisible format character (U+202E, the right-to-left override that shows a name ending in <c>txt.exe</c> backwards, a zero-width space, a bidi mark) is a <c>-</c>
/// too, but for a zero-width joiner between two shown characters, which joins an emoji sequence (👨‍👩‍👧). Pure.
/// </summary>
public static class FileNames
{
    /// <summary>The longest a made-up name's words run.</summary>
    public const int MaxChars = 60;

    private static readonly HashSet<char> Invalid = [.. Path.GetInvalidFileNameChars(), '/', '\\', ':', '*', '?', '"', '<', '>', '|'];

    /// <summary>
    /// <paramref name="words"/> as a file-safe stem (no extension), at most <paramref name="max"/> characters; <paramref name="lowerCase"/>
    /// false keeps the words' case (a document's title, 2026-10-29, review P5-4).
    /// </summary>
    public static string Safe(string words, int max = MaxChars, bool lowerCase = true)
    {
        ArgumentNullException.ThrowIfNull(words);
        var name = new StringBuilder(words.Length);
        string trimmed = words.Trim();
        string source = lowerCase ? trimmed.ToLowerInvariant() : trimmed;
        for (int i = 0; i < source.Length; i++)
        {
            char c = source[i];
            name.Append(char.IsWhiteSpace(c) || Invalid.Contains(c) || char.IsControl(c) || (IsFormat(c) && !IsJoining(source, i)) ? '-' : c);
        }

        string text = name.ToString();
        while (text.Contains("--", StringComparison.Ordinal))
        {
            text = text.Replace("--", "-", StringComparison.Ordinal);
        }

        text = text.Trim('-', '.');
        return text.Length > max ? TextCut.Safe(text, max).TrimEnd('-', '.') : text;
    }

    private const char ZeroWidthJoiner = '\u200D';

    private static bool IsFormat(char c) => CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format;

    /// <summary>Whether the character at <paramref name="i"/> is a zero-width joiner with a shown character on each side.</summary>
    internal static bool IsJoining(string text, int i) =>
        text[i] == ZeroWidthJoiner && i > 0 && i < text.Length - 1 && !IsFormat(text[i - 1]) && !IsFormat(text[i + 1]) && !char.IsWhiteSpace(text[i - 1]) && !char.IsWhiteSpace(text[i + 1]);

    /// <summary>
    /// A file name a server chose (a <c>Content-Disposition</c> name, a URL's last segment), as <c>download_file</c> saves it (2026-11-16, the
    /// web-tools review's #60, #90, #91, #108, #136): every character that hides how the name reads — a format character (bidi, a zero-width
    /// one, a soft hyphen, a direction mark), a control, a line or paragraph separator — becomes <c>-</c>, so <c>invoice\u202Efdp.exe</c> can't
    /// show as <c>invoiceexe.pdf</c>. Kept: a zero-width joiner between two letters, marks or symbols (an emoji family, a skin tone, a variation
    /// selector beside it), and a zero-width non-joiner between two letters or marks (Persian, Indic names). Runs of <c>-</c> folded, <c>-</c> and
    /// <c>.</c> trimmed from the ends; <c>""</c> when nothing is left, and the caller falls back. The user's own names never come here. Pure.
    /// </summary>
    public static string ServerName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var kept = new StringBuilder(name.Length);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            bool hides = category is UnicodeCategory.Format or UnicodeCategory.Control or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;
            if (hides && !KeptJoiner(name, i))
            {
                kept.Append('-');
                continue;
            }

            kept.Append(c);
        }

        string text = kept.ToString();
        while (text.Contains("--", StringComparison.Ordinal))
        {
            text = text.Replace("--", "-", StringComparison.Ordinal);
        }

        return text.Trim().Trim('-', '.').Trim();
    }

    private const char ZeroWidthNonJoiner = '\u200C';

    // A joiner ServerName keeps: ZWJ between letters, marks or symbols; ZWNJ between letters or marks. Neighbors read as code points.
    private static bool KeptJoiner(string text, int i)
    {
        if (text[i] is not (ZeroWidthJoiner or ZeroWidthNonJoiner) || i == 0 || i == text.Length - 1)
        {
            return false;
        }

        var before = CategoryBefore(text, i);
        var after = CategoryAt(text, i + 1);
        static bool LetterOrMark(UnicodeCategory c) => c is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
            or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;
        static bool Joins(UnicodeCategory c) => LetterOrMark(c) || c is UnicodeCategory.OtherSymbol or UnicodeCategory.ModifierSymbol;
        return text[i] == ZeroWidthJoiner ? Joins(before) && Joins(after) : LetterOrMark(before) && LetterOrMark(after);
    }

    private static UnicodeCategory CategoryAt(string text, int i) =>
        char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])
            ? CharUnicodeInfo.GetUnicodeCategory(char.ConvertToUtf32(text[i], text[i + 1]))
            : CharUnicodeInfo.GetUnicodeCategory(text[i]);

    private static UnicodeCategory CategoryBefore(string text, int i) =>
        char.IsLowSurrogate(text[i - 1]) && i >= 2 && char.IsHighSurrogate(text[i - 2])
            ? CharUnicodeInfo.GetUnicodeCategory(char.ConvertToUtf32(text[i - 2], text[i - 1]))
            : CharUnicodeInfo.GetUnicodeCategory(text[i - 1]);

    /// <summary>
    /// The last part of <paramref name="path"/>, split on either separator (2026-10-30, review F39: a Windows path read on a Mac kept its folders,
    /// since <see cref="Path.GetFileName(string)"/> there splits on <c>/</c> alone): <c>C:\Users\me\a.png</c> and <c>/Users/me/a.png</c> both give
    /// <c>a.png</c>. Pure.
    /// </summary>
    public static string LastPart(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path[(path.LastIndexOfAny(['\\', '/']) + 1)..];
    }
}
