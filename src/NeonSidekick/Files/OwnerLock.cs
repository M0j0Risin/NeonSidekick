using System.Globalization;
using System.Runtime.InteropServices;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.Files;

/// <summary>
/// What a lock says: the process, when it started, and the machine it runs on (<paramref name="Host"/>, 2026-10-20, a review: a plan in a shared or
/// synced folder was locked by a pid on another PC, which this one read as a process of its own that was gone, took over, and ran the plan beside
/// it). A lock with no host (one the previous build wrote) is this machine's. <paramref name="Machine"/> (2026-10-30, review F41): the home's
/// <c>machine-id</c>, a GUID made once, since a Mac's host name changes with the network and its own lock then read as another machine's.
/// </summary>
public sealed record LockOwner(int ProcessId, DateTimeOffset Started, string? Host = null, string? Machine = null)
{
    /// <summary>
    /// Whether the lock was written on another machine (whose processes this one cannot see): by the machine id when both sides have one, else by
    /// the host name (a lock an older build wrote, or a run with no home id).
    /// </summary>
    public bool Foreign => Machine is { Length: > 0 } machine && OwnerLock.MachineId is { Length: > 0 } mine
        ? !string.Equals(machine, mine, StringComparison.OrdinalIgnoreCase)
        : Host is { Length: > 0 } host && !string.Equals(host, Environment.MachineName, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// What <see cref="OwnerLock.TryLock"/> found: taken (free, stale, or this process's already), held by another live process (or one that can't be
/// read), refused for want of an atomic publish on its volume, or not writable at all (2026-10-29, the audit lock: a first audit makes
/// <c>.neon/audits</c> through its lock, and a folder that can't be made read as "another holds it").
/// </summary>
public enum LockTake
{
    Taken,
    Held,
    Unsupported,
    Unwritable,
}

/// <summary>
/// A lock file naming the one process that may write something (2026-10-29, lifted out of the Ralph loop's <c>ralph.lock</c> so <c>/code-audit</c>'s
/// <c>&lt;name&gt;.lock</c> is made the same way): the pid, its start time and the machine, published whole where none is, a stale one taken over
/// under a guard, one that can't be read or another machine's counted as held. Message-free: each caller words its own refusals. The path is a
/// working-folder one, resolved under <see cref="WriteGuard.AsApp"/> (the locks live under <c>.neon</c>).
/// </summary>
public static partial class OwnerLock
{
    private static Lazy<LockOwner> s_me = NewMe();

    private static string? s_machine;

    private static Lazy<LockOwner> NewMe() => new(() =>
    {
        using var me = System.Diagnostics.Process.GetCurrentProcess();
        // A host that isn't a name (IsName) would make this process's own lock unreadable to itself: left out, the machine id (or nothing) decides.
        string host = Environment.MachineName;
        return new LockOwner(Environment.ProcessId, new DateTimeOffset(me.StartTime.ToUniversalTime(), TimeSpan.Zero), IsName(host) ? host : null, s_machine);
    });

    /// <summary>This process as a lock names it (the screen's and headless's one owner, 2026-10-19, the second review: each kept a copy).</summary>
    public static LockOwner Me => s_me.Value;

    /// <summary>This machine's id (<see cref="LockOwner.Machine"/>), null until <see cref="UseMachineId"/> (the tests leave it unset: the host decides).</summary>
    public static string? MachineId => s_machine;

    /// <summary>The home's machine id file (2026-10-30, review F41).</summary>
    public const string MachineIdFileName = "machine-id";

    /// <summary>
    /// This machine's id set once, at the composition root before anything reads <see cref="Me"/>: a later set would leave <see cref="Me"/> without
    /// it, and this process's own lock would then no longer equal <see cref="Me"/> (it would lock itself out), so that throws.
    /// </summary>
    public static void UseMachineId(string? id)
    {
        if (s_me.IsValueCreated)
        {
            throw new InvalidOperationException("The machine id is set before the lock owner is first read.");
        }

        s_machine = id is { Length: > 0 } && IsName(id) ? id : null;
    }

    /// <summary>The machine id and <see cref="Me"/> made again for one test, put back on dispose (the suite runs serially).</summary>
    internal static IDisposable MachineIdForTests(string? id)
    {
        var (machine, me) = (s_machine, s_me);
        s_machine = id;
        s_me = NewMe();
        return new Restore(() => (s_machine, s_me) = (machine, me));
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    /// <summary>
    /// The id kept in <c>&lt;home&gt;/machine-id</c>, made the first time (a GUID, published whole without replacing one, so two first launches
    /// agree); null when the home can't be read or written (the host name then decides, as before).
    /// </summary>
    public static string? ReadOrCreateMachineId(string home)
    {
        ArgumentNullException.ThrowIfNull(home);
        string path = Path.Combine(home, MachineIdFileName);
        try
        {
            if (ReadId(path) is { } kept)
            {
                return kept;
            }

            Directory.CreateDirectory(home);
            string temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
            try
            {
                File.WriteAllText(temp, Guid.NewGuid().ToString("D") + "\n");
                File.Move(temp, path, overwrite: false);
            }
            catch (IOException)
            {
                // Another launch made it first (or a damaged one stands there): read below.
            }
            finally
            {
                DeleteQuietly(temp);
            }

            return ReadId(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Warn("Files", "machine id: " + path + " can't be read or written (" + ex.Message + "); locks name the host alone");
            return null;
        }

        static string? ReadId(string path) => File.Exists(path) && File.ReadAllText(path).Trim() is { Length: > 0 } text && IsName(text) ? text : null;
    }

    /// <summary>
    /// Whether a lock's owner still runs: a process of its id started within 2 s of the time it wrote (an id reused later is another process). A
    /// process of that id whose start time cannot be read (another user's, an elevated one: access denied) counts as the owner (2026-10-19, the
    /// third review: read as gone, a live loop's lock was taken over and two loops ran one plan); only a process that is gone frees the lock.
    /// </summary>
    public static bool OwnerAlive(LockOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (owner.Foreign)
        {
            // Another machine's process: never seen from here, so never taken for gone (the user deletes a lock its crash left: the callers say where).
            return true;
        }

        System.Diagnostics.Process process;
        try
        {
            process = System.Diagnostics.Process.GetProcessById(owner.ProcessId);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // No process of that id runs.
            return false;
        }

        using (process)
        {
            try
            {
                return Math.Abs((process.StartTime.ToUniversalTime() - owner.Started.UtcDateTime).TotalSeconds) < 2;
            }
            catch (InvalidOperationException)
            {
                // It ended meanwhile.
                return false;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or NotSupportedException or UnauthorizedAccessException)
            {
                return true;
            }
        }
    }

    /// <summary>Whether a lock's owner still runs, for <see cref="HeldElsewhere"/> (a seam for the tests: <see cref="OwnerAlive"/>).</summary>
    internal static Func<LockOwner, bool> LiveOwner { get; set; } = OwnerAlive;

    /// <summary>
    /// Whether a lock names a process of this machine whose start time can't be read (another user's, an elevated one, EPERM on a Mac): counted
    /// alive by <see cref="OwnerAlive"/>, so the refusal names the lock to delete when no NeonSidekick runs it (2026-10-30, review F76/F86: it said
    /// "wait" for good). False for one that is gone, one whose start reads, and another machine's.
    /// </summary>
    public static bool StartUnreadable(LockOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (owner.Foreign)
        {
            return false;
        }

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(owner.ProcessId);
            _ = process.StartTime;
            return false;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or NotSupportedException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary><see cref="StartUnreadable"/>, a seam for the tests.</summary>
    internal static Func<LockOwner, bool> CannotCheck { get; set; } = StartUnreadable;

    /// <summary>The lock file's text for <paramref name="owner"/>: <c>pid:</c>, <c>started:</c>, then <c>host:</c> and <c>machine:</c> when known.</summary>
    internal static string TextOf(LockOwner owner) =>
        "pid: " + N(owner.ProcessId) + "\nstarted: " + owner.Started.ToString("O", CultureInfo.InvariantCulture) + "\n"
        + (owner.Host is { Length: > 0 } host ? "host: " + host + "\n" : "")
        + (owner.Machine is { Length: > 0 } machine ? "machine: " + machine + "\n" : "");

    /// <summary>
    /// The lock at <paramref name="lockPath"/> taken for <paramref name="me"/>: <see cref="LockTake.Taken"/> when it was free, stale
    /// (<paramref name="alive"/> says its owner is gone) or already this process's; <see cref="LockTake.Held"/> while another live process holds it.
    /// Taken by creating the file whole, never over one that is there (2026-10-19, the review: read-then-write let two instances starting together
    /// both take it): published whole, only where none is (<see cref="TryCreate"/>), so of two racing for it one fails, and a reader finds it whole or
    /// not at all. A stale lock is removed only while it still says what was read (under the take-over guard, <see cref="RemoveIfStill"/>), then the
    /// take tried again: of several takers, the one that removes it and publishes first holds it, and the rest read that one's.
    /// <see cref="LockTake.Unwritable"/> when the path is refused, or every publish failed while no lock stood there (a folder that can't be made, a
    /// read-only drive).
    /// </summary>
    public static LockTake TryLock(WorkingDirectory files, string lockPath, LockOwner me, Func<LockOwner, bool> alive)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(lockPath);
        ArgumentNullException.ThrowIfNull(me);
        ArgumentNullException.ThrowIfNull(alive);
        string full = "";
        if (WriteGuard.AsApp(() => files.Resolve(lockPath, forWrite: true, out full)) != FileOutcome.Ok)
        {
            return LockTake.Unwritable;
        }

        string text = TextOf(me);
        bool missing = false;
        bool refused = false;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var published = TryCreate(full, text);
            if (published == PublishOutcome.Published)
            {
                return LockTake.Taken;
            }

            if (published == PublishOutcome.Unsupported)
            {
                return LockTake.Unsupported;
            }

            refused |= published == PublishOutcome.Exists;

            if (!Directory.Exists(Path.GetDirectoryName(full)))
            {
                // Its folder couldn't be made (a file stands there, a read-only drive): off Windows the read below would say unreadable, so held.
                return LockTake.Unwritable;
            }

            var (state, held) = ReadLockAt(full);
            missing = state == LockRead.Missing;
            if (missing)
            {
                // Let go since (or never written): take it again.
                continue;
            }

            // A lock that cannot be read is never taken for a stale one (2026-10-19, the third review: read as no owner, it matched "not a lock").
            if (state == LockRead.Unreadable || (held is not null && (held == me || alive(held))))
            {
                return held == me ? LockTake.Taken : LockTake.Held;
            }

            // Stale (its owner gone) or not a lock at all: removed while it still says so, then taken again.
            if (!RemoveIfStill(full, held, alive, out bool unsupported))
            {
                return unsupported ? LockTake.Unsupported : LockTake.Held;
            }
        }

        // No lock there after every publish failed: it couldn't be written (2026-10-29, the audit lock; until then read as held). Unless a publish was
        // refused because a lock stood there (2026-10-30, review F75): then the lock went between the publish and the read, each time, which is
        // another taker's race, not a folder that can't be written.
        return missing && !refused ? LockTake.Unwritable : LockTake.Held;
    }

    /// <summary>
    /// Whether another live process holds the lock at <paramref name="lockPath"/>, <paramref name="owner"/> the one it names (null for a lock that
    /// cannot be read, which counts as held as <see cref="TryLock"/> reads it); false with none, a stale one, or this process's own. Liveness through
    /// <see cref="LiveOwner"/>.
    /// </summary>
    public static bool HeldElsewhere(WorkingDirectory files, string lockPath, out LockOwner? owner)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(lockPath);
        owner = null;
        if (files.Resolve(lockPath, forWrite: false, out string full) != FileOutcome.Ok)
        {
            return false;
        }

        var (state, read) = ReadLockAt(full);
        if (state == LockRead.Missing || (state == LockRead.Read && (read is null || read == Me || !LiveOwner(read))))
        {
            return false;
        }

        owner = read;
        return true;
    }

    /// <summary>The lock let go when it is <paramref name="me"/>'s (a stop, a profile switch, the app's exit).</summary>
    public static void Unlock(WorkingDirectory files, string lockPath, LockOwner me)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(lockPath);
        string full = "";
        if (ReadLock(files, lockPath) == me && WriteGuard.AsApp(() => files.Resolve(lockPath, forWrite: true, out full)) == FileOutcome.Ok)
        {
            try
            {
                File.Delete(full);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Left behind: stale once this process is gone, and taken over then.
            }
        }
    }

    /// <summary>
    /// What publishing a lock (or a take-over guard) found: published, not written, one there already (<see cref="Exists"/>, 2026-10-30, review F75:
    /// told apart from a write that failed), or no atomic way on its volume.
    /// </summary>
    internal enum PublishOutcome
    {
        Published,
        NotTaken,
        Unsupported,
        Exists,
    }

    // Windows' "a file is there" answers to a move without replace: ERROR_FILE_EXISTS and ERROR_ALREADY_EXISTS as HRESULTs.
    private const int FileExistsHResult = unchecked((int)0x80070050);
    private const int AlreadyExistsHResult = unchecked((int)0x800700B7);
    private const int Eexist = 17;

    /// <summary><see cref="Publish"/>, a seam for the tests (a refusal by a lock that is gone by the read, which no real race repeats on cue).</summary>
    internal static Func<string, string, PublishOutcome> PublishCall { get; set; } = Publish;

    /// <summary>
    /// <paramref name="text"/> as a new file at <paramref name="full"/>: <see cref="PublishOutcome.NotTaken"/> when one is there (or it could not be
    /// written). Written whole to a temp file of its own beside it, then published under the lock's name without ever replacing one (2026-10-20, the
    /// code review): see <see cref="Publish"/>. The temp file is removed after, published or not. A reader so finds the lock whole or not at all, and
    /// of any number of racers exactly one publishes. Until then it was created in place with <see cref="FileMode.CreateNew"/>: a reader could find it
    /// empty or half written (read as held for a few seconds, then as garbage a second taker removed, so a stalled writer and that taker both ran), and
    /// off Windows <see cref="FileShare.None"/> is no share mode but an <c>flock</c> taken after the <c>O_EXCL</c> create, so a reader's shared
    /// <c>flock</c> in between failed the creator's and nobody held it. The move alone had been tried before that, and on macOS .NET's move without
    /// overwrite is a look then a rename: of eight racers all eight took it (the Mac's gate).
    /// </summary>
    private static PublishOutcome TryCreate(string full, string text)
    {
        string temp = full + "." + N(Environment.ProcessId) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(temp, text);
            return PublishCall(temp, full);
        }
        catch (IOException ex) when (ex.HResult is FileExistsHResult or AlreadyExistsHResult)
        {
            return PublishOutcome.Exists;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return PublishOutcome.NotTaken;
        }
        finally
        {
            DeleteQuietly(temp);
        }
    }

