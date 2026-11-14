using NeonSidekick.App;
using NeonSidekick.Midi;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// The MIDI core (2026-11-12, the MIDI plan): <see cref="MidiParser"/> (running status, a Note On at velocity 0, realtime bytes between a
/// message's data, SysEx skipped whole, a message cut across two packets, a Program Change's one data byte), <see cref="MidiEdge"/> (a
/// momentary pedal, a latching one, a knob swept past halfway, an input forgotten), <see cref="MidiNames"/> (Windows' "2- " prefix, the
/// occurrences of identical devices), <see cref="MidiCommand"/>'s forms and completion, and <see cref="MidiText.Describe"/>.
/// </summary>
public sealed class MidiCoreTests
{
    private static List<MidiMessage> Parse(params byte[][] packets)
    {
        var parser = new MidiParser();
        var into = new List<MidiMessage>();
        foreach (var packet in packets)
        {
            parser.Feed(packet, into);
        }

        return into;
    }

    [Fact]
    public void Parser_ReadsANoteOn_ACc_AndAProgramChange_WithTheirChannels()
    {
        var messages = Parse([0x90, 60, 100], [0xB3, 80, 127], [0xC0, 5]);
        Assert.Equal(
        [
            new MidiMessage(MidiKind.Note, 1, 60, 100),
            new MidiMessage(MidiKind.ControlChange, 4, 80, 127),
            new MidiMessage(MidiKind.ProgramChange, 1, 5, 0),
        ], messages);
    }

    [Fact]
    public void Parser_FollowsRunningStatus_AndDropsANoteOnAtVelocityZero_AndNoteOffs()
    {
        var messages = Parse([0x90, 60, 100, 62, 90, 60, 0], [0x80, 62, 64], [0xB0, 1, 10, 1, 20]);
        Assert.Equal(
        [
            new MidiMessage(MidiKind.Note, 1, 60, 100),
            new MidiMessage(MidiKind.Note, 1, 62, 90),
            new MidiMessage(MidiKind.ControlChange, 1, 1, 10),
            new MidiMessage(MidiKind.ControlChange, 1, 1, 20),
        ], messages);
    }

    [Fact]
    public void Parser_SkipsRealtimeBytes_EvenBetweenAMessagesDataBytes()
    {
        var messages = Parse([0xF8, 0xB0, 0xF8, 80, 0xFE, 127, 0xFA]);
        Assert.Equal([new MidiMessage(MidiKind.ControlChange, 1, 80, 127)], messages);
    }

    [Fact]
    public void Parser_SkipsSysExWhole_AndItEndsTheRunningStatus()
    {
        var messages = Parse([0xB0, 80, 127, 0xF0, 0x7E, 0x00, 0x06, 0x01, 0xF7, 80, 0], [0xC1, 3]);
        Assert.Equal([new MidiMessage(MidiKind.ControlChange, 1, 80, 127), new MidiMessage(MidiKind.ProgramChange, 2, 3, 0)], messages);
    }

    [Fact]
    public void Parser_CompletesAMessageCutAcrossTwoPackets_AndReadsPastPitchBendAndAftertouch()
    {
        var messages = Parse([0x90, 60], [100], [0xE0, 0, 64, 0xD0, 33, 0xA0, 60, 10], [0x99, 36, 127]);
        Assert.Equal([new MidiMessage(MidiKind.Note, 1, 60, 100), new MidiMessage(MidiKind.Note, 10, 36, 127)], messages);
    }

    [Fact]
    public void Parser_ProgramChange_UnderRunningStatus_TakesOneDataByteEach()
    {
        var messages = Parse([0xC0, 1, 2, 3]);
        Assert.Equal([1, 2, 3], messages.Select(m => m.Number));
    }

    [Fact]
    public void Edge_AMomentaryPedal_FiresOnceAPress()
    {
        var edge = new MidiEdge();
        var down = new MidiMessage(MidiKind.ControlChange, 1, 80, 127);
        var up = down with { Value = 0 };
        Assert.True(edge.Fires("pedal", down));
        Assert.False(edge.Fires("pedal", up));
        Assert.True(edge.Fires("pedal", down));
        Assert.False(edge.Fires("pedal", down));   // a repeat while held
    }

