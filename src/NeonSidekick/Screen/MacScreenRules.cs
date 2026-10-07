using System.Globalization;

namespace NeonSidekick.Screen;

/// <summary>A display as CoreGraphics gives it: its id, its frame in global points (top-left origin), its size in pixels, whether it is the main one.</summary>
public sealed record MacDisplay(uint Id, double X, double Y, double Width, double Height, int PixelWidth, int PixelHeight, bool Main)
{
    /// <summary>Pixels per point (2 on Retina, 1 on most external screens); 1 when the frame is empty.</summary>
    public double Scale => Width > 0 ? PixelWidth / Width : 1;
}

/// <summary>One row of <c>CGWindowListCopyWindowInfo</c>, front to back: the fields the rules read, the frame in global points.</summary>
public sealed record MacWindowEntry(long Id, int Layer, double Alpha, bool OnScreen, int Sharing, string Title, string Owner, int OwnerPid, double X, double Y, double Width, double Height);

/// <summary>Where one display's picture goes on the canvas (<see cref="MacScreenRules.Plan"/>): the display, and its rectangle in canvas pixels (it may run past the edges, which clip).</summary>
public sealed record MacPlacement(uint Display, ScreenRect Into);

/// <summary>A capture of an area as <see cref="MacScreenRules.Plan"/> lays it out: the canvas's size and the displays drawn onto it.</summary>
public sealed record MacCanvas(int Width, int Height, IReadOnlyList<MacPlacement> Placements);

/// <summary>
/// The screen capture's decisions on a Mac (2026-10-07, Stage 2), pure so the tests reach them anywhere; <see cref="MacScreenSystem"/>
/// is the native side. <b>One space:</b> CoreGraphics' geometry is global points, and a Mac's displays need not share a scale, so
/// the <see cref="ScreenRect"/>s the portable code sees are points times the largest scale among the displays (the user's call):
/// one display, or displays of one scale, read as their physical pixels exactly as on Windows (a 5K panel is 5120x2880); a 1×
/// screen beside a Retina one lists at twice its pixels, is captured alone at its own pixels, and is scaled up inside <c>all</c>.
/// <b>The windows:</b> those Windows would list by its own rules — the normal layer (0: the menu bar is 24, its status items 25,
/// the Dock 20, Notification Center far below), on the screen, not fully transparent, not kept from capture (sharing state none),
/// titled, more than a point each way. The id is the CG window number, ScreenCaptureKit's <c>windowID</c> too, stable while the
/// window lives.
/// </summary>
public static class MacScreenRules
{
    /// <summary>The largest pixels-per-point among <paramref name="displays"/>, at least 1: the scale of the shared space. Pure.</summary>
    public static double Scale(IReadOnlyList<MacDisplay> displays)
    {
        ArgumentNullException.ThrowIfNull(displays);
        return displays.Count == 0 ? 1 : Math.Max(1, displays.Max(d => d.Scale));
    }

    /// <summary>A frame in points as the shared space's rectangle (<paramref name="scale"/> pixels a point). Pure.</summary>
    public static ScreenRect ToSpace(double x, double y, double width, double height, double scale)
    {
        int left = Edge(x * scale), top = Edge(y * scale);
        return new ScreenRect(left, top, Edge((x + width) * scale) - left, Edge((y + height) * scale) - top);
    }

    // Both edges rounded, half away from zero, so neighbouring frames meet without a gap.
    private static int Edge(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);

    /// <summary>The displays as monitors, numbered from 1 in CoreGraphics' order (the main display first), named by their ids. Pure.</summary>
    public static IReadOnlyList<ScreenMonitor> Monitors(IReadOnlyList<MacDisplay> displays)
    {
        ArgumentNullException.ThrowIfNull(displays);
        double scale = Scale(displays);
        return displays.Select((d, i) => new ScreenMonitor(i + 1, "display " + d.Id.ToString(CultureInfo.InvariantCulture), ToSpace(d.X, d.Y, d.Width, d.Height, scale), d.Main)).ToList();
    }

    /// <summary>Whether a window is one <c>screen_list</c> names and <c>window:</c> can aim at (the rules above). Pure.</summary>
    public static bool Listed(MacWindowEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.Layer == 0 && entry.Alpha > 0 && entry.OnScreen && entry.Sharing != 0 && entry.Title.Trim().Length > 0 && entry.Width > 1 && entry.Height > 1;
    }

