using System.Text.Json.Serialization;
using NeonSidekick.Sql;

namespace NeonSidekick.Unc;

/// <summary>
/// One named share of <c>unc.json</c> (2026-09-30, the user's ask: "a system similar to the way we implemented integrated auth
/// and run-as for the SQL tools which could be used to access UNC paths … the file path replaces the database name"): where it
/// is — a <c>\\server\share[\folder]</c> path, or (the user's call) a local folder outside the working directory such as
/// <c>D:\Data</c> — how it is reached (<c>windows</c>: as the user running the app; <c>runas</c>: as another Windows account,
/// <c>runas /netonly</c>'s way, its password kept as <c>sql.json</c>'s are: <see cref="PasswordStore"/> <c>file</c> DPAPI-encrypted
/// in <see cref="Password"/>, or <c>credman</c> in Windows Credential Manager), and what the model may do there (<c>read</c>, the
/// default, or <c>readwrite</c> — which still needs <c>UNC writes</c> on, the user's two-key call). A password is never shown,
/// logged or sent to the model. Read through <see cref="UncJsonContext"/> alone.
/// </summary>
public sealed class UncShareConfig
{
    /// <summary>The <see cref="Auth"/> word for the user running the app (the default).</summary>
    public const string WindowsAuth = "windows";

    /// <summary>The <see cref="Auth"/> word for another Windows account's credentials on the network (<c>runas /netonly</c>).</summary>
    public const string RunAsAuth = "runas";

    /// <summary>The <see cref="Access"/> word for reading only (the default).</summary>
    public const string ReadAccess = "read";

    /// <summary>The <see cref="Access"/> word for reading and changing, under <c>UNC writes</c>.</summary>
    public const string ReadWriteAccess = "readwrite";

    /// <summary>The <see cref="PasswordStore"/> word for a DPAPI value in <see cref="Password"/> (the default).</summary>
    public const string FileStore = SqlConnectionConfig.FileStore;

    /// <summary>The <see cref="PasswordStore"/> word for a Windows Credential Manager entry.</summary>
    public const string CredmanStore = SqlConnectionConfig.CredmanStore;

    /// <summary>What <see cref="CredentialTarget"/> is for a share that names no <see cref="Credential"/>: <c>NeonSidekick/unc/&lt;name&gt;</c>.</summary>
    public const string CredentialPrefix = "NeonSidekick/unc/";

    /// <summary>The share's path: <c>\\server\share</c>, a folder under it, or a local folder (<c>D:\Data</c>); <c>/</c> reads as <c>\</c>.</summary>
    public string? Path { get; set; }

    /// <summary><c>windows</c> (the default) or <c>runas</c>.</summary>
    public string? Auth { get; set; }

    /// <summary>The Windows account under <c>runas</c>: <c>DOMAIN\name</c> or <c>name@domain</c>.</summary>
    public string? User { get; set; }

    /// <summary>The password under <c>runas</c> and the <c>file</c> store: <c>dpapi:…</c>, or plain text the next read encrypts. Never shown.</summary>
    public string? Password { get; set; }

    /// <summary><c>file</c> (the default) or <c>credman</c>: where the <c>runas</c> password is kept.</summary>
    public string? PasswordStore { get; set; }

    /// <summary>The Credential Manager entry under <c>credman</c>; <see cref="CredentialPrefix"/> and the share's name when absent.</summary>
    public string? Credential { get; set; }

    /// <summary><c>read</c> (the default) or <c>readwrite</c>.</summary>
    public string? Access { get; set; }

    /// <summary>What the share holds, in the user's words; the model reads it to pick a share.</summary>
    public string? Description { get; set; }

