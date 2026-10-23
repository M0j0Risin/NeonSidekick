using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace NeonSidekick.Computers;

/// <summary>How a guarded call ended short of its work's own answer.</summary>
public enum SignInStop
{
    /// <summary>The work ran: its value or its failure stands.</summary>
    None,

    /// <summary>This account's password was refused before; nothing was tried (a new password is tried at once).</summary>
    Held,

    /// <summary>Too many earlier calls to the server still run abandoned; nothing was tried.</summary>
    StillWaiting,

    /// <summary>The call's time ran out; the work, if it had not begun a change, was abandoned.</summary>
    TimedOut,
}

/// <summary>One guarded call's facts: whose sign-in it is (null for the user running the app), the server, and which failures mean "wrong password".</summary>
/// <param name="HoldKey">A hash of where, the account and the password (<see cref="SignInGuard.HoldKey"/>), or null when no password is sent.</param>
/// <param name="Server">The server (and port) the abandoned-call cap counts against.</param>
/// <param name="WrongPassword">Whether a failure says the password is wrong — never locked, disabled, expired or must-change (B8).</param>
public sealed record SignInCall(string? HoldKey, string Server, Func<Exception, bool> WrongPassword);

/// <summary>What <see cref="SignInGuard.RunAsync{T}"/> came to: the work's value, its failure (rethrown by the caller's own mapping), or a stop.</summary>
public readonly record struct SignInRun<T>(T? Value, Exception? Failure, SignInStop Stop)
{
    /// <summary>Whether the work ran a change it had begun (<see cref="SignInTurn.Begin"/>) to its end after the caller stopped waiting.</summary>
    public bool WaitedOut { get; init; }
}

/// <summary>
/// The work's side of a guarded call. A change calls <see cref="Begin"/> just before it acts: once begun, the call is waited out whatever
/// happens to the caller (a half-made change is never abandoned), and before it, a cancel or the timeout abandons the work with nothing done
/// (2026-10-23, the admin review's B2: Esc could not end a hung connect). <see cref="Token"/> is the caller's token joined with the timeout.
/// </summary>
public sealed class SignInTurn
{
    private int _state;   // 0 connecting, 1 begun, 2 abandoned

    internal SignInTurn(CancellationToken token) => Token = token;

    public CancellationToken Token { get; }

    /// <summary>Marks the change as begun; throws <see cref="OperationCanceledException"/> when the caller has already given up.</summary>
    public void Begin()
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
        {
            throw new OperationCanceledException(Token);
        }
    }

    internal bool Begun => Volatile.Read(ref _state) == 1;

    /// <summary>True when the work may be abandoned (it had not begun a change); it then can't begin one.</summary>
    internal bool TryAbandon() => Interlocked.CompareExchange(ref _state, 2, 0) is 0 or 2;
}

/// <summary>
/// The sign-in guard every admin family's calls go through (2026-10-23, the admin review's mechanism 2: <c>DirectoryAccess</c>'s hold and
/// abandoned-call cap moved out and shared by AD, services, tasks and event logs — F2, B2). What it holds, process-wide:
/// <list type="bullet">
/// <item>A refused password (<see cref="SignInCall.WrongPassword"/> only — B8: a locked, disabled, expired or must-change answer is not the
/// password's fault, so an unlock takes effect at once): a call with the same where, account and password is answered
/// <see cref="SignInStop.Held"/> without reaching the server; a new password hashes differently and is tried at once. Nothing of the password
/// itself is kept. A refusal that arrives after the call was abandoned is recorded too (F3).</item>
/// <item>While a password is unproven its sign-ins go one at a time (F1: eight parallel calls all tried a stale one before any refusal was
/// recorded); once one signs in, the rest run in parallel again.</item>
/// <item>Abandoned calls per server: at most <see cref="MaxAbandoned"/>, and at most <see cref="MaxRunning"/> running in all (F4: the count was
/// checked before it was raised, so parallel calls all passed); past them, <see cref="SignInStop.StillWaiting"/>. Every abandoned task is
/// observed, its fault never reaching the crash log (I12).</item>
/// </list>
/// An already-cancelled call starts nothing (Q14). A test takes <see cref="Scope"/>.
/// </summary>
public static class SignInGuard
{
    /// <summary>The abandoned calls one server may hold before the next is refused.</summary>
    public const int MaxAbandoned = 2;

    /// <summary>The calls (running and abandoned) one server may have at once.</summary>
    public const int MaxRunning = 8;

    private static State s_state = new();

