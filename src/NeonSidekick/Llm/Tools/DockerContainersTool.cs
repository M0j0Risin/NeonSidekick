using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Docker;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>docker_containers(all?, filter?, project?)</c> (2026-10-02): the containers, a line each — name, state and health,
/// status, image, ports, compose project, short id — running first; stopped ones too unless <c>all</c> is false, narrowed
/// by part of a name or image and by a compose project.
/// </summary>
public sealed class DockerContainersTool : DockerTool
{
    public const string ToolName = "docker_containers";
    public const string AllArgument = "all";
    public const string FilterArgument = "filter";
    public const string ProjectArgument = "project";

    /// <summary>The most lines one call lists.</summary>
    public const int MaxLines = 100;

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "all": { "type": "boolean", "description": "Include stopped containers (default true)." },
            "filter": { "type": "string", "description": "Only containers whose name or image contains this text." },
            "project": { "type": "string", "description": "Only the containers of this compose project." }
          }
        }
        """);

    public DockerContainersTool(DockerSession docker) : base(docker, confirm: null)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Lists the Docker containers on this machine (Docker Desktop): name, state and health, status, image, published ports and compose project, running first. " +
        "Use it to find a container's name before the other docker_ tools.";

    public override JsonElement JsonSchema => Schema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var (all, error) = Flag(arguments, AllArgument, fallback: true);
        if (error is not null)
        {
            return error;
        }

        var (containers, failed) = await Docker.ContainersAsync(cancellationToken).ConfigureAwait(false);
        if (containers is null)
        {
            return failed;
        }

        return List(containers, all, Optional(arguments, FilterArgument), Optional(arguments, ProjectArgument));
    }

    /// <summary>The listing for a container list and the filters. Pure.</summary>
    public static string List(IReadOnlyList<DockerContainer> containers, bool all, string? filter, string? project)
    {
        ArgumentNullException.ThrowIfNull(containers);
        IEnumerable<DockerContainer> pool = containers;
        var filters = new List<string>(3);
        if (!all)
        {
            pool = pool.Where(c => c.Running || c.Paused);
            filters.Add("running");
        }

        if (filter is not null)
        {
            pool = pool.Where(c => c.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) || c.Image.Contains(filter, StringComparison.OrdinalIgnoreCase));
            filters.Add("'" + filter + "'");
        }

        if (project is not null)
        {
            pool = pool.Where(c => string.Equals(c.Project, project, StringComparison.OrdinalIgnoreCase));
            filters.Add("project " + project);
        }

        var shown = DockerText.Ordered(pool);
        var lines = new List<string>(Math.Min(shown.Count, MaxLines) + 2) { DockerText.ContainersHeader(shown, string.Join(", ", filters)) };
        lines.AddRange(shown.Take(MaxLines).Select(DockerText.ContainerLine));
        if (shown.Count > MaxLines)
        {
            lines.Add("(… " + (shown.Count - MaxLines).ToString(CultureInfo.InvariantCulture) + " more; narrow with filter or project)");
        }

        return string.Join('\n', lines);
    }
}
