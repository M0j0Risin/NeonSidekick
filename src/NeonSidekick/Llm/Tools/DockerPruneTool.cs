using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Diagnostics;
using NeonSidekick.Docker;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>docker_prune(kind, all?)</c> (2026-10-02): what no container uses removed in one go — the stopped containers, the
/// dangling images (every unused one with <c>all</c>), the empty networks, the anonymous unused volumes (named ones too with
/// <c>all</c>), the build cache. The question on the pane says how many and how much first, from the lists and
/// <c>/system/df</c>. Offered only under <c>Docker writes</c>, off by name in a fresh profile's <c>ToolsDisabled</c>.
/// </summary>
public sealed class DockerPruneTool : DockerTool
{
    public const string ToolName = "docker_prune";
    public const string KindArgument = "kind";
    public const string AllArgument = "all";

    /// <summary>The kinds, in the schema's order.</summary>
    public static readonly IReadOnlyList<string> Kinds = ["containers", "images", "networks", "volumes", "build_cache"];

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "kind": { "type": "string", "enum": ["containers", "images", "networks", "volumes", "build_cache"] },
            "all": { "type": "boolean", "description": "images: every image no container uses, not only untagged ones; volumes: named volumes too, not only anonymous ones; build_cache: all of it (default false)." }
          },
          "required": ["kind"]
        }
        """);

    public DockerPruneTool(DockerSession docker, Func<string, CancellationToken, Task<bool?>>? confirm) : base(docker, confirm)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Removes every unused Docker thing of one kind: stopped containers, unused images, empty networks, unused volumes or the build cache. The user is asked to approve each call, with what it would free.";

    public override JsonElement JsonSchema => Schema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        string kind = (Optional(arguments, KindArgument) ?? "").ToLowerInvariant().Replace(' ', '_');
        if (!Kinds.Contains(kind))
        {
            return kind.Length == 0 ? DockerText.Missing(KindArgument) : DockerText.BadChoice(KindArgument, kind, Kinds);
        }

        var (all, flagError) = Flag(arguments, AllArgument, fallback: false);
        if (flagError is not null)
        {
            return flagError;
        }

        if (DockerPolicy.WriteRefusal(Effective.DockerWrites) is { } off)
        {
            return off;
        }

        string preview = await PreviewAsync(kind, all, cancellationToken).ConfigureAwait(false);
        if (await GateWriteAsync(DockerText.PruneAct(kind, all, preview), cancellationToken).ConfigureAwait(false) is { } refused)
        {
            return refused;
        }

        var reply = await Docker.Client().PostAsync(PrunePath(kind, all), DockerSession.CleanupTimeout, cancellationToken).ConfigureAwait(false);
        if (!reply.Ok)
        {
            DiagnosticLog.Info(DockerText.Category, DockerText.AuditLine(DockerText.ByModel, "prune " + kind, reply.Error!));
            return reply.Error;
        }

        var (deleted, reclaimed) = DockerJson.PruneResult(reply.Body) ?? (0, 0);
        string result = DockerText.Pruned(kind, deleted, reclaimed);
        DiagnosticLog.Info(DockerText.Category, DockerText.AuditLine(DockerText.ByModel, "prune " + kind, result));
        return result;
    }

    /// <summary>The prune's endpoint: <c>/containers/prune</c>, <c>/images/prune</c> (<c>dangling=false</c> for all), <c>/networks/prune</c>, <c>/volumes/prune</c> (<c>all=true</c> for named ones), <c>/build/prune</c> (<c>all=1</c>). Pure.</summary>
    public static string PrunePath(string kind, bool all) => kind switch
    {
        "containers" => "/containers/prune",
        "images" => "/images/prune" + (all ? "?filters=" + Uri.EscapeDataString("{\"dangling\":[\"false\"]}") : ""),
        "networks" => "/networks/prune",
        "volumes" => "/volumes/prune" + (all ? "?filters=" + Uri.EscapeDataString("{\"all\":[\"true\"]}") : ""),
        _ => "/build/prune" + (all ? "?all=1" : ""),
    };

    /// <summary>
    /// What the prune would take, in words (<c>3 stopped containers</c>, <c>about 1.2 GB</c>): read from the lists and the disk
    /// use; empty when they cannot be read — the question then asks without it.
    /// </summary>
    private async Task<string> PreviewAsync(string kind, bool all, CancellationToken cancellationToken)
    {
        var client = Docker.Client();
        switch (kind)
        {
            case "containers":
            {
                var (containers, _) = await Docker.ContainersAsync(cancellationToken).ConfigureAwait(false);
                return containers is null ? "" : DockerText.Count(containers.Count(c => !c.Running && !c.Paused && !string.Equals(c.State, "restarting", StringComparison.OrdinalIgnoreCase)), "stopped container");
            }

            case "networks":
            {
                var (containers, _) = await Docker.ContainersAsync(cancellationToken).ConfigureAwait(false);
                var reply = await client.GetAsync("/networks", DockerSession.ReadTimeout, cancellationToken).ConfigureAwait(false);
                if (containers is null || !reply.Ok || DockerJson.Networks(reply.Body) is not { } networks)
                {
                    return "";
                }

                return DockerText.Count(networks.Count(n => !DockerResourcesTool.BuiltInNetworks.Contains(n.Name) && !containers.Any(c => c.Networks.Contains(n.Name, StringComparer.Ordinal))), "unused network");
            }

            default:
            {
                var reply = await client.GetAsync("/system/df", DockerSession.CleanupTimeout, cancellationToken).ConfigureAwait(false);
                if (!reply.Ok || DockerJson.DiskUsage(reply.Body) is not { } usage)
                {
                    return "";
                }

                return DockerText.PrunePreview(kind, all, usage);
            }
        }
    }
}
