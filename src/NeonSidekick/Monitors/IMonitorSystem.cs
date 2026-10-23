namespace NeonSidekick.Monitors;

/// <summary>A layout change, as Win+P names them (2026-10-23, the monitors plan): every monitor extended, all showing the same, only one, or one made primary.</summary>
public enum MonitorLayout
{
    Extend,
    Duplicate,
    Only,
    Primary,
}

/// <summary>The layout in force, as the Layout tab marks it with ● and <c>/monitor list</c> heads with: extended, duplicated (a source shared), or one monitor alone.</summary>
public enum MonitorArrangement
{
    Extended,
    Duplicated,
    Single,
}

/// <summary>An active monitor's mode: its desktop size in pixels, its refresh rate, and where its top-left sits on the desktop.</summary>
public readonly record struct MonitorMode(int Width, int Height, double RefreshHz, int X, int Y);

/// <summary>
/// One display target as the system reads it, before <see cref="MonitorNumbering"/> numbers it: <see cref="Target"/> the system's own
/// handle for it (the adapter and target ids, stable for the run), its device path (null for an indirect or virtual display), its
/// EDID friendly name (scrubbed), the connector's word, whether Windows has it on the desktop, the source it shows
/// (<see cref="Source"/>, shared while duplicated), its GDI device name and its mode.
/// </summary>
public sealed record MonitorTarget(string Target, string? DevicePath, string Name, string Connector, bool Active, string? Source, string? GdiName, MonitorMode? Mode);

/// <summary>
/// A monitor as every front end names it: its <see cref="Number"/> (the screen capture's <c>monitor:N</c>, <see cref="MonitorNumbering"/>),
/// the system's <see cref="Target"/>, its input names' <see cref="Key"/> (<see cref="MonitorKey"/>; null without a device path), its name
/// (a <c> #2</c> on a repeated one), connector, whether on the desktop, primary (at the desktop's origin), its mode, GDI device name,
/// and whether its source is shared with another (duplicated: its inputs can't be told apart).
/// </summary>
public sealed record MonitorInfo(int Number, string Target, string? Key, string Name, string Connector, bool Active, bool Primary, MonitorMode? Mode, string? GdiName, bool Shared)
{
    /// <summary><c>2 XG27UQDMS</c>: the number and the name, a tab's title and every outcome's.</summary>
    public string Label => Number.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + Name;
}

/// <summary>The monitors connected, numbered (active first), and the layout in force. <see cref="Empty"/> before the first read.</summary>
public sealed record MonitorSnapshot(IReadOnlyList<MonitorInfo> Monitors, MonitorArrangement Layout)
{
    public static readonly MonitorSnapshot Empty = new([], MonitorArrangement.Single);

    /// <summary>The monitor the system calls <paramref name="target"/>, or null.</summary>
    public MonitorInfo? ByTarget(string target) => Monitors.FirstOrDefault(m => string.Equals(m.Target, target, StringComparison.Ordinal));

    /// <summary>The monitor numbered <paramref name="number"/>, or null.</summary>
    public MonitorInfo? ByNumber(int number) => Monitors.FirstOrDefault(m => m.Number == number);

    /// <summary>The monitors on the desktop.</summary>
    public IReadOnlyList<MonitorInfo> Active => Monitors.Where(m => m.Active).ToList();
}

/// <summary>
/// What a monitor's MCCS capabilities string says of its inputs: the VCP 0x60 values it lists, or the common four when it lists none
/// (<see cref="Reported"/> false, <see cref="Mccs.CommonInputs"/>); <see cref="Raw"/> the string itself (scrubbed), which only
/// <c>--monitor-check</c> shows — hardware text never reaches the model.
/// </summary>
public sealed record MonitorCaps(IReadOnlyList<byte> Inputs, bool Reported, string Raw);

/// <summary>
/// The monitors connected to this PC (2026-10-23, the monitors plan): the layout through the CCD API and each monitor's input over DDC/CI.
/// <see cref="WindowsMonitorSystem"/> on Windows; <c>FakeMonitorSystem</c> in the tests; none elsewhere (<c>Program</c> passes null: no 🖥️,
/// no <c>/monitor</c>, no tools). Every call is synchronous and may block (a DDC/CI round trip; a capabilities read took 4.5 s on the
/// XG27UQDMS), so callers go through <see cref="MonitorCalls"/>, never the screen's thread. Every call may throw <see cref="MonitorException"/>,
/// whose message is the user's sentence. <paramref name="target"/> is <see cref="MonitorInfo.Target"/>.
/// </summary>
public interface IMonitorSystem
{
    /// <summary>The monitors connected (on the desktop or not), numbered, and the layout.</summary>
    MonitorSnapshot Query();

    /// <summary>
    /// Applies (or, with <paramref name="validateOnly"/>, only checks) a layout: <paramref name="target"/> is the monitor for
    /// <see cref="MonitorLayout.Only"/> and <see cref="MonitorLayout.Primary"/>, ignored for the others.
    /// </summary>
    void Layout(MonitorLayout kind, string? target, bool validateOnly);

    /// <summary>The monitor's capabilities (one DDC/CI round trip, slow).</summary>
    MonitorCaps Capabilities(string target);

    /// <summary>The monitor's current input (VCP 0x60, its low byte).</summary>
    byte CurrentInput(string target);

    /// <summary>Switches the monitor's input (VCP 0x60).</summary>
    void SetInput(string target, byte value);
}

/// <summary>A monitor failure whose message is the sentence the user and the model read (<see cref="MonitorText"/>'s).</summary>
public sealed class MonitorException : Exception
{
    public MonitorException()
    {
    }

    public MonitorException(string message)
        : base(message)
    {
    }

    public MonitorException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
