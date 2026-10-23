namespace NeonSidekick.Monitors;

/// <summary>One path of <c>QDC_ALL_PATHS</c> as <see cref="MonitorLayoutPlan.OnlyPath"/> reads it: its index, the target and source it pairs, and whether it is active.</summary>
public readonly record struct LayoutPath(int Index, string Target, string Source, bool Active);

/// <summary>A source mode of <c>QDC_ONLY_ACTIVE_PATHS</c>: its index into the mode array and its desktop position.</summary>
public readonly record struct LayoutSourceMode(int Index, int X, int Y);

/// <summary>
/// The path and mode arithmetic of <see cref="MonitorLayout.Only"/> and <see cref="MonitorLayout.Primary"/> over plain records (2026-10-23,
/// the monitors plan's spike, proven on this PC): Only supplies one path, the target's active one or — for a monitor off the desktop — the
/// pairing whose source no active path uses (Windows may give it another: the spike's XG27UQDMS came back on source 0); Primary shifts every
/// source mode by minus the chosen one's position, so it lands at the desktop's origin and the rest keep their places around it.
/// <see cref="WindowsMonitorSystem"/> applies them. Pure.
/// </summary>
public static class MonitorLayoutPlan
{
    /// <summary>The index of the path <see cref="MonitorLayout.Only"/> supplies for <paramref name="target"/>, or null when it has none.</summary>
    public static int? OnlyPath(IReadOnlyList<LayoutPath> paths, string target)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var mine = paths.Where(p => string.Equals(p.Target, target, StringComparison.Ordinal)).ToList();
        if (mine.FirstOrDefault(p => p.Active) is { Active: true } active)
        {
            return active.Index;
        }

        var used = paths.Where(p => p.Active).Select(p => p.Source).ToHashSet(StringComparer.Ordinal);
        return mine.FirstOrDefault(p => !used.Contains(p.Source)) is { Target: not null } free ? free.Index
            : mine.Count > 0 ? mine[0].Index : null;
    }

    /// <summary>Each source mode's new position with <paramref name="chosen"/> (an index into the mode array) moved to (0,0); the others by the same offset.</summary>
    public static IReadOnlyList<LayoutSourceMode> Primary(IReadOnlyList<LayoutSourceMode> modes, int chosen)
    {
        ArgumentNullException.ThrowIfNull(modes);
        var origin = modes.FirstOrDefault(m => m.Index == chosen);
        return modes.Select(m => m with { X = m.X - origin.X, Y = m.Y - origin.Y }).ToList();
    }
}
