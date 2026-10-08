using NeonSidekick.Shortcuts;

namespace NeonSidekick.Tests.Fakes;

/// <summary>
/// A shortcut writer over a folder of its own as the desktop (2026-10-07, <c>/shortcut</c>): each spec recorded and a stand-in file
/// written at its path, so a second write finds the first; <see cref="Failure"/> makes the next writes throw. <see cref="Kind"/> is the
/// writer's (a Mac's <c>.command</c> since 2026-10-08).
/// </summary>
public sealed class FakeShortcutWriter(string desktopFolder, string? executable, ShortcutKind kind = ShortcutKind.Lnk) : IShortcutWriter
{
    public ShortcutKind Kind { get; } = kind;

    public string DesktopFolder { get; } = desktopFolder;

    public string? Executable { get; set; } = executable;

    /// <summary>Every spec written, in order.</summary>
    public List<ShortcutSpec> Written { get; } = [];

    /// <summary>When set, what <see cref="Write"/> throws.</summary>
    public Exception? Failure { get; set; }

    public void Write(ShortcutSpec spec)
    {
        if (Failure is { } failure)
        {
            throw failure;
        }

        Directory.CreateDirectory(DesktopFolder);
        File.WriteAllText(spec.LinkPath, spec.Arguments);
        Written.Add(spec);
    }
}
