using System.Globalization;
using System.Text;
using NeonSidekick.Files;
using NeonSidekick.Sql;

namespace NeonSidekick.Unc;

/// <summary>
/// The UNC tools' wording and formatting (2026-09-30), pure and pinned: the <c>unc.json</c> problems, the reach and sign-in
/// refusals, the write gate, the listings. A file operation's own result is <see cref="FileText"/>'s, its root named for the
/// share (<see cref="Scoped"/>). Every error starts <c>Error:</c>. Invariant culture throughout.
/// </summary>
public static class UncText
{
    // ─── unc.json ───────────────────────────────────────────────────────────────

    public const string NoPath = "no \"path\" is given";
    public const string BlankName = "a share has a blank name";
    public static string DevicePath(string path) => $"\"path\" is '{path}', a device path; give the share as \\\\server\\share or a folder as X:\\folder";
    public static string NoShareName(string path) => $"\"path\" is '{path}', a server without a share; give it as \\\\server\\share";
    public static string NotAbsolute(string path) => $"\"path\" is '{path}'; it must be a full path, \\\\server\\share or X:\\folder";
    public static string WholeDrive(string drive) => $"\"path\" is the whole of {drive}\\; a share is a folder — give one on it";
    public static string BadPath(string path) => $"\"path\" is '{path}', which is not a usable folder path";
    public static string BadAuth(string word) => $"\"auth\" is '{word}'; it must be windows or runas";
    public static string BadAccess(string word) => $"\"access\" is '{word}'; it must be read or readwrite";
    public const string RunAsNoUser = "runas needs the Windows account in \"user\" (DOMAIN\\name or name@domain)";
    public const string CredmanWithWindows = "\"passwordStore\": \"credman\" needs a password to keep; windows sign-in has none (use runas to sign in as another account)";
    public static string UnreadableFile(string detail) => $"the file cannot be read ({detail}); a backslash in JSON is written \\\\ (or use /)";
    public static string ProfilesUnlistedLogLine(string root, string detail) => $"could not list the profiles in {root}, so only the home's unc.json was checked for plain passwords: {detail}";
    public static string NoPassword(string name) => $"'{name}' has no password; set it on the UNC tab of /tools (UNC set password)";
    public static string NoCredential(string target) => $"no password in Windows Credential Manager for {target}; set it on the UNC tab of /tools, or: cmdkey /generic:{target} /user:<DOMAIN\\name> /pass";

    /// <summary>A usable entry's notice: a mapped network drive's letter where the UNC path belongs. Pinned.</summary>
    public static string MappedDriveWarning(string drive) =>
        $"{drive} is a mapped network drive; give its \\\\server\\share path instead — a mapping belongs to a sign-in, and a runas token or an elevated app may not see it";

    /// <summary>A usable entry's notice: runas on a local folder. Pinned.</summary>
    public const string RunAsLocalWarning = "runas only changes who the network sees; a local folder is read as you";

    // ─── which share ────────────────────────────────────────────────────────────

    public const string NoShares = "Error: no UNC share is defined; the user adds one to unc.json (the UNC tab of /tools)";
    /// <summary><c>open</c> given a share while none is offered (2026-10-01): the switch off or nothing ticked. Pinned.</summary>
    public const string NoSharesOffered = "Error: no UNC share is offered; the user turns on UNC tools and ticks a share in UNC shares offered (the UNC tab of /tools)";

    /// <summary>
    /// <c>open</c> on a <c>runas</c> share on the network (2026-10-01, the user's call): the program the shell starts runs as the
    /// user, never with the share's sign-in, so the model is pointed to a copy instead. Pinned.
    /// </summary>
    public static string OpenRunAsRefused(string name) =>
        $"Error: share '{name}' signs in as another account, which a program the shell starts cannot use; unc_fetch the file into the working directory, then open the copy there";

