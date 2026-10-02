using System.Globalization;
using System.Text;
using System.Text.Json;

namespace NeonSidekick.Docker;

/// <summary>
/// <c>docker_inspect</c>'s answer (2026-10-02): <c>GET /containers/{id}/json</c> as a few lines a model reads at a glance —
/// the state and health, the image, the command, the environment's names, the ports, the mounts, the networks, the
/// restart policy and limits, the compose project and the other labels — never the raw JSON, and every value
/// <see cref="DockerRedaction"/> hides hidden. The storage driver's paths, the log driver's options, the resolv/hosts paths
/// and the exec ids are left out: noise for the model, and the log options can hold a token. Pure.
/// </summary>
public static class DockerInspect
{
    /// <summary>The most labels listed; the rest counted.</summary>
    public const int MaxLabels = 20;

    /// <summary>The most health checks listed, newest last.</summary>
    public const int MaxHealthLogs = 3;

    /// <summary>The summary of an inspect answer, or null for one that cannot be read.</summary>
    public static string? Summary(string json)
    {
        using var doc = DockerJson.TryParse(json);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var r = doc.RootElement;
        var config = Child(r, "Config");
        var state = Child(r, "State");
        var host = Child(r, "HostConfig");
        string name = (DockerJson.Str(r, "Name") ?? "").TrimStart('/');
        string id = DockerJson.Str(r, "Id") ?? "";
        var text = new StringBuilder();

        string status = DockerJson.Str(state, "Status") ?? "unknown";
        var health = Child(state, "Health");
        string? healthStatus = DockerJson.Str(health, "Status");
        text.Append(name).Append(" · ").Append(DockerJson.ShortHash(id)).Append(" · ").Append(status);
        if (healthStatus is not null && healthStatus != "none")
        {
            text.Append(" (").Append(healthStatus).Append(')');
        }

        text.Append('\n');
        text.Append("Image: ").Append(DockerJson.Str(config, "Image") ?? "?").Append(" (").Append(DockerJson.ShortHash(DockerJson.Str(r, "Image") ?? "")).Append(")\n");

        var stateParts = new List<string>(5);
        if (Moment(DockerJson.Str(state, "StartedAt")) is { } started)
        {
            stateParts.Add("started " + started);
        }

        if (!string.Equals(status, "running", StringComparison.OrdinalIgnoreCase) && Moment(DockerJson.Str(state, "FinishedAt")) is { } finished)
        {
            stateParts.Add("finished " + finished);
        }

        stateParts.Add("restarts " + (DockerJson.Long(r, "RestartCount") ?? 0).ToString(CultureInfo.InvariantCulture));
        stateParts.Add("exit code " + (DockerJson.Long(state, "ExitCode") ?? 0).ToString(CultureInfo.InvariantCulture));
        if (DockerJson.Bool(state, "OOMKilled") == true)
        {
            stateParts.Add("killed out of memory");
        }

        if (DockerJson.Str(state, "Error") is { Length: > 0 } error)
        {
            stateParts.Add("error: " + DockerRedaction.Url(error));
        }

        text.Append("State: ").Append(string.Join(" · ", stateParts)).Append('\n');

        if (healthStatus is not null && health.ValueKind == JsonValueKind.Object)
        {
            text.Append("Health: ").Append(healthStatus).Append(" · failing streak ").Append((DockerJson.Long(health, "FailingStreak") ?? 0).ToString(CultureInfo.InvariantCulture));
            if (health.TryGetProperty("Log", out var log) && log.ValueKind == JsonValueKind.Array && log.GetArrayLength() > 0)
            {
                var checks = log.EnumerateArray().TakeLast(MaxHealthLogs).Select(entry =>
                    (Moment(DockerJson.Str(entry, "End")) ?? "?") + " exit " + (DockerJson.Long(entry, "ExitCode") ?? 0).ToString(CultureInfo.InvariantCulture)
                    + (DockerJson.Str(entry, "Output") is { Length: > 0 } output ? " \"" + Clip(DockerRedaction.Line(Single(output)), 200) + "\"" : ""));
                text.Append(" · last checks: ").Append(string.Join("; ", checks));
            }

            text.Append('\n');
        }

        var entrypoint = DockerJson.Strings(config, "Entrypoint");
        var cmd = DockerJson.Strings(config, "Cmd");
        if (entrypoint.Count > 0)
        {
            text.Append("Entrypoint: ").Append(string.Join(' ', DockerRedaction.Args(entrypoint))).Append('\n');
        }

        if (cmd.Count > 0)
        {
            text.Append("Command: ").Append(string.Join(' ', DockerRedaction.Args(cmd))).Append('\n');
        }

        if (Child(config, "Healthcheck") is { ValueKind: JsonValueKind.Object } check && DockerJson.Strings(check, "Test") is { Count: > 0 } test)
        {
            text.Append("Health check: ").Append(string.Join(' ', test.Select(DockerRedaction.Line))).Append('\n');
        }

        var where = new List<string>(2);
        if (DockerJson.Str(config, "WorkingDir") is { Length: > 0 } dir)
        {
            where.Add("working dir " + dir);
        }

        if (DockerJson.Str(config, "User") is { Length: > 0 } user)
        {
            where.Add("user " + user);
        }

        if (where.Count > 0)
        {
            text.Append(char.ToUpperInvariant(where[0][0])).Append(string.Join(" · ", where)[1..]).Append('\n');
        }

        var env = DockerJson.Strings(config, "Env");
        if (env.Count > 0)
        {
            text.Append("Env (values redacted): ").Append(string.Join(", ", env.Select(DockerRedaction.Env))).Append('\n');
        }

        if (Ports(Child(Child(r, "NetworkSettings"), "Ports")) is { Length: > 0 } ports)
        {
            text.Append("Ports: ").Append(ports).Append('\n');
        }

        if (r.TryGetProperty("Mounts", out var mounts) && mounts.ValueKind == JsonValueKind.Array && mounts.GetArrayLength() > 0)
        {
            var lines = mounts.EnumerateArray().Select(m =>
            {
                string type = DockerJson.Str(m, "Type") ?? "?";
                string source = type == "volume" ? ShortVolume(DockerJson.Str(m, "Name") ?? "?") : DockerJson.Str(m, "Source") ?? "?";
                return type + " " + source + " → " + (DockerJson.Str(m, "Destination") ?? "?") + (DockerJson.Bool(m, "RW") == false ? " (ro)" : " (rw)");
            });
            text.Append("Mounts: ").Append(string.Join("; ", lines)).Append('\n');
        }

        if (Child(Child(r, "NetworkSettings"), "Networks") is { ValueKind: JsonValueKind.Object } networks)
        {
            var nets = networks.EnumerateObject().Select(n => n.Name + (DockerJson.Str(n.Value, "IPAddress") is { Length: > 0 } ip ? " " + ip : "")).ToList();
            if (nets.Count > 0)
            {
                text.Append("Networks: ").Append(string.Join(", ", nets)).Append('\n');
            }
        }

        var limits = new List<string>(4);
        if (Child(host, "RestartPolicy") is { ValueKind: JsonValueKind.Object } restart && DockerJson.Str(restart, "Name") is { Length: > 0 } policy)
        {
            limits.Add("restart " + policy);
        }

        long memory = DockerJson.Long(host, "Memory") ?? 0;
        limits.Add("memory " + (memory > 0 ? Files.FileText.Size(memory) : "no limit"));
        long nanoCpus = DockerJson.Long(host, "NanoCpus") ?? 0;
        limits.Add("cpus " + (nanoCpus > 0 ? (nanoCpus / 1e9).ToString("0.##", CultureInfo.InvariantCulture) : "no limit"));
        if (DockerJson.Bool(host, "Privileged") == true)
        {
            limits.Add("privileged");
        }

        text.Append("Policy: ").Append(string.Join(" · ", limits)).Append('\n');

        var labels = DockerJson.Labels(config, "Labels");
        if (labels.TryGetValue(DockerContainer.ProjectLabel, out var project))
        {
            text.Append("Compose: project ").Append(project);
            if (labels.TryGetValue(DockerContainer.ServiceLabel, out var service))
            {
                text.Append(", service ").Append(service);
            }

            if (labels.TryGetValue(DockerContainer.WorkingDirLabel, out var workingDir))
            {
                text.Append(", in ").Append(workingDir);
            }

            text.Append('\n');
        }

        var other = labels.Where(l => !l.Key.StartsWith("com.docker.compose.", StringComparison.Ordinal) && !l.Key.StartsWith("desktop.docker.io/", StringComparison.Ordinal))
            .OrderBy(l => l.Key, StringComparer.Ordinal).ToList();
        if (other.Count > 0)
        {
            text.Append("Labels: ").Append(string.Join(", ", other.Take(MaxLabels).Select(l => l.Key + "=" + Clip(DockerRedaction.Label(l.Key, l.Value), 120))));
            if (other.Count > MaxLabels)
            {
                text.Append(" (and ").Append((other.Count - MaxLabels).ToString(CultureInfo.InvariantCulture)).Append(" more)");
            }

            text.Append('\n');
        }

        return text.ToString().TrimEnd('\n');
    }

