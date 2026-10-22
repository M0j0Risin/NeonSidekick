using System.Collections.Concurrent;

namespace NeonSidekick.Files;

/// <summary>
/// Whether names under a folder differ by case alone (2026-10-22, review G16, the fix plan's one answer for G16, G17, K14 and Q13): every
/// path compare assumed a case-blind volume, so on a case-sensitive one (an APFS volume made so, a Windows folder with
/// <c>fsutil file setCaseSensitiveInfo</c>, a Linux mount) <c>../FILES</c> counted as inside a root named <c>files</c>, and a copy onto
/// <c>A.txt</c> took a different <c>a.txt</c> for a rename. The volume is asked once per folder — Windows' per-folder flag
/// (<see cref="CaseRuleNative"/>, <c>GetFileInformationByHandleEx</c>'s <c>FileCaseSensitiveInfo</c>), a Mac's <c>pathconf(_PC_CASE_SENSITIVE)</c>
/// (<see cref="MacCaseRule"/>) — the nearest folder that exists standing for one still to be made; a folder the answer can't be had for is
/// its system's default (Windows and a Mac case-blind, anything else case-sensitive). Cached by the folder's spelling: a flag changed later
/// is seen at the next run.
/// </summary>
public static class CaseRule
{
    private static readonly ConcurrentDictionary<string, bool> s_sensitive = new(StringComparer.Ordinal);
    private static volatile Func<string, bool?>? s_probe;

    /// <summary>Whether names under <paramref name="folder"/> differ by case alone.</summary>
    public static bool IsSensitive(string folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        if (s_probe is { } probe)
        {
            return probe(folder) ?? Default;
        }

        return s_sensitive.GetOrAdd(folder, Ask);
    }

    /// <summary>How two paths under <paramref name="folder"/> compare.</summary>
    public static StringComparison Comparison(string folder) => IsSensitive(folder) ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    /// <summary>The comparer for paths under <paramref name="folder"/>.</summary>
    public static StringComparer Comparer(string folder) => IsSensitive(folder) ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    /// <summary>The tests' seam: <paramref name="probe"/> answers in place of the volume (null = the default) until the scope ends.</summary>
    public static IDisposable Override(Func<string, bool?> probe)
    {
        var previous = s_probe;
        s_probe = probe ?? throw new ArgumentNullException(nameof(probe));
        return new Restore(previous);
    }

    /// <summary>The system's own default: case-blind on Windows and a Mac, case-sensitive elsewhere.</summary>
    public static bool Default => !(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS());

    private static bool Ask(string folder)
    {
        string? existing = Nearest(folder);
        if (existing is null)
        {
            return Default;
        }

        bool? answer = null;
        if (OperatingSystem.IsWindows())
        {
            answer = CaseRuleNative.IsCaseSensitive(existing);
        }
        else if (OperatingSystem.IsMacOS())
        {
            answer = MacCaseRule.IsCaseSensitive(existing);
        }

        return answer ?? Default;
    }

    /// <summary>The folder itself or its nearest ancestor that exists; null when none does or the spelling is no path.</summary>
    private static string? Nearest(string folder)
    {
        try
        {
            string? current = Path.GetFullPath(folder);
            while (current is not null && !Directory.Exists(current))
            {
                current = Path.GetDirectoryName(current);
            }

            return current;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private sealed class Restore(Func<string, bool?>? previous) : IDisposable
    {
        public void Dispose() => s_probe = previous;
    }
}
