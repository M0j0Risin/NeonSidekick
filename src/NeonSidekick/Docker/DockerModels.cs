using System.Globalization;
using System.Text.Json;

namespace NeonSidekick.Docker;

/// <summary>A published or exposed port of a container: <c>3307→3306/tcp</c>; <see cref="Public"/> null for one only exposed.</summary>
public sealed record DockerPort(string? Ip, int Private, int? Public, string Type);

/// <summary>A mount of a container, from the list's summary: a named or anonymous volume, a bind, a tmpfs.</summary>
public sealed record DockerMount(string Type, string? Name, string? Source, string Destination, bool ReadWrite);

/// <summary>
/// One container as <c>GET /containers/json</c> lists it (2026-10-02): the id, the name without its <c>/</c>, the image as it
/// was asked for, the state word (<c>running</c>, <c>exited</c>, <c>paused</c>, <c>created</c>, <c>restarting</c>,
/// <c>removing</c>, <c>dead</c>), the status sentence (<c>Up 40 hours (healthy)</c>), the creation time, the ports, the
/// labels (the compose project's among them), the networks it is on and its mounts.
/// </summary>
public sealed record DockerContainer(
    string Id,
    string Name,
    string Image,
    string ImageId,
    string State,
    string Status,
    long Created,
    IReadOnlyList<DockerPort> Ports,
    IReadOnlyDictionary<string, string> Labels,
    IReadOnlyList<string> Networks,
    IReadOnlyList<DockerMount> Mounts)
{
    /// <summary>The compose project label.</summary>
    public const string ProjectLabel = "com.docker.compose.project";

    /// <summary>The compose service label.</summary>
    public const string ServiceLabel = "com.docker.compose.service";

    /// <summary>The compose working-directory label.</summary>
    public const string WorkingDirLabel = "com.docker.compose.project.working_dir";

    /// <summary>The compose files label.</summary>
    public const string ConfigFilesLabel = "com.docker.compose.project.config_files";

    /// <summary>The compose dependencies label: <c>db:service_started:false,cache:service_healthy:true</c>.</summary>
    public const string DependsOnLabel = "com.docker.compose.depends_on";

    /// <summary>The first twelve characters of the id, as the CLI shows it.</summary>
    public string ShortId => Id.Length > 12 ? Id[..12] : Id;

    /// <summary>Whether the container runs (a paused one does not).</summary>
    public bool Running => string.Equals(State, "running", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether it is paused.</summary>
    public bool Paused => string.Equals(State, "paused", StringComparison.OrdinalIgnoreCase);

    /// <summary>The health from the status sentence: <c>healthy</c>, <c>unhealthy</c>, <c>starting</c>, or null without a health check.</summary>
    public string? Health => DockerJson.HealthOf(Status);

    /// <summary>The compose project, or null for a container compose did not make.</summary>
    public string? Project => Labels.TryGetValue(ProjectLabel, out var p) && p.Length > 0 ? p : null;

    /// <summary>The compose service, or null.</summary>
    public string? Service => Labels.TryGetValue(ServiceLabel, out var s) && s.Length > 0 ? s : null;
}

/// <summary>One TCP port binding: the container's port, the host address it is bound on (null or blank for every address) and the host port.</summary>
public sealed record DockerBinding(int Private, string? HostIp, int HostPort);

/// <summary>
/// What a Docker server's switch needs of <c>GET /containers/{id}/json</c> (2026-10-02): the state word and the exit code, the
/// ports published while it runs (<c>NetworkSettings.Ports</c>) and the ones it asks for (<c>HostConfig.PortBindings</c>, there
/// while it is stopped too), TCP only, and the network mode.
/// </summary>
public sealed record DockerInspected(string Id, string Name, string Status, int ExitCode, IReadOnlyList<DockerBinding> Published, IReadOnlyList<DockerBinding> Bound, string? NetworkMode)
{
    /// <summary>Whether it holds what a running container holds: running, paused or restarting (DockerLlmPicker's rule — each still has its memory).</summary>
    public bool Active => Status is "running" or "paused" or "restarting";

    /// <summary>Whether it has exited or died: a server that is never coming up.</summary>
    public bool Gone => Status is "exited" or "dead";
}

/// <summary>An image as <c>GET /images/json</c> lists it.</summary>
public sealed record DockerImage(string Id, IReadOnlyList<string> Tags, long Created, long Size)
{
    /// <summary>The id without <c>sha256:</c>, twelve characters, as the CLI shows it.</summary>
    public string ShortId => DockerJson.ShortHash(Id);

    /// <summary>Whether it has no tag (<c>&lt;none&gt;:&lt;none&gt;</c>): dangling.</summary>
    public bool Dangling => Tags.Count == 0;
}

/// <summary>A volume as <c>GET /volumes</c> lists it.</summary>
public sealed record DockerVolume(string Name, string Driver, string? CreatedAt, IReadOnlyDictionary<string, string> Labels)
{
    /// <summary>Whether the name is a hash, as an anonymous volume's is.</summary>
    public bool Anonymous => Name.Length == 64 && Name.All(char.IsAsciiHexDigitLower);
}

/// <summary>A network as <c>GET /networks</c> lists it.</summary>
public sealed record DockerNetwork(string Id, string Name, string Driver, string Scope, IReadOnlyList<string> Subnets, bool Internal);

/// <summary>The engine as <c>GET /version</c> answers: the Desktop's name, the engine's version and its API range.</summary>
public sealed record DockerVersion(string? Platform, string Version, string ApiVersion, string MinApiVersion, string Os, string Arch, string? Kernel);

/// <summary>The machine as <c>GET /info</c> answers: the counts, the CPUs and memory the engine has, its OS.</summary>
public sealed record DockerInfo(int Containers, int Running, int Paused, int Stopped, int Images, int Cpus, long Memory, string? OperatingSystem, string? Name);

/// <summary>The disk use as <c>GET /system/df</c> answers it, summed per kind.</summary>
public sealed record DockerDiskUsage(DockerDiskLine Images, DockerDiskLine Containers, DockerDiskLine Volumes, DockerDiskLine BuildCache);

/// <summary>One kind's line of <see cref="DockerDiskUsage"/>: how many, how many in use, the size and what a prune would free.</summary>
public sealed record DockerDiskLine(int Count, int Active, long Size, long Reclaimable);

/// <summary>
/// The Engine API's JSON, read by hand over <see cref="JsonDocument"/> (2026-10-02): no serializer context — the
/// <see cref="HomeAssistant.HaJson"/> and <see cref="Claude.ClaudeStreamParser"/> way — so a field the engine drops or
/// adds never fails a read. Every parse is pure and returns null for an answer it cannot read.
/// </summary>
public static class DockerJson
{
    /// <summary>The container list, or null.</summary>
    public static IReadOnlyList<DockerContainer>? Containers(string json)
    {
        using var doc = TryParse(json);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var list = new List<DockerContainer>(doc.RootElement.GetArrayLength());
        foreach (var c in doc.RootElement.EnumerateArray())
        {
            if (c.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string name = c.TryGetProperty("Names", out var names) && names.ValueKind == JsonValueKind.Array && names.GetArrayLength() > 0
                ? (names[0].GetString() ?? "").TrimStart('/')
                : "";
            var ports = new List<DockerPort>();
            if (c.TryGetProperty("Ports", out var portList) && portList.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in portList.EnumerateArray())
                {
                    ports.Add(new DockerPort(Str(p, "IP"), Int(p, "PrivatePort") ?? 0, Int(p, "PublicPort"), Str(p, "Type") ?? "tcp"));
                }
            }

            var networks = new List<string>();
            if (c.TryGetProperty("NetworkSettings", out var settings) && settings.ValueKind == JsonValueKind.Object
                && settings.TryGetProperty("Networks", out var nets) && nets.ValueKind == JsonValueKind.Object)
            {
                networks.AddRange(nets.EnumerateObject().Select(n => n.Name));
            }

            var mounts = new List<DockerMount>();
            if (c.TryGetProperty("Mounts", out var mountList) && mountList.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in mountList.EnumerateArray())
                {
                    mounts.Add(new DockerMount(Str(m, "Type") ?? "", Str(m, "Name"), Str(m, "Source"), Str(m, "Destination") ?? "", Bool(m, "RW") ?? true));
                }
            }

            list.Add(new DockerContainer(
                Str(c, "Id") ?? "",
                name,
                Str(c, "Image") ?? "",
                Str(c, "ImageID") ?? "",
                Str(c, "State") ?? "",
                Str(c, "Status") ?? "",
                Long(c, "Created") ?? 0,
                ports,
                Labels(c, "Labels"),
                networks,
                mounts));
        }

        return list;
    }

    /// <summary>An inspect answer read into <see cref="DockerInspected"/>, or null. Pure.</summary>
    public static DockerInspected? Inspected(string json)
    {
        using var doc = TryParse(json);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var r = doc.RootElement;
        var state = r.TryGetProperty("State", out var s) ? s : default;
        var host = r.TryGetProperty("HostConfig", out var h) ? h : default;
        var network = r.TryGetProperty("NetworkSettings", out var n) ? n : default;
        return new DockerInspected(
            Str(r, "Id") ?? "",
            (Str(r, "Name") ?? "").TrimStart('/'),
            Str(state, "Status") ?? "",
            Int(state, "ExitCode") ?? 0,
            Bindings(network.ValueKind == JsonValueKind.Object && network.TryGetProperty("Ports", out var published) ? published : default),
            Bindings(host.ValueKind == JsonValueKind.Object && host.TryGetProperty("PortBindings", out var bound) ? bound : default),
            Str(host, "NetworkMode"));
    }

    /// <summary>A port map (<c>{"8000/tcp": [{"HostIp": "0.0.0.0", "HostPort": "8000"}]}</c>) as its TCP bindings; a blank or unparsable host port is skipped.</summary>
    private static List<DockerBinding> Bindings(JsonElement map)
    {
        var list = new List<DockerBinding>();
        if (map.ValueKind != JsonValueKind.Object)
        {
            return list;
        }

        foreach (var port in map.EnumerateObject())
        {
            string[] parts = port.Name.Split('/');
            if (parts.Length != 2 || !string.Equals(parts[1], "tcp", StringComparison.OrdinalIgnoreCase)
                || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int inner) || port.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var binding in port.Value.EnumerateArray())
            {
                if (int.TryParse(Str(binding, "HostPort"), NumberStyles.None, CultureInfo.InvariantCulture, out int hostPort) && hostPort > 0)
                {
                    list.Add(new DockerBinding(inner, Str(binding, "HostIp"), hostPort));
                }
            }
        }

        return list;
    }

    /// <summary>The image list, or null.</summary>
    public static IReadOnlyList<DockerImage>? Images(string json)
    {
        using var doc = TryParse(json);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return doc.RootElement.EnumerateArray()
            .Where(i => i.ValueKind == JsonValueKind.Object)
            .Select(i => new DockerImage(Str(i, "Id") ?? "", Strings(i, "RepoTags").Where(t => !t.StartsWith("<none>", StringComparison.Ordinal)).ToList(), Long(i, "Created") ?? 0, Long(i, "Size") ?? 0))
            .ToList();
    }

    /// <summary>The volume list (<c>{"Volumes": [...]}</c>), or null.</summary>
    public static IReadOnlyList<DockerVolume>? Volumes(string json)
    {
        using var doc = TryParse(json);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!doc.RootElement.TryGetProperty("Volumes", out var volumes) || volumes.ValueKind != JsonValueKind.Array)
        {
            return volumes.ValueKind == JsonValueKind.Null ? [] : null;
        }

        return volumes.EnumerateArray()
            .Where(v => v.ValueKind == JsonValueKind.Object)
            .Select(v => new DockerVolume(Str(v, "Name") ?? "", Str(v, "Driver") ?? "", Str(v, "CreatedAt"), Labels(v, "Labels")))
            .ToList();
    }

    /// <summary>The network list, or null.</summary>
    public static IReadOnlyList<DockerNetwork>? Networks(string json)
    {
        using var doc = TryParse(json);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var list = new List<DockerNetwork>();
        foreach (var n in doc.RootElement.EnumerateArray())
        {
            if (n.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var subnets = new List<string>();
            if (n.TryGetProperty("IPAM", out var ipam) && ipam.ValueKind == JsonValueKind.Object
                && ipam.TryGetProperty("Config", out var config) && config.ValueKind == JsonValueKind.Array)
            {
                subnets.AddRange(config.EnumerateArray().Select(c => Str(c, "Subnet")).OfType<string>());
            }

            list.Add(new DockerNetwork(Str(n, "Id") ?? "", Str(n, "Name") ?? "", Str(n, "Driver") ?? "", Str(n, "Scope") ?? "", subnets, Bool(n, "Internal") ?? false));
        }

        return list;
    }

    /// <summary><c>GET /version</c>'s answer, or null.</summary>
    public static DockerVersion? Version(string json)
    {
        using var doc = TryParse(json);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var root = doc.RootElement;
        string? platform = root.TryGetProperty("Platform", out var p) && p.ValueKind == JsonValueKind.Object ? Str(p, "Name") : null;
        string? api = Str(root, "ApiVersion");
        if (api is null)
        {
            return null;
        }

        return new DockerVersion(platform, Str(root, "Version") ?? "", api, Str(root, "MinAPIVersion") ?? api, Str(root, "Os") ?? "", Str(root, "Arch") ?? "", Str(root, "KernelVersion"));
    }

    /// <summary><c>GET /info</c>'s answer, or null.</summary>
    public static DockerInfo? Info(string json)
    {
        using var doc = TryParse(json);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var r = doc.RootElement;
        return new DockerInfo(Int(r, "Containers") ?? 0, Int(r, "ContainersRunning") ?? 0, Int(r, "ContainersPaused") ?? 0, Int(r, "ContainersStopped") ?? 0, Int(r, "Images") ?? 0, Int(r, "NCPU") ?? 0, Long(r, "MemTotal") ?? 0, Str(r, "OperatingSystem"), Str(r, "Name"));
    }

    /// <summary><c>GET /system/df</c>'s answer summed per kind, or null.</summary>
    public static DockerDiskUsage? DiskUsage(string json)
    {
        using var doc = TryParse(json);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var r = doc.RootElement;
        var images = Items(r, "Images");
        var containers = Items(r, "Containers");
        var volumes = Items(r, "Volumes");
        var cache = Items(r, "BuildCache");

        var imageLine = new DockerDiskLine(
            images.Count,
            images.Count(i => (Long(i, "Containers") ?? 0) > 0),
            r.TryGetProperty("LayersSize", out var layers) && layers.TryGetInt64(out long total) ? total : images.Sum(i => Long(i, "Size") ?? 0),
            images.Where(i => (Long(i, "Containers") ?? 0) <= 0).Sum(i => Math.Max(0, (Long(i, "Size") ?? 0) - Math.Max(0, Long(i, "SharedSize") ?? 0))));
        var containerLine = new DockerDiskLine(
            containers.Count,
            containers.Count(c => string.Equals(Str(c, "State"), "running", StringComparison.OrdinalIgnoreCase)),
            containers.Sum(c => Long(c, "SizeRw") ?? 0),
            containers.Where(c => !string.Equals(Str(c, "State"), "running", StringComparison.OrdinalIgnoreCase)).Sum(c => Long(c, "SizeRw") ?? 0));
        var volumeLine = new DockerDiskLine(
            volumes.Count,
            volumes.Count(v => VolumeRefs(v) > 0),
            volumes.Sum(v => Math.Max(0, VolumeSize(v))),
            volumes.Where(v => VolumeRefs(v) <= 0).Sum(v => Math.Max(0, VolumeSize(v))));
        var cacheLine = new DockerDiskLine(
            cache.Count,
            cache.Count(c => Bool(c, "InUse") == true),
            cache.Sum(c => Long(c, "Size") ?? 0),
            cache.Where(c => Bool(c, "InUse") != true && Bool(c, "Shared") != true).Sum(c => Long(c, "Size") ?? 0));
        return new DockerDiskUsage(imageLine, containerLine, volumeLine, cacheLine);
    }

    private static long VolumeSize(JsonElement volume) =>
        volume.TryGetProperty("UsageData", out var usage) && usage.ValueKind == JsonValueKind.Object ? Long(usage, "Size") ?? 0 : 0;

    private static long VolumeRefs(JsonElement volume) =>
        volume.TryGetProperty("UsageData", out var usage) && usage.ValueKind == JsonValueKind.Object ? Long(usage, "RefCount") ?? 0 : 0;

    /// <summary>
    /// The engine's own error sentence from a failed answer's body (<c>{"message": "…"}</c>), or the body itself, trimmed;
    /// empty for none. Pure.
    /// </summary>
    public static string ErrorMessage(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        using var doc = TryParse(body);
        if (doc is not null && doc.RootElement.ValueKind == JsonValueKind.Object && Str(doc.RootElement, "message") is { } message)
        {
            return message.Trim();
        }

        return body.Trim();
    }

    /// <summary>
    /// What a prune freed: the deleted items (<c>ContainersDeleted</c>, <c>ImagesDeleted</c> — each an object —,
    /// <c>VolumesDeleted</c>, <c>NetworksDeleted</c>, <c>CachesDeleted</c>) and <c>SpaceReclaimed</c>; null for an
    /// unreadable answer. Pure.
    /// </summary>
    public static (int Deleted, long Reclaimed)? PruneResult(string json)
    {
        using var doc = TryParse(json);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        int deleted = 0;
        foreach (var property in doc.RootElement.EnumerateObject())
        {
            if (property.Name.EndsWith("Deleted", StringComparison.Ordinal) && property.Value.ValueKind == JsonValueKind.Array)
            {
                deleted += property.Value.GetArrayLength();
            }
        }

        return (deleted, Long(doc.RootElement, "SpaceReclaimed") ?? 0);
    }

    /// <summary>
    /// A pull's JSON-lines answer read to its end: the last <c>status</c> line (<c>Status: Downloaded newer image for
    /// nginx:latest</c>), or the error a line carried (<c>errorDetail</c>, or <c>error</c>). Pure.
    /// </summary>
    public static (string? Status, string? Error) PullResult(string lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        string? final = null;
        string? last = null;
        foreach (string line in lines.Split('\n'))
        {
            using var doc = TryParse(line.Trim());
            if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var r = doc.RootElement;
            if (r.TryGetProperty("errorDetail", out var detail) && detail.ValueKind == JsonValueKind.Object && Str(detail, "message") is { } message)
            {
                return (final ?? last, message);
            }

            if (Str(r, "error") is { } error)
            {
                return (final ?? last, error);
            }

            if (Str(r, "status") is { } status)
            {
                // The closing "Status: …" line says what the pull came to; a layer's progress line only what it is doing.
                if (status.StartsWith("Status:", StringComparison.Ordinal))
                {
                    final = status;
                }
                else if (Str(r, "id") is null)
                {
                    last = status;
                }
            }
        }

        return (final ?? last, null);
    }

    /// <summary>The health in a status sentence: <c>(healthy)</c>, <c>(unhealthy)</c>, <c>(health: starting)</c>; null for none. Pure.</summary>
    public static string? HealthOf(string status)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (status.Contains("(unhealthy)", StringComparison.OrdinalIgnoreCase))
        {
            return "unhealthy";
        }

        if (status.Contains("(healthy)", StringComparison.OrdinalIgnoreCase))
        {
            return "healthy";
        }

        return status.Contains("health: starting", StringComparison.OrdinalIgnoreCase) ? "starting" : null;
    }

    /// <summary>A <c>sha256:…</c> id as the CLI shows it: the hash's first twelve characters. Pure.</summary>
    public static string ShortHash(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        string hash = id.StartsWith("sha256:", StringComparison.Ordinal) ? id[7..] : id;
        return hash.Length > 12 ? hash[..12] : hash;
    }

    /// <summary>An API version's two numbers (<c>1.47</c>), or null. Pure.</summary>
    public static (int Major, int Minor)? ApiVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string[] parts = text.Trim().TrimStart('v').Split('.');
        return parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int major)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int minor)
            ? (major, minor)
            : null;
    }

    // ── element readers ─────────────────────────────────────────────────────

    /// <summary>The text parsed, or null for text that is not JSON.</summary>
    public static JsonDocument? TryParse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A string property, or null when missing or not a string.</summary>
    public static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>A whole-number property, or null.</summary>
    public static int? Int(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) ? number : null;

    /// <summary>A 64-bit whole-number property, or null.</summary>
    public static long? Long(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number) ? number : null;

    /// <summary>An unsigned 64-bit property (a cgroup's limits run to 2^64 − 1), or null.</summary>
    public static ulong? ULong(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out ulong number) ? number : null;

    /// <summary>A boolean property, or null.</summary>
    public static bool? Bool(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    /// <summary>An array of strings, empty when missing.</summary>
    public static IReadOnlyList<string> Strings(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString() ?? "").ToList()
            : [];

    /// <summary>A string-to-string object (labels), empty when missing; non-string values are skipped.</summary>
    public static IReadOnlyDictionary<string, string> Labels(JsonElement element, string name)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object)
        {
            foreach (var label in value.EnumerateObject())
            {
                if (label.Value.ValueKind == JsonValueKind.String)
                {
                    labels[label.Name] = label.Value.GetString() ?? "";
                }
            }
        }

        return labels;
    }

    private static List<JsonElement> Items(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.Object).ToList() : [];
}
