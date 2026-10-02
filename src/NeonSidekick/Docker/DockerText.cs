using System.Globalization;
using NeonSidekick.Files;

namespace NeonSidekick.Docker;

/// <summary>
/// The Docker words (2026-10-02): the tools' results and failures, the container and resource lines, the confirm
/// question, the audit line, the transcript's note and the <c>/docker</c> lines. Every failure starts <c>Error:</c>; a
/// result's first line is its header, which is the transcript's note (<see cref="Note"/>). Pure; pinned by tests.
/// </summary>
public static class DockerText
{
    /// <summary>The log category.</summary>
    public const string Category = "Docker";

    /// <summary>The whale: <c>/docker</c>'s pane title and the help row's mark.</summary>
    public const string Glyph = "🐳";

    /// <summary>What every hidden value reads (<see cref="DockerRedaction"/>). Pinned.</summary>
    public const string Redacted = "<redacted>";

    // ─── failures ───────────────────────────────────────────────────────────────

    /// <summary>Nothing serves the pipe: Docker Desktop is not running (or the pipe setting names another). Pinned.</summary>
    public static string EngineDown(string pipe) => $"Error: Docker Desktop is not running (nothing answers on {pipe}); start Docker Desktop, or check Docker engine pipe on the Docker tab of /tools";

    /// <summary>Windows refused this account the pipe. Pinned.</summary>
    public static string PipeDenied(string pipe) => $"Error: Windows refused access to {pipe}; the account may need to be in the docker-users group";

    /// <summary>Any other transport failure, as it came.</summary>
    public static string Unreachable(string pipe, string detail) => $"Error: cannot reach the Docker engine on {pipe}: {detail.Trim()}";

    /// <summary>No answer in time.</summary>
    public static string NoAnswer(string timeout) => $"Error: the Docker engine did not answer within {timeout}";

    /// <summary>An engine older than the API the tools need.</summary>
    public static string TooOld(string apiVersion) => $"Error: the Docker engine speaks API {apiVersion}; the Docker tools need {DockerClient.MinimumApiVersion} or later (Docker 20.10+)";

    /// <summary>A non-2xx answer: the status, the endpoint and the engine's own words, cut short.</summary>
    public static string HttpError(int status, string endpoint, string message)
    {
        string said = Clip(Single(message), 300);
        return $"Error: the Docker engine answered {status.ToString(CultureInfo.InvariantCulture)} to {endpoint}" + (said.Length > 0 ? ": " + said : "");
    }

    /// <summary>An answer that could not be read.</summary>
    public static string BadAnswer(string endpoint) => $"Error: the Docker engine's answer to {endpoint} could not be read";

    /// <summary>The tools off Windows: the pipe is Docker Desktop's on Windows. Pinned.</summary>
    public const string NotWindows = "Error: the Docker tools reach Docker Desktop through its Windows named pipe; this system has none";

    /// <summary><c>Docker writes</c> is off. Pinned.</summary>
    public const string WritesOff = "Error: Docker writes is off, so the model may only look; the user can act with /docker, or switch Docker writes on in /tools";

    /// <summary>The user said no on the pane. Pinned.</summary>
    public const string Declined = "Error: the user declined this Docker action; do not retry it unless they ask";

    /// <summary>The action needed the user's yes and nothing could ask (headless, no pane). Pinned.</summary>
    public const string NotAsked = "Error: this Docker action needs the user's yes and nobody could be asked; the user can run it with /docker";

    /// <summary>A name that matched nothing, with what is there.</summary>
    public static string NotFound(string what, string target, IReadOnlyList<string> near)
    {
        ArgumentNullException.ThrowIfNull(near);
        string tail = near.Count == 0 ? $"; there is no {what} at all" : "; there are: " + string.Join(", ", near);
        return $"Error: no {what} named '{target.Trim()}'" + tail;
    }

    /// <summary>A name that matched several.</summary>
    public static string Ambiguous(string target, IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        return $"Error: '{target.Trim()}' could be {names.Count.ToString(CultureInfo.InvariantCulture)} containers; name one: " + string.Join(", ", names.Take(DockerTargets.MaxNamesListed))
            + (names.Count > DockerTargets.MaxNamesListed ? " …" : "");
    }

    /// <summary>A required argument left out.</summary>
    public static string Missing(string argument) => $"Error: give \"{argument}\"";

