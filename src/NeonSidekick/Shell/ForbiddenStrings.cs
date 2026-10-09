using System.Text;

namespace NeonSidekick.Shell;

/// <summary>
/// The shell police's second rule (2026-10-03, the user's idea): <c>Shell police forbidden strings</c>, a list the user keeps, and
/// a <c>run_command</c> line, <c>execute_code</c> script or <c>process</c> write that contains one of them is refused before the gate
/// is asked. Loose on purpose (the user's call): case ignored and every run of whitespace — spaces, tabs, line breaks — read as
/// one space on both sides, so <c>rm -rf</c> catches <c>RM   -RF</c> and a script's <c>rm</c> and <c>-rf</c> on two lines. A
/// tripwire, not a sandbox: text built in pieces (<c>"r" + "m"</c>, cmd's <c>r^m</c>) gets past it. The list keeps the user's case
/// (unlike <see cref="CommandAllowList.Merge"/>, which lower-cases), sorted A to Z ignoring case, no duplicates ignoring case.
/// </summary>
public static class ForbiddenStrings
{
    /// <summary>
    /// The entries a fresh profile starts with on either system (2026-10-08, the user's call, from the review in
    /// <c>.notes/RECOMMENDED_FORBIDDEN_STRINGS.md</c>): actions that do harm wherever they run, worded so ordinary agent work
    /// almost never contains them, since a match has no pane and no yolo. A pipe into a shell is spelled both ways because
    /// <see cref="Normalize"/> never adds or removes a space. PowerShell's (<c>-EncodedCommand</c>, <c>| iex</c>,
    /// <c>DownloadString(</c>) stay on a Mac too: pwsh may be installed, and they match nothing otherwise.
    /// </summary>
    public static string[] SharedDefaults =>
    [
        "shutdown -",
        ".ssh/id_",
        "| bash",
        "|bash",
        "-EncodedCommand",
        "DownloadString(",
        "| iex",
        "|iex",
        "push --force",
        "reset --hard",
    ];

    /// <summary>
    /// Windows' own defaults beside <see cref="SharedDefaults"/>: disks, boot and backups; power; Defender and the firewall;
    /// scheduled tasks, services and accounts; credentials, the app's own DPAPI secrets among them (the Win32 call and .NET's
    /// wrapper from C# and PowerShell, never a bare <c>ProtectedData</c>, which Microsoft.Extensions.AI's
    /// <c>TextReasoningContent.ProtectedData</c> would trip); the event logs. <c>of=/dev/sd</c>/<c>nvme</c> are dd under WSL or
    /// Git Bash (a bare <c>of=/dev/</c> would refuse <c>of=/dev/null</c>); <c>PhysicalDrive</c> is <c>\\.\PhysicalDrive0</c>
    /// in any backslash spelling.
    /// </summary>
    public static string[] WindowsDefaults =>
    [
        "format c:",
        "Format-Volume",
        "Clear-Disk",
        "Initialize-Disk",
        "diskpart",
        "bcdedit",
        "vssadmin delete",
        "shadowcopy delete",
        "wbadmin delete",
        "cipher /w",
        "mkfs",
        "of=/dev/sd",
        "of=/dev/nvme",
        "PhysicalDrive",
        "shutdown /",
        "Stop-Computer",
        "Restart-Computer",
        "Set-MpPreference",
        "Add-MpPreference",
        "netsh advfirewall",
        "Set-NetFirewallProfile",
        "schtasks /create",
        "Register-ScheduledTask",
        "New-Service",
        "New-LocalUser",
        "Add-LocalGroupMember",
        "net localgroup",
        "mimikatz",
        "sekurlsa",
        "lsass",
        "reg save hklm",
        "cmdkey /list",
        "vaultcmd",
        "CryptUnprotectData",
        "ProtectedData.Unprotect",
        "ProtectedData]::Unprotect",
        @".ssh\id_",
        "wevtutil cl",
        "Clear-EventLog",
    ];

