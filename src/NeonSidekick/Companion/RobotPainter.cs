using System.Numerics;
using Spectre.Console;

namespace NeonSidekick.Companion;

/// <summary>
/// Paints the whole robot into a canvas-sized <see cref="PixelBuffer"/>: the legs, each where its step put it, then the upper
/// body and the head. The head
/// goes ears and antennas first so the shell covers whatever of them is behind it, then the shell and its shading, the visor, the eyes and their lids, the mouth and the cap. Light comes from the upper right, as in
/// the emoji. Stateless; the animator decides what to paint.
/// </summary>
public static class RobotPainter
{
    /// <summary>The furthest turn at which the visor is wide enough for both happy eye arcs (two steps out they touch).</summary>
    internal const int HappyEyesYaw = 1;

    /// <summary>
    /// Paints the robot with its head at <paramref name="pose"/>, its eyes looking toward <paramref name="gaze"/> (−1..1 per
    /// axis; both eyes move together, so it never goes cross-eyed), <paramref name="blink"/> closed (0 open, 1 shut), its
    /// body moved by <paramref name="body"/> (<see cref="BodyMotion.Rest"/> when null) and its face — expression and lights —
    /// as <paramref name="face"/> says (<see cref="RobotFace.Default"/> when null).
    /// </summary>
    public static void Paint(PixelBuffer buffer, HeadPose pose, Vector2 gaze, float blink, RobotPalette palette, BodyMotion? body = null, RobotFace? face = null)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(palette);
        var motion = body ?? BodyMotion.Rest;
        var look = face ?? RobotFace.Default;
        int power = Math.Clamp(look.Power, RobotFace.Off, RobotFace.On);

        int lean = Math.Clamp(motion.Lean, -1, 1);
        int wave = Math.Sign(motion.Wave);

        // Below the line stage the core is switched off (−1).
        int core = power < RobotFace.Line ? -1 : Math.Clamp(motion.Core, 0, BodyMotion.MaxCore);

        buffer.Clear();
        PaintLegs(buffer, palette, Math.Clamp(motion.LeftLeg, -1, 1), Math.Clamp(motion.RightLeg, -1, 1), Math.Sign(motion.Lift));
        var pads = PaintUpperBody(buffer, palette, lean, core, wave);
        PaintHead(buffer, HeadLayout.For(pose), gaze, blink, Math.Clamp(motion.Sway, -1, 1), palette, look.Expression, power);

