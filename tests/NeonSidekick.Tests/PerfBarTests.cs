using NeonSidekick.App;
using NeonSidekick.Perf;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.Tests;

/// <summary>The performance bar (2026-09-29): its setting's words, its arithmetic, its sampler and its four looks.</summary>
public class PerfBarTests
{
    // ── The setting ─────────────────────────────────────────────────────────

    /// <summary>The four meters of the bar before the checklist (2026-09-30), what the looks' tests draw.</summary>
    private static readonly IReadOnlySet<string> Four = PerfBarItems.Defaults.ToHashSet(StringComparer.Ordinal);

    private static IReadOnlySet<string> Only(params string[] ids) => ids.ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void TheLooks_ArePinned_LedByDefault()
    {
        // The look alone since 2026-09-30: whether the bar shows is the checklist's (PerfBarItems).
        Assert.Equal(["text", "gauge", "spark", "led"], PerfBarMode.Names);
        Assert.Equal(["off", "text", "gauge", "spark", "led"], PerfBarMode.Words);
        Assert.Equal("led", PerfBarMode.Default);   // text until 2026-10-02 (the user's ask)
        Assert.Equal("led", new AppSettingsData().PerformanceBarLook);
        foreach (string name in PerfBarMode.Names)
        {
            Assert.True(PerfBarMode.TryParse(" " + name.ToUpperInvariant() + " ", out var style));
            Assert.Equal(name, PerfBarMode.Name(style));
            Assert.NotEqual("", PerfBarMode.Describe(name));
        }

        Assert.Equal("hide the performance bar", PerfBarMode.Describe("off"));
        Assert.False(PerfBarMode.TryParse("off", out _));
        Assert.False(PerfBarMode.TryParse("bars", out var none));
        Assert.Equal(PerfBarStyle.Led, none);   // the default's, as Parse
        Assert.Equal(PerfBarStyle.Led, PerfBarMode.Parse("led"));
        Assert.Equal(PerfBarStyle.Led, PerfBarMode.Parse("bars"));
        Assert.Equal("", PerfBarMode.Describe("bars"));
    }

    /// <summary>The looks' buttons on the checklist (2026-10-03, the user's picks): a glyph left of each name; the saved words stay bare.</summary>
    [Fact]
    public void TheLooksButtons_CarryAGlyph_TheSavedWordsStayBare()
    {
        Assert.Equal(["≡ text", "◔ gauge", "▁ spark", "● led"], PerfBarMode.Names.Select(PerfBarMode.ButtonTitle));
        Assert.Equal("bars", PerfBarMode.ButtonTitle("bars"));
        Assert.Equal(["≡ text", "◔ gauge", "▁ spark", "● led"], SettingsMenu.PerfBarButtons(PerfBarStyle.Led).Skip(3).Select(b => b.Title));
        Assert.Equal(["● led"], SettingsMenu.PerfBarButtons(PerfBarStyle.Led).Where(b => b.On).Select(b => b.Title));
        Assert.Equal(['t', 'g', 's', 'l'], SettingsMenu.PerfBarButtons(PerfBarStyle.Led).Skip(3).Select(b => b.Key!.Value));   // the keys stay the names' first letters
    }

    [Fact]
    public void TheMeters_ArePinned_TheFourByDefault()
    {
        Assert.Equal(["cpu", "ram", "gpu", "vram", "net", "netdown", "netup", "proc"], PerfBarItems.Names);   // PROC last, 2026-10-05
        Assert.Equal(["CPU", "RAM", "GPU", OperatingSystem.IsMacOS() ? "GMEM" : "VRAM", "NET", "NET↓", "NET↑", "PROC"], PerfBarItems.Names.Select(PerfBarItems.Title));   // GMEM on a Mac, 2026-10-07
        Assert.Equal(["cpu", "ram", "gpu", "vram"], PerfBarItems.Defaults);
        Assert.Null(new AppSettingsData().PerformanceBarItems);
        Assert.Equal(PerfBarItems.Defaults, PerfBarItems.Names.Where(PerfBarItems.Resolve(null).Contains));   // null: the four (2026-10-02; none before)
        Assert.Empty(PerfBarItems.Resolve([]));                                                               // empty: no bar
        var resolved = PerfBarItems.Resolve([" NETUP ", "Ram", "bogus"]);
        Assert.Equal(["ram", "netup"], PerfBarItems.Names.Where(resolved.Contains));
        Assert.Equal(["cpu"], PerfBarItems.Names.Where(PerfBarItems.Resolve(["cpu", null!]).Contains));   // a hand-edited null: skipped
        Assert.Empty(PerfBarItems.Save(Only()));   // none saves as the empty list, never null (the four since 2026-10-02)
        Assert.Equal(PerfBarItems.Defaults, PerfBarItems.Save(Only("vram", "gpu", "ram", "cpu")));   // the four as the list
        Assert.Equal(["cpu", "netdown"], PerfBarItems.Save(Only("netdown", "cpu")));   // the bar's order
        Assert.Equal("off", PerfBarItems.Value([], "gauge"));
        Assert.Equal("CPU, RAM, GPU, " + Perf.PerfText.VramLabel + " · gauge", PerfBarItems.Value(null, "gauge"));
        Assert.Equal("CPU, RAM, NET↓ · gauge", PerfBarItems.Value(["netdown", "ram", "cpu"], "gauge"));
        Assert.Equal("all · led", PerfBarItems.Value([.. PerfBarItems.Names], "bars"));   // an unknown look reads as the default, led since 2026-10-02
        Assert.Equal("[[x]] CPU     " + Theme.DimMarkup("processor load"), PerfBarItems.Label("cpu", true));   // markup: the brackets escaped
        Assert.StartsWith("[[ ]] NET↑    ", PerfBarItems.Label("netup", false), StringComparison.Ordinal);
        Assert.Equal("[[ ]] PROC    " + Theme.DimMarkup("background processes"), PerfBarItems.Label("proc", false));
        Assert.All(PerfBarItems.Names, id => Assert.NotEqual("", PerfBarItems.Describe(id)));
        var copy = AppSettings.Copy(new AppSettingsData { PerformanceBarItems = ["cpu"], PerformanceBarLastItems = ["gpu"], PerformanceBarLook = "spark" });
        Assert.Equal(["cpu"], copy.PerformanceBarItems);
        Assert.Equal(["gpu"], copy.PerformanceBarLastItems);
        Assert.Equal("spark", copy.PerformanceBarLook);
    }

