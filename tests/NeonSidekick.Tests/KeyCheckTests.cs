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
        var chords = KeyCheck.Chords(rows.Select(r => r.Pair), ConsoleKey.F4);
        Assert.Contains(chords, c => c.Label == "F4" && !c.Chord.HasModifier);
        Assert.Contains(chords, c => c.Label == "Ctrl+Alt+M" && c.Chord == CtrlAlt(ConsoleKey.M) && c.Meaning == (OperatingSystem.IsMacOS() ? "open the memory pane (in the app's windows; Ctrl+Alt+A in the terminal) (/memory)" : "open the memory pane (/memory)"));
        Assert.DoesNotContain(chords, c => c.Label is "Enter" or "ESC" or "Up / Down" or "PgUp / PgDn" or "Up" or "PgUp");
        Assert.Equal(rows.Where(r => KeyChord.TryParse(r.Key, out var k) && k.HasModifier).Select(r => r.Key), chords.Where(c => c.Chord.HasModifier && rows.Any(r => r.Key == c.Label)).Select(c => c.Label));   // the tab's order

        Assert.DoesNotContain(KeyCheck.Chords(ChatScreen.KeyRows(voiceOn: false, ConsoleKey.F4, false, "").Select(r => r.Pair), null), c => !c.Chord.HasModifier);
    }

    /// <summary>
    /// A row naming two keys is two chords (2026-10-11): <c>Ctrl+Left / Right</c> (Mission Control's on a Mac, kept out until then),
    /// a Mac's <c>Ctrl+V / Alt+V</c> as written, Windows' <c>Ctrl+Backspace / Delete</c>; each with the row's meaning, in its place.
    /// </summary>
    [Fact]
    public void ARowNamingTwoKeys_IsTwoChords_TheBareOneTakingTheFirstsModifiers()
    {
        var chords = KeyCheck.Chords([("Up / Down", "move"), ("Ctrl+Left / Right", "word"), ("Ctrl+V / Alt+V", "paste"), ("Ctrl+Backspace / Delete", "delete"), ("Ctrl+A / Nope", "x")], null);
        Assert.Equal(["Ctrl+Left", "Ctrl+Right", "Ctrl+V", "Alt+V", "Ctrl+Backspace", "Ctrl+Delete", "Ctrl+A"], chords.Select(c => c.Label));
        Assert.Equal(Ctrl(ConsoleKey.RightArrow), chords[1].Chord);
        Assert.Equal(new KeyChord(false, true, false, ConsoleKey.V), chords[3].Chord);
        Assert.Equal(Ctrl(ConsoleKey.Delete), chords[5].Chord);
        Assert.Equal("word", chords[1].Meaning);

        var mac = KeyCheck.Chords(ChatScreen.KeyRows(false, ConsoleKey.F4, false, "", mac: true).Select(r => r.Pair), null).Select(c => c.Label).ToList();
        Assert.Contains("Ctrl+Left", mac);
        Assert.Contains("Ctrl+Right", mac);
        Assert.Contains("Alt+V", mac);
        var windows = KeyCheck.Chords(ChatScreen.KeyRows(false, ConsoleKey.F4, false, "", mac: false).Select(r => r.Pair), null).Select(c => c.Label).ToList();
        Assert.Contains("Ctrl+Right", windows);
        Assert.Contains("Ctrl+Delete", windows);
    }

    /// <summary>
    /// The Keys tab is the one list (2026-10-04): every chord the key handling answers to — the command chords, the kill switch,
    /// the learning's cancel, the fold toggle, Ctrl+C — is on it, so <c>/keycheck</c> (and <c>/help</c>) never miss one.
    /// </summary>
    [Fact]
    public void EveryChordTheKeysAnswerTo_IsOnTheKeysTab()
    {
        var listed = KeyCheck.Chords(ChatScreen.KeyRows(false, ConsoleKey.F4, false, "").Select(r => r.Pair), null).Select(c => c.Chord).ToHashSet();
        var keys = Enumerable.Range('A', 26).Select(c => (ConsoleKey)c).Append(ConsoleKey.Oem2).Append(ConsoleKey.OemPeriod).Append(ConsoleKey.Oem6);   // Ctrl+], a Mac's /terminal, 2026-10-07
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
        Assert.Equal(1, probe.Begun);
        Assert.False(report.Mac);
    }

    /// <summary>A Mac's report (2026-10-11): the probe's words and notes ride on it, and the lines carry them under the caveat.</summary>
    [Fact]
    public void AMacProbe_GivesTheReportItsWords_AndItsNotes()
    {
        var probe = new FakeHotkeyProbe { Mac = true };
        probe.Notes.Add("note one");
        probe.Held.Add(Ctrl(ConsoleKey.LeftArrow));
        probe.Causes[Ctrl(ConsoleKey.LeftArrow)] = HotkeyCause.System;
        probe.Held.Add(new KeyChord(false, true, false, ConsoleKey.V));
        probe.Causes[new KeyChord(false, true, false, ConsoleKey.V)] = HotkeyCause.Terminal;

        var report = KeyCheck.Run([("Ctrl+E", "explore"), ("Ctrl+Left / Right", "word"), ("Ctrl+V / Alt+V", "paste")], null, probe);

        Assert.True(report.Mac);
        Assert.Equal(2, report.Held);
        Assert.Equal(
        [
            KeyCheckText.MacHeader(2, 5),
            KeyCheckText.MacCaveat,
            "note one",
            "  macOS shortcut        Ctrl+Left   word",
            "  held by the terminal  Alt+V       paste",
            "  free                  Ctrl+E      explore",
            "  free                  Ctrl+Right  word",
            "  free                  Ctrl+V      paste",
        ], KeyCheckText.Lines(report));
    }

    [Fact]
    public void TheMacWording_HeaderCausesAndTheTerminalsOptionKey()
    {
        Assert.Equal("All 3 chords are free: no macOS shortcut, exclusive app hotkey or terminal setting takes them.", KeyCheckText.MacHeader(0, 3));
        Assert.Equal("2 of 40 chords taken: macOS, another app or the terminal may keep those keys from the app.", KeyCheckText.MacHeader(2, 40));
        Assert.Equal("macOS shortcut", KeyCheckText.Status(new HotkeyProbeResult(HotkeyStatus.Held, 0, HotkeyCause.System)));
        Assert.Equal("held by an app", KeyCheckText.Status(new HotkeyProbeResult(HotkeyStatus.Held, 0, HotkeyCause.OtherApp)));
        Assert.Equal("held by the terminal", KeyCheckText.Status(new HotkeyProbeResult(HotkeyStatus.Held, 0, HotkeyCause.Terminal)));
        Assert.Equal("not checked (the app was busy)", KeyCheckText.Status(new HotkeyProbeResult(HotkeyStatus.Unknown, MacHotkeyRules.MainThreadBusy)));
        Assert.Equal("not checked", KeyCheckText.Status(new HotkeyProbeResult(HotkeyStatus.Unknown, 0, HotkeyCause.NoKeyCode)));
        Assert.Equal("Terminal's default profile, Pro: Use Option as Meta key is on, so the Option chords arrive.", KeyCheckText.TerminalAppOption("Pro", true));
        Assert.Equal("iTerm2, profile Default: the left Option key sends Esc+, so the Option chords arrive with it (not with the right one).", KeyCheckText.ItermOption("Default", true, false));
        Assert.Equal("The Option key's setting in Ghostty is not known here: if Option types characters, the Option chords never arrive.", KeyCheckText.UnknownOption("Ghostty"));
        Assert.Equal("The key check needs Windows or macOS: it asks the system which keys other programs hold.", KeyCheckText.Unsupported);
        Assert.StartsWith("Only macOS's own shortcuts, hotkeys an app holds exclusively and the terminal's Option key show here;", KeyCheckText.MacCaveat, StringComparison.Ordinal);
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

    /// <summary>
    /// The Mac probe for real (2026-10-11): HIToolbox binds, macOS lists its shortcuts, the terminal's note comes, and a chord no shortcut
    /// uses answers free or held, never an error (the smoke's keys:carbon too). Without a window server there is nothing to ask.
    /// </summary>
    [MacFact]
    public void TheMacProbe_Answers_OnAChordNoShortcutUses()
    {
        if (!OperatingSystem.IsMacOS() || !NeonSidekick.Viewer.AppKitNative.HasWindowServer())
        {
            return;
        }

        var probe = new MacHotkeyProbe("Apple_Terminal", null);
        var notes = ((IHotkeyProbe)probe).Begin();
        Assert.NotEmpty(notes);
        Assert.DoesNotContain(notes, n => n.StartsWith("macOS did not list", StringComparison.Ordinal));
        var result = probe.Probe(new KeyChord(true, true, true, ConsoleKey.F19));
        Assert.NotEqual(HotkeyStatus.Unknown, result.Status);
        Assert.Equal(HotkeyStatus.Unknown, probe.Probe(new KeyChord(true, false, false, ConsoleKey.F24)).Status);   // no Mac key: not asked
        var smoke = SmokeChecks.ProbeCarbonHotkeys();
        Assert.True(smoke.Passed, smoke.Detail);
    }
}
