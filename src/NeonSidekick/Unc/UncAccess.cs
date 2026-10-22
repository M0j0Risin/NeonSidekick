using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using NeonSidekick.Diagnostics;
using NeonSidekick.Files;
using NeonSidekick.Settings;
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
    private readonly IShareMounts? _mounts;

    /// <summary>
    /// The run's mounts on a Mac (2026-10-11): <c>Program</c> sets them once (a <see cref="ShareMounts"/> over NetFS), the way
    /// <c>DockerSocket.MachineEnvironment</c> is set, so every <see cref="UncAccess"/> — the screen's, <c>--unc-check</c>'s, the
    /// wizard's test — shares one set of mounts and one exit. Null on Windows and in tests that pass their own.
    /// </summary>
    public static IShareMounts? Mounts { get; set; }

    /// <param name="catalog">The offered shares, read afresh at each call (<see cref="UncConfigFile.LoadCatalog"/> narrowed by <c>UNC shares offered</c>).</param>
    /// <param name="time">The clock behind the dates the tools show.</param>
    /// <param name="mounts">What mounts a network share off Windows; <see cref="Mounts"/> when null.</param>
    public UncAccess(Func<UncCatalog> catalog, TimeProvider time, IShareMounts? mounts = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _mounts = mounts ?? Mounts;
    }

    /// <summary>The mounts this access reaches a Mac's network shares through, or null.</summary>
    public IShareMounts? MountsInUse => _mounts;

    /// <summary>The shares a call can name, read now.</summary>
    public UncCatalog Catalog() => _catalog();

    /// <summary>
    /// Whether the UNC group is offered (2026-09-30; here since 2026-10-01, when <c>open</c> asked it too): the setting
    /// <c>UNC tools</c> on and at least one share offered. A Mac was left out from 2026-10-06 to 2026-10-11, when it began
    /// reading <c>smb://</c> and <c>/folder</c> shares (<see cref="UncPath"/>) and mounting the network ones (<see cref="IShareMounts"/>).
    /// </summary>
    public bool IsOffered(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return effective.UncTools && Catalog().Shares.Count > 0;
    }

    /// <summary>
    /// The share and the path within it a call means: <paramref name="share"/> by name (case-insensitive); or, with none named and
    /// <paramref name="path"/> a full path, the share it lies in (<see cref="UncCatalog.Locate"/>); else the only share
    /// when just one is offered (2026-10-09: <c>UNC default share</c> gone; several and none named is refused). A full path under a named share must lie in it. On Windows a stream name (<c>:</c> past a drive) is refused. The path
    /// comes back as <see cref="WorkingDirectory.Resolve(string, bool, out string)"/> takes it over the share's files (<see cref="ForFiles"/>):
    /// relative, or full under the share — on a Mac a full path under a network share comes back relative to it, the files being
    /// under its mount point. Null with the sentence on a refusal.
    /// </summary>
    public UncNamedShare? Resolve(string? share, string? path, out string relative, out string? error)
    {
        relative = (path ?? "").Trim();
        error = null;
        var catalog = Catalog();
        if (catalog.Shares.Count == 0)
        {
            error = UncText.NoShares;
            return null;
        }

        bool windows = OperatingSystem.IsWindows();
        string spelled = UncPath.Spell(relative, windows);
        bool absolute = UncPath.IsAbsolute(spelled, windows);
        if (windows && (absolute ? spelled[2..] : spelled).Contains(':', StringComparison.Ordinal))
        {
            error = UncText.StreamPath(relative);
            return null;
        }

        UncNamedShare? found;
        if (!string.IsNullOrWhiteSpace(share))
        {
            found = catalog.Find(share);
            if (found is null)
            {
                error = UncText.UnknownShare(share.Trim(), Names(catalog));
                return null;
            }
        }
        else if (absolute)
        {
            found = catalog.Locate(spelled);
            if (found is null)
            {
                error = UncText.NotUnderAnyShare(relative, Names(catalog));
                return null;
            }
        }
        else
        {
            found = catalog.Find(null);
            if (found is null)
            {
                error = UncText.ShareNeeded(Names(catalog));
                return null;
            }
        }

        if (ForFiles(found, relative) is not { } within)
        {
            error = UncText.NotInShare(relative, found);
            return null;
        }

        relative = within;

        // The most specific share holding the target decides its access and account (2026-10-22, review J13: a read-only share nested in a
        // read-write one was writable by naming its parent).
        string joined = found.Config.Root + (windows ? "\\" : "/") + relative.TrimStart('\\', '/');
        if (relative is not ("" or ".") && catalog.Locate(joined) is { } deepest && !ReferenceEquals(deepest, found)
            && deepest.Config.Root.Length > found.Config.Root.Length && WorkingDirectory.IsInside(found.Config.Root, deepest.Config.Root)
            && ForFiles(deepest, joined) is { } under)
        {
            found = deepest;
            relative = under;
        }

        return found;
    }

    /// <summary>
    /// <paramref name="path"/> as the share's <see cref="WorkingDirectory"/> takes it, or null for a full path outside the share. A
    /// relative path is kept as given; a full one is spelled for the platform (<see cref="UncPath.Spell"/>) and must lie under the
    /// share's root. On a Mac a full path under a network share (<c>smb://fs01/eng/specs/a.md</c>, <c>\\fs01\eng\specs\a.md</c>)
    /// becomes the path under it (<c>specs/a.md</c>; <c>.</c> for the root), since the files are under the share's mount point, never
    /// under its <c>smb://</c> name. Pure.
    /// </summary>
    public static string? ForFiles(UncNamedShare share, string path)
    {
        ArgumentNullException.ThrowIfNull(share);
        bool windows = OperatingSystem.IsWindows();
        string spelled = UncPath.Spell(path, windows);
        if (!UncPath.IsAbsolute(spelled, windows))
        {
            // A network share's relative path in Windows' spelling on a Mac (2026-10-22, review H17: reports\q3.md became one file named
            // with a backslash at the share's root): the server's names never hold one, so it is a separator there.
            string relative = (path ?? "").Trim();
            return !windows && share.Config.IsUnc ? relative.Replace('\\', '/') : relative;
        }

        string root = share.Config.Root;
        string full;
        try
        {
            full = UncPath.Normalize(spelled, windows);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            if (!windows)
            {
                return null;
            }

            full = spelled;
        }

        if (!WorkingDirectory.IsInside(root, full))
        {
            return null;
        }

        if (windows || !share.Config.IsUnc)
        {
            return spelled;
        }

        // The tail as the mount names it: Normalize keeps a % as %25, every other character decoded.
        return full.Length == root.Length ? "." : Uri.UnescapeDataString(full[(root.Length + 1)..]);
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

    /// <summary>
    /// The audit line with where it went (<paramref name="to"/>, a move's or copy's other end) and, under <paramref name="partial"/>, that
    /// the change failed partway (2026-10-22, review J8: only a finished change was logged, so a recursive delete or a merging copy that
    /// failed partway left permanent changes unlogged, and a move never named where it went).
    /// </summary>
    public static void Audit(UncNamedShare share, string action, string path, string? to, bool partial = false)
    {
        ArgumentNullException.ThrowIfNull(share);
        DiagnosticLog.Info(UncConfigFile.Category, UncText.AuditLogLine(share.Name, Account(share), action, path, to, partial));
    }

    /// <summary>Who a share is reached as: the <c>runas</c> account, else the user running the app.</summary>
    public static string Account(UncNamedShare share)
    {
        ArgumentNullException.ThrowIfNull(share);
        return share.Config.IsRunAs ? share.Config.User!.Trim() : OperatingSystem.IsWindows() ? Environment.UserDomainName + "\\" + Environment.UserName : Environment.UserName;
    }

    /// <summary>
    /// Runs <paramref name="act"/> against <paramref name="share"/>'s root as the share's account: the password resolved and a
    /// netonly token taken under <c>runas</c>, then the preflight and the work on a pool thread that owns the token. A read
    /// (<paramref name="write"/> false) is abandoned when <paramref name="cancellationToken"/> fires (it throws); a write is waited
    /// out. A refusal before the work — no password, no token, an unreachable or refusing root — is the result's sentence.
    /// <paramref name="signIn"/> false (2026-10-01, <c>open</c>) runs it as the user even on a <c>runas</c> share: no token taken.
    /// On a Mac (2026-10-11) a network share is mounted first (<see cref="IShareMounts"/>, on the same pool thread, under the
    /// <c>runas</c> account with its password) and the work runs over the mount; a local folder is read as the user, as Windows reads one.
    /// </summary>
    public async Task<UncResult<T>> RunAsync<T>(UncNamedShare share, bool write, Func<WorkingDirectory, T> act, CancellationToken cancellationToken, bool signIn = true)
    {
        ArgumentNullException.ThrowIfNull(share);
        ArgumentNullException.ThrowIfNull(act);
        Task<UncResult<T>> work;
        if (!OperatingSystem.IsWindows())
        {
            if (!share.Config.IsUnc)
            {
                work = Task.Run(() => Execute(null, share, Files(share), act), CancellationToken.None);
            }
            else if (_mounts is not { } mounts)
            {
                return UncResult<T>.Failed(UncText.NoMountsHere(share.Name));
            }
            else
            {
                work = Task.Run(() => Mounted(mounts, share, act, write), CancellationToken.None);
            }

            return write ? await work.ConfigureAwait(false) : await work.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        SafeAccessTokenHandle? token = null;
        if (share.Config.IsRunAs && signIn)
        {
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

        var files = Files(share);
        work = Task.Run(() => Execute(token, share, files, act), CancellationToken.None);
        return write ? await work.ConfigureAwait(false) : await work.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A <see cref="WorkingDirectory"/> over <paramref name="share"/>'s root, the share's options. Nothing is touched.</summary>
    public WorkingDirectory Files(UncNamedShare share)
    {
        ArgumentNullException.ThrowIfNull(share);
        return Files(share.Config.Root);
    }

    private WorkingDirectory Files(string root) => new(() => root, _time, WorkingDirectoryOptions.Share);

    /// <summary>
    /// The pool thread's part on a Mac's network share: the mount (made now, or the run's), then the preflight and the work under it.
    /// The <c>runas</c> password is resolved only when a mount is to be made (2026-10-11, code review).
    /// </summary>
    private UncResult<T> Mounted<T>(IShareMounts mounts, UncNamedShare share, Func<WorkingDirectory, T> act, bool write)
    {
        // A write call mounts read-write and lets a runas mount go when it ends (2026-10-22, review H4).
        var root = mounts.Root(share, () => SignIn(share), writable: write);
        if (root.Error is { } refused)
        {
            return UncResult<T>.Failed(refused);
        }

        try
        {
            return Preflight(share, root.Value!) is { } reason ? UncResult<T>.Failed(reason) : UncResult<T>.Ok(act(Files(root.Value!)));
        }
        finally
        {
            if (write)
            {
                mounts.EndWrite(share);
            }
        }
    }

    /// <summary>A Mac's mount's password: none as you, the stored one under <c>runas</c>, or the refusal.</summary>
    private static UncResult<string?> SignIn(UncNamedShare share)
    {
        if (!share.Config.IsRunAs)
        {
            return UncResult<string?>.Ok(null);
        }

        var secret = UncSecrets.Resolve(share);
        return secret.Error is { } missing ? UncResult<string?>.Failed(UncText.CannotSignIn(share.Name, missing)) : UncResult<string?>.Ok(secret.Value);
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

        UncResult<T> Body() => Preflight(share, share.Config.Root, token) is { } reason ? UncResult<T>.Failed(reason) : UncResult<T>.Ok(act(files));
    }

    [SupportedOSPlatform("windows")]
    private static UncResult<T> Impersonated<T>(SafeAccessTokenHandle token, Func<UncResult<T>> body) => WindowsIdentity.RunImpersonated(token, body);

    /// <summary>
    /// The root answers, or the sentence that says why not: its attributes read under the caller's token (the read itself on a
    /// task of its own, which the execution context keeps impersonated) and waited for <see cref="ReachTimeout"/> at most. Not a
    /// folder, missing, unreachable, refused — each named from the Win32 code.
    /// </summary>
    internal static string? Preflight(UncNamedShare share) => Preflight(share, share.Config.Root);

    /// <summary>
    /// <see cref="Preflight(UncNamedShare)"/> on the folder <paramref name="folder"/> the share's files are under (a Mac's mount
    /// point and the folder under it), the sentences naming the share's own root.
    /// </summary>
    internal static string? Preflight(UncNamedShare share, string folder) => Preflight(share, folder, null);

    /// <summary>
    /// <see cref="Preflight(UncNamedShare, string)"/> with the probe on a thread of its own, impersonating <paramref name="token"/> itself
    /// (2026-10-22, review H14: the probe was a pool task inheriting the caller's impersonation, and the token was disposed at the timeout
    /// while a late-starting probe still meant to use it — the probe now holds a reference to the handle until it ends; review P11: an
    /// abandoned probe kept a pool thread in SMB about a minute, and retries against a dead server starved the pool). A folder whose last
    /// probe is still stuck is answered as unreachable at once, no second thread.
    /// </summary>
    internal static string? Preflight(UncNamedShare share, string folder, SafeAccessTokenHandle? token)
    {
        string root = share.Config.Root;
        if (s_stuckProbes.TryGetValue(folder, out var stuck) && !stuck.IsCompleted)
        {
            return UncText.Unreachable(share.Name, root, UncText.TimedOut(ReachTimeout));
        }

        var probe = StartProbe(folder, token);
        try
        {
            if (!probe.Wait(ReachTimeout))
            {
                s_stuckProbes[folder] = probe;
                _ = probe.ContinueWith(static (done, key) => s_stuckProbes.TryRemove(new KeyValuePair<string, Task<FileAttributes>>((string)key!, done)), folder,
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                return UncText.Unreachable(share.Name, root, UncText.TimedOut(ReachTimeout));
            }

            return (probe.Result & FileAttributes.Directory) != 0 ? null : UncText.NotAFolder(share.Name, root);
        }
        catch (AggregateException ex) when (ex.InnerException is { } inner && IsReachFailure(inner))
        {
            return ReachReason(share, root, inner);
        }
    }

    /// <summary>The probes given up at their timeout and not yet ended, by folder (review P11).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<FileAttributes>> s_stuckProbes =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary>
    /// <c>File.GetAttributes(folder)</c> on a background thread of its own, nothing flowing into it, impersonating <paramref name="token"/>
    /// when there is one; the handle is held (<see cref="System.Runtime.InteropServices.SafeHandle.DangerousAddRef"/>) until the read ends,
    /// so the caller's dispose never closes it under the probe (review H14).
    /// </summary>
    private static Task<FileAttributes> StartProbe(string folder, SafeAccessTokenHandle? token)
    {
        var done = new TaskCompletionSource<FileAttributes>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool held = false;
        token?.DangerousAddRef(ref held);
        var thread = new Thread(() =>
        {
            try
            {
                done.SetResult(held && OperatingSystem.IsWindows() ? ReadImpersonated(token!, folder) : File.GetAttributes(folder));
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
            finally
            {
                if (held)
                {
                    token!.DangerousRelease();
                }
            }
        })
        {
            IsBackground = true,
            Name = "UNC preflight",
        };

        try
        {
            using (ExecutionContext.SuppressFlow())
            {
                thread.Start();
            }
        }
        catch
        {
            if (held)
            {
                token!.DangerousRelease();
            }

            throw;
        }

        return done.Task;
    }

    [SupportedOSPlatform("windows")]
    private static FileAttributes ReadImpersonated(SafeAccessTokenHandle token, string folder) =>
        WindowsIdentity.RunImpersonated(token, () => File.GetAttributes(folder));

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

    private static string Names(UncCatalog catalog) => string.Join(", ", catalog.Shares.Select(s => s.Name));
}
