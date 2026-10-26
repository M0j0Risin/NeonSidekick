using NeonSidekick.Monitors;

namespace NeonSidekick.App;

public static partial class SmokeChecks
{
    /// <summary>
    /// <c>monitor:ccd</c> (2026-10-23, the monitors plan): the CCD imports in the published binary — the topology read whole
    /// (<c>QueryDisplayConfig</c>, the target names through <c>DisplayConfigGetDeviceInfo</c>, the GDI order through the
    /// <c>[UnmanagedCallersOnly]</c> callback) and an Extend only validated (<c>SDC_VALIDATE</c>: never applied; a virtual display's refusal
    /// is said, not failed). Changes nothing. Skipped off Windows and in a session with no display (a service: no active path, or access denied).
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

            var first = snapshot.Active[0];
            string read = $"{snapshot.Monitors.Count} monitor(s), {MonitorText.LayoutWord(snapshot.Layout)}; 1 is {first.Name} ({first.Connector}{(first.Mode is { } m ? ", " + MonitorText.Mode(m) : "")})";
            try
            {
                system.Layout(MonitorLayout.Extend, null, validateOnly: true);
            }
            catch (MonitorException e)
            {
                // A virtual display (2026-10-26, the first v0.5.2 release run: a GitHub runner's one Hyper-V monitor) refuses the validation
                // with a Windows code (0x1F there): SetDisplayConfig was bound and answered, which is what the probe proves.
                return new SmokeCheck(name, true, $"{read}; Extend not validated here ({e.Message})");
            }

            return new SmokeCheck(name, true, $"{read}; Extend validated");
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return new SmokeCheck(name, false, $"{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>
    /// <c>monitor:ddc</c> (2026-10-23): dxva2's DDC/CI imports in the published binary — the primary monitor's physical monitors counted, its
    /// current input and its capabilities read (the string's length reported, never its text). Changes nothing. Skipped off Windows, in a
    /// session with no display, on a virtual display with no physical monitor behind it (a CI runner's), and when the monitor doesn't answer
    /// DDC/CI (many ship with it off in their own menu).
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

            int physical;
            try
            {
                physical = system.PhysicalCount(primary.Target);
            }
            catch (MonitorException e) when (e.Message == MonitorText.NotSupported)
            {
                // A virtual display has no physical monitor behind it (2026-10-26, the first v0.5.2 release run: a GitHub runner's display
                // answered error 50): dxva2 was bound and answered.
                return new SmokeCheck(name, true, $"skipped: {primary.Name} has no physical monitor for DDC/CI ({e.Message})");
            }

            byte input;
            try
            {
                input = system.CurrentInput(primary.Target);
            }
            catch (MonitorException e) when (e.Message is MonitorText.NoDdc or MonitorText.NoInputSwitch or MonitorText.NotSupported)
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
