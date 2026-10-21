using NeonSidekick.Companion;
using System.Numerics;

namespace NeonSidekick.Tests.Companion;

public sealed class HeadLayoutTests
{
    private static readonly PixelRect Canvas = new(0, 0, RobotMetrics.Width, RobotMetrics.Height);

    private static readonly Vector2[] Gazes =
        [.. from x in new[] { -5f, -1f, 0f, 1f, 5f } from y in new[] { -5f, -1f, 0f, 1f, 5f } select new Vector2(x, y)];

    public static TheoryData<int, int> Poses()
    {
        var data = new TheoryData<int, int>();
        foreach (var pose in HeadPose.All)
        {
            data.Add(pose.Yaw, pose.Pitch);
        }

        return data;
    }

    [Fact]
    public void ThereAreTwentyOnePoses()
    {
        Assert.Equal(21, HeadPose.All.Count);
        Assert.Equal(21, HeadPose.All.Distinct().Count());
    }

    [Theory]
    [MemberData(nameof(Poses))]
    public void EverythingFitsWhereItBelongs(int yaw, int pitch)
    {
        var head = HeadLayout.For(new HeadPose(yaw, pitch));

        foreach (var part in new[] { head.Shell, head.Cap, head.LeftEar, head.RightEar, head.LeftTip, head.RightTip, head.LeftStalk, head.RightStalk })
        {
            Assert.True(Canvas.Contains(part), $"{part} is off the canvas");
        }

        // The visor sits inside the shell with shell all round it.
        Assert.True(head.Visor.X > head.Shell.X && head.Visor.Right < head.Shell.Right);
        Assert.True(head.Visor.Y > head.Shell.Y && head.Visor.Bottom < head.Shell.Bottom);

        // The mouth is on the shell below the visor, with a row of shell between them.
        Assert.True(head.Shell.Contains(head.Mouth));
        Assert.True(head.Mouth.Y > head.Visor.Bottom);

        // The cap sits on top of the head — touching it, or overlapping it by a row — never floating above it.
        Assert.InRange(head.Cap.Bottom, head.Shell.Y, head.Shell.Y + 1);
        Assert.True(head.Cap.X > head.Shell.X && head.Cap.Right < head.Shell.Right);

        // Each antenna runs from its tip, above the head, down to its ear.
        foreach (var (tip, stalk, ear) in new[] { (head.LeftTip, head.LeftStalk, head.LeftEar), (head.RightTip, head.RightStalk, head.RightEar) })
        {
            Assert.True(tip.Bottom <= head.Shell.Y);
            Assert.Equal(tip.Bottom, stalk.Y);
            Assert.Equal(ear.Y, stalk.Bottom);
            Assert.True(stalk.X >= ear.X && stalk.Right <= ear.Right);
        }
    }

    [Theory]
    [MemberData(nameof(Poses))]
    public void TheEyesStayInsideTheVisorWhereverTheyLook(int yaw, int pitch)
    {
        var head = HeadLayout.For(new HeadPose(yaw, pitch));
        foreach (var gaze in Gazes)
        {
            var left = HeadLayout.EyeAt(head.LeftEye, gaze);
            var right = HeadLayout.EyeAt(head.RightEye, gaze);
            foreach (var eye in new[] { left, right })
            {
                // At least a pixel of visor all round: never touching the shell, nor the visor's cut corners.
                Assert.True(eye.X > head.Visor.X && eye.Right < head.Visor.Right, $"{eye} touches a side of {head.Visor}");
                Assert.True(eye.Y > head.Visor.Y && eye.Bottom < head.Visor.Bottom, $"{eye} touches the top or bottom of {head.Visor}");
            }

            Assert.True(left.Right < right.X, "the eyes must not touch");
        }
    }

    [Fact]
    public void TheEyesTravelTheirReach()
    {
        var eye = new PixelRect(8, 9, 2, 4);

        Assert.Equal(new PixelRect(6, 8, 2, 4), HeadLayout.EyeAt(eye, new Vector2(-1, -1)));
        Assert.Equal(eye, HeadLayout.EyeAt(eye, Vector2.Zero));
        Assert.Equal(new PixelRect(10, 10, 2, 4), HeadLayout.EyeAt(eye, new Vector2(1, 1)));
        Assert.Equal(new PixelRect(10, 10, 2, 4), HeadLayout.EyeAt(eye, new Vector2(9, 9)));
    }

