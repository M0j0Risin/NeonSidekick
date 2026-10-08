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

    /// <summary>The error where the screen has no shortcut writer (a build for neither Windows nor a Mac). Pinned.</summary>
    public const string NotHere = "/shortcut needs Windows or a Mac: it writes a Windows shortcut (.lnk) or a Mac .command file.";

    /// <summary>The error where the running program's own path is unknown. Pinned.</summary>
    public const string NoOwnExecutable = "The app's own executable path is unknown, so there is nothing for a shortcut to start.";

    /// <summary>The error where the system refused the shortcut.</summary>
    public static string WriteFailed(string linkPath, string detail) => $"The desktop shortcut {linkPath} was not written: {detail}";

    /// <summary>A <c>.command</c> file's comment after its description (2026-10-08). Pinned.</summary>
    public const string ScriptNote = "Written by /shortcut; double-click it in Finder. /shortcut writes it again.";

    /// <summary>What a <c>.command</c> file says when its exe has gone, the path after it. Pinned.</summary>
    public const string ScriptGone = "NeonSidekick is no longer at";

    /// <summary>The <c>.command</c> file's wait after <see cref="ScriptGone"/>. Pinned.</summary>
    public const string ScriptPressAKey = "Run /shortcut again from the copy you keep. Press any key to close.";

    /// <summary>The diagnostic line where a <c>.command</c> file's icon was not set (it still works, with Finder's script icon).</summary>
    public static string IconNotSet(string linkPath) => $"{linkPath} keeps Finder's own icon: the app's icon could not be set.";

    /// <summary>The diagnostic line naming the terminal a <c>.command</c> file opens in, or that it was left to Finder's default.</summary>
    public static string OpensIn(string linkPath, string? terminal) => terminal is null
        ? $"{linkPath} opens in the terminal Finder picks for .command files: the terminal app was not found."
        : $"{linkPath} opens in {terminal}.";

    /// <summary>The argument list's notes: a profile, the loaded one, the log switch.</summary>
    public const string ProfileNote = "a shortcut to this profile";
    public const string LoadedProfileNote = "the loaded profile";
    public const string LogNote = "and log to logs beside the exe";

    /// <summary>The shortcut's tooltip (its Comment). Pinned.</summary>
    public static string Description(string profile) => "NeonSidekick on profile " + profile;

    /// <summary>The diagnostic line for a written shortcut.</summary>
    public static string LogLine(ShortcutSpec spec) => $"Wrote {spec.LinkPath}: \"{spec.Target}\" {spec.Arguments} (starts in {spec.WorkingDirectory})";
}
