using System.Text;

namespace NeonSidekick.Files;

/// <summary>
/// A file name made of words that may hold anything (2026-10-29, the Office writing plan; <c>DirectoryCsv.NameFor</c>'s rule of 2026-10-15,
/// lifted so the database exports name their files the same way): lower-cased, a space and every character a file name can't hold on either
/// OS (Windows' set added to the running one's, so <c>server\instance</c> and <c>C:</c> come out alike on a Mac) as <c>-</c>, runs folded,
/// no <c>-</c> or <c>.</c> at the ends, cut to a length. Pure.
/// </summary>
public static class FileNames
{
    /// <summary>The longest a made-up name's words run.</summary>
    public const int MaxChars = 60;

    private static readonly HashSet<char> Invalid = [.. Path.GetInvalidFileNameChars(), '/', '\\', ':', '*', '?', '"', '<', '>', '|'];

    /// <summary><paramref name="words"/> as a file-safe stem (no extension), at most <paramref name="max"/> characters.</summary>
    public static string Safe(string words, int max = MaxChars)
    {
        ArgumentNullException.ThrowIfNull(words);
        var name = new StringBuilder(words.Length);
        foreach (char c in words.Trim().ToLowerInvariant())
        {
            name.Append(char.IsWhiteSpace(c) || Invalid.Contains(c) || char.IsControl(c) ? '-' : c);
        }

        string text = name.ToString();
        while (text.Contains("--", StringComparison.Ordinal))
        {
            text = text.Replace("--", "-", StringComparison.Ordinal);
        }

        text = text.Trim('-', '.');
        return text.Length > max ? text[..max].TrimEnd('-', '.') : text;
    }
}