    [Fact]
    public void TurningShiftsTheShellNarrowsTheVisorAndTucksTheNearEarAway()
    {
        var center = HeadLayout.For(HeadPose.Center);
        var slight = HeadLayout.For(new HeadPose(-1, 0));
        var half = HeadLayout.For(new HeadPose(-2, 0));
        var full = HeadLayout.For(new HeadPose(-3, 0));

        Assert.Equal([3, 2, 1, 0], new[] { center.Shell.X, slight.Shell.X, half.Shell.X, full.Shell.X });
        Assert.Equal([14, 13, 12, 11], new[] { center.Visor.Width, slight.Visor.Width, half.Visor.Width, full.Visor.Width });

        // The near (left) ear: out beside the head, half behind it, then wholly behind it — where it stays, flush with the
        // shell's edge, so its antenna keeps clear of the cap. The far one stays out.
        Assert.Equal(center.Shell.X, center.LeftEar.Right);
        Assert.Equal(slight.Shell.X + 1, slight.LeftEar.Right);
        Assert.Equal(half.Shell.X, half.LeftEar.X);
        Assert.Equal(full.Shell.X, full.LeftEar.X);
        Assert.Equal(full.Shell.Right, full.RightEar.X);
        Assert.True(full.LeftTip.Right <= full.Cap.X, "the hidden ear's antenna keeps clear of the cap");
    }

    [Fact]
    public void TiltingMovesTheFaceAndTheCap()
    {
        var up = HeadLayout.For(new HeadPose(0, -1));
        var level = HeadLayout.For(HeadPose.Center);
        var down = HeadLayout.For(new HeadPose(0, 1));

        Assert.Equal([5, 7, 9], new[] { up.Visor.Y, level.Visor.Y, down.Visor.Y });
        Assert.Equal([14, 16, 18], new[] { up.Mouth.Y, level.Mouth.Y, down.Mouth.Y });
        // The cap stays seated on the head: on top level and looking up, sunk a row into it looking down.
        Assert.Equal([2, 2, 3], new[] { up.Cap.Y, level.Cap.Y, down.Cap.Y });
    }

    [Fact]
    public void LeftAndRightAreMirrorImages()
    {
        foreach (var pose in HeadPose.All)
        {
            var head = HeadLayout.For(pose);
            var mirror = HeadLayout.For(pose with { Yaw = -pose.Yaw });

            Assert.Equal(Mirror(head.Shell), mirror.Shell);
            Assert.Equal(Mirror(head.Visor), mirror.Visor);
            Assert.Equal(Mirror(head.LeftEye), mirror.RightEye);
            Assert.Equal(Mirror(head.Mouth), mirror.Mouth);
            Assert.Equal(Mirror(head.Cap), mirror.Cap);
            Assert.Equal(Mirror(head.LeftEar), mirror.RightEar);
            Assert.Equal(Mirror(head.LeftTip), mirror.RightTip);
            Assert.Equal(Mirror(head.LeftStalk), mirror.RightStalk);
        }

        static PixelRect Mirror(PixelRect r) => r with { X = RobotMetrics.Width - r.Right };
    }

    [Fact]
    public void EveryPosePaintsDifferently()
    {
        var painted = HeadPose.All.Select(pose =>
        {
            var buffer = PixelBuffer.ForRobot();
            RobotPainter.Paint(buffer, pose, Vector2.Zero, 0, RobotPalette.Default);
            return buffer;
        }).ToList();

        for (int i = 0; i < painted.Count; i++)
        {
            for (int j = i + 1; j < painted.Count; j++)
            {
                Assert.False(painted[i].SameAs(painted[j]), $"{HeadPose.All[i]} and {HeadPose.All[j]} look the same");
            }
        }
    }

