using NeonSidekick.Monitors;

namespace NeonSidekick.Tests.Fakes;

/// <summary>
/// Monitors for the tests (2026-10-23, the monitors plan): this PC's two by default — 1 LG ULTRAWIDE (primary, 3440×1440 at 160 Hz, on
/// DisplayPort-1, listing HDMI-1, HDMI-2, DisplayPort-1, DisplayPort-2) and 2 XG27UQDMS (3840×2160 at 60 Hz to its right, on DisplayPort-1,
/// listing DisplayPort-1, HDMI-1, HDMI-2). Inputs, capabilities and failures settable per monitor; layouts simulated (Extend side by side,
/// Duplicate on one source, Only one at the origin, Primary shifted); <see cref="DropsOn"/> inputs take a monitor off the desktop when
/// switched to; <see cref="Block"/> holds every call until set. Every change is logged in <see cref="Changes"/>, every call counted in
/// <see cref="Calls"/>. Thread-safe.
/// </summary>
public sealed class FakeMonitorSystem : IMonitorSystem
{
    public const string Lg = "A:4353";
    public const string Xg = "A:4355";

    private readonly object _gate = new();

    public sealed class Display
    {
        public required string Target { get; init; }
        public string? DevicePath { get; set; }
        public required string Name { get; set; }
        public string Connector { get; set; } = "DisplayPort";
        public bool Active { get; set; } = true;
        public string Source { get; set; } = "A:0";
        public string Gdi { get; set; } = @"\\.\DISPLAY1";
        public MonitorMode Mode { get; set; }
        public byte Input { get; set; } = 0x0F;
        public string? Caps { get; set; }
        public string? DdcFail { get; set; }
        public string? CapsFail { get; set; }
        public HashSet<byte> DropsOn { get; } = [];
        public HashSet<byte> Ignores { get; } = [];
    }

    public List<Display> Screens { get; } =
    [
        new()
        {
            Target = Lg, Name = "LG ULTRAWIDE", DevicePath = @"\\?\DISPLAY#GSM7768#5&1ceef1a8&1&UID4353#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}",
            Source = "A:0", Gdi = @"\\.\DISPLAY1", Mode = new MonitorMode(3440, 1440, 159.96, 0, 0),
            Caps = "(prot(monitor)type(lcd)model(WP65C)vcp(02 04 10 12 60(11 12 0F 10 ) AC)mccs_ver(2.1))",
        },
        new()
        {
            Target = Xg, Name = "XG27UQDMS", DevicePath = @"\\?\DISPLAY#AUSAA34#5&1ceef1a8&1&UID4355#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}",
            Source = "A:1", Gdi = @"\\.\DISPLAY2", Mode = new MonitorMode(3840, 2160, 60, 3440, 0),
            Caps = "(prot(monitor)type(LCD)model(XG27UQDMS)vcp(02 04 5F(0005) 60(0F 11 12) 72(5000 6400))mccs_ver(2.2))",
        },
    ];

    /// <summary>Every change, in order: <c>input A:4355 11</c>, <c>layout Only A:4355</c>, <c>validate Extend</c>.</summary>
    public List<string> Changes { get; } = [];

    /// <summary>Every call, in order: <c>query</c>, <c>read A:4355</c>, <c>caps A:4355</c>, <c>set A:4355 11</c>, <c>layout Extend</c>.</summary>
    public List<string> Calls { get; } = [];

    /// <summary>The next query throws this sentence (each query while set).</summary>
    public string? QueryFail { get; set; }

    /// <summary>A layout change throws this sentence.</summary>
    public string? LayoutFail { get; set; }

    /// <summary>A plain exception thrown from every call while set (not a <see cref="MonitorException"/>).</summary>
    public Exception? Crash { get; set; }

    /// <summary>The next <see cref="FailNext"/> DDC/CI calls throw <see cref="MonitorText.NoDdc"/>, then they work (the retry's case).</summary>
    public int FailNext { get; set; }

    /// <summary>Held calls wait on this; null for none.</summary>
    public ManualResetEventSlim? Block { get; set; }

    public Display Of(string target) => Screens.Single(s => s.Target == target);

    public int Count(string prefix)
    {
        lock (_gate)
        {
            return Calls.Count(c => c.StartsWith(prefix, StringComparison.Ordinal));
        }
    }

