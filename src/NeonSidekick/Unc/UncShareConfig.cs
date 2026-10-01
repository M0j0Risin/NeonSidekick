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

    /// <summary>The share's root, full, <c>\</c>-spelled, without a trailing separator. Call only on an entry without a <see cref="Problem"/>.</summary>
    [JsonIgnore]
    public string Root => NormalizeRoot(Path!);

    /// <summary>Whether <see cref="Root"/> is a network path (<c>\\server\share…</c>) rather than a local folder.</summary>
    [JsonIgnore]
    public bool IsUnc => Root.StartsWith(@"\\", StringComparison.Ordinal);

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
                    return UncText.RunAsNoUser;
                }

                if (WindowsCredentials.SplitAccount(User) is null)
                {
                    return SqlText.RunAsNeedsDomain(User.Trim());
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
            if (Problem is not null || IsUnc)
            {
                return null;
            }

            if (IsMappedDrive(Root))
            {
                return UncText.MappedDriveWarning(Root[..2]);
            }

            return IsRunAs ? UncText.RunAsLocalWarning : null;
        }
    }

    /// <summary>
    /// Why <paramref name="path"/> cannot be a share's root, or null. It must be absolute: <c>\\server\share</c> (a folder under it
    /// too) or a fully qualified local folder <c>X:\folder</c> — never a device path (<c>\\?\</c>, <c>\\.\</c>), a drive-relative
    /// <c>C:folder</c>, a rooted <c>\folder</c>, a whole drive (<c>D:\</c>: a share is a folder), or a stream (<c>:</c> past the
    /// drive). Pure: nothing is touched.
    /// </summary>
    public static string? PathProblem(string? path)
    {
        string text = (path ?? "").Trim().Replace('/', '\\');
        if (text.Length == 0)
        {
            return UncText.NoPath;
        }

        if (text.StartsWith(@"\\?\", StringComparison.Ordinal) || text.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            return UncText.DevicePath(text);
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

        try
        {
            NormalizeRoot(text);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return UncText.BadPath(text);
        }

        return null;
    }

    /// <summary><paramref name="path"/> as a root: <c>/</c> as <c>\</c>, full (<c>..</c> folded), no trailing separator.</summary>
    public static string NormalizeRoot(string path) =>
        System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path.Trim().Replace('/', '\\')));

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