    [Fact]
    public void ShutEyesAreAThinLine()
    {
        var palette = RobotPalette.Default;
        int Lit(float blink)
        {
            var buffer = PixelBuffer.ForRobot();
            RobotPainter.Paint(buffer, HeadPose.Center, Vector2.Zero, blink, palette);
            // The head only: the chest core glows in the eye colors too.
            int count = 0;
            for (int y = 0; y < RobotMetrics.HeadTop + RobotMetrics.HeadHeight; y++)
            {
                for (int x = 0; x < buffer.Width; x++)
                {
                    if (buffer[x, y] == palette.Eye || buffer[x, y] == palette.EyeShade)
                    {
                        count++;
                    }
                }
            }

            return count;
        }

        Assert.Equal(2 * RobotMetrics.EyeWidth * RobotMetrics.EyeHeight, Lit(0));
        Assert.Equal(2 * RobotMetrics.EyeWidth, Lit(1));
    }

    [Fact]
    public void AnAntennaBehindTheHeadStillShowsThroughTheRoundedCorner()
    {
        var buffer = PixelBuffer.ForRobot();
        RobotPainter.Paint(buffer, new HeadPose(-2, 0), Vector2.Zero, 0, RobotPalette.Default);
        var stalk = HeadLayout.For(new HeadPose(-2, 0)).LeftStalk;

        // The shell's top-left corner is cut away; the stalk behind it shows there.
        Assert.Equal(RobotPalette.Default.Ear, buffer[stalk.X, RobotMetrics.HeadTop]);
    }

    [Theory]
    [MemberData(nameof(Poses))]
    public void TheCapStopsAPixelShortOfTheHeadsEdge(int yaw, int pitch)
    {
        var head = HeadLayout.For(new HeadPose(yaw, pitch));

        // The shell's top row runs from X + 2 to Right − 3 (its corners are rounded away); the cap keeps a pixel inside it.
        Assert.True(head.Cap.X >= head.Shell.X + 3, $"yaw {yaw}: cap at {head.Cap.X}, shell at {head.Shell.X}");
        Assert.True(head.Cap.Right <= head.Shell.Right - 3, $"yaw {yaw}: cap to {head.Cap.Right}, shell to {head.Shell.Right}");
    }

    [Fact]
    public void TheCapsParallaxStopsAtTwoSteps()
    {
        // Two pixels a step (one with the shell, one further), then just the shell's one.
        Assert.Equal([8, 10, 12, 13], Enumerable.Range(0, 4).Select(yaw => HeadLayout.For(new HeadPose(yaw, 0)).Cap.X));
        Assert.Equal([8, 6, 4, 3], Enumerable.Range(0, 4).Select(yaw => HeadLayout.For(new HeadPose(-yaw, 0)).Cap.X));
    }

    [Theory]
    [MemberData(nameof(Poses))]
    public void TheCapRestsOnTheHeadAcrossItsWholeWidth(int yaw, int pitch)
    {
        var pose = new HeadPose(yaw, pitch);
        var buffer = PixelBuffer.ForRobot();
        RobotPainter.Paint(buffer, pose, Vector2.Zero, 0, RobotPalette.Default);
        var cap = HeadLayout.For(pose).Cap;

        // Under every pixel of the cap's bottom row there is head, not background.
        for (int x = cap.X; x < cap.Right; x++)
        {
            Assert.NotNull(buffer[x, cap.Bottom]);
        }
    }

    [Fact]
    public void TheBodysOutlineIsSymmetric()
    {
        var buffer = PixelBuffer.ForRobot();
        RobotPainter.Paint(buffer, HeadPose.Center, Vector2.Zero, 0, RobotPalette.Default);

        // The colors aren't (the light comes from one side), but the shape is.
        for (int y = RobotMetrics.HeadTop + RobotMetrics.HeadHeight; y < RobotMetrics.Height; y++)
        {
            for (int x = 0; x < RobotMetrics.Width / 2; x++)
            {
                Assert.Equal(buffer[x, y] is null, buffer[RobotMetrics.Width - 1 - x, y] is null);
            }
        }
    }

    [Fact]
    public void HeadStepsMoveOneUnitPerAxis()
    {
        var pose = new HeadPose(-2, -1);

        Assert.Equal(new HeadPose(-1, 0), pose.StepToward(new HeadPose(2, 1)));
        Assert.Equal(pose, pose.StepToward(pose));
        Assert.Equal(new HeadPose(3, 1), new HeadPose(9, 9).Clamped());
    }
}
