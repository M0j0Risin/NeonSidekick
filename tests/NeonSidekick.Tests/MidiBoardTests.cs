using NeonSidekick.Midi;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// The board (2026-11-12, the MIDI plan, <see cref="MidiBoard"/>) over <see cref="FakeMidiSystem"/>, its work run inline: only the bound inputs
/// open, and only while <i>MIDI input</i> is on; a press runs its binding's line and a CC's release doesn't; the pane's hold shows and never
/// runs; a learn hears every input and gives the inputs back after; an input another app holds is listed as in use and said once; a pedal
/// plugged in later or pulled out; a profile switch; two identical pedals told apart.
/// </summary>
public sealed class MidiBoardTests : IDisposable
{
    private static readonly byte[] Down = [0xB0, 80, 127];
    private static readonly byte[] Up = [0xB0, 80, 0];

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();
    private readonly List<string> _lines = [];
    private readonly List<string> _notices = [];

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string Profile(string name = "default") => Path.Combine(_dir, name);

    private MidiBoard Board(FakeMidiSystem fake) =>
        new(fake, line => { lock (_lines) { _lines.Add(line); } }, _notices.Add, _time, run: work => work());

    private void Bind(MidiTrigger trigger, string command, string profile = "default") => new MidiBindings(Profile(profile)).Bind(trigger, command);

    [Fact]
    public void Off_OpensNothing_AndListsNothing()
    {
        var fake = new FakeMidiSystem("FS-4");
        Bind(new MidiTrigger("FS-4", 0, MidiKind.ControlChange, 1, 80), "/tts");
        using var board = Board(fake);
        board.Apply(false, Profile());
        _time.Advance(MidiBoard.RescanEvery * 3);
        Assert.Empty(fake.Opened);
        Assert.Equal(0, fake.Listed);
        Assert.Single(board.Bindings.Bindings);
    }

    [Fact]
    public void On_OpensOnlyTheBoundInputs_APressRunsItsLine_AndTheReleaseDoesNot()
    {
        var fake = new FakeMidiSystem("Keys", "2- FS-4");
        Bind(new MidiTrigger("FS-4", 0, MidiKind.ControlChange, 1, 80), "/tts toggle");
        using var board = Board(fake);
        long before = board.Version;
        board.Apply(true, Profile());

        Assert.Equal(["2- FS-4"], fake.Opened);
        Assert.True(board.Version > before);
        Assert.Equal([("Keys", false), ("FS-4", true)], board.Inputs.Select(i => (i.Name, i.Open)));
        Assert.True(fake.Send("2- FS-4", Down));
        Assert.True(fake.Send("2- FS-4", Up));
        Assert.True(fake.Send("2- FS-4", [.. Down, 0xF8, .. Down]));   // held: a repeat is no press
        Assert.True(fake.Send("2- FS-4", [0xB0, 81, 127]));   // a button nothing binds
        Assert.Equal(["/tts toggle", "/tts toggle"], _lines);
        Assert.Equal(new MidiSeen(new MidiTrigger("FS-4", 0, MidiKind.ControlChange, 1, 81), 127, true), board.LastSeen);

        board.Apply(false, Profile());
        Assert.Equal(["2- FS-4"], fake.Closed);
        Assert.Empty(board.Inputs);
    }

    [Fact]
    public void Held_ShowsWhatIsHeard_AndRunsNothing_UntilTheLeaseGoes()
    {
        var fake = new FakeMidiSystem("FS-4");
        Bind(new MidiTrigger("FS-4", 0, MidiKind.Note, 1, 60), "/tts");
        using var board = Board(fake);
        board.Apply(true, Profile());
        var lease = board.Hold();
        Assert.True(board.Held);
        fake.Send("FS-4", [0x90, 60, 100]);
        Assert.Empty(_lines);
        Assert.Equal(60, board.LastSeen?.Trigger.Number);

        lease.Dispose();
        lease.Dispose();
        Assert.False(board.Held);
        fake.Send("FS-4", [0x90, 60, 100]);
        Assert.Equal(["/tts"], _lines);
    }

