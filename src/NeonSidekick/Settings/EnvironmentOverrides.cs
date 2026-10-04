using System.Globalization;
using NeonSidekick.Diagnostics;
using NeonSidekick.Llm;

namespace NeonSidekick.Settings;

/// <summary>
/// The environment variables NeonSidekick honours, read through one injected reader.
///
/// <para><b>A variable always outranks a saved setting</b> — it was set by whoever launched the
/// process. The rule is expressed structurally: <see cref="ApplyTo"/> layers the variables over a
/// saved snapshot, and callers use the result, so no code path can consult the file "first".
/// Nothing else in the process calls <c>Environment.GetEnvironmentVariable</c>; tests hand this
/// class a dictionary lookup and never mutate process state.</para>
///
/// <para>Numeric values are parsed with <see cref="CultureInfo.InvariantCulture"/> (the build is
/// <c>InvariantGlobalization</c>). A value that fails to parse is logged and ignored, never
/// thrown and never clamped: a bad variable should cost the override, not the launch.</para>
/// </summary>
public sealed class EnvironmentOverrides
{
    public const string Prefix = "NEONSIDEKICK_";
    public const string HomeVariable = Prefix + "HOME";
    public const string LlmUrlVariable = Prefix + "LLM_URL";
    public const string LlmModelVariable = Prefix + "LLM_MODEL";
    public const string LlmApiKeyVariable = Prefix + "LLM_API_KEY";
    public const string RequestTimeoutVariable = Prefix + "LLM_REQUEST_TIMEOUT";
    public const string TurnTimeoutVariable = Prefix + "LLM_TURN_TIMEOUT";
    public const string TtsUrlVariable = Prefix + "TTS_URL";
    public const string TtsVoiceVariable = Prefix + "TTS_VOICE";
    public const string TtsSpeedVariable = Prefix + "TTS_SPEED";
    public const string WhisperModelVariable = Prefix + "WHISPER_MODEL";
    public const string LlmReasoningVariable = Prefix + "LLM_REASONING";
    public const string TtsVoice2Variable = Prefix + "TTS_VOICE2";
    public const string TtsMixVariable = Prefix + "TTS_MIX";
    public const string InterruptEchoVariable = Prefix + "INTERRUPT_ECHO";
    public const string InterruptConfirmVariable = Prefix + "INTERRUPT_CONFIRM";
    public const string LlmContextVariable = Prefix + "LLM_CONTEXT";
    public const string SearxngUrlVariable = Prefix + "SEARXNG_URL";
    public const string CommandPolicyVariable = Prefix + "COMMAND_POLICY";
    public const string ObsidianVaultVariable = Prefix + "OBSIDIAN_VAULT";
    public const string ComfyUrlVariable = Prefix + "COMFY_URL";
    public const string ProfileVariable = Prefix + "PROFILE";
    public const string ShellPoliceVariable = Prefix + "SHELL_POLICE";
    public const string ShellNativeVariable = Prefix + "SHELL_NATIVE";
    public const string ClaudeCliExeVariable = Prefix + "CLAUDE_CLI_EXE";
    public const string ClaudeCliPermissionsVariable = Prefix + "CLAUDE_CLI_PERMISSIONS";
    public const string ClaudeCliAdvisorVariable = Prefix + "CLAUDE_CLI_ADVISOR";
    public const string AnthropicApiVariable = Prefix + "ANTHROPIC_API";
    public const string AnthropicApiKeyVariable = Prefix + "ANTHROPIC_API_KEY";
    public const string ClaudeCliServerVariable = Prefix + "CLAUDE_CLI_SERVER";
    public const string OpenAIApiVariable = Prefix + "OPENAI_API";
    public const string OpenAIApiKeyVariable = Prefix + "OPENAI_API_KEY";
    public const string LlmSamplingVariable = Prefix + "LLM_SAMPLING";
    public const string HomeAssistantUrlVariable = Prefix + "HA_URL";
    public const string HomeAssistantTokenVariable = Prefix + "HA_TOKEN";
    public const string EmbeddedBackendVariable = Prefix + "EMBEDDED_BACKEND";
    public const string EmbeddedContextVariable = Prefix + "EMBEDDED_CONTEXT";
    public const string DockerPipeVariable = Prefix + "DOCKER_PIPE";

