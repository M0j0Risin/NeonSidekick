using System.Globalization;

namespace NeonSidekick.Unc;

/// <summary>One mounted file system as <c>statfs</c> reports it: its id, who mounted it, its type, where from and where on, and whether it is read-only (<c>MNT_RDONLY</c>).</summary>
public sealed record MountInfo(long Fsid, uint Owner, string Type, string From, string On, bool ReadOnly = false);

/// <summary>One mount the app made, as its record keeps it: the app that made it (pid and start), where, which file system, from where.</summary>
public sealed record MountRecord(int OwnerPid, long OwnerStartTicks, string MountPoint, long Fsid, string From);

/// <summary>
/// The decisions behind a Mac's share mounts (2026-10-11, UNC shares on a Mac), pure so every OS pins them: the URL NetFS is
/// given, which mounts are one, whose a mount is, the records the sweep reads, and the sentence for a mount that failed.
/// <see cref="ShareMounts"/> acts on them over <see cref="IMountSystem"/>.
/// </summary>
public static class MacMountRules
{
    /// <summary><c>statfs</c>'s type name for an SMB mount.</summary>
    public const string SmbType = "smbfs";

    /// <summary>The folder under the home the records sit in. Pinned.</summary>
    public const string FolderName = "mounts";

    /// <summary>
    /// The URL NetFS mounts for <paramref name="server"/> and <paramref name="share"/>: <c>smb://server/share</c>, the share's name
    /// percent-encoded (spaces, a Mac's curly apostrophe in "Name’s Public Folder"), the server as written (a port stays).
    /// </summary>
    public static string MountUrl(string server, string share) => UncPath.SmbScheme + server + "/" + Uri.EscapeDataString(share);

    /// <summary>
    /// Which mounts are one: the server and share (case-insensitive, as SMB is), the account (as you when null), and whether it is
    /// read-only — shares that differ only in the folder under the share use one mount.
    /// </summary>
    public static string Key(string server, string share, string? account, bool readOnly) =>
        string.Join('|', server.ToLowerInvariant(), share.ToLowerInvariant(), account?.Trim().ToLowerInvariant() ?? "", readOnly ? "ro" : "rw");

    /// <summary>
    /// The user a mount's <c>f_mntfromname</c> names (<c>//name@server/share</c> → <c>name</c>, percent-decoded), or null when it
    /// names none.
    /// </summary>
    public static string? FromUser(string from)
    {
        string text = (from ?? "").Trim();
        if (!text.StartsWith("//", StringComparison.Ordinal))
        {
            return null;
        }

        int at = text.IndexOf('@', StringComparison.Ordinal);
        int slash = text.IndexOf('/', 2);
        return at > 2 && (slash < 0 || at < slash) ? Uri.UnescapeDataString(text[2..at]) : null;
    }

