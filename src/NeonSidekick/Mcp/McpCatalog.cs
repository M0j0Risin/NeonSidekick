namespace NeonSidekick.Mcp;

/// <summary>Where a server was configured: the profile's <c>mcp.json</c> or the home's.</summary>
public enum McpScope
{
    Profile,
    Global,
}

/// <summary>
/// One configured server after the merge: its name, its config, the file it came from and — for a
/// global server the profile's file also names — the scope that shadows it (never started, shown dim).
/// </summary>
public sealed record McpServerEntry(string Name, McpServerConfig Config, McpScope Scope, McpScope? ShadowedBy = null)
{
    /// <summary>Whether this entry can be started at all: not shadowed by the profile's own.</summary>
    public bool Startable => ShadowedBy is null;
}

/// <summary>The two files merged: the entries in order (the profile's, then the home's) and every problem either had.</summary>
public sealed record McpMerged(IReadOnlyList<McpServerEntry> Entries, IReadOnlyList<McpConfigProblem> Problems)
{
    public static readonly McpMerged Empty = new([], []);

    /// <summary>Whether either file named a server or had a problem worth showing: what <c>/mcp</c> lists and what the startup connect considers.</summary>
    public bool Configured => Entries.Count > 0 || Problems.Count > 0;
}

/// <summary>
/// The pure merge of the profile's and the home's <c>mcp.json</c> (2026-09-20): the profile's servers
/// first in their file order, then the home's in theirs, a home server whose name the profile also
/// uses marked <see cref="McpServerEntry.ShadowedBy"/> <see cref="McpScope.Profile"/> — the skills' two-root
/// rule, profile over global, first found wins. Names compare ordinal. The problems are the profile's
/// then the home's.
/// </summary>
public static class McpCatalog
{
    public static McpMerged Merge(McpConfigLoad profile, McpConfigLoad global)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(global);
        var entries = new List<McpServerEntry>(profile.Servers.Count + global.Servers.Count);
        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, config) in profile.Servers)
        {
            if (taken.Add(name))
            {
                entries.Add(new McpServerEntry(name, config, McpScope.Profile));
            }
        }

        // A profile entry with a problem still takes its name (2026-11-20, the mcp-ask review's #64): a typo in the profile's copy must not
        // quietly start the home's server of the same name in its place.
        foreach (var problem in profile.Problems)
        {
            if (problem.Name is { } named)
            {
                taken.Add(named);
            }
        }

        foreach (var (name, config) in global.Servers)
        {
            entries.Add(new McpServerEntry(name, config, McpScope.Global, taken.Contains(name) ? McpScope.Profile : null));
        }

        var problems = new List<McpConfigProblem>(profile.Problems.Count + global.Problems.Count);
        problems.AddRange(profile.Problems);
        problems.AddRange(global.Problems);
        return new McpMerged(entries, problems);
    }
}
