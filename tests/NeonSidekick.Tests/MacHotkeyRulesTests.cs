using NeonSidekick.Hotkeys;

namespace NeonSidekick.Tests;

/// <summary>
/// A Mac's <c>/keycheck</c> decisions (2026-10-11), pure, on every OS: the key codes and modifiers as Carbon has them, the fn rule
/// over the entries macOS 15.7 listed in the spike, the order of the three checks, and the terminals' Option settings.
/// </summary>
public class MacHotkeyRulesTests
{
    private static KeyChord Ctrl(ConsoleKey key) => new(true, false, false, key);

    /// <summary>The kVK codes the spike swept for every Mac Keys-tab chord.</summary>
    [Theory]
    [InlineData(ConsoleKey.A, 0x00)]
    [InlineData(ConsoleKey.C, 0x08)]
    [InlineData(ConsoleKey.D, 0x02)]
    [InlineData(ConsoleKey.E, 0x0E)]
    [InlineData(ConsoleKey.F, 0x03)]
    [InlineData(ConsoleKey.H, 0x04)]
    [InlineData(ConsoleKey.L, 0x25)]
    [InlineData(ConsoleKey.M, 0x2E)]
    [InlineData(ConsoleKey.O, 0x1F)]
    [InlineData(ConsoleKey.P, 0x23)]
    [InlineData(ConsoleKey.Q, 0x0C)]
    [InlineData(ConsoleKey.R, 0x0F)]
    [InlineData(ConsoleKey.S, 0x01)]
    [InlineData(ConsoleKey.T, 0x11)]
    [InlineData(ConsoleKey.U, 0x20)]
    [InlineData(ConsoleKey.V, 0x09)]
    [InlineData(ConsoleKey.X, 0x07)]
    [InlineData(ConsoleKey.Y, 0x10)]
    [InlineData(ConsoleKey.Z, 0x06)]
    [InlineData(ConsoleKey.D0, 0x1D)]
    [InlineData(ConsoleKey.D1, 0x12)]
    [InlineData(ConsoleKey.OemPeriod, 0x2F)]
    [InlineData(ConsoleKey.Oem2, 0x2C)]
    [InlineData(ConsoleKey.Oem6, 0x1E)]
    [InlineData(ConsoleKey.Enter, 0x24)]
    [InlineData(ConsoleKey.Home, 0x73)]
    [InlineData(ConsoleKey.End, 0x77)]
    [InlineData(ConsoleKey.LeftArrow, 0x7B)]
    [InlineData(ConsoleKey.RightArrow, 0x7C)]
    [InlineData(ConsoleKey.UpArrow, 0x7E)]
    [InlineData(ConsoleKey.F1, 0x7A)]
    [InlineData(ConsoleKey.F4, 0x76)]
    [InlineData(ConsoleKey.F9, 0x65)]
    [InlineData(ConsoleKey.F10, 0x6D)]
    [InlineData(ConsoleKey.F19, 0x50)]
    [InlineData(ConsoleKey.Backspace, 0x33)]
    [InlineData(ConsoleKey.Delete, 0x75)]
    public void KeyCodes_AreCarbons(ConsoleKey key, int code) => Assert.Equal((ushort)code, MacHotkeyRules.KeyCodeOf(key));

    [Theory]
    [InlineData(ConsoleKey.F21)]
    [InlineData(ConsoleKey.F24)]
    [InlineData(ConsoleKey.Pause)]
    public void AKeyNoMacKeyboardHas_HasNoCode(ConsoleKey key) => Assert.Null(MacHotkeyRules.KeyCodeOf(key));

    [Fact]
    public void Modifiers_AreCarbons_AndNeverWindowsBits()
    {
        Assert.Equal(0x1000u, MacHotkeyRules.ModifiersOf(Ctrl(ConsoleKey.A)));
        Assert.Equal(0x1800u, MacHotkeyRules.ModifiersOf(new KeyChord(true, true, false, ConsoleKey.A)));
        Assert.Equal(0x1A00u, MacHotkeyRules.ModifiersOf(new KeyChord(true, true, true, ConsoleKey.F19)));
        Assert.Equal(0x0800u, MacHotkeyRules.ModifiersOf(new KeyChord(false, true, false, ConsoleKey.V)));
        Assert.Equal(0u, MacHotkeyRules.ModifiersOf(new KeyChord(false, false, false, ConsoleKey.F9)));
    }

    /// <summary>The fn rule over the measured entries: an arrow's or F-key's fn is its own; a letter's is the Globe key.</summary>
    [Fact]
    public void Matches_IgnoresFnOnTheKeysThatCarryIt_AndNeedsItAbsentElsewhere()
    {
        Assert.True(MacHotkeyRules.Matches(new(0x7B, 0x21000, true), 0x7B, 0x1000));    // Mission Control: Ctrl+Left
        Assert.True(MacHotkeyRules.Matches(new(0x7B, 0x1000, true), 0x7B, 0x1000));
        Assert.False(MacHotkeyRules.Matches(new(0x08, 0x21000, true), 0x08, 0x1000));   // Globe+Ctrl+C (tiling) is not Ctrl+C
        Assert.True(MacHotkeyRules.Matches(new(0x24, 0x1000, true), 0x24, 0x1000));     // Ctrl+Return
        Assert.True(MacHotkeyRules.Matches(new(0x65, 0x21000, true), 0x65, 0x1000));    // Ctrl+F9
        Assert.False(MacHotkeyRules.Matches(new(0x65, 0x21000, true), 0x65, 0));        // … is not F9
        Assert.False(MacHotkeyRules.Matches(new(0x7B, 0x21000, false), 0x7B, 0x1000));  // off
        Assert.False(MacHotkeyRules.Matches(new(0x7B, 0x21200, true), 0x7B, 0x1000));   // Ctrl+Shift+Left
        Assert.False(MacHotkeyRules.Matches(new(MacHotkeyRules.NoCode, 0, true), MacHotkeyRules.NoCode, 0));
        Assert.True(MacHotkeyRules.Matches(new(0x00, 0x1400, true), 0x00, 0x1000));     // caps lock is no part of it
        Assert.True(MacHotkeyRules.CarriesFn(0x7E));
        Assert.True(MacHotkeyRules.CarriesFn(0x50));
        Assert.False(MacHotkeyRules.CarriesFn(0x24));
    }