    /// <summary>Whether the share is reached as another account (<c>runas</c>).</summary>
    [JsonIgnore]
    public bool IsRunAs => string.Equals(Auth?.Trim(), RunAsAuth, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a password is needed: <c>runas</c> alone.</summary>
    [JsonIgnore]
    public bool NeedsPassword => IsRunAs;

    /// <summary>Whether the entry allows changes (<c>readwrite</c>); the tools still need <c>UNC writes</c> on.</summary>
    [JsonIgnore]
    public bool IsReadWrite => string.Equals(Access?.Trim(), ReadWriteAccess, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the password lives in Windows Credential Manager rather than in the file.</summary>
    [JsonIgnore]
    public bool InCredentialManager => string.Equals(PasswordStore?.Trim(), CredmanStore, StringComparison.OrdinalIgnoreCase);

    /// <summary>The Credential Manager entry this share's password is read from under <c>credman</c>.</summary>
    public string CredentialTarget(string name) => string.IsNullOrWhiteSpace(Credential) ? CredentialPrefix + name : Credential.Trim();

    /// <summary>
    /// The share's root, full, in the platform's spelling (<see cref="UncPath"/>: <c>\\server\share\x</c> or <c>D:\x</c> on Windows,
    /// <c>smb://server/share/x</c> or <c>/x</c> on a Mac), without a trailing separator. On a Mac a network root is a name, not a path
    /// the files are under (<see cref="MacShareMounts"/>). Call only on an entry without a <see cref="Problem"/>.
    /// </summary>
    [JsonIgnore]
    public string Root => NormalizeRoot(Path!);

    /// <summary>Whether <see cref="Root"/> is a share on the network (<c>\\server\share…</c>, a Mac's <c>smb://…</c>) rather than a local folder.</summary>
    [JsonIgnore]
    public bool IsUnc => UncPath.IsNetwork(Root, OperatingSystem.IsWindows());

    /// <summary>
    /// The reason this entry cannot be used, or null: the path (<see cref="PathProblem"/>), an auth, access or store word out of
    /// range, a <c>runas</c> without a <c>DOMAIN\name</c> or UPN account, a Credential Manager store under <c>windows</c>. Pure.
    /// </summary>
    [JsonIgnore]
    public string? Problem
    {
        get
        {
            if (PathProblem(Path) is { } path)
            {
                return path;
            }

            string auth = Auth?.Trim() ?? "";
            if (auth.Length > 0 && !auth.Equals(WindowsAuth, StringComparison.OrdinalIgnoreCase) && !auth.Equals(RunAsAuth, StringComparison.OrdinalIgnoreCase))
            {
                return UncText.BadAuth(auth);
            }

            string access = Access?.Trim() ?? "";
            if (access.Length > 0 && !access.Equals(ReadAccess, StringComparison.OrdinalIgnoreCase) && !access.Equals(ReadWriteAccess, StringComparison.OrdinalIgnoreCase))
            {
                return UncText.BadAccess(access);
            }

            string store = PasswordStore?.Trim() ?? "";
            if (store.Length > 0 && !store.Equals(FileStore, StringComparison.OrdinalIgnoreCase) && !store.Equals(CredmanStore, StringComparison.OrdinalIgnoreCase))
            {
                return SqlText.BadPasswordStore(store);
            }

            if (IsRunAs)
            {
                if (string.IsNullOrWhiteSpace(User))
                {
                    return UncText.RunAsNoUserFor(OperatingSystem.IsWindows());
                }

                if (RunAsAccountProblem(User, OperatingSystem.IsWindows()) is { } account)
                {
                    return account;
                }
            }
            else if (InCredentialManager)
            {
                return UncText.CredmanWithWindows;
            }

            return null;
        }
    }

    /// <summary>
    /// What is worth knowing about a usable entry, or null (the wizard's notice, <c>unc_shares</c>' line): a <c>runas</c> on a local
    /// folder (the other account's credentials only count on the network), or a drive letter that is a mapped network drive (a
    /// mapping belongs to a logon session — a <c>runas</c> token's, or an elevated app's, may not see it; the UNC path always works).
    /// </summary>
    [JsonIgnore]
    public string? Warning
    {
        get
        {
            if (Problem is not null)
            {
                return null;
            }

            if (IsUnc)
            {
                return IsRunAs && !OperatingSystem.IsWindows() ? UncText.RunAsMountedWarning(User!.Trim()) : null;
            }

            if (OperatingSystem.IsWindows() && IsMappedDrive(Root))
            {
                return UncText.MappedDriveWarning(Root[..2]);
            }

            return IsRunAs ? UncText.RunAsLocalWarning : null;
        }
    }

    /// <summary>
    /// Why <paramref name="path"/> cannot be a share's root on this machine, or null (<see cref="PathProblem(string?, bool)"/>).
    /// </summary>
    public static string? PathProblem(string? path) => PathProblem(path, OperatingSystem.IsWindows());

    /// <summary>
    /// Why <paramref name="path"/> cannot be a share's root, or null. It must be absolute. On Windows: <c>\\server\share</c> (a folder
    /// under it too; a Mac's <c>smb://</c> reads as it) or a fully qualified local folder <c>X:\folder</c> — never a device path
    /// (<c>\\?\</c>, <c>\\.\</c>), a drive-relative <c>C:folder</c>, a rooted <c>\folder</c>, a whole drive (<c>D:\</c>: a share is a
    /// folder), or a stream (<c>:</c> past the drive). On a Mac (2026-10-11): <c>smb://server/share</c> (or <c>\\…</c>, <c>//…</c>) or
    /// an absolute <c>/folder</c> — never a device path, a drive path (a Windows entry in a shared file: skipped, said why), <c>/</c>
    /// itself, or a <c>..</c> above the share. Pure: nothing is touched.
    /// </summary>
    public static string? PathProblem(string? path, bool windows)
    {
        string text = UncPath.Spell(path, windows);
        if (text.Length == 0)
        {
            return UncText.NoPath;
        }

        if (UncPath.IsDevicePath(path ?? ""))
        {
            return windows ? UncText.DevicePath(text) : UncText.DevicePathHere((path ?? "").Trim());
        }

        if (!windows)
        {
            return MacPathProblem(text);
        }

        if (text.StartsWith(@"\\", StringComparison.Ordinal))
        {
            string[] parts = text[2..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                return UncText.NoShareName(text);
            }

            if (text[2..].Contains(':', StringComparison.Ordinal))
            {
                return UncText.BadPath(text);
            }
        }
        else
        {
            if (text.Length < 3 || !char.IsAsciiLetter(text[0]) || text[1] != ':' || text[2] != '\\')
            {
                return UncText.NotAbsolute(text);
            }

            if (text[2..].Contains(':', StringComparison.Ordinal))
            {
                return UncText.BadPath(text);
            }

            if (text[3..].Trim('\\').Length == 0)
            {
                return UncText.WholeDrive(text[..2]);
            }
        }

        string folded;
        try
        {
            folded = UncPath.Normalize(text, windows: true);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return UncText.BadPath(text);
        }

        // The whole drive judged by the folded path (2026-10-22, review V8: C:\Temp\.. and C:\. passed the spelling's check and rooted the
        // share at C:\), as a Mac's side folds first.
        if (UncPath.IsDrivePath(folded) && folded[3..].Trim('\\').Length == 0)
        {
            return UncText.WholeDrive(folded[..2]);
        }

        return null;
    }

    private static string? MacPathProblem(string text)
    {
        if (UncPath.IsDrivePath(text))
        {
            return UncText.DriveNotHere(text);
        }

        if (UncPath.IsNetwork(text, windows: false))
        {
            try
            {
                return UncPath.Split(text) is null ? UncText.NoShareNameHere(text) : null;
            }
            catch (ArgumentException)
            {
                return UncText.BadPath(text);
            }
        }

        if (!text.StartsWith('/'))
        {
            return UncText.NotAbsoluteHere(text);
        }

        try
        {
            UncPath.Normalize(text, windows: false);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return UncText.BadPath(text);
        }

        return FoldsToRoot(text) ? UncText.WholeDisk : null;
    }

    /// <summary>
    /// Whether a POSIX path folds to <c>/</c> (its <c>.</c> and <c>..</c> folded, a <c>..</c> at the top staying there), read the same on
    /// any OS (2026-10-11, the shopping-list merge's Windows run): <see cref="Path.GetFullPath(string)"/> on Windows reads <c>/</c> as the
    /// current drive's root, so comparing <see cref="UncPath.Normalize"/>'s answer with <c>/</c> held only on a Mac.
    /// </summary>
    private static bool FoldsToRoot(string path)
    {
        int depth = 0;
        foreach (string part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            depth = part switch { "." => depth, ".." => Math.Max(0, depth - 1), _ => depth + 1 };
        }

        return depth == 0;
    }

    /// <summary>
    /// Why <paramref name="user"/> cannot be a <c>runas</c> account, or null. Windows (and a file shared with it) wants
    /// <c>DOMAIN\name</c> or <c>name@domain</c>, <see cref="WindowsCredentials.SplitAccount"/>'s rule. A Mac mounts the share as the
    /// account (2026-10-11), so a server's own local account (<c>name</c>) is one too.
    /// </summary>
    public static string? RunAsAccountProblem(string? user, bool windows)
    {
        string trimmed = (user ?? "").Trim();
        if (trimmed.Length == 0)
        {
            return UncText.RunAsNoUserFor(windows);
        }

        if (windows)
        {
            return WindowsCredentials.SplitAccount(trimmed) is null ? SqlText.RunAsNeedsDomain(trimmed) : null;
        }

        return trimmed.Any(char.IsWhiteSpace) || trimmed.Contains(';', StringComparison.Ordinal) ? UncText.BadAccountHere(trimmed) : null;
    }

    /// <summary><paramref name="path"/> as a root in this machine's spelling: full, folded, no trailing separator (<see cref="UncPath.Normalize"/>).</summary>
    public static string NormalizeRoot(string path)
    {
        bool windows = OperatingSystem.IsWindows();
        return UncPath.Normalize(UncPath.Spell(path, windows), windows);
    }

    private static bool IsMappedDrive(string root)
    {
        try
        {
            return new DriveInfo(root[..2]).DriveType == DriveType.Network;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>A share by its name, and the file it came from.</summary>
public sealed record UncNamedShare(string Name, UncShareConfig Config, string Source);
