namespace NeonSidekick.Viewer;

/// <summary>
/// Which app TAB brings forward from the app's own windows on a Mac (2026-10-07, Stage 2 phase 4; <see cref="TerminalHandoff"/>'s
/// Mac side). Measured that day: <c>TERM_PROGRAM</c> said <c>Apple_Terminal</c> in a shell started from Terminal while iTerm2 was
/// the app in front, so the app in front at startup (Windows' fallback) would pick the wrong terminal, and a variable inherited from
/// another terminal can lie too. So the order is: the first of this process's ancestors that is an app with a Dock icon (the
/// terminal that started the shell that started the app: zsh → login → Terminal), then the app <c>TERM_PROGRAM</c> names (tmux or
/// ssh break the chain), then none (the window falls back to the app that was in front before it took the keyboard). It is the
/// terminal app that comes forward, not one of its windows. Pure.
/// </summary>
public static class TerminalPick
{
    /// <summary>The bundle ids of the terminals whose <c>TERM_PROGRAM</c> is known.</summary>
    public static readonly IReadOnlyDictionary<string, string> Bundles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Apple_Terminal"] = "com.apple.Terminal",
        ["iTerm.app"] = "com.googlecode.iterm2",
        ["vscode"] = "com.microsoft.VSCode",
        ["WezTerm"] = "com.github.wez.wezterm",
        ["ghostty"] = "com.mitchellh.ghostty",
        ["WarpTerminal"] = "dev.warp.Warp-Stable",
        ["Hyper"] = "co.zeit.hyper",
        ["Tabby"] = "org.tabby",
    };

    /// <summary>The bundle id <paramref name="termProgram"/> names, or null for one not known (or none). Pure.</summary>
    public static string? BundleFor(string? termProgram) =>
        termProgram is { Length: > 0 } name && Bundles.TryGetValue(name, out string? bundle) ? bundle : null;

    /// <summary>
    /// The first of <paramref name="ancestors"/> (the parent first, launchd last) that <paramref name="isApp"/> says is an app with a
    /// Dock icon; null when none is. Pure.
    /// </summary>
    public static int? FirstApp(IReadOnlyList<int> ancestors, Func<int, bool> isApp)
    {
        ArgumentNullException.ThrowIfNull(ancestors);
        ArgumentNullException.ThrowIfNull(isApp);
        foreach (int pid in ancestors)
        {
            if (pid > 1 && isApp(pid))
            {
                return pid;
            }
        }

        return null;
    }

    /// <summary>
    /// The ancestors of a process from a parent lookup (<paramref name="parentOf"/>: a pid's parent, or null when it cannot be read),
    /// parent first, stopping at launchd (1), a loop, or <paramref name="limit"/> steps. Pure.
    /// </summary>
    public static IReadOnlyList<int> Ancestors(int pid, Func<int, int?> parentOf, int limit = 32)
    {
        ArgumentNullException.ThrowIfNull(parentOf);
        var chain = new List<int>();
        var seen = new HashSet<int> { pid };
        int current = pid;
        while (chain.Count < limit && parentOf(current) is int parent && parent > 0 && seen.Add(parent))
        {
            chain.Add(parent);
            if (parent == 1)
            {
                break;
            }

            current = parent;
        }

        return chain;
    }
}
