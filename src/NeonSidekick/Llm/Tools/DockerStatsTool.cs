using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Docker;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>docker_stats(container?)</c> (2026-10-02): what a running container uses right now — CPU (of one CPU, as
/// <c>docker stats</c> shows it), memory against its limit, network and disk traffic, processes — for one container or every
/// running one, read <see cref="MaxParallel"/> at a time (the engine takes about a second over each).
/// </summary>
public sealed class DockerStatsTool : DockerTool
{
    public const string ToolName = "docker_stats";
    public const string ContainerArgument = "container";

    /// <summary>How many containers are sampled at once.</summary>
    public const int MaxParallel = 8;

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "container": { "type": "string", "description": "One container's name (or part of it) or id; leave out for every running container." }
          }
        }
        """);

    public DockerStatsTool(DockerSession docker) : base(docker, confirm: null)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Shows the CPU, memory, network and disk use of one running Docker container, or of all of them.";

    public override JsonElement JsonSchema => Schema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        IReadOnlyList<DockerContainer> targets;
        if (Optional(arguments, ContainerArgument) is { } target)
        {
            var (container, _, error) = await ResolveAsync(target, cancellationToken).ConfigureAwait(false);
            if (container is null)
            {
                return error;
            }

            if (!container.Running)
            {
                return DockerText.NotRunning(container);
            }

            targets = [container];
        }
        else
        {
            var (all, error) = await Docker.ContainersAsync(cancellationToken).ConfigureAwait(false);
            if (all is null)
            {
                return error;
            }

            targets = DockerText.Ordered(all.Where(c => c.Running));
        }

        var lines = await SampleAsync(Docker, targets, cancellationToken).ConfigureAwait(false);
        return DockerText.StatsHeader(targets.Count) + (lines.Count > 0 ? "\n" + string.Join('\n', lines) : "");
    }

    /// <summary>One line per container, in the given order, sampled <see cref="MaxParallel"/> at a time; a failed read says so in its place.</summary>
    public static async Task<IReadOnlyList<string>> SampleAsync(DockerSession docker, IReadOnlyList<DockerContainer> targets, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(docker);
        ArgumentNullException.ThrowIfNull(targets);
        var client = docker.Client();
        var lines = new string[targets.Count];
        using var slots = new SemaphoreSlim(MaxParallel);
        await Task.WhenAll(targets.Select(async (container, i) =>
        {
            await slots.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var reply = await client.GetAsync("/containers/" + Uri.EscapeDataString(container.Id) + "/stats?stream=false", DockerSession.StatsTimeout, cancellationToken).ConfigureAwait(false);
                lines[i] = !reply.Ok ? DockerText.StatsFailed(container.Name, reply.Error!)
                    : DockerStatsMath.Parse(reply.Body) is { } sample ? DockerText.StatsLine(container.Name, sample)
                    : DockerText.StatsFailed(container.Name, DockerText.BadAnswer("/containers/" + container.Name + "/stats"));
            }
            finally
            {
                slots.Release();
            }
        })).ConfigureAwait(false);
        return lines;
    }
}
