using System.Diagnostics;
using System.Globalization;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.ActiveDirectory;

/// <summary>What one directory call came to.</summary>
public readonly record struct DirectoryResult<T>(T? Value, string? Error)
{
    public static DirectoryResult<T> Ok(T value) => new(value, null);

    public static DirectoryResult<T> Failed(string error) => new(default, error);
}

/// <summary>Where one call reads: the domain, the target bound to, and whether the model reads it redacted.</summary>
public sealed record DirectorySource(DirectoryDomain Domain, DirectoryTarget Target, bool Redacted)
{
    public string Where => Domain.DomainName;
}

/// <summary>
/// The directory's one way in (2026-10-15, the admin-tools plan), the tools' and <c>/ad</c>'s alike: the ticked domains (<c>Directory domains
/// offered</c>; <c>current</c> built in on a joined PC), the sign-in (a <c>runas</c> password resolved per call), and the work on a pool thread
/// under 30 s per search plus the bind's 10 s, abandoned when it runs out. Everything reads; nothing here can write (<see cref="LdapNative"/>
/// declares no write entry point). Configured domains are the user's own entries: never judged by <c>Network tools reach</c>.
/// </summary>
public sealed class DirectoryAccess
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);

    /// <summary>
    /// The runas sign-ins a domain refused this run (2026-10-18, the user's call, both systems), as SHA-256s of the domain, account and
    /// password: a call with the same three is answered <see cref="DirectoryText.Held"/> without reaching the DC, so a model retrying cannot
    /// lock the account. A new password hashes differently and is tried at once; nothing of the password itself is kept. Calls run on pool
    /// threads, so the set is locked.
    /// </summary>
    private static readonly HashSet<string> s_held = new(StringComparer.Ordinal);

    private static string HoldKey(DirectorySource source, DirectoryLogin login) =>
        Convert.ToHexString(global::System.Security.Cryptography.SHA256.HashData(global::System.Text.Encoding.UTF8.GetBytes(
            source.Domain.DomainName.ToLowerInvariant() + "\n" + login.User + "\n" + login.Password)));

    /// <summary>
    /// The calls abandoned at <see cref="Timeout"/> or ESC whose work still runs, by server and port (2026-10-20, a review's finding):
    /// wldap32's Negotiate bind (<c>ldap_bind_s</c>) takes no timeout, so a DC that accepts the connection and never answers the bind holds a
    /// pool thread and its connection until it lets go. At most <see cref="MaxAbandoned"/> such calls stand per server; past them a call is
    /// answered <see cref="DirectoryText.StillWaiting"/> without connecting, so a model retrying cannot pile up threads. Locked: pool threads.
    /// </summary>
    private static readonly Dictionary<string, int> s_abandoned = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The abandoned calls one server may hold before the next is refused.</summary>
    public const int MaxAbandoned = 2;

    private static string AbandonKey(DirectoryTarget target) => target.Server + ":" + target.Port.ToString(CultureInfo.InvariantCulture);

    /// <summary>How many abandoned calls to <paramref name="target"/> still run (the tests' look).</summary>
    public static int AbandonedRunning(DirectoryTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        lock (s_abandoned)
        {
            return s_abandoned.GetValueOrDefault(AbandonKey(target));
        }
    }

    /// <summary>A call given up on while its work runs: counted against its server until the work returns (its fault observed then).</summary>
    private static void Abandon(Task running, string key)
    {
        if (running.IsCompleted)
        {
            _ = running.Exception;
            return;
        }

        lock (s_abandoned)
        {
            s_abandoned[key] = s_abandoned.GetValueOrDefault(key) + 1;
        }

        _ = running.ContinueWith(done =>
        {
            _ = done.Exception;
            lock (s_abandoned)
            {
                if (s_abandoned.TryGetValue(key, out int left) && left > 1)
                {
                    s_abandoned[key] = left - 1;
                }
                else
                {
                    s_abandoned.Remove(key);
                }
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>Whether <paramref name="code"/> with <paramref name="detail"/> is a refused password (never Windows' NTLM-blocked variant, never an unreachable KDC).</summary>
    public static bool RefusedPassword(int code, string detail) =>
        code == 0x31 && !detail.Contains("NTLM", StringComparison.OrdinalIgnoreCase);

    private readonly Func<DirectoryCatalog> _loaded;
    private readonly Func<IReadOnlyList<string>?> _offered;
    private readonly Func<string?> _joined;

    public DirectoryAccess(IDirectorySystem? system, Func<DirectoryCatalog> loaded, Func<IReadOnlyList<string>?> offered, Func<string?> joined, TimeProvider time)
    {
        System = system;
        _loaded = loaded ?? throw new ArgumentNullException(nameof(loaded));
        _offered = offered ?? throw new ArgumentNullException(nameof(offered));
        _joined = joined ?? throw new ArgumentNullException(nameof(joined));
        Time = time ?? throw new ArgumentNullException(nameof(time));
    }

    public IDirectorySystem? System { get; }

    public TimeProvider Time { get; }

    /// <summary>This PC's joined domain, or null.</summary>
    public string? Joined => _joined();

    public DirectoryCatalog Loaded() => _loaded();

    public DirectoryCatalog Catalog() => _loaded().Offered(_offered(), _joined() is not null);

    public (DirectorySource? Source, string? Error) Domain(string? name, bool forceRedacted)
    {
        var (found, error) = Catalog().Find(name);
        if (found is null)
        {
            return (null, error);
        }

        DirectoryLogin? login = null;
        if (found.Config.IsRunAs)
        {
            var secret = DirectoryConfigFile.ResolvePassword(found);
            if (secret.Error is { } missing)
            {
                return (null, Computers.ComputerText.CannotSignIn(found.Name, missing));
            }

            string user = found.Config.User!.Trim();
            if (Sql.KerberosSignIn.Offered)
            {
                // A Mac signs in through Kerberos (2026-10-18, the Mac AD plan, SQL's rule): name@REALM, the realm in capitals. The file's
                // check keeps Windows' rule, so an ad.json shared with a PC loads whole, and a DOMAIN\name is refused here, at the call.
                if (Sql.KerberosSignIn.Principal(user) is not { } principal)
                {
                    return (null, "Error: " + Sql.SqlText.RunAsNeedsRealm(user));
                }

                user = principal;
            }

            login = new DirectoryLogin(user, secret.Value!);
        }

        return (new DirectorySource(found, new DirectoryTarget(found.Server, found.Port, found.Config.UsesTls, login, found.DomainName), forceRedacted || !found.Config.Full), null);
    }

    public Task<DirectoryResult<T>> RunAsync<T>(DirectorySource source, Func<IDirectorySession, T> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        return RunAsync(source, (session, _) => work(session), cancellationToken);
    }

    /// <summary>
    /// <see cref="RunAsync{T}(DirectorySource, Func{IDirectorySession, T}, CancellationToken)"/> whose work is handed a deadline (2026-10-15, the AD
    /// gaps plan): <see cref="DirectoryLimits.CallBudget"/> from this call's start and the caller's token, so a walk answers with what it has before
    /// <see cref="Timeout"/> abandons it, and ESC stops it starting searches.
    /// </summary>
    public async Task<DirectoryResult<T>> RunAsync<T>(DirectorySource source, Func<IDirectorySession, DirectoryDeadline, T> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(work);
        var deadline = DirectoryDeadline.From(Time, DirectoryLimits.CallBudget, cancellationToken);
        if (System is not { } system)
        {
            return DirectoryResult<T>.Failed(DirectoryText.NoneHere);
        }

        string? holdKey = source.Target.Login is { } login ? HoldKey(source, login) : null;
        if (holdKey is not null)
        {
            lock (s_held)
            {
                if (s_held.Contains(holdKey))
                {
                    return DirectoryResult<T>.Failed(DirectoryText.Held(source.Where, source.Domain.Account));
                }
            }
        }

        string abandonKey = AbandonKey(source.Target);
        lock (s_abandoned)
        {
            if (s_abandoned.GetValueOrDefault(abandonKey) >= MaxAbandoned)
            {
                DiagnosticLog.Debug(DirectoryText.Category, $"{source.Domain.Name}: {MaxAbandoned.ToString(CultureInfo.InvariantCulture)} earlier calls to {source.Target.Server} still waiting; refused");
                return DirectoryResult<T>.Failed(DirectoryText.StillWaiting(source.Where, source.Target.Server));
            }
        }

        using var timeout = new CancellationTokenSource(Timeout, Time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var started = Stopwatch.GetTimestamp();
        var running = Task.Run(() => system.Run(source.Target, session => work(session, deadline)), CancellationToken.None);
        try
        {
            var value = await running.WaitAsync(linked.Token).ConfigureAwait(false);
            DiagnosticLog.Debug(DirectoryText.Category, $"{source.Domain.Name} ({source.Target.Server}) read in {Stopwatch.GetElapsedTime(started).TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)} ms");
            return DirectoryResult<T>.Ok(value);
        }
        catch (DirectoryException ex)
        {
            DiagnosticLog.Debug(DirectoryText.Category, $"{source.Domain.Name} failed: {ex.Message}");
            if (holdKey is not null && RefusedPassword(ex.Code, ex.Message))
            {
                lock (s_held)
                {
                    s_held.Add(holdKey);
                }

                DiagnosticLog.Debug(DirectoryText.Category, $"{source.Domain.Name}: the password for {source.Domain.Account} was refused; held until it changes");
            }

            return DirectoryResult<T>.Failed(DirectoryErrors.Sentence(ex.Code, source.Where, source.Target.Server, source.Target.Port, source.Domain.Account, ex.Message));
        }
        catch (OperationCanceledException)
        {
            Abandon(running, abandonKey);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return DirectoryResult<T>.Failed(DirectoryText.TimedOut(source.Where));
        }
    }

    /// <summary>A domain tried as it stands (the wizard's Test and <c>--ad-check</c>): RootDSE, the bind, the domain's policy and one user read.</summary>
    public static async Task<(bool Ok, string Text)> TestAsync(IDirectorySystem? system, DirectoryDomain domain, TimeProvider time, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domain);
        var access = new DirectoryAccess(system, () => new DirectoryCatalog([domain], []), () => [domain.Name], () => null, time);
        var (source, error) = access.Domain(domain.Name, forceRedacted: true);
        if (source is null)
        {
            return (false, error!);
        }

        var result = await access.RunAsync(source, session =>
        {
            var root = session.RootDse();
            var head = session.Search(domain.BaseDn, DirectoryScope.Base, "(objectClass=*)", AdAttributes.DomainHead, 1, paged: false);
            var users = session.Search(domain.BaseDn, DirectoryScope.Subtree, "(&" + LdapFilter.Users + ")", ["sAMAccountName"], 1, paged: false);
            return (root.Text("dnsHostName") ?? domain.Server, head.Entries.Count, users.Entries.Count, session.Transport);
        }, cancellationToken).ConfigureAwait(false);
        return result.Error is { } failed
            ? (false, failed)
            : (true, DirectoryText.WithTransport(DirectoryText.TestOk(domain.Name, result.Value.Item1, domain.DomainName, result.Value.Item3), result.Value.Item4));   // how it connected, where the backend says (a Mac's, 2026-10-18)
    }
}
