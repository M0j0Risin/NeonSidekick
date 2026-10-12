using NeonSidekick.App;
using NeonSidekick.Hotkeys;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary><c>/keycheck</c> on the screen (2026-10-04): the pane with the held chords first, the lines without one, a Mac's words, the error elsewhere.</summary>
public partial class ChatScreenTests
{
    /// <summary>The probe the chat screen gets; null (the default) is a platform without one.</summary>
    private FakeHotkeyProbe? _hotkeyProbe;

    [Fact]
    public async Task KeyCheck_OnThePane_TheHeldChordsFirst_UnderTheHeaderAndTheCaveat()
    {
        _settings.Update(d => d.TtsOutput = false);
        _hotkeyProbe = new FakeHotkeyProbe();
        _hotkeyProbe.Held.Add(new KeyChord(true, true, false, ConsoleKey.S));
        _console.Profile.Height = 60;
        _geometry = new ScreenGeometry(() => null);
        PushLine("/keycheck");
        _console.Input.PushKey(Keys.Escape);
        PushLine("/exit");

        string output = await RunAsync();

        int total = KeyCheck.Chords(ChatScreen.KeyRows(false, ConsoleKey.F4, false, "").Select(r => r.Pair), null).Count;
        string header = KeyCheckText.Header(1, total);
        Assert.Contains(KeyCheckText.Label, output);
        Assert.Contains(header, output);
        Assert.DoesNotContain("· " + header, output);   // on the pane, not a notice
        Assert.Contains(KeyCheckText.Caveat[..60], output);
        int held = output.IndexOf("Ctrl+Alt+S", StringComparison.Ordinal);
        int first = output.IndexOf("Ctrl+.", StringComparison.Ordinal);
        Assert.True(held >= 0 && first > held, output);   // the held one leads, the rest in the Keys tab's order
        Assert.Equal(total, _hotkeyProbe.Asked.Count);
    }

    [Fact]
    public async Task KeyCheck_WithoutThePane_TheLinesAsNotices()
    {
        _settings.Update(d => d.TtsOutput = false);
        _hotkeyProbe = new FakeHotkeyProbe();
        PushLine("/keycheck");
        PushLine("/exit");

        string output = await RunAsync();

        int total = KeyCheck.Chords(ChatScreen.KeyRows(false, ConsoleKey.F4, false, "").Select(r => r.Pair), null).Count;
        Assert.Contains("· " + KeyCheckText.Header(0, total), output);
        Assert.Contains("free  Ctrl+Alt+M", output);
    }

    /// <summary>A Mac's probe (2026-10-11): its header, caveat and note on the pane, the held chord by its cause, in the Mac's rows.</summary>
    [Fact]
    public async Task KeyCheck_AMacProbe_ItsWordsAndTheTerminalsNote_OnThePane()
    {
        _settings.Update(d => d.TtsOutput = false);
        _hotkeyProbe = new FakeHotkeyProbe { Mac = true };
        _hotkeyProbe.Notes.Add("Terminal's default profile, Pro: Use Option as Meta key is on, so the Option chords arrive.");
        var left = new KeyChord(true, false, false, ConsoleKey.LeftArrow);
        _hotkeyProbe.Held.Add(left);
        _hotkeyProbe.Causes[left] = HotkeyCause.System;
        _console.Profile.Height = 60;
        _geometry = new ScreenGeometry(() => null);
        PushLine("/keycheck");
        _console.Input.PushKey(Keys.Escape);
        PushLine("/exit");

        string output = await RunAsync();

        int total = KeyCheck.Chords(ChatScreen.KeyRows(false, ConsoleKey.F4, false, "").Select(r => r.Pair), null).Count;
        Assert.Contains(KeyCheckText.MacHeader(1, total), output);
        Assert.Contains(KeyCheckText.MacCaveat[..60], output);
        Assert.Contains("Use Option as Meta key is on", output);
        Assert.Contains(KeyCheckText.SystemWord, output);
        Assert.DoesNotContain(KeyCheckText.Caveat[..40], output);
        Assert.Equal(1, _hotkeyProbe.Begun);
    }

    [Fact]
    public async Task KeyCheck_WithNoProbe_SaysItNeedsWindowsOrMacOS_AndAWord_IsRefused()
    {
        _settings.Update(d => d.TtsOutput = false);
        PushLine("/keycheck");
        PushLine("/keycheck now");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(KeyCheckText.Unsupported, output);
        Assert.Contains(ChatScreen.NoArgumentError("/keycheck"), output);
    }

    /// <summary>
    /// <c>/help</c>'s Keys tab carries the key check toggle (2026-10-12, the user's ask): K swaps in <c>/keycheck</c>'s list, probed
    /// then and only then, and K again brings the keys back without asking the probe again.
    /// </summary>
    [Fact]
    public async Task Help_TheKeysTab_KSwapsInTheKeyCheck_AndKAgainBringsTheKeysBack()
    {
        _settings.Update(d => d.TtsOutput = false);
        _hotkeyProbe = new FakeHotkeyProbe();
        _hotkeyProbe.Held.Add(new KeyChord(true, true, false, ConsoleKey.S));
        _console.Profile.Width = 120;
        _console.Profile.Height = 60;
        _geometry = new ScreenGeometry(() => null);
        PushLine("/help");
        _console.Input.PushKey(Keys.Left);   // Basic wraps to Keys
        _console.Input.PushKey(Keys.Char('k'));
        _console.Input.PushKey(Keys.Char('k'));
        _console.Input.PushKey(Keys.Escape);
        PushLine("/exit");

        string output = await RunAsync();

        int total = KeyCheck.Chords(ChatScreen.KeyRows(false, ConsoleKey.F4, false, "").Select(r => r.Pair), null).Count;
        Assert.Equal(total, _hotkeyProbe.Asked.Count);   // once: the open, the measure and the swap back never probe
        int strip = output.IndexOf(" │ " + KeyCheckText.ToggleTitle + " ", StringComparison.Ordinal);
        Assert.True(strip >= 0, output);
        int header = output.IndexOf(KeyCheckText.Header(1, total), strip, StringComparison.Ordinal);
        Assert.True(header > strip, output);
        int afterOn = output.LastIndexOf(InfoPane.ToggleHint(KeyCheckText.ToggleKey, KeyCheckText.ToggleWord), StringComparison.Ordinal);
        Assert.True(afterOn > header, output);
        Assert.Matches("open the usage pane +/usage", output[header..]);   // the keys again after the second K, the command column theirs alone
    }

    /// <summary>Without a probe (neither Windows nor macOS) the Keys tab has no toggle, and K is a find.</summary>
    [Fact]
    public async Task Help_WithNoProbe_TheKeysTabHasNoKeyCheck()
    {
        _settings.Update(d => d.TtsOutput = false);
        _console.Profile.Width = 120;
        _geometry = new ScreenGeometry(() => null);
        PushLine("/help");
        _console.Input.PushKey(Keys.Left);
        _console.Input.PushKey(Keys.Escape);
        PushLine("/exit");

        string output = await RunAsync();

        Assert.DoesNotContain(KeyCheckText.ToggleTitle, output);
        Assert.Contains(InfoPane.HintText[..40], output);
    }
}
