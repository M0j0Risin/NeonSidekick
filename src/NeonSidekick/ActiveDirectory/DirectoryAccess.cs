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
    /// The abandoned calls one server may hold before the next is refused (<see cref="Computers.SignInGuard.MaxAbandoned"/>). The refused-password
    /// hold (2026-10-18, the user's call) and this cap (2026-10-20, a review: wldap32's Negotiate bind takes no timeout) moved into the shared
    /// <see cref="Computers.SignInGuard"/> on 2026-10-23 (the admin review's mechanism 2), which the services, tasks and event logs use too.
    /// </summary>
    public const int MaxAbandoned = Computers.SignInGuard.MaxAbandoned;

    private static string AbandonKey(DirectoryTarget target) => "ad:" + target.Server + ":" + target.Port.ToString(CultureInfo.InvariantCulture);

    /// <summary>How many abandoned calls to <paramref name="target"/> still run (the tests' look).</summary>
    public static int AbandonedRunning(DirectoryTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return Computers.SignInGuard.AbandonedRunning(AbandonKey(target));
    }

    /// <summary>
    /// Whether <paramref name="code"/> with <paramref name="detail"/> is a refused password: never Windows' NTLM-blocked variant, never an
    /// unreachable KDC, and since 2026-10-23 (the admin review's B8, with J5's server error) never an account that is locked, disabled, expired
    /// or must change its password — 0x31 says all of those, and holding the password made an unlock take effect only at the next start.
    /// Windows' server error names it (<c>data 775</c>, …: <see cref="DirectoryErrors.SubCode"/>); a Mac's KDC says it apart
    /// (<see cref="Ldap.LdapResultCodes.AccountRevoked"/>). With no sub-code, 0x31 is held, as before.
    /// </summary>
    public static bool RefusedPassword(int code, string detail) =>
        code == 0x31 && !detail.Contains("NTLM", StringComparison.OrdinalIgnoreCase) && DirectoryErrors.SubCode(detail) is null or "52e";

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

        var call = new Computers.SignInCall(
            source.Target.Login is { } login ? Computers.SignInGuard.HoldKey("ad:" + source.Domain.DomainName, login.User, login.Password) : null,
            AbandonKey(source.Target),
            failure => failure is DirectoryException refused && RefusedPassword(refused.Code, refused.Message));
        var started = Stopwatch.GetTimestamp();
        var run = await Computers.SignInGuard.RunAsync(call, _ => system.Run(source.Target, session => work(session, deadline)), Timeout, Time, cancellationToken).ConfigureAwait(false);
        switch (run.Stop)
        {
            case Computers.SignInStop.Held:
                return DirectoryResult<T>.Failed(DirectoryText.Held(source.Where, source.Domain.Account));
            case Computers.SignInStop.StillWaiting:
                return DirectoryResult<T>.Failed(DirectoryText.StillWaiting(source.Where, source.Target.Server));
            case Computers.SignInStop.TimedOut:
                return DirectoryResult<T>.Failed(DirectoryText.TimedOut(source.Where));
        }

        if (run.Failure is DirectoryException ex)
        {
            DiagnosticLog.Debug(DirectoryText.Category, $"{source.Domain.Name} failed: {ex.Message}");
            return DirectoryResult<T>.Failed(DirectoryErrors.Sentence(ex.Code, source.Where, source.Target.Server, source.Target.Port, source.Domain.Account, ex.Message));
        }

        if (run.Failure is { } other)
        {
            global::System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(other).Throw();
        }

        DiagnosticLog.Debug(DirectoryText.Category, $"{source.Domain.Name} ({source.Target.Server}) read in {Stopwatch.GetElapsedTime(started).TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)} ms");
        return DirectoryResult<T>.Ok(run.Value!);
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
