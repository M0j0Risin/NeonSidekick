namespace NeonSidekick.Monitors;

/// <summary>
/// Which monitors the app itself switched away from this PC, this run (2026-10-23, the monitors plan): a monitor showing another source
/// usually stays on the desktop (proven on this PC), so "active" doesn't mean "showing this PC", and the app can't tell which input is
/// this PC's. What it knows is its own switches: each monitor it switched away and the input it left, keyed by <see cref="MonitorKey"/>
/// (numbers move with the layout). A second switch of a monitor already marked keeps the input it first left (DisplayPort-1 → HDMI-1 →
/// HDMI-2 still marks DisplayPort-1); the mark clears when the monitor is switched back to it (or to this PC's input), when a read shows that
/// input again, or when the monitor leaves the desktop. In memory only, never saved: a stale saved record would be worse than none. A switch
/// made elsewhere (the monitor's buttons, another program) is never in it; since later on 2026-10-23 the user's pick and the cable's kind
/// say the rest (<see cref="MonitorShowing"/>, which reads these marks). Thread-safe.
/// </summary>
public sealed class MonitorAway
{
    private readonly object _gate = new();
    private Dictionary<string, byte> _left = new(StringComparer.Ordinal);

    /// <summary>The marks: a monitor's key → the input it left.</summary>
    public IReadOnlyDictionary<string, byte> Marks
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, byte>(_left, StringComparer.Ordinal);
            }
        }
    }

    /// <summary>The input <paramref name="key"/> left, or null when it is not marked.</summary>
    public byte? LeftFrom(string? key)
    {
        if (key is null)
        {
            return null;
        }

        lock (_gate)
        {
            return _left.TryGetValue(key, out byte from) ? from : null;
        }
    }

    /// <summary>
    /// Notes a switch of <paramref name="key"/> from <paramref name="from"/> (null: not known) to <paramref name="to"/>; true when a mark changed.
    /// Later on 2026-10-23: only a <paramref name="from"/> that counted as showing this PC (<paramref name="fromCounts"/>) is marked — switching
    /// back from an input the monitor's own buttons chose made a mark "left HDMI-1" and ↩ called HDMI-1 this PC's — and a switch to this PC's
    /// input (<paramref name="toIsPc"/>: the pick, or the cable's kind) clears the mark. A 0x00 <paramref name="from"/> is none (no input has it).
    /// </summary>
    public bool Switched(string? key, byte? from, byte to, bool fromCounts = true, bool toIsPc = false)
    {
        if (key is null)
        {
            return false;
        }

        lock (_gate)
        {
            if (_left.TryGetValue(key, out byte left))
            {
                return (left == to || toIsPc) && _left.Remove(key);
            }

            if (fromCounts && !toIsPc && from is { } was && was != 0 && was != to)
            {
                _left[key] = was;
                return true;
            }

            return false;
        }
    }

    /// <summary>Notes a read of <paramref name="key"/>'s input: the mark clears when it shows the input it left again; true when it did.</summary>
    public bool Seen(string? key, byte current)
    {
        if (key is null)
        {
            return false;
        }

        lock (_gate)
        {
            return _left.TryGetValue(key, out byte left) && left == current && _left.Remove(key);
        }
    }

    /// <summary>Drops the marks of monitors no longer on the desktop of <paramref name="snapshot"/>; true when one went.</summary>
    public bool Prune(MonitorSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var active = snapshot.Monitors.Where(m => m.Active && m.Key is not null).Select(m => m.Key!).ToHashSet(StringComparer.Ordinal);
        lock (_gate)
        {
            var kept = _left.Where(kv => active.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            bool changed = kept.Count != _left.Count;
            _left = kept;
            return changed;
        }
    }

}
