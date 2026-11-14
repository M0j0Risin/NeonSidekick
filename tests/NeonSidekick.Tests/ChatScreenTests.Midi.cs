using NeonSidekick.App;
using NeonSidekick.Midi;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// MIDI control on the screen (2026-11-12, the MIDI plan), over <see cref="FakeMidiSystem"/>'s FS-4 pedal: a press runs its command at the
/// idle line with the 🎹 flash; a binding whose command is gone is one error line; under a reply a quick command runs at once and another
/// waits for the reply's end; <c>/midi list|on|off|forget</c>; the learn picks a command, takes its arguments and saves, turning
/// <i>MIDI input</i> on; and a press while the MIDI pane is open only shows what it sends.
/// </summary>
public partial class ChatScreenTests
{
    private static readonly byte[] MidiDown80 = [0xB0, 80, 127];
    private static readonly byte[] MidiDown81 = [0xB0, 81, 127];

    /// <summary>The fake MIDI system the chat screen gets; null (the default) is a screen with none.</summary>
    private FakeMidiSystem? _midiSystem;

    private static readonly MidiTrigger Fs80 = new("FS-4", 0, MidiKind.ControlChange, 1, 80);
    private static readonly MidiTrigger Fs81 = new("FS-4", 0, MidiKind.ControlChange, 1, 81);

    private FakeMidiSystem MidiFixture(bool pane, bool on = true)
    {
        _midiSystem = new FakeMidiSystem("FS-4");
        _settings.Update(d => { d.TtsOutput = false; d.ToolbarItems = []; d.PerformanceBarItems = []; d.MidiInput = on; });
        if (pane)
        {
            _console.Profile.Height = 40;
            _console.Profile.Width = 120;
            _geometry = new ScreenGeometry(() => null);
        }
        else
        {
            _console.Profile.Width = 240;
        }

        return _midiSystem;
    }

    private void MidiBind(MidiTrigger trigger, string command) => new MidiBindings(_settings.ProfileDirectory).Bind(trigger, command);

    /// <summary>A press once the pedal is open, then the screen's taking of its line waited for (never a send before the open: the mic tests' flake).</summary>
    private void Press(FakeMidiSystem fake, byte[] bytes)
    {
        Until(() => fake.IsOpen("FS-4"));
        Assert.True(fake.Send("FS-4", bytes));
        Until(() => _keys?.PendingCommands == 0);
    }

    [Fact]
    public async Task Midi_APress_RunsItsCommandAtTheIdleLine_AndAGoneCommandIsAnErrorLine()
    {
        var fake = MidiFixture(pane: false);
        MidiBind(Fs80, "/echo the pedal spoke");
        MidiBind(Fs81, "/no-such-command");
        GatedSteps(
            Now(_ => Press(fake, MidiDown80)),   // the wait for the open inside the step: a gate is judged once a read (ScriptedInput.OnWait)
            Shown("the pedal spoke", _ => Assert.True(fake.Send("FS-4", MidiDown81))),
            Shown(MidiText.NotACommand("/no-such-command"), Line("/exit")));

        string output = await RunAsync();

        Assert.Contains("the pedal spoke", output);
        Assert.Contains(MidiText.NotACommand("/no-such-command"), output);
        Assert.Empty(_chat.Requests);
    }

    [Fact]
    public async Task Midi_UnderAReply_AQuickCommandRunsAtOnce_AndAnotherWaitsForTheEnd()
    {
        var fake = MidiFixture(pane: true);   // under a reply, lines are read only with the pane (as typed ones are)
        MidiBind(Fs80, "/midi list");
        MidiBind(Fs81, "/echo after the reply");
        _chat.EnqueueText("One. ", "Two.");
        _chat.BeforeUpdate = async (i, _) =>
        {
            if (i == 1 && _chat.Requests.Count == 1)
            {
                Press(fake, MidiDown80);
                Press(fake, MidiDown81);
                await Task.Delay(50);
            }
        };
        LinesWhenIdle("tell me something", "/exit");

        string output = await RunAsync();

        Assert.Contains(MidiText.ListLine(1, new MidiBinding(Fs80, "/midi list")), output);
        Assert.Contains(ChatScreen.MidTurnDeferredNotice("/echo"), output);
        Assert.True(output.IndexOf("Two.", StringComparison.Ordinal) < output.LastIndexOf("after the reply", StringComparison.Ordinal));
        Assert.DoesNotContain(ChatScreen.CancelledNotice, output);
    }

    [Fact]
    public async Task Midi_ListOnOffForget_WithoutThePane()
    {
        MidiFixture(pane: false, on: false);
        MidiBind(Fs80, "/tts toggle");
        MidiBind(Fs81, "/voice");
        PushLine("/midi list");
        PushLine("/midi on");
        PushLine("/midi on");
        PushLine("/midi forget 3");
        PushLine("/midi forget 1");
        PushLine("/midi off");
        PushLine("/midi sideways");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains("1. CC 80 · ch 1 · FS-4 → /tts toggle", output);
        Assert.Contains("2. CC 81 · ch 1 · FS-4 → /voice", output);
        Assert.Contains(MidiText.OffWithBindings, output);
        Assert.Contains(MidiText.TurnedOn, output);
        Assert.Contains(MidiText.AlreadyOn, output);
        Assert.Contains(MidiText.ForgetUsage(2), output);
        Assert.Contains(MidiText.Forgot(1, new MidiBinding(Fs80, "/tts toggle")), output);
        Assert.Contains(MidiText.TurnedOff, output);
        Assert.Contains(MidiText.Usage, output);
        Assert.False(_settings.Current.MidiInput);
        Assert.Equal(["/voice"], new MidiBindings(_settings.ProfileDirectory).Load().Bindings.Select(b => b.Command));
    }