    [Fact]
    public void Edge_AKnobSwept_FiresOncePastHalfway_AndEachInputAndControllerIsItsOwn()
    {
        var edge = new MidiEdge();
        bool[] fired = [.. Enumerable.Range(0, 128).Select(v => edge.Fires("a", new MidiMessage(MidiKind.ControlChange, 1, 7, v)))];
        Assert.Equal(1, fired.Count(f => f));
        Assert.True(fired[MidiEdge.Threshold]);
        Assert.True(edge.Fires("b", new MidiMessage(MidiKind.ControlChange, 1, 7, 127)));
        Assert.True(edge.Fires("a", new MidiMessage(MidiKind.ControlChange, 2, 7, 127)));
    }

    [Fact]
    public void Edge_NotesAndProgramsAlwaysFire_AndForgetStartsAnInputFresh()
    {
        var edge = new MidiEdge();
        var note = new MidiMessage(MidiKind.Note, 1, 60, 100);
        Assert.True(edge.Fires("a", note));
        Assert.True(edge.Fires("a", note));
        Assert.True(edge.Fires("a", new MidiMessage(MidiKind.ProgramChange, 1, 2, 0)));
        var cc = new MidiMessage(MidiKind.ControlChange, 1, 64, 127);
        Assert.True(edge.Fires("a", cc));
        Assert.False(edge.Fires("a", cc));
        edge.Forget("a");
        Assert.True(edge.Fires("a", cc));
    }

    [Theory]
    [InlineData("FS-4", "FS-4")]
    [InlineData("2- FS-4", "FS-4")]
    [InlineData("  12- FS-4 ", "FS-4")]
    [InlineData("1234- FS-4", "1234- FS-4")]
    [InlineData("X- FS-4", "X- FS-4")]
    [InlineData("2 - STARRYPAD MINI", "STARRYPAD MINI")]
    [InlineData("12  -  Pad", "Pad")]
    [InlineData("MIDIIN2 (3 - STARRYPAD MINI)", "MIDIIN2 (STARRYPAD MINI)")]
    [InlineData("MIDIIN2 (STARRYPAD MINI)", "MIDIIN2 (STARRYPAD MINI)")]
    [InlineData("4 - MIDIIN3 (4 - PAD)", "MIDIIN3 (PAD)")]
    [InlineData("2 -Pad", "2 -Pad")]
    [InlineData("(2 - Pad)", "(2 - Pad)")]
    [InlineData(null, "")]
    public void Names_TakeOffWindowsPrefix(string? name, string expected) => Assert.Equal(expected, MidiNames.Normalize(name));

    [Fact]
    public void Names_Occurrences_CountIdenticalDevicesInOrder()
    {
        var inputs = new[] { new MidiInputInfo(0, "Pedal"), new MidiInputInfo(1, "Pad"), new MidiInputInfo(2, "2- Pedal"), new MidiInputInfo(3, "pedal") };
        Assert.Equal([0, 0, 1, 2], MidiNames.Occurrences(inputs));
        Assert.True(MidiNames.Same("2- PEDAL", "Pedal"));
    }

    [Fact]
    public void Trigger_SameButton_IgnoresTheNamesCaseAndPrefix_ButNotTheOccurrence()
    {
        var a = new MidiTrigger("FS-4", 0, MidiKind.ControlChange, 1, 80);
        Assert.True(a.SameButton(a with { Device = "2- fs-4" }));
        Assert.False(a.SameButton(a with { Occurrence = 1 }));
        Assert.False(a.SameButton(a with { Kind = MidiKind.Note }));
    }

    [Fact]
    public void Describe_SaysTheMessageChannelAndDevice()
    {
        Assert.Equal("CC 80 · ch 1 · FS-4", MidiText.Describe(new MidiTrigger("FS-4", 0, MidiKind.ControlChange, 1, 80)));
        Assert.Equal("Note 36 · ch 10 · Pad #2", MidiText.Describe(new MidiTrigger("Pad", 1, MidiKind.Note, 10, 36)));
        Assert.Equal("Program 3 · ch 2 · Pedal", MidiText.Describe(new MidiTrigger("Pedal", 0, MidiKind.ProgramChange, 2, 3)));
    }

