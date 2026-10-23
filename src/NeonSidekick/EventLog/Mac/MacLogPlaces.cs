using System.Text.RegularExpressions;

namespace NeonSidekick.EventLog;

/// <summary>One folder of crash and diagnostic reports and who it belongs to (<c>user</c> or <c>system</c>), as the answers name it.</summary>
public sealed record MacCrashFolder(string Path, string Owner);

/// <summary>
/// Where a Mac keeps the logs that are files (2026-10-18, the Mac event-log plan's phase 3): the crash and diagnostic reports of the
/// account (<c>~/Library/Logs/DiagnosticReports</c>) and of the system (<c>/Library/Logs/DiagnosticReports</c>, readable by the
/// <c>_analyticsusers</c> group, which an administrator is in), each with its <c>Retired</c> folder, and <c>/var/log/install.log</c>
/// (world-readable). Read in place, never through the sandbox: they are fixed paths outside it, and the tools only read them. The home is
/// what a redacted answer writes as <c>~</c>. Tests point all three at a temporary folder.
/// </summary>
public sealed partial record MacLogPlaces(IReadOnlyList<MacCrashFolder> CrashFolders, string InstallLog, string Home)
{
    /// <summary>This account's places.</summary>
    public static MacLogPlaces Default => Under(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>The places for a home folder: its own reports, the system's, and the system's install.log.</summary>
    public static MacLogPlaces Under(string home) => new(
        [new MacCrashFolder(Path.Combine(home, "Library", "Logs", "DiagnosticReports"), "user"), new MacCrashFolder("/Library/Logs/DiagnosticReports", "system")],
        "/var/log/install.log",
        home);

    /// <summary>
    /// <paramref name="text"/> with the home folder written as <c>~</c> (a crash report names it in every path of the account's own
    /// programs); only where the home is a whole folder name, so <c>/Users/ann</c> leaves <c>/Users/anne</c> alone. Since 2026-10-23 (the admin
    /// review's X1) the name ends at anything but a letter, a digit, <c>-</c> or <c>_</c> (a plist's <c>&lt;</c>, a PATH's <c>:</c>, a
    /// sentence's <c>.</c> left it whole), and case is ignored (APFS: <c>/users/ann</c> is the same folder).
    /// </summary>
    public string Tilde(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string home = Home.TrimEnd('/');
        if (home.Length < 2 || !text.Contains(home, StringComparison.OrdinalIgnoreCase))
        {
            return text;
        }

        var answer = new System.Text.StringBuilder(text.Length);
        int at = 0;
        while (text.IndexOf(home, at, StringComparison.OrdinalIgnoreCase) is var found and >= 0)
        {
            int end = found + home.Length;
            bool whole = end == text.Length || !(char.IsLetterOrDigit(text[end]) || text[end] is '-' or '_');
            answer.Append(text, at, found - at).Append(whole ? "~" : text.AsSpan(found, home.Length));
            at = end;
        }

        return answer.Append(text, at, text.Length - at).ToString();
    }

    /// <summary>
    /// <paramref name="text"/> with every other account's home written as <c>/Users/&lt;redacted&gt;</c> (2026-10-23, the admin review's X2: the
    /// system's reports and the unified log name them in paths, frames and senders); <c>/Users/Shared</c> kept. After <see cref="Tilde"/>.
    /// </summary>
    public static string OtherHomes(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Contains("/users/", StringComparison.OrdinalIgnoreCase) ? OtherHome().Replace(text, m => m.Groups["head"].Value + Llm.Tools.ToolRedaction.Placeholder) : text;
    }

    [GeneratedRegex(@"(?<head>(?<![\w.])(?:/System/Volumes/Data)?/Users/)(?!Shared(?![\w-]))[\w.\-]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OtherHome();
}
