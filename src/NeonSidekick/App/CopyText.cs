using System.Globalization;
using NeonSidekick.Files;

namespace NeonSidekick.App;

/// <summary>
/// <c>/copy</c>'s words (moved here 2026-11-02, review P2-10: the family was spread over <see cref="ChatScreen"/>, and the code-copy strings
/// would have split it again), and the one conversion every copy of the transcript goes through, <see cref="ForClipboard"/>.
/// </summary>
public static class CopyText
{
    /// <summary>The <c>/copy</c> word for every exchange.</summary>
    public const string CopyAllWord = "all";

    /// <summary>The <c>/copy</c> switch for the model's thinking too (2026-09-26, the user's ask), after <c>/cmdcopy</c>'s <c>--history</c> (folded into <c>/profile push history</c> on 2026-10-09).</summary>
    public const string CopyThinkingSwitch = "--thinking";

    /// <summary>The <c>/copy</c> word for a code block of the last reply (2026-11-01, copy a code block).</summary>
    public const string CopyCodeWord = "code";

    public const string CopyUsageError = "/copy copies the last reply; /copy <n> the last n; /copy all every one; /copy code [<n>] a code block of the last one; add --thinking for the model's thinking too.";

    /// <summary>The <c>/copy</c> list's note on <c>all</c>. Pinned.</summary>
    public const string CopyAllNote = "every reply";

    /// <summary>The <c>/copy</c> list's note on <see cref="CopyThinkingSwitch"/>.</summary>
    public const string CopyThinkingNote = "with the model's thinking";

    /// <summary>The <c>/copy</c> list's note on <see cref="CopyCodeWord"/> (2026-11-01).</summary>
    public const string CopyCodeNote = "a code block of the last reply";

    public const string NothingToCopyNotice = "(nothing to copy yet)";

    /// <summary>The last reply holds no code block (<c>/copy code</c>). Pinned.</summary>
    public const string NoCodeBlockNotice = "(the last reply has no code block)";

    /// <summary><c>/copy code</c>'s number past the last reply's blocks: how many there are.</summary>
    public static string CodeBlockPastCountError(int asked, int count) =>
        $"The last reply has {count.ToString(CultureInfo.InvariantCulture)} code block{(count == 1 ? "" : "s")}; there is no code block {asked.ToString(CultureInfo.InvariantCulture)}.";

    /// <summary>The line after <c>/copy code</c>: which block went, of how many.</summary>
    public static string CopiedCodeNotice(int block, int count) =>
        $"(copied code block {block.ToString(CultureInfo.InvariantCulture)} of {count.ToString(CultureInfo.InvariantCulture)} to the clipboard)";

    /// <summary>
    /// The line after a copy: how many went, of how many there are, as replies or exchanges — and,
    /// with <paramref name="withThinking"/> (2026-09-26), that the thinking went with them. Pinned.
    /// </summary>
    public static string CopiedNotice(int copied, int total, bool withUserText, bool withThinking = false)
    {
        string noun = withUserText ? "exchange" : "reply";
        string nouns = withUserText ? "exchanges" : "replies";
        string one = withThinking ? " with its thinking" : "";
        string many = withThinking ? " with their thinking" : "";
        return copied == 1 ? $"(copied the last {noun}{one} to the clipboard)"
            : copied < total ? $"(copied the last {copied} {nouns}{many} to the clipboard)"
            : $"(copied all {copied} {nouns}{many} to the clipboard)";
    }

    /// <summary>
    /// What a copy's notice and flash add when <see cref="ForClipboard"/> dropped <paramref name="dropped"/> hidden characters (2026-11-02,
    /// review P12-4), so a paste that reads shorter isn't a surprise: <c>2 hidden characters left out</c>. Pinned.
    /// </summary>
    public static string HiddenLeftOut(int dropped) =>
        $"{dropped.ToString(CultureInfo.InvariantCulture)} hidden character{(dropped == 1 ? "" : "s")} left out";

    /// <summary><paramref name="line"/> (a parenthesized notice, or the hint row's flash) with <see cref="HiddenLeftOut"/> after it; as it is with none dropped.</summary>
    public static string WithHidden(string line, int dropped)
    {
        ArgumentNullException.ThrowIfNull(line);
        return dropped <= 0 ? line
            : line.StartsWith('(') && line.EndsWith(')') ? line[..^1] + "; " + HiddenLeftOut(dropped) + ")"
            : line + " (" + HiddenLeftOut(dropped) + ")";
    }

    /// <summary>
    /// What the clipboard gets for <paramref name="text"/> (2026-11-02, reviews P3-1 and P12-4): the hidden characters out
    /// (<see cref="SafeText.ForClipboard"/>, <paramref name="dropped"/> counted), then this platform's line ending, <c>\r\n</c> on Windows (the
    /// CF_UNICODETEXT convention) and <c>\n</c> elsewhere (a Mac's pasteboard and editors take bare line feeds). <see cref="string.ReplaceLineEndings(string)"/>,
    /// so a reply already holding <c>\r\n</c> never becomes <c>\r\r\n</c>. Plain <c>/copy</c>, <c>/copy code</c> and a ⧉ click all go through it.
    /// </summary>
    public static string ForClipboard(string text, out int dropped) =>
        SafeText.ForClipboard(text, out dropped).ReplaceLineEndings(OperatingSystem.IsWindows() ? "\r\n" : "\n");
}
