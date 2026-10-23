using System.Globalization;

namespace NeonSidekick.Monitors;

/// <summary>
/// The monitors' words (2026-10-23, the monitors plan): the system's refusals, the outcomes, the pane's notes and rows, <c>/monitor</c>'s
/// usage and the tools' results. American spelling; hints in the house's <c>key = what</c> form and within a 120-column window's Hints bar.
/// </summary>
public static class MonitorText
{
    /// <summary>The 🖥️ the pane's title, the home bar item and the picker lead with.</summary>
    public const string Glyph = "🖥️";

    public const string Title = Glyph + " Monitors";

    /// <summary>A target with no EDID name.</summary>
    public const string Unnamed = "Display";

    // ----- The system's refusals -----

    public const string NoneHere = "Monitor control isn't available here: it needs Windows.";

    public const string NoMonitorControl = "This Windows has no monitor-control library (dxva2), so inputs can't be switched here; the layout still can.";

    public const string NoDdc = "This monitor doesn't answer DDC/CI. Many monitors ship with DDC/CI turned off in their own on-screen menu (look for \"DDC/CI\"), and some don't implement it fully.";

    public const string NoInputSwitch = "This monitor answers DDC/CI but can't switch inputs over it.";

    public const string NoAnswer = "The monitor didn't answer within 10 seconds; it may still be busy.";

    public const string OnlyFromDesktop = "Windows refused: monitors can be changed only from this PC's own desktop, not a remote or locked session.";

    public const string BadConfiguration = "Windows refused that layout: the monitors can't be arranged that way.";

    public const string NotSupported = "Windows or the display driver doesn't support that change.";

    public const string NoSuchTarget = "That monitor isn't connected any more.";

    public const string SharedSource = "Inputs can't be told apart while duplicated; Extend first.";

    public const string OffDesktop = "Off the desktop: add it with Only this monitor or Extend all to switch its input.";

    public const string NotReported = "Not reported by the monitor; it may ignore some.";

    public const string Reading = "Reading the monitor…";

    public const string NothingToDo = "This PC's only monitor has no DDC/CI, so there is nothing to change.";

    public const string NoMonitors = "Windows reports no monitor in this session.";

    /// <summary>Any other failure, with what the system said.</summary>
    public static string Failed(string detail) => "The monitor call failed (" + detail + ").";

    // ----- Outcomes -----

    public static string Switched(string monitor, string input) => monitor + " switched to " + input + ".";

    public static string SwitchedUnlisted(string monitor, string input) => monitor + " switched to " + input + ", a code it doesn't list; it may ignore it.";

    public static string SwitchedNoRead(string monitor, string input) => monitor + " was sent " + input + "; it didn't say back which input it shows.";

    public static string LeftDesktop(string monitor, string input) => monitor + " switched to " + input + " and left the desktop; its own buttons or the other machine bring it back.";

    public static string AlreadyOn(string monitor, string input) => monitor + " is already on " + input + ".";

    public static string Arranged(string layout) => "The monitors are " + layout + ".";

    public static string AlreadyArranged(string layout) => "The monitors are already " + layout + ".";

    public static string MadePrimary(string monitor) => monitor + " is the primary monitor now.";

    public static string AlreadyPrimary(string monitor) => monitor + " is already the primary monitor.";

    public static string OnlyNow(string monitor) => "Only " + monitor + " shows the desktop now.";

    public static string AlreadyOnly(string monitor) => monitor + " is already the only monitor.";

    public static string Ignored(string monitor, string input, string current) => monitor + " still shows " + current + ": it didn't take " + input + ".";

    public const string PrimaryWhileDuplicated = "While duplicated every monitor shows the primary desktop; Extend first.";

    public const string OneMonitor = "Only one monitor is connected, so there is nothing to duplicate.";

    public const string Declined = "Left as it was.";

    // ----- Rows (the Layout tab's, a monitor tab's, and the changes in words) -----

    public const string ExtendRow = "Extend all";

    public const string DuplicateRow = "Duplicate";

    public static string OnlyRow(string monitor) => "Only " + monitor;

    public static string PrimaryRow(string monitor) => "Make " + monitor + " primary";

    public const string OnlyThisRow = "Only this monitor";

    public const string PrimaryThisRow = "Make primary";

    public const string EnterCodeRow = "Enter a code…";

    public const string InputNamesRow = "Input names…";

    // ----- /monitor -----

    public const string Usage = "/monitor takes nothing (the pane), list, extend, duplicate, or a monitor's number or name, then input <input>, only or primary; quote a name with spaces.";

    public static string NoSuchMonitor(string typed, IReadOnlyList<int> numbers) =>
        "There is no monitor " + typed + (numbers.Count == 0 ? "." : "; the numbers are " + string.Join(", ", numbers.Select(n => n.ToString(CultureInfo.InvariantCulture))) + ".");