    /// <summary>Every variable this class reads, for documentation.</summary>
    public static readonly string[] AllVariables =
    {
        HomeVariable, ProfileVariable, LlmUrlVariable, LlmModelVariable, LlmApiKeyVariable,
        RequestTimeoutVariable, TurnTimeoutVariable, TtsUrlVariable, TtsVoiceVariable, TtsSpeedVariable,
        WhisperModelVariable, LlmReasoningVariable, TtsVoice2Variable, TtsMixVariable,
        InterruptEchoVariable, InterruptConfirmVariable, LlmContextVariable, SearxngUrlVariable,
        CommandPolicyVariable, ShellPoliceVariable, ObsidianVaultVariable, ComfyUrlVariable,
        ShellNativeVariable, ClaudeCliExeVariable, ClaudeCliPermissionsVariable, ClaudeCliAdvisorVariable,
        AnthropicApiVariable, AnthropicApiKeyVariable, LlmSamplingVariable, HomeAssistantUrlVariable,
        HomeAssistantTokenVariable, EmbeddedBackendVariable, EmbeddedContextVariable, ClaudeCliServerVariable,
        DockerPipeVariable, OpenAIApiVariable, OpenAIApiKeyVariable,
    };

    /// <summary>The log category of every environment line.</summary>
    public const string Category = "Environment";
    private readonly Func<string, string?> _read;

    /// <param name="read">Typically <c>Environment.GetEnvironmentVariable</c>; a dictionary lookup in tests.</param>
    public EnvironmentOverrides(Func<string, string?> read)
    {
        _read = read ?? throw new ArgumentNullException(nameof(read));
    }

    /// <summary>An instance that sees no variables at all.</summary>
    public static EnvironmentOverrides Empty { get; } = new(_ => null);

    /// <summary>Settings directory override, or null.</summary>
    public string? Home => Read(HomeVariable);

    /// <summary>
    /// The profile this launch loads, or null (2026-09-26): like <see cref="Home"/> it picks a file, not a value, so it
    /// is not in <see cref="ApplyTo"/>; <c>--profile</c> outranks it and <c>Program.cs</c> hands the winner to
    /// <see cref="AppSettings"/>. Checked there (an unknown name ends the launch), not here.
    /// </summary>
    public string? Profile => Read(ProfileVariable);

    public string? LlmUrl => Read(LlmUrlVariable);
    public string? LlmModel => Read(LlmModelVariable);
    public string? LlmApiKey => Read(LlmApiKeyVariable);
    public string? TtsHttpUrl => Read(TtsUrlVariable);
    public string? TtsVoice => Read(TtsVoiceVariable);

    /// <summary>SearXNG instance URL override, or null. The URL alone: the setting <c>Browser search method</c> still picks the engine. Validated where it is used (a non-URL under <c>searxng</c> falls back to DuckDuckGo), not here.</summary>
    public string? WebSearxngUrl => Read(SearxngUrlVariable);

    /// <summary>The Obsidian vault's folder, or null (2026-09-22). Checked where it is used (a folder without <c>.obsidian</c> offers no vault tool), not here.</summary>
    public string? ObsidianVault => Read(ObsidianVaultVariable);

    /// <summary>The ComfyUI server's URL, or null (2026-09-24). Checked where it is used (a non-http(s) value offers no image tool), not here.</summary>
    public string? ComfyUrl => Read(ComfyUrlVariable);

    /// <summary>The Claude Code CLI's path for <c>/claude</c>, or null (2026-09-27). Checked where it is used (a path that is no file is <c>/claude</c>'s error), not here.</summary>
    public string? ClaudeCliExecutable => Read(ClaudeCliExeVariable);

    /// <summary><c>Claude CLI slash command permissions</c> for this launch as one of <see cref="Claude.ClaudePermission.Names"/> (lowercased), or null when unset or not a level (2026-09-27: a scripted headless run says <c>edit</c> without saving it).</summary>
    public string? ClaudeCliPermissions => ReadClaudePermissions(ClaudeCliPermissionsVariable);

    /// <summary>Whisper model name or path, or null. Validated where it is used, not here.</summary>
    public string? SttWhisperModel => Read(WhisperModelVariable);

