using System.Globalization;
using System.Runtime.Versioning;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.Monitors;

/// <summary>
/// DDC/CI to a monitor off the desktop through NVIDIA's driver (2026-10-27, the NVIDIA plan; the user's ask: "Is there no way to get the list
/// without switching to it first?"): dxva2 opens a monitor only through its desktop handle, which a monitor off the desktop has none of, while
/// NVIDIA's I2C reaches the cable itself. The spike read the off-desktop XG27UQDMS's input in 62 ms and its capabilities in 2.7 s, and switched
/// it, on this PC's RTX 5090. Only <see cref="WindowsMonitorSystem"/> calls it, for a monitor that isn't active, inside <see cref="MonitorCalls"/>
/// (one call at a time, the cap, the one retry on <see cref="MonitorText.NoDdc"/>). Every call finds the monitor afresh (<see cref="Find"/>:
/// GPUs, displays, outputs, EDIDs and adapters; a replugged cable or a driver reset changes the handles, and a stale one would aim at the wrong
/// output) and holds <see cref="Bus"/> across its whole exchange, so two calls never interleave on one cable even when <see cref="MonitorCalls"/>
/// let a late one run on past its cap; the lock is the process's (every <see cref="WindowsMonitorSystem"/> shares it). Another program on the
/// same bus can still land between a request and its reply: the reply's checks catch it and the retry follows, as with dxva2. Not reached (no
/// library, no NVIDIA GPU, a failing status, no single match) throws <see cref="MonitorText.OffDesktop"/>, today's sentence, its reason logged
/// at Debug; a bad or missing reply throws <see cref="MonitorText.NoDdc"/>. It sends only <see cref="DdcPacket"/>'s three requests. Excluded
/// from coverage: the smoke's <c>monitor:nvapi</c> and the live fact prove it; the pure parts are <see cref="DdcPacket"/> and
/// <see cref="NvidiaDisplays"/>.
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe class NvidiaDdc
{
    /// <summary>The wait after a request before its reply is read (the spike's).</summary>
    private static readonly TimeSpan RequestWait = TimeSpan.FromMilliseconds(50);

    /// <summary>The wait before a capabilities fragment is read.</summary>
    private static readonly TimeSpan FragmentWait = TimeSpan.FromMilliseconds(60);

    /// <summary><c>NVAPI_I2C_SPEED_DEPRECATED</c>: the speed is the driver's default.</summary>
    private const uint SpeedDefault = 0xFFFF;

    private static readonly object Bus = new();

    /// <summary>
    /// How long a call waits for <see cref="Bus"/>: a second inside <see cref="MonitorCalls.Cap"/> (2026-10-27, the review). A call queued behind
    /// one that ran past its cap gives up as <see cref="MonitorText.NoAnswer"/> before its own cap, never runs on later: a switch its caller
    /// was told didn't answer is never applied after all.
    /// </summary>
    private static readonly TimeSpan BusWait = MonitorCalls.Cap - TimeSpan.FromSeconds(1);

    /// <summary><paramref name="monitor"/>'s input now (VCP 0x60's value).</summary>
    public static byte CurrentInput(MonitorSnapshot snapshot, MonitorInfo monitor) => OnBus(() =>
    {
        var display = Find(snapshot, monitor);
        return Exchange(display, DdcPacket.GetInput(), DdcPacket.InputReplyLength, RequestWait) is { } reply && DdcPacket.ReadInput(reply) is { } current
            ? current
            : throw new MonitorException(MonitorText.NoDdc);
    });

    /// <summary><paramref name="monitor"/>'s capabilities, fragment by fragment.</summary>
    public static MonitorCaps Capabilities(MonitorSnapshot snapshot, MonitorInfo monitor) => OnBus(() =>
    {
        var display = Find(snapshot, monitor);
        return DdcPacket.ReadCapabilities(offset => Exchange(display, DdcPacket.Capabilities(offset), DdcPacket.FragmentReadLength, FragmentWait))
            ?? throw new MonitorException(MonitorText.NoDdc);
    });

    /// <summary>Sends Set VCP 0x60 = <paramref name="code"/>; a Set has no reply (the caller reads the input back after its settle).</summary>
    public static void SetInput(MonitorSnapshot snapshot, MonitorInfo monitor, byte code) => OnBus(() =>
        Write(Find(snapshot, monitor), DdcPacket.SetInput(code)) ? true : throw new MonitorException(MonitorText.NoDdc));

    /// <summary><paramref name="body"/> holding <see cref="Bus"/>, taken within <see cref="BusWait"/>, else <see cref="MonitorText.NoAnswer"/>.</summary>
    private static T OnBus<T>(Func<T> body)
    {
        if (!System.Threading.Monitor.TryEnter(Bus, BusWait))
        {
            DiagnosticLog.Debug("Monitors", "the NVIDIA bus stayed busy past the wait; the call gave up");
            throw new MonitorException(MonitorText.NoAnswer);
        }

        try
        {
            return body();
        }
        finally
        {
            System.Threading.Monitor.Exit(Bus);
        }
    }

    /// <summary>The NVIDIA display behind <paramref name="monitor"/>, found afresh; <see cref="MonitorText.OffDesktop"/> when not reached.</summary>
    private static NvidiaDisplay Find(MonitorSnapshot snapshot, MonitorInfo monitor)
    {
        int initialized = NvapiNative.Initialize();
        if (initialized != NvapiNative.Ok)
        {
            throw NotReached(monitor, "NVIDIA's driver API answered " + Status(initialized));
        }

        var (status, displays) = List();
        if (displays.Count == 0)
        {
            throw NotReached(monitor, status != NvapiNative.Ok ? "listing NVIDIA's displays answered " + Status(status) : "no display connected to an NVIDIA GPU");
        }

        return NvidiaDisplays.Match(displays, snapshot, monitor)
            ?? throw NotReached(monitor, Status(displays.Count(d => !d.Active)) + " NVIDIA display(s) off the desktop, none matched alone");
    }

    /// <summary>
    /// Every connected display on every NVIDIA GPU, with its EDID's PnP id and its adapter (the smoke lists them too). A GPU or display whose
    /// own call fails is passed over (logged at Debug), and one whose EDID won't read is listed with no PnP id, which never matches
    /// (2026-10-27, the review: one failing display hid every other); the status is the first failure's only when nothing was listed, else
    /// <see cref="NvapiNative.Ok"/>. It initializes the API itself, and answers <see cref="NvapiNative.Initialize"/>'s failure as its status:
    /// its function pointers are null until then (2026-10-27: the live test called it first and the process died on 0xC0000005).
    /// </summary>
    public static (int Status, IReadOnlyList<NvidiaDisplay> Displays) List()
    {
        var displays = new List<NvidiaDisplay>();
        int initialized = NvapiNative.Initialize();
        if (initialized != NvapiNative.Ok)
        {
            return (initialized, displays);
        }

        nint* gpus = stackalloc nint[NvapiNative.MaxGpus];
        uint gpuCount = 0;
        int status = NvapiNative.EnumPhysicalGpus(gpus, &gpuCount);
        if (status != NvapiNative.Ok)
        {
            return (status, displays);
        }

        int failure = NvapiNative.Ok;
        for (int g = 0; g < Math.Min((int)gpuCount, NvapiNative.MaxGpus); g++)
        {
            nint logical;
            status = NvapiNative.GetLogicalGpuFromPhysicalGpu(gpus[g], &logical);
            if (Failed(status, "a GPU's logical GPU", ref failure))
            {
                continue;
            }

            long luid = 0;
            var data = new NvLogicalGpuData { Version = NvapiNative.LogicalGpuVersion, OsAdapterId = &luid };
            status = NvapiNative.GetLogicalGpuInfo(logical, &data);
            if (Failed(status, "a GPU's adapter", ref failure))
            {
                continue;
            }

            (status, var ids) = ConnectedIds(gpus[g]);
            if (Failed(status, "a GPU's connected displays", ref failure))
            {
                continue;
            }

            foreach (var id in ids)
            {
                nint gpu;
                uint output;
                status = NvapiNative.GetGpuAndOutputIdFromDisplayId(id.DisplayId, &gpu, &output);
                if (Failed(status, "a display's output", ref failure))
                {
                    continue;
                }

                var edid = new NvEdid { Version = NvapiNative.EdidVersion };
                status = NvapiNative.GetEdid(gpu, output, &edid);
                string? pnp = Failed(status, "a display's EDID", ref failure) ? null : NvidiaDisplays.PnpId(new ReadOnlySpan<byte>(edid.Data, 256));
                displays.Add(new NvidiaDisplay(pnp, NvidiaDisplays.Luid(luid), (id.Flags & NvapiNative.FlagActive) != 0, gpu, output));
            }
        }

        return (displays.Count == 0 ? failure : NvapiNative.Ok, displays);
    }

    // Whether status failed: logged at Debug with what was asked, and kept in failure when it is the first.
    private static bool Failed(int status, string what, ref int failure)
    {
        if (status == NvapiNative.Ok)
        {
            return false;
        }

        DiagnosticLog.Debug("Monitors", "NVIDIA's " + what + " answered " + Status(status) + "; passed over");
        if (failure == NvapiNative.Ok)
        {
            failure = status;
        }

        return true;
    }

    /// <summary>
    /// A display's input read once with no retry (the smoke's <c>monitor:nvapi</c>): its status (<see cref="NvapiNative.Ok"/> with the input,
    /// else the I2C call's), or <see cref="NvapiNative.Ok"/> with null when the bus answered but the reply didn't read.
    /// </summary>
    public static (int Status, byte? Input) ReadOnce(NvidiaDisplay display)
    {
        lock (Bus)
        {
            int status = WriteStatus(display, DdcPacket.GetInput());
            if (status != NvapiNative.Ok)
            {
                return (status, null);
            }

            Thread.Sleep(RequestWait);
            var reply = new byte[DdcPacket.InputReplyLength];
            status = ReadStatus(display, reply);
            return (status, status == NvapiNative.Ok ? DdcPacket.ReadInput(reply) : null);
        }
    }

    // A GPU's connected displays, counted then filled; counted again when a display came between the two (a few times).
    private static (int Status, NvDisplayIds[] Ids) ConnectedIds(nint gpu)
    {
        for (int attempt = 0; ; attempt++)
        {
            uint count = 0;
            int status = NvapiNative.GetConnectedDisplayIds(gpu, null, &count, NvapiNative.ConnectedUncached);
            if (status != NvapiNative.Ok || count == 0)
            {
                return (status, []);
            }

            var ids = new NvDisplayIds[count];
            for (int i = 0; i < ids.Length; i++)
            {
                ids[i].Version = NvapiNative.DisplayIdsVersion;
            }

            fixed (NvDisplayIds* p = ids)
            {
                status = NvapiNative.GetConnectedDisplayIds(gpu, p, &count, NvapiNative.ConnectedUncached);
            }

            if (status == NvapiNative.InsufficientBuffer && attempt < 5)
            {
                continue;
            }

            return (status, status == NvapiNative.Ok ? ids[..(int)Math.Min(count, (uint)ids.Length)] : []);
        }
    }

    // A request, its wait, and a reply of replyLength bytes; null when either I2C call failed (logged).
    private static byte[]? Exchange(NvidiaDisplay display, byte[] request, int replyLength, TimeSpan wait)
    {
        if (!Write(display, request))
        {
            return null;
        }

        Thread.Sleep(wait);
        var reply = new byte[replyLength];
        int status = ReadStatus(display, reply);
        if (status != NvapiNative.Ok)
        {
            DiagnosticLog.Debug("Monitors", "NVIDIA I2C read answered " + Status(status));
            return null;
        }

        return reply;
    }

    private static bool Write(NvidiaDisplay display, byte[] request)
    {
        int status = WriteStatus(display, request);
        if (status != NvapiNative.Ok)
        {
            DiagnosticLog.Debug("Monitors", "NVIDIA I2C write answered " + Status(status));
        }

        return status == NvapiNative.Ok;
    }

    // Host → display: the request's bytes after the 0x51 register byte, at the display's address on the DDC port.
    private static int WriteStatus(NvidiaDisplay display, byte[] request)
    {
        byte register = DdcPacket.HostAddress;
        fixed (byte* data = request)
        {
            var info = new NvI2cInfo
            {
                Version = NvapiNative.I2cVersion, DisplayMask = display.Output, IsDdcPort = 1, DevAddress = DdcPacket.DisplayAddress,
                RegAddress = &register, RegAddrSize = 1, Data = data, Size = (uint)request.Length, Speed = SpeedDefault,
            };
            return NvapiNative.I2cWrite(display.Gpu, &info);
        }
    }

    private static int ReadStatus(NvidiaDisplay display, byte[] reply)
    {
        fixed (byte* data = reply)
        {
            var info = new NvI2cInfo
            {
                Version = NvapiNative.I2cVersion, DisplayMask = display.Output, IsDdcPort = 1, DevAddress = DdcPacket.DisplayAddress,
                Data = data, Size = (uint)reply.Length, Speed = SpeedDefault,
            };
            return NvapiNative.I2cRead(display.Gpu, &info);
        }
    }

    private static MonitorException NotReached(MonitorInfo monitor, string why)
    {
        DiagnosticLog.Debug("Monitors", monitor.Label + " off the desktop, not reached: " + why);
        return new MonitorException(MonitorText.OffDesktop);
    }

    private static string Status(int status) => status.ToString(CultureInfo.InvariantCulture);
}
