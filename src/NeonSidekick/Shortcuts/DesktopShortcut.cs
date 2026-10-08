using System.Text;
using NeonSidekick.App;

namespace NeonSidekick.Shortcuts;

/// <summary>
/// What one desktop shortcut holds: the <c>.lnk</c> it is written to, the program it starts, that program's command line,
/// the folder it starts in (Explorer's "Start in") and its tooltip. The icon is the target's own first one.
/// </summary>
public sealed record ShortcutSpec(string LinkPath, string Target, string Arguments, string WorkingDirectory, string Description);

/// <summary>
/// The kind of shortcut a writer makes: Windows' <c>.lnk</c>, or a Mac's <c>.command</c> file (2026-10-08, the user's ask), a zsh script
/// Finder runs in a terminal on a double-click.
/// </summary>
public enum ShortcutKind
{
    Lnk,
    Command,
}

/// <summary>What a <c>/shortcut</c> line asked for: a profile by name (null = the loaded one) and whether the launch logs.</summary>
public readonly record struct ShortcutRequest(string? Profile, bool Log);

/// <summary>
/// <c>/shortcut [profile] [--log]</c>'s decisions (2026-10-07, the user's ask: a desktop shortcut that launches the app on a
/// profile), pure so every system's tests hold them: the line read, the shortcut's name, its command line and its log path.
/// The user's picks the same day: it starts in the exe's own folder, it is called <c>NeonSidekick (&lt;profile&gt;)</c> with or
/// without the log (one already there replaced), and the log is <c>logs\neon-&lt;profile&gt;-{ts}.log</c> beside the exe (the profile in
/// the name since 2026-10-08, the user's ask, so the logs of shortcuts to different profiles tell apart) — written absolute and
/// quoted, so it holds even if the shortcut's Start-in is edited; <c>{ts}</c> stays as typed and
/// <see cref="SidekickOptions.WithLogStamp"/> stamps it at each launch (<c>DiagnosticFileSink</c> makes the folder). On a Mac
/// (2026-10-08) the same decisions make a <see cref="ShortcutKind.Command"/> file: its paths quoted for zsh
/// (<see cref="ShellQuote"/>) and its text <see cref="CommandScript"/>.
/// </summary>
public static class DesktopShortcut
{
    /// <summary>The switch that adds the log to the shortcut, as <c>--log</c> on the app's own command line. Pinned.</summary>
    public const string LogSwitch = SidekickOptions.LogFlag;

    /// <summary>The folder beside the exe the log goes into. Pinned.</summary>
    public const string LogFolder = "logs";

    /// <summary>
    /// The log's file name for <paramref name="profile"/> (as the profile list spells it): <c>neon-samuel-{ts}.log</c>,
    /// <see cref="SidekickOptions.LogStampToken"/> left for the launch to stamp. Pinned.
    /// </summary>
    public static string LogFileName(string profile) => "neon-" + profile + "-" + SidekickOptions.LogStampToken + ".log";

    /// <summary>The shortcut's file name on the desktop: <c>NeonSidekick (samuel).lnk</c>, or <c>.command</c> on a Mac. Pinned.</summary>
    public static string LinkName(string profile, ShortcutKind kind = ShortcutKind.Lnk) =>
        "NeonSidekick (" + profile + ")" + (kind == ShortcutKind.Command ? ".command" : ".lnk");

    /// <summary>
    /// Reads what follows <c>/shortcut</c>: <see cref="LogSwitch"/> anywhere (any case) and at most one other word, the profile.
    /// False for any other <c>-</c> word or a second name.
    /// </summary>
    public static bool TryParse(string args, out ShortcutRequest request)
    {
        request = default;
        string? profile = null;
        bool log = false;
        foreach (string word in (args ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(word, LogSwitch, StringComparison.OrdinalIgnoreCase))
            {
                log = true;
            }
            else if (word.StartsWith('-') || profile is not null)
            {
                return false;
            }
            else
            {
                profile = word;
            }
        }

        request = new ShortcutRequest(profile, log);
        return true;
    }

