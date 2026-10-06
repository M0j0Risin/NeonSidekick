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
