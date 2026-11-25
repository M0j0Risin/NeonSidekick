namespace NeonSidekick.Shell;

/// <summary>
/// The programs that run a file or a line they are given, in one place (2026-11-24, the session log review): the SQLite police's interpreters
/// and their options, the trusted files' shells, and each shell's switches before its script, read by <see cref="CommandPrefix"/>'s nested
/// shells. Not <c>Interpreters</c>: that is the class that finds <c>execute_code</c>'s interpreters.
/// </summary>
public static class ShellLaunchers
{
    /// <summary>
    /// The programs that run the file named after them (2026-10-22, review L4: a script was found by its extension alone, so
    /// <c>bash run</c> or <c>python job</c> ran a file no police read). Matched without case, a <c>.exe</c> off.
    /// </summary>
    public static readonly IReadOnlySet<string> Runners = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "python", "python3", "py", "pythonw", "node", "deno", "bun", "bash", "sh", "zsh", "dash", "ksh", "ruby", "perl", "php", "lua",
        "pwsh", "powershell", "Rscript", "dotnet-script", "cscript", "wscript", "call", "source", ".",
        // 2026-10-24 (review Q2): more shells and launchers that run a file named after them.
        "fish", "tclsh", "wish", "awk", "gawk", "julia", "ts-node", "tsx", "csi", "scriptcs", "osascript", "groovy", "kotlin", "elixir", "swift",
    };

    /// <summary>The runners' options whose next word is code or a module, never a script file (<c>-c</c>, <c>-e</c>, <c>-m</c>, <c>-Command</c>).</summary>
    public static readonly IReadOnlySet<string> InlineOptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "-c", "-e", "-p", "-m", "-r", "--eval", "--print", "-Command", "-EncodedCommand", "-ec", "-enc", "/c", "/k", "--command",
    };

    /// <summary>The runners' options that take a value before the script (2026-10-24, review Q2: <c>python -X utf8 job</c> ran <c>job</c> unread).</summary>
    public static readonly IReadOnlySet<string> ValueOptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "-X", "-W", "--require", "--import", "-ExecutionPolicy", "-ep", "-WindowStyle", "-ConfigurationName", "--allow-read", "--loader",
    };

    /// <summary>The programs that run the line they're given (2026-11-05, review P22-5): <c>cmd /c rd /s /q C:\Users\me</c> passed where <c>rm -rf C:\Users\me</c> didn't.</summary>
    public static readonly IReadOnlySet<string> ShellPrograms = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "cmd", "pwsh", "powershell", "bash", "sh", "zsh",
    };

    /// <summary>
    /// Whether <paramref name="program"/> (as <see cref="CommandPrefix.Program"/> writes it, its folder kept) is a shell by its file name
    /// (2026-11-24): <c>/bin/bash</c> and <c>c:/windows/system32/cmd</c> are, as <c>bash</c> and <c>cmd</c> are.
    /// </summary>
    public static bool IsShell(string program)
    {
        ArgumentNullException.ThrowIfNull(program);
        string name = CommandPrefix.Program(program);
        return ShellPrograms.Contains(name[(name.LastIndexOf('/') + 1)..]);
    }
}
