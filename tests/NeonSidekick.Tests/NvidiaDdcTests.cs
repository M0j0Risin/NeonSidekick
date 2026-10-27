using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using NeonSidekick.Monitors;

namespace NeonSidekick.Tests;

/// <summary>DDC/CI's frames by hand (2026-10-27, the NVIDIA plan), over the bytes the spike sent and read on this PC's XG27UQDMS and LG.</summary>
public sealed class DdcPacketTests
{
    private static readonly byte[] GoodInput = [0x6E, 0x88, 0x02, 0x00, 0x60, 0x00, 0x00, 0x12, 0x00, 0x0F, 0xC9];

    // A capabilities fragment reply for offset with data, its checksum from 0x50.
    private static byte[] Fragment(int offset, params byte[] data)
    {
        var reply = new List<byte> { 0x6E, (byte)(0x80 | (data.Length + 3)), 0xE3, (byte)(offset >> 8), (byte)offset };
        reply.AddRange(data);
        byte check = 0x50;
        foreach (byte b in reply)
        {
            check ^= b;
        }

        reply.Add(check);
        return [.. reply];
    }

    [Fact]
    public void Requests_AreTheSpikesBytes()
    {
        Assert.Equal(new byte[] { 0x82, 0x01, 0x60, 0xDC }, DdcPacket.GetInput());
        Assert.Equal(new byte[] { 0x84, 0x03, 0x60, 0x00, 0x11, 0xC9 }, DdcPacket.SetInput(0x11));
        Assert.Equal(new byte[] { 0x83, 0xF3, 0x00, 0x00, 0x4F }, DdcPacket.Capabilities(0));
        Assert.Equal(new byte[] { 0x83, 0xF3, 0x01, 0x20, 0x6E ^ 0x51 ^ 0x83 ^ 0xF3 ^ 0x01 ^ 0x20 }, DdcPacket.Capabilities(0x120));
        Assert.Throws<ArgumentOutOfRangeException>(() => DdcPacket.Capabilities(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => DdcPacket.Capabilities(0x10000));
    }

    [Fact]
    public void ReadInput_TheXg27sReply_IsDisplayPort1_AnyFaultIsNull()
    {
        Assert.Equal((byte)0x0F, DdcPacket.ReadInput(GoodInput));
        Assert.Equal((byte)0x0F, DdcPacket.ReadInput([.. GoodInput, 0xFF, 0xFF]));

        static byte[] With(int at, byte value)
        {
            byte[] copy = [.. GoodInput];
            copy[at] = value;
            return copy;
        }

        Assert.Null(DdcPacket.ReadInput(GoodInput.AsSpan(0, 10)));     // short
        Assert.Null(DdcPacket.ReadInput(With(10, 0xC8)));                // checksum
        Assert.Null(DdcPacket.ReadInput(With(0, 0x6F)));                 // not the display
        Assert.Null(DdcPacket.ReadInput(With(2, 0x03)));                 // opcode
        Assert.Null(DdcPacket.ReadInput(Fixed(With(3, 0x01))));   // result: unsupported, its checksum right
        Assert.Null(DdcPacket.ReadInput(Fixed(With(4, 0x10))));   // another code
        Assert.Null(DdcPacket.ReadInput([.. Enumerable.Repeat((byte)0xFF, 11)]));  // the bus read nothing
    }

    [Fact]
    public void ReadFragment_ChecksTheOffsetAndChecksum_TheLastIsEmpty()
    {
        Assert.Equal("(pro"u8.ToArray(), DdcPacket.ReadFragment(Fragment(0, "(pro"u8.ToArray()), 0));
        Assert.Empty(DdcPacket.ReadFragment(Fragment(64), 64)!);
        Assert.Null(DdcPacket.ReadFragment(Fragment(32, 0x41), 0));
        byte[] bad = Fragment(0, 0x41);
        bad[^1] ^= 1;
        Assert.Null(DdcPacket.ReadFragment(bad, 0));
        Assert.Null(DdcPacket.ReadFragment(Fragment(0, 0x41, 0x42).AsSpan(0, 7), 0));
        Assert.Null(DdcPacket.ReadFragment([0x6E, 0x80], 0));
    }

    [Fact]
    public void ReadCapabilities_JoinsTheFragments_CutsAtTheNul_RetriesAFault()
    {
        const string caps = "(prot(monitor)type(lcd)model(WP65C)cmds(01 02 03 0C E3 F3)vcp(60(11 12 0F 10 ))mccs_ver(2.1))";
        byte[] all = [.. System.Text.Encoding.ASCII.GetBytes(caps), 0x00, 0xFF];
        int faults = 0;
        var asked = new List<int>();
        var read = DdcPacket.ReadCapabilities(offset =>
        {
            asked.Add(offset);
            if (offset == 32 && faults++ < 2)
            {
                return null;   // the bus failed twice: the third try reads it
            }

            if (offset >= all.Length + 1)
            {
                return Fragment(offset);
            }

            // The LG's tail: 00 FF, then FF alone, then the empty end.
            byte[] data = offset >= all.Length ? [0xFF] : all.Skip(offset).Take(32).ToArray();
            return Fragment(offset, data);
        });

        Assert.NotNull(read);
        Assert.Equal(caps, read.Raw);
        Assert.Equal(new byte[] { 0x11, 0x12, 0x0F, 0x10 }, read.Inputs);
        Assert.True(read.Reported);
        Assert.Equal(3, asked.Count(o => o == 32));
    }

    [Fact]
    public void ReadCapabilities_AFragmentNeverRight_OrNoEnd_IsNull()
    {
        int tries = 0;
        Assert.Null(DdcPacket.ReadCapabilities(_ =>
        {
            tries++;
            return null;
        }));
        Assert.Equal(DdcPacket.FragmentTries, tries);
        Assert.Null(DdcPacket.ReadCapabilities(offset => Fragment(offset == 0 ? 1 : offset, 0x41)));
        Assert.Null(DdcPacket.ReadCapabilities(offset => Fragment(offset, 0x41)));   // never empty: cut at MaxFragments
        Assert.Equal("", DdcPacket.ReadCapabilities(offset => Fragment(offset))!.Raw);
    }

    // The reply with its checksum made right again after a byte changed.
    private static byte[] Fixed(byte[] reply)
    {
        byte check = 0x50;
        foreach (byte b in reply.AsSpan(0, reply.Length - 1))
        {
            check ^= b;
        }

        reply[^1] = check;
        return reply;
    }
}

/// <summary>Matching NVIDIA's displays to Windows' targets (2026-10-27, the NVIDIA plan): EDID PnP ids, adapters, exactly one each way.</summary>
public sealed class NvidiaDisplaysTests
{
    private const string Adapter = "00000000000152C3";
    private const string XgTarget = Adapter + ":4355";
    private const string LgTarget = Adapter + ":4353";