    /// <summary>
    /// Whether a mount from <paramref name="from"/> is signed in as <paramref name="account"/>: its user is the account, the part
    /// before an <c>@</c>, or the part after a <c>\</c> or <c>;</c> (the SMB client keeps the name a server answered with). Case-insensitive.
    /// </summary>
    public static bool IsSignedInAs(string from, string account)
    {
        if (FromUser(from) is not { } user)
        {
            return false;
        }

        string name = account.Trim();
        int cut = Math.Max(name.LastIndexOf('\\'), name.LastIndexOf(';'));
        string bare = cut >= 0 ? name[(cut + 1)..] : name;
        int at = bare.IndexOf('@', StringComparison.Ordinal);
        string local = at > 0 ? bare[..at] : bare;
        int userCut = Math.Max(user.LastIndexOf('\\'), user.LastIndexOf(';'));
        string mounted = userCut >= 0 ? user[(userCut + 1)..] : user;
        return mounted.Equals(name, StringComparison.OrdinalIgnoreCase)
            || mounted.Equals(bare, StringComparison.OrdinalIgnoreCase)
            || mounted.Equals(local, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The mount of <paramref name="server"/>'s <paramref name="share"/> among <paramref name="mounts"/> (its <c>f_mntfromname</c>,
    /// <c>//user@server/share</c> with the share percent-encoded, compared without case), or null: what EEXIST meant. With
    /// <paramref name="readOnly"/> given, only a mount whose read-only flag is that (2026-10-11, code review: an <c>access: read</c>
    /// share never runs on a writable mount, nor a <c>readwrite</c> one on a read-only mount).
    /// </summary>
    public static MountInfo? Find(IEnumerable<MountInfo> mounts, string server, string share, bool? readOnly = null)
    {
        ArgumentNullException.ThrowIfNull(mounts);
        string encoded = server + "/" + Uri.EscapeDataString(share);
        return mounts.FirstOrDefault(m => (readOnly is not { } ro || m.ReadOnly == ro)
            && (m.From.EndsWith("@" + encoded, StringComparison.OrdinalIgnoreCase)
                || string.Equals(m.From, "//" + encoded, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>A record's file name: the file system's id, so a mount has one record however often it is seen.</summary>
    public static string RecordName(long fsid) => fsid.ToString("x16", CultureInfo.InvariantCulture) + ".txt";

    /// <summary>The record's text: lines, as <c>LlamaRecords</c>' are. Pure, so the tests pin the format.</summary>
    public static string Format(MountRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return string.Create(CultureInfo.InvariantCulture, $"owner {record.OwnerPid} {record.OwnerStartTicks}\nfsid {record.Fsid}\nfrom {record.From}\non {record.MountPoint}\n");
    }

    /// <summary>A record read back from <paramref name="text"/>; null for anything that is not one whole record.</summary>
    public static MountRecord? Parse(string text)
    {
        (int Pid, long Ticks)? owner = null;
        long? fsid = null;
        string? from = null, on = null;
        foreach (var raw in (text ?? "").Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.StartsWith("from ", StringComparison.Ordinal))
            {
                from = line["from ".Length..];
            }
            else if (line.StartsWith("on ", StringComparison.Ordinal))
            {
                on = line["on ".Length..];
            }
            else if (line.StartsWith("fsid ", StringComparison.Ordinal) && long.TryParse(line["fsid ".Length..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long id))
            {
                fsid = id;
            }
            else if (line.Split(' ') is ["owner", var pid, var ticks]
                && int.TryParse(pid, NumberStyles.None, CultureInfo.InvariantCulture, out int p)
                && long.TryParse(ticks, NumberStyles.None, CultureInfo.InvariantCulture, out long t))
            {
                owner = (p, t);
            }
        }

        return owner is { } o && fsid is { } f && !string.IsNullOrEmpty(from) && !string.IsNullOrEmpty(on)
            ? new MountRecord(o.Pid, o.Ticks, on, f, from)
            : null;
    }

    /// <summary>
    /// Whether the sweep unmounts what <paramref name="record"/> names: its app is gone (<paramref name="ownerAlive"/> false), and
    /// the mount there now is the very one recorded — the same file system id, an SMB mount, made by this user — so a mount the
    /// user made since at the same place is never touched.
    /// </summary>
    public static bool SweepTakes(MountRecord record, bool ownerAlive, MountInfo? now, uint uid)
    {
        ArgumentNullException.ThrowIfNull(record);
        return !ownerAlive && now is not null && now.Fsid == record.Fsid && now.Owner == uid
            && string.Equals(now.Type, SmbType, StringComparison.Ordinal)
            && string.Equals(now.On, record.MountPoint, StringComparison.Ordinal);
    }

    /// <summary>
    /// The sentence for a mount NetFS refused, from its status (<c>NetFS.h</c>: positive an errno, negative an OSStatus, the
    /// <c>ENETFS*</c> and <c>kNetAuth*</c> codes among them), never the message text.
    /// </summary>
    public static string Refusal(int status, UncNamedShare share, string account)
    {
        ArgumentNullException.ThrowIfNull(share);
        string root = share.Config.Root;
        return status switch
        {
            2 or -5998 or -6003 => UncText.RootMissing(share.Name, root),
            13 or 1 => UncText.AccessDenied(share.Name, root, account),
            51 or 60 or 61 or 64 or 65 => UncText.Unreachable(share.Name, root, ErrnoName(status)),
            80 or 81 or -5997 => UncText.BadAccount(share.Name, account),
            -5045 => UncText.AccountRefused(share.Name, account, UncText.MustChangePassword),
            -5046 => UncText.AccountRefused(share.Name, account, UncText.PasswordPolicy),
            -5999 => UncText.AccountRefused(share.Name, account, UncText.AccountRestricted),
            -5996 => UncText.ReachFailed(share.Name, root, UncText.NoSmbVersion),
            -6004 => UncText.ReachFailed(share.Name, root, UncText.NoGuest),
            _ => UncText.ReachFailed(share.Name, root, UncText.MountStatus(status)),
        };
    }

    private static string ErrnoName(int errno) => errno switch
    {
        51 => UncText.NetworkUnreachable,
        60 => UncText.ConnectionTimedOut,
        61 => UncText.ConnectionRefused,
        64 => UncText.HostDown,
        65 => UncText.NoRoute,
        _ => UncText.MountStatus(errno),
    };
}