    public static string UnknownShare(string name, string names) => $"Error: no UNC share is named '{name}'; the shares are {names}";
    public static string NotInShare(string path, UncNamedShare share) => $"Error: '{path}' is not under share '{share.Name}' ({share.Config.Root}); give a path relative to it, or leave \"share\" out";
    public static string NotUnderAnyShare(string path, string names) => $"Error: '{path}' is under no UNC share the tools can reach; the shares are {names} (unc_shares lists where each points)";
    public static string StreamPath(string path) => $"Error: '{path}' names a stream (a ':' in the path); give a plain file or folder";
    public static string CrossShare(string to) => $"Error: '{to}' is on another share; move and copy stay within one share (unc_fetch then unc_put carries a file between shares through the working directory)";

    // ─── the write gate ─────────────────────────────────────────────────────────

    public const string WritesOff = "Error: UNC writes is off, so every share is read-only; the user turns it on (the UNC tab of /tools)";
    public static string ReadOnlyShare(string name) => $"Error: share '{name}' is read-only (\"access\": \"read\" in unc.json); the user makes it readwrite to allow changes";
    public static string AuditLogLine(string share, string account, string action, string path) => $"{share} ({account}): {action} {path}";

    // ─── the reach ──────────────────────────────────────────────────────────────

    public static string CannotSignIn(string name, string detail) => $"Error: cannot sign in to share '{name}': {detail}";
    public static string Unreachable(string name, string root, string detail) => $"Error: share '{name}' ({root}) cannot be reached: {detail}";
    public static string TimedOut(TimeSpan wait) => $"no answer in {wait.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} s";
    public static string NotAFolder(string name, string root) => $"Error: share '{name}' points at {root}, which is a file, not a folder";
    public static string RootMissing(string name, string root) => $"Error: share '{name}' points at {root}, which does not exist";
    public static string AccessDenied(string name, string root, string account) => $"Error: share '{name}' ({root}) refused {account}: access denied";
    public static string BadAccount(string name, string account) => $"Error: share '{name}' refused the sign-in as {account}: unknown account or wrong password (UNC set password on the UNC tab of /tools)";
    public static string NoLogonServer(string name) => $"Error: share '{name}': no domain controller answered to check the account";
    public static string AccountRefused(string name, string account, string detail) => $"Error: share '{name}' refused the sign-in as {account}: {detail}";
    public static string ReachFailed(string name, string root, string detail) => $"Error: share '{name}' ({root}): {detail}";

    // ─── results ────────────────────────────────────────────────────────────────

    /// <summary>How a share is named in a result: <c>share 'eng' (\\fs01\eng)</c>. Pinned.</summary>
    public static string ShareName(UncNamedShare share)
    {
        ArgumentNullException.ThrowIfNull(share);
        return $"share '{share.Name}' ({share.Config.Root})";
    }

    /// <summary>
    /// A <see cref="FileText"/> sentence with its root named for the share: "the working directory" (<see cref="FileText.RootName"/>)
    /// becomes <see cref="ShareName"/>, so a listing reads <c>share 'eng' (\\fs01\eng) (12 entries):</c> and a refusal names where it
    /// stopped. Pure.
    /// </summary>
    public static string Scoped(string sentence, UncNamedShare share)
    {
        ArgumentNullException.ThrowIfNull(sentence);
        string name = ShareName(share);
        return sentence
            .Replace(FileText.RootName, name, StringComparison.Ordinal)
            .Replace("The working directory", char.ToUpperInvariant(name[0]) + name[1..], StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>unc_fetch</c>'s answer: <c>fetched reports\q3.xlsx from share 'fin' (\\fs02\fin) to q3.xlsx in the working directory</c>;
    /// a refusal is the copy's own sentence (the working directory's end).
    /// </summary>
    public static string Fetched(MoveResult result, UncNamedShare share)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Outcome != FileOutcome.Ok
            ? FileText.Copied(result)
            : $"fetched {result.From} from {ShareName(share)} to {result.To} in the working directory";
    }

    /// <summary>
    /// <c>unc_put</c>'s answer: <c>put notes.md into share 'eng' (\\fs01\eng) as specs\notes.md</c> — or the copy's refusal, in the
    /// share's words (the share is its end that writes).
    /// </summary>
    public static string Put(MoveResult result, UncNamedShare share)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Outcome != FileOutcome.Ok
            ? Scoped(FileText.Copied(result), share)
            : $"put {result.From} into {ShareName(share)} as {result.To}";
    }

