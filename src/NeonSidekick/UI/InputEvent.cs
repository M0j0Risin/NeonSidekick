using Spectre.Console;

namespace NeonSidekick.UI;

/// <summary>A mouse button the input area answers to.</summary>
public enum MouseButton
{
    Left,
    Right,
}

/// <summary>One thing the user did at the console: a key, a paste, a click, a drag, a release, or a wheel notch.</summary>
public abstract record InputEvent
{
    private InputEvent()
    {
    }

    /// <summary>A key press, as <c>Console.ReadKey</c> would report it.</summary>
    public sealed record Key(ConsoleKeyInfo Info) : InputEvent;

    /// <summary>
    /// A burst of characters that arrived together — the terminal's paste — as one block, line
    /// breaks as <c>'\n'</c>, tabs as <c>'\t'</c> (<see cref="PasteBurst"/> decides what is a burst).
    /// It is never a key: no Enter inside it submits anything.
    /// </summary>
    public sealed record Paste(string Text) : InputEvent;

    /// <summary>A mouse button pressed at a cell; <see cref="X"/> / <see cref="Y"/> are screen-buffer coordinates, 0-based.</summary>
    public sealed record Click(int X, int Y, MouseButton Button) : InputEvent;

    /// <summary>
    /// The mouse moved to a cell with the left button held (a selection being dragged); the same
    /// coordinates as <see cref="Click"/>. Only the left button drags: a move with the right
    /// button held is nothing.
    /// </summary>
    public sealed record Drag(int X, int Y) : InputEvent;

    /// <summary>
    /// The left button let go at a cell (2026-09-28, for a picture dragged onto the chat line: the drop); the same
    /// coordinates as <see cref="Click"/>. The right button's release is nothing. Every reader that has no use for one
    /// lets it pass as nothing, as it lets a <see cref="Drag"/> pass.
    /// </summary>
    public sealed record Release(int X, int Y) : InputEvent;

    /// <summary>
    /// The wheel turned at a cell; <see cref="Notches"/> is signed, positive away from the user
    /// (up), one record's worth (a plain wheel is ±1; a fine-resolution one may report more or, for
    /// a fraction of a notch, still 1). Reaches the app only while a pane holds the wheel
    /// (<c>WindowsConsoleInput.HoldWheel</c>); otherwise a notch hands the mouse back to the terminal.
    /// </summary>
    public sealed record Wheel(int X, int Y, int Notches) : InputEvent;
}

/// <summary>
/// The stream <see cref="KeySource"/> reads: keys, clicks, drags, releases and wheel notches in the order they happened. The
/// contracts every source keeps: <see cref="ReadAsync"/> returns null <em>only</em> on cancellation
/// (a Spectre prompt loops on a null and would spin on one from a dead source), and both
/// <see cref="IsAvailable"/> and <see cref="ReadAsync"/> throw <see cref="InvalidOperationException"/>
/// once there is no keyboard — a redirected stdin, or a scripted console that ran dry.
/// </summary>
public interface IInputEvents
{
    /// <summary>An event is queued and <see cref="Read"/> will return it at once.</summary>
    bool IsAvailable { get; }

    /// <summary>The queued event <see cref="Read"/> would return next is a mouse event — a click, a drag, a release or a wheel notch, never a key or a paste (false when none is queued).</summary>
    bool NextIsMouse { get; }

    /// <summary>The next queued event, or null when none is queued. Never blocks.</summary>
    InputEvent? Read();

    /// <summary>The next event, waiting for one; null when <paramref name="cancellationToken"/> fires first.</summary>
    Task<InputEvent?> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>
/// An <see cref="IAnsiConsoleInput"/> (Spectre's <c>DefaultInput</c>, a <c>TestConsoleInput</c>) as
/// an <see cref="IInputEvents"/>: keys only.
///
/// <para>Spectre's real console throws <see cref="TaskCanceledException"/> from its <c>ReadKeyAsync</c>
/// when the token fires (its poll loop is a <c>Task.Delay</c> on it), so that is caught here and
/// turned into the null the interface promises. The wake word's first live run crashed the
/// published exe through exactly that path. <b>[scar]</b></para>
/// </summary>
public sealed class KeyEvents : IInputEvents
{
    private readonly IAnsiConsoleInput _input;

    public KeyEvents(IAnsiConsoleInput input)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
    }

    public bool IsAvailable => _input.IsKeyAvailable();

    public bool NextIsMouse => false;

    public InputEvent? Read() => _input.IsKeyAvailable() && _input.ReadKey(true) is { } key ? new InputEvent.Key(key) : null;

    public async Task<InputEvent?> ReadAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        try
        {
            return await _input.ReadKeyAsync(true, cancellationToken).ConfigureAwait(false) is { } key ? new InputEvent.Key(key) : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }
}
