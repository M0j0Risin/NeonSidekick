using System.Text;

namespace NeonSidekick.UI;

/// <summary>
/// A frame of pane output held back and let go as one write (2026-09-29, the user's report: the hint row, the toolbar and
/// the performance bar flickered at every turn's end). <see cref="ScreenPane"/> holds around each synchronized frame
/// (<c>\e[?2026h</c> … <c>\e[?2026l</c>), so a terminal that does not honour synchronized output still gets the erase and
/// the redraw in one piece rather than the dozens of writes a draw is made of. Null on the pane (tests,
/// <see cref="Spectre.Console.Testing.TestConsole"/>) is no holding.
/// </summary>
public interface IFrameHold
{
    /// <summary>From here until the matching <see cref="Release"/>, output is held; holds nest.</summary>
    void Hold();

    /// <summary>The matching end of a <see cref="Hold"/>: the last one writes what was held, at once.</summary>
    void Release();

    /// <summary>
    /// What is held so far written now, the hold kept (2026-09-29, the user's report: <c>/clear</c> left the pane
    /// unpinned): before the pane asks the console where its cursor is, since the console answers for what it has
    /// received. The frame's synchronized-output codes still keep a terminal that honours them from showing it early.
    /// </summary>
    void Settle();
}

/// <summary>
/// stdout for the interactive screen (2026-09-29): a <see cref="TextWriter"/> over the standard output stream that
/// flushes after every write, as the console's own writer does — until <see cref="Hold"/>, when writes gather until the
/// last <see cref="Release"/> and go out as one. <c>Program</c> installs it with <see cref="Console.SetOut"/> before the
/// Spectre console is created, so Spectre still sees <see cref="Console.Out"/> (its width and terminal detection ask
/// whether its writer is stdout's).
/// </summary>
public sealed class FrameWriter : TextWriter, IFrameHold
{
    private readonly Stream _stream;
    private readonly StreamWriter _writer;
    private readonly Lock _gate = new();
    private int _held;

    /// <param name="stream">The stream the frames go to: <see cref="Console.OpenStandardOutput()"/> in the app; the tests pass a memory stream.</param>
    public FrameWriter(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 16 * 1024, leaveOpen: true)
        {
            AutoFlush = false,
        };
    }

    public override Encoding Encoding => _writer.Encoding;

    /// <summary>Whether a <see cref="Hold"/> is open (tests).</summary>
    public bool Holding
    {
        get { lock (_gate) { return _held > 0; } }
    }

    public void Hold()
    {
        lock (_gate)
        {
            _held++;
        }
    }

    public void Release()
    {
        lock (_gate)
        {
            if (_held == 0)
            {
                return;
            }

            if (--_held == 0)
            {
                FlushLocked();
            }
        }
    }

    public void Settle()
    {
        lock (_gate)
        {
            FlushLocked();
        }
    }

    public override void Write(char value)
    {
        lock (_gate)
        {
            _writer.Write(value);
            FlushUnheld();
        }
    }

    public override void Write(string? value)
    {
        lock (_gate)
        {
            _writer.Write(value);
            FlushUnheld();
        }
    }

    public override void Write(char[] buffer, int index, int count)
    {
        lock (_gate)
        {
            _writer.Write(buffer, index, count);
            FlushUnheld();
        }
    }

    public override void Write(ReadOnlySpan<char> buffer)
    {
        lock (_gate)
        {
            _writer.Write(buffer);
            FlushUnheld();
        }
    }

    public override void WriteLine(string? value)
    {
        lock (_gate)
        {
            _writer.WriteLine(value);
            FlushUnheld();
        }
    }

    /// <summary>What is written so far goes out, unless a hold is open: Spectre flushes after every write it makes, and the frame is the point.</summary>
    public override void Flush()
    {
        lock (_gate)
        {
            FlushUnheld();
        }
    }

    private void FlushUnheld()
    {
        if (_held == 0)
        {
            FlushLocked();
        }
    }

    private void FlushLocked()
    {
        _writer.Flush();
        _stream.Flush();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_gate)
            {
                _held = 0;
                FlushLocked();
                _writer.Dispose();
            }
        }

        base.Dispose(disposing);
    }
}
