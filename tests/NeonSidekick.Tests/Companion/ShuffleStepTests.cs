using NeonSidekick.Companion;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests.Companion;

/// <summary>The feet shuffling after the torso's lean.</summary>
public sealed class ShuffleStepTests
{
    private const int CenterRow = 6;
    private static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(10);

    private readonly ManualTimeProvider _time = new();
    private readonly RobotAnimator _robot;

    public ShuffleStepTests()
    {
        _robot = new RobotAnimator(_time, new Random(1234), RobotPalette.Default) { BootsUp = false, SmilesWhileWaving = false };
    }

    private (int Left, int Right, int Lift) Legs => (_robot.Body.LeftLeg, _robot.Body.RightLeg, _robot.Body.Lift);

    /// <summary>Runs <paramref name="span"/> of frames, collecting each distinct leg state in order, with when it began.</summary>
    private List<((int Left, int Right, int Lift) Legs, TimeSpan At)> Watch(TimeSpan span, Action? each = null)
    {
        var seen = new List<((int, int, int), TimeSpan)>();
        for (var elapsed = Frame; elapsed <= span; elapsed += Frame)
        {
            _time.Advance(Frame);
            _robot.Update();
            each?.Invoke();
            if (seen.Count == 0 || seen[^1].Item1 != Legs)
            {
                seen.Add((Legs, elapsed));
            }
        }

        return seen;
    }

    // Holds the pointer far to one side (re-reporting it so the robot doesn't go idle).
    private void Point(int side) => _robot.OnPointer(12 + (60 * side), CenterRow);

    [Fact]
    public void TurningFullRightTheNearFootThenTheFarOneStepOver()
    {
        _robot.Update();
        Point(1);
        TimeSpan? leaned = null;
        var seen = Watch(TimeSpan.FromSeconds(1), () => leaned ??= _robot.Body.Lean == 1 ? TimeSpan.Zero : null);

        Assert.Equal(
            [(0, 0, 0), (0, 1, 1), (0, 1, 0), (1, 1, -1), (1, 1, 0)],
            seen.Select(s => s.Legs));

        // Each frame of the step lasts StepFrame.
        for (int i = 2; i < seen.Count; i++)
        {
            Assert.InRange(seen[i].At - seen[i - 1].At, RobotAnimator.StepFrame - Frame, RobotAnimator.StepFrame + Frame);
        }
    }

    [Fact]
    public void TheStepStartsAsTheTorsoLeans()
    {
        _robot.Update();
        Point(1);
        for (int i = 0; i < 100 && _robot.Body.Lean == 0; i++)
        {
            _time.Advance(Frame);
            _robot.Update();
            if (_robot.Body.Lean == 1)
            {
                // The same frame the hips go over, the near (right) foot comes up.
                Assert.Equal((0, 1, 1), Legs);
            }
        }

        Assert.Equal(1, _robot.Body.Lean);
    }

    [Fact]
    public void TurningBackTheLeftFootLeads()
    {
        _robot.Update();
        Point(1);
        Watch(TimeSpan.FromSeconds(1));

        Point(0);
        var seen = Watch(TimeSpan.FromSeconds(1));

        Assert.Equal(
            [(1, 1, 0), (0, 1, -1), (0, 1, 0), (0, 0, 1), (0, 0, 0)],
            seen.Select(s => s.Legs));
    }

    [Fact]
    public void AQuickSweepStepsThroughEveryStanceAndNeverDoublesUp()
    {
        _robot.Update();
        Point(-1);
        Watch(TimeSpan.FromSeconds(1));
        Assert.Equal((-1, -1, 0), Legs);

        // Straight across to full right: the lean goes −1 → 0 → +1 faster than the feet can follow.
        Point(1);
        var states = new List<(int Left, int Right, int Lift)>();
        Watch(TimeSpan.FromSeconds(2), () => states.Add(Legs));

        Assert.All(states, legs =>
        {
            Assert.InRange(legs.Left, -1, 1);
            Assert.InRange(legs.Right, -1, 1);
            Assert.InRange(Math.Abs(legs.Left - legs.Right), 0, 1); // never more than a step apart
        });

        // Two whole steps, each led by the right foot (the side they go): −1 → 0, then 0 → +1. The second follows straight on
        // from the first — the left foot lands as the right one lifts — like walking, with no planted pause between.
        var distinct = states.Where((s, i) => i == 0 || s != states[i - 1]).ToList();
        Assert.Equal(
            [(-1, 0, 1), (-1, 0, 0), (0, 0, -1), (0, 1, 1), (0, 1, 0), (1, 1, -1), (1, 1, 0)],
            distinct.SkipWhile(s => s == (-1, -1, 0)));
    }

    [Fact]
    public void HoldingADeepTurnTheFeetStayPlanted()
    {
        _robot.Update();
        Point(1);
        Watch(TimeSpan.FromSeconds(1));

        // Re-reporting the pointer once a second keeps the robot following it, and the feet stay put under the lean.
        for (int second = 0; second < 3; second++)
        {
            Point(1);
            Assert.All(Watch(TimeSpan.FromSeconds(1)), s => Assert.Equal((1, 1, 0), s.Legs));
        }
    }

    [Fact]
    public void LookingStraightAheadTheyNeverMove()
    {
        _robot.Update();
        Point(0);

        Assert.All(Watch(TimeSpan.FromSeconds(3)), s => Assert.Equal((0, 0, 0), s.Legs));
    }
}
