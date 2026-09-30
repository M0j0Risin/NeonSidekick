using System.Diagnostics;
using System.Net.NetworkInformation;

namespace NeonSidekick.Perf;

/// <summary>One network adapter's running totals: its id, the bytes it has received and sent, and its link speed in bits/s (0 when unsaid).</summary>
public readonly record struct NetworkAdapterReading(string Id, long Received, long Sent, long LinkBits);

/// <summary>Where the network meters read the adapters: <see cref="NetworkCounters"/> on the machine, a script in the tests.</summary>
public interface INetworkCounters
{
    /// <summary>The adapters that carry the machine's traffic, their totals now; empty when there is none.</summary>
    IReadOnlyList<NetworkAdapterReading> Read();
}

/// <summary>
/// The machine's network adapters as .NET's own <see cref="NetworkInterface"/> reports them (2026-09-30, the performance
/// bar's NET%, NET↓ and NET↑, the user's ask): no P/Invoke of the app's own and no native library — the runtime reads the
/// adapters (on Windows through iphlpapi) and is AOT-clean; the smoke's <c>perf:network</c> proves it on the published exe.
/// The adapters counted are those that are up, are neither loopback nor a tunnel, and have a gateway: the one that really
/// carries the traffic, so a Hyper-V or WSL <c>vEthernet</c> switch without one never counts the same bytes twice.
/// </summary>
public sealed class NetworkCounters : INetworkCounters
{
    public IReadOnlyList<NetworkAdapterReading> Read()
    {
        var adapters = new List<NetworkAdapterReading>();
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up
                    || adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel
                    || adapter.GetIPProperties().GatewayAddresses.Count == 0)
                {
                    continue;
                }

                var statistics = adapter.GetIPStatistics();
                adapters.Add(new NetworkAdapterReading(adapter.Id, statistics.BytesReceived, statistics.BytesSent, Math.Max(0, adapter.Speed)));
            }
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException or InvalidOperationException)
        {
            // No adapters readable: the network meters are left out, as the GPU's are without a GPU.
        }

        return adapters;
    }
}

/// <summary>
/// The network meters from two readings a sample apart (2026-09-30): the bits a second received and sent over the adapters
/// seen in both readings — an adapter that came or went between them adds nothing, so a Wi-Fi reconnect is no spike — and
/// the link speed of those now. Null before the second reading, and with no adapter.
/// </summary>
public sealed class NetworkMeter(INetworkCounters counters, Func<TimeSpan>? clock = null)
{
    private readonly INetworkCounters _counters = counters ?? throw new ArgumentNullException(nameof(counters));
    private readonly Func<TimeSpan> _clock = clock ?? (() => Stopwatch.GetElapsedTime(0));
    private Dictionary<string, NetworkAdapterReading>? _previous;
    private TimeSpan _previousAt;

    /// <summary>The rates since the last call, in bits/s, and the link speed; null before a second reading or with no adapter.</summary>
    public (double Down, double Up, double Link)? Sample()
    {
        var now = _counters.Read();
        var at = _clock();
        var previous = _previous;
        var elapsed = at - _previousAt;
        _previous = now.GroupBy(a => a.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        _previousAt = at;
        if (previous is null || now.Count == 0)
        {
            return null;
        }

        double down = 0, up = 0, link = 0;
        bool any = false;
        foreach (var adapter in now)
        {
            if (!previous.TryGetValue(adapter.Id, out var before))
            {
                continue;
            }

            any = true;
            down += PerfMath.Rate(before.Received, adapter.Received, elapsed) ?? 0;
            up += PerfMath.Rate(before.Sent, adapter.Sent, elapsed) ?? 0;
            link += adapter.LinkBits;
        }

        return any ? (down, up, link) : null;
    }
}