    /// <summary>The published ports of an inspect's <c>NetworkSettings.Ports</c>: <c>3306/tcp → 0.0.0.0:3306, [::]:3306</c>; an exposed one alone.</summary>
    private static string Ports(JsonElement ports)
    {
        if (ports.ValueKind != JsonValueKind.Object)
        {
            return "";
        }

        var parts = new List<string>();
        foreach (var port in ports.EnumerateObject())
        {
            if (port.Value.ValueKind != JsonValueKind.Array || port.Value.GetArrayLength() == 0)
            {
                parts.Add(port.Name + " (not published)");
                continue;
            }

            var bindings = port.Value.EnumerateArray().Select(b =>
            {
                string ip = DockerJson.Str(b, "HostIp") ?? "";
                string hostPort = DockerJson.Str(b, "HostPort") ?? "?";
                return (ip.Contains(':', StringComparison.Ordinal) ? "[" + ip + "]" : ip.Length == 0 ? "0.0.0.0" : ip) + ":" + hostPort;
            });
            parts.Add(port.Name + " → " + string.Join(", ", bindings));
        }

        return string.Join("; ", parts);
    }

    /// <summary>A moment of the engine's (<c>2026-09-30T22:52:06.123456789Z</c>) as <c>2026-09-30 22:52:06 UTC</c>; null for none or the zero time.</summary>
    public static string? Moment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith("0001-", StringComparison.Ordinal))
        {
            return null;
        }

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var moment)
            ? moment.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC"
            : value;
    }

    /// <summary>An anonymous volume's hash cut to twelve, a named volume as it is.</summary>
    public static string ShortVolume(string name) => name.Length == 64 && name.All(char.IsAsciiHexDigitLower) ? name[..12] : name;

    private static JsonElement Child(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var child) ? child : default;

    private static string Single(string text) => string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()));

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
