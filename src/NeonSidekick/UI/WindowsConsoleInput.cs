using System.Diagnostics;
using System.Threading.Channels;
using NeonSidekick.Diagnostics;
using Spectre.Console;
using static NeonSidekick.UI.ConsoleInputNative;

namespace NeonSidekick.UI;

/// <summary>
/// The console's input buffer read directly — keys <em>and</em> mouse clicks and drags — in place of
/// Spectre's <c>DefaultInput</c>, which goes through <c>Console.ReadKey</c> and throws mouse
/// records away. The ONE reader of the console input handle in the app; <see cref="KeySource"/>
/// wraps it and nothing else may touch <c>System.Console</c>'s input while it lives (a
/// <c>Console.KeyAvailable</c> elsewhere would eat the mouse records while peeking).
///
/// <para>Created only for the interactive screen on a real console (<see cref="TryCreate"/>), and it
/// starts <em>released</em>: the mouse is the terminal's until the input line asks for it with
/// <see cref="Capture"/> (a draft to click into) and the terminal's again once the line lets go.
/// A press is seen by exactly one of the two, and the terminal cannot start a selection from a
/// press it never saw, so who owns the mouse is decided before the press: the screen holds it
/// for its whole run (2026-09-17, the user's call once the transcript was the app's to scroll —
/// from 2026-09-13 to then the draft decided, so the terminal selected at an empty line), and a
/// pane keeps it (a <c>Mouse in menus</c> setting could hand it to the terminal there until 2026-09-21). Two modes, both from the original: released = the original with
/// <c>ENABLE_EXTENDED_FLAGS</c> on (so quick-edit is honoured as the shell had it — the classic
/// console's drag-select) and <c>ENABLE_VIRTUAL_TERMINAL_INPUT</c> off (with it on, keys arrive as
/// ESC-led sequences the key loop cannot parse); captured = that with <c>ENABLE_MOUSE_INPUT</c> on
/// and <c>ENABLE_QUICK_EDIT_MODE</c> off (quick-edit suppresses mouse events). Both drop
/// <c>ENABLE_PROCESSED_INPUT</c> (since 2026-09-17), so Ctrl+C is a key record the screen decides
/// about — copy, stop the speech, cancel, twice to exit — and never <c>Console.CancelKeyPress</c>
/// while the reader lives; Ctrl+Break is the host's whatever the mode and stays the app token.
/// Both drop <c>ENABLE_LINE_INPUT</c> and <c>ENABLE_ECHO_INPUT</c> as well (2026-10-01), so Ctrl+S is the
/// <c>/server</c> chord and never the host's pause key.
/// The released mode with processed, line and echo input as the shell had them comes back on <see cref="Dispose"/>.</para>
///
/// <para>Under Windows Terminal the mouse mode reaches the terminal through ConPTY, and while it is
/// on <em>every</em> mouse event comes to the app unless Shift is held (Shift+drag is the terminal's own
/// selection, which the app never sees; a plain drag is the input line's) — including the wheel.
/// While the wheel is held (<see cref="HoldWheel"/>: the screen's whole run since 2026-09-17, the
/// transcript region scrolling by it; a pane's rows under one) a notch is queued as an
/// <see cref="InputEvent.Wheel"/>; a notch while the mouse is taken but the wheel is not hands the
/// mouse back (<c>ENABLE_MOUSE_INPUT</c> off, the notch consumed) until the next key press takes it
/// again while it is still wanted — the shape of 2026-09-12, when the transcript was the terminal's
/// scrollback, kept as the rule for a holder that does not want the wheel. A mode change reaches ConPTY only with
/// the next console write (microsoft/terminal #15711), so <see cref="ModeChanged"/> is raised — on
/// the pool, never on the reader thread — and the screen answers with a write that draws nothing new.</para>
///
/// <para>A background thread waits on the handle in short slices (so it can be stopped) and reads
/// one record at a time into a channel; <see cref="ConsoleInputRecords"/> decides what each record
/// is. The key records the buffer held together are one run, and <see cref="PasteBurst"/> decides
/// whether the run is the terminal's paste (one <see cref="InputEvent.Paste"/>) or keys. Dispose
/// stops and joins the thread <em>before</em> restoring the mode, or a late key record would
/// switch the mouse back on after the restore and the shell would inherit it.</para>
/// </summary>
public sealed class WindowsConsoleInput : IAnsiConsoleInput, IInputEvents, IDisposable
{
    public const string Category = "Screen";
    public const string NoKeyboard = "Failed to read input: the console input is gone.";

