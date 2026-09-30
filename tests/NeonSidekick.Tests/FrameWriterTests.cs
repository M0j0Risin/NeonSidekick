using System.Text;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// stdout's frame writer (2026-09-29, the user's report: the rows under the input flickered at every turn's end): every
/// write out at once as the console's own writer, until a hold — then nothing until the last release lets the frame go.
/// </summary>
public sealed class FrameWriterTests
{
    private readonly MemoryStream _stream = new();

    private string Written => Encoding.UTF8.GetString(_stream.ToArray());

    [Fact]
    public void Unheld_EveryWrite_ReachesTheStream_AtOnce()
    {
        using var writer = new FrameWriter(_stream);
        writer.Write("a");
        Assert.Equal("a", Written);
        writer.Write('b');
        writer.WriteLine("c");
        writer.Write("é🛠️".AsSpan());
        Assert.Equal("abc" + Environment.NewLine + "é🛠️", Written);
        Assert.Equal(0, _stream.ToArray().Take(3).Count(b => b == 0xEF));   // no BOM
    }

    [Fact]
    public void Held_WritesAndFlushes_WaitForTheLastRelease()
    {
        using var writer = new FrameWriter(_stream);
        writer.Write("before ");
        writer.Hold();
        writer.Write("\e[?2026h");
        writer.Write("frame");
        writer.Flush();                         // Spectre's flush after each write: held too
        writer.Hold();                          // a nested frame
        writer.Write(" inner");
        writer.Release();
        Assert.Equal("before ", Written);
        Assert.True(writer.Holding);

        writer.Write("\e[?2026l");
        writer.Release();
        Assert.False(writer.Holding);
        Assert.Equal("before \e[?2026hframe inner\e[?2026l", Written);

        writer.Release();                       // one too many: nothing
        writer.Write(" after");
        Assert.EndsWith(" after", Written);
    }

    /// <summary>Settle (2026-09-29): what is held so far goes out, the hold stays — later writes wait for the release.</summary>
    [Fact]
    public void Settle_WritesWhatIsHeld_AndKeepsTheHold()
    {
        using var writer = new FrameWriter(_stream);
        writer.Hold();
        writer.Write("lift");
        writer.Settle();
        Assert.Equal("lift", Written);
        Assert.True(writer.Holding);
        writer.Write(" pane");
        Assert.Equal("lift", Written);
        writer.Release();
        Assert.Equal("lift pane", Written);
    }

    [Fact]
    public void Dispose_LetsAHeldFrameGo()
    {
        var writer = new FrameWriter(_stream);
        writer.Hold();
        writer.Write("held");
        writer.Dispose();
        Assert.Equal("held", Written);
    }
}
