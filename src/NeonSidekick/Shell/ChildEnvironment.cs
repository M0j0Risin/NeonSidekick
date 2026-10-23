using System.Diagnostics;
using NeonSidekick.Settings;

namespace NeonSidekick.Shell;

/// <summary>
/// The environment every child gets (2026-09-21): the app's own, inherited whole — there is no
/// secret model to strip anything by, and a build or a test wants the user's PATH and variables —
/// plus <see cref="Overrides"/> on top: no colour and no pager (the output is read, not shown;
/// ANSI codes and a waiting <c>less</c> would only get in the way), UTF-8 and unbuffered Python,
/// no git credential prompt (nothing could answer it on a hidden console), a quiet dotnet. A
/// launch's own variables (<see cref="ProcessLaunch.Environment"/>: the Claude Code server's <c>MCP_TOOL_TIMEOUT</c>)
/// go on last. Pure over <see cref="ProcessStartInfo.Environment"/>, the child's own snapshot —
/// nothing reads the process environment here.
///
/// <para>The app's own secrets are the exception (2026-10-12, the shell review): <see cref="Withheld"/>, the API keys and the
/// Home Assistant token a user may hand the app through the environment, are taken out first, so a model's
/// <c>Get-ChildItem env:</c> or <c>env</c> never reads them. Since 2026-10-19 (the second shell review) also every other
/// <c>NEONSIDEKICK_*</c> variable whose name says it holds a secret (<see cref="IsWithheld"/>: a key, a token, a password, a
/// connection string), the <c>NEONSIDEKICK_TEST_*</c> ones among them — a decrypted OpenAI key, the live databases' connection
/// strings — so a <c>dotnet test</c> the model runs skips those live gates; the rest (model paths, URLs, switches) still reach it.
/// MCP stdio servers, third-party code, get the same cut (<see cref="WithheldForMcp"/>).</para>
///
/// <para>Later on 2026-10-19 (the third shell review): every <c>NEONSIDEKICK_TEST_*</c> variable, whatever its name says — the live AD
/// gate's <c>NEONSIDEKICK_TEST_AD_DOMAIN</c> carries a <c>Password=</c> under a name with none of the words — so a model's <c>dotnet test</c>
/// skips every live gate. And the children that are not the shell's get the cut too (<see cref="Withhold"/>, <see cref="WithoutSecrets"/>):
/// <c>llama-server</c> and its guard, and the headless browser. A shell child also gets <c>NoDefaultCurrentDirectoryInExePath</c>
/// (<see cref="Overrides"/>), so <c>cmd</c> — and every <c>CreateProcess</c> under it — never runs a bare name from the working folder: a
/// <c>git.cmd</c> the model wrote there ran in place of git under an allowed <c>git status</c>.</para>
/// </summary>
public static class ChildEnvironment
{
    /// <summary>The words that make a <c>NEONSIDEKICK_*</c> variable a secret by its name. Pinned.</summary>
    public static readonly IReadOnlyList<string> SecretWords = ["KEY", "TOKEN", "SECRET", "PASSWORD", "CONNECTION", "CREDENTIAL"];

    /// <summary>The prefix of the test suite's variables, every one withheld (later on 2026-10-19). Pinned.</summary>
    public const string TestPrefix = EnvironmentOverrides.Prefix + "TEST_";

    /// <summary>Whether <paramref name="name"/> is never handed to a child: one of <see cref="Withheld"/>, any <see cref="TestPrefix"/> name, or a <c>NEONSIDEKICK_*</c> name holding one of <see cref="SecretWords"/>.</summary>
    public static bool IsWithheld(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Withheld.Contains(name, StringComparer.OrdinalIgnoreCase)
            || name.StartsWith(TestPrefix, StringComparison.OrdinalIgnoreCase)
            || (name.StartsWith(EnvironmentOverrides.Prefix, StringComparison.OrdinalIgnoreCase)
                && SecretWords.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Takes every <see cref="IsWithheld"/> name out of <paramref name="environment"/> (a <see cref="ProcessStartInfo.Environment"/>): what a child that is not the shell's gets, with nothing set on top.</summary>
    public static void Withhold(IDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        foreach (string name in environment.Keys.Where(IsWithheld).ToList())
        {
            environment.Remove(name);
        }
    }

    /// <summary>The app's environment as a child would inherit it, less every <see cref="IsWithheld"/> name: a snapshot, for a start that writes its own block (<see cref="EmbeddedLlm.ChildJob"/>).</summary>
    public static IDictionary<string, string?> WithoutSecrets()
    {
        var environment = new ProcessStartInfo().Environment;
        Withhold(environment);
        return environment;
    }

    /// <summary>
    /// The variables an MCP stdio server is started without, as the SDK's <c>EnvironmentVariables</c> takes a removal (a null value):
    /// every <see cref="IsWithheld"/> name in the app's environment, read from a <see cref="ProcessStartInfo"/>'s snapshot. A server's own
    /// <c>env</c> entries go on top.
    /// </summary>
    public static Dictionary<string, string?> WithheldForMcp()
    {
        var removals = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (string name in new ProcessStartInfo().Environment.Keys)
        {
            if (IsWithheld(name))
            {
                removals[name] = null;
            }
        }

        return removals;
    }

    /// <summary>The variables set on every child, in the order they are applied. Pinned.</summary>
    public static readonly IReadOnlyList<KeyValuePair<string, string>> Overrides =
    [
        new("NO_COLOR", "1"),
        new("FORCE_COLOR", "0"),
        new("CLICOLOR", "0"),
        new("TERM", "dumb"),
        new("PYTHONIOENCODING", "utf-8"),
        new("PYTHONUTF8", "1"),
        new("PYTHONUNBUFFERED", "1"),
        new("GIT_TERMINAL_PROMPT", "0"),
        new("GIT_PAGER", "cat"),
        new("PAGER", "cat"),
        new("DOTNET_NOLOGO", "1"),
        new("DOTNET_CLI_TELEMETRY_OPTOUT", "1"),
        new("NoDefaultCurrentDirectoryInExePath", "1"),
    ];

    /// <summary>The app's secret variables, never handed to a child. Pinned.</summary>
    public static readonly IReadOnlyList<string> Withheld =
    [
        EnvironmentOverrides.LlmApiKeyVariable,
        EnvironmentOverrides.AnthropicApiKeyVariable,
        EnvironmentOverrides.OpenAIApiKeyVariable,
        EnvironmentOverrides.HomeAssistantTokenVariable,
        EnvironmentOverrides.YouTubeApiKeyVariable,
    ];

    /// <summary>Takes out every <see cref="IsWithheld"/> name, applies <see cref="Overrides"/>, then <paramref name="launch"/>'s own variables, to <paramref name="start"/>.</summary>
    public static void Apply(ProcessStartInfo start, ProcessLaunch launch)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(launch);
        Withhold(start.Environment);

        foreach (var (name, value) in Overrides)
        {
            start.Environment[name] = value;
        }

        if (launch.Environment is { } own)
        {
            foreach (var (name, value) in own)
            {
                start.Environment[name] = value;
            }
        }
    }
}