    [Fact]
    public void Perf_Bare_HidesKeepingTheMeters_ThenBringsThemBack()
    {
        var hidden = PerfBarMode.Toggle("", ["cpu", "net"], null, "gauge")!;
        Assert.Empty(hidden.Items!);   // the empty list, no bar (null is the four since 2026-10-02)
        Assert.Equal(["cpu", "net"], hidden.LastItems);
        Assert.Equal("gauge", hidden.Look);

        var shown = PerfBarMode.Toggle(" ", hidden.Items, hidden.LastItems, hidden.Look)!;
        Assert.Equal(["cpu", "net"], shown.Items);
        Assert.Equal("gauge", shown.Look);

        // Never picked: the four show (2026-10-02, the user's ask), so a bare /perfbar hides them and keeps them to come back.
        var never = PerfBarMode.Toggle("", null, null, "text")!;
        Assert.Empty(never.Items!);
        Assert.Equal(["cpu", "ram", "gpu", "vram"], never.LastItems);

        // Hidden with nothing to bring back: the bar as it was before the checklist; a hand-edited look reads as the default (led since 2026-10-02).
        var first = PerfBarMode.Toggle("", [], null, "bogus")!;
        Assert.Equal(["cpu", "ram", "gpu", "vram"], first.Items);
        Assert.Equal("led", first.Look);
    }

    [Fact]
    public void Perf_ALook_SetsItAndShowsTheBar_OffHides_AnythingElseIsTheUsage()
    {
        var led = PerfBarMode.Toggle(" LED ", [], ["gpu"], "text")!;
        Assert.Equal(["gpu"], led.Items);
        Assert.Equal("led", led.Look);
        var spark = PerfBarMode.Toggle("spark", ["ram"], null, "gauge")!;
        Assert.Equal(["ram"], spark.Items);   // shown already: its meters kept
        Assert.Equal("spark", spark.Look);
        var off = PerfBarMode.Toggle("OFF", ["ram"], null, "gauge")!;
        Assert.Empty(off.Items!);
        Assert.Equal(["ram"], off.LastItems);
        Assert.Equal("gauge", off.Look);
        var stillOff = PerfBarMode.Toggle("off", [], ["vram"], "gauge")!;
        Assert.Empty(stillOff.Items!);
        Assert.Equal(["vram"], stillOff.LastItems);   // hidden already: the last meters kept
        Assert.Null(PerfBarMode.Toggle("bogus", null, null, "text"));
    }

    [Fact]
    public void Perf_Wording_IsPinned()
    {
        Assert.Equal("📈", PerfText.Glyph);
        Assert.Equal("(📈 performance bar on: gauge)", PerfText.BarNotice("gauge"));
        Assert.Equal("(📈 performance bar off)", PerfText.BarNotice("off"));
        Assert.Equal("/perfbar takes off, text, gauge, spark or led, or nothing to toggle.", PerfText.UsageError);
    }

    // ── The network (2026-09-30) ────────────────────────────────────────────

    [Fact]
    public void Rate_IsBitsASecond_AResetCounterReadsZero()
    {
        Assert.Equal(8_000, PerfMath.Rate(1_000, 2_000, TimeSpan.FromSeconds(1)));
        Assert.Equal(4_000, PerfMath.Rate(1_000, 2_000, TimeSpan.FromSeconds(2)));
        Assert.Equal(0, PerfMath.Rate(5_000, 100, TimeSpan.FromSeconds(1)));   // the adapter reset
        Assert.Null(PerfMath.Rate(0, 100, TimeSpan.Zero));
        Assert.Equal(50, PerfMath.LinkPercent(50e6, 100e6));
        Assert.Equal(100, PerfMath.LinkPercent(300e6, 100e6));
        Assert.Null(PerfMath.LinkPercent(1e6, 0));
        Assert.Null(PerfMath.LinkPercent(null, 1e9));
        Assert.Equal(40, PerfMath.NetPercent(10e6, 40e6, 100e6));   // the busier direction
        Assert.Null(PerfMath.NetPercent(null, null, 100e6));
        Assert.Null(PerfMath.NetPercent(10e6, 1e6, null));
    }