    /// <summary>
    /// <paramref name="temp"/> made <paramref name="full"/>, never over a file there. On Windows by <c>File.Move(overwrite: false)</c>, MoveFileEx
    /// without a replace, atomic there (a share's server makes it so too). Off Windows by <c>link(2)</c>, which fails with <c>EEXIST</c> on a name
    /// already there, in one step; where the volume takes no hard links (later on 2026-10-20, the code review: it fell back to the racy look-then-move),
    /// on a Mac by <c>renamex_np(RENAME_EXCL)</c>, the same in one rename. Measured on the Mac's own disk images: FAT32 refuses the link (ENOTSUP)
    /// and takes the exclusive rename, EEXIST over a lock there; exFAT refuses both (ENOTSUP). Where neither holds the lock is
    /// <see cref="PublishOutcome.Unsupported"/> and each caller decides (the Ralph loop refuses its start; a code audit runs unlocked with a warning,
    /// 2026-10-29, the user's call), rather than taken by a move two instances could both win: a lock that can be held twice is no lock. An O_EXCL
    /// guard around a move was weighed and left: it serializes the takers only while no crash leaves the guard, and clearing a stale one is a race
    /// of its own.
    /// </summary>
    private static PublishOutcome Publish(string temp, string full)
    {
        if (OperatingSystem.IsWindows())
        {
            File.Move(temp, full, overwrite: false);   // IOException when one is there: TryCreate's NotTaken
            return PublishOutcome.Published;
        }

        var outcome = PublishOutcomeOf(LinkCall(temp, full));
        if (outcome == PublishOutcome.Unsupported && OperatingSystem.IsMacOS())
        {
            outcome = PublishOutcomeOf(RenameExclusiveCall(temp, full));
        }

        if (outcome == PublishOutcome.Unsupported)
        {
            DiagnosticLog.Warn("Files", Path.GetFileName(full) + ": the volume of " + full + " takes neither a hard link nor an exclusive rename; the lock is refused");
        }

        return outcome;
    }