    /// <summary>Request-timeout override in seconds, or null when unset or unparseable.</summary>
    public double? LlmRequestTimeoutSeconds => ReadSeconds(RequestTimeoutVariable, Llm.LlmTimeouts.MaxRequestSeconds);

    /// <summary>Turn-timeout override in seconds, or null when unset or unparseable.</summary>
    public double? LlmTurnTimeoutSeconds => ReadSeconds(TurnTimeoutVariable, Llm.LlmTimeouts.MaxTurnSeconds);

    /// <summary>Speech speed override, or null when unset or outside <see cref="AppSettingsData.MinTtsSpeed"/>–<see cref="AppSettingsData.MaxTtsSpeed"/>.</summary>
    public double? TtsSpeed => ReadSpeed(TtsSpeedVariable);

    /// <summary>Reasoning-effort override as one of <see cref="ReasoningLevel.Levels"/> (lowercased), or null when unset or not a level.</summary>
    public string? LlmReasoning => ReadLevel(LlmReasoningVariable);

    /// <summary>
    /// Secondary-voice override, or null when unset. A blank variable is "unset", so a variable
    /// cannot clear a saved secondary voice; <see cref="TtsMixVariable"/>=100 sends the primary alone.
    /// </summary>
    public string? TtsVoice2 => Read(TtsVoice2Variable);

    /// <summary>Voice-mix override (the primary voice's percent), or null when unset or outside <see cref="AppSettingsData.MinTtsVoiceMix"/>–<see cref="AppSettingsData.MaxTtsVoiceMix"/>.</summary>
    public int? TtsVoiceMix => ReadMix(TtsMixVariable);

    /// <summary>Interrupt echo-guard override in percent, or null when unset or outside <see cref="AppSettingsData.MinSttInterruptEchoGuard"/>–<see cref="AppSettingsData.MaxSttInterruptEchoGuard"/>.</summary>
    public int? SttInterruptEchoGuard => ReadPercent(InterruptEchoVariable, AppSettingsData.MinSttInterruptEchoGuard, AppSettingsData.MaxSttInterruptEchoGuard);

    /// <summary>Interrupt confirm-window override in milliseconds, or null when unset or outside <see cref="AppSettingsData.MinSttInterruptConfirmMs"/>–<see cref="AppSettingsData.MaxSttInterruptConfirmMs"/>.</summary>
    public int? SttInterruptConfirmMs => ReadMilliseconds(InterruptConfirmVariable, AppSettingsData.MinSttInterruptConfirmMs, AppSettingsData.MaxSttInterruptConfirmMs);

    /// <summary>Context-window override in tokens, or null when unset or not a positive whole number.</summary>
    public int? LlmContextLength => ReadTokens(LlmContextVariable);

    /// <summary>Shell command policy override as one of <see cref="Shell.CommandPolicy.Names"/> (lowercased), or null when unset or not a mode (2026-09-21: the way a scripted headless run says <c>yolo</c>).</summary>
    public string? ShellCommandPolicy => ReadCommandPolicy(CommandPolicyVariable);

    /// <summary>
    /// The path police for this launch (<c>Shell police outside paths</c>), or null when unset or not a switch word
    /// (2026-09-26, the user's ask: the police could only be turned off by saving the profile, which every later
    /// launch then inherited). Either way: <c>on</c> can bring it back over a saved <c>off</c>. <c>--no-police</c>
    /// outranks it. Separate from <see cref="ShellCommandPolicy"/> on purpose: <c>yolo</c> never implies it.
    /// </summary>
    public bool? ShellPolice => ReadSwitch(ShellPoliceVariable);

    /// <summary>
    /// <c>Shell prefer native tools</c> for this launch, or null when unset or not a switch word (2026-09-26, the
    /// police's shape): <c>off</c> lets a run's scripted <c>cat</c> or <c>git status</c> go to the shell as written.
    /// </summary>
    public bool? ShellNative => ReadSwitch(ShellNativeVariable);

    /// <summary><c>Claude CLI advisor tool</c> for this launch, or null when unset or not a switch word (2026-09-27): a scripted headless run offers <c>claude_advisor_cli</c> without saving it.</summary>
    public bool? ClaudeCliAdvisor => ReadSwitch(ClaudeCliAdvisorVariable);