    /// <summary>The hold key of a sign-in: SHA-256 of <paramref name="where"/> (lower-cased), the account and the password.</summary>
    public static string HoldKey(string where, string user, string password)
    {
        ArgumentNullException.ThrowIfNull(where);
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(password);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(where.Trim().ToLowerInvariant() + "\n" + user.Trim() + "\n" + password)));
    }

    /// <summary>
    /// The guard's facts for a Windows computer reached by host: the hold key shared by the families (a services.json, tasks.json and
    /// eventlog.json entry with one host, account and password are one sign-in), the server key per <paramref name="family"/>.
    /// </summary>
    public static SignInCall ForHost(string family, string? host, string? user, string? password, Func<Exception, bool> wrongPassword)
    {
        ArgumentNullException.ThrowIfNull(family);
        ArgumentNullException.ThrowIfNull(wrongPassword);
        string where = (host ?? "local").Trim().ToLowerInvariant();
        return new SignInCall(user is not null && password is not null ? HoldKey("host:" + where, user, password) : null, family + ":" + where, wrongPassword);
    }

    /// <summary>The Win32 answers that mean a wrong password (ERROR_LOGON_FAILURE, ERROR_INVALID_PASSWORD), as a code or its HRESULT; never locked (1909), disabled (1331), expired (1793, 1330) or must-change (1907): B8.</summary>
    public static bool IsWrongPassword(int code) => code is 1326 or 86 || unchecked((uint)code) is 0x8007052E or 0x80070056;

    /// <summary>Whether <paramref name="holdKey"/> is held (the tests' look).</summary>
    public static bool IsHeld(string holdKey)
    {
        lock (s_state)
        {
            return s_state.Held.Contains(holdKey);
        }
    }

    /// <summary>How many abandoned calls to <paramref name="server"/> still run (the tests' look).</summary>
    public static int AbandonedRunning(string server)
    {
        lock (s_state)
        {
            return s_state.Servers.TryGetValue(server, out var counts) ? counts.Abandoned : 0;
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> on a pool thread under the guard and <paramref name="timeout"/>. The caller's cancel is thrown (after the
    /// work is abandoned, or waited out once it began a change); the timeout is <see cref="SignInStop.TimedOut"/>; the work's own exception is
    /// <see cref="SignInRun{T}.Failure"/>.
    /// </summary>
    public static async Task<SignInRun<T>> RunAsync<T>(SignInCall call, Func<SignInTurn, T> work, TimeSpan timeout, TimeProvider time, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(time);
        cancellationToken.ThrowIfCancellationRequested();
        var state = s_state;
        if (call.HoldKey is { } held && IsHeldIn(state, held))
        {
            return new SignInRun<T>(default, null, SignInStop.Held);
        }

        if (!Admit(state, call.Server))
        {
            return new SignInRun<T>(default, null, SignInStop.StillWaiting);
        }

        bool released = false;
        using var timer = new CancellationTokenSource(timeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timer.Token);
        SemaphoreSlim? gate = null;
        try
        {
            if (call.HoldKey is { } key && GateFor(state, key) is { } unproven)
            {
                // One at a time until this password signs in (F1); the wait counts against the call's own time.
                try
                {
                    await unproven.WaitAsync(linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    Release(state, call.Server, abandoned: false);
                    released = true;
                    if (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }

                    return new SignInRun<T>(default, null, SignInStop.TimedOut);
                }

                gate = unproven;
                if (IsHeldIn(state, key))
                {
                    gate.Release();
                    gate = null;
                    Release(state, call.Server, abandoned: false);
                    released = true;
                    return new SignInRun<T>(default, null, SignInStop.Held);
                }
            }

            var turn = new SignInTurn(linked.Token);
            var running = Task.Run(() => work(turn), CancellationToken.None);
            var ownGate = gate;
            gate = null;

            // Whatever becomes of the wait, the task's end settles the books: the gate, the proof or the hold, the counts, its fault observed.
            var abandoned = new Flag();
            _ = running.ContinueWith(done =>
            {
                bool ok = done.Status == TaskStatus.RanToCompletion;
                if (call.HoldKey is { } holdKey)
                {
                    if (ok)
                    {
                        Proven(state, holdKey);
                    }
                    else if (done.Exception?.GetBaseException() is { } failure && call.WrongPassword(failure))
                    {
                        Hold(state, holdKey);
                    }
                }

                ownGate?.Release();
                Release(state, call.Server, abandoned.Set);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            released = true;

            try
            {
                return new SignInRun<T>(await running.WaitAsync(linked.Token).ConfigureAwait(false), null, SignInStop.None);
            }
            catch (Exception ex)
            {
                if (!running.IsCompleted)
                {
                    // The wait ended (a cancel, the timeout) while the work runs.
                    if (!turn.TryAbandon())
                    {
                        // A change had begun: waited out, never abandoned half made (T13: the caller is told it completed).
                        var outcome = await Settle(running).ConfigureAwait(false);
                        return outcome with { WaitedOut = true };
                    }

                    MarkAbandoned(state, call.Server, abandoned, running);
                    cancellationToken.ThrowIfCancellationRequested();
                    return new SignInRun<T>(default, null, SignInStop.TimedOut);
                }

                if (running.IsCompletedSuccessfully)
                {
                    // The work ended just as the wait did (2026-10-23, the branch review of DP7): its result stands, so a change made at the
                    // timeout's edge is never told "not changed" and tried again.
                    return new SignInRun<T>(running.Result, null, SignInStop.None);
                }

                var failure = running.Exception?.GetBaseException() ?? ex;
                if (running.IsCanceled || failure is OperationCanceledException)
                {
                    // The work stopped itself on the token.
                    cancellationToken.ThrowIfCancellationRequested();
                    if (linked.IsCancellationRequested)
                    {
                        return new SignInRun<T>(default, null, SignInStop.TimedOut);
                    }
                }

                return new SignInRun<T>(default, failure, SignInStop.None);
            }
        }
        finally
        {
            gate?.Release();
            if (!released)
            {
                Release(state, call.Server, abandoned: false);
            }
        }
    }

    private static async Task<SignInRun<T>> Settle<T>(Task<T> running)
    {
        try
        {
            return new SignInRun<T>(await running.ConfigureAwait(false), null, SignInStop.None);
        }
        catch (Exception ex)
        {
            return new SignInRun<T>(default, ex, SignInStop.None);
        }
    }

    /// <summary>A test's scope: the guard empty inside, the previous one put back after (the <c>ThemeScope</c> shape).</summary>
    public static IDisposable Scope()
    {
        var previous = s_state;
        s_state = new State();
        return new Restore(previous);
    }

    private static bool IsHeldIn(State state, string key)
    {
        lock (state)
        {
            return state.Held.Contains(key);
        }
    }

    private static void Hold(State state, string key)
    {
        lock (state)
        {
            state.Held.Add(key);
            state.Proven.Remove(key);
        }

        Diagnostics.DiagnosticLog.Debug("SignIn", "a password was refused; it is held until it changes");
    }

    private static void Proven(State state, string key)
    {
        lock (state)
        {
            state.Proven.Add(key);
        }
    }

    /// <summary>The gate a hold key's sign-ins wait on while its password is unproven; null once it has signed in.</summary>
    private static SemaphoreSlim? GateFor(State state, string key)
    {
        lock (state)
        {
            if (state.Proven.Contains(key))
            {
                return null;
            }

            if (!state.Gates.TryGetValue(key, out var gate))
            {
                gate = new SemaphoreSlim(1, 1);
                state.Gates[key] = gate;
            }

            return gate;
        }
    }

    private static bool Admit(State state, string server)
    {
        lock (state)
        {
            var counts = state.Servers.GetValueOrDefault(server);
            if (counts.Abandoned >= MaxAbandoned || counts.Running >= MaxRunning)
            {
                Diagnostics.DiagnosticLog.Debug("SignIn", $"{counts.Abandoned.ToString(CultureInfo.InvariantCulture)} abandoned and {counts.Running.ToString(CultureInfo.InvariantCulture)} running calls to {server}; refused");
                return false;
            }

            state.Servers[server] = counts with { Running = counts.Running + 1 };
            return true;
        }
    }

    private static void MarkAbandoned(State state, string server, Flag abandoned, Task running)
    {
        lock (state)
        {
            if (running.IsCompleted)
            {
                return;   // ended between the cancel and here: its continuation releases it as a plain call
            }

            abandoned.Set = true;
            var counts = state.Servers.GetValueOrDefault(server);
            state.Servers[server] = counts with { Abandoned = counts.Abandoned + 1 };
        }
    }

    private static void Release(State state, string server, bool abandoned)
    {
        lock (state)
        {
            var counts = state.Servers.GetValueOrDefault(server);
            counts = new Counts(Math.Max(0, counts.Running - 1), Math.Max(0, counts.Abandoned - (abandoned ? 1 : 0)));
            if (counts.Running == 0 && counts.Abandoned == 0)
            {
                state.Servers.Remove(server);
            }
            else
            {
                state.Servers[server] = counts;
            }
        }
    }

    private readonly record struct Counts(int Running, int Abandoned);

    /// <summary>Whether a call was abandoned, read by its task's continuation.</summary>
    private sealed class Flag
    {
        private volatile bool _set;

        public bool Set
        {
            get => _set;
            set => _set = value;
        }
    }

    private sealed class State
    {
        public readonly HashSet<string> Held = new(StringComparer.Ordinal);
        public readonly HashSet<string> Proven = new(StringComparer.Ordinal);
        public readonly Dictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);
        public readonly Dictionary<string, Counts> Servers = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class Restore(State previous) : IDisposable
    {
        public void Dispose() => s_state = previous;
    }
}
