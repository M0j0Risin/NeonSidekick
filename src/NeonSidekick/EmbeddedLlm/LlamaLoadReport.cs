using System.Globalization;
using System.Text.RegularExpressions;

namespace NeonSidekick.EmbeddedLlm;

/// <summary>
/// What <c>llama-server</c>'s load lines say about where the model went (2026-10-01, the user's ask: Embedded VRAM only),
/// read from the log at <c>-lv 4</c> — at the default level 3 build b11258 prints none of them. Measured that day on an
/// RTX 5090 (CUDA and Vulkan), the lines it keeps:
/// <list type="bullet">
/// <item><c>load_tensors: offloaded 66/66 layers to GPU</c>, once per model loaded (the weights, a drafter): fewer than all
/// is layers in system RAM. Each one opens a load of its own.</item>
/// <item><c>CUDA_Host compute buffer size = 154.77 MiB</c> and <c>CUDA_Host  output buffer size = 0.95 MiB</c>
/// (<c>Vulkan_Host</c> on Vulkan): pinned host memory llama.cpp asks for itself, which Windows counts as the process's shared
/// GPU memory — the part of that reading that is not a spill. Kept per load and per buffer, the last size winning (the
/// same day's review): the spike's logs, fit's probe passes included, print each buffer once per context, but a second
/// reserve of the same buffer replaces it rather than adding to it, so a repeated line cannot widen the allowance and hide
/// a spill.</item>
/// <item><c>cudaMalloc failed: out of memory</c> / <c>ErrorOutOfDeviceMemory</c> and <c>failed to allocate … buffer</c>: a
/// buffer that did not fit; with every layer on the GPU the server then exits before it is ready. A line about
/// <c>pinned</c> memory is not one (the same day's review): llama.cpp falls back to plain host memory then and goes on.</item>
/// <item><c>srv  llama_server: model loaded</c>: the end of the load, after every line above, on the same stream
/// (<see cref="Loaded"/>) — <c>/health</c> may answer 200 before the pipe has delivered it.</item>
/// </list>
/// Not thread-safe: the host feeds it under its own lock.
/// </summary>
public sealed partial class LlamaLoadReport
{
    private readonly List<Load> _loads = [new Load(0, 0)];   // the first is the lines before any load line

    /// <summary>The layers put on the GPU, summed over the loads seen.</summary>
    public int OffloadedLayers => _loads.Sum(l => l.Offloaded);

    /// <summary>The layers there were, summed over the loads seen; 0 when no load line came.</summary>
    public int TotalLayers => _loads.Sum(l => l.Total);

    /// <summary>The pinned host buffers in MiB: each load's last size of each buffer, summed.</summary>
    public double HostBufferMiB => _loads.Sum(l => l.HostBuffers.Values.Sum());

    /// <summary>True when a device allocation failed.</summary>
    public bool OutOfMemory { get; private set; }

    /// <summary>True once the server said the model is loaded: every load line is in by then.</summary>
    public bool Loaded { get; private set; }

    /// <summary>The layers left in system RAM.</summary>
    public int LayersOnCpu => TotalLayers - OffloadedLayers;

    /// <summary>A report of <paramref name="lines"/>, in order.</summary>
    public static LlamaLoadReport Parse(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var report = new LlamaLoadReport();
        foreach (var line in lines)
        {
            report.Add(line);
        }

        return report;
    }

    /// <summary>Takes one log line in; lines that say nothing about memory are ignored.</summary>
    public void Add(string? line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return;
        }

        if (Offloaded().Match(line) is { Success: true } offloaded)
        {
            _loads.Add(new Load(
                int.Parse(offloaded.Groups[1].ValueSpan, CultureInfo.InvariantCulture),
                int.Parse(offloaded.Groups[2].ValueSpan, CultureInfo.InvariantCulture)));
        }
        else if (HostBuffer().Match(line) is { Success: true } host)
        {
            string key = host.Groups[1].Value + " " + host.Groups[2].Value;
            _loads[^1].HostBuffers[key] = double.Parse(host.Groups[3].ValueSpan, NumberStyles.Float, CultureInfo.InvariantCulture);
        }
        else if (line.TrimEnd().EndsWith(": model loaded", StringComparison.Ordinal))
        {
            Loaded = true;
        }
        else if (!line.Contains("pinned", StringComparison.OrdinalIgnoreCase)
            && (line.Contains("cudaMalloc failed", StringComparison.OrdinalIgnoreCase)
                || line.Contains("OutOfDeviceMemory", StringComparison.OrdinalIgnoreCase)
                || (line.Contains("failed to allocate", StringComparison.OrdinalIgnoreCase) && line.Contains(" buffer", StringComparison.OrdinalIgnoreCase))))
        {
            OutOfMemory = true;
        }
    }

    [GeneratedRegex(@"offloaded (\d+)/(\d+) layers to GPU", RegexOptions.CultureInvariant)]
    private static partial Regex Offloaded();

    [GeneratedRegex(@"\b(\w+_Host)\s+(\w+)\s+buffer size\s*=\s*([0-9]+(?:\.[0-9]+)?)\s*MiB", RegexOptions.CultureInvariant)]
    private static partial Regex HostBuffer();

    /// <summary>One model's load: its layer line and its pinned host buffers by name and kind.</summary>
    private sealed record Load(int Offloaded, int Total)
    {
        public Dictionary<string, double> HostBuffers { get; } = new(StringComparer.Ordinal);
    }
}

/// <summary>
/// A load that put part of the model in system memory while Embedded VRAM only was on (2026-10-01): the layers llama.cpp
/// left on the CPU, and the shared GPU memory beyond its own pinned buffers — what the NVIDIA driver's sysmem fallback
/// placed there without llama.cpp knowing (<see cref="Check"/>).
/// </summary>
public sealed record VramSpill(int LayersOnCpu, int TotalLayers, long SharedMiB)
{
    /// <summary>
    /// The shared memory a clean load may hold beyond its pinned host buffers, in MiB. Measured on 2026-10-01 (RTX 5090,
    /// driver defaults): clean loads sat 78 to 143 MiB above their buffers, a load past free VRAM 527 MiB above. Pinned.
    /// </summary>
    public const int SlackMiB = 256;

    /// <summary>
    /// The spill in <paramref name="report"/> and <paramref name="sharedBytes"/> (the server's shared GPU memory, null when
    /// it was not read), or null for a clean load: no layer on the CPU and no more shared memory than the host buffers
    /// plus <paramref name="slackMiB"/>. A report with no layer line says nothing about the layers; the host refuses it
    /// before asking (<see cref="LlamaServerHost.VramRefusal"/>).
    /// </summary>
    public static VramSpill? Check(LlamaLoadReport report, long? sharedBytes, int slackMiB = SlackMiB)
    {
        ArgumentNullException.ThrowIfNull(report);
        long beyond = sharedBytes is { } bytes ? (long)Math.Round(bytes / 1_048_576.0 - report.HostBufferMiB) : 0;
        bool sharedSpill = beyond > slackMiB;
        return report.LayersOnCpu > 0 || sharedSpill
            ? new VramSpill(Math.Max(report.LayersOnCpu, 0), report.TotalLayers, sharedSpill ? beyond : 0)
            : null;
    }
}