    /// <summary>
    /// What a publishing call's answer means (0, else its errno): published; <see cref="PublishOutcome.Unsupported"/> for the errnos that say the
    /// volume has no such call (EPERM, which Linux's link gives on FAT; ENOTSUP and EOPNOTSUPP, macOS's and Linux's); anything else (EEXIST: held;
    /// the folder gone, no write access) not taken, EEXIST as <see cref="PublishOutcome.Exists"/> (2026-10-30, review F75). Pure.
    /// </summary>
    internal static PublishOutcome PublishOutcomeOf(int errno) => errno switch
    {
        0 => PublishOutcome.Published,
        Eexist => PublishOutcome.Exists,
        Eperm or EnotsupMac or EopnotsuppMac or EopnotsuppLinux => PublishOutcome.Unsupported,
        _ => PublishOutcome.NotTaken,
    };

    // The errno values that say a volume has no such call: macOS's and Linux's EPERM, macOS's ENOTSUP and EOPNOTSUPP, Linux's EOPNOTSUPP.
    private const int Eperm = 1;
    private const int EnotsupMac = 45;
    private const int EopnotsuppMac = 102;
    private const int EopnotsuppLinux = 95;

    // renamex_np's flag (<sys/stdio.h>): fail with EEXIST rather than replace what is there.
    private const uint RenameExcl = 0x00000004;