    public static string NoSuchMonitorName(string typed, IReadOnlyList<string> monitors) =>
        "No monitor is named '" + typed + "'" + (monitors.Count == 0 ? "." : "; there are " + string.Join(", ", monitors) + ".");

    public static string Ambiguous(string typed, IReadOnlyList<string> monitors) => "'" + typed + "' names " + string.Join(" and ", monitors) + ": give its number.";

    public static string InputUsage(string monitor, IReadOnlyList<string> inputs) =>
        "/monitor " + monitor + " input takes an input: " + string.Join(", ", inputs) + ", or a code in hex such as 0x1B.";

    public static string UnknownInput(string monitor, string typed, IReadOnlyList<string> inputs) =>
        monitor + " has no input '" + typed + "': " + string.Join(", ", inputs) + ", or a code in hex such as 0x1B.";

    /// <summary>A <c>/monitor</c> word's completion hint.</summary>
    public static string VerbHint(string verb) => verb switch
    {
        MonitorCommand.ListWord => "print the monitors and their inputs",
        MonitorCommand.ExtendWord => "extend the desktop across every monitor",
        MonitorCommand.DuplicateWord => "show the same desktop on every monitor",
        MonitorCommand.InputWord => "switch its input",
        MonitorCommand.OnlyWord => "turn off every other monitor",
        MonitorCommand.PrimaryWord => "make it the primary monitor",
        _ => "",
    };

    /// <summary>Headless: the pane is the screen's.</summary>
    public const string NeedsScreen = "The monitors pane is part of the interactive screen; /monitor list prints them, and the changes run here too.";

    // ----- /monitor list and monitor_list -----

    /// <summary>The lines <c>/monitor list</c> prints and <c>monitor_list</c> answers, from one place: the layout, then a line a monitor.</summary>
    public static IReadOnlyList<string> ListLines(MonitorState state, MonitorWords words)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(words);
        var snapshot = state.Topology;
        if (snapshot.Monitors.Count == 0)
        {
            return [NoMonitors];
        }

        var lines = new List<string> { "Monitors: " + LayoutWord(snapshot.Layout) };
        foreach (var m in snapshot.Monitors)
        {
            var parts = new List<string> { m.Label, m.Connector };
            if (!m.Active)
            {
                parts.Add("off the desktop");
                lines.Add(string.Join(" · ", parts));
                continue;
            }

            if (m.Mode is { } mode)
            {
                parts.Add(Mode(mode));
            }

            if (m.Primary)
            {
                parts.Add("primary");
            }

            var inputs = state.InputsOf(m);
            if (m.Shared)
            {
                parts.Add("duplicated: inputs can't be told apart");
            }
            else if (inputs.Problem is { } problem)
            {
                parts.Add(ProblemWord(problem));
            }
            else if (inputs.Current is { } current)
            {
                string away = state.AwayOf(m) is { } left ? ", left " + words.Describe(m, left) : "";
                parts.Add("input " + words.Describe(m, current) + away + (inputs.Stale ? " (may be old)" : ""));
                var rows = words.Rows(m, inputs.Offered, current, state.AwayOf(m)).Where(r => !r.Hidden);
                parts.Add("inputs " + string.Join(", ", rows.Select(r => (r.Given is { } g ? g + " " : "") + (r.Standard ?? Mccs.Hex(r.Code)) + " (" + Mccs.Hex(r.Code) + ")"))
                    + (inputs.Reported ? "" : " (not reported by the monitor)"));
            }
            else
            {
                parts.Add("input not read");
            }

            lines.Add(string.Join(" · ", parts));
        }

