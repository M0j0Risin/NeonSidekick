namespace NeonSidekick.Monitors;

/// <summary>
/// The monitors' numbers and names (2026-10-23, the monitors plan): numbers match the screen capture's <c>monitor:N</c> — the active
/// targets in <c>EnumDisplayMonitors</c>' order, matched by GDI device name — so <c>/monitor 2</c>, the pane's <c>2 XG27UQDMS</c> tab and
/// <c>screen_capture monitor:2</c> are the same screen. While duplicated the targets sharing a screen sit together and the numbers run
/// on; monitors off the desktop come after. Not Windows Settings' "1"/"2", which nothing claims. A repeated name gets <c> #2</c>,
/// <c> #3</c> in number order, everywhere a monitor is named. Pure.
/// </summary>
public static class MonitorNumbering
{
    /// <summary>The snapshot of <paramref name="targets"/> (in the system's path order) numbered by <paramref name="gdiOrder"/>, the GDI names as <c>EnumDisplayMonitors</c> gives them.</summary>
    public static MonitorSnapshot Build(IReadOnlyList<MonitorTarget> targets, IReadOnlyList<string> gdiOrder)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(gdiOrder);
        int Place(MonitorTarget t)
        {
            int at = t.GdiName is null ? -1 : gdiOrder.ToList().FindIndex(g => string.Equals(g, t.GdiName, StringComparison.OrdinalIgnoreCase));
            return at < 0 ? int.MaxValue : at;
        }

        var active = targets.Where(t => t.Active).Select((t, i) => (t, i)).OrderBy(x => Place(x.t)).ThenBy(x => x.i).Select(x => x.t).ToList();
        var inactive = targets.Where(t => !t.Active).ToList();
        var sources = active.Where(t => t.Source is not null).GroupBy(t => t.Source!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var ordered = active.Concat(inactive).ToList();
        var counts = ordered.GroupBy(t => BaseName(t), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var monitors = new List<MonitorInfo>(ordered.Count);
        for (int i = 0; i < ordered.Count; i++)
        {
            var t = ordered[i];
            string name = BaseName(t);
            int n = seen[name] = seen.GetValueOrDefault(name) + 1;
            string shown = counts[name] > 1 && n > 1 ? name + " #" + n.ToString(System.Globalization.CultureInfo.InvariantCulture) : name;
            bool shared = t.Active && t.Source is not null && sources.GetValueOrDefault(t.Source) > 1;
            bool primary = t.Active && t.Mode is { X: 0, Y: 0 };
            monitors.Add(new MonitorInfo(i + 1, t.Target, MonitorKey.From(t.DevicePath), shown, t.Connector, t.Active, primary, t.Active ? t.Mode : null, t.Active ? t.GdiName : null, shared));
        }

        return new MonitorSnapshot(monitors, ArrangementOf(monitors));
    }

    /// <summary>One monitor (or none) on the desktop is <see cref="MonitorArrangement.Single"/>; any source shared is duplicated; else extended.</summary>
    public static MonitorArrangement ArrangementOf(IReadOnlyList<MonitorInfo> monitors)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        int active = monitors.Count(m => m.Active);
        return active <= 1 ? MonitorArrangement.Single : monitors.Any(m => m.Active && m.Shared) ? MonitorArrangement.Duplicated : MonitorArrangement.Extended;
    }

    private static string BaseName(MonitorTarget t) => t.Name.Trim().Length > 0 ? t.Name.Trim() : MonitorText.Unnamed;
}
