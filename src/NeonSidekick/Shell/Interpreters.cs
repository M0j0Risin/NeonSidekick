using NeonSidekick.Diagnostics;

namespace NeonSidekick.Shell;

/// <summary>
/// The PATH walk (2026-09-21), pure: <see cref="Find"/> tries every folder of a PATH string with
/// every extension of a PATHEXT string (the file name's own when it has one), first hit wins, the
/// way <c>where.exe</c> does — in process, no child, no <c>where.exe</c>. <paramref name="exists"/>
/// is the file test (the file system in the app, a set in tests) and <paramref name="skip"/> drops a
/// hit by its full path: the Store's <c>python.exe</c> stub under <c>WindowsApps</c>, the WSL launcher
/// <c>bash.exe</c> under <c>System32</c>.
/// </summary>
public static class InterpreterProbe
{
    /// <summary>The PATHEXT a machine without one gets: enough for every interpreter this app runs.</summary>
    public const string DefaultPathExt = ".COM;.EXE;.BAT;.CMD";

    /// <param name="windows">Windows' PATH rules (<c>;</c> between folders, PATHEXT's extensions tried) or Unix's (<c>:</c>, the name as
    /// it is); the running OS's when null (2026-10-06, the macOS build).</param>
    public static string? Find(string fileName, string? path, string? pathExt, Func<string, bool> exists, Func<string, bool>? skip = null, bool? windows = null)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(exists);
        bool onWindows = windows ?? OperatingSystem.IsWindows();
        var extensions = Path.HasExtension(fileName) || !onWindows
            ? [""]
            : (string.IsNullOrWhiteSpace(pathExt) ? DefaultPathExt : pathExt).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string folder in (path ?? "").Split(onWindows ? ';' : ':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string directory = folder.Trim('"');
            if (directory.Length == 0)
            {
                continue;
            }

            foreach (string extension in extensions)
            {
                string candidate;
                try
                {
                    // PATHEXT spells its extensions upper case; the file is found either way, and .exe reads better in a row.
                    candidate = Path.GetFullPath(Path.Combine(directory, fileName + extension.ToLowerInvariant()));
                }
                catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
                {
                    // A PATH entry that is not a path: skipped, as where.exe skips it.
                    continue;
                }

                if (exists(candidate) && !(skip?.Invoke(candidate) ?? false))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>The Store alias stub: a <c>python.exe</c> under <c>WindowsApps</c> that opens the Store or exits 9009 instead of running.</summary>
    public static bool IsStoreStub(string fullPath) =>
        fullPath.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase) || fullPath.Contains("/WindowsApps/", StringComparison.OrdinalIgnoreCase);

    /// <summary>The WSL launcher: the <c>bash.exe</c> under <c>System32</c> starts a Linux distribution, not Git Bash.</summary>
    public static bool IsWslLauncher(string fullPath) =>
        fullPath.Contains(@"\System32\", StringComparison.OrdinalIgnoreCase) || fullPath.Contains("/System32/", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Where the shells (and, from phase C, the script interpreters) are on this machine (2026-09-21):
/// one probe per kind over the PATH read through the one environment door
/// (<see cref="Settings.EnvironmentOverrides.System"/>), cached until <see cref="Refresh"/> — the
/// screen refreshes before each turn and when <c>/tools</c> opens, so an install during the session
/// shows without a restart, and a turn's schema and its run agree. <c>cmd.exe</c> and Windows
/// PowerShell are found under <see cref="Environment.SystemDirectory"/> whatever the PATH says, so
/// two shells are always there; <c>pwsh.exe</c> on the PATH is preferred for PowerShell; bash is
/// the PATH's <c>bash.exe</c> that is not the WSL launcher, else Git's own folders.
/// </summary>
public sealed class Interpreters
{
    private readonly Func<string, string?> _environment;
    private readonly Func<string, bool> _exists;
    private readonly object _lock = new();
    private Dictionary<ShellKind, string?>? _shells;
    private Dictionary<CodeLanguage, string?>? _languages;

    /// <param name="environment">Reads a system variable (<c>PATH</c>, <c>PATHEXT</c>, <c>ProgramFiles</c>); the app passes <see cref="Settings.EnvironmentOverrides.System"/>, tests a dictionary.</param>
    /// <param name="exists">The file test; <see cref="File.Exists"/> in the app.</param>
    public Interpreters(Func<string, string?> environment, Func<string, bool>? exists = null)
    {
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _exists = exists ?? File.Exists;
    }

    /// <summary>Forgets every cached path: the next <see cref="Locate"/> probes again.</summary>
    public void Refresh()
    {
        lock (_lock)
        {
            _shells = null;
            _languages = null;
        }
    }

    /// <summary>The executable for <paramref name="kind"/>, or null when it is not installed.</summary>
    public string? Locate(ShellKind kind)
    {
        lock (_lock)
        {
            _shells ??= Probe();
            return _shells.TryGetValue(kind, out var path) ? path : null;
        }
    }

    /// <summary>The interpreter for <paramref name="language"/>, or null when it is not installed: PowerShell as the shell's, python from the PATH (the Store stub skipped) else <c>py.exe</c>, node from the PATH; off Windows <c>python3</c> before <c>python</c>.</summary>
    public string? Locate(CodeLanguage language)
    {
        lock (_lock)
        {
            _languages ??= ProbeLanguages();
            return _languages.TryGetValue(language, out var path) ? path : null;
        }
    }

    /// <summary>The languages of <paramref name="wanted"/> whose interpreter is installed, in <see cref="CodeLanguages.Names"/> order.</summary>
    public IReadOnlyList<CodeLanguage> AvailableLanguages(IReadOnlyList<CodeLanguage> wanted)
    {
        ArgumentNullException.ThrowIfNull(wanted);
        var available = new List<CodeLanguage>(3);
        foreach (var language in new[] { CodeLanguage.PowerShell, CodeLanguage.Python, CodeLanguage.Node })
        {
            if (wanted.Contains(language) && Locate(language) is not null)
            {
                available.Add(language);
            }
        }

        return available;
    }

    private Dictionary<CodeLanguage, string?> ProbeLanguages()
    {
        string? path = _environment("PATH");
        string? pathExt = _environment("PATHEXT");
        var found = new Dictionary<CodeLanguage, string?>
        {
            [CodeLanguage.PowerShell] = Locate(ShellKind.PowerShell),
            [CodeLanguage.Python] = OperatingSystem.IsWindows()
                ? InterpreterProbe.Find("python", path, pathExt, _exists, InterpreterProbe.IsStoreStub) ?? InterpreterProbe.Find("py", path, pathExt, _exists)
                : InterpreterProbe.Find("python3", path, pathExt, _exists) ?? InterpreterProbe.Find("python", path, pathExt, _exists),
            [CodeLanguage.Node] = InterpreterProbe.Find("node", path, pathExt, _exists),
        };
        DiagnosticLog.Debug(ShellKinds.Category, "Interpreters: " + string.Join(", ", found.OrderBy(p => p.Key).Select(p => CodeLanguages.Name(p.Key) + "=" + (p.Value ?? "not found"))));
        return found;
    }

    /// <summary>The shells that are installed, in <see cref="ShellKinds.Names"/> order.</summary>
    public IReadOnlyList<ShellKind> AvailableShells()
    {
        var available = new List<ShellKind>(3);
        foreach (var kind in ShellKinds.Names.Select(name => ShellKinds.TryParse(name, out var k) ? k : ShellKind.PowerShell))
        {
            if (Locate(kind) is not null)
            {
                available.Add(kind);
            }
        }

        return available;
    }

    private Dictionary<ShellKind, string?> Probe()
    {
        string? path = _environment("PATH");
        string? pathExt = _environment("PATHEXT");
        if (!OperatingSystem.IsWindows())
        {
            // macOS (2026-10-06): zsh and bash from the PATH, else the system's own in /bin; pwsh only from the PATH; no cmd.
            var unix = new Dictionary<ShellKind, string?>
            {
                [ShellKind.Zsh] = InterpreterProbe.Find("zsh", path, pathExt, _exists) ?? First("/bin/zsh"),
                [ShellKind.Bash] = InterpreterProbe.Find("bash", path, pathExt, _exists) ?? First("/bin/bash"),
                [ShellKind.PowerShell] = InterpreterProbe.Find("pwsh", path, pathExt, _exists),
                [ShellKind.Cmd] = null,
            };
            DiagnosticLog.Debug(ShellKinds.Category, "Shells: " + string.Join(", ", unix.OrderBy(p => p.Key).Select(p => ShellKinds.Name(p.Key) + "=" + (p.Value ?? "not found"))));
            return unix;
        }

        string system = Environment.SystemDirectory;
        var found = new Dictionary<ShellKind, string?>
        {
            [ShellKind.Cmd] = First(Path.Combine(system, "cmd.exe")) ?? InterpreterProbe.Find("cmd", path, pathExt, _exists),
            [ShellKind.PowerShell] = InterpreterProbe.Find("pwsh", path, pathExt, _exists)
                ?? First(Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe"))
                ?? InterpreterProbe.Find("powershell", path, pathExt, _exists),
            [ShellKind.Bash] = InterpreterProbe.Find("bash", path, pathExt, _exists, InterpreterProbe.IsWslLauncher)
                ?? First(GitBashCandidates(_environment("ProgramFiles"), _environment("ProgramW6432"), _environment("LocalAppData"))),
        };
        DiagnosticLog.Debug(ShellKinds.Category, "Shells: " + string.Join(", ", found.OrderBy(p => p.Key).Select(p => ShellKinds.Name(p.Key) + "=" + (p.Value ?? "not found"))));
        return found;
    }

    /// <summary>Git for Windows' own bash, where the PATH does not name it: under Program Files or a per-user install.</summary>
    public static IReadOnlyList<string> GitBashCandidates(string? programFiles, string? programW6432, string? localAppData)
    {
        var candidates = new List<string>();
        foreach (string? root in new[] { programW6432, programFiles, localAppData is null ? null : Path.Combine(localAppData, "Programs") })
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            candidates.Add(Path.Combine(root, "Git", "bin", "bash.exe"));
            candidates.Add(Path.Combine(root, "Git", "usr", "bin", "bash.exe"));
        }

        return candidates;
    }

    private string? First(params IEnumerable<string> candidates)
    {
        foreach (string candidate in candidates)
        {
            if (_exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
