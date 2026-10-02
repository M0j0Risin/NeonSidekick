using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Docker;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>docker_resources(kind, filter?, unused?)</c> (2026-10-02): the engine's other things, one kind a call — the images
/// (tags, size, age, which containers use each), the volumes (driver, which containers mount each), the networks (driver,
/// subnets, which containers are on each) or the disk use per kind as <c>docker system df</c> gives it. One tool rather
/// than four: a small model chooses among fewer tools better (the plan's call).
/// </summary>
public sealed class DockerResourcesTool : DockerTool
{
    public const string ToolName = "docker_resources";
    public const string KindArgument = "kind";
    public const string FilterArgument = "filter";
    public const string UnusedArgument = "unused";

    /// <summary>The kinds, in the schema's order.</summary>
    public static readonly IReadOnlyList<string> Kinds = ["images", "volumes", "networks", "disk"];

    /// <summary>The most lines one call lists.</summary>
    public const int MaxLines = 100;

    /// <summary>The networks every engine has, never counted unused.</summary>
    public static readonly IReadOnlySet<string> BuiltInNetworks = new HashSet<string>(StringComparer.Ordinal) { "bridge", "host", "none" };

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "kind": { "type": "string", "enum": ["images", "volumes", "networks", "disk"], "description": "What to list; disk is the space each kind takes and what a prune would free." },
            "filter": { "type": "string", "description": "Only those whose name or tag contains this text." },
            "unused": { "type": "boolean", "description": "Only those no container uses (dangling images, unmounted volumes, empty networks)." }
          },
          "required": ["kind"]
        }
        """);

    public DockerResourcesTool(DockerSession docker) : base(docker, confirm: null)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Lists Docker images, volumes or networks with what uses each, or shows the disk space Docker takes and what could be reclaimed.";

    public override JsonElement JsonSchema => Schema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        string kind = (Optional(arguments, KindArgument) ?? "").ToLowerInvariant();
        if (!Kinds.Contains(kind))
        {
            return kind.Length == 0 ? DockerText.Missing(KindArgument) : DockerText.BadChoice(KindArgument, kind, Kinds);
        }

        var (unused, flagError) = Flag(arguments, UnusedArgument, fallback: false);
        if (flagError is not null)
        {
            return flagError;
        }

        string? filter = Optional(arguments, FilterArgument);
        var client = Docker.Client();
        if (kind == "disk")
        {
            var df = await client.GetAsync("/system/df", DockerSession.CleanupTimeout, cancellationToken).ConfigureAwait(false);
            if (!df.Ok)
            {
                return df.Error;
            }

            return DockerJson.DiskUsage(df.Body) is { } usage ? string.Join('\n', DockerText.DiskLines(usage)) : DockerText.BadAnswer("/system/df");
        }

        var (containers, error) = await Docker.ContainersAsync(cancellationToken).ConfigureAwait(false);
        if (containers is null)
        {
            return error;
        }

        string path = kind switch { "images" => "/images/json", "volumes" => "/volumes", _ => "/networks" };
        var reply = await client.GetAsync(path, DockerSession.ReadTimeout, cancellationToken).ConfigureAwait(false);
        if (!reply.Ok)
        {
            return reply.Error;
        }

        return kind switch
        {
            "images" => DockerJson.Images(reply.Body) is { } images ? Images(images, containers, filter, unused, Docker.Time.GetUtcNow()) : DockerText.BadAnswer(path),
            "volumes" => DockerJson.Volumes(reply.Body) is { } volumes ? Volumes(volumes, containers, filter, unused) : DockerText.BadAnswer(path),
            _ => DockerJson.Networks(reply.Body) is { } networks ? Networks(networks, containers, filter, unused) : DockerText.BadAnswer(path),
        };
    }

    /// <summary>The image listing, newest first, with each one's containers. Pure.</summary>
    public static string Images(IReadOnlyList<DockerImage> images, IReadOnlyList<DockerContainer> containers, string? filter, bool unused, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(containers);
        var users = containers.GroupBy(c => c.ImageId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(c => c.Name).ToList(), StringComparer.Ordinal);
        IReadOnlyList<string> UsedBy(DockerImage image) => users.TryGetValue(image.Id, out var names) ? names : [];
        var pool = images.Where(i => filter is null || i.Tags.Any(t => t.Contains(filter, StringComparison.OrdinalIgnoreCase)) || i.Id.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Where(i => !unused || UsedBy(i).Count == 0)
            .OrderByDescending(i => i.Created).ToList();
        int idle = pool.Count(i => UsedBy(i).Count == 0);
        var lines = new List<string> { DockerText.ResourceHeader("images", pool.Count, Math.Min(pool.Count, MaxLines), Filters(filter, unused), idle) };
        lines.AddRange(pool.Take(MaxLines).Select(i => DockerText.ImageLine(i, UsedBy(i), now)));
        return string.Join('\n', lines);
    }

    /// <summary>The volume listing, by name, with each one's containers. Pure.</summary>
    public static string Volumes(IReadOnlyList<DockerVolume> volumes, IReadOnlyList<DockerContainer> containers, string? filter, bool unused)
    {
        ArgumentNullException.ThrowIfNull(volumes);
        ArgumentNullException.ThrowIfNull(containers);
        IReadOnlyList<string> UsedBy(DockerVolume volume) => containers.Where(c => c.Mounts.Any(m => m.Type == "volume" && string.Equals(m.Name, volume.Name, StringComparison.Ordinal))).Select(c => c.Name).ToList();
        var pool = volumes.Where(v => filter is null || v.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Select(v => (Volume: v, Users: UsedBy(v)))
            .Where(v => !unused || v.Users.Count == 0)
            .OrderBy(v => v.Volume.Anonymous).ThenBy(v => v.Volume.Name, StringComparer.Ordinal).ToList();
        var lines = new List<string> { DockerText.ResourceHeader("volumes", pool.Count, Math.Min(pool.Count, MaxLines), Filters(filter, unused), pool.Count(v => v.Users.Count == 0)) };
        lines.AddRange(pool.Take(MaxLines).Select(v => DockerText.VolumeLine(v.Volume, v.Users)));
        return string.Join('\n', lines);
    }

    /// <summary>The network listing, by name, with the containers on each. Pure.</summary>
    public static string Networks(IReadOnlyList<DockerNetwork> networks, IReadOnlyList<DockerContainer> containers, string? filter, bool unused)
    {
        ArgumentNullException.ThrowIfNull(networks);
        ArgumentNullException.ThrowIfNull(containers);
        IReadOnlyList<string> Members(DockerNetwork network) => containers.Where(c => c.Networks.Contains(network.Name, StringComparer.Ordinal)).Select(c => c.Name).ToList();
        var pool = networks.Where(n => filter is null || n.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Select(n => (Network: n, Members: Members(n)))
            .Where(n => !unused || (n.Members.Count == 0 && !BuiltInNetworks.Contains(n.Network.Name)))
            .OrderBy(n => n.Network.Name, StringComparer.Ordinal).ToList();
        var lines = new List<string> { DockerText.ResourceHeader("networks", pool.Count, Math.Min(pool.Count, MaxLines), Filters(filter, unused), pool.Count(n => n.Members.Count == 0 && !BuiltInNetworks.Contains(n.Network.Name))) };
        lines.AddRange(pool.Take(MaxLines).Select(n => DockerText.NetworkLine(n.Network, n.Members)));
        return string.Join('\n', lines);
    }

    private static string Filters(string? filter, bool unused)
    {
        var parts = new List<string>(2);
        if (filter is not null)
        {
            parts.Add("'" + filter + "'");
        }

        if (unused)
        {
            parts.Add("unused");
        }

        return string.Join(", ", parts);
    }
}
