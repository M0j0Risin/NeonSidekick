using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using NeonSidekick.Files;
using NeonSidekick.Screen;
using static NeonSidekick.Monitors.MonitorNative;

namespace NeonSidekick.Monitors;

/// <summary>
/// The monitors through the CCD API and dxva2's DDC/CI (2026-10-23, the monitors plan; the MonitorTrials <c>DisplaySwitcherTest</c>
/// moved over, its Duplicate, Only and Primary proven by the plan's spike the same day on this PC's LG ULTRAWIDE and XG27UQDMS).
/// The layout: <c>QueryDisplayConfig(QDC_ALL_PATHS)</c> read whole (retried on 122 when the topology moves between the two calls),
/// every change validated (<c>SDC_VALIDATE</c>) before it is applied — Extend and Duplicate as Win+P asks them, Only by the one path
/// <see cref="MonitorLayoutPlan.OnlyPath"/> picks (<c>SDC_TOPOLOGY_SUPPLIED</c>, which fails when Windows' display database has no entry for
/// that monitor alone, then the same path again with <c>SDC_USE_SUPPLIED_DISPLAY_CONFIG|SDC_ALLOW_CHANGES</c>, its best-mode logic: both
/// proven), Primary by the shifted source modes. The spike found every mode (3440×1440 at 159.96 Hz, scaling, HDR off) unchanged around
/// each, so the plain query is enough. DDC/CI: each call opens the physical monitors behind the target's GDI device, works, destroys them;
/// a duplicated source has several and no documented mapping to the targets (the spike: the shared device listed the XG27 first, the LG as
/// "Generic PnP Monitor"), so a shared target is refused with <see cref="MonitorText.SharedSource"/>. Nothing is keyed by GDI name: Windows
/// moves them (the XG27 brought back alone became <c>\\.\DISPLAY1</c>). A missing dxva2 (Server Core) is
/// <see cref="MonitorText.NoMonitorControl"/>; the layout still works there. Names from the monitor are scrubbed (<see cref="NameScrub"/>).
/// Excluded from coverage: the smoke's <c>monitor:ccd</c>/<c>monitor:ddc</c> and <c>--monitor-check</c> prove it.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class WindowsMonitorSystem : IMonitorSystem
{
    public MonitorSnapshot Query() => QueryCore();

    private static MonitorSnapshot QueryCore()
    {
        var (paths, modes) = Read(QdcAllPaths);
        var targets = new List<MonitorTarget>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in paths.Where(p => (p.Flags & PathActive) != 0))
        {
            if (seen.Add(TargetOf(path.TargetInfo)))
            {
                targets.Add(Target(path, modes, active: true));
            }
        }

        // QDC_ALL_PATHS lists every source/target pairing, so a target off the desktop shows once per source.
        foreach (var path in paths.Where(p => (p.Flags & PathActive) == 0 && p.TargetInfo.TargetAvailable != 0))
        {
            if (seen.Add(TargetOf(path.TargetInfo)))
            {
                targets.Add(Target(path, modes, active: false));
            }
        }

        return MonitorNumbering.Build(targets, GdiOrder().Select(m => m.Name).ToList());
    }

    public void Layout(MonitorLayout kind, string? target, bool validateOnly)
    {
        switch (kind)
        {
            case MonitorLayout.Extend:
                Apply(0, null, 0, null, SdcTopologyExtend | SdcPathPersistIfRequired, validateOnly);
                break;
            case MonitorLayout.Duplicate:
                Apply(0, null, 0, null, SdcTopologyClone | SdcPathPersistIfRequired, validateOnly);
                break;
            case MonitorLayout.Only:
                Only(target ?? throw new MonitorException(MonitorText.NoSuchTarget), validateOnly);
                break;
            case MonitorLayout.Primary:
                Primary(target ?? throw new MonitorException(MonitorText.NoSuchTarget), validateOnly);
                break;
        }
    }

    public MonitorCaps Capabilities(string target) => Ddc(target, handle =>
    {
        if (!GetCapabilitiesStringLength(handle, out uint length))
        {
            throw Last();
        }

        var buffer = new byte[Math.Max(1, length)];
        fixed (byte* p = buffer)
        {
            if (!CapabilitiesRequestAndCapabilitiesReply(handle, p, length))
            {
                throw Last();
            }
        }

        int end = Array.IndexOf(buffer, (byte)0);
        string raw = NameScrub.Display(Encoding.ASCII.GetString(buffer, 0, end >= 0 ? end : buffer.Length));
        var (inputs, reported) = Mccs.Inputs(raw);
        return new MonitorCaps(inputs, reported, raw);
    });

    /// <summary>The number of physical monitors behind the target's GDI device (the smoke's <c>monitor:ddc</c>; two or more while duplicated).</summary>
    public int PhysicalCount(string target)
    {
        var monitor = QueryCore().ByTarget(target) ?? throw new MonitorException(MonitorText.NoSuchTarget);
        IntPtr hmonitor = monitor.GdiName is null ? IntPtr.Zero
            : GdiOrder().FirstOrDefault(m => string.Equals(m.Name, monitor.GdiName, StringComparison.OrdinalIgnoreCase)).Handle;
        if (hmonitor == IntPtr.Zero)
        {
            throw new MonitorException(MonitorText.OffDesktop);
        }

        try
        {
            return GetNumberOfPhysicalMonitorsFromHMONITOR(hmonitor, out uint count) ? (int)count : throw Last();
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new MonitorException(MonitorText.NoMonitorControl, e);
        }
    }

    public byte CurrentInput(string target) => Ddc(target, handle =>
        GetVCPFeatureAndVCPFeatureReply(handle, Mccs.InputSource, null, out uint current, out _) ? (byte)(current & 0xFF) : throw Last());

    public void SetInput(string target, byte value) => Ddc(target, handle =>
        SetVCPFeature(handle, Mccs.InputSource, value) ? 0 : throw Last());

    // The system's handle for a target: its adapter LUID and target id, stable for the run.
    private static string TargetOf(in DisplayPathTargetInfo t) =>
        t.AdapterId.HighPart.ToString("X8", CultureInfo.InvariantCulture) + t.AdapterId.LowPart.ToString("X8", CultureInfo.InvariantCulture)
        + ":" + t.Id.ToString(CultureInfo.InvariantCulture);

    private static string SourceOf(in DisplayPathSourceInfo s) =>
        s.AdapterId.HighPart.ToString("X8", CultureInfo.InvariantCulture) + s.AdapterId.LowPart.ToString("X8", CultureInfo.InvariantCulture)
        + ":" + s.Id.ToString(CultureInfo.InvariantCulture);

    private static MonitorTarget Target(in DisplayPathInfo path, DisplayModeInfo[] modes, bool active)
    {
        var (name, devicePath) = TargetName(path.TargetInfo);
        string? gdi = null;
        MonitorMode? mode = null;
        if (active)
        {
            gdi = GdiName(path.SourceInfo);
            uint at = path.SourceInfo.ModeInfoIdx;
            if (at != PathModeIdxInvalid && at < modes.Length && modes[at].InfoType == ModeInfoTypeSource)
            {
                var source = modes[at].SourceMode;
                var r = path.TargetInfo.RefreshRate;
                double hz = r.Denominator == 0 ? 0 : (double)r.Numerator / r.Denominator;
                mode = new MonitorMode((int)source.Width, (int)source.Height, hz, source.Position.X, source.Position.Y);
            }
        }

        return new MonitorTarget(TargetOf(path.TargetInfo), devicePath.Length > 0 ? devicePath : null, NameScrub.Display(name),
            MonitorText.Connector(path.TargetInfo.OutputTechnology), active, active ? SourceOf(path.SourceInfo) : null, gdi, mode);
    }

    private static (DisplayPathInfo[] Paths, DisplayModeInfo[] Modes) Read(uint flags)
    {
        for (int attempt = 0; ; attempt++)
        {
            int result = GetDisplayConfigBufferSizes(flags, out uint pathCount, out uint modeCount);
            if (result != ErrorSuccess)
            {
                throw new MonitorException(MonitorErrors.Sentence(result));
            }

            var paths = new DisplayPathInfo[pathCount];
            var modes = new DisplayModeInfo[modeCount];
            fixed (DisplayPathInfo* p = paths)
            fixed (DisplayModeInfo* m = modes)
            {
                result = QueryDisplayConfig(flags, ref pathCount, p, ref modeCount, m, IntPtr.Zero);
            }

            // The topology can change between the two calls: again with fresh sizes, a few times.
            if (result == ErrorInsufficientBuffer && attempt < 5)
            {
                continue;
            }

            if (result != ErrorSuccess)
            {
                throw new MonitorException(MonitorErrors.Sentence(result));
            }

            return (paths[..(int)pathCount], modes[..(int)modeCount]);
        }
    }

    // Validates, then (unless only validating) applies; a refusal as its sentence.
    private static void Apply(uint pathCount, DisplayPathInfo* paths, uint modeCount, DisplayModeInfo* modes, uint flags, bool validateOnly)
    {
        int result = Set(pathCount, paths, modeCount, modes, flags, validateOnly);
        if (result != ErrorSuccess)
        {
            throw new MonitorException(MonitorErrors.Sentence(result));
        }
    }

    private static int Set(uint pathCount, DisplayPathInfo* paths, uint modeCount, DisplayModeInfo* modes, uint flags, bool validateOnly)
    {
        int result = SetDisplayConfig(pathCount, paths, modeCount, modes, flags | SdcValidate);
        return result != ErrorSuccess || validateOnly ? result : SetDisplayConfig(pathCount, paths, modeCount, modes, flags | SdcApply);
    }

    private static void Only(string target, bool validateOnly)
    {
        var (paths, _) = Read(QdcAllPaths);
        var plan = paths.Select((p, i) => new LayoutPath(i, TargetOf(p.TargetInfo), SourceOf(p.SourceInfo), (p.Flags & PathActive) != 0)).ToList();
        if (MonitorLayoutPlan.OnlyPath(plan, target) is not int index)
        {
            throw new MonitorException(MonitorText.NoSuchTarget);
        }

        var one = new[] { paths[index] };
        one[0].Flags |= PathActive;
        one[0].SourceInfo.ModeInfoIdx = PathModeIdxInvalid;
        one[0].TargetInfo.ModeInfoIdx = PathModeIdxInvalid;
        fixed (DisplayPathInfo* p = one)
        {
            // From the display database first; a monitor never shown alone has no entry there, so then Windows' best mode.
            if (Set(1, p, 0, null, SdcTopologySupplied | SdcAllowPathOrderChanges, validateOnly) == ErrorSuccess)
            {
                return;
            }

            Apply(1, p, 0, null, SdcUseSuppliedDisplayConfig | SdcSaveToDatabase | SdcAllowChanges, validateOnly);
        }
    }

    private static void Primary(string target, bool validateOnly)
    {
        var (paths, modes) = Read(QdcOnlyActivePaths);
        int at = Array.FindIndex(paths, p => TargetOf(p.TargetInfo) == target);
        uint chosen = at < 0 ? PathModeIdxInvalid : paths[at].SourceInfo.ModeInfoIdx;
        if (chosen == PathModeIdxInvalid || chosen >= modes.Length)
        {
            throw new MonitorException(MonitorText.OffDesktop);
        }

        var sources = modes.Select((m, i) => (m, i)).Where(x => x.m.InfoType == ModeInfoTypeSource)
            .Select(x => new LayoutSourceMode(x.i, x.m.SourceMode.Position.X, x.m.SourceMode.Position.Y)).ToList();
        foreach (var moved in MonitorLayoutPlan.Primary(sources, (int)chosen))
        {
            modes[moved.Index].SourceMode.Position = new DisplayPoint { X = moved.X, Y = moved.Y };
        }

        fixed (DisplayPathInfo* p = paths)
        fixed (DisplayModeInfo* m = modes)
        {
            Apply((uint)paths.Length, p, (uint)modes.Length, m, SdcUseSuppliedDisplayConfig | SdcSaveToDatabase | SdcAllowChanges, validateOnly);
        }
    }

    // Opens the one physical monitor behind the target's GDI device, runs the body, destroys every handle.
    private static T Ddc<T>(string target, Func<IntPtr, T> body)
    {
        var snapshot = QueryCore();
        var monitor = snapshot.ByTarget(target) ?? throw new MonitorException(MonitorText.NoSuchTarget);
        if (!monitor.Active || monitor.GdiName is null)
        {
            throw new MonitorException(MonitorText.OffDesktop);
        }

        if (monitor.Shared)
        {
            throw new MonitorException(MonitorText.SharedSource);
        }

        IntPtr hmonitor = GdiOrder().FirstOrDefault(m => string.Equals(m.Name, monitor.GdiName, StringComparison.OrdinalIgnoreCase)).Handle;
        if (hmonitor == IntPtr.Zero)
        {
            throw new MonitorException(MonitorText.OffDesktop);
        }

        try
        {
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hmonitor, out uint count))
            {
                throw Last();
            }

            if (count == 0)
            {
                throw new MonitorException(MonitorText.NoDdc);
            }

            var physical = new PhysicalMonitor[count];
            fixed (PhysicalMonitor* p = physical)
            {
                if (!GetPhysicalMonitorsFromHMONITOR(hmonitor, count, p))
                {
                    throw Last();
                }
            }

            try
            {
                return body(physical[0].Handle);
            }
            finally
            {
                foreach (var one in physical)
                {
                    _ = DestroyPhysicalMonitor(one.Handle);
                }
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new MonitorException(MonitorText.NoMonitorControl, e);
        }
    }

    private static MonitorException Last() => new(MonitorErrors.Sentence(Marshal.GetLastPInvokeError(), Marshal.GetLastPInvokeErrorMessage()));

    private static (string Name, string DevicePath) TargetName(in DisplayPathTargetInfo target)
    {
        var request = new DisplayTargetDeviceName();
        request.Header.Type = DeviceInfoGetTargetName;
        request.Header.Size = (uint)sizeof(DisplayTargetDeviceName);
        request.Header.AdapterId = target.AdapterId;
        request.Header.Id = target.Id;
        if (DisplayConfigGetDeviceInfo(&request.Header) != ErrorSuccess)
        {
            return ("", "");
        }

        return (Text(request.MonitorFriendlyDeviceName, 64), Text(request.MonitorDevicePath, 128));
    }

    private static string? GdiName(in DisplayPathSourceInfo source)
    {
        var request = new DisplaySourceDeviceName();
        request.Header.Type = DeviceInfoGetSourceName;
        request.Header.Size = (uint)sizeof(DisplaySourceDeviceName);
        request.Header.AdapterId = source.AdapterId;
        request.Header.Id = source.Id;
        return DisplayConfigGetDeviceInfo(&request.Header) == ErrorSuccess ? Text(request.ViewGdiDeviceName, 32) : null;
    }

    private static string Text(char* buffer, int length)
    {
        var span = new ReadOnlySpan<char>(buffer, length);
        int end = span.IndexOf('\0');
        return new string(end >= 0 ? span[..end] : span);
    }

    // The HMONITORs in EnumDisplayMonitors' order (the screen capture's numbers) with their GDI names.
    private static List<(IntPtr Handle, string Name)> GdiOrder()
    {
        var list = new List<(IntPtr, string)>();
        var handle = GCHandle.Alloc(list);
        try
        {
            _ = ScreenNative.EnumDisplayMonitors(IntPtr.Zero, null, &OnMonitor, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }

        return list;
    }

    [UnmanagedCallersOnly]
    private static int OnMonitor(IntPtr monitor, IntPtr dc, ScreenNative.Rect* clip, IntPtr data)
    {
        var list = (List<(IntPtr, string)>)GCHandle.FromIntPtr(data).Target!;
        var info = new ScreenNative.MonitorInfoEx { cbSize = (uint)sizeof(ScreenNative.MonitorInfoEx) };
        if (ScreenNative.GetMonitorInfo(monitor, &info))
        {
            list.Add((monitor, new string(info.szDevice).TrimEnd('\0')));
        }

        return 1;
    }
}
