using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;

namespace NeonSidekick.Shell;

/// <summary>The interpreter an <c>execute_code</c> script runs under (2026-09-21).</summary>
public enum CodeLanguage
{
    /// <summary><c>pwsh.exe</c> when installed, else Windows PowerShell 5.1: always there on Windows.</summary>
    PowerShell,

    /// <summary><c>python.exe</c> on the PATH (the Store's stub skipped), else <c>py.exe</c>.</summary>
    Python,

    /// <summary><c>node.exe</c> on the PATH.</summary>
    Node,
}

/// <summary>
/// The setting <c>Shell code languages</c> (2026-09-21, the user's call: a multiple choice, one or
/// more): the three words the operator picks from (<c>powershell</c>, <c>python</c>, <c>node</c>) and
/// their mapping to <see cref="CodeLanguage"/>, the <see cref="ShellKinds"/> shape over a list.
/// <see cref="Resolve"/> is the one place the saved list becomes the set: an unknown word is dropped
/// with a warning, and a list that names nothing usable falls back to <see cref="Default"/> — the
/// editor never saves an empty one, so that is a hand-edited file. What is offered is the set's
/// languages whose interpreter is found (<see cref="Interpreters.AvailableLanguages"/>).
/// </summary>
public static class CodeLanguages
{
    /// <summary>The languages in menu order.</summary>
    public static readonly string[] Names = { "powershell", "python", "node" };

    /// <summary>All three. The compiled default, pinned by <c>AppSettingsTests</c>.</summary>
    public static readonly IReadOnlyList<string> Default = Names;

    /// <summary>Trims and ignores case; false (and <see cref="CodeLanguage.PowerShell"/>) for anything that is not one of <see cref="Names"/>.</summary>
    public static bool TryParse(string? text, out CodeLanguage language)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "powershell": language = CodeLanguage.PowerShell; return true;
            case "python": language = CodeLanguage.Python; return true;
            case "node": language = CodeLanguage.Node; return true;
            default: language = CodeLanguage.PowerShell; return false;
        }
    }

    /// <summary>The saved word for <paramref name="language"/>.</summary>
    public static string Name(CodeLanguage language) => language switch
    {
        CodeLanguage.Python => "python",
        CodeLanguage.Node => "node",
        _ => "powershell",
    };

    /// <summary>The menu hint next to a language, the running OS's (2026-10-06, the macOS build). Pinned.</summary>
    public static string Describe(string name) => OperatingSystem.IsWindows()
        ? name switch
        {
            "powershell" => "a .ps1 through pwsh or Windows PowerShell; Invoke-NeonTool calls a tool",
            "python" => "a .py through python.exe; from neon_tools import …",
            "node" => "a .js through node.exe; require('neon_tools')",
            _ => "",
        }
        : name switch
        {
            "powershell" => "a .ps1 through pwsh; Invoke-NeonTool calls a tool",
            "python" => "a .py through python3; from neon_tools import …",
            "node" => "a .js through node; require('neon_tools')",
            _ => "",
        };

    /// <summary>The executable's file name, for the not-installed sentence: <c>python.exe</c>, <c>node.exe</c>, <c>powershell.exe</c>; off Windows <c>python3</c>, <c>node</c>, <c>pwsh</c>.</summary>
    public static string FileName(CodeLanguage language) => OperatingSystem.IsWindows()
        ? language switch
        {
            CodeLanguage.Python => "python.exe",
            CodeLanguage.Node => "node.exe",
            _ => "powershell.exe",
        }
        : language switch
        {
            CodeLanguage.Python => "python3",
            CodeLanguage.Node => "node",
            _ => "pwsh",
        };

    /// <summary>The languages the saved list names, in <see cref="Names"/> order; an unknown word warns and is dropped, nothing usable falls back to <see cref="Default"/>.</summary>
    public static IReadOnlyList<CodeLanguage> Resolve(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        var set = new HashSet<CodeLanguage>();
        foreach (string word in effective.ShellCodeLanguages)
        {
            if (TryParse(word, out var language))
            {
                set.Add(language);
            }
            else
            {
                DiagnosticLog.Warn(ShellKinds.Category, $"{nameof(AppSettingsData.ShellCodeLanguages)} holds '{word}', which is not one of {string.Join(", ", Names)}; ignoring it.");
            }
        }

        if (set.Count == 0)
        {
            DiagnosticLog.Warn(ShellKinds.Category, $"{nameof(AppSettingsData.ShellCodeLanguages)} names no language; using {string.Join(", ", Default)}.");
            return [CodeLanguage.PowerShell, CodeLanguage.Python, CodeLanguage.Node];
        }

        return new[] { CodeLanguage.PowerShell, CodeLanguage.Python, CodeLanguage.Node }.Where(set.Contains).ToList();
    }
}
