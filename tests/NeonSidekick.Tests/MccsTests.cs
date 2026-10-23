using System.Runtime.CompilerServices;
using NeonSidekick.Monitors;

namespace NeonSidekick.Tests;

/// <summary>The MCCS capabilities parse, the input codes and names (2026-10-23, the monitors plan), over this PC's two monitors' real strings.</summary>
public sealed class MccsTests
{
    private const string Lg = "(prot(monitor)type(lcd)model(WP65C)cmds(01 02 03 0C E3 F3)vcp(02 04 05 08 10 12 14(05 08 0B ) 16 18 1A 52 60(11 12 0F 10 ) AC AE B2 B6 C0 C6 C8 C9 D6(01 04) DF 62 8D F4 F5(01 02 03 04) F6(00 01 02) 4D 4E 4F 15(01 06 11 13 14 15 18 19 20 22 23 24 28 29 32 48) F7(00 01 02 03) F8(00 01) F9 EF FA(00 01) FD(00 01) FE(00 01 02) FF)mccs_ver(2.1)mswhql(1))";

    private const string Xg = "(prot(monitor)type(LCD)model(XG27UQDMS)cmds(01 02 03 07 0C E3 F3)vcp(02 04 05 08 10 12 14(03 04 05 06 07 08 09 0B) 16 18 1A 52 59 5A 5B 5C 5D 5E 5F(0005) 60(0F 11 12) 61(01 02) 62 72(5000 6400 7800 8C00 A000) 87 8A 8D(01 02) A8 AC)mswhql(1)asset_eep(40)mccs_ver(2.2))";

    [Fact]
    public void Inputs_ThisPcsMonitors_InTheirOrder()
    {
        Assert.Equal(new byte[] { 0x11, 0x12, 0x0F, 0x10 }, Mccs.Inputs(Lg).Inputs);
        Assert.Equal(new byte[] { 0x0F, 0x11, 0x12 }, Mccs.Inputs(Xg).Inputs);
        Assert.True(Mccs.Inputs(Xg).Reported);
    }

    [Fact]
    public void Inputs_Unspaced_ReadInPairs()
    {
        Assert.Equal(new byte[] { 0x0F, 0x11, 0x1B }, Mccs.Inputs("(vcp(021060(0F111B)AC))").Inputs);
    }

    [Fact]
    public void Inputs_NestedGroupsBefore_AreSkippedWhole()
    {
        Assert.Equal(new byte[] { 0x11 }, Mccs.Inputs("(vcp(02 C8(01(02 03) 04) 60(11) AC))").Inputs);
    }

    [Fact]
    public void Inputs_FourDigitValues_KeepTheirLowByte()
    {
        Assert.Equal(new byte[] { 0x0F, 0x11 }, Mccs.Inputs("(vcp(60(000F 0011)))").Inputs);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("(prot(monitor)type(lcd))")]
    [InlineData("(vcp(02 04 60 AC))")]
    [InlineData("(vcp(60( )))")]
    [InlineData("(vcpname(60(Input))vcp(02))")]
    public void Inputs_NoneListed_TheCommonFour(string? caps)
    {
        var (inputs, reported) = Mccs.Inputs(caps);
        Assert.False(reported);
        Assert.Equal(Mccs.CommonInputs, inputs);
        Assert.Equal(new byte[] { 0x0F, 0x10, 0x11, 0x12 }, inputs);
    }

