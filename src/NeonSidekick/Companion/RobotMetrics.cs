namespace NeonSidekick.Companion;

/// <summary>
/// The robot's fixed sizes. The canvas is <see cref="Columns"/> by <see cref="Rows"/> terminal cells; each cell is one pixel
/// across and two down (the upper half block <c>▀</c> with its foreground the upper pixel and its background the lower one), so
/// the pixel canvas is <see cref="Width"/> by <see cref="Height"/>. A cell is about twice as tall as it is wide, so a pixel
/// comes out about square. Every other number here is in pixels from the canvas's top-left unless it says cells.
/// </summary>
public static class RobotMetrics
{
    /// <summary>The canvas width in cells (and pixels).</summary>
    public const int Columns = 24;

    /// <summary>The canvas height in cells.</summary>
    public const int Rows = 20;

    /// <summary>The canvas width in pixels.</summary>
    public const int Width = Columns;

    /// <summary>The canvas height in pixels: two a cell.</summary>
    public const int Height = Rows * 2;

    /// <summary>The head's left edge looking straight ahead; a turn shifts it by the yaw. The ears stick out 2 px each side.</summary>
    public const int HeadLeft = 3;

    /// <summary>The head's top row.</summary>
    public const int HeadTop = 4;

    /// <summary>The head's width.</summary>
    public const int HeadWidth = 18;

    /// <summary>The head's height (rows 4–21): square, like the emoji's.</summary>
    public const int HeadHeight = 18;

    /// <summary>The visor's height; its width comes from the pose.</summary>
    public const int VisorHeight = 8;

    /// <summary>An eye's width: a tall pill.</summary>
    public const int EyeWidth = 2;

    /// <summary>An eye's height.</summary>
    public const int EyeHeight = 4;

    /// <summary>How far the eyes travel inside the visor at full gaze, across and down.</summary>
    public const int EyeReachX = 2;

    /// <inheritdoc cref="EyeReachX"/>
    public const int EyeReachY = 1;

    /// <summary>The head's center looking straight ahead, in cells (column 12 is between pixels 11 and 12; row 6.5 is pixel row 13).</summary>
    public const double HeadCenterColumn = 12.0;

    /// <inheritdoc cref="HeadCenterColumn"/>
    public const double HeadCenterRow = 6.5;

    /// <summary>The furthest the head turns each way.</summary>
    public const int MaxYaw = 3;

    /// <summary>The furthest the head tilts each way.</summary>
    public const int MaxPitch = 1;

    /// <summary>
    /// The head-only crop, in pixels: the antenna tips, the cap, the ears and the head (rows 0–21), and nothing of the neck
    /// or shoulders. Only a waving arm would reach past it, and the animator stops it when only the head shows
    /// (<see cref="RobotAnimator.HeadOnly"/>).
    /// </summary>
    public const int HeadPixelRows = 22;
}
