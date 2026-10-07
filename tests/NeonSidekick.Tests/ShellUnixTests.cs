using NeonSidekick.Settings;
using NeonSidekick.Shell;

namespace NeonSidekick.Tests;

/// <summary>The shells on macOS (2026-10-06, the macOS build): zsh as a kind, the Unix PATH walk, the per-OS words and default.</summary>
public sealed class ShellUnixTests
{
    [Fact]
    public void Zsh_ParsesAndRunsLikeBash()
    {
        Assert.True(ShellKinds.TryParse(" ZSH ", out var kind));
        Assert.Equal(ShellKind.Zsh, kind);
        Assert.Equal("zsh", ShellKinds.Name(ShellKind.Zsh));

        var launch = ShellCommandLine.For(ShellKind.Zsh, "echo hi", "/bin/zsh", "/Users/me/Project");
        Assert.Equal(["-lc", "echo hi"], launch.ArgumentList!);
    }

    [Fact]
    public void TheWords_AreTheRunningOs()
    {
        Assert.Equal(["powershell", "cmd", "bash"], ShellKinds.WindowsNames);
        Assert.Equal(["zsh", "bash", "powershell"], ShellKinds.UnixNames);
        Assert.Equal(OperatingSystem.IsWindows() ? ShellKinds.WindowsNames : ShellKinds.UnixNames, ShellKinds.Names);
        Assert.Equal(OperatingSystem.IsWindows() ? "powershell" : "zsh", ShellKinds.PlatformDefault);
        Assert.Equal(ShellKinds.PlatformDefault, new AppSettingsData().ShellDefault);
    }

    [Fact]
    public void Find_UnixRules_SplitOnColons_AndTryNoExtensions()
    {
        var tried = new List<string>();
        string? found = InterpreterProbe.Find("zsh", "/opt/homebrew/bin:/bin", "", path =>
        {
            tried.Add(path.Replace('\\', '/'));
            return path.Replace('\\', '/').EndsWith("/bin/zsh", StringComparison.Ordinal) && !path.Contains("homebrew", StringComparison.Ordinal);
        }, windows: false);

        Assert.NotNull(found);
        Assert.Equal(2, tried.Count);
        Assert.All(tried, path => Assert.EndsWith("/zsh", path));
    }

    [UnixFact]
    public void AWindowsWord_ReadsAsTheDefault_OffWindows()
    {
        Assert.Equal(ShellKind.Zsh, ShellKinds.Resolve(new AppSettingsData { ShellDefault = "cmd" }));
        Assert.Equal(ShellKind.Bash, ShellKinds.Resolve(new AppSettingsData { ShellDefault = "bash" }));
        Assert.Equal("pwsh", ShellKinds.FileName(ShellKind.PowerShell));
        Assert.Equal("python3", CodeLanguages.FileName(CodeLanguage.Python));
    }

    /// <summary>The Unix side of <c>ShellTests.ShellText_Errors_LogLines_AndThePane_ArePinned</c>' file names (2026-10-06): no <c>.exe</c>.</summary>
    [UnixFact]
    public void TheNotInstalledSentences_NameTheUnixFiles()
    {
        Assert.Equal("Error: bash is not installed (no bash found)", ShellText.ShellNotInstalled(ShellKind.Bash));
        Assert.Equal("Error: powershell is not installed (no pwsh found)", ShellText.ShellNotInstalled(ShellKind.PowerShell));
        Assert.Equal("Error: outside the working directory: '/etc/hosts' — a command or a script may only name paths under it", ShellText.OutsidePath("/etc/hosts"));
    }
}
