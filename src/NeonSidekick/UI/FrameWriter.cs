using System.Text;
using NeonSidekick.Diagnostics;

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
/// <para>
/// A terminal that went away (2026-10-25, the Mac pass: iTerm2 closes a window's pty about 5 s after the close, then sends SIGHUP; a
/// redraw tick between the two wrote to the closed pty, and the <see cref="IOException"/> "Input/output error" on a pool thread ended
/// the process with a crash.log while the hangup's own close work ran) is <see cref="Gone"/> from the first write that fails: what was
/// held and everything after is dropped, never thrown, and it is logged once.
/// </para>
/// </summary>
public sealed class FrameWriter : TextWriter, IFrameHold
{
    /// <summary>The log line when the terminal stops taking output; <c>{0}</c> is the failure's message.</summary>
    public const string GoneLogLine = "The terminal took no more output ({0}); the screen's writes are dropped from here.";

    private readonly Stream _stream;
    private readonly StreamWriter _writer;
    private readonly Lock _gate = new();
    private int _held;
    private bool _gone;
    private string? _goneReason;   // set under the lock when a write first fails, logged after it

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

    /// <summary>Whether the terminal stopped taking output (a write failed): everything since is dropped.</summary>
    public bool Gone
    {
        get { lock (_gate) { return _gone; } }
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

        ReportGone();
    }

    public void Settle()
    {
        lock (_gate)
        {
            FlushLocked();
        }

        ReportGone();
    }

    public override void Write(char value)
    {
        lock (_gate)
        {
            if (!_gone)
            {
                try
                {
                    _writer.Write(value);   // a full buffer flushes here too
                    FlushUnheld();
                }
                catch (IOException ex)
                {
                    MarkGone(ex);
                }
            }
        }

        ReportGone();
    }

    public override void Write(string? value)
    {
        lock (_gate)
        {
            if (!_gone)
            {
                try
                {
                    _writer.Write(value);   // a full buffer flushes here too
                    FlushUnheld();
                }
                catch (IOException ex)
                {
                    MarkGone(ex);
                }
            }
        }

        ReportGone();
    }

    public override void Write(char[] buffer, int index, int count)
    {
        lock (_gate)
        {
            if (!_gone)
            {
                try
                {
                    _writer.Write(buffer, index, count);   // a full buffer flushes here too
                    FlushUnheld();
                }
                catch (IOException ex)
                {
                    MarkGone(ex);
                }
            }
        }

        ReportGone();
    }

    public override void Write(ReadOnlySpan<char> buffer)
    {
        lock (_gate)
        {
            if (!_gone)
            {
                try
                {
                    _writer.Write(buffer);   // a full buffer flushes here too
                    FlushUnheld();
                }
                catch (IOException ex)
                {
                    MarkGone(ex);
                }
            }
        }

        ReportGone();
    }

    public override void WriteLine(string? value)
    {
        lock (_gate)
        {
            if (!_gone)
            {
                try
                {
                    _writer.WriteLine(value);   // a full buffer flushes here too
                    FlushUnheld();
                }
                catch (IOException ex)
                {
                    MarkGone(ex);
                }
            }
        }

        ReportGone();
    }

    /// <summary>What is written so far goes out, unless a hold is open: Spectre flushes after every write it makes, and the frame is the point.</summary>
    public override void Flush()
    {
        lock (_gate)
        {
            FlushUnheld();
        }

        ReportGone();
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
        if (_gone)
        {
            return;
        }

        try
        {
            _writer.Flush();
            _stream.Flush();
        }
        catch (IOException ex)
        {
            MarkGone(ex);
        }
    }

    // Under the lock: the terminal is gone from here.
    private void MarkGone(IOException ex)
    {
        _gone = true;
        _goneReason = ex.Message;
    }

    // The one log line for a terminal gone, outside the lock: a log subscriber may draw (2026-10-25).
    private void ReportGone()
    {
        string? reason;
        lock (_gate)
        {
            reason = _goneReason;
            _goneReason = null;
        }

        if (reason is not null)
        {
            DiagnosticLog.Info(ScreenPane.Category, string.Format(System.Globalization.CultureInfo.InvariantCulture, GoneLogLine, reason));
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_gate)
            {
                _held = 0;
                FlushLocked();
                try
                {
                    _writer.Dispose();   // flushes again: a terminal gone still holds what failed
                }
                catch (IOException)
                {
                    // gone already, or now: nothing more can reach it
                }
            }

            ReportGone();
        }

        base.Dispose(disposing);
    }
}
