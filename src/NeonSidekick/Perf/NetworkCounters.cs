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
/// The adapters read are those that are up, are neither loopback nor a tunnel, and have a gateway, so a Hyper-V or WSL
/// <c>vEthernet</c> switch without one is never read; which of them the meters show is <see cref="NetworkMeter"/>'s call. Each
/// adapter is read on its own (later on 2026-09-30, the review's catch): one that goes away between the list and its
/// statistics is left out of this reading, not every adapter with it.
/// </summary>
public sealed class NetworkCounters : INetworkCounters
{
    public IReadOnlyList<NetworkAdapterReading> Read()
    {
        var adapters = new List<NetworkAdapterReading>();
        NetworkInterface[] all;
        try
        {
            all = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (Exception ex) when (Unreadable(ex))
        {
            return adapters;   // no adapters readable: the network meters are left out, as the GPU's are without a GPU
        }

        foreach (var adapter in all)
        {
            try
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
            catch (Exception ex) when (Unreadable(ex))
            {
                // Gone between the list and its statistics (a VPN or a Hyper-V switch going down): this reading goes without it.
            }
        }

        return adapters;
    }

    private static bool Unreadable(Exception ex) => ex is NetworkInformationException or PlatformNotSupportedException or InvalidOperationException;
}

/// <summary>
/// The network meters from two readings a sample apart (2026-09-30): the busiest adapter's bits a second received and sent,
/// and its link speed. The busiest, not the sum (later on 2026-09-30, the review's catch): a VPN adapter with a gateway
/// carries the same bytes as the card under it, so a sum read double, and a docked laptop's Wi-Fi and Ethernet together
/// summed their links and halved NET%; Windows sends the traffic over one of them, and the busiest is that one. Each adapter
/// is rated against its own last reading, whenever that was: one missing from a reading keeps its last, so a reading that
/// skipped it is no gap in the meters, and an adapter new to the list adds nothing until its second reading (a Wi-Fi
/// reconnect is no spike). Null before a second reading of any adapter, and with none.
/// </summary>
public sealed class NetworkMeter(INetworkCounters counters, Func<TimeSpan>? clock = null)
{
    // An adapter not seen for this long is forgotten: a VPN connected once a day leaves no reading behind for the day.
    private static readonly TimeSpan Forget = TimeSpan.FromMinutes(1);

    private readonly INetworkCounters _counters = counters ?? throw new ArgumentNullException(nameof(counters));
    private readonly Func<TimeSpan> _clock = clock ?? (() => Stopwatch.GetElapsedTime(0));
    private readonly Dictionary<string, (NetworkAdapterReading Reading, TimeSpan At)> _previous = new(StringComparer.Ordinal);

    /// <summary>The busiest adapter's rates since its last reading, in bits/s, and its link speed; null before a second reading or with no adapter.</summary>
    public (double Down, double Up, double Link)? Sample()
    {
        var now = _counters.Read();
        var at = _clock();
        (double Down, double Up, double Link)? busiest = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var adapter in now)
        {
            if (!seen.Add(adapter.Id))
            {
                continue;
            }

            if (_previous.TryGetValue(adapter.Id, out var before))
            {
                var elapsed = at - before.At;
                double down = PerfMath.Rate(before.Reading.Received, adapter.Received, elapsed) ?? 0;
                double up = PerfMath.Rate(before.Reading.Sent, adapter.Sent, elapsed) ?? 0;
                double load = Math.Max(down, up);
                if (busiest is not { } b || load > Math.Max(b.Down, b.Up) || (load == Math.Max(b.Down, b.Up) && adapter.LinkBits > b.Link))
                {
                    busiest = (down, up, adapter.LinkBits);   // at rest, the fastest link's
                }
            }

            _previous[adapter.Id] = (adapter, at);
        }

        foreach (string id in _previous.Where(p => at - p.Value.At > Forget).Select(p => p.Key).ToList())
        {
            _previous.Remove(id);
        }

        return busiest;
    }

    /// <summary>Forgets every reading (the meters left off a while): the next <see cref="Sample"/> is a first again.</summary>
    public void Reset() => _previous.Clear();
}
