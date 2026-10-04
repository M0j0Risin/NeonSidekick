using NeonSidekick.App;
using NeonSidekick.Hotkeys;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary><c>/keycheck</c>'s pure part (2026-10-04): the labels parsed, the chords chosen from the Keys tab, the report and its wording.</summary>
public class KeyCheckTests
{
    private static KeyChord Ctrl(ConsoleKey key) => new(true, false, false, key);

    private static KeyChord CtrlAlt(ConsoleKey key) => new(true, true, false, key);

    [Theory]
    [InlineData("Ctrl+Alt+M", true, true, false, ConsoleKey.M)]
    [InlineData("ctrl+alt+x", true, true, false, ConsoleKey.X)]
    [InlineData("Ctrl+.", true, false, false, ConsoleKey.OemPeriod)]
    [InlineData("Ctrl+/", true, false, false, ConsoleKey.Oem2)]
    [InlineData("Ctrl+Home", true, false, false, ConsoleKey.Home)]
    [InlineData("Ctrl+End", true, false, false, ConsoleKey.End)]
    [InlineData("Ctrl+Enter", true, false, false, ConsoleKey.Enter)]
    [InlineData("Alt+V", false, true, false, ConsoleKey.V)]
    [InlineData("Shift+F10", false, false, true, ConsoleKey.F10)]
    [InlineData("F4", false, false, false, ConsoleKey.F4)]
    [InlineData("F24", false, false, false, ConsoleKey.F24)]
    [InlineData("Ctrl+1", true, false, false, ConsoleKey.D1)]
    public void TryParse_TheKeysTabsLabels(string label, bool ctrl, bool alt, bool shift, ConsoleKey key)
    {
        Assert.True(KeyChord.TryParse(label, out var chord));
        Assert.Equal(new KeyChord(ctrl, alt, shift, key), chord);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ESC ESC")]
    [InlineData("Up / Down")]
    [InlineData("say \"hey neon\"")]
    [InlineData("Ctrl+")]
    [InlineData("Win+E")]
    [InlineData("Ctrl+F25")]
    [InlineData("Ctrl+Nope")]
    public void TryParse_AnythingElse_IsNoChord(string label) => Assert.False(KeyChord.TryParse(label, out _));

    [Fact]
    public void AChord_IsRegisterHotKeysModifiersAndVirtualKey()
    {
        var chord = new KeyChord(true, true, true, ConsoleKey.OemPeriod);
        Assert.Equal(0x0007u, chord.Modifiers);   // MOD_CONTROL | MOD_ALT | MOD_SHIFT
        Assert.Equal(0xBEu, chord.VirtualKey);    // VK_OEM_PERIOD
        Assert.Equal(0xBFu, Ctrl(ConsoleKey.Oem2).VirtualKey);
        Assert.Equal(0x4Du, CtrlAlt(ConsoleKey.M).VirtualKey);
        Assert.False(new KeyChord(false, false, false, ConsoleKey.F4).HasModifier);
    }

    [Fact]
    public void Chords_AreTheKeysTabsChords_ThePushToTalkKeyOnlyWhenGiven()
    {
        var rows = ChatScreen.KeyRows(voiceOn: true, ConsoleKey.F4, wakeReady: true, "hey neon");
        var chords = KeyCheck.Chords(rows, ConsoleKey.F4);
        Assert.Contains(chords, c => c.Label == "F4" && !c.Chord.HasModifier);
        Assert.Contains(chords, c => c.Label == "Ctrl+Alt+M" && c.Chord == CtrlAlt(ConsoleKey.M) && c.Meaning == "open the memory pane (/memory)");
        Assert.DoesNotContain(chords, c => c.Label is "Enter" or "ESC" or "Up / Down" or "PgUp / PgDn");
        Assert.Equal(rows.Where(r => KeyChord.TryParse(r.Key, out var k) && k.HasModifier).Select(r => r.Key), chords.Where(c => c.Chord.HasModifier).Select(c => c.Label));   // the tab's order

        Assert.DoesNotContain(KeyCheck.Chords(ChatScreen.KeyRows(voiceOn: false, ConsoleKey.F4, false, ""), null), c => !c.Chord.HasModifier);
    }

    /// <summary>
    /// The Keys tab is the one list (2026-10-04): every chord the key handling answers to — the command chords, the kill switch,
    /// the learning's cancel, the fold toggle, Ctrl+C — is on it, so <c>/keycheck</c> (and <c>/help</c>) never miss one.
    /// </summary>
    [Fact]
    public void EveryChordTheKeysAnswerTo_IsOnTheKeysTab()
    {
        var listed = KeyCheck.Chords(ChatScreen.KeyRows(false, ConsoleKey.F4, false, ""), null).Select(c => c.Chord).ToHashSet();
        var keys = Enumerable.Range('A', 26).Select(c => (ConsoleKey)c).Append(ConsoleKey.Oem2).Append(ConsoleKey.OemPeriod);
        foreach (var key in keys)
        {
            foreach (bool alt in new[] { false, true })
            {
                var info = new ConsoleKeyInfo('\0', key, shift: false, alt: alt, control: true);
                if (Keys.ShortcutLine(info) is not null || Keys.IsKillSwitch(info) || Keys.IsLearnCancel(info) || Keys.IsToolToggle(info) || Keys.IsInterrupt(info))
                {
                    Assert.Contains(new KeyChord(true, alt, false, key), listed);
                }
            }
        }
    }

    [Fact]
    public void Run_TheHeldOnesFirst_ThenTheRestInOrder_AndTheCount()
    {
        var probe = new FakeHotkeyProbe();
        probe.Held.Add(CtrlAlt(ConsoleKey.S));
        probe.Held.Add(Ctrl(ConsoleKey.A));
        probe.Errors[CtrlAlt(ConsoleKey.X)] = 5;
        (string, string)[] rows = [("Enter", "send"), ("Ctrl+A", "select all"), ("Ctrl+E", "explore"), ("Ctrl+Alt+S", "skills"), ("Ctrl+Alt+X", "kill switch")];

        var report = KeyCheck.Run(rows, null, probe);

        Assert.Equal(2, report.Held);
        Assert.Equal(["Ctrl+A", "Ctrl+Alt+S", "Ctrl+E", "Ctrl+Alt+X"], report.Rows.Select(r => r.Label));
        Assert.Equal(new HotkeyProbeResult(HotkeyStatus.Unknown, 5), report.Rows[^1].Result);
        Assert.Equal(4, probe.Asked.Count);   // Enter is no chord: never asked
    }

    [Fact]
    public void TheWording_HeaderStatusAndLines()
    {
        Assert.Equal("All 3 chords are free: no other program holds them.", KeyCheckText.Header(0, 3));
        Assert.Equal("1 of 39 chords held by another program: those keys never reach the app.", KeyCheckText.Header(1, 39));
        Assert.Equal("free", KeyCheckText.Status(new HotkeyProbeResult(HotkeyStatus.Free)));
        Assert.Equal("held", KeyCheckText.Status(new HotkeyProbeResult(HotkeyStatus.Held)));
        Assert.Equal("unknown (error 5)", KeyCheckText.Status(new HotkeyProbeResult(HotkeyStatus.Unknown, 5)));

        var probe = new FakeHotkeyProbe();
        probe.Held.Add(CtrlAlt(ConsoleKey.S));
        var lines = KeyCheckText.Lines(KeyCheck.Run([("Ctrl+E", "explore"), ("Ctrl+Alt+S", "skills")], null, probe));
        Assert.Equal(
        [
            KeyCheckText.Header(1, 2),
            KeyCheckText.Caveat,
            "  held  Ctrl+Alt+S  skills",
            "  free  Ctrl+E      explore",
        ], lines);
    }

    /// <summary>The real probe on a chord no keyboard has: Windows answers free or held, never an error (the smoke's keys:hotkey too).</summary>
    [Fact]
    public void TheWindowsProbe_Answers_OnAChordNoKeyboardHas()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = new WindowsHotkeyProbe().Probe(new KeyChord(true, true, true, ConsoleKey.F24));
        Assert.NotEqual(HotkeyStatus.Unknown, result.Status);
        Assert.True(SmokeChecks.ProbeHotkey().Passed);
    }
}
