namespace NeonSidekick.Shell;

/// <summary>The file a run writes into its folder: the script.</summary>
/// <param name="ScriptName">The script's file name (<c>script.py</c>, <c>script.js</c>, <c>script.ps1</c>).</param>
/// <param name="ScriptText">The script's text: the code as sent, PowerShell's under the same wrapper as <c>run_command</c>.</param>
public sealed record CodeFiles(string ScriptName, string ScriptText);

/// <summary>
/// How an <c>execute_code</c> run is laid out and started (2026-09-21), pure over its inputs and
/// pinned by <c>ExecuteCodeToolTests</c>' layout tests. The script goes into a folder of its own per run and the
/// interpreter starts over it in the working directory, with no variables of its own: the script does
/// everything itself. PowerShell runs under the same wrapper as <c>run_command</c>
/// (<see cref="ShellCommandLine.PowerShellScript"/>) from a file, so 5.1's CLIXML never reaches the result
/// and the exit code follows the same rules.
/// The tool bridge's module (<c>neon_tools</c>, written beside the script with <c>Shell tool bridge</c> on) and its address
/// and token in the environment were retired on 2026-10-23 (the user's call); a script that imports it fails like any missing module.
/// </summary>
public static class CodeLaunch
{
    /// <summary>The script for <paramref name="code"/> under <paramref name="language"/>: the code as sent, PowerShell's under the bare wrapper.</summary>
    public static CodeFiles Files(CodeLanguage language, string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        return language switch
        {
            CodeLanguage.Python => new CodeFiles("script.py", code),
            CodeLanguage.Node => new CodeFiles("script.js", code),
            _ => new CodeFiles("script.ps1", ShellCommandLine.PowerShellScript(code)),
        };
    }

    /// <summary>The launch: the interpreter over the script in <paramref name="runFolder"/>, starting in <paramref name="workingDirectory"/>, with no environment of its own.</summary>
    public static ProcessLaunch For(CodeLanguage language, string executable, string runFolder, string scriptName, string workingDirectory, string label)
    {
        ArgumentNullException.ThrowIfNull(executable);
        ArgumentNullException.ThrowIfNull(runFolder);
        ArgumentNullException.ThrowIfNull(scriptName);
        string script = Path.Combine(runFolder, scriptName);
        string name = CodeLanguages.Name(language);
        return language switch
        {
            CodeLanguage.Python => new ProcessLaunch(executable, ["-X", "utf8", script], null, workingDirectory, label, name),
            CodeLanguage.Node => new ProcessLaunch(executable, [script], null, workingDirectory, label, name),
            _ => new ProcessLaunch(executable, [.. ShellCommandLine.PowerShellSwitches, "-File", script], null, workingDirectory, label, name),
        };
    }
}
