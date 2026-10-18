using NeonSidekick.Files;

namespace NeonSidekick.Audits;

/// <summary>
/// The code audit's one path form (2026-10-18): a finding's file, a scope file, a coverage line and a <c>covered</c> entry are each resolved
/// through the sandbox (so <c>src\x.cs</c>, <c>./src/x.cs</c>, <c>src/x.cs</c> and a full path inside it are one file), made
/// sandbox-relative and <c>/</c>-separated, so a report written on the PC reads the same on a Mac. Compared with
/// <see cref="WorkingDirectory.PathComparer"/>: case-blind where the file system is.
/// </summary>
public static class AuditPath
{
    /// <summary>The comparer every audit path is compared with.</summary>
    public static StringComparer Comparer => WorkingDirectory.PathComparer;

    /// <summary>
    /// <paramref name="path"/> in the one form, or null with <paramref name="outcome"/> saying why (outside the sandbox). The root itself is
    /// the empty string.
    /// </summary>
    public static string? Of(WorkingDirectory files, string path, out FileOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(path);
        outcome = files.Resolve(path.Trim(), forWrite: false, out string full);
        return outcome == FileOutcome.Ok ? FromRelative(files.Relative(full)) : null;
    }

    /// <summary>A sandbox-relative path (either separator) in the one form: <c>/</c>-separated, no leading <c>./</c>, no trailing separator.</summary>
    public static string FromRelative(string relative)
    {
        ArgumentNullException.ThrowIfNull(relative);
        string path = relative.Trim().Replace('\\', '/');
        while (path.StartsWith("./", StringComparison.Ordinal))
        {
            path = path[2..];
        }

        return path.TrimEnd('/');
    }

    /// <summary>Whether <paramref name="path"/> (the one form) is the app's own folder or under it: <c>.neon</c>, never audited.</summary>
    public static bool InAppFolder(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string folder = Plans.PlanSlug.AppFolder;
        return Comparer.Equals(path, folder) || path.StartsWith(folder + "/", OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    /// <summary>Whether <paramref name="path"/> is <paramref name="folder"/> or lies under it (the one form; an empty folder is the root, holding everything).</summary>
    public static bool Under(string path, string folder)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(folder);
        return folder.Length == 0 || Comparer.Equals(path, folder)
            || path.StartsWith(folder + "/", OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    /// <summary>A path as a scope word: double-quoted when it holds a blank, so <c>PrintCommand.Words</c> reads it back whole.</summary>
    public static string Word(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path.Any(char.IsWhiteSpace) ? "\"" + path + "\"" : path;
    }
}
