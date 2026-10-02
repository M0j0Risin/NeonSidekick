using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Docker;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>docker_compose(project?)</c> (2026-10-02): the compose projects as their containers' labels describe them — the folder
/// and files each was started from, how many of its containers run, a line per service. Read-only: a project's containers
/// are started and stopped with <c>docker_lifecycle</c>, and <c>compose up</c> from the files is not offered (it needs the
/// CLI, which this app does not start).
/// </summary>
public sealed class DockerComposeTool : DockerTool
{
    public const string ToolName = "docker_compose";
    public const string ProjectArgument = "project";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "project": { "type": "string", "description": "One project's name; leave out for every project." }
          }
        }
        """);

    public DockerComposeTool(DockerSession docker) : base(docker, confirm: null)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Lists the Docker Compose projects running or stopped on this machine, with each project's folder, compose files and services.";

    public override JsonElement JsonSchema => Schema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var (containers, error) = await Docker.ContainersAsync(cancellationToken).ConfigureAwait(false);
        if (containers is null)
        {
            return error;
        }

        return List(containers, Optional(arguments, ProjectArgument));
    }

    /// <summary>The project listing, or one project's; the refusal for a project that is not there. Pure.</summary>
    public static string List(IReadOnlyList<DockerContainer> containers, string? project)
    {
        ArgumentNullException.ThrowIfNull(containers);
        if (project is not null)
        {
            var match = DockerTargets.Project(containers, project);
            if (match.Error is not null)
            {
                return match.Error;
            }

            containers = match.Containers;
        }

        var projects = DockerCompose.Group(containers);
        var lines = new List<string> { DockerText.ProjectsHeader(projects.Count) };
        lines.AddRange(projects.SelectMany(DockerText.ProjectLines));
        return string.Join('\n', lines);
    }
}