    /// <summary>An argument outside what the tool takes.</summary>
    public static string BadChoice(string argument, string given, IEnumerable<string> choices) =>
        $"Error: '{given.Trim()}' is not a valid {argument}; use one of: " + string.Join(", ", choices);

    /// <summary>A number argument out of its range.</summary>
    public static string OutOfRange(string argument, string given, int min, int max) =>
        $"Error: '{given.Trim()}' is not a whole number from {min.ToString(CultureInfo.InvariantCulture)} to {max.ToString(CultureInfo.InvariantCulture)} for '{argument}'";

    /// <summary>A <c>since</c> that is neither an age nor a moment.</summary>
    public static string BadSince(string given) => $"Error: '{given.Trim()}' is not a time; give an age such as 10m, 2h or 1d, or a moment such as 2026-10-02T14:00:00Z";

    /// <summary>A boolean argument that is not one.</summary>
    public static string NotBoolean(string argument, string given) => $"Error: '{given.Trim()}' is not true or false for '{argument}'";

    // ─── the confirm pane and the audit ─────────────────────────────────────────

    /// <summary>The question before a model's write: what is done, to what. Pinned.</summary>
    public static string ConfirmQuestion(string act) => $"Let the model {act}?";

    /// <summary>A lifecycle act in words: <c>stop container mysql_dev (mysql:8.4, Up 3 days)</c>, <c>restart compose project web: api, db</c>.</summary>
    public static string LifecycleAct(string action, IReadOnlyList<DockerContainer> targets, string? project)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (project is not null)
        {
            return $"{action} compose project {project}: " + Names(targets);
        }

