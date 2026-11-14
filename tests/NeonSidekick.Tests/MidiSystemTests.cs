using NeonSidekick.Midi;

namespace NeonSidekick.Tests;

/// <summary>
/// Resolves once per assembly whether a real MIDI input may be used (2026-11-12, the MIDI plan): <c>NEONSIDEKICK_TEST_MIDI_DEVICE</c> is
/// the input's name (the "2- " prefix Windows adds doesn't matter), on Windows or a Mac. Skipped without; never a CI safety net. Pressing
/// buttons is <c>--midi-check</c>'s job, not the suite's.
/// </summary>
internal static class LiveMidi
{
    public const string Variable = "NEONSIDEKICK_TEST_MIDI_DEVICE";

    public static readonly string? DeviceName = Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } value ? value.Trim() : null;

    /// <summary>The platform's MIDI layer: WinMM on Windows, CoreMIDI on a Mac.</summary>
    public static IMidiSystem NewSystem() =>
        OperatingSystem.IsWindows() ? new WindowsMidiSystem()
        : OperatingSystem.IsMacOS() ? new MacMidiSystem()
        : throw new PlatformNotSupportedException(Unavailable);

    public static string? Unavailable =>
        !OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS() ? "The MIDI layer is Windows or macOS only."
        : DeviceName is null ? $"{Variable} is not set (a MIDI input's name)."
        : null;
}

/// <summary>Skips unless <see cref="LiveMidi"/> names a real MIDI input.</summary>
public sealed class MidiDeviceFactAttribute : FactAttribute
{
    public MidiDeviceFactAttribute()
    {
        if (LiveMidi.Unavailable is { } why)
        {
            Skip = why;
        }
    }
}

/// <summary>
/// The platform MIDI layers without a device (2026-11-12, the MIDI plan): the list never throws with none, and an input that isn't there
/// is the user's "gone" sentence; with <c>NEONSIDEKICK_TEST_MIDI_DEVICE</c>, the named input opens, starts and closes cleanly, and a second
/// open of it is either "in use" (the classic Windows stack) or a second listener (a multi-client stack).
/// </summary>
public sealed class MidiSystemTests
{
    private static readonly MidiInputInfo NotThere = new(4096, "No such MIDI input");

