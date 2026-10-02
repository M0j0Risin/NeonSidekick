namespace NeonSidekick.Docker;

/// <summary>A compose project as its containers' labels describe it: the folder it was started from, its files, its containers.</summary>
public sealed record DockerProject(string Name, string? WorkingDir, string? ConfigFiles, IReadOnlyList<DockerContainer> Containers)
{
    /// <summary>How many of its containers run.</summary>
    public int RunningCount => Containers.Count(c => c.Running);
}

/// <summary>
/// The compose projects among the containers (2026-10-02). The Engine API knows no compose: a project is the
/// containers that carry its <c>com.docker.compose.project</c> label, and the order compose would start them in is read
/// from <c>com.docker.compose.depends_on</c> (<c>db:service_started:false,cache:service_healthy:true</c>) — a dependency
/// first, the rest by name; a stop goes the other way. <c>compose up</c> from the files is not here: that needs the CLI, a
/// process this app does not start (the user's call). Pure.
/// </summary>
public static class DockerCompose
{
    /// <summary>The projects, by name; a container without the label is in none.</summary>
    public static IReadOnlyList<DockerProject> Group(IReadOnlyList<DockerContainer> all)
    {
        ArgumentNullException.ThrowIfNull(all);
        return all.Where(c => c.Project is not null)
            .GroupBy(c => c.Project!, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var members = g.OrderBy(c => c.Service ?? c.Name, StringComparer.OrdinalIgnoreCase).ToList();
                string? dir = members.Select(c => c.Labels.GetValueOrDefault(DockerContainer.WorkingDirLabel)).FirstOrDefault(v => !string.IsNullOrEmpty(v));
                string? files = members.Select(c => c.Labels.GetValueOrDefault(DockerContainer.ConfigFilesLabel)).FirstOrDefault(v => !string.IsNullOrEmpty(v));
                return new DockerProject(g.Key, dir, files, members);
            })
            .ToList();
    }

    /// <summary>The services one container's <c>depends_on</c> label names.</summary>
    public static IReadOnlyList<string> DependsOn(DockerContainer container)
    {
        ArgumentNullException.ThrowIfNull(container);
        if (!container.Labels.TryGetValue(DockerContainer.DependsOnLabel, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(entry => entry.Split(':')[0].Trim())
            .Where(service => service.Length > 0)
            .ToList();
    }

    /// <summary>
    /// <paramref name="containers"/> in the order compose would start them: each after the services it depends on, ties by
    /// service name; a cycle (or a dependency outside the list) never stalls it — what is left goes in name order.
    /// </summary>
    public static IReadOnlyList<DockerContainer> StartOrder(IReadOnlyList<DockerContainer> containers)
    {
        ArgumentNullException.ThrowIfNull(containers);
        var pending = containers.OrderBy(c => c.Service ?? c.Name, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var services = new HashSet<string>(pending.Select(c => c.Service ?? c.Name), StringComparer.OrdinalIgnoreCase);
        var started = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var order = new List<DockerContainer>(pending.Count);
        while (pending.Count > 0)
        {
            var next = pending.FirstOrDefault(c => DependsOn(c).All(d => !services.Contains(d) || started.Contains(d))) ?? pending[0];
            pending.Remove(next);
            order.Add(next);
            // A service with replicas is started once its first container is: the others need nothing more.
            started.Add(next.Service ?? next.Name);
        }

        return order;
    }

    /// <summary>The reverse of <see cref="StartOrder"/>: dependents stop before what they depend on.</summary>
    public static IReadOnlyList<DockerContainer> StopOrder(IReadOnlyList<DockerContainer> containers) => StartOrder(containers).Reverse().ToList();
}