    /// <summary>How long the reader waits for a record before checking whether it should stop.</summary>
    public static readonly TimeSpan WaitSlice = TimeSpan.FromMilliseconds(250);

    private const uint WaitTimeout = 0x00000102;

    private readonly IntPtr _handle;
    private readonly uint _originalMode;
    private readonly Channel<InputEvent> _events = Channel.CreateUnbounded<InputEvent>(new UnboundedChannelOptions { SingleWriter = true });
    private readonly object _gate = new();
    private Thread? _thread;
    private volatile bool _stop;
    private bool _disposed;
    private bool _captured;
    private volatile bool _wanted;
    private volatile bool _wheelWanted;

    private WindowsConsoleInput(IntPtr handle, uint originalMode)
    {
        _handle = handle;
        _originalMode = originalMode;
    }

    /// <summary>Raised (on the thread pool) after the mouse was taken or handed back: the screen should write something.</summary>
    public Action? ModeChanged { get; set; }

    /// <summary>The mouse is the app's right now (false while released, and between a wheel notch and the next key).</summary>
    public bool MouseCaptured
    {
        get
        {
            lock (_gate)
            {
                return _captured;
            }
        }
    }

    /// <summary>
    /// Takes the mouse (<paramref name="on"/>) or hands it back to the terminal: the screen takes
    /// it at its start and every pane keeps it (2026-09-21; a setting could hand it back before).
    /// While taken without the wheel (<see cref="HoldWheel"/>) a notch hands it back and
    /// the next key press takes it again; while handed back, keys change nothing.
    /// </summary>
    public void Capture(bool on)
    {
        _wanted = on;
        SetMouse(on);
    }

    /// <summary>
    /// Keeps the wheel too (<paramref name="on"/>): while the mouse is taken a notch is queued as an
    /// <see cref="InputEvent.Wheel"/> instead of handing the mouse back. The screen sets it with its
    /// take, a pane clears and sets it with its release and re-take; it means nothing while the mouse is released.
    /// </summary>
    public void HoldWheel(bool on) => _wheelWanted = on;

