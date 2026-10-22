namespace NeonSidekick.Files;

/// <summary>
/// A cut that never splits a surrogate pair (2026-10-22, review G20 and its folded sites, the fix plan's mechanism 3): <c>read_file</c>'s
/// long line, search's clipped line, <c>read_pdf</c>'s page and find context, <c>vault_daily</c>'s body all cut by <see cref="string.Length"/>,
/// so an emoji at the edge left a lone high surrogate — which System.Text.Json then refused to put in the next request, failing the
/// turn. The step-back <see cref="Llm.Tools.UntrustedText"/>'s fence cut already made. Pure.
/// </summary>
public static class TextCut
{
    /// <summary>The first <paramref name="max"/> characters of <paramref name="text"/>, one fewer when the last would be half a pair.</summary>
    public static string Safe(string text, int max)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Length <= max ? text : text[..SafeLength(text, max)];
    }

    /// <summary><see cref="Safe(string, int)"/> over a span.</summary>
    public static ReadOnlySpan<char> Safe(ReadOnlySpan<char> text, int max) => text.Length <= max ? text : text[..SafeLength(text, max)];

    /// <summary>How many characters a cut at <paramref name="max"/> keeps: <paramref name="max"/>, or one fewer before a pair's second half.</summary>
    public static int SafeLength(ReadOnlySpan<char> text, int max)
    {
        if (max <= 0)
        {
            return 0;
        }

        if (max >= text.Length)
        {
            return text.Length;
        }

        return char.IsHighSurrogate(text[max - 1]) && char.IsLowSurrogate(text[max]) ? max - 1 : max;
    }

    /// <summary>The text from <paramref name="start"/> on, one later when <paramref name="start"/> would begin on a pair's second half.</summary>
    public static int SafeStart(ReadOnlySpan<char> text, int start)
    {
        if (start <= 0)
        {
            return 0;
        }

        if (start >= text.Length)
        {
            return text.Length;
        }

        return char.IsLowSurrogate(text[start]) && char.IsHighSurrogate(text[start - 1]) ? start + 1 : start;
    }
}
