using System.Runtime.Versioning;
using System.Threading.Channels;
using NeonSidekick.Diagnostics;
using Spectre.Console;
using static NeonSidekick.UI.TermiosNative;

namespace NeonSidekick.UI;

/// <summary>
/// The terminal's input read directly on macOS (2026-10-06, the macOS build), the Unix twin of <see cref="WindowsConsoleInput"/>:
/// keys, the terminal's paste and the mouse, with Ctrl+C a key the screen decides about. The ONE reader of stdin while it lives —
/// nothing else may touch <c>System.Console</c>'s input, and nothing may ask .NET for the cursor (<c>Console.CursorTop</c> on
/// Unix writes a query and reads the answer from stdin, and puts the terminal back in its own mode around that): the screen's
/// geometry asks through <see cref="QueryCursor"/> instead, the answer read here like any other input.
///
/// <para>Raw mode is the original termios with line editing, echo, signals, flow control and CR→LF off
/// (<see cref="TermiosNative.Raw"/>), set at <see cref="TryCreate"/> and the original restored at <see cref="Dispose"/> and at process
/// exit. Bracketed paste is turned on for the run, so a paste arrives as one <see cref="InputEvent.Paste"/>. The mouse is the
/// terminal's until <see cref="Capture"/> takes it: then the terminal reports presses, drags (button-event tracking) and the
/// wheel in SGR form; the terminal keeps its own selection under a bypass key (Fn in Terminal.app, Option in iTerm2). A wheel notch while
/// nobody holds the wheel hands the mouse back until the next key, as on Windows.</para>
///
/// <para>A background thread polls stdin in short slices (so it can stop) and feeds a <see cref="VtInputParser"/>; an ESC with
/// nothing after it within <see cref="EscapeTimeout"/> is the ESC key. Each slice also checks the terminal is still raw: .NET
/// puts its own saved mode back after a child process that used the terminal, and on SIGCONT, and the reader puts its own back.</para>
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class UnixConsoleInput : IAnsiConsoleInput, IInputEvents, IMouseInput, IDisposable
{
    public const string Category = "Screen";

    /// <summary>How long a lone ESC waits for the rest of a sequence before it is the ESC key.</summary>
    public static readonly TimeSpan EscapeTimeout = TimeSpan.FromMilliseconds(50);

    /// <summary>How long the reader waits for input before checking whether it should stop.</summary>
    public static readonly TimeSpan WaitSlice = TimeSpan.FromMilliseconds(250);

    /// <summary>How long <see cref="QueryCursor"/> waits for the terminal's answer.</summary>
    public static readonly TimeSpan CursorTimeout = TimeSpan.FromMilliseconds(500);

    private const string MouseOn = "\x1b[?1000h\x1b[?1002h\x1b[?1006h";
    private const string MouseOff = "\x1b[?1006l\x1b[?1002l\x1b[?1000l";
    private const string PasteOn = "\x1b[?2004h";
    private const string PasteOff = "\x1b[?2004l";

    private readonly Termios _original;
    private readonly Channel<InputEvent> _events = Channel.CreateUnbounded<InputEvent>(new UnboundedChannelOptions { SingleWriter = false });
    private readonly VtInputParser _parser = new();
    private readonly object _gate = new();
    private readonly object _cursorGate = new();
    private TaskCompletionSource<(int Row, int Column)>? _cursor;
    private Thread? _thread;
    private volatile bool _stop;
    private bool _disposed;
    private bool _captured;
    private volatile bool _wanted;
    private volatile bool _wheelWanted;

    private UnixConsoleInput(Termios original)
    {
        _original = original;
        _parser.CursorReport = (row, column) => _cursor?.TrySetResult((row, column));
    }

    public Action? ModeChanged { get; set; }

    /// <summary>
    /// The reader over the real terminal, or null when there is none to take: not macOS, stdin redirected or not a terminal, or
    /// a terminal whose attributes cannot be read or set (then Spectre's own input serves, keys only).
    /// </summary>
    public static unsafe UnixConsoleInput? TryCreate()
    {
        if (!OperatingSystem.IsMacOS() || Console.IsInputRedirected || IsATty(StdinFileNo) != 1)
        {
            return null;
        }

        Termios original;
        if (TcGetAttr(StdinFileNo, &original) != 0)
        {
            return null;
        }

        var raw = Raw(original);
        if (TcSetAttr(StdinFileNo, TcsaNow, &raw) != 0)
        {
            return null;
        }

        var input = new UnixConsoleInput(original);
        Write(PasteOn);
        AppDomain.CurrentDomain.ProcessExit += input.OnProcessExit;
        input._thread = new Thread(input.Run) { IsBackground = true, Name = "console-input" };
        input._thread.Start();
        DiagnosticLog.Debug(Category, "Terminal input in raw mode (termios), bracketed paste on.");
        return input;
    }

    public void Capture(bool on)
    {
        _wanted = on;
        SetMouse(on);
    }

    public void HoldWheel(bool on) => _wheelWanted = on;

    /// <summary>A key queued as though the terminal had sent it (the Windows reader's <c>Inject</c>; nothing calls it on macOS yet).</summary>
    public void Inject(ConsoleKeyInfo key) => _events.Writer.TryWrite(new InputEvent.Key(key));

    /// <summary>
    /// The cursor's 0-based screen row and column, asked of the terminal (<c>ESC[6n</c>) and read from the answer by this
    /// reader; null when the terminal does not answer within <see cref="CursorTimeout"/>. Any thread but the reader's.
    /// </summary>
    public (int Row, int Column)? QueryCursor()
    {
        if (_stop || Thread.CurrentThread == _thread)
        {
            return null;
        }

        lock (_cursorGate)
        {
            var waiting = new TaskCompletionSource<(int Row, int Column)>(TaskCreationOptions.RunContinuationsAsynchronously);
            _cursor = waiting;
            _parser.AwaitingCursor = true;
            Write("\x1b[6n");
            bool answered = waiting.Task.Wait(CursorTimeout);
            _parser.AwaitingCursor = false;
            _cursor = null;
            return answered ? waiting.Task.Result : null;
        }
    }

    /// <summary>The screen's geometry over <see cref="QueryCursor"/>: the one way the cursor is asked while this reader owns stdin.</summary>
    public ScreenGeometry Geometry() => ScreenGeometry.FromCursorQuery(() => QueryCursor()?.Row);

    // ── IInputEvents ────────────────────────────────────────────────────────

    public bool IsAvailable
    {
        get
        {
            if (_events.Reader.TryPeek(out _))
            {
                return true;
            }

            if (_events.Reader.Completion.IsCompleted)
            {
                throw new InvalidOperationException(WindowsConsoleInput.NoKeyboard);
            }

            return false;
        }
    }

    public bool NextIsMouse => _events.Reader.TryPeek(out var e) && e is InputEvent.Click or InputEvent.Drag or InputEvent.Release or InputEvent.Wheel;

    public InputEvent? Read() => _events.Reader.TryRead(out var e) ? e : null;

    public async Task<InputEvent?> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _events.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException ex)
        {
            throw new InvalidOperationException(WindowsConsoleInput.NoKeyboard, ex);
        }
    }

    // ── IAnsiConsoleInput (keys only; KeySource reads the events) ──

    public bool IsKeyAvailable()
    {
        while (NextIsMouse)
        {
            _events.Reader.TryRead(out _);
        }

        return IsAvailable;
    }

    public ConsoleKeyInfo? ReadKey(bool intercept)
    {
        while (_events.Reader.TryRead(out var e))
        {
            if (e is InputEvent.Key key)
            {
                return key.Info;
            }
        }

        return null;
    }

    public async Task<ConsoleKeyInfo?> ReadKeyAsync(bool intercept, CancellationToken cancellationToken)
    {
        while (true)
        {
            var e = await ReadAsync(cancellationToken).ConfigureAwait(false);
            if (e is null)
            {
                return null;
            }

            if (e is InputEvent.Key key)
            {
                return key.Info;
            }
        }
    }

    // ── The reader thread ───────────────────────────────────────────────────

    private unsafe void Run()
    {
        var buffer = new byte[4096];
        var parsed = new List<InputEvent>();
        try
        {
            while (!_stop)
            {
                int timeout = _parser.HasPending ? (int)EscapeTimeout.TotalMilliseconds : (int)WaitSlice.TotalMilliseconds;
                var fd = new PollFd { Fd = StdinFileNo, Events = PollIn };
                int ready = Poll(&fd, 1, timeout);
                if (_stop)
                {
                    break;
                }

                if (ready < 0)
                {
                    // EINTR (a signal landed, SIGWINCH on every resize): try again; anything else means no terminal.
                    if (System.Runtime.InteropServices.Marshal.GetLastPInvokeError() == Eintr)
                    {
                        continue;
                    }

                    break;
                }

                if (ready == 0)
                {
                    if (_parser.HasPending)
                    {
                        _parser.Flush(parsed);
                        Deliver(parsed);
                    }

                    KeepRaw();
                    continue;
                }

                nint read;
                fixed (byte* p = buffer)
                {
                    read = TermiosNative.Read(StdinFileNo, p, (nuint)buffer.Length);
                }

                if (read <= 0)
                {
                    // End of input (the terminal went away) or an error other than a signal: no keyboard any more.
                    if (read < 0 && System.Runtime.InteropServices.Marshal.GetLastPInvokeError() == Eintr)
                    {
                        continue;
                    }

                    break;
                }

                _parser.Feed(buffer.AsSpan(0, (int)read), parsed);
                Deliver(parsed);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Warn(Category, $"Terminal input reader stopped: {ex.Message}");
        }

        _events.Writer.TryComplete();
    }

    private void Deliver(List<InputEvent> parsed)
    {
        foreach (var e in parsed)
        {
            switch (e)
            {
                case InputEvent.Key or InputEvent.Paste:
                    if (_wanted)
                    {
                        SetMouse(true);
                    }

                    if (e is InputEvent.Paste paste)
                    {
                        DiagnosticLog.Debug(Category, $"Paste of {paste.Text.Length} characters.");
                    }

                    _events.Writer.TryWrite(e);
                    break;

                case InputEvent.Wheel when !(_wheelWanted && MouseCaptured):
                    // Nobody holds the wheel: the notch is the terminal's scrollback's.
                    SetMouse(false);
                    break;

                default:
                    if (MouseCaptured)
                    {
                        if (e is InputEvent.Click click)
                        {
                            DiagnosticLog.Debug(Category, $"Mouse {click.Button} click at column {click.X}, screen row {click.Y}.");
                        }

                        _events.Writer.TryWrite(e);
                    }

                    break;
            }
        }

        parsed.Clear();
    }

    private bool MouseCaptured
    {
        get
        {
            lock (_gate)
            {
                return _captured;
            }
        }
    }

    private void SetMouse(bool on)
    {
        lock (_gate)
        {
            if (_disposed || _captured == on)
            {
                return;
            }

            Write(on ? MouseOn : MouseOff);
            _captured = on;
        }

        DiagnosticLog.Debug(Category, on ? "Mouse captured." : _wanted ? "Mouse handed back to the terminal (wheel)." : "Mouse handed back to the terminal.");
        if (ModeChanged is { } changed)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    changed();
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Warn(Category, $"Console mode flush failed: {ex.Message}");
                }
            });
        }
    }

    // .NET puts back the mode it saved after a child that used the terminal, and on SIGCONT; the reader's slice puts its own
    // back. Never while disposed: then the original is the right one.
    private unsafe void KeepRaw()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            Termios current;
            if (TcGetAttr(StdinFileNo, &current) == 0 && !IsRaw(current))
            {
                var raw = Raw(_original);
                TcSetAttr(StdinFileNo, TcsaNow, &raw);
                DiagnosticLog.Debug(Category, "Terminal raw mode restored (something put the line discipline back).");
            }
        }
    }

    private static void Write(string sequence)
    {
        lock (Console.Out)
        {
            Console.Out.Write(sequence);
            Console.Out.Flush();
        }
    }

    private void OnProcessExit(object? sender, EventArgs e) => Restore();

    private unsafe void Restore()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_captured)
            {
                Write(MouseOff);
                _captured = false;
            }

            Write(PasteOff);
            var original = _original;
            TcSetAttr(StdinFileNo, TcsaNow, &original);
        }
    }

    public void Dispose()
    {
        _stop = true;
        _thread?.Join();
        Restore();
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        _events.Writer.TryComplete();
    }
}
