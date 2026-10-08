using NeonSidekick.App;

namespace NeonSidekick.Shortcuts;

/// <summary><c>/shortcut</c>'s wording (2026-10-07): the notice, the errors, the tooltip and the log line. The <c>*Text.cs</c> shape. Pinned.</summary>
public static class ShortcutText
{
    public const string Category = "Shortcut";

    /// <summary>The line after a shortcut is written: its name, whether one was replaced, and the command line it runs.</summary>
    public static string Made(string linkName, string arguments, bool replaced) =>
        "(" + NoticeGlyphs.Shortcut + (replaced ? "replaced " : "put ") + Path.GetFileNameWithoutExtension(linkName) + " on the desktop: " + arguments + ")";

    /// <summary>A line <c>/shortcut</c> cannot read: a switch other than <c>--log</c>, or two names. Pinned.</summary>
    public const string Usage = "Usage: /shortcut [<profile>] [--log]";

    /// <summary>The error where the screen has no shortcut writer (a non-Windows build). Pinned.</summary>
    public const string NeedsWindows = "/shortcut needs Windows for now: it writes a Windows desktop shortcut (.lnk).";

    /// <summary>The error where the running program's own path is unknown. Pinned.</summary>
    public const string NoOwnExecutable = "The app's own executable path is unknown, so there is nothing for a shortcut to start.";

    /// <summary>The error where Windows refused the shortcut.</summary>
    public static string WriteFailed(string linkPath, string detail) => $"The desktop shortcut {linkPath} was not written: {detail}";

    /// <summary>The argument list's notes: a profile, the loaded one, the log switch.</summary>
    public const string ProfileNote = "a shortcut to this profile";
    public const string LoadedProfileNote = "the loaded profile";
    public const string LogNote = "and log to logs beside the exe";

    /// <summary>The shortcut's tooltip (its Comment). Pinned.</summary>
    public static string Description(string profile) => "NeonSidekick on profile " + profile;

    /// <summary>The diagnostic line for a written shortcut.</summary>
    public static string LogLine(ShortcutSpec spec) => $"Wrote {spec.LinkPath}: \"{spec.Target}\" {spec.Arguments} (starts in {spec.WorkingDirectory})";
}
