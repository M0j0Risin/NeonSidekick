using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Docker;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>docker_lifecycle(target, action, scope?, timeout_seconds?)</c> (2026-10-02): start, stop, restart, pause or unpause one
/// container, or every container of a compose project — in compose's order (dependencies first to start, last to stop,
/// <see cref="DockerCompose"/>). Offered only under <c>Docker writes</c>, read again at the call, and every call waits for the
/// user's yes on the pane (headless refuses it). Each container's outcome is audited.
/// </summary>
public sealed class DockerLifecycleTool : DockerTool
{
    public const string ToolName = "docker_lifecycle";
    public const string TargetArgument = "target";
    public const string ActionArgument = "action";
    public const string ScopeArgument = "scope";
    public const string TimeoutArgument = "timeout_seconds";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "target": { "type": "string", "description": "The container's name (or part of it) or id, or the compose project's name with scope project." },
            "action": { "type": "string", "enum": ["start", "stop", "restart", "pause", "unpause"] },
            "scope": { "type": "string", "enum": ["container", "project"], "description": "One container (default), or every container of a compose project." },
            "timeout_seconds": { "type": "integer", "description": "For stop and restart: seconds to wait before the container is killed, 0 to 120 (default 10)." }
          },
          "required": ["target", "action"]
        }
        """);

    public DockerLifecycleTool(DockerSession docker, Func<string, CancellationToken, Task<bool?>>? confirm) : base(docker, confirm)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Starts, stops, restarts, pauses or unpauses a Docker container, or every container of a compose project. The user is asked to approve each call.";

    public override JsonElement JsonSchema => Schema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        if (Optional(arguments, TargetArgument) is not { } target)
        {
            return DockerText.Missing(TargetArgument);
        }

        string action = (Optional(arguments, ActionArgument) ?? "").ToLowerInvariant();
        if (!DockerText.ActionVerbs.Contains(action))
        {
            return action.Length == 0 ? DockerText.Missing(ActionArgument) : DockerText.BadChoice(ActionArgument, action, DockerText.ActionVerbs);
        }

        string scope = (Optional(arguments, ScopeArgument) ?? "container").ToLowerInvariant();
        if (scope is not ("container" or "project"))
        {
            return DockerText.BadChoice(ScopeArgument, scope, ["container", "project"]);
        }

        var (seconds, numberError) = Number(arguments, TimeoutArgument, 0, DockerSession.MaxStopSeconds, null);
        if (numberError is not null)
        {
            return numberError;
        }

        // The writes key before anything is read: off, the model learns so at once.
        if (DockerPolicy.WriteRefusal(Effective.DockerWrites) is { } off)
        {
            return off;
        }

        var (all, error) = await Docker.ContainersAsync(cancellationToken).ConfigureAwait(false);
        if (all is null)
        {
            return error;
        }

        var match = scope == "project" ? DockerTargets.Project(all, target) : DockerTargets.Resolve(all, target);
        if (match.Error is not null)
        {
            return match.Error;
        }

        var ordered = Order(action, match.Containers);
        string? project = scope == "project" ? ordered[0].Project : null;
        if (await GateWriteAsync(DockerText.LifecycleAct(action, ordered, project), cancellationToken).ConfigureAwait(false) is { } refused)
        {
            return refused;
        }

        return await Docker.ActAsync(action, ordered, seconds, DockerText.ByModel, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A project's containers in the order <paramref name="action"/> takes them: dependencies first to start or restart, last to stop or pause. Pure.</summary>
    public static IReadOnlyList<DockerContainer> Order(string action, IReadOnlyList<DockerContainer> containers) =>
        action is "stop" or "pause" ? DockerCompose.StopOrder(containers) : DockerCompose.StartOrder(containers);
}
