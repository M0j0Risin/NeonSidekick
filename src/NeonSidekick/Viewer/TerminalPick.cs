namespace NeonSidekick.Viewer;

/// <summary>A terminal app on a Mac: its bundle's path when it was found among the ancestors (null otherwise) and its bundle id.</summary>
public readonly record struct TerminalApp(string? BundlePath, string BundleId);

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

    /// <summary>The bundle <c>/terminal</c> opens on a Mac when none of the ancestors is one it knows (2026-10-07, the user's pick).</summary>
    public const string TerminalBundle = "com.apple.Terminal";

    /// <summary>iTerm2's bundle id, which <c>/terminal</c> opens when the app runs in it.</summary>
    public const string ITermBundle = "com.googlecode.iterm2";

    /// <summary>
    /// The app bundle an executable sits in (<c>/Applications/iTerm.app/Contents/MacOS/iTerm2</c> → <c>/Applications/iTerm.app</c>);
    /// null for one outside a bundle (<c>/usr/bin/login</c>, <c>-zsh</c>, iTerm2's <c>iTermServer</c> under Application Support).
    /// The innermost bundle, so a helper app inside another app is itself. Pure.
    /// </summary>
    public static string? AppBundleOf(string? executable)
    {
        const string inside = ".app/Contents/MacOS/";
        int at = executable?.LastIndexOf(inside, StringComparison.Ordinal) ?? -1;
        return at > 0 && executable![0] == '/' ? executable[..(at + 4)] : null;
    }

    /// <summary>
    /// <c>/usr/bin/open</c>'s arguments for <c>/terminal</c> on a Mac (2026-10-07, the user's ask): a new terminal in
    /// <paramref name="folder"/>, in the terminal the app runs in when that is Terminal or iTerm2, else Terminal. The terminal is
    /// found as TAB finds it — the first ancestor inside an app bundle (<paramref name="ancestorPaths"/>, the executables, parent
    /// first; under tmux or ssh the chain has none), opened by that bundle's path (<c>-a</c>), so a second copy elsewhere is not
    /// the one started; then <paramref name="termProgram"/> (<c>iTerm.app</c> → iTerm2) by bundle id (<c>-b</c>); else Terminal. A
    /// terminal app it does not know (VS Code's, Ghostty) opens Terminal rather than guess how that app takes a folder. The folder
    /// is its own argument, never text spliced into a command: measured on macOS 15.7, a folder named
    /// <c>odd dir/ä 'q"; $x &amp; (y)</c> opened as it is. What each does with it (measured too): Terminal opens a new window with
    /// its shell in the folder; iTerm2 opens a new tab in its front window there (the user's call: iTerm2's own choice, which its
    /// settings can change, rather than AppleScript). Pure.
    /// </summary>
    public static IReadOnlyList<string> OpenTerminalArguments(IReadOnlyList<string?> ancestorPaths, string? termProgram, string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        var app = AppFor(ancestorPaths, termProgram);
        return app.BundlePath is { } bundle ? ["-a", bundle, folder] : ["-b", app.BundleId, folder];
    }

    /// <summary>
    /// The terminal app <c>/terminal</c> opens and <c>/shortcut</c>'s <c>.command</c> file opens in (2026-10-08, the user's pick: the
    /// same rule for both): the first ancestor inside an app bundle when that is Terminal or iTerm2 (by its path, so a second copy
    /// elsewhere is not the one meant), Terminal for any other app; with no bundle among the ancestors (tmux, ssh),
    /// <paramref name="termProgram"/>'s iTerm2 or else Terminal, by bundle id. Pure.
    /// </summary>
    public static TerminalApp AppFor(IReadOnlyList<string?> ancestorPaths, string? termProgram)
    {
        ArgumentNullException.ThrowIfNull(ancestorPaths);
        foreach (string? path in ancestorPaths)
        {
            if (AppBundleOf(path) is { } bundle)
            {
                return Path.GetFileName(bundle) switch
                {
                    "Terminal.app" => new TerminalApp(bundle, TerminalBundle),
                    "iTerm.app" => new TerminalApp(bundle, ITermBundle),
                    _ => new TerminalApp(null, TerminalBundle),
                };
            }
        }

        return new TerminalApp(null, BundleFor(termProgram) == ITermBundle ? ITermBundle : TerminalBundle);
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
