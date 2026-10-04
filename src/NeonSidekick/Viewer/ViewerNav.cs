namespace NeonSidekick.Viewer;

/// <summary>
/// The picture viewer's arrows (2026-10-03, the user's ask: "little floating (semi-transparent) buttons on the left and right
/// of the image viewer, like &lt; and &gt;", each "exactly the same as pressing left or right"): a round button at the middle of
/// each side, <c>&lt;</c> at the left for <see cref="ViewerAction.Newer"/> (←) and <c>&gt;</c> at the right for
/// <see cref="ViewerAction.Older"/> (→), the newest at the left as the keys and the picture strip run. Each is left out where
/// its key would do nothing (<see cref="ViewerState.CanNewer"/>, <see cref="ViewerState.CanOlder"/>): no <c>&lt;</c> on the newest
/// picture, no <c>&gt;</c> on the oldest. They show only while the mouse is in the window (the user's pick), fading in and out,
/// brighter under the mouse, never in the camera's window. Everything here is pure — the squares, which one a point is on,
/// the fade and the button's pixels — so <see cref="PictureWindow"/> only blends and hit-tests what this decides.
/// </summary>
public static class ViewerNav
{
    /// <summary>A button's side at 96 DPI, and its gap from the window's edge.</summary>
    public const int Size = 44;
    public const int Inset = 12;

    /// <summary>The buttons' opacity while the mouse is in the window, and the one under it. Out of 255.</summary>
    public const byte RestAlpha = 150;
    public const byte HotAlpha = 235;

    /// <summary>The fade's tick and how far it moves each tick, out of 255: in or out in about 100 ms.</summary>
    public const uint FadeMilliseconds = 16;
    public const int FadeStep = 42;

    /// <summary>A button's square in client pixels.</summary>
    public readonly record struct Square(int X, int Y, int Side)
    {
        /// <summary>Whether the client point is inside the square.</summary>
        public bool Contains(int x, int y) => x >= X && x < X + Side && y >= Y && y < Y + Side;
    }

    /// <summary>
    /// The two squares in a client of <paramref name="clientWidth"/> × <paramref name="clientHeight"/> at <paramref name="dpi"/>:
    /// the newer one (<c>&lt;</c>) at the left, the older (<c>&gt;</c>) at the right, both centred top to bottom. Null when the
    /// window is too small to hold them with a button's room between. Pure.
    /// </summary>
    public static (Square Newer, Square Older)? Layout(int clientWidth, int clientHeight, uint dpi)
    {
        int side = Scale(Size, dpi);
        int inset = Scale(Inset, dpi);
        if (clientWidth < 2 * (inset + side) + side || clientHeight < side + 2 * inset)
        {
            return null;
        }

        int y = (clientHeight - side) / 2;
        return (new Square(inset, y, side), new Square(clientWidth - inset - side, y, side));
    }

    /// <summary>The action of the button at (<paramref name="x"/>, <paramref name="y"/>): a shown button's, else <see cref="ViewerAction.None"/>. Pure.</summary>
    public static ViewerAction At(int x, int y, (Square Newer, Square Older)? layout, bool canNewer, bool canOlder)
    {
        if (layout is not { } squares)
        {
            return ViewerAction.None;
        }

        if (canNewer && squares.Newer.Contains(x, y))
        {
            return ViewerAction.Newer;
        }

        return canOlder && squares.Older.Contains(x, y) ? ViewerAction.Older : ViewerAction.None;
    }

    /// <summary>
    /// The fade's next level from <paramref name="level"/> toward <paramref name="shown"/>'s end (255 in, 0 out), one
    /// <see cref="FadeStep"/> a tick. Pure.
    /// </summary>
    public static int Fade(int level, bool shown) =>
        shown ? Math.Min(255, level + FadeStep) : Math.Max(0, level - FadeStep);