        var c = targets[0];
        return $"{action} container {c.Name} ({c.Image}, {c.Status})";
    }

    /// <summary>The log line every change carries (<see cref="Diagnostics.DiagnosticLog"/>, Info): who asked, the act and how it ended.</summary>
    public static string AuditLine(string who, string act, string outcome) => $"{who}: {act} — {outcome}";

    /// <summary>The audit's word for the model's act.</summary>
    public const string ByModel = "model";

    /// <summary>The audit's word for the user's own (<c>/docker</c>, its pane).</summary>
    public const string ByUser = "user";

    // ─── results ────────────────────────────────────────────────────────────────

    /// <summary>The transcript's one-line note for a result: its first line.</summary>
    public static string Note(string result)
    {
        ArgumentNullException.ThrowIfNull(result);
        int newline = result.IndexOf('\n', StringComparison.Ordinal);
        return newline < 0 ? result : result[..newline];
    }

    /// <summary>A pull's act in words, for the question: <c>pull image nginx:latest</c>.</summary>
    public static string PullAct(string reference) => "pull image " + reference;

    /// <summary>A remove's act in words: <c>remove volume pgdata</c>, <c>remove container web (nginx, Up 2 hours), forced</c>.</summary>
    public static string RemoveAct(string kind, string what, bool force) => $"remove {kind} {what}" + (force ? ", forced" : "");

    /// <summary>A prune's act in words, with what it would take when that could be read: <c>prune volumes (named ones too): 3 unused volumes, up to 1.2 GB</c>.</summary>
    public static string PruneAct(string kind, bool all, string preview)
    {
        string scope = (kind, all) switch
        {
            ("images", true) => " (every unused image)",
            ("volumes", true) => " (named ones too)",
            ("build_cache", true) => " (all of it)",
            _ => "",
        };
        return "prune " + kind.Replace('_', ' ') + scope + (preview.Length > 0 ? ": " + preview : "");
    }

    /// <summary>What a prune would take, from the disk use: the counts and sizes <c>/system/df</c> gives.</summary>
    public static string PrunePreview(string kind, bool all, DockerDiskUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        return kind switch
        {
            "images" when !all => "untagged images, up to " + FileText.Size(usage.Images.Reclaimable),
            "images" => Count(usage.Images.Count - usage.Images.Active, "unused image") + ", about " + FileText.Size(usage.Images.Reclaimable),
            "volumes" => (all ? Count(usage.Volumes.Count - usage.Volumes.Active, "unused volume") : "unused anonymous volumes") + ", up to " + FileText.Size(usage.Volumes.Reclaimable),
            _ => "about " + FileText.Size(usage.BuildCache.Reclaimable) + " of build cache",
        };
    }

    /// <summary><c>3 stopped containers</c>, <c>1 unused network</c>.</summary>
    public static string Count(int n, string noun) => n.ToString(CultureInfo.InvariantCulture) + " " + noun + (n == 1 ? "" : "s");

    /// <summary>A pull done: <c>Docker: pulled nginx:latest · Status: Image is up to date for nginx:latest</c>.</summary>
    public static string Pulled(string reference, string? status) => $"Docker: pulled {reference}" + (string.IsNullOrWhiteSpace(status) ? "" : " · " + status.Trim());

    /// <summary>A pull the registry or the engine refused, its words.</summary>
    public static string PullFailed(string reference, string error) => $"Error: the pull of {reference} failed: {Clip(Single(error), 300)}";

    /// <summary>A remove done: <c>Docker: removed volume pgdata</c>.</summary>
    public static string Removed(string kind, string name) => $"Docker: removed {kind} {name}";

    /// <summary>A prune done: <c>Docker: pruned volumes · 3 removed · 1.2 GB reclaimed</c>.</summary>
    public static string Pruned(string kind, int deleted, long reclaimed) =>
        $"Docker: pruned {kind.Replace('_', ' ')} · {deleted.ToString(CultureInfo.InvariantCulture)} removed · {FileText.Size(reclaimed)} reclaimed";

    /// <summary>The past tense of a lifecycle action: <c>started</c>, <c>stopped</c>, <c>restarted</c>, <c>paused</c>, <c>unpaused</c>.</summary>
    public static string Past(string action) => action switch
    {
        "stop" => "stopped",
        "start" => "started",
        "restart" => "restarted",
        "pause" => "paused",
        "unpause" => "unpaused",
        "kill" => "killed",
        _ => action + "ed",
    };

    /// <summary>One container's act done: <c>stopped mysql_dev</c>, or <c>mysql_dev was already stopped</c> on a 304.</summary>
    public static string Done(string action, DockerContainer container, bool already) =>
        already ? $"{container.Name} was already {Past(action)}" : $"{Past(action)} {container.Name}";

    /// <summary>The header over a lifecycle act's lines: <c>Docker: stop · 2 of 2 containers done</c>.</summary>
    public static string ActHeader(string action, int done, int total) =>
        $"Docker: {action} · {done.ToString(CultureInfo.InvariantCulture)} of {total.ToString(CultureInfo.InvariantCulture)} {(total == 1 ? "container" : "containers")} done";

    /// <summary>
    /// A container as one plain line: <c>mysql_dev · running (healthy) · Up 40 hours · mysql:8.4 · 3306→3306/tcp · compose
    /// web/db · 2dc6d221ea8a</c>.
    /// </summary>
    public static string ContainerLine(DockerContainer container)
    {
        ArgumentNullException.ThrowIfNull(container);
        var parts = new List<string>(7)
        {
            container.Name,
            container.State + (container.Health is { } health ? " (" + health + ")" : ""),
            container.Status,
            container.Image,
        };
        if (Ports(container.Ports) is { Length: > 0 } ports)
        {
            parts.Add(ports);
        }

        if (container.Project is { } project)
        {
            parts.Add("compose " + project + (container.Service is { } service ? "/" + service : ""));
        }

        parts.Add(container.ShortId);
        return string.Join(" · ", parts);
    }

    /// <summary>
    /// A container's ports: <c>3307→3306/tcp</c>, the IPv4 and IPv6 rows of one binding as one, a host address other
    /// than the wildcards kept (<c>127.0.0.1:8080→80/tcp</c>), an exposed port alone (<c>5432/tcp</c>). Pure.
    /// </summary>
    public static string Ports(IReadOnlyList<DockerPort> ports)
    {
        ArgumentNullException.ThrowIfNull(ports);
        return string.Join(", ", ports
            .Select(p => p.Public is { } host
                ? (p.Ip is null or "" or "0.0.0.0" or "::" ? "" : (p.Ip.Contains(':', StringComparison.Ordinal) ? "[" + p.Ip + "]" : p.Ip) + ":") + host.ToString(CultureInfo.InvariantCulture) + "→" + p.Private.ToString(CultureInfo.InvariantCulture) + "/" + p.Type
                : p.Private.ToString(CultureInfo.InvariantCulture) + "/" + p.Type)
            .Distinct(StringComparer.Ordinal));
    }

    /// <summary>The containers' header: <c>Docker: 5 containers (4 running, 1 exited)</c>, with the filters said.</summary>
    public static string ContainersHeader(IReadOnlyList<DockerContainer> shown, string filters)
    {
        ArgumentNullException.ThrowIfNull(shown);
        var states = shown.GroupBy(c => c.State.Length == 0 ? "unknown" : c.State, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => string.Equals(g.Key, "running", StringComparison.OrdinalIgnoreCase)).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.Count().ToString(CultureInfo.InvariantCulture) + " " + g.Key);
        string count = shown.Count.ToString(CultureInfo.InvariantCulture) + (shown.Count == 1 ? " container" : " containers");
        string by = filters.Length > 0 ? " matching " + filters : "";
        return shown.Count == 0 ? $"Docker: no containers{by}" : $"Docker: {count}{by} ({string.Join(", ", states)})";
    }

    /// <summary>The order the lists show containers in: running first, then by name.</summary>
    public static IReadOnlyList<DockerContainer> Ordered(IEnumerable<DockerContainer> containers) =>
        containers.OrderByDescending(c => c.Running).ThenByDescending(c => c.Paused).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The log's header: <c>Docker logs: mysql_dev · last 100 lines</c>, the grep and the cap said.</summary>
    public static string LogsHeader(string name, int shown, int? tail, string? grep, bool capped)
    {
        string what = grep is not null
            ? $"{shown.ToString(CultureInfo.InvariantCulture)} {(shown == 1 ? "line" : "lines")} matching '{grep}'"
            : tail is null ? $"{shown.ToString(CultureInfo.InvariantCulture)} {(shown == 1 ? "line" : "lines")}" : $"last {shown.ToString(CultureInfo.InvariantCulture)} {(shown == 1 ? "line" : "lines")}";
        return $"Docker logs: {name} · {what}" + (capped ? " (the read stopped at " + FileText.Size(DockerLogStream.MaxBytes) + ")" : "");
    }

    /// <summary>A log with nothing in it.</summary>
    public static string NoLogLines(string name) => $"Docker logs: {name} · no lines";

    /// <summary>The note after logs cut to the character budget.</summary>
    public static string LogsCut(int dropped) => $"(… {dropped.ToString(CultureInfo.InvariantCulture)} earlier lines cut to fit)";

    /// <summary>One container's stats: <c>mysql_dev · CPU 0.3% · memory 455.1 MB / 52.6 GB (0.9%) · net 215.2 KB in / 192.4 KB out · disk 140.2 MB read / 304.1 MB written · 50 pids</c>.</summary>
    public static string StatsLine(string name, DockerStatsSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        string pids = sample.Pids is { } p ? " · " + p.ToString(CultureInfo.InvariantCulture) + (p == 1 ? " pid" : " pids") : "";
        return $"{name} · CPU {Percent(sample.CpuPercent)} · memory {FileText.Size(sample.MemoryUsed)} / {FileText.Size(sample.MemoryLimit)} ({Percent(sample.MemoryPercent)}) · net {FileText.Size(sample.NetRx)} in / {FileText.Size(sample.NetTx)} out · disk {FileText.Size(sample.BlockRead)} read / {FileText.Size(sample.BlockWrite)} written{pids}";
    }

    /// <summary>The stats' header: <c>Docker stats: 4 running containers</c>.</summary>
    public static string StatsHeader(int count) => count == 0 ? "Docker stats: no container is running" : $"Docker stats: {count.ToString(CultureInfo.InvariantCulture)} running {(count == 1 ? "container" : "containers")}";

    /// <summary>Stats asked of a container that does not run.</summary>
    public static string NotRunning(DockerContainer container)
    {
        ArgumentNullException.ThrowIfNull(container);
        return $"Docker stats: {container.Name} is {container.State}, so it uses nothing";
    }

    /// <summary>A stats read that failed for one container, among the others.</summary>
    public static string StatsFailed(string name, string error) => $"{name} · {error}";

    /// <summary>A percentage: <c>0.3%</c>, <c>12%</c>, <c>250%</c>.</summary>
    public static string Percent(double value) => value.ToString(value < 10 ? "0.#" : "0", CultureInfo.InvariantCulture) + "%";

    /// <summary>An image line: <c>mysql:8.4 · 6ea90827b110 · 812.3 MB · 3 weeks ago · used by mysql_dev</c>.</summary>
    public static string ImageLine(DockerImage image, IReadOnlyList<string> usedBy, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(usedBy);
        string tags = image.Tags.Count == 0 ? "<none> (dangling)" : string.Join(", ", image.Tags);
        string use = usedBy.Count == 0 ? "unused" : "used by " + string.Join(", ", usedBy);
        return $"{tags} · {image.ShortId} · {FileText.Size(image.Size)} · {Age(image.Created, now)} · {use}";
    }

    /// <summary>A volume line: <c>pgdata · local · used by db</c>.</summary>
    public static string VolumeLine(DockerVolume volume, IReadOnlyList<string> usedBy)
    {
        ArgumentNullException.ThrowIfNull(volume);
        ArgumentNullException.ThrowIfNull(usedBy);
        string use = usedBy.Count == 0 ? "unused" : "used by " + string.Join(", ", usedBy);
        return $"{DockerInspect.ShortVolume(volume.Name)}{(volume.Anonymous ? " (anonymous)" : "")} · {volume.Driver} · {use}";
    }

    /// <summary>A network line: <c>bridge · bridge · local · 172.17.0.0/16 · 4 containers</c>.</summary>
    public static string NetworkLine(DockerNetwork network, IReadOnlyList<string> members)
    {
        ArgumentNullException.ThrowIfNull(network);
        ArgumentNullException.ThrowIfNull(members);
        string subnets = network.Subnets.Count == 0 ? "" : " · " + string.Join(", ", network.Subnets);
        string who = members.Count == 0 ? "no containers" : string.Join(", ", members);
        return $"{network.Name} · {network.Driver} · {network.Scope}{subnets}{(network.Internal ? " · internal" : "")} · {who}";
    }

    /// <summary>A resource list's header: <c>Docker images: 12 (3 unused)</c>.</summary>
    public static string ResourceHeader(string kind, int count, int shown, string filters, int? unused = null)
    {
        string by = filters.Length > 0 ? " matching " + filters : "";
        string more = shown < count ? $", {shown.ToString(CultureInfo.InvariantCulture)} shown" : "";
        string idle = unused is { } u ? $" ({u.ToString(CultureInfo.InvariantCulture)} unused{more})" : more.Length > 0 ? $" ({more[2..]})" : "";
        return $"Docker {kind}: {count.ToString(CultureInfo.InvariantCulture)}{by}{idle}";
    }

    /// <summary>The disk use, a line per kind, as <c>docker system df</c> shows it.</summary>
    public static IReadOnlyList<string> DiskLines(DockerDiskUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        return
        [
            "Docker disk use: " + FileText.Size(usage.Images.Size + usage.Containers.Size + usage.Volumes.Size + usage.BuildCache.Size) + " in all, " + FileText.Size(usage.Images.Reclaimable + usage.Containers.Reclaimable + usage.Volumes.Reclaimable + usage.BuildCache.Reclaimable) + " reclaimable",
            DiskLine("images", usage.Images),
            DiskLine("containers", usage.Containers),
            DiskLine("volumes", usage.Volumes),
            DiskLine("build cache", usage.BuildCache),
        ];
    }

    private static string DiskLine(string kind, DockerDiskLine line) =>
        $"{kind} · {line.Count.ToString(CultureInfo.InvariantCulture)} ({line.Active.ToString(CultureInfo.InvariantCulture)} in use) · {FileText.Size(line.Size)} · {FileText.Size(line.Reclaimable)} reclaimable";

    /// <summary>A compose project's lines: its head, then a line per container.</summary>
    public static IReadOnlyList<string> ProjectLines(DockerProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var lines = new List<string>(project.Containers.Count + 1)
        {
            $"{project.Name} · {project.RunningCount.ToString(CultureInfo.InvariantCulture)} of {project.Containers.Count.ToString(CultureInfo.InvariantCulture)} running"
                + (project.WorkingDir is { } dir ? " · in " + dir : "") + (project.ConfigFiles is { } files ? " · " + files : ""),
        };
        lines.AddRange(project.Containers.Select(c => "  " + (c.Service ?? c.Name) + " · " + c.Name + " · " + c.State + (c.Health is { } h ? " (" + h + ")" : "") + " · " + c.Image + (Ports(c.Ports) is { Length: > 0 } p ? " · " + p : "")));
        return lines;
    }

    /// <summary>The compose header: <c>Docker compose: 2 projects</c>.</summary>
    public static string ProjectsHeader(int count) => count == 0
        ? "Docker compose: no compose projects (no container carries a com.docker.compose.project label)"
        : $"Docker compose: {count.ToString(CultureInfo.InvariantCulture)} {(count == 1 ? "project" : "projects")}";

    /// <summary>The engine as <c>/docker status</c> and <c>--docker-check</c> say it: Desktop and the engine, the machine, the counts.</summary>
    public static IReadOnlyList<string> Status(string pipe, DockerVersion version, DockerInfo? info, string prefix)
    {
        ArgumentNullException.ThrowIfNull(version);
        var lines = new List<string>(3)
        {
            (version.Platform ?? "Docker engine") + " · engine " + version.Version + " · API " + version.ApiVersion + " (asking " + prefix.TrimStart('/') + ") · " + version.Os + "/" + version.Arch + " · " + pipe,
        };
        if (info is not null)
        {
            lines.Add($"Containers: {info.Running.ToString(CultureInfo.InvariantCulture)} running, {info.Paused.ToString(CultureInfo.InvariantCulture)} paused, {info.Stopped.ToString(CultureInfo.InvariantCulture)} stopped · {info.Images.ToString(CultureInfo.InvariantCulture)} images");
            lines.Add($"Engine machine: {info.Cpus.ToString(CultureInfo.InvariantCulture)} CPUs · {FileText.Size(info.Memory)} memory" + (info.OperatingSystem is { } os ? " · " + os : ""));
        }

        return lines;
    }

    /// <summary>How long ago a unix time was: <c>3 weeks ago</c>.</summary>
    public static string Age(long unixSeconds, DateTimeOffset now)
    {
        var age = now - DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        if (age < TimeSpan.Zero)
        {
            return "just now";
        }

        return age.TotalDays switch
        {
            >= 730 => Plural((int)(age.TotalDays / 365), "year") + " ago",
            >= 60 => Plural((int)(age.TotalDays / 30), "month") + " ago",
            >= 14 => Plural((int)(age.TotalDays / 7), "week") + " ago",
            >= 2 => Plural((int)age.TotalDays, "day") + " ago",
            _ => age.TotalHours >= 2 ? Plural((int)age.TotalHours, "hour") + " ago" : age.TotalMinutes >= 2 ? Plural((int)age.TotalMinutes, "minute") + " ago" : "just now",
        };
    }

    private static string Plural(int n, string word) => n.ToString(CultureInfo.InvariantCulture) + " " + word + (n == 1 ? "" : "s");

    /// <summary>The names of containers, joined; a long list cut with a count.</summary>
    public static string Names(IReadOnlyList<DockerContainer> containers)
    {
        ArgumentNullException.ThrowIfNull(containers);
        const int shown = 8;
        string head = string.Join(", ", containers.Take(shown).Select(c => c.Name));
        return containers.Count > shown ? head + $" and {(containers.Count - shown).ToString(CultureInfo.InvariantCulture)} more" : head;
    }

    // ─── /docker ────────────────────────────────────────────────────────────────

    /// <summary>The spinner's word while <c>/docker</c> waits on the engine.</summary>
    public const string Working = "asking Docker";

    /// <summary><c>/docker</c>'s words, in completion order.</summary>
    public static readonly IReadOnlyList<string> Verbs = ["ps", "status", "logs", "stats", "start", "stop", "restart", "pause", "unpause"];

    /// <summary>The verbs that change a container.</summary>
    public static readonly IReadOnlyList<string> ActionVerbs = ["start", "stop", "restart", "pause", "unpause"];

    /// <summary>What a line <c>/docker</c> cannot read gets. Pinned.</summary>
    public const string Usage = "Usage: /docker [ps | status | logs <container> [lines] | stats [container] | start|stop|restart|pause|unpause <container>]";

    /// <summary>A verb's note in <c>/docker</c>'s list.</summary>
    public static string VerbNote(string verb) => verb switch
    {
        "ps" => "list the containers",
        "status" => "Docker Desktop, the engine and the counts",
        "logs" => "a container's last log lines",
        "stats" => "CPU, memory, network and disk of the running containers",
        "start" => "start a stopped container",
        "stop" => "stop a running container",
        "restart" => "restart a container",
        "pause" => "pause a running container",
        "unpause" => "resume a paused container",
        _ => "",
    };

    // ─── helpers ────────────────────────────────────────────────────────────────

    /// <summary><paramref name="text"/> on one line, runs of whitespace one space.</summary>
    public static string Single(string text) => string.Join(' ', (text ?? "").Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));

    /// <summary><paramref name="text"/> cut to <paramref name="max"/> characters with an ellipsis.</summary>
    public static string Clip(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