    /// <summary>
    /// The reader over the real console's input, or null when there is none to take: not
    /// Windows, stdin redirected, no console handle, or a console whose mode cannot be read or
    /// set (then Spectre's own input serves, keys only, as before). Starts released.
    /// </summary>
    public static WindowsConsoleInput? TryCreate()
    {
        if (!OperatingSystem.IsWindows() || Console.IsInputRedirected)
        {
            return null;
        }

        IntPtr handle = GetStdHandle(StdInputHandle);
        if (handle == IntPtr.Zero || handle == InvalidHandleValue || !GetConsoleMode(handle, out uint mode))
        {
            return null;
        }

        var input = new WindowsConsoleInput(handle, mode);
        if (!SetConsoleMode(handle, Mode(mode, captured: false)))
        {
            return null;
        }

        input._thread = new Thread(input.Run) { IsBackground = true, Name = "console-input" };
        input._thread.Start();
        return input;
    }

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
                throw new InvalidOperationException(NoKeyboard);
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
            throw new InvalidOperationException(NoKeyboard, ex);
        }
    }

    // ── IAnsiConsoleInput (keys only; KeySource reads the events, this is the type's due) ──

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

    private void Run()
    {
        uint buttons = 0;
        // The key records the buffer held together, and when the last paste ended (PasteBurst).
        var run = new List<(ConsoleKeyInfo Key, int Repeat)>();
        long? pasteEndedAt = null;
        try
        {
            while (!_stop)
            {
                uint wait = WaitForSingleObject(_handle, (uint)WaitSlice.TotalMilliseconds);
                if (wait == WaitTimeout)
                {
                    continue;
                }

                if (wait != WaitObject0 || _stop)
                {
                    break;
                }

                if (!ReadConsoleInputW(_handle, out var record, 1, out uint read) || read == 0)
                {
                    break;
                }

                switch (record.EventType)
                {
                    case KeyEvent:
                        if (ConsoleInputRecords.TryTranslateKey(record.KeyEvent, out var key, out int repeat))
                        {
                            if (_wanted)
                            {
                                SetMouse(true);
                            }

                            run.Add((key, repeat));
                        }

                        // A paste is a burst: while the buffer still holds records, they are this run's.
                        if (GetNumberOfConsoleInputEvents(_handle, out uint pending) && pending > 0)
                        {
                            continue;
                        }

                        FlushRun();
                        break;

                    case MouseEvent:
                        FlushRun();
                        if (ConsoleInputRecords.TryTranslateMouse(record.MouseEvent, ref buttons, out var mouse, out bool wheel))
                        {
                            if (mouse is InputEvent.Wheel && !(_wheelWanted && MouseCaptured))
                            {
                                // Nobody holds the wheel: the notch is the terminal's scrollback's.
                                SetMouse(false);
                            }
                            else if (MouseCaptured)
                            {
                                // A drag is dozens of records per gesture: the click is the one worth a line.
                                if (mouse is InputEvent.Click click)
                                {
                                    DiagnosticLog.Debug(Category, $"Mouse {click.Button} click at column {click.X}, buffer row {click.Y}.");
                                }

                                _events.Writer.TryWrite(mouse);
                            }
                        }
                        else if (wheel)
                        {
                            SetMouse(false);
                        }

                        break;

                    default:
                        // A focus, menu or buffer-size record: nothing to the app, but it ends a run.
                        FlushRun();
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Warn(Category, $"Console input reader stopped: {ex.Message}");
        }

        _events.Writer.TryComplete();

        // The run so far as events: a paste when it arrived as one, or when it follows a paste
        // within the grace (ConPTY chunks a long paste), else the keys.
        void FlushRun()
        {
            if (run.Count == 0)
            {
                return;
            }

            long now = Stopwatch.GetTimestamp();
            bool joins = pasteEndedAt is long ended && Stopwatch.GetElapsedTime(ended, now) < PasteBurst.Grace;
            var events = PasteBurst.Coalesce(run, joins, out bool endsAsPaste);
            run.Clear();
            foreach (var e in events)
            {
                if (e is InputEvent.Paste paste)
                {
                    DiagnosticLog.Debug(Category, $"Paste of {paste.Text.Length} characters{(joins ? " (continued)" : "")}.");
                }

                _events.Writer.TryWrite(e);
            }

            pasteEndedAt = endsAsPaste ? Stopwatch.GetTimestamp() : null;
        }
    }

    /// <summary>
    /// The input mode for <paramref name="original"/> with the mouse taken or not, while the reader
    /// lives: <see cref="Released"/> with processed input off (Ctrl+C is a key record for the
    /// screen, since 2026-09-17; Ctrl+Break stays the host's); captured adds mouse input and drops
    /// quick-edit, released leaves both as the shell had them.
    /// Line and echo input go too (2026-10-01, the user's report that Ctrl+S did nothing): with line input on, the host
    /// takes Ctrl+S (Ctrl without Alt) as its pause key — output suspended, the key eaten, and the next press eaten to
    /// resume — so the <c>/server</c> chord never arrived. The reader takes raw records (<c>ReadConsoleInputW</c>), so
    /// neither bit ever did anything for the app; echo goes with line input, which the host requires.
    /// </summary>
    internal static uint Mode(uint original, bool captured)
    {
        uint mode = Released(original) & ~(EnableProcessedInput | EnableLineInput | EnableEchoInput);
        return captured ? (mode | EnableMouseInput) & ~EnableQuickEditMode : mode;
    }

    /// <summary>
    /// The mode the shell gets back on <see cref="Dispose"/>: <paramref name="original"/> with the
    /// extended bit set so quick-edit is what the mode says and VT input off — processed input as
    /// the shell had it, so its Ctrl+C works again.
    /// </summary>
    internal static uint Released(uint original) => (original | EnableExtendedFlags) & ~EnableVirtualTerminalInput;

    /// <summary>The mouse taken (<paramref name="on"/>) or handed back; true when it is now as asked.</summary>
    private bool SetMouse(bool on)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            if (_captured == on)
            {
                return true;
            }

            if (!SetConsoleMode(_handle, Mode(_originalMode, on)))
            {
                return false;
            }

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

        return true;
    }

    public void Dispose()
    {
        _stop = true;
        _thread?.Join();
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            SetConsoleMode(_handle, Released(_originalMode));
        }

        _events.Writer.TryComplete();
    }
}