        return lines;
    }

    /// <summary>A problem's few words on a list line.</summary>
    public static string ProblemWord(string problem) => problem switch
    {
        NoDdc => "no DDC/CI (it may be off in the monitor's own menu)",
        NoInputSwitch => "can't switch inputs over DDC/CI",
        NoMonitorControl => "no monitor-control library",
        NoAnswer => "didn't answer",
        SharedSource => "duplicated: inputs can't be told apart",
        OffDesktop => "off the desktop",
        _ => problem,
    };

    // ----- The tools -----

    /// <summary>A model's change's question: the change in words, and the last screen's warning when it applies.</summary>
    /// <remarks>The warning's reason (its lines after the first, the question there being this one's) goes on a line of its own: the pane's caption.</remarks>
    public static string ToolQuestion(string change, string? warning) =>
        "The model asks to " + char.ToLowerInvariant(change[0]) + change[1..] + "." + (warning is null ? "" : "\n" + Reason(warning));

    /// <summary>A question's reason: its lines after the first (the pane's caption), or the whole text when it has one line.</summary>
    public static string Reason(string question) => question.IndexOf('\n', StringComparison.Ordinal) is int newline and >= 0 ? question[(newline + 1)..] : question;

    public const string NoPaneToAsk = "Error: nobody to ask: the monitor tools change a monitor only after the user's yes on the pane.";

    public const string ToolDeclined = "The user said no; nothing changed.";

    public static string MissingArgument(string name) => "Error: '" + name + "' is required.";

    public const string BadAction = "Error: 'action' is one of extend, duplicate, only, primary.";

    public static string NeedsMonitor(string action) => "Error: '" + action + "' needs 'monitor' (a number from monitor_list or a name).";

    // ----- The pane -----

    /// <summary>The Layout tab's title.</summary>
    public const string LayoutTab = "Layout";

    /// <summary>The rule between a monitor's inputs and its layout rows.</summary>
    public const string ActionsHeading = "Layout";

    /// <summary>The pane's hint.</summary>
    public const string Keys = "←/→ = monitor · Enter = choose · Esc = close";

    /// <summary>The Input names… page's hint.</summary>
    public const string NamesKeys = "Enter = rename · h = hide/show · Del = clear name · Esc = back";

    /// <summary>The typed text's hint (a code, a rename).</summary>
    public const string EditKeys = "Enter = save · Esc = back";

    /// <summary>The Layout tab's caption: the layout in force.</summary>
    public static string LayoutNow(MonitorArrangement layout) => "Now " + LayoutWord(layout) + ".";

    public static string NamesTitle(string monitor) => Title + " · " + monitor + " · Input names";

    public const string CurrentMark = "●";

    /// <summary>Beside the input the app switched a monitor away from.</summary>
    public const string AwayMark = "↩";

    public const string HiddenWord = "hidden";

    public const string StaleWord = "(may be old: the last read failed)";

    public const string ExtendFooter = "Gives every connected monitor its own part of the desktop.";

    public const string DuplicateFooter = "Shows the same desktop on every monitor.";

    public const string OnlyFooter = "Turns off every other monitor.";

    public const string PrimaryFooter = "Moves the taskbar and the desktop's origin here.";

    public const string EnterCodeFooter = "Type a code in hex (0x1B, 1B) for an input the monitor doesn't list, such as USB-C.";

    public const string InputNamesFooter = "Rename or hide this monitor's inputs (\"This PC\" for the one this PC is on).";

    public static string InputFooter(byte code, bool away) => Mccs.Hex(code) + (away ? " · " + AwayFooter : "");

    /// <summary>The ↩'s meaning, on its row's footer.</summary>
    public const string AwayFooter = "this PC's input before the app switched it";

    public static string AwayRowFooter(string footer, string left) => footer + " Asks first: the app switched this monitor away from " + left + ".";

    public const string DuplicateAwayFooter = DuplicateFooter + " Asks first: a monitor the app switched away can't be switched back while duplicated.";

    public static string EnterCodePrompt(string monitor) => Title + " · " + monitor + " · Enter a code (hex)";

    public static string NotACode(string text) => "'" + text + "' isn't an input code: type hex from 01 to FF, such as 0x1B.";

    public static string NameTaken(string name) => "'" + name + "' is already another input's name on this monitor.";

    public static string Renamed(string input, string name) => input + " is named " + name + ".";

    public static string NameCleared(string input) => input + " has its own name again.";

    public static string HiddenNow(string input) => input + " is hidden from the monitor's tab.";

    public static string ShownNow(string input) => input + " shows on the monitor's tab again.";

    // ----- Questions -----

    // The questions: the question on the first line (the pane's title), the reason after it (its caption, which may wrap).

    public static string LastScreenQuestion(string monitor, string input) =>
        "Switch " + monitor + " to " + input + "?\nNo other monitor would still show this PC; its own buttons or the other machine bring it back.";

    public static string AwayLayoutQuestion(string change, string monitor, string left) =>
        change + "?\n" + monitor + " was switched away from " + left + " by the app, so it may not show this PC.";

    public static string DuplicateAwayQuestion(string monitor, string left) =>
        "Duplicate?\n" + monitor + " was switched away from " + left + ", and while duplicated the app can't switch it back until Extend.";

    // ----- Layout words -----

    public static string LayoutWord(MonitorArrangement layout) => layout switch
    {
        MonitorArrangement.Duplicated => "duplicated",
        MonitorArrangement.Single => "on one monitor",
        _ => "extended",
    };

    /// <summary>The connector's word for a CCD output technology (<c>DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY</c>).</summary>
    public static string Connector(uint technology) => technology switch
    {
        0 => "VGA",
        4 => "DVI",
        5 => "HDMI",
        10 => "DisplayPort",
        11 => "Internal",
        18 => "USB-C",
        15 => "Miracast",
        16 or 17 => "Indirect",
        0x80000000 => "Internal",
        _ => "Other",
    };

    /// <summary><c>3840×2160 at 60 Hz</c> (the rate to two places at most).</summary>
    public static string Mode(MonitorMode mode) =>
        mode.Width.ToString(CultureInfo.InvariantCulture) + "×" + mode.Height.ToString(CultureInfo.InvariantCulture)
        + " at " + mode.RefreshHz.ToString("0.##", CultureInfo.InvariantCulture) + " Hz";
}
