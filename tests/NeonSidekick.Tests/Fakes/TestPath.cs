namespace NeonSidekick.Tests.Fakes;

/// <summary>
/// The environment the screen and app fixtures give the interpreter probe (2026-10-06, the macOS build). On Windows nothing, as
/// before: PowerShell is found in System32 without a PATH. Off Windows a minimal <c>PATH</c> (<c>/usr/bin:/bin</c>), since there
/// every interpreter comes from the PATH: with none, <c>execute_code</c> was never offered and the fixtures' tool lists were the
/// real app's less one. <c>/usr/bin/python3</c> is the Xcode command line tools' (BUILD.md requires them); pwsh is not on it.
/// </summary>
public static class TestPath
{
    public const string Unix = "/usr/bin:/bin";

    /// <summary>The variable reader: <c>PATH</c> off Windows, nothing else anywhere.</summary>
    public static string? Read(string name) => !OperatingSystem.IsWindows() && name == "PATH" ? Unix : null;
}
