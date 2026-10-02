using System.Globalization;
using System.Text.Json;
using NeonSidekick.Docker;
using NeonSidekick.Llm.Tools;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// <c>--docker-check</c> (2026-10-02, the Docker tools): their proof on the published binary against the real engine on the
/// pipe the settings name, nothing changed: the version agreed, the containers listed, then on the first running one an
/// inspect whose summary must hold none of the container's environment values, its last log lines and one stats sample.
/// Exit 0 only when every line passes. Not part of the build gate: it needs Docker Desktop running.
/// </summary>
internal static class DockerCheck
{
    public static string IntroLine(string pipe) => $"Docker check: the engine on {pipe} — nothing is changed.";

    public static async Task<int> RunAsync(IAnsiConsole console, DockerSession docker, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(docker);
        var client = docker.Client();
        console.MarkupLine(Markup.Escape(IntroLine(client.Display)));
        var checks = new List<SmokeCheck>();

        var status = await DockerCommand.StatusAsync(docker, cancellationToken).ConfigureAwait(false);
        checks.Add(new SmokeCheck("docker:version", !status.Failed, string.Join(" · ", status.Lines)));
        if (status.Failed)
        {
            return Report(console, checks);
        }

        var (containers, error) = await docker.ContainersAsync(cancellationToken).ConfigureAwait(false);
        checks.Add(new SmokeCheck("docker:containers", containers is not null, containers is null ? error! : DockerText.ContainersHeader(containers, "")));
        if (containers?.FirstOrDefault(c => c.Running) is not { } running)
        {
            if (containers is not null)
            {
                checks.Add(new SmokeCheck("docker:inspect", true, "skipped: no container is running"));
            }

            return Report(console, checks);
        }

        var inspect = await client.GetAsync("/containers/" + Uri.EscapeDataString(running.Id) + "/json", DockerSession.ReadTimeout, cancellationToken).ConfigureAwait(false);
        if (!inspect.Ok || DockerInspect.Summary(inspect.Body) is not { } summary)
        {
            checks.Add(new SmokeCheck("docker:inspect", false, inspect.Error ?? DockerText.BadAnswer("/containers/" + running.Name + "/json")));
        }
        else
        {
            var leaked = EnvValues(inspect.Body).Where(v => v.Length >= 4 && summary.Contains(v, StringComparison.Ordinal)).ToList();
            checks.Add(new SmokeCheck("docker:inspect", leaked.Count == 0, leaked.Count == 0
                ? $"{running.Name}: {summary.Split('\n').Length.ToString(CultureInfo.InvariantCulture)} summary lines, every environment value redacted"
                : $"{running.Name}: {leaked.Count.ToString(CultureInfo.InvariantCulture)} environment values appear in the summary"));
        }

        var logs = await client.LogsAsync(running.Id, 20, null, timestamps: false, DockerLogStreams.Both, DockerSession.ReadTimeout, cancellationToken).ConfigureAwait(false);
        checks.Add(new SmokeCheck("docker:logs", logs.Error is null, logs.Error ?? $"{running.Name}: {logs.Lines.Count.ToString(CultureInfo.InvariantCulture)} of the last 20 lines read"));

        var stats = await DockerStatsTool.SampleAsync(docker, [running], cancellationToken).ConfigureAwait(false);
        bool sampled = !stats[0].Contains("Error:", StringComparison.Ordinal);
        checks.Add(new SmokeCheck("docker:stats", sampled, stats[0]));
        return Report(console, checks);
    }

    /// <summary>The values of an inspect answer's <c>Config.Env</c>: what the summary must not show.</summary>
    private static IReadOnlyList<string> EnvValues(string json)
    {
        using var doc = DockerJson.TryParse(json);
        if (doc is null || !doc.RootElement.TryGetProperty("Config", out var config) || config.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        return DockerJson.Strings(config, "Env").Select(e => e.IndexOf('=', StringComparison.Ordinal) is >= 0 and var at ? e[(at + 1)..] : "").Where(v => v.Length > 0).ToList();
    }

    private static int Report(IAnsiConsole console, IReadOnlyList<SmokeCheck> checks)
    {
        int failed = 0;
        foreach (var check in checks)
        {
            failed += check.Passed ? 0 : 1;
            string verdict = check.Passed ? Theme.ColorMarkup(Theme.Good, "PASS") : Theme.ColorMarkup(Theme.Bad, "FAIL");
            console.MarkupLine($"  {verdict}  {Markup.Escape(check.Name)}  {Theme.DimMarkup(check.Detail)}");
        }

        console.WriteLine();
        console.MarkupLine(failed == 0
            ? Theme.ColorMarkup(Theme.Good, $"DOCKER CHECK PASS  {checks.Count.ToString(CultureInfo.InvariantCulture)} checks")
            : Theme.ColorMarkup(Theme.Bad, $"DOCKER CHECK FAIL  {failed.ToString(CultureInfo.InvariantCulture)} of {checks.Count.ToString(CultureInfo.InvariantCulture)} checks failed"));
        return failed == 0 ? 0 : 1;
    }
}