    public MonitorSnapshot Query()
    {
        Enter("query");
        lock (_gate)
        {
            if (QueryFail is { } fail)
            {
                throw new MonitorException(fail);
            }

            var targets = Screens.Select(s => new MonitorTarget(s.Target, s.DevicePath, s.Name, s.Connector, s.Active, s.Active ? s.Source : null,
                s.Active ? s.Gdi : null, s.Active ? s.Mode : null)).ToList();
            var order = Screens.Where(s => s.Active).OrderBy(s => s.Gdi, StringComparer.Ordinal).Select(s => s.Gdi).Distinct().ToList();
            return MonitorNumbering.Build(targets, order);
        }
    }

    public void Layout(MonitorLayout kind, string? target, bool validateOnly)
    {
        Enter("layout " + kind);
        lock (_gate)
        {
            if (LayoutFail is { } fail)
            {
                throw new MonitorException(fail);
            }

            if (validateOnly)
            {
                Changes.Add("validate " + kind);
                return;
            }

            Changes.Add("layout " + kind + (target is null ? "" : " " + target));
            switch (kind)
            {
                case MonitorLayout.Extend:
                    int x = 0;
                    for (int i = 0; i < Screens.Count; i++)
                    {
                        var s = Screens[i];
                        s.Active = true;
                        s.Source = "A:" + i;
                        s.Gdi = @"\\.\DISPLAY" + (i + 1);
                        s.Mode = s.Mode with { X = x, Y = 0 };
                        x += s.Mode.Width;
                    }

                    break;
                case MonitorLayout.Duplicate:
                    foreach (var s in Screens)
                    {
                        s.Active = true;
                        s.Source = "A:0";
                        s.Gdi = @"\\.\DISPLAY1";
                        s.Mode = s.Mode with { X = 0, Y = 0 };
                    }

                    break;
                case MonitorLayout.Only:
                    foreach (var s in Screens)
                    {
                        s.Active = s.Target == target;
                        if (s.Active)
                        {
                            s.Source = "A:0";
                            s.Gdi = @"\\.\DISPLAY1";
                            s.Mode = s.Mode with { X = 0, Y = 0 };
                        }
                    }

                    break;
                case MonitorLayout.Primary:
                    var chosen = Of(target!);
                    int dx = chosen.Mode.X, dy = chosen.Mode.Y;
                    foreach (var s in Screens.Where(s => s.Active))
                    {
                        s.Mode = s.Mode with { X = s.Mode.X - dx, Y = s.Mode.Y - dy };
                    }

                    break;
            }
        }
    }

    public MonitorCaps Capabilities(string target)
    {
        Enter("caps " + target);
        lock (_gate)
        {
            var s = Reach(target);
            if (s.CapsFail is { } fail)
            {
                throw new MonitorException(fail);
            }

            var (inputs, reported) = Mccs.Inputs(s.Caps);
            return new MonitorCaps(inputs, reported, s.Caps ?? "");
        }
    }

    public byte CurrentInput(string target)
    {
        Enter("read " + target);
        lock (_gate)
        {
            return Reach(target).Input;
        }
    }

    public void SetInput(string target, byte value)
    {
        Enter("set " + target + " " + value.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
        lock (_gate)
        {
            var s = Reach(target);
            Changes.Add("input " + target + " " + value.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            if (!s.Ignores.Contains(value))
            {
                s.Input = value;
            }

            if (s.DropsOn.Contains(value))
            {
                s.Active = false;
            }
        }
    }

    private void Enter(string call)
    {
        lock (_gate)
        {
            Calls.Add(call);
        }

        Block?.Wait(TimeSpan.FromSeconds(30));
        if (Crash is { } crash)
        {
            throw crash;
        }
    }

    // A DDC/CI call's checks, under the lock.
    private Display Reach(string target)
    {
        var s = Screens.FirstOrDefault(x => x.Target == target) ?? throw new MonitorException(MonitorText.NoSuchTarget);
        if (!s.Active)
        {
            throw new MonitorException(MonitorText.OffDesktop);
        }

        if (Screens.Count(x => x.Active && x.Source == s.Source) > 1)
        {
            throw new MonitorException(MonitorText.SharedSource);
        }

        if (FailNext > 0)
        {
            FailNext--;
            throw new MonitorException(MonitorText.NoDdc);
        }

        if (s.DdcFail is { } fail)
        {
            throw new MonitorException(fail);
        }

        return s;
    }
}
