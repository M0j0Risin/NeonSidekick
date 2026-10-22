using NeonSidekick.Diagnostics;
using NeonSidekick.EmbeddedLlm;

namespace NeonSidekick.Unc;

/// <summary>
/// What reaches a network share where no path does (2026-10-11, a Mac): the folder its files are under, mounting it first.
/// <see cref="UncAccess"/> asks it for every call on an <c>smb://</c> share off Windows; Windows never has one.
/// </summary>
public interface IShareMounts
{
    /// <summary>
    /// The folder <paramref name="share"/>'s root is under now — its mount point and the folder under the share — mounted first
    /// as the share's account, or the refusal. <paramref name="signIn"/> gives the password (null as you) or the refusal; it is
    /// asked only when a mount is to be made (2026-10-11, code review: a mount already there needs no Keychain read, and goes on
    /// serving while the stored password is gone). Blocks for the mount; call it on a pool thread.
    /// </summary>
    UncResult<string> Root(UncNamedShare share, Func<UncResult<string?>> signIn);

    /// <summary>
    /// <see cref="Root(UncNamedShare, Func{UncResult{string?}})"/> for a call that may write (2026-10-22, review H4): only then is a
    /// <c>readwrite</c> share mounted read-write — a read, and every call while <c>UNC writes</c> is off, gets a read-only mount.
    /// </summary>
    UncResult<string> Root(UncNamedShare share, Func<UncResult<string?>> signIn, bool writable) => Root(share, signIn);

    /// <summary>
    /// A write call on <paramref name="share"/> ended: a read-write mount of ours made as another account is let go now, so it doesn't stay
    /// writable by anything else this user runs (a shell command) for the rest of the run (review H4). Never throws.
    /// </summary>
    void EndWrite(UncNamedShare share)
    {
    }

    /// <summary>Where <paramref name="share"/> is mounted and as whom (<c>--unc-check</c>'s line), or null while it is not.</summary>
    string? Describe(UncNamedShare share);

    /// <summary>Unmounts every mount this run made and removes its record. Never throws; the app's exit calls it.</summary>
    void UnmountAll();
}

/// <summary>The three things a Mac's mounts need from the system (<see cref="MacMountSystem"/>; a fake in the tests).</summary>
public interface IMountSystem
{
    /// <summary>
    /// Mounts <paramref name="url"/> under <c>/Volumes</c> with no UI: NetFS's status (0, an errno, or a negative OSStatus) and,
    /// on success, the mount point.
    /// </summary>
    int Mount(string url, string? user, string? password, bool readOnly, bool newSession, out string? mountPoint);

    /// <summary>The mount <paramref name="path"/> lies on (<c>statfs</c>), or null when it cannot be read.</summary>
    MountInfo? Info(string path);

    /// <summary>The SMB mounts there are now.</summary>
    IReadOnlyList<MountInfo> SmbMounts();

    /// <summary>Unmounts <paramref name="mountPoint"/> (<paramref name="force"/>: even while busy); 0 or the errno.</summary>
    int Unmount(string mountPoint, bool force);
}

