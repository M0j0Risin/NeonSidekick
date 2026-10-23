using System.Globalization;

namespace NeonSidekick.Monitors;

/// <summary>
/// A Win32 code from the CCD API or dxva2 as the user's sentence (2026-10-23, the monitors plan, <c>ServiceErrors</c>' shape). The DDC/CI
/// failures (the <c>ERROR_GRAPHICS_I2C_*</c>/<c>DDCCI_*</c>/<c>MCA_*</c> family, 0xC0262580 on) read as <see cref="MonitorText.NoDdc"/>, which
/// says the commonest cause: DDC/CI turned off in the monitor's own menu. Two read differently: <c>ERROR_GRAPHICS_DDCCI_VCP_NOT_SUPPORTED</c>
/// (the monitor answers but not VCP 0x60) is <see cref="MonitorText.NoInputSwitch"/>, and <c>ERROR_GRAPHICS_ONLY_CONSOLE_SESSION_SUPPORTED</c>
/// (a remote session) is <see cref="MonitorText.OnlyFromDesktop"/>, as access denied is. The spike saw 0xC0262589
/// (<c>DDCCI_INVALID_MESSAGE_COMMAND</c>) for ~250 ms after every input switch: <see cref="MonitorCalls"/>' one retry, 300 ms on, rides it out.
/// Pure.
/// </summary>
public static class MonitorErrors
{
    public const int AccessDenied = 5;
    public const int NotSupported = 50;
    public const int BadConfiguration = 1610;
    public const int GenFailure = 31;
    public const int InvalidParameter = 87;

    /// <summary>The first of the DDC/CI family (<c>ERROR_GRAPHICS_I2C_NOT_SUPPORTED</c>).</summary>
    public const int DdcFirst = unchecked((int)0xC0262580);

    /// <summary>The last of it (<c>ERROR_GRAPHICS_ONLY_CONSOLE_SESSION_SUPPORTED</c>).</summary>
    public const int DdcLast = unchecked((int)0xC02625E0);

    public const int VcpNotSupported = unchecked((int)0xC0262584);
    public const int OnlyConsoleSession = DdcLast;

    /// <summary>Whether <paramref name="code"/> is one of the DDC/CI family.</summary>
    public static bool IsDdc(int code) => unchecked((uint)code >= (uint)DdcFirst && (uint)code <= (uint)DdcLast);

    /// <summary>The sentence for <paramref name="code"/>; <paramref name="detail"/> (the system's own message) goes into the fallback only.</summary>
    public static string Sentence(int code, string detail = "") => code switch
    {
        AccessDenied or OnlyConsoleSession => MonitorText.OnlyFromDesktop,
        VcpNotSupported => MonitorText.NoInputSwitch,
        BadConfiguration => MonitorText.BadConfiguration,
        NotSupported => MonitorText.NotSupported,
        _ when IsDdc(code) => MonitorText.NoDdc,
        _ => MonitorText.Failed("code 0x" + code.ToString("X8", CultureInfo.InvariantCulture) + (detail.Length > 0 ? ", " + detail : "")),
    };
}
