namespace NeonSidekick.UI;

/// <summary>
/// The one writer of the console window's title. Windows Terminal shows it on the tab (the
/// console's <c>SetConsoleTitle</c> reaches it through ConPTY; a WT profile with "use
/// application title" off keeps its own name). Nothing else in the process touches
/// <see cref="Console.Title"/>; the chat screen takes this as its <c>setTitle</c> seam.
/// </summary>
public static class ConsoleTitle
{
    /// <summary>
    /// The title last set, null before any (2026-10-07): a Mac's screen capture tells the app's own terminal window from the
    /// terminal's others by it, since <c>Console.Title</c> cannot be read back off Windows.
    /// </summary>
    public static string? Last { get; private set; }

    /// <summary>
    /// Sets the window title. True when it took; false on a detached or redirected console, where
    /// the runtime throws — the title is decoration, never worth a crash.
    /// </summary>
    public static bool TrySet(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        try
        {
            Console.Title = title;
            Last = title;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
