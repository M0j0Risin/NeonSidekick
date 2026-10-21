using NeonSidekick.Companion;
using System.Numerics;
using Spectre.Console;

namespace NeonSidekick.Tests.Companion;

public sealed class RobotFaceTests
{
    private static readonly RobotPalette Palette = RobotPalette.Default;

    private static PixelBuffer Paint(RobotFace face, HeadPose? pose = null, Vector2? gaze = null, BodyMotion? body = null)
    {
        var buffer = PixelBuffer.ForRobot();
        RobotPainter.Paint(buffer, pose ?? HeadPose.Center, gaze ?? Vector2.Zero, 0, Palette, body ?? BodyMotion.Rest, face);
        return buffer;
    }

    private static RobotFace Face(Expression expression = Expression.Neutral, int power = RobotFace.On) =>
        new(expression, power);

    private static int Count(PixelBuffer buffer, Color color, PixelRect? within = null)
    {
        var area = within ?? new PixelRect(0, 0, buffer.Width, buffer.Height);
        int count = 0;
        for (int y = area.Y; y < area.Bottom; y++)
        {
            for (int x = area.X; x < area.Right; x++)
            {
                count += buffer[x, y] == color ? 1 : 0;
            }
        }

        return count;
    }

    [Fact]
    public void TheDefaultFaceIsWhatPaintsWithoutOne()
    {
        var buffer = PixelBuffer.ForRobot();
        RobotPainter.Paint(buffer, HeadPose.Center, Vector2.Zero, 0, Palette, BodyMotion.Rest);

        Assert.True(buffer.SameAs(Paint(RobotFace.Default)));
    }

    [Fact]
    public void ASmileRaisesTheMouthsCornersAtEveryPose()
    {
        foreach (var pose in HeadPose.All)
        {
            var mouth = HeadLayout.For(pose).Mouth;
            var buffer = Paint(Face(Expression.Smile), pose);

            Assert.Equal(Palette.Mouth, buffer[mouth.X, mouth.Y]);
            Assert.Equal(Palette.Mouth, buffer[mouth.Right - 1, mouth.Y]);
            Assert.Equal(Palette.Mouth, buffer[mouth.X + 1, mouth.Y + 1]);
            Assert.Equal(Palette.Mouth, buffer[mouth.X + 2, mouth.Y + 1]);

            // The gaps show the shell.
            Assert.Equal(Palette.Shell, buffer[mouth.X + 1, mouth.Y]);
            Assert.Equal(Palette.Shell, buffer[mouth.X, mouth.Y + 1]);
            Assert.Equal(4, Count(buffer, Palette.Mouth, mouth));
        }
    }

