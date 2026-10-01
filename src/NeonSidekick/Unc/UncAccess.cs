using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using NeonSidekick.Diagnostics;
using NeonSidekick.Files;
using NeonSidekick.Sql;

namespace NeonSidekick.Unc;

/// <summary>What one UNC call came to: its value, or the sentence that says why not (never both).</summary>
public readonly record struct UncResult<T>(T? Value, string? Error)
{
    public static UncResult<T> Ok(T value) => new(value, null);

    public static UncResult<T> Failed(string error) => new(default, error);
}

/// <summary>
/// The UNC tools' one way in (2026-09-30, the user's ask: SQL's integrated auth and run-as, for file shares): the share a call
/// means, then the call itself run against a <see cref="WorkingDirectory"/> over the share's root under
/// <see cref="WorkingDirectoryOptions.Share"/> — so the sandbox's path check, caps and outcomes are the working directory's own —
/// as the account the share names.
///
/// <para><b>The sign-in.</b> A <c>windows</c> share is reached as the user running the app. A <c>runas</c> share gets a
/// <c>LOGON32_LOGON_NEW_CREDENTIALS</c> token per call (<see cref="WindowsCredentials.LogonNetOnly"/>, <c>runas /netonly</c>'s): the
/// app's own identity locally, the other account's credentials for every network sign-in made under it. That token is a logon
/// session of its own, so its SMB session never meets the user's mapped drives — no "multiple connections to a server by the same
/// user" (1219) — and nothing is left behind in <c>net use</c>: no <c>WNetAddConnection2</c>, no drive letter. A local folder under
/// <c>runas</c> is read as the app's own user (netonly credentials only count on the network).</para>
///
/// <para><b>The thread.</b> The work runs on a pool thread inside <see cref="WindowsIdentity.RunImpersonated{T}(SafeAccessTokenHandle, Func{T})"/>,
/// whole and synchronous, and that thread owns the token: it disposes it when the work is done. .NET flows the impersonation
/// through the execution context, so a content search's parallel readers run as the account too (pinned by a test). A read the
/// user cancels (ESC) is abandoned — its walk finishes in the background, the token disposed when it does, never under it; a
/// write is always waited out.</para>
///
/// <para><b>The reach.</b> Before the work, a preflight (<see cref="ReachTimeout"/>) reads the root's attributes under the same
/// token and turns a refusal into the sentence that names it — unreachable, bad account or password, access denied, no logon
/// server — from the Win32 code, never the message text (Windows words those in its own UI language).</para>
/// </summary>
public sealed class UncAccess
{
    /// <summary>How long the preflight waits for the root to answer before the call is given up as unreachable.</summary>
    public static readonly TimeSpan ReachTimeout = TimeSpan.FromSeconds(10);

    private readonly Func<UncCatalog> _catalog;
    private readonly TimeProvider _time;

    /// <param name="catalog">The offered shares, read afresh at each call (<see cref="UncConfigFile.LoadCatalog"/> narrowed by <c>UNC shares offered</c>).</param>
    /// <param name="time">The clock behind the dates the tools show.</param>
    public UncAccess(Func<UncCatalog> catalog, TimeProvider time)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <summary>The shares a call can name, read now.</summary>
    public UncCatalog Catalog() => _catalog();