    /// <summary><c>Anthropic API</c> for this launch, or null when unset or not a switch word (2026-09-27): a scripted run offers the Anthropic API without saving it.</summary>
    public bool? AnthropicApi => ReadSwitch(AnthropicApiVariable);

    /// <summary>The Anthropic API key for this launch, plain (2026-09-27); never logged (<see cref="Describe"/> shows <see cref="SecretSet"/>).</summary>
    public string? AnthropicApiKey => Read(AnthropicApiKeyVariable);

    /// <summary><c>Claude CLI server</c> for this launch, or null when unset or not a switch word (2026-09-30): a scripted run offers the Claude CLI without saving it.</summary>
    public bool? ClaudeCliServer => ReadSwitch(ClaudeCliServerVariable);

    /// <summary><c>OpenAI API</c> for this launch, or null when unset or not a switch word (2026-10-03): a scripted run offers the OpenAI API without saving it.</summary>
    public bool? OpenAIApi => ReadSwitch(OpenAIApiVariable);

    /// <summary>The OpenAI API key for this launch, plain (2026-10-03); never logged (<see cref="Describe"/> shows <see cref="SecretSet"/>).</summary>
    public string? OpenAIApiKey => Read(OpenAIApiKeyVariable);

    /// <summary>The Home Assistant server's URL, or null (2026-09-28). Checked where it is used (a non-http(s) value offers no Home Assistant tool), not here.</summary>
    public string? HomeAssistantUrl => Read(HomeAssistantUrlVariable);

    /// <summary>The Home Assistant long-lived access token for this launch, plain (2026-09-28); never logged (<see cref="Describe"/> shows <see cref="SecretSet"/>).</summary>
    public string? HomeAssistantToken => Read(HomeAssistantTokenVariable);

    /// <summary>The Docker engine's pipe for this launch, or null (2026-10-02, <c>Docker engine pipe</c>): any form <see cref="Docker.DockerPipe.Normalize"/> reads, so a CI box's <c>npipe:////./pipe/…</c> from <c>DOCKER_HOST</c> can be passed as it is.</summary>
    public string? DockerPipe => Read(DockerPipeVariable);

    /// <summary>
    /// Sampling for this launch, or null when unset or refused (2026-09-28, the setting <c>LLM sampling</c>): a JSON object
    /// in wire names, <c>{"temperature":0.6,"top_k":20,"typical_p":0.9}</c> — the named fields in their ranges, any other
    /// key the extra body (<see cref="Llm.LlmSampling.TryParseEntry"/>). <see cref="ApplyTo"/> lays it over every model's
    /// entry and <c>*</c>, so it wins for whatever model is connected; a field it does not name keeps the saved value.
    /// </summary>
    public LlmSamplingEntry? LlmSampling => ReadSampling(LlmSamplingVariable);

    /// <summary>
    /// <c>Embedded backend</c> for this launch, or null when unset or not one of <see cref="EmbeddedLlm.EmbeddedBackends.Names"/>
    /// (2026-09-29, warned): a scripted run forces the CPU or Vulkan build without saving it.
    /// </summary>
    public string? EmbeddedBackend
    {
        get
        {
            var raw = Read(EmbeddedBackendVariable);
            if (raw is null)
            {
                return null;
            }

            if (EmbeddedLlm.EmbeddedBackends.IsValid(raw))
            {
                return raw.Trim().ToLowerInvariant();
            }

            DiagnosticLog.Warn(Category, $"{EmbeddedBackendVariable}='{raw}' {EmbeddedLlm.EmbeddedBackends.Error}; ignoring it.");
            return null;
        }
    }

    /// <summary><c>Embedded context size</c> for this launch, or null when unset or out of range (2026-09-29, warned): 0 or 512 to 262144 tokens.</summary>
    public int? EmbeddedContextSize
    {
        get
        {
            var raw = Read(EmbeddedContextVariable);
            if (raw is null)
            {
                return null;
            }

            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tokens) && EmbeddedLlm.EmbeddedContextSize.IsValid(tokens))
            {
                return tokens;
            }

