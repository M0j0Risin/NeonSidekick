using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Docker;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Settings;

namespace NeonSidekick.Tests;

/// <summary>
/// Resolves once per assembly whether a real Docker engine and a running container to read are named (2026-10-02):
/// <c>NEONSIDEKICK_TEST_DOCKER_CONTAINER</c> holds the name of a running container (on the user's machine <c>mysql_dev</c>
/// does), and Docker Desktop's pipe must be there (<c>NEONSIDEKICK_TEST_DOCKER_PIPE</c>, else <c>docker_engine</c>) — looked
/// for among the pipe names, never opened to find out. Skipped without; never a CI safety net. Nothing is ever changed.
/// </summary>
internal static class LiveDocker
{
    public const string ContainerVariable = "NEONSIDEKICK_TEST_DOCKER_CONTAINER";
    public const string PipeVariable = "NEONSIDEKICK_TEST_DOCKER_PIPE";

    public static readonly string? Container = Environment.GetEnvironmentVariable(ContainerVariable) is { Length: > 0 } name ? name.Trim() : null;

    public static readonly string Pipe = DockerPipe.Normalize(Environment.GetEnvironmentVariable(PipeVariable));

    public static readonly bool PipeExists = OperatingSystem.IsWindows() && Directory.GetFiles(@"\\.\pipe\").Any(p => string.Equals(Path.GetFileName(p), Pipe, StringComparison.OrdinalIgnoreCase));

    public static string? Unavailable =>
        Container is null ? $"{ContainerVariable} is not set to a running container's name."
        : !PipeExists ? $"No pipe {DockerPipe.Display(Pipe)}: Docker Desktop is not running."
        : null;
}

/// <summary>Skips unless <see cref="LiveDocker"/> names a container and the engine's pipe is there.</summary>
public sealed class LiveDockerFactAttribute : FactAttribute
{
    public LiveDockerFactAttribute()
    {
        if (LiveDocker.Unavailable is { } why)
        {
            Skip = why;
        }
    }
}

/// <summary>The Docker read tools against the real engine (2026-10-02): the list, a redacted inspect, a short log, a stats sample, the compose grouping.</summary>
public sealed class LiveDockerTests : IDisposable
{
    private readonly AppSettingsData _settings = new() { DockerTools = true, DockerEnginePipe = LiveDocker.Pipe };
    private readonly DockerSession _docker;
    private readonly IReadOnlyList<AIFunction> _tools;

    public LiveDockerTests()
    {
        _docker = new DockerSession(() => _settings);
        _tools = ChatScreen.DockerTools(_docker, confirm: null);
    }

    public void Dispose() => _docker.Dispose();

    private async Task<string> Invoke<T>(params (string Name, object? Value)[] pairs) where T : AIFunction
    {
        var arguments = new AIFunctionArguments();
        foreach (var (name, value) in pairs)
        {
            arguments[name] = value;
        }

        return (string)(await _tools.OfType<T>().Single().InvokeAsync(arguments))!;
    }

    [LiveDockerFact]
    public async Task TheList_HoldsTheContainer_Running()
    {
        string list = await Invoke<DockerContainersTool>(("all", false));
        Assert.StartsWith("Docker: ", list);
        Assert.Contains("\n" + LiveDocker.Container + " · running", list);
    }

    [LiveDockerFact]
    public async Task TheInspect_ShowsNoEnvironmentValue()
    {
        var (containers, error) = await _docker.ContainersAsync(CancellationToken.None);
        Assert.Null(error);
        var container = Assert.Single(containers!, c => c.Name == LiveDocker.Container);
        var raw = await _docker.Client().GetAsync("/containers/" + container.Id + "/json", DockerSession.ReadTimeout, CancellationToken.None);
        using var doc = DockerJson.TryParse(raw.Body)!;
        var values = DockerJson.Strings(doc.RootElement.GetProperty("Config"), "Env").Select(e => e[(e.IndexOf('=') + 1)..]).Where(v => v.Length >= 4).ToList();

        string summary = await Invoke<DockerInspectTool>(("container", LiveDocker.Container));
        Assert.StartsWith("Docker inspect: " + LiveDocker.Container + " · ", summary);
        Assert.All(values, v => Assert.DoesNotContain(v, summary));
    }

    [LiveDockerFact]
    public async Task TheLog_ReturnsAtMostTheTail()
    {
        string log = await Invoke<DockerLogsTool>(("container", LiveDocker.Container), ("tail", 5));
        Assert.StartsWith("Docker logs: " + LiveDocker.Container, log);
        Assert.True(log.Split('\n').Length <= 6, log);
    }

    [LiveDockerFact]
    public async Task TheStats_HaveAMemoryLimit()
    {
        string stats = await Invoke<DockerStatsTool>(("container", LiveDocker.Container));
        Assert.StartsWith("Docker stats: 1 running container\n" + LiveDocker.Container + " · CPU ", stats);
        Assert.DoesNotContain(" / 0 B ", stats);
    }

    [LiveDockerFact]
    public async Task TheStatus_AgreesAVersion_AndCountsTheContainers()
    {
        var status = await DockerCommand.StatusAsync(_docker, CancellationToken.None);
        Assert.False(status.Failed, string.Join("\n", status.Lines));
        Assert.Contains(" (asking v1.", status.Lines[0]);
        Assert.StartsWith("Containers: ", status.Lines[1]);
        string compose = await Invoke<DockerComposeTool>();
        Assert.StartsWith("Docker compose: ", compose);
    }
}