    /// <summary><c>link(2)</c>: 0, else its errno. A seam for the tests (the volume's answer injected).</summary>
    internal static Func<string, string, int> LinkCall { get; set; } = (existing, created) => Link(existing, created) == 0 ? 0 : Marshal.GetLastPInvokeError();

    /// <summary><c>renamex_np(RENAME_EXCL)</c>, a Mac's only: 0, else its errno. A seam for the tests.</summary>
    internal static Func<string, string, int> RenameExclusiveCall { get; set; } = RenameExclusive;

    /// <summary>The Mac's exclusive rename itself (macOS 10.12+, in libSystem as link is).</summary>
    internal static int RenameExclusive(string from, string to) => RenameX(from, to, RenameExcl) == 0 ? 0 : Marshal.GetLastPInvokeError();

    [LibraryImport("libc", EntryPoint = "link", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Link(string existing, string created);

    [LibraryImport("libc", EntryPoint = "renamex_np", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int RenameX(string from, string to, uint flags);

    /// <summary>
    /// How old a take-over guard whose owner can't be judged (one that can't be read, the old pid-only kind, or another machine's) is before it counts
    /// as left by a crash: minutes, never the 30 s of before (2026-10-30, review F38: a live taker stalled past 30 s, or a clock ahead, lost its
    /// guard to a second taker).
    /// </summary>
    private static readonly TimeSpan GuardStale = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The lock at <paramref name="full"/> removed when it still names <paramref name="held"/> (or is gone already); false when another took it
    /// meanwhile, or is taking it over now. Done under a take-over guard beside it (<c>&lt;lock&gt;.takeover</c>, created whole as the lock is), so one
    /// taker at a time reads and removes (2026-10-19, the third review: a read then a delete let one of two instances taking over the same stale
    /// lock delete the other's fresh one, and both ran). Under the guard the read and the delete cannot be raced: a fresh lock is only ever
    /// created where none is, and only a guard holder removes one it did not write. The guard names its owner as a lock does (2026-10-30, review
    /// F38), and goes when that owner is gone (<paramref name="alive"/>); one whose owner can't be judged here goes once older than
    /// <see cref="GuardStale"/>.
    /// </summary>
    private static bool RemoveIfStill(string full, LockOwner? held, Func<LockOwner, bool> alive, out bool unsupported)
    {
        string guard = full + ".takeover";
        var guarded = TryCreate(guard, TextOf(Me));
        unsupported = guarded == PublishOutcome.Unsupported;
        if (guarded != PublishOutcome.Published)
        {
            try
            {
                if (GuardLeft(guard, alive))
                {
                    File.Delete(guard);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Its holder lets it go.
            }

            return false;
        }

        try
        {
            var (state, now) = ReadLockAt(full);
            if (state == LockRead.Missing)
            {
                return true;
            }

            if (state == LockRead.Unreadable || now != held)
            {
                return false;
            }

            File.Delete(full);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            DeleteQuietly(guard);
        }
    }

    /// <summary>
    /// Whether the take-over guard at <paramref name="guard"/> was left by a taker that is gone: one naming this process never (another of its own
    /// takes removes it), one of this machine when <paramref name="alive"/> says its owner is gone, and one that can't be judged here (unreadable,
    /// the old pid-only kind, another machine's, whose processes this one can't see) once older than <see cref="GuardStale"/>.
    /// </summary>
    private static bool GuardLeft(string guard, Func<LockOwner, bool> alive)
    {
        var (state, owner) = ReadLockAt(guard);
        if (state == LockRead.Missing || owner == Me)
        {
            return false;
        }

        if (state == LockRead.Read && owner is { Foreign: false })
        {
            return !alive(owner);
        }

        return DateTime.UtcNow - File.GetLastWriteTimeUtc(guard) > GuardStale;
    }

    private static void DeleteQuietly(string full)
    {
        try
        {
            File.Delete(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A stray file beside the lock: never read as the lock.
        }
    }

    /// <summary>What reading a lock file found: none there, one that could not be read, or one read (its owner null when it is not a lock).</summary>
    private enum LockRead
    {
        Missing,
        Unreadable,
        Read,
    }

    /// <summary>The most a lock file is read: a lock is a few short lines, so a bigger file is no lock this app wrote, and counts as unreadable.</summary>
    private const int MaxLockBytes = 4096;

    /// <summary>
    /// The lock file at a full path read, sharing every access so a taker's read never stops another's delete. Always whole, being published
    /// whole (<see cref="TryCreate"/>), so what it says is what its writer wrote: one that names no owner is not a lock. One past
    /// <see cref="MaxLockBytes"/>, or with a host or machine that isn't a name, is unreadable (2026-10-30, review F85: held, and its text never
    /// reaches the transcript or the log).
    /// </summary>
    private static (LockRead State, LockOwner? Owner) ReadLockAt(string full)
    {
        try
        {
            using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            byte[] buffer = new byte[MaxLockBytes + 1];
            int length = 0;
            while (length < buffer.Length && stream.Read(buffer, length, buffer.Length - length) is int read and > 0)
            {
                length += read;
            }

            return length > MaxLockBytes ? (LockRead.Unreadable, null) : ParseLock(System.Text.Encoding.UTF8.GetString(buffer, 0, length));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return (LockRead.Missing, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (LockRead.Unreadable, null);
        }
    }

    /// <summary>The owner a lock at a working-folder path names, else null (none there, not a lock, or a path refused).</summary>
    internal static LockOwner? ReadOwner(WorkingDirectory files, string lockPath)
    {
        ArgumentNullException.ThrowIfNull(files);
        return files.Resolve(lockPath, forWrite: false, out string full) == FileOutcome.Ok && ReadLockAt(full) is (LockRead.Read, { } owner) ? owner : null;
    }

    private static LockOwner? ReadLock(WorkingDirectory files, string path)
    {
        var read = files.ReadText(path, null, null);
        return read.Outcome == FileOutcome.Ok && read.Text.Length <= MaxLockBytes && ParseLock(read.Text) is (LockRead.Read, { } owner) ? owner : null;
    }

    /// <summary>
    /// A lock's text read: <see cref="LockRead.Read"/> with its owner, or a null owner when it names no pid (not a lock); <see cref="LockRead.Unreadable"/>
    /// when its host or machine isn't a name (<see cref="IsName"/>). Lines it doesn't know are skipped, so an older build still reads a newer lock.
    /// </summary>
    private static (LockRead State, LockOwner? Owner) ParseLock(string text)
    {
        int pid = 0;
        DateTimeOffset started = DateTimeOffset.MinValue;
        string? host = null;
        string? machine = null;
        foreach (string line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (line.StartsWith("pid: ", StringComparison.Ordinal))
            {
                int.TryParse(line[5..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out pid);
            }
            else if (line.StartsWith("started: ", StringComparison.Ordinal))
            {
                DateTimeOffset.TryParse(line[9..].Trim(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out started);
            }
            else if (line.StartsWith("host: ", StringComparison.Ordinal) && line[6..].Trim() is { Length: > 0 } named)
            {
                if (!IsName(named))
                {
                    return (LockRead.Unreadable, null);
                }

                host = named;
            }
            else if (line.StartsWith("machine: ", StringComparison.Ordinal) && line[9..].Trim() is { Length: > 0 } id)
            {
                if (!IsName(id))
                {
                    return (LockRead.Unreadable, null);
                }

                machine = id;
            }
        }

        return (LockRead.Read, pid > 0 ? new LockOwner(pid, started, host, machine) : null);
    }

    /// <summary>
    /// Whether a lock's host or machine id is a name: at most 64 letters, digits, dots, dashes and underscores. Letters beyond ASCII count (a Mac's
    /// name can hold them, and this process's own host must pass its own check).
    /// </summary>
    internal static bool IsName(string text) =>
        text.Length is > 0 and <= 64 && text.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_');

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);
}