/// <summary>
/// A Mac's share mounts for one run (2026-10-11, UNC shares on a Mac; the user's picks: the app mounts shares itself, runas too,
/// a mount living for the run and swept after a crash). macOS has no per-call sign-in, so a share is reached through a mount:
/// NetFS (<see cref="MacMountSystem"/>), in-process, no process-start site.
///
/// <para><b>Where.</b> Under <c>/Volumes</c>, the name macOS picks: NetAuthSysAgent refuses a no-UI mount anywhere else ("is not
/// under /Volumes and NoUI is explicitly set - rejecting", measured on 15.7 that day), and without NoUI a failed sign-in would pop a
/// password dialog over a terminal app. Each mount is <c>nobrowse</c> (Finder leaves it out), <c>rdonly</c> for an <c>access: read</c>
/// share — the kernel then refuses every write, a layer Windows has no twin of — and, under <c>runas</c>, a session of its own.</para>
///
/// <para><b>Which.</b> One mount per server, share, account and read-only (<see cref="MacMountRules.Key"/>), kept while its file
/// system is still the one mounted. Every mount asks NetFS for a session of its own (<c>ForceNewSession</c>): measured that day, a
/// share already mounted (by Finder, or by the app read-only) answers a plain mount with EEXIST and no mount point, while a new
/// session gets a mount of its own (<c>…-1</c>), as you too — so the app's mounts are always its own, read-only honoured. Should
/// EEXIST come anyway, the mount there is found by <c>f_mntfromname</c> (<see cref="MacMountRules.Find"/>) and, not being ours, is
/// used but never unmounted or recorded. Under <c>runas</c> a mount is used only when it is signed in as that account
/// (<see cref="MacMountRules.IsSignedInAs"/>), so a share is never read as someone else.</para>
///
/// <para><b>Leaving none behind.</b> Every mount of ours gets a record in <c>&lt;home&gt;/mounts/</c> (<see cref="MacMountRules.Format"/>);
/// <see cref="UnmountAll"/> runs at the app's exit (<c>/exit</c>, SIGTERM, SIGQUIT, SIGHUP) and <see cref="Sweep"/> when the mounts are
/// made, taking only a mount whose app is gone and that is still the very one recorded (<see cref="MacMountRules.SweepTakes"/>). A
/// busy mount (a shell sitting in it) is forced at exit. A mount slower than <see cref="UncAccess.ReachTimeout"/> is given up as
/// unreachable but still recorded when it lands, so the exit and the sweep take it too. Once <see cref="UnmountAll"/> has run
/// (2026-10-11, code review) no mount is begun, the ones under way are waited for a moment (<see cref="ExitWait"/>), and one landing
/// later still is unmounted as it lands, never kept. Only a mount NetFS is still making when the process itself ends can outlive
/// it: recorded if it landed first (the next start's sweep takes it), else unrecorded, in <c>/Volumes</c> until ejected by hand.</para>
///
/// <para><b>Found, not made.</b> A mount NetFS reports made but that <c>statfs</c> cannot find at its point (2026-10-11, code review) is
/// unmounted again and the call refused, never left mounted unrecorded; an EEXIST mount is taken only when its read-only flag is the
/// share's, and never one the app made for another account or flag, nor, for a share read as you, one kept under runas.</para>
/// </summary>
public sealed class ShareMounts : IShareMounts
{
    private const int Busy = 16;   // EBUSY

    /// <summary>NetFS's answer when the share is mounted already and no new session was asked (EEXIST, no mount point; measured).</summary>
    private const int Exists = 17;

    /// <summary>The status of a mount whose call threw or that was let go (logged); never one of NetFS's.</summary>
    private const int Failed = int.MinValue;

    /// <summary>How long <see cref="UnmountAll"/> waits for the mounts under way before it unmounts the run's (by default).</summary>
    public static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(3);

    private readonly IMountSystem _system;
    private readonly string _folder;
    private readonly uint _uid;
    private readonly Func<int, long?> _startTicks;
    private readonly TimeSpan _wait;
    private readonly TimeSpan _exitWait;
    private readonly object _gate = new();
    private readonly Dictionary<string, Mounted> _mounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<int>> _pending = new(StringComparer.Ordinal);
    private bool _closed;

    /// <summary>One mount kept: where, which file system, from where, whether the app made it, its record, and its key's account (null as you).</summary>
    private sealed record Mounted(string Point, long Fsid, string From, bool Ours, string? Record, string? Account);

    /// <param name="system">The native calls.</param>
    /// <param name="home">The app's home: the records go to <c>&lt;home&gt;/mounts/</c>.</param>
    /// <param name="uid">This user's id: a mount of ours is one this user made.</param>
    /// <param name="startTicks">A process's start in UTC ticks, null when it is gone (<see cref="LlamaRecords.StartTicks"/> when null).</param>
    /// <param name="wait">How long a call waits for a mount (<see cref="UncAccess.ReachTimeout"/> when null).</param>
    /// <param name="exitWait">How long the exit waits for the mounts under way (<see cref="ExitWait"/> when null).</param>
    public ShareMounts(IMountSystem system, string home, uint uid, Func<int, long?>? startTicks = null, TimeSpan? wait = null, TimeSpan? exitWait = null)
    {
        _system = system ?? throw new ArgumentNullException(nameof(system));
        ArgumentException.ThrowIfNullOrWhiteSpace(home);
        _folder = Path.Combine(home, MacMountRules.FolderName);
        _uid = uid;
        _startTicks = startTicks ?? LlamaRecords.StartTicks;
        _wait = wait ?? UncAccess.ReachTimeout;
        _exitWait = exitWait ?? ExitWait;
    }