    /// <summary>A button's opacity at fade <paramref name="level"/> (0–255): <see cref="HotAlpha"/> under the mouse, else <see cref="RestAlpha"/>, scaled by the fade. Pure.</summary>
    public static byte Alpha(int level, bool hot) => (byte)((Math.Clamp(level, 0, 255) * (hot ? HotAlpha : RestAlpha) + 127) / 255);

    /// <summary>
    /// A button's pixels, <paramref name="side"/> × <paramref name="side"/>, top row first, as premultiplied BGRA (what
    /// <c>AlphaBlend</c> takes with a per-pixel alpha): a disc of <paramref name="fill"/> with a soft edge and, on it, a chevron of
    /// <paramref name="ink"/> pointing left (<paramref name="pointsLeft"/>) or right. Colours are COLORREFs (<c>0x00BBGGRR</c>). Pure.
    /// </summary>
    public static uint[] Pixels(int side, bool pointsLeft, uint fill, uint ink)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(side, 1);
        var pixels = new uint[side * side];
        double centre = side / 2.0;
        double radius = side / 2.0 - 0.5;

        // The chevron: two strokes meeting at the tip, a third of the side tall each way, its tip a little past the middle
        // so the shape looks centred.
        double half = side * 0.2;
        double depth = side * 0.11;
        double thickness = Math.Max(1.5, side * 0.085);
        double direction = pointsLeft ? -1 : 1;
        double tipX = centre + direction * depth;
        double tailX = centre - direction * depth;
        for (int row = 0; row < side; row++)
        {
            for (int col = 0; col < side; col++)
            {
                double px = col + 0.5, py = row + 0.5;
                double disc = Math.Clamp(radius + 0.5 - Math.Sqrt((px - centre) * (px - centre) + (py - centre) * (py - centre)), 0, 1);
                if (disc <= 0)
                {
                    continue;
                }

                double stroke = Math.Min(
                    SegmentDistance(px, py, tailX, centre - half, tipX, centre),
                    SegmentDistance(px, py, tipX, centre, tailX, centre + half));
                double mark = Math.Clamp(thickness / 2 + 0.5 - stroke, 0, 1);
                pixels[row * side + col] = Premultiplied(Mix(fill, ink, mark), disc);
            }
        }

        return pixels;
    }

    private static int Scale(int value, uint dpi) => (int)((value * (long)Math.Max(96u, dpi) + 48) / 96);

    // The distance from (px, py) to the segment (ax, ay)–(bx, by).
    private static double SegmentDistance(double px, double py, double ax, double ay, double bx, double by)
    {
        double dx = bx - ax, dy = by - ay;
        double t = Math.Clamp(((px - ax) * dx + (py - ay) * dy) / (dx * dx + dy * dy), 0, 1);
        double ex = ax + t * dx - px, ey = ay + t * dy - py;
        return Math.Sqrt(ex * ex + ey * ey);
    }

    // Two COLORREFs mixed, the second's share t.
    private static (double R, double G, double B) Mix(uint a, uint b, double t) =>
        (Lerp(a & 0xFF, b & 0xFF, t), Lerp((a >> 8) & 0xFF, (b >> 8) & 0xFF, t), Lerp((a >> 16) & 0xFF, (b >> 16) & 0xFF, t));

    private static double Lerp(uint a, uint b, double t) => a + (b - (double)a) * t;

    // A colour at coverage alpha as a premultiplied BGRA pixel (blue in the low byte, alpha in the high one).
    private static uint Premultiplied((double R, double G, double B) colour, double alpha)
    {
        uint a = (uint)Math.Round(alpha * 255, MidpointRounding.AwayFromZero);
        uint r = (uint)Math.Round(colour.R * alpha, MidpointRounding.AwayFromZero);
        uint g = (uint)Math.Round(colour.G * alpha, MidpointRounding.AwayFromZero);
        uint b = (uint)Math.Round(colour.B * alpha, MidpointRounding.AwayFromZero);
        return b | (g << 8) | (r << 16) | (a << 24);
    }
}