    /// <summary>The note after a search or recent list that stopped at a share's budget. Pinned.</summary>
    public const string BudgetNote = "(stopped early: a share's search reads at most 256 MB and looks at 100,000 entries; narrow it with path, files or depth)";

    /// <summary>One share as <c>unc_shares</c> lists it: the name, where, how it is reached, read or read-write, the description — never the password.</summary>
    public static string ShareLine(UncNamedShare share, bool isDefault, bool writesOn, string? reach = null)
    {
        ArgumentNullException.ThrowIfNull(share);
        var config = share.Config;
        string how = config.IsRunAs ? $"as {config.User?.Trim()} (runas)" : "as you";
        string access = config.IsReadWrite ? (writesOn ? "read-write" : "read-only (readwrite, but UNC writes is off)") : "read-only";
        string line = $"- {share.Name}{(isDefault ? " (default)" : "")}: {config.Root}, {how}, {access}";
        if (!string.IsNullOrWhiteSpace(config.Description))
        {
            line += " — " + config.Description.Trim();
        }

        if (config.Warning is { } warning)
        {
            line += " [" + warning + "]";
        }

        return reach is null ? line : line + "\n  " + reach;
    }

    /// <summary>The closing line of <c>unc_shares</c> while the profile hides some: how many, never which. Pinned.</summary>
    public static string HiddenShares(int count) =>
        count == 1
            ? "1 more share in unc.json is switched off for this profile (the UNC tab of /tools)."
            : $"{count.ToString(CultureInfo.InvariantCulture)} more shares in unc.json are switched off for this profile (the UNC tab of /tools).";

    /// <summary>What <c>unc_shares</c> with <c>check</c> says of a share that answered. Pinned.</summary>
    public static string Reached(int entries) => $"reachable ({SqlText.Count(entries, "entry", "entries")} at its root)";

    /// <summary><c>unc_shares</c>' whole answer: a count, one line each (with its reach under <c>check</c>), the problems that kept any out.</summary>
    public static string Shares(UncCatalog catalog, string? defaultName, bool writesOn, IReadOnlyDictionary<string, string>? reach = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var sb = new StringBuilder();
        if (catalog.Shares.Count == 0)
        {
            sb.Append(NoShares);
        }
        else
        {
            var chosen = catalog.Find(null, defaultName);
            sb.Append(SqlText.Count(catalog.Shares.Count, "UNC share")).Append(" (every UNC tool takes one by name in \"share\", or a full path under it; the default is used when both are left out):");
            foreach (var share in catalog.Shares)
            {
                string? reached = reach is not null && reach.TryGetValue(share.Name, out string? r) ? r : null;
                sb.Append('\n').Append(ShareLine(share, ReferenceEquals(share, chosen), writesOn, reached));
            }

            if (!writesOn)
            {
                sb.Append("\nUNC writes is off: every share is read-only.");
            }
        }

        foreach (var problem in catalog.Problems)
        {
            sb.Append("\nSkipped ").Append(problem.Source).Append(": ").Append(problem.Reason);
        }

        if (catalog.Hidden > 0)
        {
            sb.Append('\n').Append(HiddenShares(catalog.Hidden));
        }

        return sb.ToString();
    }

    /// <summary>
    /// A share's note on the <c>*</c>-mention list: where it points, read or read-write, then its description. Pinned. Until
    /// 2026-10-01 it began <c>UNC ·</c> to read apart in the database connections' <c>%</c> list; the shares' own list needs none.
    /// </summary>
    public static string MentionNote(UncNamedShare share)
    {
        ArgumentNullException.ThrowIfNull(share);
        var config = share.Config;
        string where = config.Root + (config.IsReadWrite ? " · readwrite" : "");
        return string.IsNullOrWhiteSpace(config.Description) ? where : where + " — " + config.Description.Trim();
    }
}