        // A waving arm goes up beside the head, so it is painted last: the hand in front of the ear.
        if (wave != 0)
        {
            PaintWavingArm(buffer, palette, wave < 0 ? pads.Left : pads.Right, wave, Math.Clamp(motion.WaveFrame, 0, BodyMotion.WaveIn));
        }
    }

    /// <summary>The two legs, each offset by <paramref name="leftLeg"/> / <paramref name="rightLeg"/>, the <paramref name="lift"/> side's foot up.</summary>
    private static void PaintLegs(PixelBuffer buffer, RobotPalette palette, int leftLeg, int rightLeg, int lift)
    {
        PaintLeg(buffer, palette, -1, leftLeg, lifted: lift < 0);
        PaintLeg(buffer, palette, 1, rightLeg, lifted: lift > 0);
    }

    /// <summary>
    /// One leg, on <paramref name="side"/> (−1 left, +1 right, as the viewer sees it) and moved <paramref name="dx"/>: thigh,
    /// knee, shin, then a boot wider at the sole, rows 34–39 — or, <paramref name="lifted"/> mid-step, the boot and sole a
    /// row up (37–38) with the shin hidden behind them and nothing under the sole. The right leg is the left one mirrored,
    /// but both are shaded on their left edge: the light comes from the upper right.
    /// </summary>
    private static void PaintLeg(PixelBuffer buffer, RobotPalette palette, int side, int dx, bool lifted)
    {
        // A left-leg column (x, width wide) on this leg's side, moved by its step.
        int X(int x, int width) => (side < 0 ? x : RobotMetrics.Width - x - width) + dx;

        int up = lifted ? 1 : 0;
        int leg = X(8, 3);
        int boot = X(7, 4);
        buffer.Fill(leg, 34, 3, 2, palette.Shell);           // thigh
        buffer.Fill(leg, 36, 3, 1, palette.Visor);           // knee
        if (!lifted)
        {
            buffer.Fill(leg, 37, 3, 1, palette.Shell);       // shin
        }

        buffer.Fill(boot, 38 - up, 4, 1, palette.Shell);
        buffer.Fill(X(6, 5), 39 - up, 5, 1, palette.ShellShade); // sole
        Shade(buffer, new PixelRect(leg, 34, 1, 4 - up), palette.Shell, palette.ShellShade);
        Shade(buffer, new PixelRect(boot, 38 - up, 1, 1), palette.Shell, palette.ShellShade);
    }

    /// <summary>
    /// Everything between the head and the knees, after a retro-futurist robot: a dark collar, arms with sockets, elbow joints
    /// and claw hands, a chest plate around a glowing concentric core, domed shoulder pads, a ribbed waist and rounded hip armor.
    /// The joints are the visor's dark color and the core glows in the eyes' colors, so the whole robot themes from one palette.
    /// <paramref name="lean"/> turns the torso toward one of the two deepest head turns —
    /// the chest, core, shoulders, arms, waist and hips shift a pixel that way together (so neither arm comes away from the
    /// chest, and the feet then step after the hips) and the
    /// near shoulder and arm drop a pixel (the far ones stay level: raised, they read as a shrug). A <paramref name="core"/>
    /// of −1 is the core switched off, all dark. The arm on the
    /// <paramref name="wave"/> side (if any) is left off for the waving arm to replace. Returns where the shoulder pads went.
    /// </summary>
    private static (PixelRect Left, PixelRect Right) PaintUpperBody(PixelBuffer buffer, RobotPalette palette, int lean, int core, int wave)
    {
        var shell = palette.Shell;
        var shade = palette.ShellShade;
        var joint = palette.Visor;

        buffer.Fill(10, 22, 4, 1, joint); // collar

        // Turned left (lean −1) the left side is near and drops; turned right the right side does.
        int leftDrop = Math.Max(0, -lean);
        int rightDrop = Math.Max(0, lean);
        if (wave >= 0)
        {
            PaintArm(buffer, palette, 3 + lean, leftDrop, fingerX: 3 + lean);
        }

        if (wave <= 0)
        {
            PaintArm(buffer, palette, 19 + lean, rightDrop, fingerX: 20 + lean);
        }

        // The chest plate and its core: rings around a center, brighter or dimmer with the pulse.
        var chest = new PixelRect(6 + lean, 23, 12, 7);
        chest.Fill(buffer, shell);
        buffer.Fill(chest.X, chest.Bottom - 1, chest.Width, 1, shade);
        var (outer, inner, center) = core switch
        {
            < 0 => (joint, joint, joint),
            0 => (palette.EyeShade, palette.EyeShade, palette.Eye),
            1 => (palette.EyeShade, palette.Eye, palette.Eye),
            3 => (palette.Eye, palette.ShellLight, palette.ShellLight),
            _ => (palette.EyeShade, palette.Eye, palette.ShellLight),
        };
        buffer.Fill(chest.X + 4, chest.Y + 1, 4, 1, outer);
        buffer.Fill(chest.X + 3, chest.Y + 2, 6, 3, outer);
        buffer.Fill(chest.X + 4, chest.Y + 5, 4, 1, outer);
        buffer.Fill(chest.X + 4, chest.Y + 2, 4, 3, inner);
        buffer.Fill(chest.X + 5, chest.Y + 3, 2, 1, center);
        buffer.Set(chest.X, chest.Y, null);
        buffer.Set(chest.Right - 1, chest.Y, null);
        Shade(buffer, chest with { Width = 1 }, shell, shade);
        Shade(buffer, new PixelRect(chest.Right - 1, chest.Y + 1, 1, 5), shell, palette.ShellLight);

        // Domed shoulder pads over the sockets, on top of the chest's edge when it leans under one.
        var leftPad = PaintPad(buffer, palette, 2 + lean, 23 + leftDrop);
        var rightPad = PaintPad(buffer, palette, 18 + lean, 23 + rightDrop);

        // The ribbed waist.
        buffer.Fill(8 + lean, 30, 8, 1, joint);
        buffer.Fill(9 + lean, 31, 6, 1, shade);

        // Hip armor: two rounded plates meeting at a dark seam.
        var hips = new PixelRect(6 + lean, 32, 12, 2);
        hips.Fill(buffer, shell);
        buffer.Fill(hips.X, hips.Bottom - 1, hips.Width, 1, shade);
        buffer.Fill(hips.X + 5, hips.Y, 2, 2, joint);
        buffer.Set(hips.X, hips.Bottom - 1, null);
        buffer.Set(hips.Right - 1, hips.Bottom - 1, null);
        Shade(buffer, hips with { Width = 1 }, shell, shade);
        return (leftPad, rightPad);
    }

    /// <summary>
    /// An arm raised to wave, out from under its shoulder pad (<paramref name="pad"/>) on <paramref name="side"/> (−1 left,
    /// +1 right): the upper arm out to the canvas's edge, a dark elbow, the forearm up beside the head and an open hand —
    /// a palm with two fingers — just below the ear. Wherever the forearm or hand passes in front of the head it gets a dark
    /// outline, so it reads as in front rather than merging into the shell. <see cref="BodyMotion.WaveLift"/> is the arm
    /// halfway up; <see cref="BodyMotion.WaveIn"/> tips the hand and the forearm's top a pixel in toward the head, so
    /// alternating it with <see cref="BodyMotion.WaveOut"/> is the wave. Drawn for the right side and mirrored for the left.
    /// </summary>
    private static void PaintWavingArm(PixelBuffer buffer, RobotPalette palette, PixelRect pad, int side, int frame)
    {
        int edge = RobotMetrics.Width - 1;
        int Column(int x) => side > 0 ? x : edge - x;

        void Fill(int x, int y, int width, int height, Color color)
        {
            int left = side > 0 ? x : edge - (x + width - 1);
            buffer.Fill(left, y, width, height, color);
        }

        // A dark outline on the head side of a part: only over pixels already painted (the head, an ear), never over background.
        void Outline(int x, int y, int height)
        {
            for (int row = y; row < y + height; row++)
            {
                int column = Column(x - 1);
                if (buffer[column, row] is not null)
                {
                    buffer.Set(column, row, palette.Visor);
                }
            }
        }

        // In right-side columns: the pad's outer edge (wherever the lean put it), the forearm's two columns, the elbow's row.
        int padOuter = side > 0 ? pad.Right : edge - pad.X + 1;
        int forearm = edge - 1;
        int elbow = pad.Y;

        Fill(padOuter, pad.Y + 1, edge - padOuter + 1, 2, palette.Shell); // upper arm, out from under the pad
        Fill(padOuter, pad.Y + 2, edge - padOuter + 1, 1, palette.ShellShade);
        Fill(forearm, elbow, 2, 1, palette.Visor);                          // elbow

        if (frame == BodyMotion.WaveLift)
        {
            // Halfway: a short forearm with the hand just above it.
            Outline(forearm, elbow - 4, 4);
            Fill(forearm, elbow - 2, 2, 2, palette.Shell);
            Fill(forearm, elbow - 4, 2, 2, palette.ShellShade);
            return;
        }

        int tip = frame == BodyMotion.WaveIn ? -1 : 0;
        int palm = forearm - 1 + tip;                                        // three wide, overhanging the forearm inward

        Outline(forearm, elbow - 3, 1);
        Outline(forearm + tip, elbow - 4, 1);
        Outline(palm, elbow - 7, 3);
        Fill(forearm, elbow - 3, 2, 3, palette.Shell);                       // forearm, its top tipping with the wave
        Fill(forearm + tip, elbow - 4, 2, 1, palette.Shell);
        Fill(forearm, elbow - 3, 1, 3, palette.ShellShade);
        Fill(palm, elbow - 6, 3, 2, palette.Shell);                          // palm
        Fill(palm, elbow - 5, 3, 1, palette.ShellShade);
        Fill(palm, elbow - 7, 1, 1, palette.ShellShade);                     // two fingers, apart
        Fill(palm + 2, elbow - 7, 1, 1, palette.ShellShade);
    }

    /// <summary>An arm two pixels wide at <paramref name="x"/>: socket, upper arm, elbow, forearm and a claw hand whose finger hangs at <paramref name="fingerX"/>.</summary>
    private static void PaintArm(PixelBuffer buffer, RobotPalette palette, int x, int dy, int fingerX)
    {
        buffer.Fill(x, 26 + dy, 2, 1, palette.Visor);
        buffer.Fill(x, 27 + dy, 2, 3, palette.Shell);
        buffer.Fill(x, 30 + dy, 2, 1, palette.Visor);
        buffer.Fill(x, 31 + dy, 2, 2, palette.Shell);
        buffer.Fill(x, 33 + dy, 2, 1, palette.ShellShade);
        buffer.Set(fingerX, 34 + dy, palette.ShellShade);
        Shade(buffer, new PixelRect(x, 27 + dy, 1, 6), palette.Shell, palette.ShellShade);
    }

    private static PixelRect PaintPad(PixelBuffer buffer, RobotPalette palette, int x, int y)
    {
        var pad = new PixelRect(x, y, 4, 3);
        pad.Fill(buffer, palette.Shell);
        buffer.Fill(x, pad.Bottom - 1, pad.Width, 1, palette.ShellShade);
        buffer.Set(x, pad.Y, null);
        buffer.Set(pad.Right - 1, pad.Y, null);
        Shade(buffer, pad with { Width = 1 }, palette.Shell, palette.ShellShade);
        return pad;
    }

    private static void PaintHead(PixelBuffer buffer, HeadLayout head, Vector2 gaze, float blink, int sway, RobotPalette palette, Expression expression, int power)
    {
        // The tips sway, bending the top of each stalk with them so they stay joined; a tip already at the canvas's edge
        // (the near antenna at the deepest turn) doesn't sway past it.
        foreach (var (stalk, tip) in (ReadOnlySpan<(PixelRect, PixelRect)>)[(head.LeftStalk, head.LeftTip), (head.RightStalk, head.RightTip)])
        {
            int dx = SwayWithin(tip, sway);
            stalk.Fill(buffer, palette.Ear);
            if (dx != 0)
            {
                buffer.Set(stalk.X, stalk.Y, null);
                buffer.Set(stalk.X + dx, stalk.Y, palette.Ear);
            }

            tip.Offset(dx, 0).Fill(buffer, palette.Accent);
        }
        head.LeftEar.Fill(buffer, palette.Ear);
        head.RightEar.Fill(buffer, palette.Ear);

        // The shell's rounded corners show whatever is behind them (an antenna stalk), so keep it.
        var shell = head.Shell;
        var behind = Corners(shell, 2).Select(p => (p.X, p.Y, Color: buffer[p.X, p.Y])).ToList();
        shell.Fill(buffer, palette.Shell);
        Shade(buffer, shell with { Width = 1 }, palette.Shell, palette.ShellShade);
        int underside = head.Pose.Pitch < 0 ? 3 : 1;
        Shade(buffer, shell with { Y = shell.Bottom - underside, Height = underside }, palette.Shell, palette.ShellShade);
        if (head.Pose.Pitch > 0)
        {
            Shade(buffer, shell with { Height = 2 }, palette.Shell, palette.ShellLight);
        }

        foreach (var (x, y, color) in behind)
        {
            buffer.Set(x, y, color);
        }

        var visor = head.Visor;
        visor.Fill(buffer, palette.Visor);
        // Just the corner pixels: a rounder visor would crowd the eyes when they look into a corner.
        RoundCorners(buffer, visor, 1, palette.Shell);

        bool happyEyes = expression == Expression.Happy && Math.Abs(head.Pose.Yaw) <= HappyEyesYaw;
        int lidRows = (int)Math.Ceiling(Math.Clamp(blink, 0f, 1f) * (RobotMetrics.EyeHeight - 1));
        foreach (var neutral in (ReadOnlySpan<PixelRect>)[head.LeftEye, head.RightEye])
        {
            var eye = HeadLayout.EyeAt(neutral, gaze);
            switch (power)
            {
                case RobotFace.Line:
                    buffer.Fill(eye.X, eye.Y + 1, eye.Width, 1, palette.ShellLight);
                    break;
                case RobotFace.Dot:
                    buffer.Set(eye.X + 1, eye.Y + 1, palette.ShellLight);
                    break;
                case RobotFace.On when happyEyes:
                    // Closed happily: an arc over the eye's middle rows, wherever the gaze was.
                    buffer.Fill(neutral.X, neutral.Y + 1, neutral.Width, 1, palette.Eye);
                    buffer.Set(neutral.X - 1, neutral.Y + 2, palette.Eye);
                    buffer.Set(neutral.Right, neutral.Y + 2, palette.Eye);
                    break;
                case RobotFace.On:
                    // A lid closes from the top; shut, a sliver of each eye stays as a line.
                    eye.Fill(buffer, palette.Eye);
                    buffer.Fill(eye.X, eye.Y, 1, eye.Height, palette.EyeShade);
                    buffer.Fill(eye.X, eye.Y, eye.Width, lidRows, palette.Visor);
                    break;
            }
        }

        var mouth = head.Mouth;
        if (expression == Expression.Neutral)
        {
            mouth.Fill(buffer, palette.Mouth);
        }
        else
        {
            // A smile: the corners up, the middle down.
            buffer.Set(mouth.X, mouth.Y, palette.Mouth);
            buffer.Set(mouth.Right - 1, mouth.Y, palette.Mouth);
            buffer.Fill(mouth.X + 1, mouth.Y + 1, mouth.Width - 2, 1, palette.Mouth);
        }

        // The cap's rounded top corners show whatever is behind them (an antenna beside it at a deep turn). Switched off,
        // it's all shade.
        var cap = head.Cap;
        var capCorners = new[] { (cap.X, cap.Y), (cap.Right - 1, cap.Y) }.Select(p => (p.Item1, p.Item2, Color: buffer[p.Item1, p.Item2])).ToList();
        cap.Fill(buffer, power < RobotFace.Line ? palette.CapShade : palette.Cap);
        buffer.Fill(cap.X, cap.Bottom - 1, cap.Width, 1, palette.CapShade);
        foreach (var (x, y, color) in capCorners)
        {
            buffer.Set(x, y, color);
        }
    }

    /// <summary>A tip's sway, kept on the canvas.</summary>
    internal static int SwayWithin(PixelRect tip, int sway) => Math.Clamp(sway, -tip.X, RobotMetrics.Width - tip.Right);

    /// <summary>Recolors the pixels of <paramref name="area"/> that are <paramref name="from"/>, so joints and gaps keep their own.</summary>
    private static void Shade(PixelBuffer buffer, PixelRect area, Color from, Color to)
    {
        for (int y = area.Y; y < area.Bottom; y++)
        {
            for (int x = area.X; x < area.Right; x++)
            {
                if (buffer[x, y] == from)
                {
                    buffer.Set(x, y, to);
                }
            }
        }
    }

    /// <summary>
    /// Rounds <paramref name="area"/>'s corners by <paramref name="radius"/> pixels (a stair of radius 2 drops three pixels a
    /// corner, radius 1 just the corner), setting them to <paramref name="fill"/> — empty unless given.
    /// </summary>
    private static void RoundCorners(PixelBuffer buffer, PixelRect area, int radius, Color? fill = null)
    {
        foreach (var (x, y) in Corners(area, radius))
        {
            buffer.Set(x, y, fill);
        }
    }

    private static IEnumerable<(int X, int Y)> Corners(PixelRect area, int radius)
    {
        for (int i = 0; i < radius; i++)
        {
            for (int j = 0; j < radius - i; j++)
            {
                yield return (area.X + i, area.Y + j);
                yield return (area.Right - 1 - i, area.Y + j);
                yield return (area.X + i, area.Bottom - 1 - j);
                yield return (area.Right - 1 - i, area.Bottom - 1 - j);
            }
        }
    }
}