    [Fact]
    public void Held_WithMidiOff_ListsTheInputs_ForTheDevicesTab()
    {
        var fake = new FakeMidiSystem("FS-4", "Keys");
        using var board = Board(fake);
        board.Apply(false, Profile());
        using (board.Hold())
        {
            Assert.Equal(["FS-4", "Keys"], board.Inputs.Select(i => i.Name));
            Assert.Empty(fake.Opened);
        }

        Assert.Empty(board.Inputs);
    }

    [Fact]
    public async Task Learn_HearsEveryInput_TakesTheFirstPress_AndGivesTheExtraInputsBack()
    {
        var fake = new FakeMidiSystem("FS-4", "Keys");
        Bind(new MidiTrigger("FS-4", 0, MidiKind.ControlChange, 1, 80), "/tts");
        using var board = Board(fake);
        board.Apply(true, Profile());
        Assert.Equal(["FS-4"], fake.Opened);

        var learning = board.LearnAsync(CancellationToken.None);
        Assert.True(board.Learning);
        Assert.Equal(["FS-4", "Keys"], fake.Opened);
        fake.Send("Keys", [0xB0, 7, 20]);   // below the edge: not a press
        fake.Send("Keys", [0x80, 60, 0]);   // a Note Off
        fake.Send("Keys", [0x90, 60, 100]);
        var trigger = await learning.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(new MidiTrigger("Keys", 0, MidiKind.Note, 1, 60), trigger);
        Assert.Empty(_lines);   // a learn's press runs nothing

        SpinWait.SpinUntil(() => fake.Closed.Contains("Keys"), TimeSpan.FromSeconds(10));
        Assert.Equal(["Keys"], fake.Closed);
        Assert.True(fake.IsOpen("FS-4"));
        Assert.False(board.Learning);
    }

    [Fact]
    public async Task Learn_Cancelled_IsNull_AndAnInputAnotherAppHolds_IsListedAsInUse()
    {
        var fake = new FakeMidiSystem("FS-4", "DAW port");
        fake.Held.Add("DAW port");
        using var board = Board(fake);
        board.Apply(false, Profile());
        using var cts = new CancellationTokenSource();
        var learning = board.LearnAsync(cts.Token);
        var held = Assert.Single(board.Inputs, i => i.Name == "DAW port");
        Assert.True(held.InUse);
        Assert.False(held.Open);
        Assert.Empty(_notices);   // nothing bound there: nothing said

        await cts.CancelAsync();
        Assert.Null(await learning.WaitAsync(TimeSpan.FromSeconds(10)));
        SpinWait.SpinUntil(() => fake.Closed.Contains("FS-4"), TimeSpan.FromSeconds(10));
        Assert.Equal(["FS-4"], fake.Closed);
    }

    [Fact]
    public void ABoundInputAnotherAppHolds_IsSaidOnce_AndOpensWhenFreed()
    {
        var fake = new FakeMidiSystem("FS-4");
        fake.Held.Add("FS-4");
        Bind(new MidiTrigger("FS-4", 0, MidiKind.ControlChange, 1, 80), "/tts");
        using var board = Board(fake);
        board.Apply(true, Profile());
        _time.Advance(MidiBoard.RescanEvery);
        _time.Advance(MidiBoard.RescanEvery);
        Assert.Equal([MidiText.OpenFailedNotice("FS-4", MidiText.InUse)], _notices);

        fake.Held.Clear();
        _time.Advance(MidiBoard.RescanEvery);
        Assert.Equal(["FS-4"], fake.Opened);
    }

    /// <summary>Turned off and on, an input that still can't open is said again (2026-11-14, the review's #18).</summary>
    [Fact]
    public void ABoundInputAnotherAppHolds_IsSaidAgain_AfterMidiInputOffAndOn()
    {
        var fake = new FakeMidiSystem("FS-4");
        fake.Held.Add("FS-4");
        Bind(new MidiTrigger("FS-4", 0, MidiKind.ControlChange, 1, 80), "/tts");
        using var board = Board(fake);
        board.Apply(true, Profile());
        _time.Advance(MidiBoard.RescanEvery);
        Assert.Single(_notices);

        board.Apply(false, Profile());
        board.Apply(true, Profile());
        _time.Advance(MidiBoard.RescanEvery);

        Assert.Equal([MidiText.OpenFailedNotice("FS-4", MidiText.InUse), MidiText.OpenFailedNotice("FS-4", MidiText.InUse)], _notices);
    }