    [Fact]
    public async Task Midi_WithoutABackend_SaysSo()
    {
        PushLine("/midi");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(MidiText.NoneHere, output);
    }

    [Fact]
    public async Task Midi_Learn_HearsAPress_PicksTheCommand_TakesItsArguments_AndSaves_TurningTheInputOn()
    {
        var fake = MidiFixture(pane: true, on: false);
        GatedSteps(
            Now(Line("/midi learn")),
            Shown(MidiText.LearnWaiting, _ => Assert.True(SendWhenOpen(fake, MidiDown80))),
            Shown(MidiText.PickTitle(Fs80), input => input.Push(Keys.Char('/'), Keys.Char('t'), Keys.Char('t'), Keys.Char('s'))),
            Shown("Filter: /tts", input => input.Push(Keys.Enter)),
            Shown(MidiText.ArgsTitle(Fs80), Line("toggle")),
            Shown(MidiText.BindingsTab, input => input.Push(Keys.Escape)),
            Now(Line("/exit")));

        string output = await RunAsync();

        Assert.Equal([new MidiBinding(Fs80, "/tts toggle")], new MidiBindings(_settings.ProfileDirectory).Load().Bindings);
        Assert.True(_settings.Current.MidiInput);
        Assert.Contains(MidiText.TurnedOnToo, output);
        Assert.DoesNotContain(MidiText.Ran("/tts toggle"), output);   // the learn's press ran nothing
    }

    [Fact]
    public async Task Midi_APressWhileThePaneIsOpen_OnlyShowsWhatItSends()
    {
        var fake = MidiFixture(pane: true);
        MidiBind(Fs80, "/echo zebra");
        GatedSteps(
            Now(input => { Until(() => fake.IsOpen("FS-4")); Line("/midi")(input); }),
            Shown(MidiText.BindingsTab, input =>
            {
                Assert.True(fake.Send("FS-4", MidiDown80));
                input.Push(Keys.Right);   // the Devices tab
            }),
            Shown("Last: CC 80 · ch 1 · value 127 · FS-4 · press", input => input.Push(Keys.Escape)),
            Now(Line("/exit")));

        string output = await RunAsync();

        Assert.DoesNotContain(MidiText.Ran("/echo zebra"), output);
        Assert.Equal(0, _keys?.PendingCommands ?? 0);
    }

    /// <summary>
    /// A hand edit between the board's read and the pane's Del (2026-11-14, the review's #9): whichever binding the pane shows on its first
    /// row, the one removed is the one it named, found by its button in the file as it is now.
    /// </summary>
    [Fact]
    public async Task Midi_ThePanesDel_AfterAHandEdit_RemovesTheBindingItNamed()
    {
        var fake = MidiFixture(pane: true);
        MidiBind(Fs80, "/tts toggle");
        MidiBind(Fs81, "/voice");
        GatedSteps(
            Now(input =>
            {
                Until(() => fake.IsOpen("FS-4"));
                // By hand: the two swapped, so an index-keyed remove of the first row would take the other one.
                File.WriteAllText(Settings.Profiles.ConfigPath(_settings.ProfileDirectory, MidiBindings.FileName), """
                    { "version": 1, "bindings": [
                      { "device": "FS-4", "kind": "cc", "channel": 1, "number": 81, "command": "/voice" },
                      { "device": "FS-4", "kind": "cc", "channel": 1, "number": 80, "command": "/tts toggle" } ] }
                    """);
                Line("/midi")(input);
            }),
            Shown(MidiText.BindingsTab, input => input.Push(Keys.Delete)),
            Shown("Remove ", input => input.Push(Keys.Down, Keys.Enter)),
            Shown("Removed ", input => input.Push(Keys.Escape)),
            Now(Line("/exit")));

        string output = await RunAsync();

        var left = Assert.Single(new MidiBindings(_settings.ProfileDirectory).Load().Bindings);
        var removed = left.Trigger.SameButton(Fs80) ? new MidiBinding(Fs81, "/voice") : new MidiBinding(Fs80, "/tts toggle");
        Assert.Contains(MidiText.Removed(removed), output);
    }

    /// <summary><c>/midi forget</c> on a broken file names it, never "no bindings" (2026-11-14, the review's #59), and leaves it alone.</summary>
    [Fact]
    public async Task Midi_ForgetOnABrokenFile_NamesIt()
    {
        MidiFixture(pane: false, on: false);
        string path = Settings.Profiles.ConfigPath(_settings.ProfileDirectory, MidiBindings.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not json");
        PushLine("/midi forget 1");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(MidiText.BrokenFile, output);
        Assert.DoesNotContain(MidiText.ForgetUsage(0), output);
        Assert.Equal("{ not json", File.ReadAllText(path));
    }

    // A send once the input is open (the learn opens it on the pool).
    private static bool SendWhenOpen(FakeMidiSystem fake, byte[] bytes)
    {
        Until(() => fake.IsOpen("FS-4"));
        return fake.Send("FS-4", bytes);
    }
}
