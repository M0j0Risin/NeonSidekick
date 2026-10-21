using NeonSidekick.Companion;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests.Companion;

public sealed class RobotAnimatorTests
{
    private const int CenterRow = 6;
    private static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(10);

    private readonly ManualTimeProvider _time = new();
    private readonly RobotAnimator _animator;

    public RobotAnimatorTests()
    {
        _animator = AsToday(new Random(1234));
    }

    // The robot as it was before boot-up and smiling: these tests cover everything else, which those leave unchanged.
    // Their own tests (RobotAnimatorPolishTests) switch each on.
    private RobotAnimator AsToday(Random random) =>
        new(_time, random, RobotPalette.Default) { BootsUp = false, SmilesWhileWaving = false };

    private void Run(TimeSpan span)
    {
        for (var elapsed = TimeSpan.Zero; elapsed < span; elapsed += Frame)
        {
            _time.Advance(Frame);
            _animator.Update();
        }
    }

    [Fact]
    public void StartsIdleLookingStraightAhead()
    {
        Assert.True(_animator.Update());

        Assert.True(_animator.IsIdle);
        Assert.Equal(HeadPose.Center, _animator.Pose);
    }

    [Fact]
    public void ThePointerIsTakenRelativeToTheOrigin()
    {
        _animator.SetOrigin(100, 50);
        _animator.OnPointer(100 + 12, 50 + CenterRow);

        Assert.False(_animator.IsIdle);
        Assert.Equal(HeadPose.Center, _animator.TargetPose);

        _animator.OnPointer(100 + 40, 50 + CenterRow);
        Assert.Equal(new HeadPose(3, 0), _animator.TargetPose);
    }

    [Fact]
    public void TheHeadSweepsOneStepPerInterval()
    {
        _animator.OnPointer(-40, CenterRow);
        Run(TimeSpan.FromSeconds(1));
        Assert.Equal(new HeadPose(-3, 0), _animator.Pose);

        _animator.OnPointer(60, CenterRow);
        var seen = new List<int>();
        for (int i = 0; i < 6; i++)
        {
            _time.Advance(RobotAnimator.StepInterval);
            _animator.Update();
            seen.Add(_animator.Pose.Yaw);
        }

        Assert.Equal([-2, -1, 0, 1, 2, 3], seen);
    }

    [Fact]
    public void AtThirtyFramesASecondAFullSweepStillTakesAboutAQuarterSecond()
    {
        // 33 ms frames don't divide 45 ms steps; the leftover carries over, so steps average 45 ms instead of rounding up to 66.
        var frame = TimeSpan.FromMilliseconds(33);
        _animator.OnPointer(-60, CenterRow);
        Run(TimeSpan.FromSeconds(1));

        _animator.OnPointer(80, CenterRow);
        var elapsed = TimeSpan.Zero;
        while (_animator.Pose.Yaw != 3 && elapsed < TimeSpan.FromSeconds(1))
        {
            _time.Advance(frame);
            elapsed += frame;
            _animator.Update();
        }

        Assert.InRange(elapsed, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(300));
    }

