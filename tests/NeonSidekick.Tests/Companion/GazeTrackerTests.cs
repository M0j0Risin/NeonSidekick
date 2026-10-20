using NeonSidekick.Companion;
namespace NeonSidekick.Tests.Companion;

public sealed class GazeTrackerTests
{
    // The head's center is column 12 (between cells 11 and 12) and row 6.5.
    private const int CenterRow = 6;

    private readonly GazeTracker _gaze = new();

    [Fact]
    public void CursorRightTurnsRightAndCursorAboveTiltsUp()
    {
        _gaze.Track(40, CenterRow);
        Assert.Equal(3, _gaze.Pose.Yaw);
        Assert.True(_gaze.Gaze.X > 0);

        _gaze.Track(12, -20);
        Assert.Equal(-1, _gaze.Pose.Pitch);
        Assert.True(_gaze.Gaze.Y < 0);

        _gaze.Track(-30, 40);
        Assert.Equal(new HeadPose(-3, 1), _gaze.Pose);
    }

    [Fact]
    public void APointerOverTheFaceLooksStraightAhead()
    {
        for (int column = 8; column <= 15; column++)
        {
            for (int row = 4; row <= 8; row++)
            {
                _gaze.Reset();
                _gaze.Track(column, row);
                Assert.Equal(HeadPose.Center, _gaze.Pose);
            }
        }
    }

    [Fact]
    public void EnteringAZoneTakesGoingPastTheEdgeByTheHysteresis()
    {
        // dx = column + 0.5 - 12: the half-turn edge is 4, so entering takes 5.5.
        _gaze.Track(16, CenterRow);
        Assert.Equal(0, _gaze.Pose.Yaw);

        _gaze.Track(17, CenterRow);
        Assert.Equal(1, _gaze.Pose.Yaw);
    }

    [Fact]
    public void RestingOnAnEdgeDoesNotFlipFlop()
    {
        _gaze.Track(17, CenterRow);
        Assert.Equal(1, _gaze.Pose.Yaw);

        // Back just inside the edge (dx 3.5): still a half turn until 1.5 past it (dx under 2.5).
        foreach (int column in new[] { 15, 16, 15, 16 })
        {
            _gaze.Track(column, CenterRow);
            Assert.Equal(1, _gaze.Pose.Yaw);
        }

        _gaze.Track(13, CenterRow);
        Assert.Equal(0, _gaze.Pose.Yaw);
    }

    [Fact]
    public void DeeperTurnsNeedTheOuterEdges()
    {
        _gaze.Track(22, CenterRow); // dx 10.5: past the half turn's edge at 10, but not by the hysteresis
        Assert.Equal(1, _gaze.Pose.Yaw);

        _gaze.Track(23, CenterRow); // dx 11.5
        Assert.Equal(2, _gaze.Pose.Yaw);

        _gaze.Track(28, CenterRow); // dx 16.5: past the full turn's edge at 16, not by the hysteresis
        Assert.Equal(2, _gaze.Pose.Yaw);

        _gaze.Track(30, CenterRow); // dx 18.5
        Assert.Equal(3, _gaze.Pose.Yaw);

        _gaze.Track(-7, CenterRow); // dx -18.5
        Assert.Equal(-3, _gaze.Pose.Yaw);
    }

    [Fact]
    public void RowsCountDoubleBecauseCellsAreTall()
    {
        // 4.5 cells sideways is still straight ahead...
        _gaze.Track(16, CenterRow);
        Assert.Equal(0, _gaze.Pose.Yaw);

        // ...but 4 rows up (8 width-equivalent cells) tilts the head.
        _gaze.Reset();
        _gaze.Track(12, CenterRow - 4);
        Assert.Equal(-1, _gaze.Pose.Pitch);
    }

    [Fact]
    public void GazeSaturatesAtTheReach()
    {
        _gaze.Track(200, 200);

        Assert.Equal(1f, _gaze.Gaze.X);
        Assert.Equal(1f, _gaze.Gaze.Y);
    }

    [Fact]
    public void AlongALineTheEyesMoveFromTheFirstCharacters()
    {
        // A line from 50 cells left of the head's center to 50 right: the eyes slide with every few cells.
        var gazes = new List<float>();
        for (int column = -38; column <= -30; column++)
        {
            _gaze.TrackAlong(column, 30, -38, 62);
            gazes.Add(_gaze.Gaze.X);
        }

        Assert.True(gazes[0] < -0.95f);
        Assert.True(gazes.Zip(gazes.Skip(1)).All(p => p.Second > p.First), "the eyes follow each character");
        Assert.Equal(-3, _gaze.Pose.Yaw);
        Assert.Equal(1, _gaze.Pose.Pitch); // the line is below
    }

    [Fact]
    public void AlongALineAnEdgeHasHysteresis()
    {
        // across = (column + 0.5 - 12) / 50; the half-left edge at -5/7 (-0.714) is column -24.2.
        _gaze.TrackAlong(-38, 30, -38, 62);
        _gaze.TrackAlong(-23, 30, -38, 62); // -0.69: past the edge, not by the hysteresis
        Assert.Equal(-3, _gaze.Pose.Yaw);

        _gaze.TrackAlong(-21, 30, -38, 62); // -0.65
        Assert.Equal(-2, _gaze.Pose.Yaw);

        _gaze.TrackAlong(-25, 30, -38, 62); // back to -0.73: stays
        Assert.Equal(-2, _gaze.Pose.Yaw);
    }

    [Fact]
    public void ALineOffToOneSideUsesTheWholeRangeOnThatSide()
    {
        // The line starts right under the head's center and runs 40 cells right: its far end is a full right turn.
        _gaze.TrackAlong(51, 30, 12, 52);
        Assert.Equal(3, _gaze.Pose.Yaw);
        Assert.True(_gaze.Gaze.X > 0.95f);
    }

    [Fact]
    public void ZoneNumbersCountFromTheMiddle()
    {
        Assert.Equal(-3, GazeTracker.Zone(-100, -3, GazeTracker.YawEdges));
        Assert.Equal(0, GazeTracker.Zone(0, 0, GazeTracker.YawEdges));
        Assert.Equal(3, GazeTracker.Zone(100, 3, GazeTracker.YawEdges));
        Assert.Equal(1, GazeTracker.Zone(100, 0, GazeTracker.PitchEdges));
    }
}
