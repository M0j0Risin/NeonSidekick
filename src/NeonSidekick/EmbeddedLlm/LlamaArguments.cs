using System.Globalization;

namespace NeonSidekick.EmbeddedLlm;

/// <summary>
/// Everything one <c>llama-server</c> start depends on (2026-09-29). Its equality is the host's reuse test: a
/// reconnect whose launch equals the running one (a <c>/reasoning</c> change, a settings save that touched nothing
/// here) keeps the server, so gigabytes are not reloaded for nothing; any difference restarts it.
/// <paramref name="MayFallBack"/> is true when the backend was chosen by <c>auto</c>, so a CUDA start that fails
/// may be retried on Vulkan. <paramref name="Mtp"/> turns on MTP speculative decoding (2026-09-29), drafting with
/// <paramref name="DrafterPath"/> when there is one and with the weights' own head when not; toggling Embedded drafter
/// changes the launch, so it restarts the server. <paramref name="FitTargetMiB"/> (later on 2026-09-29, the user's ask:
/// <c>Embedded VRAM budget</c>) is the MiB llama.cpp's fit leaves free on each GPU, null for its own default; a change
/// restarts the server too. <paramref name="Draft"/> (2026-09-30) is the model's kind of drafting, which picks the
/// <c>--spec-type</c>: MTP, or DFlash for Muse Glimmer. <paramref name="VramOnly"/> (2026-10-01, the user's ask: Embedded VRAM
/// only) puts every layer on the GPU and has the host refuse a load that spilled into system memory
/// (<see cref="LlamaServerHost"/>, <see cref="VramSpill"/>); the service passes <c>all</c> as <paramref name="GpuLayers"/> then.
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
    bool Mtp = false,
    int? FitTargetMiB = null,
    DraftKind Draft = DraftKind.Mtp,
    bool VramOnly = false)
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
/// target's own weights. The target verifies every drafted token, so the answer is the same, only faster.
/// <c>--spec-type draft-dflash</c> instead (2026-09-30) for a DFlash drafter (Muse Glimmer's, <see cref="DraftKind.DFlash"/>),
/// in b11258's speculative types beside <c>draft-mtp</c>; always with <c>-md</c>, there being no DFlash head in any weights.</item>
/// <item><c>--fit-target &lt;MiB&gt;</c> (later on 2026-09-29, the user's ask: "a maximum VRAM budget, like 92%"): the margin
/// llama.cpp's fit — <c>--fit on</c>, the default in b11258 — leaves free on each device, 1024 MiB unless given; a budget
/// of <i>p</i> % of the biggest adapter's dedicated memory is a margin of (100 − <i>p</i>) % of it
/// (<see cref="EmbeddedVramBudget.FitTargetMiB"/>). Fit moves only what was left unset — an omitted <c>-c</c> (the context
/// shrinks first, down to <c>--fit-ctx</c>'s 4096) and <c>-ngl auto</c> — so a set context and layer count change nothing.
/// It measures free memory at the start: what other programs take later is not held back. Not passed on the CPU backend.</item>
/// <item><c>-c</c> only for a set context (2026-09-30, the user's report: context fit ran at a ninth of the speed). An
/// Embedded context size of 0 omits it: llama.cpp reads <c>-c 0</c> as a context the user set — the model's whole window
/// — so fit could not shrink it and moved layers to the CPU instead. Measured that day on an RTX 5090 with Qwen3.8 27B
/// NVFP4: <c>-c 0</c> took 262144 tokens with 51 of the layers on the GPU and ran at 12 tokens/s; no <c>-c</c> fitted
/// 113152 tokens (84992 under a 91 % budget) with every layer on the GPU at ~107–111 tokens/s, as <c>-c 32768</c> ran.</item>
/// <item>With <see cref="LlamaLaunch.VramOnly"/> (2026-10-01): <c>-ngld all</c> beside a drafter, so its layers stay on the GPU as
/// the weights' do (<c>-ngl all</c>, from the service), and <c>-lv 4</c>, the level at which b11258 logs the load lines the host
/// checks (<see cref="LlamaLoadReport"/>; about 28 more Debug lines a request). With <c>-ngl all</c> fit still shrinks an unset
/// context — Qwen3.8 27B NVFP4 that day: 262144 to 137984 tokens, all 66 layers on the GPU — and with a set one it gives up
/// ("n_gpu_layers already set by user"), so a context that does not fit fails to allocate instead of moving layers.</item>
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
                if (launch.VramOnly)
                {
                    args.Add("-ngld");
                    args.Add(EmbeddedGpuLayers.All);
                }
            }

            args.Add("--spec-type");
            args.Add(SpecType(launch.Draft));
        }

        args.AddRange(
        [
            "--alias", launch.Alias,
            "--host", "127.0.0.1",
            "--port", port.ToString(CultureInfo.InvariantCulture),
            "--api-key", apiKey,
            "--jinja",
        ]);
        if (launch.ContextSize > 0)
        {
            args.Add("-c");
            args.Add(launch.ContextSize.ToString(CultureInfo.InvariantCulture));
        }

        args.Add("-ngl");
        args.Add(launch.GpuLayers);
        if (launch.FitTargetMiB is { } fit)
        {
            args.Add("--fit-target");
            args.Add(fit.ToString(CultureInfo.InvariantCulture));
        }

        args.AddRange(
        [
            "--parallel", "1",
            "--no-webui",
            "--log-colors", "off",
            "--temp", Number(launch.Sampling.Temperature),
            "--top-p", Number(launch.Sampling.TopP),
            "--top-k", launch.Sampling.TopK.ToString(CultureInfo.InvariantCulture),
        ]);
        if (launch.VramOnly)
        {
            args.Add("-lv");
            args.Add(VramOnlyVerbosity);
        }

        return args;
    }

    /// <summary>The <c>-lv</c> a VRAM-only launch passes: trace, where the load lines are. Pinned.</summary>
    public const string VramOnlyVerbosity = "4";

    /// <summary>llama.cpp's <c>--spec-type</c> for <paramref name="draft"/>: <c>draft-dflash</c> or <c>draft-mtp</c>. Pinned.</summary>
    public static string SpecType(DraftKind draft) => draft == DraftKind.DFlash ? "draft-dflash" : "draft-mtp";

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

