using System.Text;

namespace NeonSidekick.Shell;

/// <summary>
/// How one command line becomes a child's arguments, per shell (2026-09-21), pure and pinned by
/// <c>ShellCommandLineTests</c>:
/// <list type="bullet">
/// <item><b>powershell</b>: <c>-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand …</c> — the
/// command wrapped by <see cref="PowerShellScript"/> and sent as base64 UTF-16LE, which sidesteps
/// every quoting problem <c>-Command</c> has with embedded quotes and <c>$</c>; the wrapper makes 5.1
/// write UTF-8 and plain text (never CLIXML) and turns the outcome into the exit code. A line over
/// <see cref="MaxCommandChars"/> is refused ahead of the 32 K command-line limit (base64 of UTF-16 is
/// 2⅔ chars per char).</item>
/// <item><b>cmd</b>: the raw string <c>/d /s /c "chcp 65001&gt;nul &amp; cmd /d /s /c "…""</c> — <c>/s</c>
/// strips exactly the outer quotes (the <c>DraftFile.CommandLine</c> precedent), at both levels. The
/// inner <c>cmd</c> is not decoration: <c>cmd.exe</c> reads the console's code page once, at its start,
/// to convert what <c>echo</c> and <c>dir</c> write, so a <c>chcp</c> in the same process leaves them on
/// the OEM page (an <c>ü</c> came out as the byte 0x81, which is not UTF-8); the inner one starts after
/// the <c>chcp</c> and writes UTF-8. The exit code is the inner <c>/c</c>'s, the last command's.</item>
/// <item><b>bash</b>: <c>-lc …</c> as a list — .NET's quoting is MSVCRT's, which is what MSYS bash
/// parses; <c>-l</c> costs a profile read but gives Git Bash its PATH (the user's call).</item>
/// <item><b>zsh</b> (2026-10-06, the macOS build): <c>-lc …</c> as a list the same way — on Unix the arguments reach the
/// child as they are, no quoting at all; <c>-l</c> reads <c>.zprofile</c>, where Homebrew puts itself on the PATH.</item>
/// </list>
/// </summary>
public static class ShellCommandLine
{
    /// <summary>The longest command line PowerShell takes here: base64 UTF-16 of the wrapper and this stays under the 32 767-char process limit with room to spare.</summary>
    public const int MaxCommandChars = 8000;

    /// <summary>The PowerShell switches ahead of the encoded command. Pinned.</summary>
    public static readonly IReadOnlyList<string> PowerShellSwitches = ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass"];

    /// <summary>
    /// What wraps the command in PowerShell (pinned; every line is load-bearing, 2026-09-21):
    /// progress off (5.1 serialises a progress record as CLIXML on a redirected stderr — "Preparing
    /// modules for first use" was the first thing every result carried), UTF-8 out (5.1's default is
    /// the OEM page), then the command inside a script block with <em>every</em> stream merged into the
    /// pipeline, so nothing reaches 5.1's CLIXML writer: an error record goes to the real stderr as
    /// its formatted text (and marks the run failed), a warning / information (<c>Write-Host</c>) /
    /// verbose / debug record becomes its line, everything else is formatted through
    /// <c>Out-String -Stream</c> at 200 columns (a hidden console is 80 wide and cuts tables) and
    /// written synchronously — the implicit <c>Out-Default</c> buffers a table until the pipeline ends,
    /// and the <c>exit</c> below would lose it. A terminating error is caught, written and exit 1. The
    /// exit code: a native command's own (<c>$LASTEXITCODE</c>), else 1 when an error record was seen,
    /// else 0; an <c>exit N</c> inside the command ends the script with N before these lines.
    /// </summary>
    public static string PowerShellScript(string command) =>
        "$ProgressPreference = 'SilentlyContinue'\n" +
        "[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false); $OutputEncoding = [Console]::OutputEncoding\n" +
        "$__ok = $true\n" +
        "try {\n" +
        "& {\n" +
        command + "\n" +
        "} *>&1 | ForEach-Object {\n" +
        "if ($_ -is [System.Management.Automation.ErrorRecord]) { $script:__ok = $false; [Console]::Error.WriteLine(($_ | Out-String).TrimEnd()) }\n" +
        "elseif ($_ -is [System.Management.Automation.WarningRecord]) { 'WARNING: ' + $_.Message }\n" +
        "elseif ($_ -is [System.Management.Automation.InformationRecord]) { [string]$_.MessageData }\n" +
        "elseif ($_ -is [System.Management.Automation.VerboseRecord]) { 'VERBOSE: ' + $_.Message }\n" +
        "elseif ($_ -is [System.Management.Automation.DebugRecord]) { 'DEBUG: ' + $_.Message }\n" +
        "else { $_ }\n" +
        "} | Out-String -Stream -Width 200 | ForEach-Object { [Console]::Out.WriteLine($_) }\n" +
        "} catch { [Console]::Error.WriteLine(($_ | Out-String).TrimEnd()); exit 1 }\n" +
        "if ($null -ne $LASTEXITCODE -and $LASTEXITCODE -ne 0) { exit $LASTEXITCODE }\n" +
        "if (-not $__ok) { exit 1 }\n" +
        "exit 0";

    /// <summary>The <c>cmd.exe</c> argument string: <c>/d /s /c "chcp 65001&gt;nul &amp; cmd /d /s /c "…""</c>. Pinned.</summary>
    public static string CmdArguments(string command) => "/d /s /c \"chcp 65001>nul & cmd /d /s /c \"" + command + "\"\"";

    /// <summary>The launch for <paramref name="command"/> under <paramref name="kind"/>, the shell at <paramref name="executable"/>, starting in <paramref name="workingDirectory"/>.</summary>
    public static ProcessLaunch For(ShellKind kind, string command, string executable, string workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(executable);
        ArgumentNullException.ThrowIfNull(workingDirectory);
        string name = ShellKinds.Name(kind);
        return kind switch
        {
            ShellKind.Cmd => new ProcessLaunch(executable, null, CmdArguments(command), workingDirectory, command, name),
            ShellKind.Bash or ShellKind.Zsh => new ProcessLaunch(executable, ["-lc", command], null, workingDirectory, command, name),
            _ => new ProcessLaunch(executable, [.. PowerShellSwitches, "-EncodedCommand", Encode(PowerShellScript(command))], null, workingDirectory, command, name),
        };
    }

    /// <summary>The base64 of the UTF-16LE bytes, what <c>-EncodedCommand</c> reads.</summary>
    public static string Encode(string script) => Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
}
