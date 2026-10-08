using System.Text;
using NeonSidekick.App;

namespace NeonSidekick.Shortcuts;

/// <summary>
/// What one desktop shortcut holds: the <c>.lnk</c> it is written to, the program it starts, that program's command line,
/// the folder it starts in (Explorer's "Start in") and its tooltip. The icon is the target's own first one.
/// </summary>
public sealed record ShortcutSpec(string LinkPath, string Target, string Arguments, string WorkingDirectory, string Description);

/// <summary>What a <c>/shortcut</c> line asked for: a profile by name (null = the loaded one) and whether the launch logs.</summary>
public readonly record struct ShortcutRequest(string? Profile, bool Log);

/// <summary>
/// <c>/shortcut [profile] [--log]</c>'s decisions (2026-10-07, the user's ask: a desktop shortcut that launches the app on a
/// profile), pure so every system's tests hold them: the line read, the shortcut's name, its command line and its log path.
/// The user's picks the same day: it starts in the exe's own folder, it is called <c>NeonSidekick (&lt;profile&gt;)</c> with or
/// without the log (one already there replaced), and the log is <c>logs\neon-{ts}.log</c> beside the exe — written absolute and
/// quoted, so it holds even if the shortcut's Start-in is edited; <c>{ts}</c> stays as typed and
/// <see cref="SidekickOptions.WithLogStamp"/> stamps it at each launch (<c>DiagnosticFileSink</c> makes the folder).
/// </summary>
public static class DesktopShortcut
{
    /// <summary>The switch that adds the log to the shortcut, as <c>--log</c> on the app's own command line. Pinned.</summary>
    public const string LogSwitch = SidekickOptions.LogFlag;

    /// <summary>The folder beside the exe the log goes into. Pinned.</summary>
    public const string LogFolder = "logs";

    /// <summary>The log's file name, <see cref="SidekickOptions.LogStampToken"/> left for the launch to stamp. Pinned.</summary>
    public const string LogFileName = "neon-" + SidekickOptions.LogStampToken + ".log";

    /// <summary>The shortcut's file name on the desktop: <c>NeonSidekick (samuel).lnk</c>. Pinned.</summary>
    public static string LinkName(string profile) => "NeonSidekick (" + profile + ").lnk";

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

    /// <summary>The log path the shortcut passes: <c>&lt;exe folder&gt;\logs\neon-{ts}.log</c>.</summary>
    public static string LogPath(string exeDirectory) => Path.Combine(exeDirectory, LogFolder, LogFileName);

    /// <summary>
    /// The shortcut's command line: <c>--profile samuel</c>, then <c>--log "D:\x\logs\neon-{ts}.log"</c> when there is a log.
    /// A profile name is letters, digits, <c>-</c> and <c>_</c> (<c>Profiles.IsValidName</c>), so only the path is quoted.
    /// </summary>
    public static string Arguments(string profile, string? logPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profile);
        string arguments = SidekickOptions.ProfileFlag + " " + profile;
        return logPath is null ? arguments : arguments + " " + SidekickOptions.LogFlag + " " + Quote(logPath);
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

    /// <summary>The shortcut for <paramref name="profile"/> (as the profile list spells it) to <paramref name="exePath"/>, written to <paramref name="desktopFolder"/>.</summary>
    public static ShortcutSpec For(string exePath, string desktopFolder, string profile, bool log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(desktopFolder);
        exePath = Path.GetFullPath(exePath);
        string exeDirectory = Path.GetDirectoryName(exePath) ?? exePath;
        return new ShortcutSpec(
            Path.Combine(desktopFolder, LinkName(profile)),
            exePath,
            Arguments(profile, log ? LogPath(exeDirectory) : null),
            exeDirectory,
            ShortcutText.Description(profile));
    }
}
