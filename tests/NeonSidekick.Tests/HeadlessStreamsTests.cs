using System.Text;
using NeonSidekick.App;

namespace NeonSidekick.Tests;

/// <summary>Headless's stdin and stderr picked by what each is (2026-11-22, the review's #4 and #121).</summary>
public class HeadlessStreamsTests
{
    [Fact]
    public void ARedirectedStdin_IsReadAsUtf8_AConsoleOne_IsTheConsolesAfterTheWideSwitch()
    {
        using var piped = HeadlessStreams.Input(redirected: true, () => new MemoryStream(Encoding.UTF8.GetBytes("é ü 漢字")), () => throw new InvalidOperationException("not the console"));
        Assert.Equal("é ü 漢字", piped.ReadToEnd());

        var console = new StringReader("typed");
        bool widened = false;
        Assert.Same(console, HeadlessStreams.Input(redirected: false, () => throw new InvalidOperationException("not the pipe"), () => console, () => widened = true));
        Assert.True(widened);

        // A console that refuses the switch keeps its own code page: still the console's reader, never a crash.
        Assert.Same(console, HeadlessStreams.Input(redirected: false, () => Stream.Null, () => console, () => throw new IOException("refused")));
    }

    [Fact]
    public void Stderr_IsUtf8_WhenRedirectedOrPinned_ElseTheConsoles()
    {
        var bytes = new MemoryStream();
        var console = new StringWriter();

        var utf8 = HeadlessStreams.Error(redirected: false, outputPinned: true, () => bytes, () => console);
        utf8.Write("漢字");
        Assert.Equal("漢字", Encoding.UTF8.GetString(bytes.ToArray()));

        var plain = HeadlessStreams.Error(redirected: false, outputPinned: false, () => throw new InvalidOperationException("not the raw stream"), () => console);
        plain.Write("x");
        Assert.Equal("x", console.ToString());
    }
}
