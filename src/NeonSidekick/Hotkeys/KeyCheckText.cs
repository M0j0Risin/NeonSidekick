using System.Globalization;

namespace NeonSidekick.Hotkeys;

/// <summary>
/// <c>/keycheck</c>'s wording (2026-10-04): the pane's label, its header and caveat, the status words, the lines without a pane. The
/// <c>*Text.cs</c> shape. Pinned. A Mac's (2026-10-11) has a header, caveat and status words of its own, and the sentences on the
/// terminal's Option key; Windows' are byte for byte as they were.
/// </summary>
public static class KeyCheckText
{
    public const string Category = "Keys";

    /// <summary>The info pane's label and its one tab's title.</summary>
    public const string Label = "Key check";
    public const string TabTitle = "Chords";

    public const string FreeWord = "free";
    public const string HeldWord = "held";

    /// <summary>A Mac's words for a held chord, by who holds it, and for a key a Mac keyboard has no code for. Pinned.</summary>
    public const string SystemWord = "macOS shortcut";
    public const string OtherAppWord = "held by an app";
    public const string TerminalWord = "held by the terminal";
    public const string NotCheckedWord = "not checked";

    public static string UnknownWord(int error) => "unknown (error " + error.ToString(CultureInfo.InvariantCulture) + ")";

    /// <summary>A row's status as the pane and the lines write it.</summary>
    public static string Status(HotkeyProbeResult result) => result.Status switch
    {
        HotkeyStatus.Free => FreeWord,
        HotkeyStatus.Held => result.Cause switch
        {
            HotkeyCause.System => SystemWord,
            HotkeyCause.OtherApp => OtherAppWord,
            HotkeyCause.Terminal => TerminalWord,
            _ => HeldWord,
        },
        _ => result.Cause == HotkeyCause.NoKeyCode ? NotCheckedWord : UnknownWord(result.Error),
    };

    /// <summary>The first line: how many of the chords another program holds.</summary>
    public static string Header(int held, int total) => held == 0
        ? $"All {N(total)} chords are free: no other program holds them."
        : $"{N(held)} of {N(total)} chords held by another program: those keys never reach the app.";

    /// <summary>
    /// A Mac's first line (2026-10-11): "may", since a macOS shortcut the list calls on is not always one that eats the key in a
    /// terminal (the probe called Ctrl+Enter's free while the list had it on).
    /// </summary>
    public static string MacHeader(int held, int total) => held == 0
        ? $"All {N(total)} chords are free: no macOS shortcut, exclusive app hotkey or terminal setting takes them."
        : $"{N(held)} of {N(total)} chords taken: macOS, another app or the terminal may keep those keys from the app.";

    /// <summary>The header for <paramref name="report"/>: a Mac's or Windows'.</summary>
    public static string HeaderFor(KeyCheckReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return report.Mac ? MacHeader(report.Held, report.Rows.Count) : Header(report.Held, report.Rows.Count);
    }

    /// <summary>What the check cannot see (the user's call: global hotkeys only). Pinned.</summary>
    public const string Caveat =
        "Only hotkeys registered with Windows show here; a keyboard hook (AutoHotkey, PowerToys Keyboard Manager) or a Windows Terminal key binding can still take a key.";

    /// <summary>
    /// What a Mac's check cannot see (2026-10-11, measured: another app's hotkey registered the shared way answers free; macOS lists
    /// no other app's hotkeys; an Apple keyboard's F-keys are media keys by default). Pinned.
    /// </summary>
    public const string MacCaveat =
        "Only macOS's own shortcuts, hotkeys an app holds exclusively and the terminal's Option key show here; most apps hold their hotkeys the shared way, which no check can see, and keyboard tools (Karabiner, BetterTouchTool) can still take a key. On an Apple keyboard F9 and F10 are media keys unless fn is held.";

    /// <summary>The caveat for <paramref name="report"/>: a Mac's or Windows'.</summary>
    public static string CaveatFor(KeyCheckReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return report.Mac ? MacCaveat : Caveat;
    }

    /// <summary>The error where there is no probe (neither Windows nor a Mac since 2026-10-11). Pinned.</summary>
    public const string Unsupported = "The key check needs Windows or macOS: it asks the system which keys other programs hold.";

    // ── The terminal's Option key on a Mac (2026-10-11) ──────────────────────

    /// <summary>Terminal.app, by its default profile (it does not say which profile this window has). Pinned.</summary>
    public static string TerminalAppOption(string profile, bool meta) => meta
        ? $"Terminal's default profile, {profile}: Use Option as Meta key is on, so the Option chords arrive."
        : $"Terminal's default profile, {profile}: Use Option as Meta key is off, so Option types characters and the Option chords never arrive (Terminal › Settings › Profiles › Keyboard).";

    /// <summary>iTerm2, by the profile this session has: which Option key sends Esc+, the one setting the app reads as Alt. Pinned.</summary>
    public static string ItermOption(string profile, bool left, bool right) => (left, right) switch
    {
        (true, true) => $"iTerm2, profile {profile}: both Option keys send Esc+, so the Option chords arrive.",
        (true, false) => $"iTerm2, profile {profile}: the left Option key sends Esc+, so the Option chords arrive with it (not with the right one).",
        (false, true) => $"iTerm2, profile {profile}: the right Option key sends Esc+, so the Option chords arrive with it (not with the left one).",
        _ => $"iTerm2, profile {profile}: neither Option key sends Esc+, so the Option chords never arrive (iTerm2 › Settings › Profiles › Keys).",
    };

    /// <summary>A terminal whose setting the app cannot read (another terminal, or Terminal's or iTerm2's preferences unreadable). Pinned.</summary>
    public static string UnknownOption(string? terminal) =>
        $"The Option key's setting in {terminal ?? "this terminal"} is not known here: if Option types characters, the Option chords never arrive.";

    /// <summary>macOS would not list its shortcuts (<c>CopySymbolicHotKeys</c>' OSStatus), so none of them shows. Pinned.</summary>
    public static string NoSystemList(int status) =>
        "macOS did not list its own shortcuts (status " + status.ToString(CultureInfo.InvariantCulture) + "), so none of them shows here.";

    /// <summary>The report as lines, for a console without the pane: the header, the caveat, the notes, then one line per chord, the columns padded.</summary>
    public static IReadOnlyList<string> Lines(KeyCheckReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var lines = new List<string> { HeaderFor(report), CaveatFor(report) };
        lines.AddRange(report.Notes ?? []);
        int label = report.Rows.Select(r => r.Label.Length).DefaultIfEmpty(0).Max();
        int status = report.Rows.Select(r => Status(r.Result).Length).DefaultIfEmpty(0).Max();
        foreach (var row in report.Rows)
        {
            lines.Add("  " + Status(row.Result).PadRight(status) + "  " + row.Label.PadRight(label) + "  " + row.Meaning);
        }

        return lines;
    }

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);
}
