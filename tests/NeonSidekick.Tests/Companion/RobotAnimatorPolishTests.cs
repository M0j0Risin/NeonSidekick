using NeonSidekick.Companion;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests.Companion;

/// <summary>Boot-up, the smile while waving and the goodbye — each switched on here, one at a time where it matters.</summary>
public sealed class RobotAnimatorPolishTests
{
    private const int CenterRow = 6;
    private static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(10);

    private readonly ManualTimeProvider _time = new();
    private TimeSpan _elapsed;

    private RobotAnimator Robot(bool bootsUp = false, bool smiles = false) =>
        new(_time, new Random(1234), RobotPalette.Default) { BootsUp = bootsUp, SmilesWhileWaving = smiles };

    private static TimeSpan Ms(double milliseconds) => TimeSpan.FromMilliseconds(milliseconds);

    /// <summary>Runs frames until <paramref name="until"/> after the robot was made, checking each one.</summary>
    private void RunTo(RobotAnimator robot, TimeSpan until, Action? each = null)
    {
        while (_elapsed < until)
        {
            _time.Advance(Frame);
            _elapsed += Frame;
            robot.Update();
            each?.Invoke();
        }
    }

    // --- Boot-up ---

    [Fact]
    public void BootingUpTheEyesFlickerOnThenOpen()
    {
        var robot = Robot(bootsUp: true);
        var seen = new List<int>();
        RunTo(robot, RobotAnimator.BootDuration + Ms(100), () =>
        {
            if (seen.Count == 0 || seen[^1] != robot.Face.Power)
            {
                seen.Add(robot.Face.Power);
            }
        });

        Assert.Equal([RobotFace.Off, RobotFace.Dot, RobotFace.Off, RobotFace.Dot, RobotFace.Line, RobotFace.On], seen);
    }

    [Theory]
    [InlineData(100, RobotFace.Off)]
    [InlineData(290, RobotFace.Dot)]
    [InlineData(360, RobotFace.Off)]
    [InlineData(430, RobotFace.Dot)]
    [InlineData(520, RobotFace.Line)]
    [InlineData(600, RobotFace.On)]
    public void EachBootStageComesOnTime(int milliseconds, int power)
    {
        var robot = Robot(bootsUp: true);
        RunTo(robot, Ms(milliseconds));

        Assert.Equal(power, robot.Face.Power);
        Assert.Equal(milliseconds < RobotAnimator.BootDuration.TotalMilliseconds, robot.IsBooting);
    }

    [Fact]
    public void BootingUpTheCoreIsDarkThenRisesFromItsDimmest()
    {
        var robot = Robot(bootsUp: true);
        var line = RobotAnimator.BootDuration - RobotAnimator.BootLine;
        RunTo(robot, line - Frame, () => Assert.Equal(0, robot.Body.Core));

        var levels = new List<int>();
        RunTo(robot, line + Ms(1200), () => levels.Add(robot.Body.Core));
        Assert.Equal(0, levels[0]);
        Assert.Contains(RobotFace.On, levels); // the brightest level, on the way up
    }

    [Fact]
    public void BootingUpNothingBlinksOrMoves()
    {
        var robot = Robot(bootsUp: true);
        robot.OnPointer(60, CenterRow);

        RunTo(robot, RobotAnimator.BootDuration - Frame, () =>
        {
            Assert.Equal(0f, robot.Blink);
            Assert.Equal(HeadPose.Center, robot.Pose);
            Assert.False(robot.IsWaving);
        });
    }

    [Fact]
    public void APointerSeenWhileBootingIsLookedAtOnceOnWithoutAWave()
    {
        var robot = Robot(bootsUp: true);
        RunTo(robot, Ms(100));
        robot.OnPointer(60, CenterRow);

        // On, it turns to the pointer, and the pointer brought no welcome-back wave...
        RunTo(robot, RobotAnimator.BootDuration + Ms(250), () => Assert.False(robot.IsWaving));
        Assert.Equal(3, robot.Pose.Yaw);

        // ...so the hello still comes.
        RunTo(robot, RobotAnimator.BootDuration + RobotAnimator.HelloAfterBoot + Ms(20));
        Assert.True(robot.IsWaving);
    }

