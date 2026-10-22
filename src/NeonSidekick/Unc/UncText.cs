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

    /// <summary>A share name given twice in one file, ignoring case (2026-10-22, review K2, K8). Pinned.</summary>
    public const string NamedTwice = "this name is given to more than one share in the file (names ignore case), so neither is used; rename or remove one";
    public static string DevicePath(string path) => $"\"path\" is '{path}', a device path; give the share as \\\\server\\share or a folder as X:\\folder";
    public static string NoShareName(string path) => $"\"path\" is '{path}', a server without a share; give it as \\\\server\\share";
    public static string NotAbsolute(string path) => $"\"path\" is '{path}'; it must be a full path, \\\\server\\share or X:\\folder";
    public static string WholeDrive(string drive) => $"\"path\" is the whole of {drive}\\; a share is a folder — give one on it";
    public static string BadPath(string path) => $"\"path\" is '{path}', which is not a usable folder path";
    /// <summary>A Mac's path refusals (2026-10-11, <see cref="UncPath"/>): no drives, no UNC paths, a share as <c>smb://</c>. Pinned.</summary>
    public static string DevicePathHere(string path) => $"\"path\" is '{path}', a Windows device path; give the share as smb://server/share or a folder as /folder";
    public static string DriveNotHere(string path) => $"\"path\" is '{path}', a drive path, which only Windows has; on a Mac give the share as smb://server/share or the folder as /folder";
    public static string NoShareNameHere(string path) => $"\"path\" is '{path}', a server without a share; give it as smb://server/share";
    public static string NotAbsoluteHere(string path) => $"\"path\" is '{path}'; it must be a full path, smb://server/share or /folder";
    public const string WholeDisk = "\"path\" is /, the whole disk; a share is a folder — give one on it";
    public static string BadAccountHere(string user) => $"\"user\" is '{user}'; give the account as name, name@REALM or DOMAIN\\name, without spaces or ';'";

    /// <summary>
    /// A Mac's runas share on the network (2026-10-11): it is mounted as that account for the rest of the run, and a mount is the
    /// whole machine's, not the app's. Pinned.
    /// </summary>
    public static string RunAsMountedWarning(string user) =>
        $"a Mac mounts the share as {user} until the app exits; meanwhile your other programs can reach it as {user} too";

    public static string BadAuth(string word) => $"\"auth\" is '{word}'; it must be windows or runas";
    public static string BadAccess(string word) => $"\"access\" is '{word}'; it must be read or readwrite";
    public const string RunAsNoUser = "runas needs the Windows account in \"user\" (DOMAIN\\name or name@domain)";
    /// <summary>A Mac's (2026-10-11, code review): the share is mounted as the account, a server's own local name too. Pinned.</summary>
    public const string RunAsNoUserHere = "runas needs the account in \"user\" (name, name@REALM or DOMAIN\\name)";
    /// <summary><see cref="RunAsNoUser"/> or a Mac's <see cref="RunAsNoUserHere"/>, by <paramref name="windows"/>.</summary>
    public static string RunAsNoUserFor(bool windows) => windows ? RunAsNoUser : RunAsNoUserHere;
    public const string CredmanWithWindows = "\"passwordStore\": \"credman\" needs a password to keep; windows sign-in has none (use runas to sign in as another account)";
    public static string UnreadableFile(string detail) => $"the file cannot be read ({detail}); a backslash in JSON is written \\\\ (or use /)";
    public static string ProfilesUnlistedLogLine(string root, string detail) => $"could not list the profiles in {root}, so only the home's unc.json was checked for plain passwords: {detail}";
    public static string NoPassword(string name) => $"'{name}' has no password; set it on the UNC tab of /tools (UNC set password)";
    /// <summary>No stored password for a runas share: the store and the command by the OS (<see cref="Sql.SqlText.CredentialStore"/>, <see cref="Sql.SqlText.CredentialCommand"/>; 2026-10-06, the macOS build — Windows' sentence unchanged). Pinned.</summary>
    public static string NoCredential(string target) => $"no password in {Sql.SqlText.CredentialStore} for {target}; set it on the UNC tab of /tools, or: {Sql.SqlText.CredentialCommand(target, "<DOMAIN\\name>")}";

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
    /// <summary>
    /// The line's note for a pasted picture path on a host the app doesn't know (2026-10-22, review K7, the fix plan's D13): it stays text,
    /// unopened, so pasting a stranger's path never signs in to it.
    /// </summary>
    public static string PasteNotOpened(string host) =>
        $"(not attached: {host} is not a UNC share's server or a mapped drive's, so the app left the path as text)";

    public static string OpenRunAsRefused(string name) =>
        $"Error: share '{name}' signs in as another account, which a program the shell starts cannot use; unc_fetch the file into the working directory, then open the copy there";

    public static string UnknownShare(string name, string names) => $"Error: no UNC share is named '{name}'; the shares are {names}";

    /// <summary>A call with several shares that names none and gives no full path (2026-10-09: <c>UNC default share</c> gone). Pinned.</summary>
    public static string ShareNeeded(string names) => $"Error: name the UNC share in \"share\" (or give a full path under one); the shares are {names}";
    public static string NotInShare(string path, UncNamedShare share) => $"Error: '{path}' is not under share '{share.Name}' ({share.Config.Root}); give a path relative to it, or leave \"share\" out";
    public static string NotUnderAnyShare(string path, string names) => $"Error: '{path}' is under no UNC share the tools can reach; the shares are {names} (unc_shares lists where each points)";
    public static string StreamPath(string path) => $"Error: '{path}' names a stream (a ':' in the path); give a plain file or folder";
    public static string CrossShare(string to) => $"Error: '{to}' is on another share; move and copy stay within one share (unc_fetch then unc_put carries a file between shares through the working directory)";

    // ─── the write gate ─────────────────────────────────────────────────────────

    public const string WritesOff = "Error: UNC writes is off, so every share is read-only; the user turns it on (the UNC tab of /tools)";
    public static string ReadOnlyShare(string name) => $"Error: share '{name}' is read-only (\"access\": \"read\" in unc.json); the user makes it readwrite to allow changes";
    public static string AuditLogLine(string share, string account, string action, string path) => $"{share} ({account}): {action} {path}";

    /// <summary>The audit line naming a move's or copy's other end, and a change that failed partway (review J8).</summary>
    public static string AuditLogLine(string share, string account, string action, string path, string? to, bool partial) =>
        AuditLogLine(share, account, action, path) + (string.IsNullOrEmpty(to) ? "" : " to " + to) + (partial ? PartialAuditNote : "");

    /// <summary>What an audit line adds for a change that failed after it began. Pinned.</summary>
    public const string PartialAuditNote = " (failed partway: part of it may have changed)";

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

    // ─── a Mac's mounts (2026-10-11, ShareMounts) ───────────────────────────────

    /// <summary>A network share where nothing can mount it (off Windows and off a Mac). Pinned.</summary>
    public static string NoMountsHere(string name) => $"Error: share '{name}' is on the network, which this build cannot mount; give a local folder instead";
    /// <summary>A mount NetFS made, or handed back, that is not signed in as the share's account (a Mac's runas). Pinned.</summary>
    public static string MountedAsOther(string name, string account) =>
        $"Error: share '{name}' could not be mounted as {account}: the Mac already has it mounted as another account; eject that mount (Finder) and try again";
    public static string MountStatus(int status) => status == int.MinValue ? "the mount failed (see the log)" : $"the mount failed ({status.ToString(CultureInfo.InvariantCulture)})";
    /// <summary>A share asked for once the app's exit has unmounted the run's mounts (2026-10-11, code review). Pinned.</summary>
    public static string MountsClosed(string name) => $"Error: share '{name}' is not mounted: the app is exiting";

    /// <summary>The reasons a mount was refused, from NetFS's status (<c>MacMountRules.Refusal</c>; here since the code review of 2026-10-11). Pinned.</summary>
    public const string MustChangePassword = "the password must be changed first";
    public const string PasswordPolicy = "the password policy refused it";
    public const string AccountRestricted = "the account is restricted";
    public const string NoSmbVersion = "the server speaks no SMB version this Mac does";
    public const string NoGuest = "the server takes no guest";
    public const string NetworkUnreachable = "the network is unreachable";
    public const string ConnectionTimedOut = "the connection timed out";
    public const string ConnectionRefused = "the server refused the connection";
    public const string HostDown = "the server is down";
    public const string NoRoute = "no route to the server";
    /// <summary><c>--unc-check</c>'s line for a mounted share: where, from whom, and whether the app made it. Pinned.</summary>
    public static string MountDescription(string point, string from, bool ours, bool readOnly) =>
        $"mounted at {point} from {from}{(readOnly ? ", read-only" : "")}{(ours ? "" : " (the Mac's own mount, left as it is at exit)")}";
    public static string MountLogLine(string name, string detail) => $"mounting share '{name}' failed: {detail}";
    public static string MountedAsOtherLogLine(string name, string account, string from) => $"share '{name}' mounted from {from}, not as {account}; not used";
    public static string MountUnreadableLogLine(string name, string point) => $"share '{name}' mounted at {point}, but no mount is found there; let go";
    public static string MountedLogLine(string name, string point, string from, bool ours) => $"share '{name}' {(ours ? "mounted" : "found mounted")} at {point} from {from}";
    public static string UnmountedLogLine(string point) => $"unmounted {point}";
    public static string UnmountFailedLogLine(string point, int errno) => $"could not unmount {point} (errno {errno.ToString(CultureInfo.InvariantCulture)}); eject it in Finder";
    public static string UnmountFailedLogLine(string point, string detail) => $"could not unmount {point}: {detail}; eject it in Finder";
    public static string LeftMountedLogLine(string point, int owner, int errno) =>
        errno == 0
            ? $"unmounted {point}, left mounted by an app that is gone (pid {owner.ToString(CultureInfo.InvariantCulture)})"
            : $"could not unmount {point}, left mounted by an app that is gone (pid {owner.ToString(CultureInfo.InvariantCulture)}): errno {errno.ToString(CultureInfo.InvariantCulture)}";

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
    /// <remarks>
    /// Only for a sentence that holds no file's text (a refusal, a copy's answer): one that may (a read, a search, a patch's region) is built
    /// through <see cref="Scoped(Func{string}, UncNamedShare)"/>, which names the root as it is built and leaves the content alone (review H2).
    /// </remarks>
    public static string Scoped(string sentence, UncNamedShare share)
    {
        ArgumentNullException.ThrowIfNull(sentence);
        string name = ShareName(share);
        return sentence
            .Replace(FileText.DefaultRootName, name, StringComparison.Ordinal)
            .Replace("The working directory", char.ToUpperInvariant(name[0]) + name[1..], StringComparison.Ordinal);
    }

    /// <summary>
    /// <paramref name="build"/>'s text with the root named for the share as it is built (<see cref="FileText.NamingRoot"/>), a file's own
    /// text in it never rewritten (2026-10-22, review H2).
    /// </summary>
    public static string Scoped(Func<string> build, UncNamedShare share)
    {
        ArgumentNullException.ThrowIfNull(build);
        using (FileText.NamingRoot(ShareName(share)))
        {
            return build();
        }
    }

    /// <summary>The root named for the share while <paramref name="build"/> runs, whatever it returns (review H2).</summary>
    public static T Scoped<T>(Func<T> build, UncNamedShare share)
    {
        ArgumentNullException.ThrowIfNull(build);
        using (FileText.NamingRoot(ShareName(share)))
        {
            return build();
        }
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
    public static string ShareLine(UncNamedShare share, bool writesOn, string? reach = null)
    {
        ArgumentNullException.ThrowIfNull(share);
        var config = share.Config;
        string how = config.IsRunAs ? $"as {config.User?.Trim()} (runas)" : "as you";
        string access = config.IsReadWrite ? (writesOn ? "read-write" : "read-only (readwrite, but UNC writes is off)") : "read-only";
        string line = $"- {share.Name}: {config.Root}, {how}, {access}";
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
    public static string Shares(UncCatalog catalog, bool writesOn, IReadOnlyDictionary<string, string>? reach = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var sb = new StringBuilder();
        if (catalog.Shares.Count == 0)
        {
            sb.Append(NoShares);
        }
        else
        {
            sb.Append(SqlText.Count(catalog.Shares.Count, "UNC share")).Append(" (every UNC tool takes one by name in \"share\", or a full path under it; both may be left out only when there is one share):");
            foreach (var share in catalog.Shares)
            {
                string? reached = reach is not null && reach.TryGetValue(share.Name, out string? r) ? r : null;
                sb.Append('\n').Append(ShareLine(share, writesOn, reached));
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