    [Theory]
    [InlineData("", MidiVerb.Pane, null, 0)]
    [InlineData("learn", MidiVerb.Learn, null, 0)]
    [InlineData("LEARN /tts toggle", MidiVerb.Learn, "/tts toggle", 0)]
    [InlineData("list", MidiVerb.List, null, 0)]
    [InlineData(" on ", MidiVerb.On, null, 0)]
    [InlineData("off", MidiVerb.Off, null, 0)]
    [InlineData("forget 2", MidiVerb.Forget, null, 2)]
    [InlineData("forget", MidiVerb.Forget, null, 0)]
    public void Command_ParsesItsForms(string args, MidiVerb verb, string? command, int number)
    {
        var parsed = MidiCommand.Parse(args);
        Assert.NotNull(parsed);
        Assert.Equal(new MidiArgs(verb, command, number), parsed);
    }

    [Theory]
    [InlineData("sideways")]
    [InlineData("list now")]
    [InlineData("forget two")]
    [InlineData("forget 0")]
    [InlineData("forget -1")]
    public void Command_RefusesWhatItDoesNotTake(string args) => Assert.Null(MidiCommand.Parse(args));

    [Fact]
    public void Command_Completes_TheVerbs_TheCommandsAfterLearn_AndTheNumbersAfterForget()
    {
        UI.CompletionItem[] commands = [new("/tts", "speech"), new("/midi", "this"), new("/timer", "timers")];
        var bindings = new[] { new MidiBinding(new MidiTrigger("FS-4", 0, MidiKind.ControlChange, 1, 80), "/tts toggle") };
        Assert.Equal(["learn", "list"], MidiCommand.Complete("l", commands, bindings).Select(i => i.Text));
        Assert.Equal(["learn /tts", "learn /timer"], MidiCommand.Complete("learn /t", commands, bindings).Select(i => i.Text));
        Assert.Empty(MidiCommand.Complete("learn /tts on", commands, bindings));
        Assert.DoesNotContain(MidiCommand.Complete("learn ", commands, bindings), i => i.Text == "learn /midi");
        var forget = Assert.Single(MidiCommand.Complete("forget ", commands, bindings));
        Assert.Equal("forget 1", forget.Text);
        Assert.Equal("CC 80 · ch 1 · FS-4 → /tts toggle", forget.Note);
    }

    [Fact]
    public void MidTurn_ListOnOffForget_RunAtOnce_ThePaneAndTheLearnOpenOverTheReply()
    {
        foreach (string args in new[] { "list", "on", "OFF", "forget 1", "forget", "sideways" })
        {
            Assert.Equal(MidTurnClass.Quick, ChatScreen.MidTurnPolicy(SlashCommand.Midi, args));
        }

        foreach (string args in new[] { "", "learn", "learn /tts toggle" })
        {
            Assert.Equal(MidTurnClass.Pane, ChatScreen.MidTurnPolicy(SlashCommand.Midi, args));
        }

        Assert.Equal(MidTurnClass.Pane, ChatScreen.MidTurnPolicy(SlashCommand.Midi, hasArgs: false));
        Assert.Equal(MidTurnClass.Quick, ChatScreen.MidTurnPolicy(SlashCommand.Midi, hasArgs: true));
        Assert.Equal(SlashCommand.Midi, SlashCommands.Parse("/midi learn /tts").Command);
    }

    [Fact]
    public void TheWebUi_RefusesEveryForm_AsThePcs()
    {
        var on = new WebCommandSettings(Commands: true, FileTools: true, ObsidianTools: true);
        foreach (string line in new[] { "/midi", "/midi list", "/midi on", "/midi learn /tts" })
        {
            Assert.Equal(WebUiText.PcOnly("/midi"), WebCommands.Check(line, on).Reason);
        }

        Assert.Contains(SlashCommand.Midi, WebCommands.Never);
    }
}