    [Theory]
    [InlineData("0x1B", 0x1B)]
    [InlineData("1b", 0x1B)]
    [InlineData(" 0X11 ", 0x11)]
    [InlineData("ff", 0xFF)]
    [InlineData("1", 0x01)]
    public void TryParseCode_Hex(string text, int code)
    {
        Assert.True(Mccs.TryParseCode(text, out byte value));
        Assert.Equal(code, value);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0x00")]
    [InlineData("100")]
    [InlineData("0x1G")]
    [InlineData("")]
    [InlineData("hdmi")]
    public void TryParseCode_RefusesOutsideTheRange(string text) => Assert.False(Mccs.TryParseCode(text, out _));

    [Fact]
    public void StandardNames_AndHex()
    {
        Assert.Equal("HDMI-1", Mccs.StandardName(0x11));
        Assert.Equal("DisplayPort-1", Mccs.StandardName(0x0F));
        Assert.Null(Mccs.StandardName(0x1B));
        Assert.Equal("0x1B", Mccs.Hex(0x1B));
    }
}

/// <summary>The monitors' pure layer (2026-10-23, the monitors plan): keys, numbers, the layout arithmetic, the errors, the struct sizes, the input names.</summary>
public sealed class MonitorKeyTests
{
    [Fact]
    public void From_ThisPcsDevicePaths()
    {
        Assert.Equal("ausaa34-uid4355", MonitorKey.From(@"\\?\DISPLAY#AUSAA34#5&1ceef1a8&1&UID4355#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}"));
        Assert.Equal("gsm7768-uid4353", MonitorKey.From(@"\\?\DISPLAY#GSM7768#5&1ceef1a8&1&UID4353#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}"));
        Assert.Null(MonitorKey.From(null));
        Assert.Null(MonitorKey.From(@"\\?\SWD#MMDEVAPI#{0.0.0}"));
        Assert.Equal("ausaa34", MonitorKey.Model("ausaa34-uid4355"));
    }

    [Fact]
    public void Saved_TheSameKey_ElseOneOfTheModelWhenOneIsConnected()
    {
        Assert.Equal("ausaa34-uid4355", MonitorKey.Saved("ausaa34-uid4355", ["ausaa34-uid4355"], ["ausaa34-uid4355"]));
        // A cable moved: the port's UID changed, one XG27 connected.
        Assert.Equal("ausaa34-uid4355", MonitorKey.Saved("ausaa34-uid4356", ["ausaa34-uid4355"], ["ausaa34-uid4356", "gsm7768-uid4353"]));
        // Two of the model connected: no guess.
        Assert.Null(MonitorKey.Saved("ausaa34-uid4356", ["ausaa34-uid4355"], ["ausaa34-uid4356", "ausaa34-uid4357"]));
        Assert.Null(MonitorKey.Saved("gsm7768-uid1", ["ausaa34-uid4355"], ["gsm7768-uid1"]));
    }
}

public sealed class MonitorNumberingTests
{
    private static MonitorTarget T(string target, string name, bool active, string? source, string? gdi, int x = 0) =>
        new(target, @"\\?\DISPLAY#M" + target + "#5&1&UID" + target + "#{x}", name, "DisplayPort", active, source, gdi, active ? new MonitorMode(1920, 1080, 60, x, 0) : null);

    [Fact]
    public void Numbers_FollowTheScreenCapturesOrder_ByGdiName()
    {
        var fake = new Fakes.FakeScreenSystem();
        var snapshot = MonitorNumbering.Build(
            [T("b", "Second", true, "s1", @"\\.\DISPLAY2", 1920), T("a", "First", true, "s0", @"\\.\DISPLAY1")],
            fake.MonitorList.Select(m => m.Name).ToList());
        Assert.Equal(["1 First", "2 Second"], snapshot.Monitors.Select(m => m.Label));
        Assert.Equal(fake.MonitorList.Select(m => m.Number), snapshot.Monitors.Select(m => m.Number));
        Assert.True(snapshot.Monitors[0].Primary);
        Assert.Equal(MonitorArrangement.Extended, snapshot.Layout);
    }

    [Fact]
    public void Duplicated_SitTogether_NumbersRunOn_InactiveAfter()
    {
        var snapshot = MonitorNumbering.Build(
            [T("c", "Off", false, null, null), T("a", "A", true, "s0", @"\\.\DISPLAY1"), T("b", "B", true, "s0", @"\\.\DISPLAY1")],
            [@"\\.\DISPLAY1"]);
        Assert.Equal(["1 A", "2 B", "3 Off"], snapshot.Monitors.Select(m => m.Label));
        Assert.True(snapshot.Monitors[0].Shared && snapshot.Monitors[1].Shared);
        Assert.False(snapshot.Monitors[2].Active);
        Assert.Null(snapshot.Monitors[2].Mode);
        Assert.Equal(MonitorArrangement.Duplicated, snapshot.Layout);
    }

