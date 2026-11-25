using NeonSidekick.Files;
using NeonSidekick.Git;

namespace NeonSidekick.Skills;

/// <summary>
/// One skills folder of the walk (<see cref="ProjectSkillWalk.Levels"/>): <paramref name="Level"/> is the folder that holds it, the working folder
/// or an ancestor, and the key its trust is kept under (<see cref="ProjectSkillTrust"/>); <paramref name="SkillsFolder"/> is its
/// <c>.agents/skills</c> or <c>.claude/skills</c>; <paramref name="Claude"/> says which, since a <c>.claude</c> skill is read by Claude Code's rules
/// (<see cref="SkillFrontmatter.TryParseClaude"/>).
/// </summary>
public sealed record ProjectSkillLevel(string Level, string SkillsFolder, bool Claude)
{
    /// <summary>The folder's own pair of names, <c>.agents/skills</c> or <c>.claude/skills</c>, <c>/</c>-separated (the per-skill keys' prefix).</summary>
    public string Kind => (Claude ? ProjectSkillWalk.ClaudeFolderName : ProjectSkillWalk.AgentsFolderName) + "/" + SkillRoots.DirectoryName;
}

/// <summary>
/// Which folders a repo's skills are read from (2026-11-23, feature idea #16, the user's call matching Codex): from the working folder up to its git
/// repo root, each level's <c>.agents/skills</c> then <c>.claude/skills</c>, nearest level first; outside a repo, the working folder alone. Never above
/// the repo root, and never the user's home itself (its <c>.agents\skills</c> is the external scope) nor NeonSidekick's own home.
/// <para>The repo root is <see cref="GitAccess.FindGitEntry"/>'s answer, not <c>Repository.Discover</c> (null on the published exe) nor
/// <see cref="GitAccess"/>'s lookup (it refuses a work tree outside the sandbox). An answer at or above the user's home counts as none, so a dotfiles
/// repo there never makes the whole home a project; one at or above the app's home while the working folder is inside it counts as none too, so a
/// home kept in git never reads <c>profiles/&lt;p&gt;</c>'s or the home's <c>.agents</c>. Every "inside" test is <see cref="WorkingDirectory.IsInside"/>:
/// the root's own case rule and drive roots handled. Pure over the file system; both homes are passed in, never read here.</para>
/// </summary>
public static class ProjectSkillWalk
{
    /// <summary>The cross-client folder, <c>.agents</c>.</summary>
    public const string AgentsFolderName = SkillRoots.ExternalDirectoryName;

    /// <summary>Claude Code's folder, <c>.claude</c>.</summary>
    public const string ClaudeFolderName = ".claude";

    /// <summary>
    /// The folders a scan from <paramref name="workingFolder"/> reads, in precedence order, whether or not each exists (the caller checks).
    /// <paramref name="userHome"/> is the user's home (two levels above the external skills folder); <paramref name="appHome"/> NeonSidekick's home.
    /// </summary>
    public static IReadOnlyList<ProjectSkillLevel> Levels(string workingFolder, string? userHome, string? appHome)
    {
        ArgumentNullException.ThrowIfNull(workingFolder);
        var levels = new List<ProjectSkillLevel>();
        foreach (string level in LevelFolders(workingFolder, userHome, appHome))
        {
            levels.Add(new ProjectSkillLevel(level, Path.Combine(level, AgentsFolderName, SkillRoots.DirectoryName), Claude: false));
            levels.Add(new ProjectSkillLevel(level, Path.Combine(level, ClaudeFolderName, SkillRoots.DirectoryName), Claude: true));
        }

        return levels;
    }

    /// <summary>The level folders alone, nearest first: the working folder, then each parent up to the repo root (<see cref="RepoRoot"/>).</summary>
    public static IReadOnlyList<string> LevelFolders(string workingFolder, string? userHome, string? appHome)
    {
        ArgumentNullException.ThrowIfNull(workingFolder);
        string start = Full(workingFolder);
        string? user = userHome is { Length: > 0 } ? Full(userHome) : null;
        string? app = appHome is { Length: > 0 } ? Full(appHome) : null;
        var folders = new List<string>();
        string? top = RepoRoot(start, user, app) ?? start;
        for (string? folder = start; folder is not null; folder = Path.GetDirectoryName(folder))
        {
            if (!Same(folder, user) && !Same(folder, app))
            {
                folders.Add(folder);
            }

            if (Same(folder, top))
            {
                break;
            }
        }

        return folders;
    }

    /// <summary>
    /// The level a skill moved into the working folder lands in (2026-11-23, the user's ask: the scope page's <c>project</c> row): the working folder,
    /// when the walk reads it as its first level; null when it doesn't (the user's home, whose <c>.agents\skills</c> is the external scope, or the app's
    /// home), so a moved skill is always one the walk offers back.
    /// </summary>
    public static string? MoveTarget(string workingFolder, string? userHome, string? appHome)
    {
        ArgumentNullException.ThrowIfNull(workingFolder);
        var levels = LevelFolders(workingFolder, userHome, appHome);
        string start = Full(workingFolder);
        return levels.Count > 0 && string.Equals(levels[0], start, StringComparison.Ordinal) ? start : null;
    }

    /// <summary>
    /// Whether the repo's skills folder at <paramref name="level"/> leads outside <paramref name="bound"/> (2026-11-24, review #28): <c>.agents</c> or
    /// <c>.agents/skills</c> a link out of the repo, so a move into it would land the skill somewhere else. The scope page's <c>project</c> row hides then.
    /// </summary>
    public static bool SkillsLinkedAway(string level, string bound)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(bound);
        string agents = Path.Combine(level, AgentsFolderName);
        return SkillCatalog.LinksOutside(agents, bound) || SkillCatalog.LinksOutside(Path.Combine(agents, SkillRoots.DirectoryName), bound);
    }

    /// <summary>
    /// The repo root above (or at) <paramref name="workingFolder"/> that the walk may climb to: <see cref="GitAccess.FindGitEntry"/>'s folder, or
    /// null for none, for one at or above <paramref name="userHome"/>, and for one at or above <paramref name="appHome"/> while the working folder is
    /// inside the app's home.
    /// </summary>
    public static string? RepoRoot(string workingFolder, string? userHome, string? appHome)
    {
        ArgumentNullException.ThrowIfNull(workingFolder);
        string start = Full(workingFolder);
        string? found;
        try
        {
            found = GitAccess.FindGitEntry(start);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }

        if (found is null)
        {
            return null;
        }

        found = Full(found);
        if (userHome is { Length: > 0 } && WorkingDirectory.IsInside(found, Full(userHome)))
        {
            return null;
        }

        if (appHome is { Length: > 0 } && Full(appHome) is var app && WorkingDirectory.IsInside(app, start) && WorkingDirectory.IsInside(found, app))
        {
            return null;
        }

        return found;
    }

    private static string Full(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool Same(string folder, string? other) =>
        other is not null && string.Equals(folder, other, CaseRule.Comparison(other));
}