    [WindowsFact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void Windows_ListsWhatWinMmCounts_AndAnInputNotThereIsGone()
    {
        var system = new WindowsMidiSystem();
        Assert.Equal((int)WinMmMidiNative.midiInGetNumDevs(), system.Inputs().Count);
        var ex = Assert.Throws<MidiException>(() => system.Open(NotThere, _ => { }));
        Assert.Equal(MidiText.Gone, ex.Message);
        Assert.False(ex.InUse);
    }

    [WindowsFact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void Windows_TheResultsAreTheUsersSentences()
    {
        Assert.True(WindowsMidiSystem.Failure(WinMmMidiNative.MmsyserrAllocated).InUse);
        Assert.Equal(MidiText.InUse, WindowsMidiSystem.Failure(WinMmMidiNative.MmsyserrAllocated).Message);
        Assert.Equal(MidiText.Gone, WindowsMidiSystem.Failure(WinMmMidiNative.MmsyserrBadDeviceId).Message);
        Assert.Equal(MidiText.NoDriver, WindowsMidiSystem.Failure(WinMmMidiNative.MmsyserrNoDriver).Message);
        Assert.Equal(MidiText.OpenFailed("MMSYSERR 11"), WindowsMidiSystem.Failure(11).Message);
    }

    /// <summary>A fixed name field is cut at its first NUL (2026-11-14, the review's #2): junk a driver leaves after it never reaches the name.</summary>
    [Fact]
    public unsafe void AFixedNameField_IsCutAtItsFirstNul()
    {
        char* field = stackalloc char[32];
        new Span<char>(field, 32).Fill('x');
        "FS-4\0junk".AsSpan().CopyTo(new Span<char>(field, 32));
        Assert.Equal("FS-4", NeonSidekick.Platform.FixedText.Read(field, 32));

        new Span<char>(field, 32).Fill('y');
        Assert.Equal(new string('y', 32), NeonSidekick.Platform.FixedText.Read(field, 32));   // no NUL: the whole field
    }

    [MacFact]
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    public void Mac_ListsWithoutThrowing_AndAnInputNotThereIsGone()
    {
        var system = new MacMidiSystem();
        Assert.NotNull(system.Inputs());
        var ex = Assert.Throws<MidiException>(() => system.Open(NotThere, _ => { }));
        Assert.Equal(MidiText.Gone, ex.Message);
    }

    [MidiDeviceFact]
    public void TheNamedInput_IsListed_Opens_AndClosesCleanly_AndASecondOpenIsInUseOrAnotherListener()
    {
        var system = LiveMidi.NewSystem();
        var input = Assert.Single(system.Inputs(), i => MidiNames.Same(i.Name, LiveMidi.DeviceName!));
        using (var opened = system.Open(input, _ => { }))
        {
            Assert.Equal(input, opened.Info);
            try
            {
                system.Open(input, _ => { }).Dispose();   // a multi-client stack (macOS, Windows MIDI Services)
            }
            catch (MidiException ex)
            {
                Assert.True(ex.InUse, ex.Message);   // the classic Windows stack
            }
        }

        using var again = system.Open(input, _ => { });   // closed cleanly: it opens again
    }
}

/// <summary>
/// <c>--midi-check</c> over the fake (2026-11-12, the MIDI plan): no system fails; no input passes; an input another app holds is a passing
/// "in use" line; what an open one sends is printed raw and decoded, the press marked, until it is stopped; every input is closed after.
/// </summary>
public sealed class MidiCheckTests
{
    private static Spectre.Console.Testing.TestConsole Console()
    {
        var console = new Spectre.Console.Testing.TestConsole();
        console.Profile.Width = 400;
        return console;
    }

    [Fact]
    public async Task NoSystem_Fails_AndNoInput_Passes()
    {
        var none = Console();
        Assert.Equal(1, await App.MidiCheck.RunAsync(none, null, TimeProvider.System, CancellationToken.None));
        Assert.Contains(MidiText.NoneHere, none.Output, StringComparison.Ordinal);

        var empty = Console();
        Assert.Equal(0, await App.MidiCheck.RunAsync(empty, new Fakes.FakeMidiSystem(), TimeProvider.System, CancellationToken.None));
        Assert.Contains(MidiText.CheckNone, empty.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhatAnInputSends_IsPrinted_AnInputInUsePasses_AndEveryInputIsClosedAfter()
    {
        var fake = new Fakes.FakeMidiSystem("FS-4", "DAW port");
        fake.Held.Add("DAW port");
        var console = Console();
        using var stop = new CancellationTokenSource();
        var running = App.MidiCheck.RunAsync(console, fake, TimeProvider.System, stop.Token);
        SpinWait.SpinUntil(() => fake.IsOpen("FS-4"), TimeSpan.FromSeconds(10));
        Assert.True(fake.Send("FS-4", [0xB0, 80, 127]));
        SpinWait.SpinUntil(() => console.Output.Contains("value 127", StringComparison.Ordinal), TimeSpan.FromSeconds(10));
        await stop.CancelAsync();

        Assert.Equal(0, await running.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains(MidiText.Heard(new MidiSeen(new MidiTrigger("FS-4", 0, MidiKind.ControlChange, 1, 80), 127, true)), console.Output, StringComparison.Ordinal);
        Assert.Contains("B0 50 7F", console.Output, StringComparison.Ordinal);
        Assert.Contains(MidiText.InUse, console.Output, StringComparison.Ordinal);
        Assert.Equal(["FS-4"], fake.Closed);
    }
}