    [Fact]
    public void RepeatedNames_GetANumber_AndAnEmptyNameReadsDisplay()
    {
        var snapshot = MonitorNumbering.Build(
            [T("a", "DELL P2419H", true, "s0", @"\\.\DISPLAY1"), T("b", "DELL P2419H", true, "s1", @"\\.\DISPLAY2", 1920), T("c", " ", false, null, null)],
            [@"\\.\DISPLAY1", @"\\.\DISPLAY2"]);
        Assert.Equal(["DELL P2419H", "DELL P2419H #2", MonitorText.Unnamed], snapshot.Monitors.Select(m => m.Name));
    }

    [Fact]
    public void OneActive_IsSingle()
    {
        var snapshot = MonitorNumbering.Build([T("a", "A", true, "s0", @"\\.\DISPLAY1"), T("b", "B", false, null, null)], [@"\\.\DISPLAY1"]);
        Assert.Equal(MonitorArrangement.Single, snapshot.Layout);
    }
}

public sealed class MonitorLayoutPlanTests
{
    [Fact]
    public void OnlyPath_TheActivePath_ElseAPairingOnAFreeSource()
    {
        var paths = new List<LayoutPath>
        {
            new(0, "lg", "s0", true),
            new(1, "xg", "s0", false),
            new(2, "xg", "s1", false),
        };
        Assert.Equal(0, MonitorLayoutPlan.OnlyPath(paths, "lg"));
        Assert.Equal(2, MonitorLayoutPlan.OnlyPath(paths, "xg"));
        Assert.Null(MonitorLayoutPlan.OnlyPath(paths, "none"));
        // Every source in use: the first pairing still (Windows picks the source again).
        Assert.Equal(1, MonitorLayoutPlan.OnlyPath([new(0, "lg", "s0", true), new(1, "xg", "s0", false)], "xg"));
    }

