namespace NeonSidekick.Docker;

/// <summary>What a name came to: the containers it means, or the sentence that says why none (<see cref="Error"/>).</summary>
public sealed record DockerMatch(IReadOnlyList<DockerContainer> Containers, string? Error)
{
    public static DockerMatch Of(params DockerContainer[] containers) => new(containers, null);

    public static DockerMatch Refused(string error) => new([], error);
}

/// <summary>
/// A container named the way a person or a model names one (2026-10-02): the exact name (the engine's leading <c>/</c>
/// or not, case ignored), the exact id, a unique id prefix of at least <see cref="MinIdPrefix"/> characters, then a unique
/// part of a name — "mysql" is <c>mysql_dev</c> while nothing else holds it. Two candidates are an ambiguity listing them,
/// none a refusal listing what is there; nothing is acted on by a guess. A compose project resolves by its label. Pure.
/// </summary>
public static class DockerTargets
{
    /// <summary>The shortest id prefix taken for an id.</summary>
    public const int MinIdPrefix = 4;

    /// <summary>The most names a refusal lists.</summary>
    public const int MaxNamesListed = 10;

    /// <summary>The one container <paramref name="target"/> means among <paramref name="all"/>, or the refusal.</summary>
    public static DockerMatch Resolve(IReadOnlyList<DockerContainer> all, string target)
    {
        ArgumentNullException.ThrowIfNull(all);
        ArgumentNullException.ThrowIfNull(target);
        string wanted = target.Trim().TrimStart('/');
        if (wanted.Length == 0)
        {
            return DockerMatch.Refused(DockerText.Missing("container"));
        }

        var exact = all.Where(c => string.Equals(c.Name, wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1)
        {
            return DockerMatch.Of(exact[0]);
        }

        var byId = all.Where(c => string.Equals(c.Id, wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byId.Count == 1)
        {
            return DockerMatch.Of(byId[0]);
        }

        if (wanted.Length >= MinIdPrefix && wanted.All(char.IsAsciiHexDigit))
        {
            var prefixed = all.Where(c => c.Id.StartsWith(wanted, StringComparison.OrdinalIgnoreCase)).ToList();
            if (prefixed.Count == 1)
            {
                return DockerMatch.Of(prefixed[0]);
            }

            if (prefixed.Count > 1)
            {
                return DockerMatch.Refused(DockerText.Ambiguous(target, prefixed.Select(c => c.Name).ToList()));
            }
        }

        var partial = all.Where(c => c.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        if (partial.Count == 1)
        {
            return DockerMatch.Of(partial[0]);
        }

        if (partial.Count > 1)
        {
            return DockerMatch.Refused(DockerText.Ambiguous(target, partial.Select(c => c.Name).ToList()));
        }

        return DockerMatch.Refused(DockerText.NotFound("container", target, all.Select(c => c.Name).Take(MaxNamesListed).ToList()));
    }

    /// <summary>The containers of compose project <paramref name="project"/> (its label, case ignored), or the refusal listing the projects there are.</summary>
    public static DockerMatch Project(IReadOnlyList<DockerContainer> all, string project)
    {
        ArgumentNullException.ThrowIfNull(all);
        ArgumentNullException.ThrowIfNull(project);
        string wanted = project.Trim();
        if (wanted.Length == 0)
        {
            return DockerMatch.Refused(DockerText.Missing("project"));
        }

        var members = all.Where(c => string.Equals(c.Project, wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        if (members.Count > 0)
        {
            return new DockerMatch(members, null);
        }

        var projects = all.Select(c => c.Project).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).Take(MaxNamesListed).ToList();
        return DockerMatch.Refused(DockerText.NotFound("compose project", project, projects));
    }
}
