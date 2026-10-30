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
    private static bool IsJoining(string text, int i) =>
        text[i] == ZeroWidthJoiner && i > 0 && i < text.Length - 1 && !IsFormat(text[i - 1]) && !IsFormat(text[i + 1]) && !char.IsWhiteSpace(text[i - 1]) && !char.IsWhiteSpace(text[i + 1]);

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