    [Fact]
    public void APedalPluggedInLater_IsOpenedByTheRescan_AndOnePulledOut_IsClosed()
    {
        var fake = new FakeMidiSystem("Keys");
        Bind(new MidiTrigger("FS-4", 0, MidiKind.ControlChange, 1, 80), "/tts");
        using var board = Board(fake);
        board.Apply(true, Profile());
        Assert.Empty(fake.Opened);

        fake.Plug("FS-4");
        _time.Advance(MidiBoard.RescanEvery);
        Assert.Equal(["FS-4"], fake.Opened);
        fake.Send("FS-4", Down);

        fake.Unplug("FS-4");
        _time.Advance(MidiBoard.RescanEvery);
        Assert.Equal(["FS-4"], fake.Closed);

        // Back in: a fresh edge, so a held-down pedal's next press still fires.
        fake.Plug("FS-4");
        _time.Advance(MidiBoard.RescanEvery);
        fake.Send("FS-4", Down);
        Assert.Equal(["/tts", "/tts"], _lines);
    }

    [Fact]
    public void AProfileSwitch_ReadsItsBindings_AndOpensItsInputs()
    {
        var fake = new FakeMidiSystem("FS-4", "Pad");
        Bind(new MidiTrigger("FS-4", 0, MidiKind.ControlChange, 1, 80), "/tts");
        Bind(new MidiTrigger("Pad", 0, MidiKind.Note, 10, 36), "/camera snap", "work");
        using var board = Board(fake);
        board.Apply(true, Profile());
        board.Apply(true, Profile("work"));
        Assert.Equal(["FS-4", "Pad"], fake.Opened);
        Assert.Equal(["FS-4"], fake.Closed);
        fake.Send("Pad", [0x99, 36, 90]);
        Assert.Equal(["/camera snap"], _lines);

        // A save from the pane: read again.
        Bind(new MidiTrigger("Pad", 0, MidiKind.Note, 10, 37), "/clear", "work");
        board.Reload();
        fake.Send("Pad", [0x99, 37, 90]);
        Assert.Equal(["/camera snap", "/clear"], _lines);
    }

    [Fact]
    public void TwoIdenticalPedals_AreToldApartByOccurrence()
    {
        var fake = new FakeMidiSystem("FS-4", "2- FS-4");
        Bind(new MidiTrigger("FS-4", 0, MidiKind.ControlChange, 1, 80), "/tts");
        Bind(new MidiTrigger("FS-4", 1, MidiKind.ControlChange, 1, 80), "/voice");
        using var board = Board(fake);
        board.Apply(true, Profile());
        fake.Send("2- FS-4", Down);
        fake.Send("FS-4", Down);
        Assert.Equal(["/voice", "/tts"], _lines);
    }

    /// <summary>A twin plugged in after the first pedal opened makes the first match by its own occurrence (2026-11-14, the review's #1).</summary>
    [Fact]
    public void ATwinPluggedInLater_MakesTheFirstPedalMatchItsOwnOccurrence()
    {
        var fake = new FakeMidiSystem("FS-4");
        Bind(new MidiTrigger("FS-4", 1, MidiKind.ControlChange, 1, 80), "/voice");   // listed first: a lone pedal would take it
        Bind(new MidiTrigger("FS-4", 0, MidiKind.ControlChange, 1, 80), "/tts");
        using var board = Board(fake);
        board.Apply(true, Profile());
        fake.Send("FS-4", Down);
        Assert.Equal(["/voice"], _lines);   // alone, the first binding of its button
        _lines.Clear();

        fake.Plug("2- FS-4");
        _time.Advance(MidiBoard.RescanEvery);
        fake.Send("FS-4", Up);
        fake.Send("FS-4", Down);

        Assert.Equal(["/tts"], _lines);   // two of them now: occurrence 0's own
        Assert.Equal(["FS-4", "2- FS-4"], fake.Opened);   // the first never reopened
    }

