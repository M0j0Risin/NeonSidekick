namespace NeonSidekick.Companion;

/// <summary>
/// Where the head points: <see cref="Yaw"/> −3 (full left) to +3 (full right), <see cref="Pitch"/> −1 (up) to +1 (down).
/// Left, right, up and down are the screen's, as the viewer sees them.
/// </summary>
public readonly record struct HeadPose(int Yaw, int Pitch)
{
    /// <summary>Straight ahead.</summary>
    public static HeadPose Center => default;

    /// <summary>All 21 poses (7 turns × 3 tilts), up row first, left to right.</summary>
    public static IReadOnlyList<HeadPose> All { get; } =
        [.. from pitch in Enumerable.Range(-RobotMetrics.MaxPitch, (2 * RobotMetrics.MaxPitch) + 1)
            from yaw in Enumerable.Range(-RobotMetrics.MaxYaw, (2 * RobotMetrics.MaxYaw) + 1)
            select new HeadPose(yaw, pitch)];

    /// <summary>This pose with both axes clamped to the head's range.</summary>
    public HeadPose Clamped() => new(
        Math.Clamp(Yaw, -RobotMetrics.MaxYaw, RobotMetrics.MaxYaw),
        Math.Clamp(Pitch, -RobotMetrics.MaxPitch, RobotMetrics.MaxPitch));

    /// <summary>One step toward <paramref name="target"/>: each axis moves at most one unit, so a turn sweeps through every pose between.</summary>
    public HeadPose StepToward(HeadPose target) => new(Yaw + Math.Sign(target.Yaw - Yaw), Pitch + Math.Sign(target.Pitch - Pitch));
}
