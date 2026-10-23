using NeonSidekick.Monitors;

namespace NeonSidekick.App;

public static partial class SmokeChecks
{
    /// <summary>
    /// <c>monitor:ccd</c> (2026-10-23, the monitors plan): the CCD imports in the published binary — the topology read whole
    /// (<c>QueryDisplayConfig</c>, the target names through <c>DisplayConfigGetDeviceInfo</c>, the GDI order through the
    /// <c>[UnmanagedCallersOnly]</c> callback) and an Extend only validated (<c>SDC_VALIDATE</c>: never applied). Changes nothing. Skipped off
    /// Windows and in a session with no display (a CI runner, a service: no active path, or access denied).
    /// </summary>
    public static SmokeCheck ProbeMonitorCcd()
    {
        const string name = "monitor:ccd";
        if (!OperatingSystem.IsWindows())
        {
            return new SmokeCheck(name, true, "skipped: not Windows");
        }

        try
        {
            var system = new WindowsMonitorSystem();
            MonitorSnapshot snapshot;
            try
            {
                snapshot = system.Query();
            }
            catch (MonitorException e) when (e.Message == MonitorText.OnlyFromDesktop)
            {
                return new SmokeCheck(name, true, "skipped: no display in this session");
            }

            if (snapshot.Active.Count == 0)
            {
                return new SmokeCheck(name, true, "skipped: no monitor in this session");
            }

            system.Layout(MonitorLayout.Extend, null, validateOnly: true);
            var first = snapshot.Active[0];
            return new SmokeCheck(name, true, $"{snapshot.Monitors.Count} monitor(s), {MonitorText.LayoutWord(snapshot.Layout)}; 1 is {first.Name} ({first.Connector}{(first.Mode is { } m ? ", " + MonitorText.Mode(m) : "")}); Extend validated");
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return new SmokeCheck(name, false, $"{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>
    /// <c>monitor:ddc</c> (2026-10-23): dxva2's DDC/CI imports in the published binary — the primary monitor's physical monitors counted, its
    /// current input and its capabilities read (the string's length reported, never its text). Changes nothing. Skipped off Windows, in a
    /// session with no display, and when the monitor doesn't answer DDC/CI (many ship with it off in their own menu).
    /// </summary>
    public static SmokeCheck ProbeMonitorDdc()
    {
        const string name = "monitor:ddc";
        if (!OperatingSystem.IsWindows())
        {
            return new SmokeCheck(name, true, "skipped: not Windows");
        }

        try
        {
            var system = new WindowsMonitorSystem();
            MonitorInfo? primary;
            try
            {
                var snapshot = system.Query();
                primary = snapshot.Active.FirstOrDefault(m => m.Primary && !m.Shared) ?? snapshot.Active.FirstOrDefault(m => !m.Shared);
            }
            catch (MonitorException)
            {
                return new SmokeCheck(name, true, "skipped: no display in this session");
            }

            if (primary is null)
            {
                return new SmokeCheck(name, true, "skipped: no monitor in this session");
            }

            int physical = system.PhysicalCount(primary.Target);
            byte input;
            try
            {
                input = system.CurrentInput(primary.Target);
            }
            catch (MonitorException e) when (e.Message is MonitorText.NoDdc or MonitorText.NoInputSwitch)
            {
                return new SmokeCheck(name, true, $"skipped: {primary.Name} doesn't answer DDC/CI ({physical} physical monitor(s))");
            }

            var caps = system.Capabilities(primary.Target);
            return new SmokeCheck(name, true, $"{primary.Name}: {physical} physical monitor(s), input {Mccs.Hex(input)}, capabilities {caps.Raw.Length} chars, {caps.Inputs.Count} input(s){(caps.Reported ? "" : " (not reported)")}");
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return new SmokeCheck(name, false, $"{e.GetType().Name}: {e.Message}");
        }
    }
}