    [Fact]
    public void AStillHeadStepsOneAtATimeNeverInABurst()
    {
        _animator.OnPointer(-60, CenterRow);
        Run(TimeSpan.FromSeconds(1));
        Assert.Equal(-3, _animator.Pose.Yaw);
        _animator.OnPointer(80, CenterRow);

        // Even a long gap between frames is one step a frame: no pose is skipped.
        int yaw = _animator.Pose.Yaw;
        for (int i = 0; i < 6; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(2));
            _animator.OnPointer(80, CenterRow); // still there, so it doesn't go idle
            _animator.Update();
            Assert.Equal(yaw + 1, _animator.Pose.Yaw);
            yaw = _animator.Pose.Yaw;
        }
    }

    [Fact]
    public void NoStepBeforeTheIntervalHasPassed()
    {
        _animator.OnPointer(60, CenterRow);
        _animator.Update();
        Assert.Equal(1, _animator.Pose.Yaw);

        _time.Advance(RobotAnimator.StepInterval / 2);
        _animator.Update();
        Assert.Equal(1, _animator.Pose.Yaw);

        _time.Advance(RobotAnimator.StepInterval / 2);
        _animator.Update();
        Assert.Equal(2, _animator.Pose.Yaw);
    }

    [Fact]
    public void GoesIdleWhenThePointerRestsAndResumesOnAMove()
    {
        _animator.OnPointer(60, CenterRow);
        Run(RobotAnimator.IdleAfter - TimeSpan.FromMilliseconds(100));
        Assert.False(_animator.IsIdle);
        Assert.Equal(new HeadPose(3, 0), _animator.Pose);

        Run(TimeSpan.FromMilliseconds(200));
        Assert.True(_animator.IsIdle);
        Assert.Equal(HeadPose.Center, _animator.TargetPose);

        _animator.OnPointer(-60, CenterRow);
        Assert.False(_animator.IsIdle);
        Assert.Equal(new HeadPose(-3, 0), _animator.TargetPose);
    }

    [Fact]
    public void GoesIdleAtOnceWhenFocusIsLost()
    {
        _animator.OnPointer(60, CenterRow);
        _animator.Update();

        _animator.OnFocus(false);
        _animator.Update();

        Assert.True(_animator.IsIdle);
    }

    [Fact]
    public void IdleGlancesAroundWithinFiveSeconds()
    {
        var targets = new HashSet<HeadPose>();
        for (int i = 0; i < 2000; i++)
        {
            _time.Advance(Frame);
            _animator.Update();
            targets.Add(_animator.TargetPose);
        }

        Assert.True(_animator.IsIdle);
        Assert.True(targets.Count > 1, "An idle robot should have glanced somewhere in 20 seconds.");
    }

    [Fact]
    public void BlinksWithinSixSecondsForAboutAHundredFiftyMilliseconds()
    {
        TimeSpan? start = null;
        TimeSpan? end = null;
        float deepest = 0;
        for (var t = TimeSpan.Zero; t < TimeSpan.FromSeconds(7); t += Frame)
        {
            _time.Advance(Frame);
            _animator.Update();
            if (_animator.Blink > 0)
            {
                start ??= t;
                deepest = Math.Max(deepest, _animator.Blink);
            }
            else if (start is not null && end is null)
            {
                end = t;
            }
        }

        Assert.NotNull(start);
        Assert.InRange(start.Value, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(6));
        Assert.NotNull(end);
        Assert.InRange(end.Value - start.Value, TimeSpan.FromMilliseconds(120), TimeSpan.FromMilliseconds(170));
        Assert.True(deepest > 0.85f);
    }

    [Fact]
    public void GazeEasesThenSettlesExactlyOnTheTarget()
    {
        _animator.OnPointer(60, 60);
        _time.Advance(Frame);
        _animator.Update();
        Assert.InRange(_animator.Gaze.X, 0.01f, 0.99f);

        Run(TimeSpan.FromSeconds(1));
        Assert.Equal(1f, _animator.Gaze.X);
        Assert.Equal(1f, _animator.Gaze.Y);
    }

    [Fact]
    public void NothingChangesWhenTimeStandsStill()
    {
        _animator.OnPointer(60, CenterRow);
        Run(TimeSpan.FromMilliseconds(500));

        Assert.False(_animator.Update());
        Assert.False(_animator.Update());
    }

    [Fact]
    public void AStallDoesNotMakeTheEyesJump()
    {
        _animator.Update();
        _animator.OnPointer(60, CenterRow);

        _time.Advance(TimeSpan.FromSeconds(3));
        _animator.Update();

        // One eased step of at most the 100 ms cap: 1 - e^(-100/80) of the way.
        Assert.InRange(_animator.Gaze.X, 0.5f, 0.8f);
    }

    /// <summary>Runs <paramref name="span"/> of frames, calling <paramref name="each"/> after every one.</summary>
    private void Run(TimeSpan span, Action each)
    {
        for (var elapsed = TimeSpan.Zero; elapsed < span; elapsed += Frame)
        {
            _time.Advance(Frame);
            _animator.Update();
            each();
        }
    }

    [Fact]
    public void TheCorePulsesThroughEveryLevel()
    {
        var seen = new HashSet<int>();
        Run(RobotAnimator.PulseCalm, () => seen.Add(_animator.Body.Core));

        Assert.Equal([0, 1, 2, 3], seen.Order());
    }

    [Fact]
    public void AQuickSweepOfThePointerExcitesTheCore()
    {
        int Changes(TimeSpan span)
        {
            int changes = 0;
            int last = _animator.Body.Core;
            Run(span, () =>
            {
                changes += _animator.Body.Core != last ? 1 : 0;
                last = _animator.Body.Core;
            });

            return changes;
        }

        int calm = Changes(TimeSpan.FromMilliseconds(600));

        // Five cells in 10 ms: 500 cells a second.
        _animator.OnPointer(10, CenterRow);
        _time.Advance(Frame);
        _animator.OnPointer(15, CenterRow);
        int excited = Changes(TimeSpan.FromMilliseconds(600));

        Assert.InRange(calm, 1, 3);
        Assert.True(excited >= calm * 2, $"excited {excited} vs calm {calm}");
    }

    [Fact]
    public void ASlowPointerDoesNotExciteTheCore()
    {
        _animator.OnPointer(10, CenterRow);
        _time.Advance(TimeSpan.FromSeconds(1));
        _animator.OnPointer(15, CenterRow);

        int changes = 0;
        int last = _animator.Body.Core;
        Run(TimeSpan.FromMilliseconds(600), () =>
        {
            changes += _animator.Body.Core != last ? 1 : 0;
            last = _animator.Body.Core;
        });

        Assert.InRange(changes, 1, 3);
    }

    [Fact]
    public void TheTorsoFollowsADeepTurnAMomentLater()
    {
        TimeSpan? headThere = null;
        TimeSpan? torsoThere = null;
        var t = TimeSpan.Zero;
        _animator.OnPointer(60, CenterRow);
        Run(TimeSpan.FromSeconds(1), () =>
        {
            t += Frame;
            if (_animator.Pose.Yaw == 2)
            {
                headThere ??= t;
            }

            if (_animator.Body.Lean == 1)
            {
                torsoThere ??= t;
            }
        });

        Assert.NotNull(headThere);
        Assert.NotNull(torsoThere);
        Assert.InRange(torsoThere.Value - headThere.Value, RobotAnimator.LeanDelay, RobotAnimator.LeanDelay + Frame);

        // Back to center: the torso straightens too.
        _animator.OnPointer(12, CenterRow);
        Run(TimeSpan.FromSeconds(1));
        Assert.Equal(0, _animator.Body.Lean);
    }

    [Fact]
    public void ASlightTurnDoesNotTurnTheTorso()
    {
        _animator.OnPointer(18, CenterRow);
        Run(TimeSpan.FromSeconds(1));

        Assert.Equal(1, _animator.Pose.Yaw);
        Assert.Equal(0, _animator.Body.Lean);
    }

    [Fact]
    public void AntennaTipsStayWithTheHeadThroughATurn()
    {
        _animator.OnPointer(-40, CenterRow);
        Run(TimeSpan.FromSeconds(1));

        _animator.OnPointer(60, CenterRow);
        Run(TimeSpan.FromSeconds(1), () => Assert.Equal(0, _animator.Body.Sway));
        Assert.Equal(3, _animator.Pose.Yaw);
    }

    [Fact]
    public void ItWavesHelloShortlyAfterStarting()
    {
        Run(RobotAnimator.HelloAfter - TimeSpan.FromMilliseconds(50));
        Assert.False(_animator.IsWaving);

        var frames = new List<int>();
        Run(RobotAnimator.WaveDuration, () =>
        {
            if (_animator.Body.Wave != 0)
            {
                frames.Add(_animator.Body.WaveFrame);
            }
        });

        // Up, then out and in back and forth, then down; with one hand or the other (nobody to wave toward yet).
        Assert.NotEmpty(frames);
        Assert.Equal(BodyMotion.WaveLift, frames[0]);
        Assert.Equal(BodyMotion.WaveLift, frames[^1]);
        Assert.Contains(BodyMotion.WaveOut, frames);
        Assert.Contains(BodyMotion.WaveIn, frames);
        int swings = frames.Zip(frames.Skip(1)).Count(p => p.First != p.Second && p.First != BodyMotion.WaveLift && p.Second != BodyMotion.WaveLift);
        Assert.Equal(RobotAnimator.WaveSwings - 1, swings);

        Run(TimeSpan.FromMilliseconds(100));
        Assert.False(_animator.IsWaving);
        Assert.Equal(0, _animator.Body.Wave);
    }

    [Fact]
    public void ItWavesWelcomeBackWithTheHandTowardThePointer()
    {
        Run(TimeSpan.FromSeconds(25)); // the hello, then the cooldown, idle all along
        Assert.False(_animator.IsWaving);

        _animator.OnPointer(60, CenterRow);
        _animator.Update();

        Assert.True(_animator.IsWaving);
        Assert.Equal(1, _animator.Body.Wave);
    }

    [Fact]
    public void ItWavesOnItsOwnAtMostOncePerCooldown()
    {
        Run(TimeSpan.FromSeconds(25));
        _animator.OnPointer(-60, CenterRow); // welcome back, left hand
        Run(TimeSpan.FromSeconds(1));
        Assert.Equal(-1, _animator.Body.Wave);

        // Idle again, then back within the cooldown: no second wave.
        Run(RobotAnimator.IdleAfter + TimeSpan.FromSeconds(1));
        Assert.True(_animator.IsIdle);
        _animator.OnPointer(-60, CenterRow);
        _animator.Update();
        Assert.False(_animator.IsWaving);

        // Asked for, it waves anyway.
        Assert.True(_animator.Wave());
        _animator.Update();
        Assert.Equal(-1, _animator.Body.Wave);
    }

    [Fact]
    public void AWaveAlreadyUnderWayIsNotRestarted()
    {
        Assert.True(_animator.Wave(1));
        Assert.False(_animator.Wave(-1));
        _animator.Update();

        Assert.Equal(1, _animator.Body.Wave);
    }

    [Fact]
    public void WithThePointerStraightAheadEitherHandMayWave()
    {
        _animator.OnPointer(12, CenterRow);
        Run(RobotAnimator.WaveDuration + TimeSpan.FromSeconds(1)); // let the welcome finish

        Assert.True(_animator.Wave());
        _animator.Update();
        Assert.Contains(_animator.Body.Wave, new[] { -1, 1 });
    }

    [Fact]
    public void AMoveBeforeTheHelloReplacesIt()
    {
        Run(TimeSpan.FromMilliseconds(300));
        _animator.OnPointer(60, CenterRow); // coming "back" from the starting idle: this is the hello
        Run(RobotAnimator.WaveDuration + TimeSpan.FromSeconds(1));

        Assert.False(_animator.IsWaving);
    }

    [Fact]
    public void TypingTakesItsEyesToTheTextCursor()
    {
        _animator.OnPointer(-60, CenterRow);
        Run(TimeSpan.FromSeconds(1));
        Assert.False(_animator.IsFollowingCaret);

        // The caret far below, at the right end of its line: it looks down and right.
        _animator.OnCaret(79, 40, 0, 80);
        Run(TimeSpan.FromSeconds(1));

        Assert.True(_animator.IsFollowingCaret);
        Assert.Equal(new HeadPose(3, 1), _animator.Pose);
        Assert.True(_animator.Gaze.Y > 0);
    }

    [Fact]
    public void TypingTurnsTheHeadASeventhOfTheLineAtATime()
    {
        // A 100-cell line with the robot centered over it, typed from its start (after a 2-cell prompt).
        _animator.SetOrigin(38, 0);
        var turns = new List<(int Column, int Yaw)>();
        int yaw = int.MinValue;
        for (int column = 2; column < 100; column++)
        {
            _animator.OnCaret(column, 40, 2, 100);
            if (_animator.TargetPose.Yaw != yaw)
            {
                yaw = _animator.TargetPose.Yaw;
                turns.Add((column, yaw));
            }
        }

        // Full left at the start, then a step every seventh of the line or so (a little later for the hysteresis).
        Assert.Equal([-3, -2, -1, 0, 1, 2, 3], turns.Select(t => t.Yaw));
        Assert.InRange(turns[1].Column, 16, 18);
        Assert.InRange(turns[2].Column, 30, 32);
        Assert.InRange(turns[3].Column, 44, 46);
        Assert.InRange(turns[4].Column, 59, 61);
        Assert.InRange(turns[5].Column, 73, 75);
        Assert.InRange(turns[6].Column, 87, 89);
    }

    [Fact]
    public void APauseInTypingSendsItIdleSoonerThanTheMouse()
    {
        _animator.OnCaret(12, 40, 0, 80);
        Run(RobotAnimator.TypingIdleAfter - TimeSpan.FromMilliseconds(100));
        Assert.True(_animator.IsFollowingCaret);

        Run(TimeSpan.FromMilliseconds(200));
        Assert.True(_animator.IsIdle);
        Assert.False(_animator.IsFollowingCaret);

        // Typing again picks the caret back up.
        _animator.OnCaret(12, 40, 0, 80);
        Assert.True(_animator.IsFollowingCaret);
    }

    [Fact]
    public void MovingTheMouseTakesItsEyesBack()
    {
        _animator.OnCaret(40, 40, 0, 80);
        Run(TimeSpan.FromSeconds(1));

        _animator.OnPointer(-60, CenterRow);
        Run(TimeSpan.FromSeconds(1));

        Assert.False(_animator.IsFollowingCaret);
        Assert.False(_animator.IsIdle);
        Assert.Equal(new HeadPose(-3, 0), _animator.Pose);
    }

    [Fact]
    public void ComingBackByTypingDoesNotWave()
    {
        Run(TimeSpan.FromSeconds(25)); // past the hello and its cooldown, idle

        _animator.OnCaret(12, 40, 0, 80);
        _animator.Update();

        Assert.False(_animator.IsWaving);
    }

    [Fact]
    public void JumpsOfTheCaretDoNotExciteTheCore()
    {
        // Enter sends the caret from the end of a long line back to its start in an instant.
        _animator.OnCaret(70, 40, 0, 80);
        _time.Advance(Frame);
        _animator.OnCaret(2, 40, 0, 80);

        int changes = 0;
        int last = _animator.Body.Core;
        Run(TimeSpan.FromMilliseconds(600), () =>
        {
            changes += _animator.Body.Core != last ? 1 : 0;
            last = _animator.Body.Core;
        });

        Assert.InRange(changes, 1, 3);
    }

    [Fact]
    public void ThePointerOnTheHeadsCenterIsStraightAhead()
    {
        var robot = AsToday(new Random(1));
        robot.SetOrigin(10, 5);

        // The head's center, column 12 and row 6.5 of the canvas, moved by the origin.
        robot.OnPointer(10 + 12, 5 + 6);

        Assert.Equal(HeadPose.Center, robot.TargetPose);
    }

    [Fact]
    public void WithJustTheHeadShowingItDoesNotWave()
    {
        _animator.HeadOnly = true;

        // No hello...
        Run(RobotAnimator.HelloAfter + RobotAnimator.WaveDuration, () => Assert.False(_animator.IsWaving));

        // ...no welcome back (after the robot has gone idle)...
        Run(TimeSpan.FromSeconds(25));
        _animator.OnPointer(60, CenterRow);
        _animator.Update();
        Assert.False(_animator.IsWaving);

        // ...and asking does nothing.
        Assert.False(_animator.Wave());
        _animator.Update();
        Assert.Equal(0, _animator.Body.Wave);

        // It still follows the pointer.
        Run(TimeSpan.FromSeconds(1));
        Assert.Equal(3, _animator.Pose.Yaw);
    }

    [Fact]
    public void ShowingJustTheHeadEndsAWaveUnderWay()
    {
        Assert.True(_animator.Wave(1));
        _animator.Update();
        Assert.Equal(1, _animator.Body.Wave);

        _animator.HeadOnly = true;
        _animator.Update();

        Assert.False(_animator.IsWaving);
        Assert.Equal(0, _animator.Body.Wave);
    }

    [Fact]
    public void ASkippedWelcomeBackDoesNotUseUpTheCooldown()
    {
        Run(TimeSpan.FromSeconds(25)); // past the hello and its cooldown
        _animator.HeadOnly = true;
        _animator.OnPointer(60, CenterRow); // skipped: only the head shows
        Run(RobotAnimator.IdleAfter + TimeSpan.FromSeconds(1));
        Assert.True(_animator.IsIdle);

        // The body is back and the pointer returns: this one waves.
        _animator.HeadOnly = false;
        _animator.OnPointer(60, CenterRow);
        _animator.Update();
        Assert.True(_animator.IsWaving);
    }

    [Fact]
    public void TheBodyStillRedrawsOnlyWhenAPixelChanges()
    {
        Run(TimeSpan.FromSeconds(2));
        int redraws = 0;
        for (int i = 0; i < 300; i++)
        {
            _time.Advance(Frame);
            redraws += _animator.Update() ? 1 : 0;
        }

        // 3 s at 100 frames a second: the pulse (and the odd blink) changes a handful of pixels a few times a second, not every frame.
        Assert.InRange(redraws, 5, 60);
    }
}