    private static readonly MonitorInfo XgOff = new(2, XgTarget, "ausaa34-uid4355", "XG27UQDMS", "DisplayPort", false, false, null, null, false);
    private static readonly MonitorInfo LgOn = new(1, LgTarget, "gsm7768-uid4353", "LG ULTRAWIDE", "DisplayPort", true, true, null, @"\\.\DISPLAY1", false);
    private static readonly NvidiaDisplay XgNv = new("AUSAA34", Adapter, false, 1, 2);
    private static readonly NvidiaDisplay LgNv = new("GSM7768", Adapter, true, 1, 1);

    private static MonitorSnapshot Snapshot(params MonitorInfo[] monitors) => new(monitors, MonitorArrangement.Single);

    private static byte[] Edid(byte maker1, byte maker2, byte productLow, byte productHigh)
    {
        var edid = new byte[128];
        edid[8] = maker1;
        edid[9] = maker2;
        edid[10] = productLow;
        edid[11] = productHigh;
        return edid;
    }

    [Fact]
    public void PnpId_ThisPcsTwoMonitors_AndAShortOrBadHeader()
    {
        Assert.Equal("AUSAA34", NvidiaDisplays.PnpId(Edid(0x06, 0xB3, 0x34, 0xAA)));
        Assert.Equal("GSM7768", NvidiaDisplays.PnpId(Edid(0x1E, 0x6D, 0x68, 0x77)));
        Assert.Null(NvidiaDisplays.PnpId(new byte[11]));
        Assert.Null(NvidiaDisplays.PnpId(Edid(0x00, 0x00, 0x34, 0xAA)));   // letter 0
        Assert.Null(NvidiaDisplays.PnpId(Edid(0x7F, 0xFF, 0x34, 0xAA)));   // letter 31
    }

    [Fact]
    public void Luid_IsTheTargetsSixteenDigits()
    {
        Assert.Equal(Adapter, NvidiaDisplays.Luid(0x152C3));
        Assert.Equal("0000000100000002", NvidiaDisplays.Luid(0x1_0000_0002));
    }

    [Fact]
    public void Match_TheOneOffDesktopDisplay_OfItsModelAndAdapter()
    {
        var snapshot = Snapshot(LgOn, XgOff);
        Assert.Same(XgNv, NvidiaDisplays.Match([LgNv, XgNv], snapshot, XgOff));
        Assert.Same(XgNv, NvidiaDisplays.Match([LgNv, XgNv with { PnpId = null }, XgNv], snapshot, XgOff));
    }

