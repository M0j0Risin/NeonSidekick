using System.Numerics;

namespace NeonSidekick.Companion;

/// <summary>
/// Where each part of the head sits for one <see cref="HeadPose"/>, in canvas pixels. The head follows the 🤖 emoji: a square
/// shell with a pill-shaped visor holding two tall eyes, a small mouth below it, a cap light on top, and an ear on each side
/// with an antenna rising from it. A pose reads as a turn rather than a slide by a few rules:
/// <list type="bullet">
/// <item>the shell shifts one pixel per yaw step toward the turn;</item>
/// <item>the visor slides to the near side and narrows (the eyes' gap with it), and the mouth and cap shift further than the
/// shell (parallax) — the cap only for the first two steps, so at the deepest turn it stops a pixel short of the near edge,
/// as the visor and mouth do;</item>
/// <item>the near ear swings in behind the head — half hidden at a slight turn, gone from a half turn on — while its antenna still
/// shows above the head;</item>
/// <item>tilting moves the visor and mouth two pixels and the antennas one, while the cap stays seated on top (looking down
/// it sinks into the top a row); the painter also shades the chin's
/// underside looking up and lights the top of the head looking down.</item>
/// </list>
/// The eye rectangles are where the eyes sit looking straight ahead; <see cref="EyeAt"/> moves them with the gaze.
/// </summary>
public sealed record HeadLayout(
    HeadPose Pose,
    PixelRect Shell,
    PixelRect Visor,
    PixelRect LeftEye,
    PixelRect RightEye,
    PixelRect Mouth,
    PixelRect Cap,
    PixelRect LeftEar,
    PixelRect RightEar,
    PixelRect LeftTip,
    PixelRect RightTip,
    PixelRect LeftStalk,
    PixelRect RightStalk)
{
    private const int MouthWidth = 4;
    private const int MouthHeight = 2;
    private const int CapWidth = 8;
    private const int CapHeight = 2;

    /// <summary>
    /// How many steps of a turn the cap shifts further than the shell (a pixel a step); past that it moves with the shell, so
    /// it never runs flush with the side of the head.
    /// </summary>
    internal const int MaxCapParallax = 2;
    private const int EarWidth = 2;
    private const int EarTop = 9;
    private const int EarHeight = 8;
    private const int TipSize = 2;

    // The visor by |yaw|: its margin on the near side and its width (the far margin is what's left).
    private static readonly (int Near, int Width)[] Visors = [(2, 14), (1, 13), (1, 12), (1, 11)];

    /// <summary>How far in from each end of the visor an eye sits.</summary>
    private const int EyeInset = 3;

    /// <summary>The layout of <paramref name="pose"/> (clamped to the head's range).</summary>
    public static HeadLayout For(HeadPose pose)
    {
        pose = pose.Clamped();
        int yaw = pose.Yaw;
        int pitch = pose.Pitch;
        int turn = Math.Abs(yaw);
        int side = Math.Sign(yaw);

        var shell = new PixelRect(RobotMetrics.HeadLeft + yaw, RobotMetrics.HeadTop, RobotMetrics.HeadWidth, RobotMetrics.HeadHeight);

        var (near, width) = Visors[turn];
        int visorX = side > 0 ? shell.Right - near - width : shell.X + (side < 0 ? near : Visors[0].Near);
        var visor = new PixelRect(visorX, 7 + (2 * pitch), width, RobotMetrics.VisorHeight);

        int eyeY = visor.Y + ((RobotMetrics.VisorHeight - RobotMetrics.EyeHeight) / 2);
        var leftEye = new PixelRect(visor.X + EyeInset, eyeY, RobotMetrics.EyeWidth, RobotMetrics.EyeHeight);
        var rightEye = new PixelRect(visor.Right - EyeInset - RobotMetrics.EyeWidth, eyeY, RobotMetrics.EyeWidth, RobotMetrics.EyeHeight);

        var mouth = new PixelRect(shell.X + 7 + (2 * yaw), 16 + (2 * pitch), MouthWidth, MouthHeight);
        // The cap always sits on the head: level and looking up it rests on the top; looking down it sinks into the lit top a row.
        int capParallax = side * Math.Min(turn, MaxCapParallax);
        var cap = new PixelRect(shell.X + 5 + capParallax, pitch > 0 ? shell.Y - 1 : shell.Y - CapHeight, CapWidth, CapHeight);

        // The near ear tucks in behind the shell (painted under it) a pixel per turn step, until it's wholly behind it: going
        // further would only slide its antenna into the cap.
        int tuck = Math.Min(turn, EarWidth);
        int leftEarX = shell.X - EarWidth + (side < 0 ? tuck : 0);
        int rightEarX = shell.Right - (side > 0 ? tuck : 0);
        var leftEar = new PixelRect(leftEarX, EarTop, EarWidth, EarHeight);
        var rightEar = new PixelRect(rightEarX, EarTop, EarWidth, EarHeight);

        int tipY = 1 + pitch;
        var leftTip = new PixelRect(leftEar.X, tipY, TipSize, TipSize);
        var rightTip = new PixelRect(rightEar.X, tipY, TipSize, TipSize);
        var leftStalk = new PixelRect(leftEar.X, leftTip.Bottom, 1, EarTop - leftTip.Bottom);
        var rightStalk = new PixelRect(rightEar.Right - 1, rightTip.Bottom, 1, EarTop - rightTip.Bottom);

        return new HeadLayout(pose, shell, visor, leftEye, rightEye, mouth, cap, leftEar, rightEar, leftTip, rightTip, leftStalk, rightStalk);
    }

    /// <summary>
    /// Where an eye that sits at <paramref name="eye"/> looking straight ahead is for a gaze of <paramref name="gaze"/> (−1..1
    /// per axis, clamped): up to <see cref="RobotMetrics.EyeReachX"/> pixels across and <see cref="RobotMetrics.EyeReachY"/> down.
    /// </summary>
    public static PixelRect EyeAt(PixelRect eye, Vector2 gaze) => eye with
    {
        X = eye.X + Step(gaze.X, RobotMetrics.EyeReachX),
        Y = eye.Y + Step(gaze.Y, RobotMetrics.EyeReachY),
    };

    private static int Step(float offset, int reach) =>
        (int)Math.Round(Math.Clamp(offset, -1f, 1f) * reach, MidpointRounding.AwayFromZero);
}
