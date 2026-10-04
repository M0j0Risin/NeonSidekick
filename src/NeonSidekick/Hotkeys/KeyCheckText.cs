using System.Globalization;

namespace NeonSidekick.Hotkeys;

/// <summary><c>/keycheck</c>'s wording (2026-10-04): the pane's label, its header and caveat, the status words, the lines without a pane. The <c>*Text.cs</c> shape. Pinned.</summary>
public static class KeyCheckText
{
    public const string Category = "Keys";

    /// <summary>The info pane's label and its one tab's title.</summary>
    public const string Label = "Key check";
    public const string TabTitle = "Chords";

    public const string FreeWord = "free";
    public const string HeldWord = "held";

    public static string UnknownWord(int error) => "unknown (error " + error.ToString(CultureInfo.InvariantCulture) + ")";

    /// <summary>A row's status as the pane and the lines write it.</summary>
    public static string Status(HotkeyProbeResult result) => result.Status switch
    {
        HotkeyStatus.Free => FreeWord,
        HotkeyStatus.Held => HeldWord,
        _ => UnknownWord(result.Error),
    };

    /// <summary>The first line: how many of the chords another program holds.</summary>
    public static string Header(int held, int total) => held == 0
        ? $"All {N(total)} chords are free: no other program holds them."
        : $"{N(held)} of {N(total)} chords held by another program: those keys never reach the app.";

    /// <summary>What the check cannot see (the user's call: global hotkeys only). Pinned.</summary>
    public const string Caveat =
        "Only hotkeys registered with Windows show here; a keyboard hook (AutoHotkey, PowerToys Keyboard Manager) or a Windows Terminal key binding can still take a key.";

    /// <summary>The error off Windows, where there is no probe. Pinned.</summary>
    public const string Unsupported = "The key check needs Windows: it asks Windows which hotkeys other programs hold.";

    /// <summary>The report as lines, for a console without the pane: the header, the caveat, then one line per chord, the columns padded.</summary>
    public static IReadOnlyList<string> Lines(KeyCheckReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var lines = new List<string> { Header(report.Held, report.Rows.Count), Caveat };
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
