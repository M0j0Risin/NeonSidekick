using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Docker;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>docker_inspect(container)</c> (2026-10-02): one container in detail — state and health with its last checks, image,
/// command, environment names, ports, mounts, networks, restart policy and limits, compose project and labels — as
/// <see cref="DockerInspect.Summary"/> writes it, every environment value and secret-sounding flag or label hidden.
/// </summary>
public sealed class DockerInspectTool : DockerTool
{
    public const string ToolName = "docker_inspect";
    public const string ContainerArgument = "container";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "container": { "type": "string", "description": "The container's name (or part of it) or id." }
          },
          "required": ["container"]
        }
        """);

    public DockerInspectTool(DockerSession docker) : base(docker, confirm: null)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Shows one Docker container in detail: state, health checks, exit code, image, command, environment variable names (values hidden), ports, mounts, networks, restart policy, limits and compose project.";

    public override JsonElement JsonSchema => Schema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        if (Optional(arguments, ContainerArgument) is not { } target)
        {
            return DockerText.Missing(ContainerArgument);
        }

        var (container, _, error) = await ResolveAsync(target, cancellationToken).ConfigureAwait(false);
        if (container is null)
        {
            return error;
        }

        var reply = await Docker.Client().GetAsync("/containers/" + Uri.EscapeDataString(container.Id) + "/json", DockerSession.ReadTimeout, cancellationToken).ConfigureAwait(false);
        if (!reply.Ok)
        {
            return reply.Error;
        }

        // The image's labels, entrypoint, command and health checks are its author's words (2026-11-15, the web-tools plan's §3), each fenced
        // where it is written since 2026-11-16 (the review's #69, #179), the note last.
        return DockerInspect.Summary(reply.Body, fenced: true) is { } summary ? UntrustedText.WithNote("Docker inspect: " + summary) : DockerText.BadAnswer("/containers/" + container.Name + "/json");
    }
}