    /// <summary>The log path the shortcut passes: <c>&lt;exe folder&gt;\logs\neon-&lt;profile&gt;-{ts}.log</c>.</summary>
    public static string LogPath(string exeDirectory, string profile) => Path.Combine(exeDirectory, LogFolder, LogFileName(profile));

    /// <summary>
    /// The shortcut's command line: <c>--profile samuel</c>, then <c>--log "D:\x\logs\neon-samuel-{ts}.log"</c> when there is a log
    /// (<c>--log '/Users/x/logs/neon-samuel-{ts}.log'</c> in a <see cref="ShortcutKind.Command"/> file). A profile name is letters, digits,
    /// <c>-</c> and <c>_</c> (<c>Profiles.IsValidName</c>), so only the path is quoted.
    /// </summary>
    public static string Arguments(string profile, string? logPath, ShortcutKind kind = ShortcutKind.Lnk)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profile);
        string arguments = SidekickOptions.ProfileFlag + " " + profile;
        return logPath is null ? arguments : arguments + " " + SidekickOptions.LogFlag + " " + (kind == ShortcutKind.Command ? ShellQuote(logPath) : Quote(logPath));
    }

    /// <summary>
    /// <paramref name="value"/> as one zsh word: in single quotes, where nothing expands (<c>$</c>, <c>`</c>, <c>"</c>, <c>\</c>, <c>!</c>
    /// and <c>{ts}</c> stay as they are), each <c>'</c> closed, escaped and opened again (<c>'\''</c>). The quoting of a
    /// <see cref="ShortcutKind.Command"/> file's paths (2026-10-08). Pure.
    /// </summary>
    public static string ShellQuote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }

    /// <summary>
    /// A <see cref="ShortcutKind.Command"/> file's text (2026-10-08): zsh, its description as a comment, then — since Finder starts a
    /// <c>.command</c> file in the home folder — a move to the exe's folder (Windows' Start in) and <c>exec</c> of the exe with the
    /// shortcut's command line, so the terminal's process is the app itself. An exe that has gone (the copy moved or deleted) is said
    /// in the window, which waits for a key so the line is read before a terminal that closes on exit takes it away. Lines end in
    /// LF whatever the system. Pure.
    /// </summary>
    public static string CommandScript(ShortcutSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        string exe = ShellQuote(spec.Target);
        return string.Join('\n',
            "#!/bin/zsh",
            "# " + spec.Description + ". " + ShortcutText.ScriptNote,
            "if [[ ! -x " + exe + " ]]; then",
            "  print -r -- " + ShellQuote(ShortcutText.ScriptGone) + " " + exe,
            "  read -k1 " + ShellQuote("?" + ShortcutText.ScriptPressAKey),
            "  exit 1",
            "fi",
            "cd -- " + ShellQuote(spec.WorkingDirectory) + " || exit 1",
            "exec " + exe + " " + spec.Arguments,
            "");
    }

    /// <summary>
    /// <paramref name="value"/> in double quotes by the argv rules: a backslash run before a quote or the closing quote doubled
    /// (so <c>D:\</c> stays <c>D:\</c>), a quote escaped. The rule <c>PersonaFile.TerminalArguments</c> follows for its folder.
    /// </summary>
    public static string Quote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var quoted = new StringBuilder("\"");
        int backslashes = 0;
        foreach (char c in value)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            quoted.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes).Append(c);
            backslashes = 0;
        }

        return quoted.Append('\\', backslashes * 2).Append('"').ToString();
    }

    /// <summary>
    /// The shortcut for <paramref name="profile"/> (as the profile list spells it) to <paramref name="exePath"/>, written to
    /// <paramref name="desktopFolder"/> as the <paramref name="kind"/> the writer makes.
    /// </summary>
    public static ShortcutSpec For(string exePath, string desktopFolder, string profile, bool log, ShortcutKind kind = ShortcutKind.Lnk)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(desktopFolder);
        exePath = Path.GetFullPath(exePath);
        string exeDirectory = Path.GetDirectoryName(exePath) ?? exePath;
        return new ShortcutSpec(
            Path.Combine(desktopFolder, LinkName(profile, kind)),
            exePath,
            Arguments(profile, log ? LogPath(exeDirectory, profile) : null, kind),
            exeDirectory,
            ShortcutText.Description(profile));
    }
}
