namespace NeonSidekick.Viewer;

/// <summary>A rectangle in top-left coordinates (y down), in points: <see cref="MacPlacement"/>'s currency.</summary>
public readonly record struct PlaceRect(double X, double Y, double Width, double Height)
{
    /// <summary>The area two rectangles share; 0 when they do not meet.</summary>
    public double Overlap(PlaceRect other)
    {
        double width = Math.Min(X + Width, other.X + other.Width) - Math.Max(X, other.X);
        double height = Math.Min(Y + Height, other.Y + other.Height) - Math.Max(Y, other.Y);
        return width > 0 && height > 0 ? width * height : 0;
    }
}

/// <summary>
/// Where a Mac window of the app's own opens and what is saved as it closes (2026-10-07, the windows over AppKit). The profile
/// keeps a window's top-left corner (<c>ViewerLeft</c>/<c>ViewerTop</c> and the other windows' pairs) as Windows does, but a
/// Mac's screens are measured from the bottom-left of the menu bar's screen, in points, y up, several screens of differing
/// scale side by side. So the corner is saved in points with y measured down from the top of that first screen
/// (<c>NSScreen.screens[0]</c>, never <c>mainScreen</c>, which follows the key window), one space for every screen whatever
/// its scale; and a corner is restored only onto a screen it still overlaps, moved in so the whole window is on that screen's
/// visible part (under the menu bar, beside the Dock) — a window is never restored off screen, which Windows'
/// SetWindowPlacement does for itself. Pure.
/// </summary>
public static class MacPlacement
{
    /// <summary>
    /// A rectangle between AppKit's coordinates (y up from the first screen's bottom) and the saved ones (y down from its top);
    /// its own inverse. Pure.
    /// </summary>
    public static PlaceRect Flip(PlaceRect rect, double firstScreenHeight) =>
        rect with { Y = firstScreenHeight - rect.Y - rect.Height };

    /// <summary>The corner saved for a window whose frame is <paramref name="topLeftFrame"/> (top-left coordinates), rounded to whole points. Pure.</summary>
    public static (int X, int Y) Corner(PlaceRect topLeftFrame) =>
        ((int)Math.Round(topLeftFrame.X, MidpointRounding.AwayFromZero), (int)Math.Round(topLeftFrame.Y, MidpointRounding.AwayFromZero));

    /// <summary>
    /// Where a window of <paramref name="width"/> × <paramref name="height"/> opens, its corner saved at <paramref name="saved"/>,
    /// given the screens' visible parts (top-left coordinates): on the screen it overlaps most, moved in to fit (and made smaller
    /// when the screen is), or null — the system's own default place — when nothing was saved or it overlaps no screen any more
    /// (a screen unplugged). Pure.
    /// </summary>
    public static PlaceRect? Restore((int X, int Y)? saved, double width, double height, IReadOnlyList<PlaceRect> screens)
    {
        ArgumentNullException.ThrowIfNull(screens);
        if (saved is not { } corner)
        {
            return null;
        }

        var wanted = new PlaceRect(corner.X, corner.Y, width, height);
        PlaceRect? best = null;
        double most = 0;
        foreach (var screen in screens)
        {
            double overlap = wanted.Overlap(screen);
            if (overlap > most)
            {
                most = overlap;
                best = screen;
            }
        }

        return best is { } on ? Fit(wanted, on) : null;
    }

    /// <summary><paramref name="rect"/> moved (and shrunk where it must be) to lie wholly inside <paramref name="screen"/>. Pure.</summary>
    public static PlaceRect Fit(PlaceRect rect, PlaceRect screen)
    {
        double width = Math.Min(rect.Width, screen.Width);
        double height = Math.Min(rect.Height, screen.Height);
        double x = Math.Clamp(rect.X, screen.X, screen.X + screen.Width - width);
        double y = Math.Clamp(rect.Y, screen.Y, screen.Y + screen.Height - height);
        return new PlaceRect(x, y, width, height);
    }
}
