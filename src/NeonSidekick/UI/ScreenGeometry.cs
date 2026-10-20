namespace NeonSidekick.UI;

/// <summary>
/// Where the cursor is on the screen, which Spectre cannot say. <see cref="ScreenPane"/> keeps
/// its own count of the rows the transcript has used, and asks this once per redraw to correct
/// that count: the terminal is the truth about soft wrapping, and a reflow after a resize moves
/// everything. A null answer means "unknown" and the count stands.
///
/// <para><see cref="ForConsole"/> is the <b>one</b> reader of <c>System.Console</c>'s cursor in
/// the app (next to the UTF-8 forcing in <c>Program.cs</c>), both rows: <c>CursorTop − WindowTop</c> is the
/// screen row under ConPTY, whose console API mirrors the VT state, and Windows Terminal's
/// virtual viewport does not follow the user's scrolling, so the row is stable. It returns null
/// when the console cannot be read at all (a redirected stdout, no console handle): then there
/// is no pane and the screen draws as a plain transcript. Tests pass a scripted function.</para>
/// </summary>
public sealed class ScreenGeometry
{
    private static readonly Func<int?> Unknown = static () => null;

    private readonly Func<int?> _cursorRow;
    private readonly Func<int?> _cursorTop;
    private readonly Func<int?>? _windowTop;

    /// <param name="cursorRow">The cursor's row within the window.</param>
    /// <param name="cursorTop">The cursor's row within the screen buffer — the frame a mouse click is reported in; null = unknown.</param>
    /// <param name="windowTop">The buffer row the window's top row is (<see cref="WindowTop"/>); null = the cursor's buffer row less its window row.</param>
    public ScreenGeometry(Func<int?> cursorRow, Func<int?>? cursorTop = null, Func<int?>? windowTop = null)
    {
        _cursorRow = cursorRow ?? throw new ArgumentNullException(nameof(cursorRow));
        _cursorTop = cursorTop ?? Unknown;
        _windowTop = windowTop;
    }

    /// <summary>The cursor's screen row (0 = the top row of the window), or null when unknown.</summary>
    public int? CursorRow() => Read(_cursorRow);

    /// <summary>The cursor's buffer row (0 = the top of the screen buffer, scrollback included), or null when unknown. A click's <c>Y</c> is in this frame.</summary>
    public int? CursorTop() => Read(_cursorTop);

    /// <summary>
    /// The buffer row of the window's top row (2026-10-20, the welcome splash's robot following the mouse): a pointer's <c>Y</c> less
    /// this is its screen row. Asked on every frame that saw the pointer move, so it never asks the terminal: the console's own
    /// number on Windows, 0 for a terminal whose cursor is asked by a query (its reports are in screen cells already). Null when unknown.
    /// </summary>
    public int? WindowTop() => _windowTop is { } top ? Read(top) : CursorTop() - CursorRow();

    private static int? Read(Func<int?> probe)
    {
        try
        {
            return probe();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or PlatformNotSupportedException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>
    /// The real console's geometry, or null when the cursor cannot be read (stdout redirected, or
    /// no console). Probes once here so a pane is only ever built over a console that answers.
    /// </summary>
    public static ScreenGeometry? ForConsole()
    {
        if (Console.IsOutputRedirected)
        {
            return null;
        }

        var geometry = new ScreenGeometry(ReadConsoleCursorRow, ReadConsoleCursorTop, ReadConsoleWindowTop);
        return geometry.CursorRow() is null ? null : geometry;
    }

    /// <summary>
    /// A terminal whose cursor is asked by a reader that owns stdin (2026-10-06, the macOS build: <see cref="UnixConsoleInput.QueryCursor"/>).
    /// A VT terminal reports screen cells and its mouse reports are in the same frame, so the screen row serves as both rows here.
    /// </summary>
    public static ScreenGeometry FromCursorQuery(Func<int?> screenRow)
    {
        ArgumentNullException.ThrowIfNull(screenRow);
        return new ScreenGeometry(screenRow, screenRow, static () => 0);
    }

    private static int? ReadConsoleCursorRow()
    {
        int row = Console.CursorTop - Console.WindowTop;
        return row >= 0 ? row : null;
    }

    private static int? ReadConsoleCursorTop()
    {
        int top = Console.CursorTop;
        return top >= 0 ? top : null;
    }

    private static int? ReadConsoleWindowTop()
    {
        int top = Console.WindowTop;
        return top >= 0 ? top : null;
    }
}