    // ── The Mac's readings (2026-10-07) ────────────────────────────────────

    /// <summary>
    /// Mach's CPU ticks: busy is user + system + nice over all four states, and each 32-bit counter's delta is taken modulo
    /// 2³², so a counter that wrapped between two readings is a delta, not a gap (Windows' <see cref="PerfMath.CpuPercent"/>
    /// reads a 64-bit counter going back as no reading).
    /// </summary>
    [Fact]
    public void MachCpu_IsBusyOverAllFourStates_AcrossAWrap()
    {
        Assert.Equal(25, PerfMath.MachCpuPercent(100, 50, 1_000, 0, 120, 60, 1_090, 0));          // 30 busy of 120
        Assert.Equal(50, PerfMath.MachCpuPercent(0, 0, 0, 0, 10, 0, 20, 10));                     // nice counts as busy
        Assert.Equal(100, PerfMath.MachCpuPercent(0, 0, 0, 0, 40, 0, 0, 0));
        Assert.Null(PerfMath.MachCpuPercent(5, 5, 5, 5, 5, 5, 5, 5));                             // no tick passed
        Assert.Equal(10, PerfMath.MachCpuPercent(uint.MaxValue - 4, 0, uint.MaxValue - 44, 0, 5, 0, 45, 0));   // both wrapped: 10 busy of 100
        Assert.Equal(10, PerfMath.MachCpuPercent(100, 0, uint.MaxValue - 9, 0, 110, 0, 80, 0));   // idle alone wrapped: 90 idle
    }

    /// <summary>
    /// Activity Monitor's Memory Used: app memory (anonymous less purgeable) + wired + compressed, in pages of the machine's size —
    /// the figures an M4 with 16 GiB gave on 2026-10-07 (vm_stat: 304707 anonymous, 4 purgeable, 166937 wired, 430249 in the
    /// compressor; 16 KiB pages) make 14.78 GB, 86 %.
    /// </summary>
    [Fact]
    public void MacMemoryUsed_IsAppPlusWiredPlusCompressed()
    {
        double used = PerfMath.MacMemoryUsedBytes(304_707, 4, 166_937, 430_249, 16_384);
        Assert.Equal((304_703.0 + 166_937 + 430_249) * 16_384, used);
        Assert.Equal(86, Math.Round(PerfMath.Percent(used, 17_179_869_184)!.Value));
        Assert.Equal(16_384 * 2, PerfMath.MacMemoryUsedBytes(1, 9, 1, 1, 16_384));   // more purgeable than anonymous: no app memory, never less
        Assert.Equal(4_096 * 6, PerfMath.MacMemoryUsedBytes(3, 1, 2, 2, 4_096));     // an Intel Mac's 4 KiB pages
    }

