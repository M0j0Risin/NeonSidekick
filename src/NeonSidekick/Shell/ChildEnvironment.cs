using System.Diagnostics;
using NeonSidekick.Settings;

namespace NeonSidekick.Shell;

/// <summary>
/// The environment every child gets (2026-09-21): the app's own, inherited whole — there is no
/// secret model to strip anything by, and a build or a test wants the user's PATH and variables —
/// plus <see cref="Overrides"/> on top: no colour and no pager (the output is read, not shown;
/// ANSI codes and a waiting <c>less</c> would only get in the way), UTF-8 and unbuffered Python,
/// no git credential prompt (nothing could answer it on a hidden console), a quiet dotnet. A
/// launch's own variables (<see cref="ProcessLaunch.Environment"/>: the bridge's address and token)
/// go on last. Pure over <see cref="ProcessStartInfo.Environment"/>, the child's own snapshot —
/// nothing reads the process environment here.
///
/// <para>The app's own secrets are the exception (2026-10-12, the shell review): <see cref="Withheld"/>, the API keys and the
/// Home Assistant token a user may hand the app through the environment, are taken out first, so a model's
/// <c>Get-ChildItem env:</c> or <c>env</c> never reads them. Only those: the <c>NEONSIDEKICK_TEST_*</c> variables are the
/// developer's own setup for a <c>dotnet test</c> the model or <c>/cmd</c> runs, and the rest are settings, not secrets.</para>
/// </summary>
public static class ChildEnvironment
{
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

    /// <summary>Takes out <see cref="Withheld"/>, applies <see cref="Overrides"/>, then <paramref name="launch"/>'s own variables, to <paramref name="start"/>.</summary>
    public static void Apply(ProcessStartInfo start, ProcessLaunch launch)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(launch);
        foreach (string name in Withheld)
        {
            start.Environment.Remove(name);
        }

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