    [Fact]
    public void Primary_MovesTheChosenToTheOrigin_AndTheRestBySameOffset()
    {
        var moved = MonitorLayoutPlan.Primary([new(0, 0, 0), new(1, 3440, 0), new(2, 3440, -200)], 1);
        Assert.Equal([new(0, -3440, 0), new(1, 0, 0), new(2, 0, -200)], moved);
    }
}

public sealed class MonitorErrorsTests
{
    [Theory]
    [InlineData(unchecked((int)0xC0262589))]
    [InlineData(unchecked((int)0xC0262580))]
    [InlineData(unchecked((int)0xC0262582))]
    [InlineData(unchecked((int)0xC02625DA))]
    public void DdcFamily_ReadsAsNoDdc(int code)
    {
        Assert.Equal(MonitorText.NoDdc, MonitorErrors.Sentence(code));
        Assert.Contains("DDC/CI", MonitorText.NoDdc, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOthers()
    {
        Assert.Equal(MonitorText.NoInputSwitch, MonitorErrors.Sentence(unchecked((int)0xC0262584)));
        Assert.Equal(MonitorText.OnlyFromDesktop, MonitorErrors.Sentence(5));
        Assert.Equal(MonitorText.OnlyFromDesktop, MonitorErrors.Sentence(unchecked((int)0xC02625E0)));
        Assert.Equal(MonitorText.BadConfiguration, MonitorErrors.Sentence(1610));
        Assert.Equal(MonitorText.NotSupported, MonitorErrors.Sentence(50));
        Assert.Contains("0x0000001F", MonitorErrors.Sentence(31, "A device attached to the system is not functioning."), StringComparison.Ordinal);
        Assert.False(MonitorErrors.IsDdc(5));
        Assert.NotEqual(MonitorText.NoDdc, MonitorText.NoMonitorControl);
    }
}

public sealed class MonitorNativeLayoutTests
{
    [Fact]
    public void StructSizes_MatchWingdi()
    {
        Assert.Equal(72, Unsafe.SizeOf<DisplayPathInfo>());
        Assert.Equal(64, Unsafe.SizeOf<DisplayModeInfo>());
        Assert.Equal(84, Unsafe.SizeOf<DisplaySourceDeviceName>());
        Assert.Equal(420, Unsafe.SizeOf<DisplayTargetDeviceName>());
        Assert.Equal(264, Unsafe.SizeOf<PhysicalMonitor>());
        Assert.Equal(20, Unsafe.SizeOf<DisplayDeviceInfoHeader>());
    }
}

public sealed class MonitorWordsTests
{
    private static readonly MonitorInfo Xg = new(2, Fakes.FakeMonitorSystem.Xg, "ausaa34-uid4355", "XG27UQDMS", "DisplayPort", true, false, null, @"\\.\DISPLAY2", false);

    [Fact]
    public void Rows_ReportedThenNamedThenCurrent_HiddenLeftOutUnlessCurrent()
    {
        var names = new Dictionary<string, string> { ["ausaa34-uid4355/1b"] = "USB-C", ["ausaa34-uid4355/11"] = "Work laptop" };
        var words = new MonitorWords(names, ["ausaa34-uid4355/12"], [Xg]);
        var rows = words.Rows(Xg, [0x0F, 0x11, 0x12], current: 0x0F, away: null);
        Assert.Equal(new byte[] { 0x0F, 0x11, 0x1B }, rows.Select(r => r.Code));
        Assert.Equal("Work laptop (HDMI-1)", rows[1].Words);
        Assert.Equal("USB-C (0x1B)", rows[2].Words);
        Assert.True(rows[0].Current);
        Assert.Equal(4, words.Rows(Xg, [0x0F, 0x11, 0x12], 0x0F, null, all: true).Count);
        Assert.Contains(words.Rows(Xg, [0x0F, 0x11], current: 0x12, away: 0x0F), r => r.Code == 0x12 && r.Current && r.Hidden);
        Assert.True(words.Rows(Xg, [0x0F, 0x11], current: 0x12, away: 0x0F)[0].Away);
    }

    [Fact]
    public void Resolve_GivenStandardOrHex()
    {
        var words = new MonitorWords(new Dictionary<string, string> { ["ausaa34-uid4355/11"] = "Work laptop" }, ["ausaa34-uid4355/12"], [Xg]);
        var rows = words.Rows(Xg, [0x0F, 0x11, 0x12], 0x0F, null, all: true);
        Assert.Equal((byte)0x11, MonitorWords.Resolve(rows, "work LAPTOP"));
        Assert.Equal((byte)0x11, MonitorWords.Resolve(rows, "hdmi-1"));
        Assert.Equal((byte)0x12, MonitorWords.Resolve(rows, "HDMI-2"));
        Assert.Equal((byte)0x1B, MonitorWords.Resolve(rows, "0x1b"));
        Assert.Equal((byte)0x03, MonitorWords.Resolve(rows, "dvi-1"));
        Assert.Null(MonitorWords.Resolve(rows, "the laptop"));
    }

    [Fact]
    public void CleanName_RefusesATakenName_AndCutsAndScrubs()
    {
        var words = new MonitorWords(new Dictionary<string, string> { ["ausaa34-uid4355/11"] = "Work laptop" }, null, [Xg]);
        var rows = words.Rows(Xg, [0x0F, 0x11, 0x12], 0x0F, null, all: true);
        Assert.Null(MonitorWords.CleanName("hdmi-2", 0x0F, rows));
        Assert.Null(MonitorWords.CleanName("work laptop", 0x0F, rows));
        Assert.Null(MonitorWords.CleanName("0x12", 0x0F, rows));
        Assert.Equal("Work laptop", MonitorWords.CleanName(" Work laptop ", 0x11, rows));
        Assert.Equal(MonitorWords.MaxName, MonitorWords.CleanName(new string('x', 40), 0x0F, rows)!.Length);
        Assert.Equal("a\\u202Eb", MonitorWords.CleanName("a\u202Eb", 0x0F, rows));
    }

    [Fact]
    public void Rename_Hide_AndAMovedCable()
    {
        var names = MonitorWords.Rename(null, "ausaa34-uid4355", 0x0F, "This PC");
        Assert.Equal("This PC", names!["ausaa34-uid4355/0f"]);
        Assert.Null(MonitorWords.Rename(names, "ausaa34-uid4355", 0x0F, " "));
        var hidden = MonitorWords.Hide(null, "ausaa34-uid4355", 0x12, hide: true);
        Assert.Equal(["ausaa34-uid4355/12"], hidden);
        Assert.Null(MonitorWords.Hide(hidden, "ausaa34-uid4355", 0x12, hide: false));

        var moved = Xg with { Key = "ausaa34-uid9999" };
        var words = new MonitorWords(names, null, [moved]);
        Assert.Equal("This PC", words.Given(moved, 0x0F));
    }
}
