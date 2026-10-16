using System.Net;

namespace NeonSidekick.Network;

/// <summary>What one echo came to.</summary>
public enum PingStatus
{
    /// <summary>The target answered.</summary>
    Reply,

    /// <summary>A router on the way said the TTL ran out: a trace's hop.</summary>
    TtlExpired,

    /// <summary>A router (or the target) said it cannot be reached.</summary>
    Unreachable,

    /// <summary>No answer in time.</summary>
    TimedOut,

    /// <summary>The echo could not be sent at all; <see cref="PingHop.Detail"/> says why.</summary>
    Failed,
}

/// <summary>One echo's outcome: who answered (the target, a router, nobody) and how long it took.</summary>
public readonly record struct PingHop(PingStatus Status, IPAddress? From, TimeSpan? Rtt, string? Detail = null);

/// <summary>
/// The echo the network tools send (2026-10-15, the admin-tools plan): <see cref="WindowsPinger"/> over .NET's <c>Ping</c> (IcmpSendEcho2,
/// in-process), <see cref="IcmpSocketPinger"/> elsewhere over an unprivileged ICMP datagram socket — never .NET's <c>Ping</c> off Windows,
/// which starts <c>/sbin/ping</c> when it cannot open a raw socket. <c>Program</c> picks by system; tests pass <c>FakePinger</c>.
/// </summary>
public interface IPinger
{
    /// <summary>One echo to <paramref name="address"/> with <paramref name="ttl"/>, waiting at most <paramref name="timeout"/>.</summary>
    Task<PingHop> PingAsync(IPAddress address, int ttl, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>Whether a router's Time Exceeded reaches this pinger, so a trace can be run.</summary>
    bool CanTrace { get; }
}