    [Fact]
    public void TheHelloComesJustAfterBootingUp()
    {
        var robot = Robot(bootsUp: true);
        var hello = RobotAnimator.BootDuration + RobotAnimator.HelloAfterBoot;

        RunTo(robot, hello - Ms(20), () => Assert.False(robot.IsWaving));
        RunTo(robot, hello + Ms(20));
        Assert.True(robot.IsWaving);
    }

    [Fact]
    public void ItCannotWaveWhileBooting()
    {
        var robot = Robot(bootsUp: true);
        RunTo(robot, Ms(100));

        Assert.False(robot.Wave());
    }

    [Fact]
    public void WithoutBootingUpItIsOnAtOnce()
    {
        var robot = Robot();
        robot.Update();

        Assert.False(robot.IsBooting);
        Assert.Equal(RobotFace.On, robot.Face.Power);
    }

    [Fact]
    public void BootingUpLeavesTheRandomTimingsAsTheyWereOnlyLater()
    {
        // The same seed blinks at the same moments, just the boot's length later.
        List<TimeSpan> Blinks(bool bootsUp, TimeSpan offset)
        {
            var time = new ManualTimeProvider();
            var robot = new RobotAnimator(time, new Random(7), RobotPalette.Default) { BootsUp = bootsUp };
            var starts = new List<TimeSpan>();
            bool closed = false;
            for (var t = TimeSpan.Zero; t < TimeSpan.FromSeconds(20); t += Frame)
            {
                time.Advance(Frame);
                robot.Update();
                if (robot.Blink > 0 && !closed)
                {
                    starts.Add(t - offset);
                }

                closed = robot.Blink > 0;
            }

            return starts;
        }

        var without = Blinks(false, TimeSpan.Zero);
        var with = Blinks(true, RobotAnimator.BootDuration);
        Assert.NotEmpty(without);
        Assert.Equal(without.Take(2), with.Take(2));
    }

    // --- The smile ---

    [Fact]
    public void ItSmilesThroughAWave()
    {
        var robot = Robot(smiles: true);
        robot.Update();
        Assert.True(robot.Wave());

        RunTo(robot, RobotAnimator.WaveDuration - Frame, () => Assert.Equal(Expression.Smile, robot.Face.Expression));
        RunTo(robot, RobotAnimator.WaveDuration + Ms(50));
        Assert.False(robot.IsWaving);
        Assert.Equal(Expression.Neutral, robot.Face.Expression);
    }

    [Fact]
    public void WithoutSmilingTheFaceStaysNeutral()
    {
        var robot = Robot();
        robot.Update();
        Assert.True(robot.Wave());

        RunTo(robot, RobotAnimator.WaveDuration + Ms(50), () => Assert.Equal(Expression.Neutral, robot.Face.Expression));
    }

    // --- The goodbye ---

    [Fact]
    public void DismissedItNodsHappilyThenPowersDown()
    {
        var robot = Robot(smiles: true);
        robot.OnPointer(-40, 0);
        RunTo(robot, Ms(500));
        var dismissed = _elapsed;
        robot.Dismiss();

        int deepest = 0;
        RunTo(robot, dismissed + RobotAnimator.GoodbyeFor - Frame, () =>
        {
            Assert.Equal(Expression.Happy, robot.Face.Expression);
            Assert.Equal(RobotFace.On, robot.Face.Power);
            Assert.Equal(BodyMotion.MaxCore, robot.Body.Core);
            deepest = Math.Max(deepest, robot.Pose.Pitch);
        });
        Assert.Equal(1, deepest); // the nod
        Assert.Equal(HeadPose.Center, robot.Pose);

        RunTo(robot, dismissed + RobotAnimator.GoodbyeFor + Ms(50));
        Assert.Equal(RobotFace.Line, robot.Face.Power);
        RunTo(robot, dismissed + RobotAnimator.GoodbyeFor + Ms(150));
        Assert.Equal(RobotFace.Dot, robot.Face.Power);
        RunTo(robot, dismissed + RobotAnimator.GoodbyeFor + Ms(250));
        Assert.Equal(RobotFace.Off, robot.Face.Power);
        Assert.Equal(Expression.Happy, robot.Face.Expression); // the smile goes dark with the rest
        Assert.True(robot.IsDismissing);
        Assert.False(robot.IsDone);
    }

