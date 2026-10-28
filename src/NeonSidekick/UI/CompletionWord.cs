namespace NeonSidekick.UI;

/// <summary>
/// A completion item's value as one word of its command (2026-10-27, the review): a path or a folder name with a blank is double-quoted, so
/// the command's parse (<c>PrintCommand.Words</c>, <c>TaskCommand</c>'s words, <c>ComfyImagine.Parse</c>) reads it back whole; and a match
/// that ignores the quotes, so <c>My</c> and <c>"My</c> both reach <c>"My Folder"</c>. Pure.
/// </summary>
public static class CompletionWord
{
    /// <summary><paramref name="value"/> double-quoted when it holds a blank, else as it is.</summary>
    public static string Quoted(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Any(char.IsWhiteSpace) ? "\"" + value + "\"" : value;
    }

    /// <summary>Whether <paramref name="item"/> starts with <paramref name="typed"/>, any case, with every double quote left out of both.</summary>
    public static bool StartsQuoteBlind(string item, string typed)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(typed);
        return item.Replace("\"", "", StringComparison.Ordinal).StartsWith(typed.Replace("\"", "", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);
    }
}
