using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Diagnostics;
using NeonSidekick.Docker;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>docker_remove(kind, name, force?)</c> (2026-10-02): one container, image or volume removed for good — a container's
/// anonymous volumes are kept, a removed volume's data is gone. Offered only under <c>Docker writes</c>, off by name in a fresh
/// profile's <c>ToolsDisabled</c>, and every call waits for the user's yes. Without <c>force</c> the engine refuses a running
/// container and an image a container uses.
/// </summary>
public sealed class DockerRemoveTool : DockerTool
{
    public const string ToolName = "docker_remove";
    public const string KindArgument = "kind";
    public const string NameArgument = "name";
    public const string ForceArgument = "force";

    /// <summary>The kinds, in the schema's order.</summary>
    public static readonly IReadOnlyList<string> Kinds = ["container", "image", "volume"];

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "kind": { "type": "string", "enum": ["container", "image", "volume"] },
            "name": { "type": "string", "description": "The container's name or id, the image's name:tag or id, or the volume's name." },
            "force": { "type": "boolean", "description": "Remove a running container, or an image a stopped container uses (default false)." }
          },
          "required": ["kind", "name"]
        }
        """);

    public DockerRemoveTool(DockerSession docker, Func<string, CancellationToken, Task<bool?>>? confirm) : base(docker, confirm)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Removes one Docker container, image or volume for good. The user is asked to approve each call.";

    public override JsonElement JsonSchema => Schema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        string kind = (Optional(arguments, KindArgument) ?? "").ToLowerInvariant();
        if (!Kinds.Contains(kind))
        {
            return kind.Length == 0 ? DockerText.Missing(KindArgument) : DockerText.BadChoice(KindArgument, kind, Kinds);
        }

        if (Optional(arguments, NameArgument) is not { } name)
        {
            return DockerText.Missing(NameArgument);
        }

        var (force, flagError) = Flag(arguments, ForceArgument, fallback: false);
        if (flagError is not null)
        {
            return flagError;
        }

        if (DockerPolicy.WriteRefusal(Effective.DockerWrites) is { } off)
        {
            return off;
        }

        string path;
        string act;
        string shown = name;
        if (kind == "container")
        {
            var (container, _, error) = await ResolveAsync(name, cancellationToken).ConfigureAwait(false);
            if (container is null)
            {
                return error;
            }

            shown = container.Name;
            path = "/containers/" + Uri.EscapeDataString(container.Id) + "?v=0&force=" + (force ? "1" : "0");
            act = DockerText.RemoveAct(kind, container.Name + " (" + container.Image + ", " + container.Status + ")", force);
        }
        else
        {
            path = (kind == "image" ? "/images/" : "/volumes/") + Uri.EscapeDataString(name) + "?force=" + (force ? "1" : "0");
            act = DockerText.RemoveAct(kind, name, force);
        }

        if (await GateWriteAsync(act, cancellationToken).ConfigureAwait(false) is { } refused)
        {
            return refused;
        }

        var reply = await Docker.Client().DeleteAsync(path, DockerSession.CleanupTimeout, cancellationToken).ConfigureAwait(false);
        DiagnosticLog.Info(DockerText.Category, DockerText.AuditLine(DockerText.ByModel, "remove " + kind + " " + shown, reply.Ok ? "done" : reply.Error!));
        return reply.Ok ? DockerText.Removed(kind, shown) : reply.Error;
    }
}