    /// <summary>The order: macOS's list, then the exclusive register, then the terminal for an Option chord; a key with no code never asked.</summary>
    [Fact]
    public void Decide_TheListFirst_ThenTheRegister_ThenTheTerminal()
    {
        SymbolicHotKey[] list = [new(0x7B, 0x21000, true), new(0x08, 0x21000, true)];
        var asked = new List<(ushort, uint)>();
        int status = MacHotkeyRules.NoErr;
        int Register(ushort code, uint modifiers)
        {
            asked.Add((code, modifiers));
            return status;
        }

        Assert.Equal(new HotkeyProbeResult(HotkeyStatus.Held, 0, HotkeyCause.System), MacHotkeyRules.Decide(Ctrl(ConsoleKey.LeftArrow), list, Register, OptionKey.Alt));
        Assert.Empty(asked);
        Assert.Equal(new HotkeyProbeResult(HotkeyStatus.Free), MacHotkeyRules.Decide(Ctrl(ConsoleKey.C), list, Register, OptionKey.Types));
        Assert.Equal([((ushort)0x08, 0x1000u)], asked);

        var ctrlAltA = new KeyChord(true, true, false, ConsoleKey.A);
        Assert.Equal(new HotkeyProbeResult(HotkeyStatus.Held, 0, HotkeyCause.Terminal), MacHotkeyRules.Decide(ctrlAltA, list, Register, OptionKey.Types));
        Assert.Equal(new HotkeyProbeResult(HotkeyStatus.Free), MacHotkeyRules.Decide(ctrlAltA, list, Register, OptionKey.Alt));
        Assert.Equal(new HotkeyProbeResult(HotkeyStatus.Free), MacHotkeyRules.Decide(ctrlAltA, list, Register, OptionKey.Unknown));

        status = MacHotkeyRules.HotKeyExists;
        Assert.Equal(new HotkeyProbeResult(HotkeyStatus.Held, 0, HotkeyCause.OtherApp), MacHotkeyRules.Decide(ctrlAltA, list, Register, OptionKey.Types));
        status = -50;
        Assert.Equal(new HotkeyProbeResult(HotkeyStatus.Unknown, -50), MacHotkeyRules.Decide(Ctrl(ConsoleKey.E), list, Register, OptionKey.Alt));

        asked.Clear();
        Assert.Equal(new HotkeyProbeResult(HotkeyStatus.Unknown, 0, HotkeyCause.NoKeyCode), MacHotkeyRules.Decide(Ctrl(ConsoleKey.F24), list, Register, OptionKey.Alt));
        Assert.Empty(asked);
    }

    [Fact]
    public void TerminalApp_MetaOnIsAlt_NeverSetIsOff_NoProfileIsUnknown()
    {
        Assert.Equal((OptionKey.Alt, KeyCheckText.TerminalAppOption("Pro", true)), MacHotkeyRules.TerminalAppOption("Pro", true));
        Assert.Equal((OptionKey.Types, KeyCheckText.TerminalAppOption("Basic", false)), MacHotkeyRules.TerminalAppOption("Basic", null));
        Assert.Equal((OptionKey.Unknown, KeyCheckText.UnknownOption("Terminal")), MacHotkeyRules.TerminalAppOption(null, true));
    }

    /// <summary>iTerm2: only Esc+ (2) reaches the app as Alt; Meta (1) sets the high bit, which the input parser never reads as Alt.</summary>
    [Fact]
    public void Iterm_EitherKeyOnEscPlusIsAlt_MetaIsNot()
    {
        Assert.Equal((OptionKey.Alt, KeyCheckText.ItermOption("Default", true, false)), MacHotkeyRules.ItermOption("Default", 2, 0));
        Assert.Equal((OptionKey.Alt, KeyCheckText.ItermOption("Default", false, true)), MacHotkeyRules.ItermOption("Default", null, 2));
        Assert.Equal((OptionKey.Types, KeyCheckText.ItermOption("Work", false, false)), MacHotkeyRules.ItermOption("Work", 1, null));
        Assert.Equal(OptionKey.Unknown, MacHotkeyRules.ItermOption(null, 2, 2).Option);
        Assert.Equal("iTerm2, profile Work: neither Option key sends Esc+, so the Option chords never arrive (iTerm2 › Settings › Profiles › Keys).", KeyCheckText.ItermOption("Work", false, false));
    }

    [Fact]
    public void ItermProfile_TheSessionsByName_ElseTheDefaultByGuid()
    {
        (string?, string?)[] profiles = [("Default", "g1"), ("Work", "g2"), (null, "g3")];
        Assert.Equal(1, MacHotkeyRules.ItermProfileIndex(profiles, "Work", "g1"));
        Assert.Equal(0, MacHotkeyRules.ItermProfileIndex(profiles, "Gone", "g1"));
        Assert.Equal(2, MacHotkeyRules.ItermProfileIndex(profiles, null, "g3"));
        Assert.Equal(-1, MacHotkeyRules.ItermProfileIndex(profiles, null, null));
    }
}
