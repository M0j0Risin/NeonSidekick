using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;

namespace NeonSidekick.Shell;

/// <summary>The shell a <c>run_command</c> line runs in (2026-09-21).</summary>
public enum ShellKind
{
    /// <summary><c>pwsh.exe</c> when installed, else Windows PowerShell 5.1: the default.</summary>
    PowerShell,

    /// <summary><c>cmd.exe</c>: batch syntax, <c>dir</c>, <c>set</c>, redirects the old way.</summary>
    Cmd,

    /// <summary>Git Bash (or any <c>bash.exe</c> on the PATH that is not the WSL launcher): offered only when found. On macOS the PATH's bash, else <c>/bin/bash</c>.</summary>
    Bash,

    /// <summary>zsh (2026-10-06, the macOS build): macOS's own shell and its default there; not offered on Windows.</summary>
    Zsh,
}

/// <summary>
/// The setting <c>Shell default</c> (2026-09-21): the three words the operator picks from
/// (<c>powershell</c>, <c>cmd</c>, <c>bash</c>) and their mapping to <see cref="ShellKind"/>, the
/// <see cref="Web.NetworkMode"/> shape. <see cref="Resolve"/> is the one place the saved string
/// becomes the enum: a hand-edited value that is none of them falls back to <see cref="Default"/>
/// with a warning. The same words are the <c>shell</c> argument's enum.
///
/// <para>The words are the running OS's (2026-10-06, the macOS build): Windows keeps its three; macOS has <c>zsh</c>, <c>bash</c>
/// and <c>powershell</c> (pwsh, when installed), cmd being Windows' alone. <see cref="PlatformDefault"/> is the default where the
/// app runs (<c>zsh</c> on macOS); <see cref="Default"/> stays Windows' word, the one the tests pin.</para>
/// </summary>
public static class ShellKinds
{
    /// <summary>PowerShell. The compiled default on Windows, pinned by <c>AppSettingsTests</c>.</summary>
    public const string Default = "powershell";

    /// <summary>zsh: the compiled default off Windows.</summary>
    public const string UnixDefault = "zsh";

    /// <summary>The default shell where the app runs: <see cref="Default"/> on Windows, <see cref="UnixDefault"/> elsewhere.</summary>
    public static string PlatformDefault => OperatingSystem.IsWindows() ? Default : UnixDefault;

    /// <summary>Windows' shells in menu order.</summary>
    public static readonly string[] WindowsNames = { "powershell", "cmd", "bash" };

    /// <summary>macOS's shells in menu order.</summary>
    public static readonly string[] UnixNames = { "zsh", "bash", "powershell" };

    /// <summary>The shells in menu order, the running OS's.</summary>
    public static readonly string[] Names = OperatingSystem.IsWindows() ? WindowsNames : UnixNames;

    /// <summary>The log category of every shell line: the runner's, the gate's, the tools'.</summary>
    public const string Category = "Shell";

    /// <summary>Trims and ignores case; false (and <see cref="ShellKind.PowerShell"/>) for anything that is not one of <see cref="Names"/>.</summary>
    public static bool TryParse(string? text, out ShellKind kind)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "powershell": kind = ShellKind.PowerShell; return true;
            case "cmd": kind = ShellKind.Cmd; return true;
            case "bash": kind = ShellKind.Bash; return true;
            case "zsh": kind = ShellKind.Zsh; return true;
            default: kind = ShellKind.PowerShell; return false;
        }
    }

    /// <summary>The saved word for <paramref name="kind"/>.</summary>
    public static string Name(ShellKind kind) => kind switch
    {
        ShellKind.Cmd => "cmd",
        ShellKind.Bash => "bash",
        ShellKind.Zsh => "zsh",
        _ => "powershell",
    };

    /// <summary>The menu hint next to a shell, the running OS's. Pinned.</summary>
    public static string Describe(string name) => OperatingSystem.IsWindows()
        ? name switch
        {
            "powershell" => "pwsh when installed, else Windows PowerShell 5.1",
            "cmd" => "cmd.exe: batch syntax",
            "bash" => "Git Bash, when bash.exe is found",
            _ => "",
        }
        : name switch
        {
            "zsh" => "the macOS shell",
            "bash" => "bash from the PATH, else /bin/bash",
            "powershell" => "pwsh, when installed",
            _ => "",
        };

    /// <summary>The executable's file name, for the not-installed sentence and the log: <c>powershell.exe</c>, <c>cmd.exe</c>, <c>bash.exe</c> on Windows; <c>pwsh</c>, <c>bash</c>, <c>zsh</c> elsewhere.</summary>
    public static string FileName(ShellKind kind) => OperatingSystem.IsWindows()
        ? kind switch
        {
            ShellKind.Cmd => "cmd.exe",
            ShellKind.Bash => "bash.exe",
            ShellKind.Zsh => "zsh.exe",
            _ => "powershell.exe",
        }
        : kind switch
        {
            ShellKind.Cmd => "cmd",
            ShellKind.Bash => "bash",
            ShellKind.Zsh => "zsh",
            _ => "pwsh",
        };

    /// <summary>The shell in force for <paramref name="effective"/>; an unknown saved value, or one this OS has not (a profile from Windows naming cmd on a Mac), warns and uses <see cref="PlatformDefault"/>.</summary>
    public static ShellKind Resolve(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        if (TryParse(effective.ShellDefault, out var kind) && Names.Contains(Name(kind)))
        {
            return kind;
        }

        DiagnosticLog.Warn(Category,
            $"{nameof(AppSettingsData.ShellDefault)}='{effective.ShellDefault}' is not one of {string.Join(", ", Names)}. Using {PlatformDefault}.");
        TryParse(PlatformDefault, out kind);
        return kind;
    }
}