    [Fact]
    public void ASmileLeavesTheEyesAlone()
    {
        var neutral = Paint(RobotFace.Default);
        var smile = Paint(Face(Expression.Smile));
        var visor = HeadLayout.For(HeadPose.Center).Visor;

        for (int y = visor.Y; y < visor.Bottom; y++)
        {
            for (int x = visor.X; x < visor.Right; x++)
            {
                Assert.Equal(neutral[x, y], smile[x, y]);
            }
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void HappyEyesAreArcsInsideTheVisorWithAGapBetween(int yaw)
    {
        var pose = new HeadPose(yaw, 0);
        var head = HeadLayout.For(pose);
        var buffer = Paint(Face(Expression.Happy), pose, new Vector2(1, 1));

        foreach (var eye in (ReadOnlySpan<PixelRect>)[head.LeftEye, head.RightEye])
        {
            // ^: the middle two pixels up, the outer two down — on the eye's neutral place, whatever the gaze.
            Assert.Equal(Palette.Eye, buffer[eye.X, eye.Y + 1]);
            Assert.Equal(Palette.Eye, buffer[eye.X + 1, eye.Y + 1]);
            Assert.Equal(Palette.Eye, buffer[eye.X - 1, eye.Y + 2]);
            Assert.Equal(Palette.Eye, buffer[eye.Right, eye.Y + 2]);
            Assert.Equal(Palette.Visor, buffer[eye.X - 1, eye.Y + 1]);
            Assert.Equal(Palette.Visor, buffer[eye.X, eye.Y + 2]);
            Assert.True(head.Visor.Contains(new PixelRect(eye.X - 1, eye.Y + 1, 4, 2)));
        }

        Assert.Equal(8, Count(buffer, Palette.Eye, head.Visor));
        Assert.Equal(0, Count(buffer, Palette.EyeShade, head.Visor));

        // The arcs never meet: at least a pixel between the left one's end and the right one's start.
        Assert.True(head.RightEye.X - 1 - head.LeftEye.Right >= 2);
    }

    [Theory]
    [InlineData(-3)]
    [InlineData(-2)]
    [InlineData(2)]
    [InlineData(3)]
    public void FurtherOutHappyIsJustTheSmile(int yaw)
    {
        var pose = new HeadPose(yaw, 0);

        Assert.True(Paint(Face(Expression.Smile), pose).SameAs(Paint(Face(Expression.Happy), pose)));
    }

    [Fact]
    public void AtTheLineStageEachEyeIsABrightLineAndTheLightsStayOn()
    {
        var gaze = new Vector2(0.5f, 0.5f);
        var head = HeadLayout.For(HeadPose.Center);
        var buffer = Paint(Face(power: RobotFace.Line), gaze: gaze);

        foreach (var neutral in (ReadOnlySpan<PixelRect>)[head.LeftEye, head.RightEye])
        {
            var eye = HeadLayout.EyeAt(neutral, gaze);
            Assert.Equal(Palette.ShellLight, buffer[eye.X, eye.Y + 1]);
            Assert.Equal(Palette.ShellLight, buffer[eye.X + 1, eye.Y + 1]);
        }

        Assert.Equal(4, Count(buffer, Palette.ShellLight, head.Visor));
        Assert.Equal(0, Count(buffer, Palette.Eye, head.Visor));
        Assert.True(Count(buffer, Palette.Cap) > 0);
        Assert.True(Count(buffer, Palette.Eye) > 0); // the core still glows
    }

    [Fact]
    public void AtTheDotStageEachEyeIsOnePixelAndTheCapAndCoreAreDark()
    {
        var head = HeadLayout.For(HeadPose.Center);
        var buffer = Paint(Face(power: RobotFace.Dot));

        Assert.Equal(Palette.ShellLight, buffer[head.LeftEye.X + 1, head.LeftEye.Y + 1]);
        Assert.Equal(Palette.ShellLight, buffer[head.RightEye.X + 1, head.RightEye.Y + 1]);
        Assert.Equal(2, Count(buffer, Palette.ShellLight, head.Visor));
        Assert.Equal(0, Count(buffer, Palette.Cap));
        Assert.Equal(0, Count(buffer, Palette.Eye));
        Assert.Equal(0, Count(buffer, Palette.EyeShade));
    }

    [Fact]
    public void SwitchedOffThereAreNoEyesNoCapLightAndNoCore()
    {
        var head = HeadLayout.For(HeadPose.Center);
        var buffer = Paint(Face(power: RobotFace.Off), body: BodyMotion.Rest with { Core = BodyMotion.MaxCore });

        Assert.Equal(0, Count(buffer, Palette.ShellLight, head.Visor));
        Assert.Equal(0, Count(buffer, Palette.Cap));
        Assert.Equal(0, Count(buffer, Palette.Eye));
        Assert.Equal(0, Count(buffer, Palette.EyeShade));

        // The visor is still there, dark.
        Assert.Equal(Palette.Visor, buffer[head.LeftEye.X, head.LeftEye.Y]);
    }

    [Fact]
    public void SwitchedOffTheSmileStillShows()
    {
        var mouth = HeadLayout.For(HeadPose.Center).Mouth;
        var buffer = Paint(Face(Expression.Happy, RobotFace.Off));

        Assert.Equal(4, Count(buffer, Palette.Mouth, mouth));
        Assert.Equal(Palette.Shell, buffer[mouth.X + 1, mouth.Y]);
    }
}