/// <summary>
/// The <c>Embedded context size</c> setting's range (2026-09-29): 0 to fit — the largest context the VRAM budget holds, from
/// the model's own window down to 4096 (<c>-c</c> left out, which llama.cpp's fit sizes; <c>-c 0</c> until 2026-09-30, which it
/// reads as the model's whole window; "the model's own" until later on 2026-09-29, when the user asked for the context to
/// shrink to a budget) — else 512 to 262144 tokens.
/// </summary>
public static class EmbeddedContextSize
{
    /// <summary>
    /// Fit, the user's call later on 2026-09-29 (32768 until then). A profile that saved 32768 keeps it; a new one, or one
    /// reset, fits. Pinned.
    /// </summary>
    public const int Default = 0;
    public const int Min = 512;
    public const int Max = 262_144;

    /// <summary>The settings-menu wording for a bad value. Pinned.</summary>
    public const string Error = "must be 0 (fit) or a whole number from 512 to 262144";

    public static bool IsValid(int value) => value == 0 || (value >= Min && value <= Max);

    /// <summary>What a start passes: the setting when valid, else <see cref="Default"/>.</summary>
    public static int Effective(int value) => IsValid(value) ? value : Default;
}

/// <summary>
/// The <c>Embedded VRAM budget</c> setting (later on 2026-09-29, the user's ask: "a maximum VRAM budget, like 92%, to force it
/// to stay at or under that amount"): <see cref="Off"/> — llama.cpp's own fit margin of 1 GiB per device — or a whole percent
/// from <see cref="Min"/> to <see cref="Max"/> of the biggest GPU's dedicated memory, the rest passed as <c>--fit-target</c>
/// (<see cref="LlamaArguments"/>). <see cref="Default"/> is 91 % since 2026-09-30 (the user's call; off until then): a profile
/// that saved off keeps it.
/// </summary>
public static class EmbeddedVramBudget
{
    public const int Off = 0;

    /// <summary>A new profile's budget. Pinned.</summary>
    public const int Default = 91;
    public const int Min = 50;
    public const int Max = 99;

    /// <summary>How <see cref="Off"/> reads and is typed. Pinned.</summary>
    public const string OffWord = "off";

    /// <summary>The settings-menu wording for a bad value. Pinned.</summary>
    public const string Error = "must be off or a whole percent from 50 to 99";

    public static bool IsValid(int value) => value == Off || value is >= Min and <= Max;

    /// <summary>A typed value (trimmed, any case, a trailing <c>%</c> allowed): <c>off</c> or 0 is <see cref="Off"/>, 50 to 99 itself, anything else null.</summary>
    public static int? Parse(string? text)
    {
        string value = (text ?? "").Trim();
        if (string.Equals(value, OffWord, StringComparison.OrdinalIgnoreCase))
        {
            return Off;
        }

        value = value.TrimEnd('%').TrimEnd();
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int percent) && IsValid(percent) ? percent : null;
    }

    /// <summary>What a start uses: the setting when valid, else <see cref="Off"/>.</summary>
    public static int Effective(int value) => IsValid(value) ? value : Off;

    /// <summary>The margin in MiB that leaves <paramref name="percent"/> of <paramref name="totalBytes"/> to fill: (100 − p) % of it, rounded half away from zero. Pinned.</summary>
    public static int FitTargetMiB(long totalBytes, int percent) =>
        (int)Math.Round(totalBytes / 1_048_576.0 * (100 - percent) / 100, MidpointRounding.AwayFromZero);
}
