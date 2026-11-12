using System.Globalization;

namespace NeonSidekick.Midi;

/// <summary>
/// The MIDI words (2026-11-12, the MIDI plan): the backends' refusals, the board's notices, <c>/midi</c>'s usage and lines, the pane's
/// tabs, rows and footers, the learn's steps and the skipped-entry reasons. American spelling; hints in the house's <c>key = what</c>
/// form and within a 120-column window's Hints bar. On a Mac no line says "needs Windows".
/// </summary>
public static class MidiText
{
    /// <summary>The 🎹 the pane's title, the flash and the notices lead with.</summary>
    public const string Glyph = "🎹";

    public const string Title = Glyph + " MIDI";

    // ----- The system's refusals (MidiException messages) -----

    public const string NoneHere = "MIDI isn't available here.";

    public const string InUse = "In use by another app; close it there, or unplug and plug in the device.";

    public const string Gone = "The device isn't connected any more.";

    public const string NoDriver = "The device's driver isn't working; unplug and plug it in again.";

    /// <summary>Another failure, with the system's code.</summary>
    public static string OpenFailed(string code) => "Could not open the device (" + code + ").";

    /// <summary>The Mac's MIDI server couldn't start (a session with no window server).</summary>
    public static string NoMidiServer(int status) => "The system's MIDI service didn't start (" + status.ToString(CultureInfo.InvariantCulture) + ").";

    // ----- Notices -----

    /// <summary>A bound device that couldn't be opened, said once until it opens.</summary>
    public static string OpenFailedNotice(string device, string problem) => Glyph + " " + device + ": " + problem;

    /// <summary>The flash as the screen takes a button's command.</summary>
    public static string Ran(string line) => Glyph + " " + line;

    /// <summary>A binding whose command is no longer one (a custom command deleted).</summary>
    public static string NotACommand(string line) => Glyph + " " + line + " isn't a command any more: /midi rebinds it.";

    public const string NotHeadlessError = "/midi is part of the interactive screen: nothing is listened to headless.";

    public const string Usage = "/midi takes nothing (the pane), learn [<command>], list, on, off or forget <n>.";

    public static string ForgetUsage(int count) => count == 0
        ? "There are no MIDI bindings to forget."
        : "/midi forget takes a binding's number, 1 to " + count.ToString(CultureInfo.InvariantCulture) + " (/midi list shows them).";

    public static string Forgot(int number, MidiBinding binding) =>
        "Forgot binding " + number.ToString(CultureInfo.InvariantCulture) + ": " + Describe(binding.Trigger) + " → " + binding.Command + ".";

    public const string TurnedOn = Glyph + " MIDI input on: bound buttons run their commands.";

    public const string TurnedOff = Glyph + " MIDI input off: the devices are let go.";

    public const string AlreadyOn = "MIDI input is already on.";

    public const string AlreadyOff = "MIDI input is already off.";

    /// <summary>A learn's binding saved while the input was off: it is turned on.</summary>
    public const string TurnedOnToo = "MIDI input was off; it's on now.";

    public const string NoBindings = "No MIDI bindings yet: /midi learn makes one.";

    public const string OffWithBindings = "MIDI input is off, so these don't run: /midi on turns it on.";

    public const string SaveFailed = "Could not save the MIDI bindings; see the log.";

    public const string NewerFile = "midi.json was written by a newer version; it is left as it is.";

    public const string BrokenFile = "midi.json couldn't be read; saving moves it aside to midi.json.bad.";

    public const string UnreadableFile = "midi.json couldn't be read from disk; nothing is changed.";

    /// <summary>A file's state as one line, or null when it reads fine.</summary>
    public static string? FileProblem(MidiFileState state) => state switch
    {
        MidiFileState.Broken => BrokenFile,
        MidiFileState.Newer => NewerFile,
        MidiFileState.Unreadable => UnreadableFile,
        _ => null,
    };

