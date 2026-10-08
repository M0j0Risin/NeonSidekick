namespace NeonSidekick.Tests;

/// <summary>
/// A fact whose subject is Windows-only for now (2026-10-06, the macOS build): DPAPI and Credential Manager, WIC through
/// MagicScaler, UNC paths, drive-letter spellings, cmd/powershell command lines. Skipped elsewhere; on Windows a plain fact.
/// A test moves off it when its subject gets a macOS backend (or a Unix twin of the test is written beside it).
/// </summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public const string SkipReason = "Windows-only for now (the macOS build leaves this feature out).";

    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = SkipReason;
        }
    }
}

/// <summary>A fact about the Unix side of a rule (2026-10-06, the macOS build): Unix paths, a Unix shell. Skipped on Windows.</summary>
public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "Unix-only: the macOS build's side of the rule.";
        }
    }
}

/// <summary>The theory twin of <see cref="UnixFactAttribute"/>.</summary>
public sealed class UnixTheoryAttribute : TheoryAttribute
{
    public UnixTheoryAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "Unix-only: the macOS build's side of the rule.";
        }
    }
}

/// <summary>The theory twin of <see cref="WindowsFactAttribute"/>.</summary>
public sealed class WindowsTheoryAttribute : TheoryAttribute
{
    public WindowsTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = WindowsFactAttribute.SkipReason;
        }
    }
}

/// <summary>
/// A fact about a macOS backend (2026-10-07, pictures through ImageIO): a Mac twin of a Windows-only test that pins WIC's exact
/// output, checking what matters there instead. Skipped elsewhere.
/// </summary>
public sealed class MacFactAttribute : FactAttribute
{
    public const string SkipReason = "macOS-only: the Mac backend's side of the rule.";

    public MacFactAttribute()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Skip = SkipReason;
        }
    }
}

/// <summary>The theory twin of <see cref="MacFactAttribute"/>.</summary>
public sealed class MacTheoryAttribute : TheoryAttribute
{
    public MacTheoryAttribute()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Skip = MacFactAttribute.SkipReason;
        }
    }
}

/// <summary>
/// A fact about a system with no picture codecs (2026-10-07): neither WIC nor ImageIO, so the picture tools are not offered. The
/// Unix twins that pinned the macOS build's counts before it had ImageIO moved here; on Windows and a Mac the original runs.
/// </summary>
public sealed class NoPictureCodecsFactAttribute : FactAttribute
{
    public NoPictureCodecsFactAttribute()
    {
        if (Files.ImageCodecs.Available)
        {
            Skip = "This system has picture codecs; the original test runs instead.";
        }
    }
}

/// <summary>
/// A fact that needs a sound backend (2026-10-07, sound on a Mac): WinMM on Windows, AudioQueue on macOS. It runs there
/// with fakes behind the seams (<c>--audio-check</c>/<c>--voice-check</c> refuse up front where there is no backend);
/// skipped elsewhere.
/// </summary>
public sealed class AudioBackendFactAttribute : FactAttribute
{
    public AudioBackendFactAttribute()
    {
        if (!Audio.AudioSupport.Available)
        {
            Skip = Audio.AudioSupport.Unavailable;
        }
    }
}