    /// <summary><see cref="Root(UncNamedShare, Func{UncResult{string?}})"/> with the password at hand (null as you).</summary>
    public UncResult<string> Root(UncNamedShare share, string? password, bool writable = false) => Root(share, () => UncResult<string?>.Ok(password), writable);

    /// <inheritdoc />
    public UncResult<string> Root(UncNamedShare share, Func<UncResult<string?>> signIn) => Root(share, signIn, writable: false);

    /// <inheritdoc />
    public UncResult<string> Root(UncNamedShare share, Func<UncResult<string?>> signIn, bool writable)
    {
        ArgumentNullException.ThrowIfNull(share);
        ArgumentNullException.ThrowIfNull(signIn);
        if (UncPath.Split(UncPath.Spell(share.Config.Root, windows: false)) is not var (server, name, rest))
        {
            return UncResult<string>.Failed(UncText.BadPath(share.Config.Root));
        }

        string? account = share.Config.IsRunAs ? share.Config.User!.Trim() : null;
        // Read-write only for a call that may write on a readwrite share (review H4: the share's access alone decided, whatever UNC writes said).
        bool readOnly = !(writable && share.Config.IsReadWrite);
        string key = MacMountRules.Key(server, name, account, readOnly);
        Task<int>? mounting;
        lock (_gate)
        {
            if (_closed)
            {
                return UncResult<string>.Failed(UncText.MountsClosed(share.Name));
            }

            if (Live(key) is { } live)
            {
                return UncResult<string>.Ok(Under(live.Point, rest));
            }

            _pending.TryGetValue(key, out mounting);
        }

        if (mounting is null)
        {
            // The password only now, outside the lock (a Keychain read), and only for a mount to be made.
            var password = signIn();
            if (password.Error is { } refused)
            {
                return UncResult<string>.Failed(refused);
            }

            lock (_gate)
            {
                if (_closed)
                {
                    return UncResult<string>.Failed(UncText.MountsClosed(share.Name));
                }

                if (Live(key) is { } live)
                {
                    return UncResult<string>.Ok(Under(live.Point, rest));
                }

                if (!_pending.TryGetValue(key, out mounting))
                {
                    mounting = Begin(key, share, server, name, account, readOnly, password.Value);
                    _pending[key] = mounting;
                }
            }
        }

        if (!mounting.Wait(_wait))
        {
            return UncResult<string>.Failed(UncText.Unreachable(share.Name, share.Config.Root, UncText.TimedOut(_wait)));
        }

        int status = mounting.Result;
        lock (_gate)
        {
            if (Live(key) is { } live)
            {
                return UncResult<string>.Ok(Under(live.Point, rest));
            }
        }

        string who = account ?? Environment.UserName;
        return UncResult<string>.Failed(status != 0 ? MacMountRules.Refusal(status, share, who) : UncText.MountedAsOther(share.Name, who));
    }

