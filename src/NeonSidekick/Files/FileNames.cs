using System.Text;

namespace NeonSidekick.Files;

/// <summary>
/// A file name made of words that may hold anything (2026-10-29, the Office writing plan; <c>DirectoryCsv.NameFor</c>'s rule of 2026-10-15,
/// lifted so the database exports name their files the same way): lower-cased, a space and every character a file name can't hold on either
/// OS (Windows' set added to the running one's, so <c>server\instance</c> and <c>C:</c> come out alike on a Mac) as <c>-</c>, runs folded,
/// no <c>-</c> or <c>.</c> at the ends, cut to a length without splitting an emoji (2026-10-29, review P3-11). Pure.
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
        foreach (char c in lowerCase ? trimmed.ToLowerInvariant() : trimmed)
        {
            name.Append(char.IsWhiteSpace(c) || Invalid.Contains(c) || char.IsControl(c) ? '-' : c);
        }

        string text = name.ToString();
        while (text.Contains("--", StringComparison.Ordinal))
        {
            text = text.Replace("--", "-", StringComparison.Ordinal);
        }

        text = text.Trim('-', '.');
        return text.Length > max ? TextCut.Safe(text, max).TrimEnd('-', '.') : text;
    }
}
