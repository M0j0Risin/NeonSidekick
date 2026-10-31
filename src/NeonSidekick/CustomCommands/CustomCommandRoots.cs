using NeonSidekick.Files;
using NeonSidekick.Settings;

namespace NeonSidekick.CustomCommands;

/// <summary>
/// The two <c>commands</c> folders a scan reads (2026-10-30): the loaded profile's, then the home's, the profile's file winning a name both
/// hold (the skills' and ComfyUI's two-root rule). Either may be a link to another folder (Claude Code's <c>~/.claude/commands</c>, shared), and
/// is followed; a link below it is not.
/// </summary>
public sealed record CustomCommandRoots(string Profile, string Global)
{
    /// <summary>The folder's name under a profile and under the home.</summary>
    public const string DirectoryName = "commands";

    /// <summary>No folders: a scan built by hand (<see cref="CustomCommandScan.Empty"/>).</summary>
    public static readonly CustomCommandRoots None = new("", "");

    /// <summary>The loaded profile's two folders.</summary>
    public static CustomCommandRoots For(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new CustomCommandRoots(settings.ProfileCustomCommandsDirectory, settings.GlobalCustomCommandsDirectory);
    }

    /// <summary>The folder of <paramref name="scope"/>.</summary>
    public string Of(CustomCommandScope scope) => scope == CustomCommandScope.Profile ? Profile : Global;

    /// <summary>Whether both folders are one (both linked to the same place): then the global one is not read again.</summary>
    public bool Same => SameFolder(Profile, Global);

    /// <summary>
    /// Whether <paramref name="a"/> and <paramref name="b"/> are one folder once links are followed (<see cref="WorkingDirectory.RealLinkTarget"/>),
    /// compared by the volume's case rule (<see cref="CaseRule"/>). Also the profile copy's check, so two profiles linked to one folder never
    /// copy a file onto itself.
    /// </summary>
    public static bool SameFolder(string a, string b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.Length == 0 || b.Length == 0)
        {
            return false;
        }

        string left = Real(a);
        string right = Real(b);
        return string.Equals(left, right, CaseRule.Comparison(left));
    }

    // The folder's full path, a link's final target in its place.
    private static string Real(string path)
    {
        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }

        return WorkingDirectory.RealLinkTarget(full) is { Length: > 0 } target ? Path.TrimEndingDirectorySeparator(target) : full;
    }
}