    /// <summary>The mount for <paramref name="key"/>, begun on a pool thread; its task ends in <see cref="Landed"/>. Under the gate.</summary>
    private Task<int> Begin(string key, UncNamedShare share, string server, string name, string? account, bool readOnly, string? password)
    {
        string url = MacMountRules.MountUrl(server, name);
        var before = _system.SmbMounts().Select(m => m.Fsid).ToHashSet();
        return Task.Run(() =>
        {
            int status = _system.Mount(url, account, password, readOnly, newSession: true, out string? point);
            bool adopted = false;
            if (status == Exists && point is null)
            {
                // Never one the app made for another key (another account, or the other read-only flag), nor, as you, one kept under runas.
                HashSet<long> others;
                lock (_gate)
                {
                    others = [.. _mounts.Where(m => m.Key != key && (m.Value.Ours || (account is null && m.Value.Account is not null))).Select(m => m.Value.Fsid)];
                }

                // As you, only this user's own mount signed in as them (review H20); under runas, Landed checks the account.
                string user = Environment.UserName;
                point = MacMountRules.Find(_system.SmbMounts().Where(m => !others.Contains(m.Fsid) && (account is not null || MacMountRules.IsOwnSignIn(m, _uid, user))), server, name, readOnly)?.On;
                adopted = point is not null;
            }

            return (status == Exists && adopted ? 0 : status, point, adopted);
        }).ContinueWith(t => Landed(key, share, account, before, t), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    /// <summary>
    /// A mount that finished (in time or not): kept when it is usable, recorded when it is ours, else let go — and after
    /// <see cref="UnmountAll"/> let go whatever it is. NetFS's status, or <see cref="Failed"/> when the call threw or the mount was let go.
    /// </summary>
    private int Landed(string key, UncNamedShare share, string? account, IReadOnlySet<long> before, Task<(int Status, string? Point, bool Adopted)> task)
    {
        lock (_gate)
        {
            _pending.Remove(key);
            if (task.Exception is { } ex)
            {
                DiagnosticLog.Warn(UncConfigFile.Category, UncText.MountLogLine(share.Name, Llm.Assistant.Explain(ex.GetBaseException())));
                return Failed;
            }

            if (task.Result.Status != 0 || task.Result.Point is not { } point)
            {
                return task.Result.Status;
            }

            if (_system.Info(point) is not { } info || !string.Equals(info.Type, MacMountRules.SmbType, StringComparison.Ordinal))
            {
                // NetFS made it but statfs cannot find it there: unmounted again rather than left with no record (an adopted one is not ours).
                DiagnosticLog.Warn(UncConfigFile.Category, UncText.MountUnreadableLogLine(share.Name, point));
                if (!task.Result.Adopted)
                {
                    _system.Unmount(point, force: false);
                }

                return Failed;
            }

            bool ours = info.Owner == _uid && !before.Contains(info.Fsid);
            if (account is not null && !MacMountRules.IsSignedInAs(info.From, account))
            {
                DiagnosticLog.Warn(UncConfigFile.Category, UncText.MountedAsOtherLogLine(share.Name, account, info.From));
                if (ours)
                {
                    _system.Unmount(point, force: false);
                }

                return 0;
            }

            if (_closed)
            {
                // Landed after the exit's unmount: never kept, never recorded.
                if (ours)
                {
                    UnmountOne(new Mounted(point, info.Fsid, info.From, ours, null, account));
                }

                return Failed;
            }

            string? record = ours ? Write(info) : null;
            _mounts[key] = new Mounted(point, info.Fsid, info.From, ours, record, account);
            DiagnosticLog.Info(UncConfigFile.Category, UncText.MountedLogLine(share.Name, point, info.From, ours));
            return 0;
        }
    }

    /// <summary>The mount kept for <paramref name="key"/> while its file system is still the one there; forgotten when it is not.</summary>
    private Mounted? Live(string key)
    {
        if (!_mounts.TryGetValue(key, out var mounted))
        {
            return null;
        }

        if (_system.Info(mounted.Point) is { } info && info.Fsid == mounted.Fsid)
        {
            return mounted;
        }

        _mounts.Remove(key);
        Remove(mounted.Record);
        return null;
    }

    private static string Under(string point, string rest) => rest.Length == 0 ? point : Path.Combine(point, rest);

    /// <inheritdoc />
    public string? Describe(UncNamedShare share)
    {
        ArgumentNullException.ThrowIfNull(share);
        if (UncPath.Split(UncPath.Spell(share.Config.Root, windows: false)) is not var (server, name, _))
        {
            return null;
        }

        string? account = share.Config.IsRunAs ? share.Config.User!.Trim() : null;
        lock (_gate)
        {
            // Read-only first, the mount a read makes (review H4); a write's read-write one while it stands.
            foreach (bool readOnly in new[] { true, false })
            {
                if (Live(MacMountRules.Key(server, name, account, readOnly)) is { } live)
                {
                    return UncText.MountDescription(live.Point, live.From, live.Ours, readOnly);
                }
            }

            return null;
        }
    }

    /// <inheritdoc />
    public void EndWrite(UncNamedShare share)
    {
        ArgumentNullException.ThrowIfNull(share);
        if (!share.Config.IsRunAs || UncPath.Split(UncPath.Spell(share.Config.Root, windows: false)) is not var (server, name, _))
        {
            return;
        }

        Mounted? mounted;
        lock (_gate)
        {
            string key = MacMountRules.Key(server, name, share.Config.User!.Trim(), readOnly: false);
            if (!_mounts.TryGetValue(key, out mounted) || !mounted.Ours)
            {
                return;
            }

            _mounts.Remove(key);
        }

        UnmountOne(mounted);
    }

    /// <inheritdoc />
    public void UnmountAll()
    {
        Task<int>[] underWay;
        lock (_gate)
        {
            _closed = true;
            underWay = [.. _pending.Values];
        }

        try
        {
            // A mount landing in this wait is let go by Landed itself (closed); one landing later still, likewise, while the process lives.
            Task.WaitAll(underWay, _exitWait);
        }
        catch (AggregateException)
        {
            // Landed logs its own failures.
        }

        List<Mounted> ours;
        lock (_gate)
        {
            ours = [.. _mounts.Values.Where(m => m.Ours)];
            _mounts.Clear();
        }

        foreach (var mounted in ours)
        {
            UnmountOne(mounted);
        }
    }

    /// <summary>Unmounts one of ours (forced when busy) and removes its record. Never throws.</summary>
    private void UnmountOne(Mounted mounted)
    {
        try
        {
            if (_system.Info(mounted.Point) is not { } info || info.Fsid != mounted.Fsid)
            {
                Remove(mounted.Record);
                return;
            }

            int errno = _system.Unmount(mounted.Point, force: false);
            if (errno == Busy)
            {
                errno = _system.Unmount(mounted.Point, force: true);
            }

            if (errno == 0)
            {
                Remove(mounted.Record);
                DiagnosticLog.Info(UncConfigFile.Category, UncText.UnmountedLogLine(mounted.Point));
            }
            else
            {
                DiagnosticLog.Warn(UncConfigFile.Category, UncText.UnmountFailedLogLine(mounted.Point, errno));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DiagnosticLog.Warn(UncConfigFile.Category, UncText.UnmountFailedLogLine(mounted.Point, Llm.Assistant.Explain(ex)));
        }
    }

    /// <summary>
    /// Unmounts what the records show an app that is gone left mounted, and removes the records of mounts that are gone (see the
    /// class). The mount points it unmounted. Never throws.
    /// </summary>
    public IReadOnlyList<string> Sweep()
    {
        var taken = new List<string>();
        try
        {
            if (!Directory.Exists(_folder))
            {
                return taken;
            }

            foreach (string file in Directory.EnumerateFiles(_folder, "*.txt"))
            {
                try
                {
                    var record = MacMountRules.Parse(File.ReadAllText(file));
                    bool alive = record is not null && _startTicks(record.OwnerPid) is { } ticks && Math.Abs(ticks - record.OwnerStartTicks) <= TimeSpan.TicksPerSecond;
                    if (alive)
                    {
                        continue;
                    }

                    if (record is not null && MacMountRules.SweepTakes(record, alive, _system.Info(record.MountPoint), _uid))
                    {
                        int errno = _system.Unmount(record.MountPoint, force: true);
                        DiagnosticLog.Warn(UncConfigFile.Category, UncText.LeftMountedLogLine(record.MountPoint, record.OwnerPid, errno));
                        if (errno != 0)
                        {
                            continue;
                        }

                        taken.Add(record.MountPoint);
                    }

                    File.Delete(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    DiagnosticLog.Debug(UncConfigFile.Category, "Sweeping " + file + ": " + ex.Message);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Warn(UncConfigFile.Category, "Sweeping " + _folder + ": " + ex.Message);
        }

        return taken;
    }

    private string? Write(MountInfo info)
    {
        try
        {
            if (_startTicks(Environment.ProcessId) is not { } started)
            {
                return null;
            }

            Directory.CreateDirectory(_folder);
            string path = Path.Combine(_folder, MacMountRules.RecordName(info.Fsid));
            // Written beside it and renamed (2026-10-11, code review): another start's sweep never reads half a record and deletes it.
            string partial = Path.ChangeExtension(path, ".partial");
            File.WriteAllText(partial, MacMountRules.Format(new MountRecord(Environment.ProcessId, started, info.On, info.Fsid, info.From)));
            File.Move(partial, path, overwrite: true);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Warn(UncConfigFile.Category, "No record of the mount at " + info.On + ": " + ex.Message);
            return null;
        }
    }

    private static void Remove(string? record)
    {
        if (record is null)
        {
            return;
        }

        try
        {
            File.Delete(record);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Debug(UncConfigFile.Category, "Removing " + record + ": " + ex.Message);
        }
    }
}
