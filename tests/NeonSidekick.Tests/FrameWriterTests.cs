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

    /// <summary>
    /// A terminal gone (2026-10-25, the Mac pass: a redraw tick wrote to a pty iTerm2 had closed, and the IOException ended the process): the
    /// first failed write marks it gone, nothing after throws or reaches the stream, held or not, the dispose too, and it is logged once.
    /// </summary>
    [Fact]
    public void ATerminalGone_DropsTheWrites_NeverThrows_AndIsLoggedOnce()
    {
        var stream = new FailingStream();
        var lines = new List<string>();
        Action<NeonSidekick.Diagnostics.DiagnosticEvent> capture = e => { if (e.Category == ScreenPane.Category && e.Message.StartsWith("The terminal took no more output", StringComparison.Ordinal)) lines.Add(e.Message); };
        NeonSidekick.Diagnostics.DiagnosticLog.Emitted += capture;
        try
        {
            var writer = new FrameWriter(stream);
            writer.Write("before");
            Assert.False(writer.Gone);

            stream.Fail = true;
            writer.Write("lost");
            Assert.True(writer.Gone);
            writer.Hold();
            writer.Write(new string('x', 40_000));   // past the buffer: a flush inside the write
            writer.Release();
            writer.WriteLine("after");
            writer.Settle();
            writer.Flush();
            writer.Dispose();

            Assert.Equal("before", Encoding.UTF8.GetString(stream.Written.ToArray()));
            Assert.Equal([string.Format(System.Globalization.CultureInfo.InvariantCulture, FrameWriter.GoneLogLine, FailingStream.Message)], lines);
        }
        finally
        {
            NeonSidekick.Diagnostics.DiagnosticLog.Emitted -= capture;
        }
    }

    /// <summary>A held frame bigger than the buffer, the terminal gone mid-frame: the write that flushes is the one that finds out.</summary>
    [Fact]
    public void ATerminalGone_WhileHeld_IsFoundByTheWriteThatFlushes()
    {
        var stream = new FailingStream();
        using var writer = new FrameWriter(stream);
        writer.Hold();
        stream.Fail = true;
        writer.Write(new string('y', 40_000));
        Assert.True(writer.Gone);
        writer.Release();
        Assert.Empty(stream.Written.ToArray());
    }

    // A pty that closes: every write after Fail throws EIO's IOException, as .NET's console stream does.
    private sealed class FailingStream : Stream
    {
        public const string Message = "Input/output error";

        public MemoryStream Written { get; } = new();

        public bool Fail { get; set; }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => Written.Length;

        public override long Position { get => Written.Position; set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (Fail)
            {
                throw new IOException(Message);
            }

            Written.Write(buffer);
        }
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
