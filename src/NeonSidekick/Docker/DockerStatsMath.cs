using System.Text.Json;

namespace NeonSidekick.Docker;

/// <summary>One container's resource use, from one <c>GET /containers/{id}/stats?stream=false</c>.</summary>
/// <param name="CpuPercent">Of one CPU, as <c>docker stats</c> shows it: 250 is two and a half CPUs busy.</param>
/// <param name="MemoryUsed">The working set: the usage less the inactive page cache.</param>
/// <param name="MemoryLimit">The limit; the engine's whole memory when none is set.</param>
/// <param name="Pids">The processes in it, null when the engine does not say.</param>
public sealed record DockerStatsSample(double CpuPercent, long MemoryUsed, long MemoryLimit, long NetRx, long NetTx, long BlockRead, long BlockWrite, long? Pids)
{
    /// <summary>The memory used as a share of the limit, 0 to 100.</summary>
    public double MemoryPercent => MemoryLimit > 0 ? MemoryUsed * 100.0 / MemoryLimit : 0;
}

/// <summary>
/// <c>docker stats</c>' arithmetic over the engine's sample (2026-10-02). Without <c>one-shot</c> the engine waits for a
/// second reading and sends the first as <c>precpu_stats</c>, so one request gives the CPU share: the container's CPU
/// time over the machine's in that interval, times the CPUs online. Memory is the CLI's working set: the usage less the
/// inactive page cache (<c>inactive_file</c> under cgroup v2, <c>total_inactive_file</c> under v1). Pure.
/// </summary>
public static class DockerStatsMath
{
    /// <summary>The sample in <paramref name="json"/>, or null for an answer that is not one.</summary>
    public static DockerStatsSample? Parse(string json)
    {
        using var doc = DockerJson.TryParse(json);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var r = doc.RootElement;
        if (!r.TryGetProperty("cpu_stats", out var cpu) || cpu.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        double cpuPercent = r.TryGetProperty("precpu_stats", out var pre) && pre.ValueKind == JsonValueKind.Object ? CpuPercent(cpu, pre) : 0;
        var (used, limit) = r.TryGetProperty("memory_stats", out var memory) && memory.ValueKind == JsonValueKind.Object ? Memory(memory) : (0, 0);
        var (rx, tx) = r.TryGetProperty("networks", out var networks) && networks.ValueKind == JsonValueKind.Object ? Network(networks) : (0, 0);
        var (read, write) = r.TryGetProperty("blkio_stats", out var blkio) && blkio.ValueKind == JsonValueKind.Object ? Block(blkio) : (0, 0);
        long? pids = r.TryGetProperty("pids_stats", out var p) && p.ValueKind == JsonValueKind.Object ? DockerJson.Long(p, "current") : null;
        return new DockerStatsSample(cpuPercent, used, limit, rx, tx, read, write, pids);
    }

    /// <summary>The CPU share between the two readings; 0 when either delta is not positive.</summary>
    public static double CpuPercent(JsonElement cpu, JsonElement pre)
    {
        ulong total = Total(cpu);
        ulong preTotal = Total(pre);
        ulong system = DockerJson.ULong(cpu, "system_cpu_usage") ?? 0;
        ulong preSystem = DockerJson.ULong(pre, "system_cpu_usage") ?? 0;
        if (total <= preTotal || system <= preSystem)
        {
            return 0;
        }

        long online = DockerJson.Long(cpu, "online_cpus") ?? 0;
        if (online <= 0 && cpu.TryGetProperty("cpu_usage", out var usage) && usage.TryGetProperty("percpu_usage", out var per) && per.ValueKind == JsonValueKind.Array)
        {
            online = per.GetArrayLength();
        }

        return CpuPercent(total - preTotal, system - preSystem, Math.Max(1, online));
    }

    /// <summary>The formula alone: <c>cpu_delta / system_delta × cpus × 100</c>; 0 for a zero system delta.</summary>
    public static double CpuPercent(ulong cpuDelta, ulong systemDelta, long cpus) =>
        systemDelta == 0 ? 0 : (double)cpuDelta / systemDelta * cpus * 100.0;

    private static ulong Total(JsonElement cpu) =>
        cpu.TryGetProperty("cpu_usage", out var usage) && usage.ValueKind == JsonValueKind.Object ? DockerJson.ULong(usage, "total_usage") ?? 0 : 0;

    /// <summary>The working set and the limit: the usage less the inactive page cache, cgroup v2's key first.</summary>
    public static (long Used, long Limit) Memory(JsonElement memory)
    {
        long usage = DockerJson.Long(memory, "usage") ?? 0;
        long cache = 0;
        if (memory.TryGetProperty("stats", out var stats) && stats.ValueKind == JsonValueKind.Object)
        {
            cache = DockerJson.Long(stats, "inactive_file") ?? DockerJson.Long(stats, "total_inactive_file") ?? 0;
        }

        long used = cache > 0 && cache < usage ? usage - cache : usage;
        ulong limit = DockerJson.ULong(memory, "limit") ?? 0;
        return (used, limit > long.MaxValue ? long.MaxValue : (long)limit);
    }

    private static (long Rx, long Tx) Network(JsonElement networks)
    {
        long rx = 0;
        long tx = 0;
        foreach (var network in networks.EnumerateObject())
        {
            rx += DockerJson.Long(network.Value, "rx_bytes") ?? 0;
            tx += DockerJson.Long(network.Value, "tx_bytes") ?? 0;
        }

        return (rx, tx);
    }

    private static (long Read, long Write) Block(JsonElement blkio)
    {
        long read = 0;
        long write = 0;
        if (blkio.TryGetProperty("io_service_bytes_recursive", out var entries) && entries.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in entries.EnumerateArray())
            {
                long value = DockerJson.Long(entry, "value") ?? 0;
                switch (DockerJson.Str(entry, "op")?.ToLowerInvariant())
                {
                    case "read":
                        read += value;
                        break;
                    case "write":
                        write += value;
                        break;
                }
            }
        }

        return (read, write);
    }
}