    /// <summary>A binding as a list line: its number, the button, the command.</summary>
    public static string ListLine(int number, MidiBinding binding) =>
        number.ToString(CultureInfo.InvariantCulture) + ". " + Describe(binding.Trigger) + " → " + binding.Command;

    /// <summary>A skipped entry as a list line.</summary>
    public static string SkippedLine(string problem) => "(skipped: " + problem + ")";

    // ----- Describing a button -----

    /// <summary>A button as the user reads it: "CC 80 · ch 1 · FS-4", "Note 60 · ch 10 · Pad #2", "Program 3 · ch 1 · Pedal".</summary>
    public static string Describe(MidiTrigger trigger)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        return Message(trigger.Kind, trigger.Number) + " · ch " + trigger.Channel.ToString(CultureInfo.InvariantCulture) + " · " + Device(trigger.Device, trigger.Occurrence);
    }

    /// <summary>A device's name with its occurrence when it isn't the first of its name ("Pedal #2").</summary>
    public static string Device(string name, int occurrence) =>
        occurrence > 0 ? name + " #" + (occurrence + 1).ToString(CultureInfo.InvariantCulture) : name;

    /// <summary>The message part: "Note 60", "CC 80", "Program 3".</summary>
    public static string Message(MidiKind kind, int number) => kind switch
    {
        MidiKind.Note => "Note ",
        MidiKind.ControlChange => "CC ",
        _ => "Program ",
    } + number.ToString(CultureInfo.InvariantCulture);

    /// <summary>A message heard, as the Devices tab and <c>--midi-check</c> show it.</summary>
    public static string Heard(MidiSeen seen)
    {
        ArgumentNullException.ThrowIfNull(seen);
        string value = seen.Trigger.Kind == MidiKind.ProgramChange ? "" : " · value " + seen.Value.ToString(CultureInfo.InvariantCulture);
        return Message(seen.Trigger.Kind, seen.Trigger.Number) + " · ch " + seen.Trigger.Channel.ToString(CultureInfo.InvariantCulture) + value
            + " · " + Device(seen.Trigger.Device, seen.Trigger.Occurrence) + (seen.Fires ? " · press" : "");
    }

    // ----- Skipped entries (a hand-edited midi.json) -----

    public const string SkippedEmpty = "an empty entry";

    public static string SkippedKind(string? kind) => "kind \"" + (kind ?? "") + "\" isn't note, cc or program";

    public const string SkippedDevice = "no device";

    public static string SkippedChannel(int channel) => "channel " + channel.ToString(CultureInfo.InvariantCulture) + " isn't 1 to 16";

    public static string SkippedNumber(int number) => "number " + number.ToString(CultureInfo.InvariantCulture) + " isn't 0 to 127";

    public static string SkippedOccurrence(int occurrence) => "occurrence " + occurrence.ToString(CultureInfo.InvariantCulture) + " is below 0";

    public static string SkippedCommand(string? command) => "command \"" + (command ?? "") + "\" doesn't start with /";

    public static string SkippedDuplicate(string button) => button + " is bound twice; the first is kept";

    // ----- The pane -----

    public const string BindingsTab = "Bindings";

    public const string DevicesTab = "Devices";

    public const string LearnRow = "Learn a button…";

    public const string LearnRowFooter = "Press a pedal, pad or button, then pick the command it runs.";

    public const string BindingFooter = "Enter = edit the command · r = relearn the button · Del = remove";

    public const string BindingsKeys = "Enter = edit · l = learn · r = relearn · o = MIDI input on/off · Del = remove · Esc = close";

    public const string DevicesKeys = "o = MIDI input on/off · Esc = close";

    public const string EditKeys = "Enter = save · Esc = back";

    public const string PickerKeys = "Type to filter · Enter = pick · Esc = back";

    /// <summary>The Bindings tab's caption while the input is off.</summary>
    public const string OffCaption = "MIDI input is off: o turns it on.";

    public const string HeldCaption = "While this pane is open, buttons only show what they send.";

    public const string NoDevices = "No MIDI device found; plug one in.";

    public const string OpenTag = "listening";

    public const string InUseTag = "in use";

    public const string NothingHeard = "Press a button to see what it sends.";

    /// <summary>The Devices tab's last line.</summary>
    public static string LastHeard(MidiSeen seen) => "Last: " + Heard(seen);

    // ----- The learn -----

    public const string LearnTitle = Glyph + " Learn a button";

    public const string LearnWaiting = "Press a pedal, pad or button… Esc cancels.";

    public const string LearnNoDevice = "No MIDI device found: plug one in (Esc cancels).";

    /// <summary>Some inputs another app holds, named on the learn page.</summary>
    public static string LearnInUse(IReadOnlyList<string> devices) => "In use by another app, so not heard: " + string.Join(", ", devices) + ".";

    public const string LearnCancelled = "Learn canceled.";

    /// <summary>The picker's title once a button is heard.</summary>
    public static string PickTitle(MidiTrigger trigger) => Glyph + " " + Describe(trigger) + ": pick its command";

    /// <summary>The argument edit's title.</summary>
    public static string ArgsTitle(MidiTrigger trigger) => Glyph + " " + Describe(trigger) + ": the command and its arguments";

    /// <summary>The button is bound already: replace?</summary>
    public static string Replace(MidiBinding binding) => Describe(binding.Trigger) + " runs " + binding.Command + " now. Replace it?";

    /// <summary>The button another binding holds, on a relearn.</summary>
    public static string TakenBy(MidiBinding binding) => Describe(binding.Trigger) + " already runs " + binding.Command + "; that binding is replaced.";

    public static string Bound(MidiBinding binding) => Glyph + " " + Describe(binding.Trigger) + " → " + binding.Command;

    public static string Removed(MidiBinding binding) => "Removed " + Describe(binding.Trigger) + " → " + binding.Command + ".";

    public static string RemoveQuestion(MidiBinding binding) => "Remove " + Describe(binding.Trigger) + " → " + binding.Command + "?";

    /// <summary>A typed command that isn't one.</summary>
    public static string NotCommand(string typed) => "\"" + typed + "\" isn't a command: start with / and name one /help lists.";

    public const string Kept = "Left as it was.";

    /// <summary>The verbs' completion notes.</summary>
    public static string VerbHint(string verb) => verb switch
    {
        MidiCommand.LearnWord => "press a button, then pick its command",
        MidiCommand.ListWord => "the bindings",
        MidiCommand.OnWord => "listen to the bound devices",
        MidiCommand.OffWord => "let the devices go",
        MidiCommand.ForgetWord => "remove a binding by its number",
        _ => "",
    };

    // ----- --midi-check -----

    public const string CheckNone = "No MIDI input found.";

    public static string CheckInputs(int count) => count.ToString(CultureInfo.InvariantCulture) + " MIDI input" + (count == 1 ? "" : "s") + ":";

    public static string CheckInput(int index, string name, string? problem) =>
        "  " + index.ToString(CultureInfo.InvariantCulture) + ". " + name + (problem is null ? "" : " (" + problem + ")");

    public const string CheckListening = "Listening: press the buttons (Ctrl+C or 60 seconds ends it).";

    public static string CheckHeard(string device, ReadOnlySpan<byte> raw, IReadOnlyList<string> decoded)
    {
        string bytes = Convert.ToHexString(raw);
        var spaced = string.Join(' ', Enumerable.Range(0, bytes.Length / 2).Select(i => bytes.Substring(i * 2, 2)));
        return "  [" + device + "] " + spaced + (decoded.Count > 0 ? "  →  " + string.Join("; ", decoded) : "");
    }

    public const string CheckDone = "MIDI check done.";
}
