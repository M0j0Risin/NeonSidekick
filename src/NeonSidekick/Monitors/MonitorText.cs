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

    /// <summary>An off-desktop monitor's tab once NVIDIA's driver read it (2026-10-27, the NVIDIA plan).</summary>
    public const string OffDesktopReached = "Off the desktop: its inputs switch from here. Picking this PC's input adds it back.";

    /// <summary>A switch to an off-desktop monitor that NVIDIA's driver reached once but that didn't answer this time.</summary>
    public static string OffDesktopNoAnswer(string monitor) =>
        monitor + " is off the desktop and didn't answer through NVIDIA's driver: add it with Only this monitor or Extend all to switch its input.";

    /// <summary><c>/monitor &lt;n&gt; pc now</c> on a monitor off the desktop: what it shows isn't this PC's.</summary>
    public static string PcNowOffDesktop(string monitor, string spelled, string example) =>
        monitor + " is off the desktop, so the input it shows isn't this PC's: name the input, as /monitor " + spelled + " pc " + example + ".";

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

    /// <summary><see cref="LeftDesktop"/> when NVIDIA's driver still reaches it (2026-10-27): the app can switch it back.</summary>
    public static string LeftDesktopReached(string monitor, string input) => monitor + " switched to " + input + " and left the desktop; its tab still switches it, and this PC's input adds it back.";

    /// <summary>An off-desktop monitor switched to this PC's input and added to the desktop (2026-10-27).</summary>
    public static string SwitchedAndAdded(string monitor, string input) => monitor + " switched to " + input + " and is on the desktop again.";

    /// <summary>The input switched, the desktop's Extend refused: the layout's own sentence follows.</summary>
    public static string SwitchedNotAdded(string monitor, string input, string why) => monitor + " switched to " + input + ", but it wasn't added to the desktop: " + why;

    /// <summary>The change's words for a switch that also adds the monitor back (the question, the tool's ask, the audit line).</summary>
    public const string AndAddWords = " and add it to the desktop";

    /// <summary>The footer of an off-desktop tab's row for this PC's input.</summary>
    public const string PcInputOffDesktopFooter = "Switches to it and adds the monitor to the desktop.";

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

    public const string Usage = "/monitor takes nothing (the pane), list, extend, duplicate, or a monitor's number or name, then input <input>, pc <input>|now|auto, only or primary; quote a name with spaces.";

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
        MonitorCommand.PcWord => "set which of its inputs is this PC's",
        MonitorCommand.NowWord => "this PC's input is the one it shows now",
        MonitorCommand.AutoWord => "no pick: guess from the cable",
        _ => "",
    };

    // ----- /monitor <n> pc (later on 2026-10-23: which input is this PC's) -----

    public static string PcUsage(string monitor, IReadOnlyList<string> inputs) =>
        "/monitor " + monitor + " pc takes an input (" + string.Join(", ", inputs) + "), now (the input it shows now) or auto (guess from the cable).";

    /// <summary>Bare <c>pc</c>: the pick, else the cable's guess or none.</summary>
    public static string PcNow(string monitor, string? picked, string? guessed, string connector) =>
        picked is not null ? monitor + ": this PC's input is " + picked + " (picked)."
            : monitor + ": this PC's input is not set; " + GuessWords(guessed, connector) + ".";

    public static string PcSet(string monitor, string input) => monitor + ": this PC's input is " + input + ".";

    public static string PcCleared(string monitor, string? guessed, string connector) => monitor + ": this PC's input is not set; " + GuessWords(guessed, connector) + ".";

    private static string GuessWords(string? guessed, string connector) =>
        guessed is not null ? "the " + connector + " cable guesses " + guessed
            : MonitorShowing.CableKnown(connector) ? "the " + connector + " cable guesses from the input it shows"
            : "nothing is guessed for a " + connector + " connection";

    public static string PcNoRead(string monitor) => monitor + " didn't say which input it shows, so nothing was saved.";

    public static string PcNoKey(string monitor) => monitor + " has no id to save a pick under (an indirect or virtual display).";

    /// <summary>Headless: the pane is the screen's.</summary>
    public const string NeedsScreen = "The monitors pane is part of the interactive screen; /monitor list prints them, and the changes run here too.";

    // ----- /monitor list and monitor_list -----

    /// <summary>
    /// The lines <c>/monitor list</c> prints and <c>monitor_list</c> answers, from one place: the layout (and, later on 2026-10-23, each monitor
    /// on another input, <see cref="MonitorShowing"/>), then a line a monitor.
    /// </summary>
    public static IReadOnlyList<string> ListLines(MonitorState state, MonitorWords words)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(words);
        var snapshot = state.Topology;
        if (snapshot.Monitors.Count == 0)
        {
            return [NoMonitors];
        }

        var lines = new List<string> { "Monitors: " + LayoutWord(snapshot.Layout) + ElsewhereTail(state.Elsewhere(words).Select(m => m.Label).ToList(), " · ") };
        foreach (var m in snapshot.Monitors)
        {
            var parts = new List<string> { m.Label, m.Connector };
            if (!m.Active)
            {
                // Read through NVIDIA's driver (2026-10-27), it lists its input and inputs as any other; else just this, no DDC/CI advice.
                parts.Add("off the desktop");
                if (state.InputsOf(m) is not { Problem: null, Current: not null })
                {
                    lines.Add(string.Join(" · ", parts));
                    continue;
                }
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
            var showing = state.ShowingOf(m, words);
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
                parts.Add(current == 0 ? ZeroRead : "input " + words.Describe(m, current) + (inputs.Stale ? " (may be old)" : ""));
                if (ShowingWords(showing, words, m) is { } said)
                {
                    parts.Add(said);
                }

                var rows = words.Rows(m, inputs.Offered, current, state.AwayOf(m)).Where(r => !r.Hidden);
                parts.Add("inputs " + string.Join(", ", rows.Select(r => (r.Given is { } g ? g + " " : "") + (r.Standard ?? Mccs.Hex(r.Code)) + " (" + Mccs.Hex(r.Code) + ")"))
                    + (inputs.Reported ? "" : " (not reported by the monitor)"));
            }
            else
            {
                parts.Add("input not read");
                if (ShowingWords(showing, words, m) is { } said)
                {
                    parts.Add(said);
                }
            }

            lines.Add(string.Join(" · ", parts));
        }

        return lines;
    }

    /// <summary><c>--monitor-check</c>'s input line for a monitor the cable says is on another input.</summary>
    public static string CheckElsewhere(string connector) => "another input (guessed from the " + connector + " cable)";

    /// <summary>A monitor whose input read 0x00 (no input has it: some LG monitors answer a read they don't support with zeros).</summary>
    public const string ZeroRead = "input not read (the monitor answered 0x00)";

    /// <summary>A list line's judgment: <c>not this PC's (…)</c> by its basis, <c>this PC's input</c> for a picked match, else nothing.</summary>
    public static string? ShowingWords(MonitorShowing showing, MonitorWords words, MonitorInfo monitor)
    {
        ArgumentNullException.ThrowIfNull(showing);
        ArgumentNullException.ThrowIfNull(words);
        ArgumentNullException.ThrowIfNull(monitor);
        return showing switch
        {
            { Kind: MonitorShowingKind.ThisPc, Basis: MonitorShowingBasis.Picked } => "this PC's input",
            { Kind: MonitorShowingKind.Elsewhere, Basis: MonitorShowingBasis.Picked, PcInput: { } pc } => "not this PC's (" + words.Describe(monitor, pc) + ")",
            { Kind: MonitorShowingKind.Elsewhere, Basis: MonitorShowingBasis.Switched, Left: { } left } => "not this PC's (the app switched it from " + words.Describe(monitor, left) + ")",
            { Kind: MonitorShowingKind.Elsewhere, Basis: MonitorShowingBasis.Cable } => "not this PC's (guessed from the " + monitor.Connector + " cable)",
            _ => null,
        };
    }

    /// <summary>
    /// Why a monitor seems not to show this PC, as a predicate after its name: <c>shows HDMI-1, not this PC's DisplayPort-1</c>, <c>was switched
    /// away from DisplayPort-1 by the app</c>, <c>seems to show another input (HDMI-1 on a DisplayPort cable)</c>. For the questions and footers.
    /// </summary>
    public static string Why(MonitorShowing showing, MonitorWords words, MonitorInfo monitor)
    {
        ArgumentNullException.ThrowIfNull(showing);
        ArgumentNullException.ThrowIfNull(words);
        ArgumentNullException.ThrowIfNull(monitor);
        return showing switch
        {
            { Basis: MonitorShowingBasis.Picked, Current: { } now, PcInput: { } pc } => "shows " + words.Describe(monitor, now) + ", not this PC's " + words.Describe(monitor, pc),
            { Basis: MonitorShowingBasis.Switched, Left: { } left } => "was switched away from " + words.Describe(monitor, left) + " by the app",
            { Basis: MonitorShowingBasis.Cable, Current: { } now } => "seems to show another input (" + words.Describe(monitor, now) + " on a " + monitor.Connector + " cable)",
            _ => "seems to show another input",
        };
    }

    /// <summary>
    /// The monitors on another input after a layout's word: <paramref name="lead"/> then <c>2 XG27UQDMS shows another input</c> or
    /// <c>2 XG27UQDMS and 3 DELL show other inputs</c>; empty for none.
    /// </summary>
    public static string ElsewhereTail(IReadOnlyList<string> monitors, string lead)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        return monitors.Count switch
        {
            0 => "",
            1 => lead + monitors[0] + " shows another input",
            _ => lead + string.Join(", ", monitors.Take(monitors.Count - 1)) + " and " + monitors[^1] + " show other inputs",
        };
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

    /// <summary>The Layout tab's caption: the layout in force, and (later on 2026-10-23) each monitor on another input.</summary>
    public static string LayoutNow(MonitorArrangement layout, IReadOnlyList<string>? elsewhere = null) =>
        "Now " + LayoutWord(layout) + ElsewhereTail(elsewhere ?? [], "; ") + ".";

    public static string NamesTitle(string monitor) => Title + " · " + monitor + " · Input names";

    // ----- This PC's input (later on 2026-10-23, the user's ask: a choice, not a name to type) -----

    public static string PcTitle(string monitor) => Title + " · " + monitor + " · This PC's input";

    /// <summary>The pick list's hint.</summary>
    public const string PcKeys = "Enter = pick · Esc = back";

    /// <summary>A monitor tab's row: the pick, the cable's guess, or nothing set.</summary>
    public static string PcRow(string? picked, string? guessed) =>
        "This PC's input: " + (picked ?? (guessed is not null ? "guessed " + guessed : "not set")) + "…";

    public const string PcRowFooter = "Which input is this PC's: it decides \"shows another input\" and when a change asks first. Nothing switches.";

    /// <summary>The pick list's first row.</summary>
    public static string PcNowRow(string input) => "The input it shows now: " + input;

    public static string PcNowFooter(string code) => "Saves " + code + ", the code the monitor reports: pick it while the monitor shows this PC.";

    public const string PcGuessRow = "Guess from the cable";

    public const string PcNotSetRow = "Not set";

    public static string PcGuessFooter(string connector) => "No pick: the " + connector + " cable guesses (HDMI on DisplayPort, or the reverse, is another input).";

    public static string PcNotSetFooter(string connector) => "No pick: nothing is guessed for a " + connector + " connection.";

    /// <summary>Dim tags on the pick list's rows and a tab's picked input.</summary>
    public const string ThisPcTag = "this PC";

    public const string CurrentTag = "current";

    public const string GuessedTag = "guessed";

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

    public const string InputNamesFooter = "Rename or hide this monitor's inputs.";

    public static string InputFooter(byte code, bool away, bool thisPc = false) => Mccs.Hex(code) + (thisPc ? " · this PC's input" : "") + (away ? " · " + AwayFooter : "");

    /// <summary>The ↩'s meaning, on its row's footer.</summary>
    public const string AwayFooter = "the input it showed before the app switched it";

    /// <summary>A layout row's footer for a monitor on another input: <paramref name="why"/> is <see cref="Why"/>'s.</summary>
    public static string AwayRowFooter(string footer, string why) => footer + " Asks first: this monitor " + why + ".";

    public const string DuplicateAwayFooter = DuplicateFooter + " Asks first: a monitor on another input can't be switched back while duplicated.";

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

    /// <summary>Only or Make primary on a monitor on another input; <paramref name="why"/> is <see cref="Why"/>'s.</summary>
    public static string AwayLayoutQuestion(string change, string monitor, string why) =>
        change + "?\n" + monitor + " " + why + ", so it may not show this PC.";

    /// <summary>Duplicate while a monitor is on another input; <paramref name="why"/> is <see cref="Why"/>'s.</summary>
    public static string DuplicateAwayQuestion(string monitor, string why) =>
        "Duplicate?\n" + monitor + " " + why + ", and while duplicated the app can't switch it back until Extend.";

    // ----- Layout words -----

    public static string LayoutWord(MonitorArrangement layout) => layout switch
    {
        MonitorArrangement.Duplicated => "duplicated",
        MonitorArrangement.Single => "on one monitor",
        _ => "extended",
    };

    /// <summary>The connector words <see cref="MonitorShowing"/>'s cable guess knows.</summary>
    public const string DisplayPortConnector = "DisplayPort";

    public const string HdmiConnector = "HDMI";

    public const string UsbCConnector = "USB-C";

    /// <summary>The connector's word for a CCD output technology (<c>DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY</c>).</summary>
    public static string Connector(uint technology) => technology switch
    {
        0 => "VGA",
        4 => "DVI",
        5 => HdmiConnector,
        10 => DisplayPortConnector,
        11 => "Internal",
        18 => UsbCConnector,
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
