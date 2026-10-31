namespace NeonSidekick.CustomCommands;

/// <summary>Which <c>commands</c> folder a custom command's file is in: the loaded profile's, or the home's (every profile's).</summary>
public enum CustomCommandScope
{
    Profile,
    Global,
}

/// <summary>
/// A custom slash command (2026-10-30, the user's ask: their own commands, as Claude Code does them): a Markdown file in a <c>commands</c>
/// folder, its path under the folder the name (<c>review.md</c> → <c>/review</c>, <c>frontend/component.md</c> → <c>/frontend:component</c>),
/// its body the prompt sent, an optional frontmatter giving <paramref name="Description"/>, <paramref name="ArgumentHint"/> and the declared
/// <paramref name="Arguments"/> names. <paramref name="Description"/> falls back to the body's first line (Claude's rule) and is for display
/// only. <paramref name="Warning"/> is the lenient read's note (a frontmatter that didn't read, <c>!</c> shell lines, the template unedited),
/// shown on its pane row and its <c>/help</c> card; the command runs all the same.
/// </summary>
public sealed record CustomCommand(
    string Name,
    CustomCommandScope Scope,
    string FilePath,
    string? Description,
    string? ArgumentHint,
    IReadOnlyList<string> Arguments,
    string? Warning,
    string Body)
{
    /// <summary>What is typed: <c>/</c> and the name.</summary>
    public string Word => "/" + Name;

    /// <summary>Whether the body holds a <c>!</c> shell line, which is sent as written and never run.</summary>
    public bool HasShellLines { get; init; }
}

/// <summary>
/// A command file that loads but never runs, and what hides it: a built-in of the same word (<paramref name="BuiltIn"/>, the word), or
/// the profile's command of the same name over a global one (<paramref name="Winner"/>).
/// </summary>
public sealed record CustomCommandShadow(CustomCommand Command, string? BuiltIn, CustomCommand? Winner);

/// <summary>
/// A file or folder the scan skipped, and why (a Skipped row). <paramref name="Name"/> is the command name the file would have had, when it
/// has one (a profile file that is a problem lets a global one of that name run, and says so).
/// </summary>
public sealed record CustomCommandProblem(string FilePath, CustomCommandScope Scope, string Problem, string? Name = null);

/// <summary>One scan's answer: the commands that run, the files a built-in or the profile hides, and the files skipped.</summary>
public sealed record CustomCommandScan(IReadOnlyList<CustomCommand> Commands, IReadOnlyList<CustomCommandShadow> Shadowed, IReadOnlyList<CustomCommandProblem> Problems)
{
    /// <summary>No command, nothing shadowed or skipped.</summary>
    public static readonly CustomCommandScan Empty = new([], [], []);

    /// <summary>The command named <paramref name="name"/> (with or without its <c>/</c>, any case), or null.</summary>
    public CustomCommand? Find(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        string bare = name.StartsWith('/') ? name[1..] : name;
        return Commands.FirstOrDefault(c => string.Equals(c.Name, bare, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Whether <paramref name="word"/> (<c>/review</c>) is a command that runs.</summary>
    public bool Knows(string word) => word.StartsWith('/') && Find(word) is not null;

    /// <summary>The words of the commands that run, any case.</summary>
    public IReadOnlySet<string> Names => Commands.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
}