    /// <summary>
    /// A pad replugged comes back under Windows' next number (2026-11-14, the user's STARRYPAD MINI: "2 - ", then "3 - "): a binding learned on
    /// the old name still fires, after a slow replug (gone a rescan, then back at the end of the list) and a quick one (renamed in place
    /// within one rescan, the review's #8: reopened, not kept on the dead handle).
    /// </summary>
    [Fact]
    public void APadReplugged_UnderWindowsNextNumber_KeepsItsBinding_SlowOrQuick()
    {
        var fake = new FakeMidiSystem("Keys", "2 - STARRYPAD MINI");
        Bind(new MidiTrigger("2 - STARRYPAD MINI", 0, MidiKind.ControlChange, 1, 80), "/tts");
        using var board = Board(fake);
        board.Apply(true, Profile());

        fake.Unplug("2 - STARRYPAD MINI");
        _time.Advance(MidiBoard.RescanEvery);
        fake.Plug("3 - STARRYPAD MINI");
        _time.Advance(MidiBoard.RescanEvery);
        Assert.True(fake.Send("3 - STARRYPAD MINI", Down));
        fake.Send("3 - STARRYPAD MINI", Up);

        fake.Replug("3 - STARRYPAD MINI", "4 - STARRYPAD MINI");
        _time.Advance(MidiBoard.RescanEvery);
        Assert.True(fake.Send("4 - STARRYPAD MINI", Down));

        Assert.Equal(["/tts", "/tts"], _lines);
        Assert.Equal(["2 - STARRYPAD MINI", "3 - STARRYPAD MINI", "4 - STARRYPAD MINI"], fake.Opened);
        Assert.Equal(["2 - STARRYPAD MINI", "3 - STARRYPAD MINI"], fake.Closed);
    }

    /// <summary>Its twin pulled out, the pedal left alone matches whatever its occurrence again (2026-11-14, the review's #1).</summary>
    [Fact]
    public void ATwinPulledOut_LeavesTheOtherMatchingWhateverItsOccurrence()
    {
        var fake = new FakeMidiSystem("FS-4", "2- FS-4");
        Bind(new MidiTrigger("FS-4", 1, MidiKind.ControlChange, 1, 80), "/voice");
        using var board = Board(fake);
        board.Apply(true, Profile());
        fake.Send("FS-4", Down);
        Assert.Empty(_lines);   // occurrence 0 has no binding while its twin is there

        fake.Unplug("FS-4");
        _time.Advance(MidiBoard.RescanEvery);
        fake.Send("2- FS-4", Up);
        fake.Send("2- FS-4", Down);

        Assert.Equal(["/voice"], _lines);
    }

    [Fact]
    public void ALonePedal_MatchesWhateverItsOccurrence()
    {
        var set = new MidiBindingSet([new MidiBinding(new MidiTrigger("FS-4", 1, MidiKind.ControlChange, 1, 80), "/tts")], [], MidiFileState.Ok);
        var pressed = new MidiTrigger("fs-4", 0, MidiKind.ControlChange, 1, 80);
        Assert.Equal("/tts", MidiBoard.BindingFor(set, pressed, unique: true)?.Command);
        Assert.Null(MidiBoard.BindingFor(set, pressed, unique: false));
        Assert.True(MidiBoard.Names(set, "FS-4", 0, sameNamed: 1));
        Assert.False(MidiBoard.Names(set, "FS-4", 0, sameNamed: 2));
    }

    [Fact]
    public async Task Dispose_ClosesEveryInput_AndEndsALearn()
    {
        var fake = new FakeMidiSystem("FS-4");
        var board = Board(fake);
        board.Apply(false, Profile());
        var learning = board.LearnAsync(CancellationToken.None);
        Assert.True(fake.IsOpen("FS-4"));
        board.Dispose();
        board.Dispose();
        Assert.Null(await learning.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.False(fake.IsOpen("FS-4"));
    }
}