    /// <summary>
    /// The Mac's own defaults beside <see cref="SharedDefaults"/>: diskutil's erasing verbs, <c>newfs_</c>, dd onto a disk,
    /// Time Machine's deletes; <c>nvram</c>; Gatekeeper, the firewalls and the privacy database; launchd, login hooks, cron
    /// and the account tools; the Keychain (the app's own <c>keychain:</c> key is a generic password, <c>Sql/MacKeychain</c>)
    /// and password prompts; a pipe into zsh; <c>log erase</c>. Left out: <c>csrutil</c> (Recovery only, so it guards nothing)
    /// and <c>com.apple.quarantine</c> (the app's own README asks the user to clear it).
    /// </summary>
    public static string[] MacDefaults =>
    [
        "diskutil erase",
        "diskutil zeroDisk",
        "diskutil secureErase",
        "diskutil partitionDisk",
        "diskutil apfs delete",
        "newfs_",
        "of=/dev/disk",
        "of=/dev/rdisk",
        "tmutil delete",
        "tmutil disable",
        "nvram",
        "spctl --master-disable",
        "spctl --global-disable",
        "spctl --add",
        "socketfilterfw",
        "pfctl -d",
        "tccutil reset",
        "TCC.db",
        "LaunchAgents",
        "LaunchDaemons",
        "launchctl load",
        "launchctl bootstrap",
        "launchctl submit",
        "LoginHook",
        "crontab",
        "dscl",
        "sysadminctl",
        "dseditgroup",
        "generic-password",
        "internet-password",
        "dump-keychain",
        ".keychain-db",
        "hidden answer",
        "with administrator privileges",
        "sudo -S",
        "| zsh",
        "|zsh",
        "log erase",
    ];

    /// <summary>
    /// What <see cref="Settings.AppSettingsData.ShellPoliceForbiddenStrings"/> starts as: <see cref="SharedDefaults"/> and this
    /// system's own list (Windows', else the Mac's), <see cref="Sorted"/> as the editor saves it. Only a fresh profile gets it; a
    /// saved list stands, the fresh <c>ToolsDisabled</c>'s way, so a profile from before keeps its own (empty) list.
    /// </summary>
    public static List<string> Defaults => Sorted([.. SharedDefaults, .. OperatingSystem.IsWindows() ? WindowsDefaults : MacDefaults]);

    /// <summary><paramref name="text"/> with every run of whitespace one space, trimmed: the form both sides are compared in.</summary>
    public static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new StringBuilder(text.Length);
        bool space = false;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                space = builder.Length > 0;
                continue;
            }

            if (space)
            {
                builder.Append(' ');
                space = false;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>
    /// The first entry of <paramref name="forbidden"/>, in list order and as the user typed it, that <paramref name="text"/>
    /// contains once both are <see cref="Normalize"/>d, case ignored; null when none does. A blank entry never matches.
    /// </summary>
    public static string? Find(string text, IReadOnlyList<string> forbidden)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(forbidden);
        if (forbidden.Count == 0)
        {
            return null;
        }

        string normalized = Normalize(text);
        foreach (string entry in forbidden)
        {
            string needle = Normalize(entry ?? "");
            if (needle.Length > 0 && normalized.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }

    /// <summary>The saved list as the editor shows it and saves it: each entry <see cref="Normalize"/>d, blanks gone, no duplicates ignoring case (the first kept), sorted A to Z ignoring case.</summary>
    public static List<string> Sorted(IReadOnlyList<string> saved)
    {
        ArgumentNullException.ThrowIfNull(saved);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (string entry in saved)
        {
            string normalized = Normalize(entry ?? "");
            if (normalized.Length > 0 && seen.Add(normalized))
            {
                list.Add(normalized);
            }
        }

        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    /// <summary>Whether <paramref name="entry"/> is already in <paramref name="saved"/>, both <see cref="Normalize"/>d, case ignored.</summary>
    public static bool Contains(IReadOnlyList<string> saved, string entry)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(entry);
        string normalized = Normalize(entry);
        return saved.Any(existing => string.Equals(Normalize(existing ?? ""), normalized, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The saved list with <paramref name="entry"/> added (<see cref="Sorted"/>); a blank entry or one already there leaves it as it was.</summary>
    public static List<string> Add(IReadOnlyList<string> saved, string entry)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(entry);
        return Sorted([.. saved, entry]);
    }

    /// <summary>The saved list without <paramref name="entry"/> (both <see cref="Normalize"/>d, case ignored), <see cref="Sorted"/>: the editor's remove.</summary>
    public static List<string> Without(IReadOnlyList<string> saved, string entry)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(entry);
        string normalized = Normalize(entry);
        var list = Sorted(saved);
        list.RemoveAll(existing => string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase));
        return list;
    }
}
