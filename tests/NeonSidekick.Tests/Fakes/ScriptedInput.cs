using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.Tests.Fakes;

/// <summary>
/// An <see cref="IAnsiConsoleInput"/> (and <see cref="IInputEvents"/>: keys, mouse clicks and drags in
/// one queue) whose failure modes are scriptable, for the cases <c>TestConsoleInput</c> cannot
/// produce: <see cref="NoKeyboard"/> makes <see cref="IsKeyAvailable"/> throw the way a redirected
/// real console does, and an empty queue makes <see cref="ReadKeyAsync"/> wait for cancellation
/// and then <b>throw <see cref="TaskCanceledException"/></b>, exactly as Spectre's <c>DefaultInput</c>
/// does (its poll loop is a <c>Task.Delay</c> on the token). An earlier version returned null
/// here, and the first live wake word crashed the published exe because nothing above it expected
/// the throw. <see cref="ReturnNullOnCancel"/> restores the old behaviour for the one test that
/// pins both shapes. <see cref="Completed"/> is a source that is gone (the real reader's channel
/// closed): every read throws <see cref="InvalidOperationException"/>.
/// </summary>
public sealed class ScriptedInput : IAnsiConsoleInput, IInputEvents
{
    private readonly Queue<InputEvent> _events = new();

    public bool NoKeyboard { get; set; }

    /// <summary>Return null on cancellation instead of throwing; the real console throws.</summary>
    public bool ReturnNullOnCancel { get; set; }

    /// <summary>The source is gone: reads throw <see cref="InvalidOperationException"/> once the queue is empty.</summary>
    public bool Completed { get; set; }

    /// <summary>
    /// How long <see cref="ReadAsync"/> waits on an empty queue before it throws
    /// <see cref="TimeoutException"/>: a script that ran dry. A screen test whose expected turn,
    /// hit or pane never came used to wait here for ever — the first release run sat in one for
    /// GitHub's six-hour maximum (2026-09-21). A <see cref="TimeoutException"/> rather than the
    /// <see cref="InvalidOperationException"/> of <see cref="Completed"/>, which the input line
    /// takes for the end of input and exits on quietly: this one fails the test with its message.
    /// Thirty seconds is above every wait a test makes on purpose (the longest, the interrupt
    /// tests' held syntheses, are five).
    /// </summary>
    public TimeSpan DryTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The message of the <see cref="TimeoutException"/> a dry script throws. Pinned.</summary>
    public static string DryMessage(TimeSpan waited) => string.Create(System.Globalization.CultureInfo.InvariantCulture,
        $"The script ran dry: the read waited {waited.TotalSeconds:F0} s for a key nothing pushed. What the script expected next (a turn, a wake hit, a pane) did not happen.");

    /// <summary>
    /// Runs once each time <see cref="ReadKeyAsync"/> finds the queue empty, before it waits: the
    /// timer tests advance the manual clock here (the screen is blocked on the read, as it would be
    /// on a real keyboard) or push the keys that come next.
    /// </summary>
    public Action? OnWait { get; set; }

    public ScriptedInput Push(params ConsoleKeyInfo[] keys)
    {
        foreach (var key in keys)
        {
            _events.Enqueue(new InputEvent.Key(key));
        }

        return this;
    }

    /// <summary>The terminal's paste of <paramref name="text"/> (one block, as the console reader delivers a burst), queued in order with the keys.</summary>
    public ScriptedInput PushPaste(string text)
    {
        _events.Enqueue(new InputEvent.Paste(text));
        return this;
    }

    /// <summary>A mouse click at buffer cell (<paramref name="x"/>, <paramref name="y"/>), queued in order with the keys.</summary>
    public ScriptedInput PushClick(int x, int y, MouseButton button = MouseButton.Left)
    {
        _events.Enqueue(new InputEvent.Click(x, y, button));
        return this;
    }

    /// <summary>The mouse dragged (left button held) to buffer cell (<paramref name="x"/>, <paramref name="y"/>), queued in order with the keys.</summary>
    public ScriptedInput PushDrag(int x, int y)
    {
        _events.Enqueue(new InputEvent.Drag(x, y));
        return this;
    }

    /// <summary>The left button let go at buffer cell (<paramref name="x"/>, <paramref name="y"/>) (2026-09-28, a drop), queued in order with the keys.</summary>
    public ScriptedInput PushRelease(int x, int y)
    {
        _events.Enqueue(new InputEvent.Release(x, y));
        return this;
    }

    /// <summary>The wheel turned <paramref name="notches"/> (positive = up, away from the user) at buffer cell (<paramref name="x"/>, <paramref name="y"/>), queued in order with the keys.</summary>
    public ScriptedInput PushWheel(int notches, int x = 0, int y = 0)
    {
        _events.Enqueue(new InputEvent.Wheel(x, y, notches));
        return this;
    }

    // ── IInputEvents ────────────────────────────────────────────────────────

    public bool IsAvailable
    {
        get
        {
            ThrowIfNoKeyboard();
            if (_events.Count == 0 && Completed)
            {
                throw new InvalidOperationException("The input source is gone.");
            }

            return _events.Count > 0;
        }
    }

    public bool NextIsMouse => _events.Count > 0 && _events.Peek() is InputEvent.Click or InputEvent.Drag or InputEvent.Release or InputEvent.Wheel;

    public InputEvent? Read()
    {
        ThrowIfNoKeyboard();
        return _events.Count > 0 ? _events.Dequeue() : null;
    }

    public async Task<InputEvent?> ReadAsync(CancellationToken cancellationToken)
    {
        ThrowIfNoKeyboard();
        if (_events.Count == 0)
        {
            if (Completed)
            {
                throw new InvalidOperationException("The input source is gone.");
            }

            OnWait?.Invoke();
        }

        var dry = System.Diagnostics.Stopwatch.StartNew();
        while (_events.Count == 0)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                if (ReturnNullOnCancel)
                {
                    return null;
                }

                throw new TaskCanceledException("A task was canceled.");
            }

            if (dry.Elapsed >= DryTimeout)
            {
                throw new TimeoutException(DryMessage(DryTimeout));
            }

            await Task.Delay(5, CancellationToken.None);
        }

        return _events.Dequeue();
    }

    // ── IAnsiConsoleInput (keys only) ───────────────────────────────────────

    public bool IsKeyAvailable()
    {
        ThrowIfNoKeyboard();
        DropMouse();
        return _events.Count > 0;
    }

    public ConsoleKeyInfo? ReadKey(bool intercept)
    {
        ThrowIfNoKeyboard();
        DropMouse();
        return _events.Count > 0 && _events.Dequeue() is InputEvent.Key key ? key.Info : null;
    }

    public async Task<ConsoleKeyInfo?> ReadKeyAsync(bool intercept, CancellationToken cancellationToken)
    {
        while (true)
        {
            var e = await ReadAsync(cancellationToken);
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

    private void DropMouse()
    {
        while (NextIsMouse)
        {
            _events.Dequeue();
        }
    }

    private void ThrowIfNoKeyboard()
    {
        if (NoKeyboard)
        {
            throw new InvalidOperationException("Failed to read input in non-interactive mode.");
        }
    }
}