    /// <summary>
    /// The share and the path within it a call means: <paramref name="share"/> by name (case-insensitive); or, with none named and
    /// <paramref name="path"/> a full path, the share it lies in (<see cref="UncCatalog.Locate"/>); else <paramref name="defaultShare"/>,
    /// else the first. A full path under a named share must lie in it. A stream name (<c>:</c> past a drive) is refused. The path
    /// comes back as the call gave it (relative, or full under the share — <see cref="WorkingDirectory.Resolve(string, bool, out string)"/>
    /// takes either). Null with the sentence on a refusal.
    /// </summary>
    public UncNamedShare? Resolve(string? share, string? path, string? defaultShare, out string relative, out string? error)
    {
        relative = (path ?? "").Trim();
        error = null;
        var catalog = Catalog();
        if (catalog.Shares.Count == 0)
        {
            error = UncText.NoShares;
            return null;
        }

        string spelled = relative.Replace('/', '\\');
        bool absolute = UncCatalog.IsAbsolute(spelled);
        if ((absolute ? spelled[2..] : spelled).Contains(':', StringComparison.Ordinal))
        {
            error = UncText.StreamPath(relative);
            return null;
        }

        if (absolute)
        {
            relative = spelled;
        }

        if (!string.IsNullOrWhiteSpace(share))
        {
            var named = catalog.Find(share, null);
            if (named is null)
            {
                error = UncText.UnknownShare(share.Trim(), Names(catalog));
                return null;
            }

            if (absolute && !WorkingDirectory.IsInside(named.Config.Root, SafeFull(spelled)))
            {
                error = UncText.NotInShare(relative, named);
                return null;
            }

            return named;
        }

        if (absolute)
        {
            var located = catalog.Locate(spelled);
            if (located is null)
            {
                error = UncText.NotUnderAnyShare(relative, Names(catalog));
            }

            return located;
        }

        return catalog.Find(null, defaultShare);
    }

    /// <summary>
    /// Why a write to <paramref name="share"/> is refused, or null: <c>UNC writes</c> off (<paramref name="writesOn"/>), or a share
    /// whose <c>access</c> is not <c>readwrite</c> — both keys are checked at every call, whatever the offered tools say.
    /// </summary>
    public static string? WriteRefusal(UncNamedShare share, bool writesOn)
    {
        ArgumentNullException.ThrowIfNull(share);
        return !writesOn ? UncText.WritesOff : !share.Config.IsReadWrite ? UncText.ReadOnlyShare(share.Name) : null;
    }

    /// <summary>The audit line of a change on a share (every write, move, delete and put): the share, the account, what, where.</summary>
    public static void Audit(UncNamedShare share, string action, string path)
    {
        ArgumentNullException.ThrowIfNull(share);
        DiagnosticLog.Info(UncConfigFile.Category, UncText.AuditLogLine(share.Name, Account(share), action, path));
    }

    /// <summary>Who a share is reached as: the <c>runas</c> account, else the user running the app.</summary>
    public static string Account(UncNamedShare share)
    {
        ArgumentNullException.ThrowIfNull(share);
        return share.Config.IsRunAs ? share.Config.User!.Trim() : Environment.UserDomainName + "\\" + Environment.UserName;
    }

