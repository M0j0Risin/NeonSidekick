using NeonSidekick.App;
using NeonSidekick.Hotkeys;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary><c>/keycheck</c> on the screen (2026-10-04): the pane with the held chords first, the lines without one, the error off Windows.</summary>
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

    [Fact]
    public async Task KeyCheck_WithNoProbe_SaysItNeedsWindows_AndAWord_IsRefused()
    {
        _settings.Update(d => d.TtsOutput = false);
        PushLine("/keycheck");
        PushLine("/keycheck now");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(KeyCheckText.Unsupported, output);
        Assert.Contains(ChatScreen.NoArgumentError("/keycheck"), output);
    }
}