    /// <summary>
    /// The 64-bit byte counts in a packed <c>ifmibdata</c> (received at 116, sent at 124, little-endian), past the 4 GiB a
    /// 32-bit counter wraps at; a buffer cut short reads nothing.
    /// </summary>
    [Fact]
    public void IfMibBytes_ReadsTheSixtyFourBitCounts()
    {
        var data = new byte[180];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(PerfMath.IfMibReceivedAt), 90_729_830_043);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(PerfMath.IfMibSentAt), 9_342_673_269);
        Assert.Equal((90_729_830_043L, 9_342_673_269L), PerfMath.IfMibBytes(data));
        Assert.Equal((116, 124), (PerfMath.IfMibReceivedAt, PerfMath.IfMibSentAt));
        Assert.Null(PerfMath.IfMibBytes(data.AsSpan(0, 131)));
        Assert.NotNull(PerfMath.IfMibBytes(data.AsSpan(0, 132)));
    }

    /// <summary>The Mac's own source (2026-10-07): the factory picks it on a Mac, and a sample reads every meter but the first CPU.</summary>
    [MacFact]
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    public void MacSource_IsTheDefault_AndReadsTheMachine()
    {
        using var source = PerfSources.CreateDefault();
        Assert.IsType<MacPerfSource>(source);
        var first = source.Sample(PerfReads.All);
        Assert.Null(first.Cpu);                                         // no previous reading yet
        Assert.InRange(first.Ram!.Value, 1, 100);
        Thread.Sleep(50);
        var second = source.Sample(PerfReads.Cpu | PerfReads.Ram);
        Assert.InRange(second.Cpu!.Value, 0, 100);
        Assert.Null(second.Gpu);                                        // not asked for
        Assert.Null(second.NetDown);
    }

    [Theory]
    [InlineData(0, "   0K")]
    [InlineData(850_000, " 850K")]
    [InlineData(12_400_000, "12.4M")]
    [InlineData(99_960_000, " 100M")]
    [InlineData(999_400, " 999K")]
    [InlineData(999_700, " 1.0M")]          // rounds to 1000K: the next unit's
    [InlineData(999_600_000, " 1.0G")]
    [InlineData(150_000_000, " 150M")]
    [InlineData(1_200_000_000, " 1.2G")]
    public void TheRate_IsFiveCellsWide(double bits, string shown)
    {
        Assert.Equal(shown, PerfText.Rate(bits));
    }

    private sealed class ScriptedCounters(params IReadOnlyList<NetworkAdapterReading>[] readings) : INetworkCounters
    {
        private int _next;

        public IReadOnlyList<NetworkAdapterReading> Read() => readings[Math.Min(_next++, readings.Length - 1)];
    }

    [Fact]
    public void NetworkMeter_RatesOverTheAdaptersInBothReadings()
    {
        var clock = TimeSpan.Zero;
        var counters = new ScriptedCounters(
            [new("eth", 1_000, 500, 1_000_000_000)],
            [new("eth", 126_000, 1_750, 1_000_000_000), new("wifi", 9_000_000, 9_000_000, 300_000_000)],   // wifi joins: no spike
            []);
        var meter = new NetworkMeter(counters, () => clock);

        Assert.Null(meter.Sample());   // the first reading: no rate yet
        clock = TimeSpan.FromSeconds(1);
        var rates = meter.Sample()!.Value;
        Assert.Equal((1_000_000, 10_000, 1_000_000_000), (rates.Down, rates.Up, rates.Link));
        clock = TimeSpan.FromSeconds(2);
        Assert.Null(meter.Sample());   // no adapter left
    }

    [Fact]
    public void NetworkMeter_ShowsTheBusiestAdapter_AndKeepsOneMissingFromAReading()
    {
        // Later on 2026-09-30 (the review's catch): a VPN adapter carries the same bytes as the card under it, so a sum read
        // double; the busiest adapter's rates and link are shown. An adapter a reading skipped is rated against its last.
        var clock = TimeSpan.Zero;
        var counters = new ScriptedCounters(
            [new("wifi", 0, 0, 866_000_000), new("vpn", 0, 0, 100_000_000)],
            [new("wifi", 1_050_000, 20_000, 866_000_000), new("vpn", 1_000_000, 10_000, 100_000_000)],
            [new("vpn", 2_000_000, 20_000, 100_000_000)],                                       // wifi skipped this reading
            [new("wifi", 3_150_000, 60_000, 866_000_000), new("vpn", 2_000_000, 20_000, 100_000_000)]);
        var meter = new NetworkMeter(counters, () => clock);

        Assert.Null(meter.Sample());
        clock = TimeSpan.FromSeconds(1);
        Assert.Equal((8_400_000, 160_000, 866_000_000), meter.Sample()!.Value);   // wifi's, not the sum
        clock = TimeSpan.FromSeconds(2);
        Assert.Equal((8_000_000, 80_000, 100_000_000), meter.Sample()!.Value);    // the vpn alone this time
        clock = TimeSpan.FromSeconds(3);
        Assert.Equal((8_400_000, 160_000, 866_000_000), meter.Sample()!.Value);   // wifi over the two seconds since its last

        meter.Reset();
        Assert.Null(meter.Sample());   // a first again
    }

    [Fact]
    public void TheSampler_ReadsWhatTheCheckedMetersNeed()
    {
        var time = new ManualTimeProvider();
        var source = new FakePerfSource();
        using var sampler = new PerfSampler(() => source, time);

        sampler.Ensure(PerfBarItems.Reads(Only("cpu", "ram")));
        time.Advance(TimeSpan.Zero);
        Assert.Equal(PerfReads.Cpu | PerfReads.Ram, source.LastReads);   // no adapter walk, no GPU read

        sampler.Ensure(PerfBarItems.Reads(Only("vram", "netup")));
        time.Advance(PerfSampler.Interval);
        Assert.Equal(PerfReads.Gpu | PerfReads.Net, source.LastReads);
        Assert.Equal(PerfReads.All, PerfBarItems.Reads(Only([.. PerfBarItems.Names])));

        sampler.Ensure(PerfBarItems.Reads(Only()));
        Assert.False(sampler.Running);
    }

    [Fact]
    public void NetworkMeters_TextShowsTheRates_TheDrawnLooksTheirShareOfTheLink()
    {
        var reading = new PerfSnapshot(null, null, null, null, NetDown: 12_400_000, NetUp: 800_000, NetLink: 100_000_000);

        var text = PerfBar.Render(PerfBarStyle.Text, Only("net", "netdown", "netup"), reading, [], 120)!;
        Assert.Equal(Centered("NET  12% · NET↓ 12.4M · NET↑  800K", 120), text.Text);
        Assert.Equal(new Style(Theme.Good), text.Segments.Single(s => s.Text == "12.4M").Style);

        var gauge = PerfBar.Render(PerfBarStyle.Gauge, Only("netdown"), reading, [], 120)!;
        Assert.Equal(Centered("NET↓ ━───────── 12.4M", 120), gauge.Text);   // 12.4 % of the link (one cell), the rate after it

        // No adapter: the network meters left out; an unknown link leaves NET out and draws the rates empty.
        Assert.Equal("", PerfBar.Render(PerfBarStyle.Text, Only("net", "netdown"), new PerfSnapshot(34, null, null, null), [], 120)!.Text);
        var unknownLink = reading with { NetLink = null };
        Assert.Equal(Centered("NET↓ 12.4M", 120), PerfBar.Render(PerfBarStyle.Text, Only("net", "netdown"), unknownLink, [], 120)!.Text);
    }

    /// <summary>
    /// PROC (2026-10-05, the user's ask and pick): the background processes running, a count no reader samples, written as a
    /// number in every look — no gauge, sparkline or LEDs — four cells wide, dim at none and green while any run.
    /// </summary>
    [Fact]
    public void Proc_IsACount_WrittenAloneInEveryLook_DimAtNone()
    {
        Assert.Equal(PerfReads.None, PerfBarItems.Reads(Only("proc")));
        Assert.Equal(PerfReads.Cpu, PerfBarItems.Reads(Only("cpu", "proc")));
        Assert.Equal("   0", PerfText.Count(0));
        Assert.Equal("  12", PerfText.Count(12));

        var text = PerfBar.Render(PerfBarStyle.Text, Only("cpu", "proc"), Reading, [], 120, processes: 2)!;
        Assert.Equal(Centered("CPU  34% · PROC    2", 120), text.Text);
        Assert.Equal(new Style(Theme.Good), text.Segments.Single(s => s.Text == "   2").Style);

        foreach (var style in new[] { PerfBarStyle.Gauge, PerfBarStyle.Spark, PerfBarStyle.Led })
        {
            var drawn = PerfBar.Render(style, Only("proc"), PerfSnapshot.None, [Reading, Reading], 120, processes: 0)!;
            Assert.Equal(Centered("PROC    0", 120), drawn.Text);   // the label and the number alone, nothing drawn
            Assert.Equal(new Style(Theme.Dim), drawn.Segments.Single(s => s.Text == "   0").Style);
        }

        // Beside a drawn meter: the gauge's, then the count; the others' widths as without it.
        var gauge = PerfBar.Render(PerfBarStyle.Gauge, Only("cpu", "proc"), Reading, [], 200, processes: 1)!;
        Assert.EndsWith(" 34%" + PerfText.MeterSeparator + "PROC    1", gauge.Text.TrimEnd(), StringComparison.Ordinal);
        Assert.Equal(PerfBar.Render(PerfBarStyle.Gauge, Only("cpu"), Reading, [], 200)!.Text.Trim(), gauge.Text.Trim()[..^(PerfText.MeterSeparator.Length + 9)]);
    }

    [Fact]
    public void OnlyTheCheckedMeters_Draw_InTheBarsOrder()
    {
        var row = PerfBar.Render(PerfBarStyle.Text, Only("vram", "cpu"), Reading, [], 120)!;
        Assert.Equal(Centered("CPU  34% · " + Perf.PerfText.VramLabel + "  91%", 120), row.Text);
    }

    // ── The arithmetic ──────────────────────────────────────────────────────

    [Fact]
    public void Cpu_IsKernelPlusUserLessIdle_OverKernelPlusUser()
    {
        // Kernel +600 (the idle +250 inside it) and user +400: 1000 ticks, 750 of them busy.
        Assert.Equal(75, PerfMath.CpuPercent(1_000, 2_000, 3_000, 1_250, 2_600, 3_400));
        Assert.Equal(0, PerfMath.CpuPercent(0, 0, 0, 100, 100, 0));     // all idle
        Assert.Equal(100, PerfMath.CpuPercent(0, 0, 0, 0, 50, 50));    // none idle
        Assert.Null(PerfMath.CpuPercent(5, 5, 5, 5, 5, 5));            // no time passed
        Assert.Null(PerfMath.CpuPercent(9, 5, 5, 5, 6, 6));            // a counter went backwards
    }

    [Fact]
    public void Percent_AndClamp_KeepInsideTheRange()
    {
        Assert.Equal(25, PerfMath.Percent(8, 32));
        Assert.Null(PerfMath.Percent(8, 0));
        Assert.Equal(100, PerfMath.Percent(40, 32));
        Assert.Equal(0, PerfMath.Clamp(double.NaN));
        Assert.Equal(0, PerfMath.Clamp(-3));
    }

    [Theory]
    [InlineData("pid_4_luid_0x00000000_0x0000D1B5_phys_0_eng_3_engtype_Copy", 0x0000_0000_0000_D1B5L)]
    [InlineData("luid_0x00000001_0x0000d1b5_phys_0", 0x0000_0001_0000_D1B5L)]
    public void TheLuid_IsReadFromACounterInstance(string instance, long luid)
    {
        Assert.True(PerfMath.TryParseLuid(instance, out long read));
        Assert.Equal(luid, read);
        Assert.Equal(luid, PerfMath.Luid((uint)(luid >> 32), (uint)luid));
    }

    [Theory]
    [InlineData("pid_16764_luid_0x00000000_0x00015985_phys_0", 16764, true)]
    [InlineData("PID_16764_luid_0x00000000_0x00015985_phys_0", 16764, true)]
    [InlineData("pid_167640_luid_0x00000000_0x00015985_phys_0", 16764, false)]
    [InlineData("pid_1676_luid_0x00000000_0x00015985_phys_0", 16764, false)]
    [InlineData("luid_0x00000000_0x00015985_phys_0", 16764, false)]
    [InlineData(null, 16764, false)]
    public void AProcessInstance_IsItsPidExactly(string? instance, int pid, bool mine)
    {
        // Embedded VRAM only's shared-memory reading (2026-10-01): pid 1676 is not pid 16764.
        Assert.Equal(mine, PerfMath.IsProcessInstance(instance, pid));
    }

    [Fact]
    public void AProcessesSharedGpuMemory_IsSummedOnTheOneAdapter()
    {
        // The review's finding (2026-10-01): a hybrid laptop's integrated GPU is not where the model spilled.
        const long Dgpu = 0x0000_0000_0001_5985L;
        (string, double)[] values =
        [
            ("pid_16_luid_0x00000000_0x00015985_phys_0", 300),
            ("pid_16_luid_0x00000000_0x00015985_phys_1", 40),
            ("pid_16_luid_0x00000000_0x0000D1B5_phys_0", 5000),   // the same process on the iGPU
            ("pid_167_luid_0x00000000_0x00015985_phys_0", 7000),   // another process on the dGPU
            ("luid_0x00000000_0x00015985_phys_0", 9000),
        ];

        Assert.Equal(340, PerfMath.ProcessAdapterSum(values, 16, Dgpu));
        Assert.Equal(7000, PerfMath.ProcessAdapterSum(values, 167, Dgpu));
        Assert.Null(PerfMath.ProcessAdapterSum(values, 167, 0xD1B5));
        Assert.Null(PerfMath.ProcessAdapterSum([], 16, Dgpu));
    }

    [Fact]
    public void AProcessesSharedGpuMemory_IsReadOrNull()
    {
        // This machine's own process: whatever PDH says (null where it has no GPU instance), never a throw.
        Assert.True(GpuMemory.ProcessSharedBytes(Environment.ProcessId) is null or >= 0);
        Assert.Null(GpuMemory.ProcessSharedBytes(-1));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("pid_4_phys_0")]
    [InlineData("luid_0x0000")]
    [InlineData("luid_0x00000000-0x0000D1B5")]
    [InlineData("luid_0xZZZZZZZZ_0x0000D1B5")]
    [InlineData("luid_0x00000000_0xGGGGGGGG")]
    public void ALuidThatIsNotThere_IsNotRead(string? instance)
    {
        Assert.False(PerfMath.TryParseLuid(instance, out _));
    }

    [Fact]
    public void TheGpuLoad_IsTheBusiestEngine_SummedOverTheProcesses_OfTheOneAdapter()
    {
        const string gpu = "luid_0x00000000_0x0000D1B5";
        const string igpu = "luid_0x00000000_0x0000AAAA";
        var engines = new List<(string, double)>
        {
            ($"pid_1_{gpu}_phys_0_eng_0_engtype_3D", 30),
            ($"pid_2_{gpu}_phys_0_eng_0_engtype_3D", 25),        // the same engine, another process: 55
            ($"pid_2_{gpu}_phys_0_eng_5_engtype_Compute_0", 70),  // the busiest engine
            ($"pid_3_{gpu}_phys_0_eng_2_engtype_Copy", -1),       // a counter's negative glitch counts as none
            ($"pid_1_{igpu}_phys_0_eng_0_engtype_3D", 99),        // the integrated GPU is not the one read
            ("pid_1_no_luid_here", 99),
        };

        Assert.Equal(70, PerfMath.GpuEnginePercent(engines, PerfMath.Luid(0, 0xD1B5)));
        Assert.Equal(99, PerfMath.GpuEnginePercent(engines, PerfMath.Luid(0, 0xAAAA)));
        Assert.Null(PerfMath.GpuEnginePercent(engines, PerfMath.Luid(0, 0xBBBB)));
        Assert.Equal(100, PerfMath.GpuEnginePercent([($"pid_1_{gpu}_phys_0_eng_0_engtype_3D", 80), ($"pid_2_{gpu}_phys_0_eng_0_engtype_3D", 80)], PerfMath.Luid(0, 0xD1B5)));   // clamped
    }

    // ── The sampler ─────────────────────────────────────────────────────────

    [Fact]
    public void TheSampler_ReadsOnceASecond_WhileOn_AndKeepsTheLastTen()
    {
        var time = new ManualTimeProvider();
        var sources = new List<FakePerfSource>();
        using var sampler = new PerfSampler(() => { var s = new FakePerfSource(); sources.Add(s); return s; }, time);
        Assert.False(sampler.Running);
        Assert.Equal(PerfSnapshot.None, sampler.Read().Latest);

        sampler.Ensure(true);
        sampler.Ensure(true);   // the same state: nothing new
        Assert.True(sampler.Running);
        Assert.Single(sources);
        var source = sources[0];
        for (int i = 1; i <= 12; i++)
        {
            source.Next = new PerfSnapshot(i, 50, null, null);
            time.Advance(i == 1 ? TimeSpan.Zero : PerfSampler.Interval);
        }

        var (latest, history, version) = sampler.Read();
        Assert.Equal(12, source.Samples);
        Assert.Equal(new PerfSnapshot(12, 50, null, null), latest);
        Assert.Equal(PerfSampler.HistoryLength, history.Count);
        Assert.Equal(Enumerable.Range(3, 10).Select(i => (double?)i), history.Select(h => h.Cpu));
        Assert.Equal(12, version);

        // Off: the timer stops, the source is disposed, the readings forgotten.
        sampler.Ensure(false);
        Assert.False(sampler.Running);
        Assert.True(source.Disposed);
        Assert.Equal(PerfSnapshot.None, sampler.Read().Latest);
        Assert.Empty(sampler.Read().History);
        time.Advance(PerfSampler.Interval * 3);
        Assert.Equal(12, source.Samples);

        // On again: a new source.
        sampler.Ensure(true);
        Assert.Equal(2, sources.Count);
    }

    [Fact]
    public void TheSampler_SurvivesASourceThatThrows_OrDoesNotOpen()
    {
        var time = new ManualTimeProvider();
        var source = new FakePerfSource { Next = new PerfSnapshot(10, 20, 30, 40), Throw = new InvalidOperationException("no counters") };
        using (var sampler = new PerfSampler(() => source, time))
        {
            sampler.Ensure(true);
            time.Advance(TimeSpan.Zero);
            Assert.Equal(PerfSnapshot.None, sampler.Read().Latest);   // the throw reads as nothing
            time.Advance(PerfSampler.Interval);
            Assert.Equal(new PerfSnapshot(10, 20, 30, 40), sampler.Read().Latest);
        }

        Assert.True(source.Disposed);   // Dispose stops it

        using var broken = new PerfSampler(() => throw new DllNotFoundException("pdh.dll"), time);
        broken.Ensure(true);
        time.Advance(TimeSpan.Zero);
        Assert.True(broken.Running);
        Assert.Equal(PerfSnapshot.None, broken.Read().Latest);
        Assert.Throws<ArgumentNullException>(() => new PerfSampler(null!, time));
        Assert.Throws<ArgumentNullException>(() => new PerfSampler(() => new NullPerfSource(), null!));
    }

    [Fact]
    public void GpuMemory_ReadsTheBiggestAdapter_OrNothing()
    {
        // The Embedded VRAM budget's total (later on 2026-09-29): a card's dedicated bytes, or null with none (a CI runner).
        long? bytes = GpuMemory.DedicatedBytes();
        Assert.True(bytes is null || bytes > 0);
    }

    [Fact]
    public void TheNullSource_ReadsNothing()
    {
        using var source = new NullPerfSource();
        Assert.Equal(PerfSnapshot.None, source.Sample(PerfReads.All));
    }

    // ── The looks ───────────────────────────────────────────────────────────

    private static readonly PerfSnapshot Reading = new(34, 62, 18, 91);

    // A row as it sits centred in `cells` (later on 2026-09-29; at the right for a few hours before): half the room ahead of it.
    private static string Centered(string text, int cells) => new string(' ', (cells - TextCells.Width(text)) / 2) + text;

    [Fact]
    public void NoMeterChecked_DrawsNoRow()
    {
        Assert.Null(PerfBar.Render(PerfBarStyle.Gauge, Only(), Reading, [], 120));
    }

    [Fact]
    public void Text_IsTheLabelsAndValues_TheValuesInTheLoadsColour()
    {
        var row = PerfBar.Render(PerfBarStyle.Text, Four, Reading, [], 120)!;
        Assert.Equal(Centered("CPU  34% · RAM  62% · GPU  18% · " + Perf.PerfText.VramLabel + "  91%", 120), row.Text);
        Assert.Equal(new Style(Theme.Good), row.Segments.Single(s => s.Text == " 34%").Style);
        Assert.Equal(new Style(Theme.Warn), row.Segments.Single(s => s.Text == " 62%").Style);
        Assert.Equal(new Style(Theme.Bad), row.Segments.Single(s => s.Text == " 91%").Style);
        Assert.Equal(Theme.DimText, row.Segments.Single(s => s.Text == "CPU ").Style);

        // No GPU reader: its meters left out. Nothing read yet: an empty row, still a row.
        Assert.Equal(Centered("CPU  34% · RAM  62%", 120), PerfBar.Render(PerfBarStyle.Text, Four, Reading with { Gpu = null, Vram = null }, [], 120)!.Text);
        Assert.Equal("", PerfBar.Render(PerfBarStyle.Text, Four, PerfSnapshot.None, [], 120)!.Text);

        // Too narrow: cut at the edge, at the row's left.
        Assert.Equal("CPU  34% · R", PerfBar.Render(PerfBarStyle.Text, Four, Reading, [], 12)!.Text);
    }

    [Fact]
    public void Gauge_FillsInHalves_TheTrackDim()
    {
        // A heavy line no taller than the labels (2026-09-30, the user's ask), in half-cell steps.
        Assert.Equal(("━━━╸", "──────"), PerfBar.Gauge(34, 10));
        Assert.Equal(("", "──────────"), PerfBar.Gauge(0, 10));
        Assert.Equal(("━━━━━━━━━━", ""), PerfBar.Gauge(100, 10));
        Assert.Equal(("", "──────────"), PerfBar.Gauge(1.25, 10));
        Assert.Equal(("╸", "─────────"), PerfBar.Gauge(5, 10));

        var row = PerfBar.Render(PerfBarStyle.Gauge, Four, Reading, [], 120)!;
        Assert.Equal(Centered("CPU ━━━╸──────  34%   RAM ━━━━━━────  62%   GPU ━━────────  18%   " + Perf.PerfText.VramLabel + " ━━━━━━━━━─  91%", 120), row.Text);
        Assert.Equal(new Style(Theme.Bad), row.Segments.Single(s => s.Text == "━━━━━━━━━").Style);
        Assert.Equal(Theme.DimText, row.Segments.Single(s => s.Text == "──────").Style);
    }

    [Fact]
    public void Spark_DrawsTheLastReadings_NewestAtTheRight()
    {
        Assert.Equal("▁", PerfBar.SparkLevel(0));
        Assert.Equal("▄", PerfBar.SparkLevel(45));
        Assert.Equal("█", PerfBar.SparkLevel(100));

        var history = new[] { 0.0, 20, 40, 60, 90 }.Select(v => new PerfSnapshot(v, 50, null, null)).ToList();
        var row = PerfBar.Render(PerfBarStyle.Spark, Four, history[^1], history, 120)!;
        Assert.Equal(Centered("CPU      ▁▂▄▅█  90%   RAM      ▅▅▅▅▅  50%", 120), row.Text);   // five readings: five blanks ahead of them
        Assert.Equal(new Style(Theme.Bad), row.Segments.First(s => s.Text == "█").Style);
        Assert.Equal(new Style(Theme.Good), row.Segments.First(s => s.Text == "▁").Style);
    }

    [Fact]
    public void Led_LightsAStartedSegment_AlongTheGradient()
    {
        Assert.Equal(4, PerfBar.LedsLit(34, 10));
        Assert.Equal(0, PerfBar.LedsLit(0, 10));
        Assert.Equal(10, PerfBar.LedsLit(100, 10));
        Assert.Equal(3, PerfBar.LedsLit(30, 10));   // exactly three, not a fourth started

        var row = PerfBar.Render(PerfBarStyle.Led, Four, new PerfSnapshot(34, null, null, null), [], 120)!;
        Assert.Equal(Centered("CPU ▰▰▰▰▱▱▱▱▱▱  34%", 120), row.Text);
        var lit = row.Segments.Where(s => s.Text == PerfText.LedOn).ToList();
        Assert.Equal(new Style(Theme.GradientStops[0]), lit[0].Style);
        Assert.Equal(Theme.DimText, row.Segments.First(s => s.Text == PerfText.LedOff).Style);
    }

    [Theory]
    [InlineData(80, 8)]   // four meters of ten are 81 cells: eight each fit
    [InlineData(62, 4)]   // four each: 62 cells exactly
    public void ANarrowWindow_ShrinksTheMeters_ThenFallsBackToText(int cells, int meter)
    {
        var row = PerfBar.Render(PerfBarStyle.Led, Four, Reading, [], cells)!;
        Assert.True(TextCells.Width(row.Text) <= cells);
        Assert.Equal(meter * 4, row.Text.Count(c => c is '▰' or '▱'));

        var text = PerfBar.Render(PerfBarStyle.Gauge, Four, Reading, [], 45)!;
        Assert.Equal(Centered("CPU  34% · RAM  62% · GPU  18% · " + Perf.PerfText.VramLabel + "  91%", 45), text.Text);   // no meter width fits: the text look
    }

    [Fact]
    public void EveryLook_IsCentred_ACutRowAtItsLeft()
    {
        foreach (var style in new[] { PerfBarStyle.Text, PerfBarStyle.Gauge, PerfBarStyle.Spark, PerfBarStyle.Led })
        {
            var row = PerfBar.Render(style, Four, Reading, [Reading], 120)!;
            int pad = row.Segments[0].Text.Length;
            int drawn = TextCells.Width(row.Text) - pad;
            Assert.True(pad > 0 && row.Segments[0].Text.All(c => c == ' '), style.ToString());
            Assert.Equal((120 - drawn) / 2, pad);   // the odd cell, if any, at the right
        }

        Assert.NotEqual(' ', PerfBar.Render(PerfBarStyle.Text, Four, Reading, [], 12)!.Text[0]);

        // The text look's values keep four cells, so a value gaining a digit moves nothing.
        Assert.Equal("CPU   7%", PerfBar.Render(PerfBarStyle.Text, Four, new PerfSnapshot(7.49, null, null, null), [], 8)!.Text);
        Assert.Equal("CPU 100%", PerfBar.Render(PerfBarStyle.Text, Four, new PerfSnapshot(100, null, null, null), [], 8)!.Text);
    }

    [Theory]
    [InlineData(34.0, " 34%")]
    [InlineData(7.49, "  7%")]
    [InlineData(99.5, "100%")]
    [InlineData(250.0, "100%")]
    public void TheValue_IsFourCellsWide(double value, string shown)
    {
        Assert.Equal(shown, PerfText.Percent(value));
    }
}