    /// <summary>
    /// Runs <paramref name="act"/> against <paramref name="share"/>'s root as the share's account: the password resolved and a
    /// netonly token taken under <c>runas</c>, then the preflight and the work on a pool thread that owns the token. A read
    /// (<paramref name="write"/> false) is abandoned when <paramref name="cancellationToken"/> fires (it throws); a write is waited
    /// out. A refusal before the work — no password, no token, an unreachable or refusing root — is the result's sentence.
    /// </summary>
    public async Task<UncResult<T>> RunAsync<T>(UncNamedShare share, bool write, Func<WorkingDirectory, T> act, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(share);
        ArgumentNullException.ThrowIfNull(act);
        var files = Files(share);
        SafeAccessTokenHandle? token = null;
        if (share.Config.IsRunAs)
        {
            if (!OperatingSystem.IsWindows())
            {
                return UncResult<T>.Failed(UncText.CannotSignIn(share.Name, WindowsCredentials.NotWindows));
            }

            var secret = UncSecrets.Resolve(share);
            if (secret.Error is { } missing)
            {
                return UncResult<T>.Failed(UncText.CannotSignIn(share.Name, missing));
            }

            token = WindowsCredentials.LogonNetOnly(share.Config.User!, secret.Value!, out string? refused);
            if (token is null)
            {
                return UncResult<T>.Failed(UncText.CannotSignIn(share.Name, refused!));
            }

            DiagnosticLog.Debug(UncConfigFile.Category, SqlText.RunAsLogLine(share.Name, share.Config.User!.Trim()));
        }

        var work = Task.Run(() => Execute(token, share, files, act), CancellationToken.None);
        return write ? await work.ConfigureAwait(false) : await work.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A <see cref="WorkingDirectory"/> over <paramref name="share"/>'s root, the share's options. Nothing is touched.</summary>
    public WorkingDirectory Files(UncNamedShare share)
    {
        ArgumentNullException.ThrowIfNull(share);
        string root = share.Config.Root;
        return new WorkingDirectory(() => root, _time, WorkingDirectoryOptions.Share);
    }

    /// <summary>The pool thread's part: the token (when there is one) impersonated around the preflight and the work, and disposed after.</summary>
    private static UncResult<T> Execute<T>(SafeAccessTokenHandle? token, UncNamedShare share, WorkingDirectory files, Func<WorkingDirectory, T> act)
    {
        using (token)
        {
            if (token is null || !OperatingSystem.IsWindows())
            {
                return Body();
            }

            return Impersonated(token, Body);
        }

        UncResult<T> Body() => Preflight(share) is { } reason ? UncResult<T>.Failed(reason) : UncResult<T>.Ok(act(files));
    }

    [SupportedOSPlatform("windows")]
    private static UncResult<T> Impersonated<T>(SafeAccessTokenHandle token, Func<UncResult<T>> body) => WindowsIdentity.RunImpersonated(token, body);

    /// <summary>
    /// The root answers, or the sentence that says why not: its attributes read under the caller's token (the read itself on a
    /// task of its own, which the execution context keeps impersonated) and waited for <see cref="ReachTimeout"/> at most. Not a
    /// folder, missing, unreachable, refused — each named from the Win32 code.
    /// </summary>
    internal static string? Preflight(UncNamedShare share)
    {
        string root = share.Config.Root;
        var probe = Task.Run(() => File.GetAttributes(root));
        try
        {
            if (!probe.Wait(ReachTimeout))
            {
                return UncText.Unreachable(share.Name, root, UncText.TimedOut(ReachTimeout));
            }

            return (probe.Result & FileAttributes.Directory) != 0 ? null : UncText.NotAFolder(share.Name, root);
        }
        catch (AggregateException ex) when (ex.InnerException is { } inner && IsReachFailure(inner))
        {
            return ReachReason(share, root, inner);
        }
    }

    private static bool IsReachFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException;

    /// <summary>The sentence for a root that refused, from the low word of the Win32 HRESULT.</summary>
    internal static string ReachReason(UncNamedShare share, string root, Exception ex)
    {
        int code = (ex.HResult >> 16 & 0x1FFF) == 7 ? ex.HResult & 0xFFFF : 0;
        if (ex is UnauthorizedAccessException && code == 0)
        {
            code = 5;
        }

        if (ex is FileNotFoundException or DirectoryNotFoundException && code == 0)
        {
            code = 3;
        }

        return code switch
        {
            2 or 3 => UncText.RootMissing(share.Name, root),
            5 => UncText.AccessDenied(share.Name, root, Account(share)),
            53 or 67 or 64 or 1231 or 1222 => UncText.Unreachable(share.Name, root, LogText.Excerpt(ex.Message)),
            1326 or 86 => UncText.BadAccount(share.Name, Account(share)),
            1311 => UncText.NoLogonServer(share.Name),
            1385 or 1327 or 1330 or 1331 or 1909 or 1907 => UncText.AccountRefused(share.Name, Account(share), LogText.Excerpt(ex.Message)),
            _ => UncText.ReachFailed(share.Name, root, LogText.Excerpt(ex.Message)),
        };
    }

    private static string SafeFull(string path)
    {
        try
        {
            return UncShareConfig.NormalizeRoot(path);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return path;
        }
    }

    private static string Names(UncCatalog catalog) => string.Join(", ", catalog.Shares.Select(s => s.Name));
}
