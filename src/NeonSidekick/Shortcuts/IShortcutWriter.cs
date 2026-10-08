namespace NeonSidekick.Shortcuts;

/// <summary>
/// Writes <c>/shortcut</c>'s desktop shortcut (2026-10-07): <see cref="WindowsShortcutWriter"/> on Windows,
/// <see cref="MacShortcutWriter"/> on a Mac (2026-10-08), a fake in the tests, none elsewhere (Program passes null and
/// <c>/shortcut</c> says it needs Windows or a Mac).
/// </summary>
public interface IShortcutWriter
{
    /// <summary>The kind of shortcut it makes: a <c>.lnk</c> on Windows, a <c>.command</c> file on a Mac.</summary>
    ShortcutKind Kind { get; }

    /// <summary>The desktop folder the shortcut goes into.</summary>
    string DesktopFolder { get; }

    /// <summary>The program a shortcut starts: the running exe, or null when its path is unknown.</summary>
    string? Executable { get; }

    /// <summary>Writes <paramref name="spec"/>, replacing a shortcut already at its path. Throws when it could not.</summary>
    void Write(ShortcutSpec spec);
}