    /// <summary>The listed windows, front to back, their frames in the shared space. Pure.</summary>
    public static IReadOnlyList<ScreenWindow> Windows(IReadOnlyList<MacWindowEntry> entries, double scale)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return entries.Where(Listed).Select(e => new ScreenWindow(e.Id, e.Title.Trim(), e.Owner, ToSpace(e.X, e.Y, e.Width, e.Height, scale))).ToList();
    }

    /// <summary>The window owner names of the terminals whose <c>TERM_PROGRAM</c> is known: the fallback when no ancestor owns a window.</summary>
    public static readonly IReadOnlyDictionary<string, string> TerminalOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Apple_Terminal"] = "Terminal",
        ["iTerm.app"] = "iTerm2",
    };

    /// <summary>
    /// The app's own window (the terminal's, <c>behind</c>'s reference): the owner is the first of <paramref name="ancestors"/> (the
    /// parent first) that owns a window on the normal layer — Terminal is zsh → login → Terminal, iTerm2 runs its shells under its
    /// <c>iTermServer</c>, whose parent is iTerm2 — else the app <paramref name="termProgram"/> names, by its owner name. Of that
    /// owner's windows, the front-most whose title holds <paramref name="title"/> (the title the app last set), else its front-most
    /// (the one the user typed into last). Null when none is found. Pure.
    /// </summary>
    public static long? Own(IReadOnlyList<MacWindowEntry> entries, IReadOnlyList<int> ancestors, string? termProgram, string? title)
    {
        var mine = TerminalWindows(entries, ancestors, termProgram);
        if (mine.Count == 0)
        {
            return null;
        }

        string wanted = (title ?? "").Trim();
        return (wanted.Length > 0 ? mine.FirstOrDefault(e => e.Title.Contains(wanted, StringComparison.Ordinal)) : null)?.Id ?? mine[0].Id;
    }

    /// <summary>
    /// The terminal app's name as macOS shows it (its windows' owner, <see cref="Own"/>'s search; readable without the permission),
    /// else the one <paramref name="termProgram"/> names; null when neither is known. For the permission's sentence. Pure.
    /// </summary>
    public static string? TerminalName(IReadOnlyList<MacWindowEntry> entries, IReadOnlyList<int> ancestors, string? termProgram) =>
        TerminalWindows(entries, ancestors, termProgram) is { Count: > 0 } mine && mine[0].Owner.Length > 0 ? mine[0].Owner
            : termProgram is { Length: > 0 } && TerminalOwners.TryGetValue(termProgram, out string? owner) ? owner : null;

    private static List<MacWindowEntry> TerminalWindows(IReadOnlyList<MacWindowEntry> entries, IReadOnlyList<int> ancestors, string? termProgram)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(ancestors);
        var normal = entries.Where(e => e.Layer == 0 && e.OnScreen).ToList();
        foreach (int pid in ancestors)
        {
            if (pid > 1 && normal.Where(e => e.OwnerPid == pid).ToList() is { Count: > 0 } owned)
            {
                return owned;
            }
        }

        return termProgram is { Length: > 0 } && TerminalOwners.TryGetValue(termProgram, out string? owner)
            ? normal.Where(e => string.Equals(e.Owner, owner, StringComparison.Ordinal)).ToList()
            : [];
    }

    /// <summary>The number of the monitor holding <paramref name="bounds"/>' centre; null when none does. Pure.</summary>
    public static int? MonitorOf(ScreenRect bounds, IReadOnlyList<ScreenMonitor> monitors)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        int x = bounds.Left + (bounds.Width / 2), y = bounds.Top + (bounds.Height / 2);
        return monitors.FirstOrDefault(m => x >= m.Bounds.Left && x < m.Bounds.Right && y >= m.Bounds.Top && y < m.Bounds.Bottom)?.Number;
    }

    /// <summary>
    /// How <paramref name="area"/> (in the shared space) is captured: exactly one display's rectangle is that display alone at its own
    /// pixels (<c>screen</c>, <c>monitor:N</c>); anything else is a canvas the area's size with every display it touches drawn at its
    /// place, scaled to the space (<c>all</c>; a gap between displays stays black). Null when it touches no display. Pure.
    /// </summary>
    public static MacCanvas? Plan(ScreenRect area, IReadOnlyList<MacDisplay> displays)
    {
        ArgumentNullException.ThrowIfNull(displays);
        if (area.Width <= 0 || area.Height <= 0)
        {
            return null;
        }

        double scale = Scale(displays);
        var placed = displays.Select(d => (Display: d, Space: ToSpace(d.X, d.Y, d.Width, d.Height, scale))).ToList();
        if (placed.FirstOrDefault(p => p.Space == area) is { Display: { } alone })
        {
            return new MacCanvas(alone.PixelWidth, alone.PixelHeight, [new MacPlacement(alone.Id, new ScreenRect(0, 0, alone.PixelWidth, alone.PixelHeight))]);
        }

        var touched = placed
            .Where(p => p.Space.Left < area.Right && p.Space.Right > area.Left && p.Space.Top < area.Bottom && p.Space.Bottom > area.Top)
            .Select(p => new MacPlacement(p.Display.Id, new ScreenRect(p.Space.Left - area.Left, p.Space.Top - area.Top, p.Space.Width, p.Space.Height)))
            .ToList();
        return touched.Count == 0 ? null : new MacCanvas(area.Width, area.Height, touched);
    }
}
