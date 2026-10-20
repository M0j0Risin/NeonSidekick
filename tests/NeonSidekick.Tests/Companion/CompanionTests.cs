using NeonSidekick.Companion;
using NeonSidekick.UI;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests.Companion;

/// <summary>The robot through the host's contract (<see cref="ICompanion"/>).</summary>
public sealed class CompanionTests
{
    private readonly ManualTimeProvider _time = new();

    private ICompanion Robot() => new RobotAnimator(_time, new Random(1234), RobotPalette.Default) { BootsUp = false };

    [Fact]
    public void TheCanvasIsTheRobotsAndTheFrameIsItsSize()
    {
        var companion = Robot();

        Assert.Equal(new CompanionCanvas(24, 40, 22), companion.Canvas);
        Assert.Equal((companion.Canvas.Width, companion.Canvas.Height), (companion.Frame.Width, companion.Frame.Height));
    }

    [Fact]
    public void ATheme_IsMappedOntoTheRobot_AndNoneIsItsOwnColors()
    {
        var companion = Robot();
        var robot = (RobotAnimator)companion;
        var roles = ThemeRoles.From(ThemeLibrary.Get("collider"));

        Assert.Null(companion.Theme);
        Assert.Equal(RobotPalette.Default, robot.Palette);

        companion.Theme = roles;
        Assert.Same(roles, companion.Theme);
        Assert.Equal(RobotPalette.FromTheme(roles), robot.Palette);

        companion.Theme = null;
        Assert.Equal(RobotPalette.Default, robot.Palette);
    }

    [Fact]
    public void ANewThemeRepaints()
    {
        var companion = Robot();
        companion.Update();

        companion.Theme = ThemeRoles.From(ThemeLibrary.Get("glitch"));
        Assert.True(companion.Update());
    }

    [Fact]
    public void TheDebugLineNamesThePoseAndTheState()
    {
        var companion = Robot();
        companion.Update();

        Assert.StartsWith("pose 0,0 -> 0,0 | gaze ", companion.DebugLine, StringComparison.Ordinal);
        Assert.EndsWith("| idle", companion.DebugLine, StringComparison.Ordinal);

        companion.OnPointer(60, 6);
        companion.Update();
        Assert.Contains("-> +3,0", companion.DebugLine, StringComparison.Ordinal);
        Assert.EndsWith("| mouse", companion.DebugLine, StringComparison.Ordinal);
    }
}
