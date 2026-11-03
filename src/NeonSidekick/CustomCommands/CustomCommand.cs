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

    /// <summary>Claude Code's restricting keys the file sets and the app doesn't honor (2026-11-02, review P12-6), in file order; said on each run.</summary>
    public IReadOnlyList<string> IgnoredKeys { get; init; } = [];

    /// <summary>
    /// The file's write time and length as the scan read it (2026-10-31, review P6-6): the one-time Shadowed notice keys on it, so a changed
    /// file is named again with no stat of its own outside the scan.
    /// </summary>
    internal string Stamp { get; init; } = "";
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

/// <summary>
/// What <c>/help &lt;word&gt;</c> shows for a custom command's word (2026-10-31, review P4-6): the command's card, or a Skipped file's reason
/// and path when no command runs by that name.
/// </summary>
public sealed record CustomHelpCard(CustomCommand? Command, CustomCommandProblem? Problem, CustomCommandShadow? Hides = null);

/// <summary>What a row of the commands' lists is (<see cref="CustomCommandText.Rows"/>).</summary>
public enum CustomCommandRowKind
{
    /// <summary>A command that runs.</summary>
    Command,

    /// <summary>A file a built-in or the profile's command hides.</summary>
    Shadow,

    /// <summary>A file the scan skipped.</summary>
    Problem,
}

/// <summary>
/// One row of the commands' lists, cleaned (2026-10-31, review P6-7): <paramref name="Word"/> is a command's word or a Skipped file's path
/// under its folder, <paramref name="Detail"/> the description, the shadow's reason or the problem, <paramref name="Path"/> the file under its
/// folder. The thing it shows rides along for the pane's acts.
/// </summary>
public sealed record CustomCommandRow(CustomCommandRowKind Kind, string Word, string? Hint, string Detail, CustomCommandScope Scope, string Path)
{
    public CustomCommand? Command { get; init; }

    public CustomCommandShadow? Shadow { get; init; }

    public CustomCommandProblem? Problem { get; init; }
}

/// <summary>
/// One scan's answer: the commands that run, the files a built-in or the profile hides, and the files skipped; since 2026-10-31 (review
/// P5-3, P5-6, P4-10) also the <see cref="Roots"/> it read, a <see cref="Note"/> about them and whether they are one folder.
/// </summary>
public sealed record CustomCommandScan(IReadOnlyList<CustomCommand> Commands, IReadOnlyList<CustomCommandShadow> Shadowed, IReadOnlyList<CustomCommandProblem> Problems)
{
    private IReadOnlySet<string>? _names;

    /// <summary>No command, nothing shadowed or skipped, no folder.</summary>
    public static readonly CustomCommandScan Empty = new([], [], []);

    /// <summary>The folders this scan read (<see cref="CustomCommandRoots.None"/> for a scan built by hand).</summary>
    public CustomCommandRoots Roots { get; init; } = CustomCommandRoots.None;

    /// <summary>A note about the folders, not a problem with a file (<see cref="CustomCommandText.SameFolderNote"/>); null for none.</summary>
    public string? Note { get; init; }

    /// <summary>Whether <see cref="Roots"/> are one folder, judged once per scan (<see cref="CustomCommandRoots.Same"/> follows links).</summary>
    public bool SameFolder { get; init; }

    /// <summary>The command named <paramref name="name"/> (with or without its <c>/</c>, any case), or null.</summary>
    public CustomCommand? Find(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        string bare = name.StartsWith('/') ? name[1..] : name;
        return Commands.FirstOrDefault(c => string.Equals(c.Name, bare, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Whether <paramref name="word"/> (<c>/review</c>) is a command that runs.</summary>
    public bool Knows(string word) => word.StartsWith('/') && Find(word) is not null;

    /// <summary>
    /// The words of the commands that run, any case: one set per scan (2026-10-31, review P2-7: a new set on every read), so the Web UI's
    /// settings carry one stable set a cache can key on.
    /// </summary>
    public IReadOnlySet<string> Names => _names ??= Commands.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
}
