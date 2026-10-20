using System.Numerics;

namespace NeonSidekick.Companion;

/// <summary>
/// Turns a pointer position into where the robot should look: a <see cref="HeadPose"/> zone and a gaze offset.
///
/// <para>Offsets are measured from the head's center looking straight ahead (<see cref="RobotMetrics.HeadCenterColumn"/>,
/// <see cref="RobotMetrics.HeadCenterRow"/>), never from the posed head, so turning can't feed back into which zone is picked.
/// A cell is about twice as tall as it is wide, so the vertical offset is doubled to make both axes the same distance. Each
/// zone boundary has hysteresis: leaving the current zone takes going that far past the boundary, so a pointer resting on an
/// edge doesn't make the head flick back and forth. A pointer over the face falls in the center zone.</para>
///
/// <para>The mouse (<see cref="Track"/>) is measured in cells, so the robot reacts most to a pointer near it. A text cursor
/// (<see cref="TrackAlong"/>) is measured across its line instead: the line splits into seven equal stretches, one per turn,
/// and the eyes slide from one end of it to the other — so typing moves them from the first few characters, wherever the
/// line starts.</para>
/// </summary>
public sealed class GazeTracker
{
    /// <summary>
    /// The yaw zone edges, in cells from the head's center: under 4 is straight, under 10 a slight turn, under 16 a half turn,
    /// beyond a full one.
    /// </summary>
    internal static readonly double[] YawEdges = [-16, -10, -4, 4, 10, 16];

    /// <summary>The yaw zone edges across a line, as a share of its half-width from the head's center: seven equal stretches.</summary>
    internal static readonly double[] LineEdges = [-5 / 7.0, -3 / 7.0, -1 / 7.0, 1 / 7.0, 3 / 7.0, 5 / 7.0];

    /// <summary>The pitch zone edges, in doubled rows from the head's center.</summary>
    internal static readonly double[] PitchEdges = [-6, 6];

    /// <summary>How far past an edge the pointer must go to leave the current zone, in cells.</summary>
    public const double Hysteresis = 1.5;

    /// <summary>The same across a line, as a share of its half-width.</summary>
    public const double LineHysteresis = 0.05;

    /// <summary>The offset at which the eyes reach the edge of their travel.</summary>
    public const double GazeReach = 20;

    private int _yaw;
    private int _pitch;

    /// <summary>The zone the last pointer picked.</summary>
    public HeadPose Pose => new(_yaw, _pitch);

    /// <summary>The gaze the last pointer picked, −1..1 per axis.</summary>
    public Vector2 Gaze { get; private set; }

    /// <summary>
    /// Updates the gaze for a pointer at (<paramref name="column"/>, <paramref name="row"/>), in canvas cells relative to the
    /// robot canvas's top-left (so it may be negative, or past the canvas).
    /// </summary>
    public void Track(double column, double row)
    {
        double dx = column + 0.5 - RobotMetrics.HeadCenterColumn;
        _yaw = Zone(dx, _yaw, YawEdges, Hysteresis);
        Gaze = new Vector2((float)Math.Clamp(dx / GazeReach, -1, 1), TrackRow(row));
    }

    /// <summary>
    /// Updates the gaze for a text cursor at (<paramref name="column"/>, <paramref name="row"/>) on a line running from
    /// column <paramref name="lineStart"/> to <paramref name="lineEnd"/> (exclusive) — all in canvas cells relative to the robot canvas's
    /// top-left like <see cref="Track"/>'s. The position across is measured from the head's center out to the line's further
    /// end, so a line off to one side still uses the whole range on that side.
    /// </summary>
    public void TrackAlong(double column, double row, double lineStart, double lineEnd)
    {
        double center = RobotMetrics.HeadCenterColumn;
        double half = Math.Max(1, Math.Max(center - lineStart, lineEnd - center));
        double across = Math.Clamp((column + 0.5 - center) / half, -1, 1);
        _yaw = Zone(across, _yaw, LineEdges, LineHysteresis);
        Gaze = new Vector2((float)across, TrackRow(row));
    }

    private float TrackRow(double row)
    {
        double dy = (row + 0.5 - RobotMetrics.HeadCenterRow) * 2;
        _pitch = Zone(dy, _pitch, PitchEdges, Hysteresis);
        return (float)Math.Clamp(dy / GazeReach, -1, 1);
    }

    /// <summary>Forgets the last zone, as if the pointer had never moved.</summary>
    public void Reset()
    {
        _yaw = 0;
        _pitch = 0;
        Gaze = Vector2.Zero;
    }

    /// <summary>
    /// The zone of <paramref name="value"/> given the <paramref name="current"/> one: zones are numbered from the middle (−n..n
    /// for 2n edges), and a change sticks only if it still holds with the value pulled <paramref name="hysteresis"/> back toward
    /// the current zone.
    /// </summary>
    internal static int Zone(double value, int current, double[] edges, double hysteresis = Hysteresis)
    {
        int candidate = Plain(value, edges);
        if (candidate > current)
        {
            return Math.Max(current, Plain(value - hysteresis, edges));
        }

        if (candidate < current)
        {
            return Math.Min(current, Plain(value + hysteresis, edges));
        }

        return current;
    }

    private static int Plain(double value, double[] edges)
    {
        int above = 0;
        foreach (double edge in edges)
        {
            if (value >= edge)
            {
                above++;
            }
        }

        return above - (edges.Length / 2);
    }
}
