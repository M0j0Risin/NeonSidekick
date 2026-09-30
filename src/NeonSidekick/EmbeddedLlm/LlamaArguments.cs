using System.Globalization;

namespace NeonSidekick.EmbeddedLlm;

/// <summary>
/// Everything one <c>llama-server</c> start depends on (2026-09-29). Its equality is the host's reuse test: a
/// reconnect whose launch equals the running one (a <c>/reasoning</c> change, a settings save that touched nothing
/// here) keeps the server, so gigabytes are not reloaded for nothing; any difference restarts it.
/// <paramref name="MayFallBack"/> is true when the backend was chosen by <c>auto</c>, so a CUDA start that fails
/// may be retried on Vulkan. <paramref name="Mtp"/> turns on MTP speculative decoding (2026-09-29), drafting with
/// <paramref name="DrafterPath"/> when there is one and with the weights' own head when not; toggling Embedded MTP
/// changes the launch, so it restarts the server.
/// </summary>
public sealed record LlamaLaunch(
    string Executable,
    LlamaBackend Backend,
    string ModelPath,
    string? MmprojPath,
    string Alias,
    int ContextSize,
    string GpuLayers,
    EmbeddedSampling Sampling,
    bool MayFallBack = false,
    string? DrafterPath = null,
    bool Mtp = false)
{
    /// <summary>The runtime folder: the process's working directory, where its DLLs are found.</summary>
    public string WorkingDirectory => Path.GetDirectoryName(Executable) ?? ".";

    /// <summary>Whether the vision projector is loaded (images can be sent).</summary>
    public bool Vision => MmprojPath is not null;
}

/// <summary>
/// The <c>llama-server</c> command line (2026-09-29), pure so the tests pin it. Every flag was checked against build
/// b11258's <c>--help</c>:
/// <list type="bullet">
/// <item><c>--host 127.0.0.1</c> and a port chosen per start: loopback only, and never one of the ports the scan probes
/// (8080 is llama.cpp's default), so the scan cannot list the app's own server as a second row.</item>
/// <item><c>--api-key</c>: a random key per start, so other programs on the machine cannot use the server.</item>
/// <item><c>--alias</c>: the catalog id, what <c>/v1/models</c> lists and the requests name.</item>
/// <item><c>--jinja</c>: the model's own chat template, which is what makes tool calls work (the default in this
/// build, passed anyway so a build that flips it does not silently break tools).</item>
/// <item><c>--parallel 1</c>: one slot owning the whole context, so <c>/props</c>' <c>n_ctx</c> is the context the
/// user set; the app's background requests (titles, reflection) queue behind the turn.</item>
/// <item><c>--no-webui</c>, <c>--log-colors off</c>: no web page to serve, no ANSI codes in the log lines.</item>
/// <item><c>--temp</c>/<c>--top-p</c>/<c>--top-k</c>: the model card's sampling as the server's defaults; a request's
/// own values (<c>/sampling</c>) still win.</item>
/// <item><c>-md</c> (<c>--spec-draft-model</c>) and <c>--spec-type draft-mtp</c> (2026-09-29): MTP speculative decoding,
/// in b11258 since llama.cpp PR #23398 (Gemma 4 MTP, 2026-06-07). With a drafter file (Gemma 4's <c>mtp-*.gguf</c>) both;
/// with a head inside the weights (Qwen3.8's NextN) the type alone, and the server builds the draft context on the
/// target's own weights. The target verifies every drafted token, so the answer is the same, only faster.</item>
/// </list>
/// </summary>
public static class LlamaArguments
{
    /// <summary>The argument list for <paramref name="launch"/> on <paramref name="port"/> with <paramref name="apiKey"/>.</summary>
    public static IReadOnlyList<string> Build(LlamaLaunch launch, int port, string apiKey)
    {
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        var args = new List<string> { "-m", launch.ModelPath };
        if (launch.MmprojPath is { } mmproj)
        {
            args.Add("--mmproj");
            args.Add(mmproj);
        }

        if (launch.Mtp)
        {
            if (launch.DrafterPath is { } drafter)
            {
                args.Add("-md");
                args.Add(drafter);
            }

            args.Add("--spec-type");
            args.Add("draft-mtp");
        }

        args.AddRange(
        [
            "--alias", launch.Alias,
            "--host", "127.0.0.1",
            "--port", port.ToString(CultureInfo.InvariantCulture),
            "--api-key", apiKey,
            "--jinja",
            "-c", launch.ContextSize.ToString(CultureInfo.InvariantCulture),
            "-ngl", launch.GpuLayers,
            "--parallel", "1",
            "--no-webui",
            "--log-colors", "off",
            "--temp", Number(launch.Sampling.Temperature),
            "--top-p", Number(launch.Sampling.TopP),
            "--top-k", launch.Sampling.TopK.ToString(CultureInfo.InvariantCulture),
        ]);
        return args;
    }

    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>The <c>Embedded GPU layers</c> setting's values (2026-09-29): <c>auto</c> (llama.cpp fits what VRAM holds), <c>all</c>, or a count.</summary>
public static class EmbeddedGpuLayers
{
    public const string Auto = "auto";
    public const string All = "all";
    public const int Max = 999;

    /// <summary>The settings-menu wording for a bad value. Pinned.</summary>
    public const string Error = "must be auto, all or a whole number from 0 to 999";

    /// <summary>The canonical spelling of a valid <paramref name="setting"/> (<c>auto</c>, <c>all</c>, a plain number), or null when it is not one.</summary>
    public static string? Normalize(string? setting)
    {
        string value = (setting ?? "").Trim().ToLowerInvariant();
        if (value is Auto or All)
        {
            return value;
        }

        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int layers) && layers <= Max
            ? layers.ToString(CultureInfo.InvariantCulture)
            : null;
    }

    /// <summary>What a start passes: the setting when valid, else <c>auto</c>.</summary>
    public static string Effective(string? setting) => Normalize(setting) ?? Auto;
}

/// <summary>The <c>Embedded context size</c> setting's range (2026-09-29): 0 for the model's own window, else 512 to 262144 tokens.</summary>
public static class EmbeddedContextSize
{
    public const int Default = 32_768;
    public const int Min = 512;
    public const int Max = 262_144;

    /// <summary>The settings-menu wording for a bad value. Pinned.</summary>
    public const string Error = "must be 0 (the model's own) or a whole number from 512 to 262144";

    public static bool IsValid(int value) => value == 0 || (value >= Min && value <= Max);

    /// <summary>What a start passes: the setting when valid, else <see cref="Default"/>.</summary>
    public static int Effective(int value) => IsValid(value) ? value : Default;
}