    [Fact]
    public void ItIsDoneJustAfterTheDarkFrame()
    {
        var robot = Robot();
        robot.Update();
        robot.Dismiss();

        RunTo(robot, RobotAnimator.DismissDuration - Frame, () => Assert.False(robot.IsDone));
        RunTo(robot, RobotAnimator.DismissDuration + Frame);
        Assert.True(robot.IsDone);
        Assert.False(robot.IsDismissing);
        Assert.Equal(RobotAnimator.DismissDuration, Ms(800));
    }

    [Fact]
    public void DoneNothingChanges()
    {
        var robot = Robot();
        robot.Dismiss();
        RunTo(robot, RobotAnimator.DismissDuration + Ms(50));

        _time.Advance(TimeSpan.FromSeconds(3));
        Assert.False(robot.Update());
    }

    [Fact]
    public void DismissedItIgnoresInput()
    {
        var robot = Robot();
        robot.Update();
        robot.Dismiss();

        robot.OnPointer(60, CenterRow);
        robot.OnCaret(90, 40, 2, 100);
        Assert.False(robot.Wave());
        RunTo(robot, RobotAnimator.DismissDuration + Frame, () => Assert.Equal(0, robot.Pose.Yaw));
        Assert.True(robot.IsDone);
    }

    [Fact]
    public void DismissingEndsAWaveAndALid()
    {
        var robot = Robot();
        robot.Update();
        Assert.True(robot.Wave());
        RunTo(robot, Ms(100));

        robot.Dismiss();
        Assert.False(robot.IsWaving);
        Assert.Equal(0f, robot.Blink);
        RunTo(robot, _elapsed + Ms(50), () => Assert.Equal(0, robot.Body.Wave));
    }

    [Fact]
    public void DismissedWhileBootingItPowersDownFromWhereItGot()
    {
        var robot = Robot(bootsUp: true);
        RunTo(robot, Ms(300)); // a dot
        Assert.Equal(RobotFace.Dot, robot.Face.Power);

        robot.Dismiss();
        RunTo(robot, Ms(350));
        Assert.Equal(RobotFace.Off, robot.Face.Power);
        Assert.Equal(Expression.Neutral, robot.Face.Expression);
        RunTo(robot, Ms(410));
        Assert.True(robot.IsDone);
    }

    [Fact]
    public void DismissedStillDarkItIsDoneAtOnce()
    {
        var robot = Robot(bootsUp: true);
        RunTo(robot, Ms(100));

        robot.Dismiss();
        robot.Update();
        Assert.True(robot.IsDone);
    }

    [Fact]
    public void JustTheHeadShowingItStillSaysGoodbye()
    {
        var robot = Robot();
        robot.HeadOnly = true;
        robot.Dismiss();

        RunTo(robot, RobotAnimator.DismissDuration + Frame);
        Assert.True(robot.IsDone);
    }

    [Fact]
    public void DismissingTwiceChangesNothing()
    {
        var robot = Robot();
        robot.Dismiss();
        RunTo(robot, Ms(400));
        robot.Dismiss();

        RunTo(robot, RobotAnimator.DismissDuration + Frame);
        Assert.True(robot.IsDone);
    }

    [Fact]
    public void DismissingBeforeTheFirstFrameWorks()
    {
        var robot = Robot();
        robot.Dismiss();

        RunTo(robot, RobotAnimator.GoodbyeFor - Frame, () => Assert.Equal(RobotFace.On, robot.Face.Power));
        RunTo(robot, RobotAnimator.DismissDuration + Frame);
        Assert.True(robot.IsDone);
    }

    [Fact]
    public void APointerBeforeTheFirstFrameIsFollowedOnceOn()
    {
        var robot = Robot(bootsUp: true);
        robot.OnPointer(60, CenterRow);

        RunTo(robot, RobotAnimator.BootDuration + Ms(250));
        Assert.Equal(3, robot.Pose.Yaw);
        Assert.False(robot.IsWaving);
    }
}