    [Fact]
    public void Match_IsNull_WhenItCantTell()
    {
        var snapshot = Snapshot(LgOn, XgOff);
        Assert.Null(NvidiaDisplays.Match([LgNv, XgNv], snapshot, LgOn));                                       // on the desktop
        Assert.Null(NvidiaDisplays.Match([LgNv], snapshot, XgOff));                                            // NVIDIA hasn't it
        Assert.Null(NvidiaDisplays.Match([XgNv with { Active = true }], snapshot, XgOff));                      // NVIDIA has it on
        Assert.Null(NvidiaDisplays.Match([XgNv with { Adapter = "0000000000099999" }], snapshot, XgOff));      // another card
        Assert.Null(NvidiaDisplays.Match([XgNv, XgNv with { Output = 4 }], snapshot, XgOff));                  // two of NVIDIA's
        Assert.Null(NvidiaDisplays.Match([XgNv], snapshot, XgOff with { Key = null }));                        // no key

        var fake = XgOff with { Target = Fakes.FakeMonitorSystem.Xg };
        Assert.Null(NvidiaDisplays.Match([XgNv], Snapshot(LgOn, fake), fake));                                  // the fake's target shape

        var twin = XgOff with { Number = 3, Target = Adapter + ":4356", Key = "ausaa34-uid4356", Name = "XG27UQDMS #2" };
        Assert.Null(NvidiaDisplays.Match([XgNv, XgNv with { Output = 4 }], Snapshot(LgOn, XgOff, twin), XgOff));   // two of Windows'
        Assert.Same(XgNv, NvidiaDisplays.Match([XgNv], Snapshot(LgOn, XgOff, twin with { Active = true, GdiName = @"\\.\DISPLAY3" }), XgOff));
    }
}

/// <summary>NVIDIA's driver API structs: the sizes its version words carry (a wrong one answers -9 from every call).</summary>
public sealed class NvapiLayoutTests
{
    [Fact]
    public void StructSizes_MatchNvapi()
    {
        Assert.Equal(64, Unsafe.SizeOf<NvI2cInfo>());
        Assert.Equal(16, Unsafe.SizeOf<NvDisplayIds>());
        Assert.Equal(272, Unsafe.SizeOf<NvEdid>());
        Assert.Equal(568, Unsafe.SizeOf<NvLogicalGpuData>());
    }

    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public void VersionWords_AreTheSizeAndTheStructVersion()
    {
        Assert.Equal(64u | (3u << 16), NvapiNative.I2cVersion);
        Assert.Equal(16u | (3u << 16), NvapiNative.DisplayIdsVersion);
        Assert.Equal(272u | (3u << 16), NvapiNative.EdidVersion);
        Assert.Equal(568u | (1u << 16), NvapiNative.LogicalGpuVersion);
    }
}

/// <summary>Skips unless <c>NEONSIDEKICK_TEST_NVIDIA_MONITOR</c> is <c>1</c>, on Windows: a monitor off the desktop on an NVIDIA card is read.</summary>
public sealed class NvidiaMonitorFactAttribute : FactAttribute
{
    public const string Variable = "NEONSIDEKICK_TEST_NVIDIA_MONITOR";

    public NvidiaMonitorFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "NVIDIA's driver API is read on Windows only.";
        }
        else if (Environment.GetEnvironmentVariable(Variable) != "1")
        {
            Skip = $"{Variable} is not 1 (a monitor off the desktop on an NVIDIA card).";
        }
    }
}

/// <summary>
/// The NVIDIA path on this PC (2026-10-27, the NVIDIA plan): every display NVIDIA lists answers its input over I2C, and a monitor connected
/// but off the desktop has its input and capabilities read through NVIDIA's driver. Reads only: no input switched, no layout changed.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class LiveNvidiaMonitorTests
{
    [NvidiaMonitorFact]
    public void EveryNvidiaDisplay_IsListed_AndAnswersItsInputOverI2c()
    {
        var (status, displays) = NvidiaDdc.List();
        Assert.Equal(NvapiNative.Ok, status);
        Assert.NotEmpty(displays);
        Assert.All(displays, d => Assert.NotNull(d.PnpId));
        Assert.All(displays, d => Assert.Equal(16, d.Adapter.Length));
        foreach (var display in displays)
        {
            var (read, input) = NvidiaDdc.ReadOnce(display);
            Assert.Equal(NvapiNative.Ok, read);
            Assert.NotNull(input);
        }
    }

    [NvidiaMonitorFact]
    public void AMonitorOffTheDesktop_IsMatchedAndRead()
    {
        var (status, displays) = NvidiaDdc.List();
        Assert.Equal(NvapiNative.Ok, status);
        var system = new WindowsMonitorSystem();
        var off = system.Query().Monitors.Where(m => !m.Active).ToList();
        Assert.True(off.Count > 0, "No monitor is off the desktop: take one off (Only the other, or Win+P) and run this again.");
        Assert.Contains(displays, d => !d.Active && d.PnpId is not null);
        var m = off[0];
        byte input = system.CurrentInput(m.Target);
        Assert.NotEqual((byte)0, input);
        var caps = system.Capabilities(m.Target);
        Assert.Contains(input, caps.Inputs);
    }
}
