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

            login = new DirectoryLogin(found.Config.User!.Trim(), secret.Value!);
        }

        return (new DirectorySource(found, new DirectoryTarget(found.Server, found.Port, found.Config.UsesTls, login), forceRedacted || !found.Config.Full), null);
    }

    public async Task<DirectoryResult<T>> RunAsync<T>(DirectorySource source, Func<IDirectorySession, T> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(work);
        if (System is not { } system)
        {
            return DirectoryResult<T>.Failed(DirectoryText.NoneHere);
        }

        using var timeout = new CancellationTokenSource(Timeout, Time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var started = Stopwatch.GetTimestamp();
        try
        {
            var value = await Task.Run(() => system.Run(source.Target, work), CancellationToken.None).WaitAsync(linked.Token).ConfigureAwait(false);
            DiagnosticLog.Debug(DirectoryText.Category, $"{source.Domain.Name} ({source.Target.Server}) read in {Stopwatch.GetElapsedTime(started).TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)} ms");
            return DirectoryResult<T>.Ok(value);
        }
        catch (DirectoryException ex)
        {
            DiagnosticLog.Debug(DirectoryText.Category, $"{source.Domain.Name} failed: {ex.Message}");
            return DirectoryResult<T>.Failed(DirectoryErrors.Sentence(ex.Code, source.Where, source.Target.Server, source.Target.Port, source.Domain.Account, ex.Message));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
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
            return (root.Text("dnsHostName") ?? domain.Server, head.Entries.Count, users.Entries.Count);
        }, cancellationToken).ConfigureAwait(false);
        return result.Error is { } failed ? (false, failed) : (true, DirectoryText.TestOk(domain.Name, result.Value.Item1, domain.DomainName, result.Value.Item3));
    }
}