            DiagnosticLog.Warn(Category, $"{EmbeddedContextVariable}='{raw}' {EmbeddedLlm.EmbeddedContextSize.Error}; ignoring it.");
            return null;
        }
    }

    /// <summary>
    /// A variable that is not an override: <c>PATH</c>, <c>PATHEXT</c>, <c>ProgramFiles</c> — what the
    /// shell probe (<see cref="Shell.Interpreters"/>) walks (2026-09-21). The one door stays this class's:
    /// nothing else calls <c>Environment.GetEnvironmentVariable</c>, and tests hand a dictionary here too.
    /// Null for unset or blank.
    /// </summary>
    public string? System(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Read(name);
    }

    /// <summary>The names of the variables that are currently set to a usable value.</summary>
    public IReadOnlyList<string> ActiveVariables()
    {
        var active = new List<string>();
        foreach (var name in AllVariables)
        {
            bool set = name switch
            {
                RequestTimeoutVariable => LlmRequestTimeoutSeconds is not null,
                TurnTimeoutVariable => LlmTurnTimeoutSeconds is not null,
                TtsSpeedVariable => ReadSpeed(name) is not null,
                LlmReasoningVariable => ReadLevel(name) is not null,
                TtsMixVariable => ReadMix(name) is not null,
                InterruptEchoVariable => SttInterruptEchoGuard is not null,
                InterruptConfirmVariable => SttInterruptConfirmMs is not null,
                LlmContextVariable => LlmContextLength is not null,
                CommandPolicyVariable => ShellCommandPolicy is not null,
                ShellPoliceVariable => ShellPolice is not null,
                ShellNativeVariable => ShellNative is not null,
                ClaudeCliAdvisorVariable => ClaudeCliAdvisor is not null,
                AnthropicApiVariable => AnthropicApi is not null,
                ClaudeCliServerVariable => ClaudeCliServer is not null,
                OpenAIApiVariable => OpenAIApi is not null,
                LlmSamplingVariable => LlmSampling is not null,
                EmbeddedBackendVariable => EmbeddedBackend is not null,
                EmbeddedContextVariable => EmbeddedContextSize is not null,
                _ => Read(name) is not null,
            };
            if (set)
            {
                active.Add(name);
            }
        }

        return active;
    }

    /// <summary>What stands in for the API key's value in <see cref="Describe"/>.</summary>
    public const string SecretSet = "(set)";

    /// <summary>
    /// The variables in force with their raw values, <c>NAME=value</c> each, for the log at
    /// startup: <c>NEONSIDEKICK_LLM_URL=http://…, NEONSIDEKICK_LLM_API_KEY=(set)</c> — the key's
    /// value never shown. Null when none is set.
    /// </summary>
    public string? Describe()
    {
        var active = ActiveVariables();
        if (active.Count == 0)
        {
            return null;
        }

        var parts = new List<string>(active.Count);
        foreach (var name in active)
        {
            parts.Add(name + "=" + (name is LlmApiKeyVariable or AnthropicApiKeyVariable or OpenAIApiKeyVariable or HomeAssistantTokenVariable ? SecretSet : Read(name)));
        }

        return string.Join(", ", parts);
    }

    /// <summary>
    /// Returns a copy of <paramref name="saved"/> with every set variable applied on top. The
    /// input is not mutated, so the saved snapshot stays what the file says and the effective
    /// snapshot stays what the process runs.
    /// </summary>
    public AppSettingsData ApplyTo(AppSettingsData saved)
    {
        ArgumentNullException.ThrowIfNull(saved);
        var effective = AppSettings.Copy(saved);

        if (LlmUrl is { } llmUrl) effective.LlmUrl = llmUrl;
        if (LlmModel is { } llmModel) effective.LlmModel = llmModel;
        if (LlmApiKey is { } apiKey) effective.LlmApiKey = apiKey;
        if (LlmRequestTimeoutSeconds is { } request) effective.LlmRequestTimeoutSeconds = request;
        if (LlmTurnTimeoutSeconds is { } turn) effective.LlmTurnTimeoutSeconds = turn;
        if (TtsHttpUrl is { } ttsUrl) effective.TtsHttpUrl = ttsUrl;
        if (TtsVoice is { } voice) effective.TtsVoice = voice;
        if (TtsSpeed is { } speed) effective.TtsSpeed = speed;
        if (SttWhisperModel is { } whisper) effective.SttWhisperModel = whisper;
        if (LlmReasoning is { } reasoning) effective.LlmReasoning = reasoning;
        if (TtsVoice2 is { } voice2) effective.TtsVoice2 = voice2;
        if (TtsVoiceMix is { } mix) effective.TtsVoiceMix = mix;
        if (SttInterruptEchoGuard is { } echo) effective.SttInterruptEchoGuard = echo;
        if (SttInterruptConfirmMs is { } confirm) effective.SttInterruptConfirmMs = confirm;
        if (LlmContextLength is { } context) effective.LlmContextLength = context;
        if (WebSearxngUrl is { } searxng) effective.WebSearxngUrl = searxng;
        if (ShellCommandPolicy is { } policy) effective.ShellCommandPolicy = policy;
        if (ShellPolice is { } police) effective.ShellPoliceOutsidePaths = police;
        if (ShellNative is { } native) effective.ShellPreferNative = native;
        if (ObsidianVault is { } vault) effective.ObsidianVault = vault;
        if (ComfyUrl is { } comfy) effective.ComfyUrl = comfy;
        if (ClaudeCliExecutable is { } claude) effective.ClaudeCliExecutable = claude;
        if (ClaudeCliPermissions is { } claudePermissions) effective.ClaudeCliPermissions = claudePermissions;
        if (ClaudeCliAdvisor is { } advisor) effective.ClaudeCliAdvisor = advisor;
        if (AnthropicApi is { } claudeApi) effective.AnthropicApi = claudeApi;
        if (AnthropicApiKey is { } claudeApiKey) effective.AnthropicApiKey = claudeApiKey;
        if (ClaudeCliServer is { } claudeCliServer) effective.ClaudeCliServer = claudeCliServer;
        if (OpenAIApi is { } openAIApi) effective.OpenAIApi = openAIApi;
        if (OpenAIApiKey is { } openAIApiKey) effective.OpenAIApiKey = openAIApiKey;
        if (HomeAssistantUrl is { } haUrl) effective.HomeAssistantUrl = haUrl;
        if (HomeAssistantToken is { } haToken) effective.HomeAssistantToken = haToken;
        if (DockerPipe is { } dockerPipe) effective.DockerEnginePipe = dockerPipe;
        if (LlmSampling is { } sampling) effective.LlmSampling = Overlay(effective.LlmSampling, sampling);
        if (EmbeddedBackend is { } embeddedBackend) effective.EmbeddedBackend = embeddedBackend;
        if (EmbeddedContextSize is { } embeddedContext) effective.EmbeddedContextSize = embeddedContext;

        return effective;
    }

    /// <summary>
    /// <paramref name="saved"/> (already the effective copy's own) with <paramref name="over"/>'s fields set on every
    /// entry and on <see cref="Llm.LlmSampling.AnyModel"/>, made if absent; its extra fields join each entry's, winning a key.
    /// </summary>
    private static Dictionary<string, LlmSamplingEntry> Overlay(Dictionary<string, LlmSamplingEntry>? saved, LlmSamplingEntry over)
    {
        var map = saved ?? new Dictionary<string, LlmSamplingEntry>(StringComparer.Ordinal);
        if (Llm.LlmSampling.KeyFor(map, Llm.LlmSampling.AnyModel) is null)
        {
            map[Llm.LlmSampling.AnyModel] = new LlmSamplingEntry();
        }

        foreach (var entry in map.Values)
        {
            foreach (var field in SamplingField.All)
            {
                if (field.Get(over) is { } value)
                {
                    field.Set(entry, value);
                }
            }

            if (over.Extra is { Count: > 0 } extra)
            {
                entry.Extra ??= new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal);
                foreach (var (name, value) in extra)
                {
                    entry.Extra[name] = value;
                }
            }
        }

        return map;
    }

    private LlmSamplingEntry? ReadSampling(string name)
    {
        var raw = Read(name);
        if (raw is null)
        {
            return null;
        }

        if (!Llm.LlmSampling.TryParseEntry(raw, out var entry, out var problem))
        {
            DiagnosticLog.Warn(Category, $"{name}: {problem}; ignoring it.");
            return null;
        }

        return entry;
    }

    private string? ReadClaudePermissions(string name)
    {
        var raw = Read(name);
        if (raw is null)
        {
            return null;
        }

        if (!Claude.ClaudePermission.TryParse(raw, out var level))
        {
            DiagnosticLog.Warn(Category, $"{name}='{raw}' is not one of {string.Join(", ", Claude.ClaudePermission.Names)}; ignoring it.");
            return null;
        }

        return Claude.ClaudePermission.Name(level);
    }

    private string? ReadCommandPolicy(string name)
    {
        var raw = Read(name);
        if (raw is null)
        {
            return null;
        }

        if (!Shell.CommandPolicy.TryParse(raw, out var mode))
        {
            DiagnosticLog.Warn(Category, $"{name}='{raw}' is not one of {string.Join(", ", Shell.CommandPolicy.Names)}; ignoring it.");
            return null;
        }

        return Shell.CommandPolicy.Name(mode);
    }

    private static readonly string[] OnWords = ["on", "true", "1", "yes"];
    private static readonly string[] OffWords = ["off", "false", "0", "no"];

    private bool? ReadSwitch(string name)
    {
        var raw = Read(name);
        if (raw is null)
        {
            return null;
        }

        if (OnWords.Contains(raw, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        if (OffWords.Contains(raw, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        DiagnosticLog.Warn(Category, $"{name}='{raw}' is not on or off; ignoring it.");
        return null;
    }

    private int? ReadTokens(string name)
    {
        var raw = Read(name);
        if (raw is null)
        {
            return null;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tokens) || tokens <= 0)
        {
            DiagnosticLog.Warn(Category, $"{name}='{raw}' is not a positive whole number of tokens; ignoring it.");
            return null;
        }

        return tokens;
    }

    private int? ReadMix(string name) => ReadPercent(name, AppSettingsData.MinTtsVoiceMix, AppSettingsData.MaxTtsVoiceMix);

    private int? ReadPercent(string name, int min, int max)
    {
        var raw = Read(name);
        if (raw is null)
        {
            return null;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var percent)
            || percent < min || percent > max)
        {
            DiagnosticLog.Warn(Category, $"{name}='{raw}' is not a whole-number percent in [{min.ToString(CultureInfo.InvariantCulture)}, {max.ToString(CultureInfo.InvariantCulture)}]; ignoring it.");
            return null;
        }

        return percent;
    }

    private int? ReadMilliseconds(string name, int min, int max)
    {
        var raw = Read(name);
        if (raw is null)
        {
            return null;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var milliseconds)
            || milliseconds < min || milliseconds > max)
        {
            DiagnosticLog.Warn(Category, $"{name}='{raw}' is not a whole number of milliseconds in [{min.ToString(CultureInfo.InvariantCulture)}, {max.ToString(CultureInfo.InvariantCulture)}]; ignoring it.");
            return null;
        }

        return milliseconds;
    }

    private string? ReadLevel(string name)
    {
        var raw = Read(name);
        if (raw is null)
        {
            return null;
        }

        if (!ReasoningLevel.TryParse(raw, out var effort))
        {
            DiagnosticLog.Warn(Category, $"{name}='{raw}' is not one of {string.Join(", ", ReasoningLevel.Levels)}; ignoring it.");
            return null;
        }

        return ReasoningLevel.Name(effort);
    }

    private double? ReadSpeed(string name)
    {
        var raw = Read(name);
        if (raw is null)
        {
            return null;
        }

        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed)
            || double.IsNaN(speed) || speed < AppSettingsData.MinTtsSpeed || speed > AppSettingsData.MaxTtsSpeed)
        {
            DiagnosticLog.Warn(Category, $"{name}='{raw}' is not a speed multiplier in [{AppSettingsData.MinTtsSpeed.ToString(CultureInfo.InvariantCulture)}, {AppSettingsData.MaxTtsSpeed.ToString(CultureInfo.InvariantCulture)}]; ignoring it.");
            return null;
        }

        return speed;
    }

    private string? Read(string name)
    {
        var value = _read(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }

    private double? ReadSeconds(string name, double max)
    {
        var raw = Read(name);
        if (raw is null)
        {
            return null;
        }

        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            || double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds <= 0 || seconds > max)
        {
            DiagnosticLog.Warn(Category, $"{name}='{raw}' is not a number of seconds in (0, {max.ToString(CultureInfo.InvariantCulture)}]; ignoring it.");
            return null;
        }

        return seconds;
    }
}
