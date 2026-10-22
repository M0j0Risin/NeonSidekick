using System.Text;

namespace NeonSidekick.Files;

/// <summary>What a guarded write does to its target: writes onto it (create, overwrite, a copy's or move's destination) or takes it away (a delete, a move's source).</summary>
public enum WriteRole
{
    Write,
    Remove,
}

/// <summary>
/// The facts <see cref="WriteGuard"/> judges by that no path holds (2026-10-22, the fix plan's mechanism 1): each a live read over the
/// effective settings, so a profile switch or a <c>/cwd</c> is seen at once. Set once at startup by <c>SidekickApp</c>, then only read.
/// </summary>
/// <param name="Home">The app's home (<c>%USERPROFILE%\.neonsidekick</c>): every profile, config and database under it.</param>
/// <param name="WorkingDirectory">The live working directory, whose root holds the project file the system prompt reads.</param>
/// <param name="SkillRoots">The skill roots (the profile's, the home's, the external <c>.agents\skills</c>).</param>
/// <param name="Databases">The SQLite databases while the SQLite tools are on, else null.</param>
/// <param name="ShoppingGuarded">Whether the shopping tools are offered (the shopping list's tripwire).</param>
/// <param name="RecycleBin">The bin a delete moves to while <c>File deletes to Recycle bin</c> is on, else null.</param>
public sealed record WriteGuardFacts(
    Func<string?> Home,
    Func<string?> WorkingDirectory,
    Func<IReadOnlyList<string>> SkillRoots,
    Func<DatabaseGuard?> Databases,
    Func<bool> ShoppingGuarded,
    Func<IRecycleBin?> RecycleBin);

/// <summary>
/// The model's writes judged by the file they reach, not by the spelling (2026-10-22, the fix plan's mechanism 1 and decision D2, the
/// user's call: refuse, except the project file, which asks). Every writer passes <see cref="WorkingDirectory.Resolve(string, bool, out string)"/>
/// with <c>forWrite</c>, and the vault its own path code; both ask <see cref="Judge"/>. What it refuses:
/// <list type="bullet">
/// <item><c>.git</c> — a write into it, and a move or rename of a <c>.git</c> itself (review I1, I2, G3): hooks and <c>config</c> run code
/// at the user's next git command, past the shell's gate.</item>
/// <item><c>.neon</c> — the plans, audits and Ralph files the app trusts (review P4): only the app writes them, and the user with
/// <c>/plan edit</c>.</item>
/// <item>The app's home and every profile under it (review P2, W1, U9): a working directory or a share that holds
/// <c>.neonsidekick</c> made <c>configs\mcp.json</c> (a program at the next start) and <c>profile.json</c> writable.</item>
/// <item>The skill roots (review P3): <c>skill_editor</c> is the model's one way to change a skill.</item>
/// <item>A SQLite database while the SQLite tools are on (the old <see cref="DatabaseGuard"/> rule, now also by its header, review I9).</item>
/// <item>A shopping list's name while the shopping tools are offered (the old tripwire, now in one place, review H16).</item>
/// <item>The project file (<c>NEON.md</c>, <c>AGENTS.md</c>, <c>CLAUDE.md</c> at the working directory's root, review P1) asks the user:
/// <see cref="FileOutcome.ProjectFileAsk"/>, which the turn turns into the allow pane.</item>
/// </list>
/// A root that is itself inside the home or a skill root (the default <c>&lt;profile&gt;\files</c>, the images folder, the skill editor's
/// own folder) may write inside itself: "trusted" means inside the home or a skill root and not inside this instance's own root.
/// <para><b>Fail-safe direction:</b> the guard holds for every write unless an app-write scope is open (<see cref="AppWrites"/>), so a
/// tool site nobody listed stays guarded and an app site nobody listed fails a test. The scope wraps a synchronous call only; a task
/// started inside it is guarded again once it closes (the scope is an object the task sees closed). The user's own commands write
/// through it too: D2 refuses the model, not the user.</para>
/// <para><b>Unset facts</b> (a test of a screen alone, a check mode, the relay child): the rules the path alone decides still hold —
/// <c>.git</c> and <c>.neon</c> — and the rest stay off for want of facts.</para>
/// </summary>
public static class WriteGuard
{
    /// <summary>The folder the app keeps a working directory's plans, audits and loop files in.</summary>
    public const string NeonFolderName = ".neon";

    private static volatile WriteGuardFacts? s_facts;
    private static readonly AsyncLocal<Scope?> s_appWrites = new();
    private static readonly AsyncLocal<Scope?> s_projectAllowed = new();
    private static readonly AsyncLocal<ProjectFileSlot?> s_slot = new();

    /// <summary>The facts in force, or null when unset.</summary>
    public static WriteGuardFacts? Facts => s_facts;

    /// <summary>Sets the facts (startup), returning what puts the previous ones back (the tests' scope; the app never restores).</summary>
    public static IDisposable Configure(WriteGuardFacts? facts)
    {
        var previous = s_facts;
        s_facts = facts;
        return new Restore(previous);
    }

    /// <summary>
    /// The app's own writes, and the user's (a slash command, a pane, the Web UI's buttons): the model's guards off for the synchronous call
    /// inside. Never wrap an <c>await</c>: a task that outlived the scope would borrow the app's pass.
    /// </summary>
    public static IDisposable AppWrites()
    {
        var scope = new Scope();
        var previous = s_appWrites.Value;
        s_appWrites.Value = scope;
        return new Closer(scope, () => s_appWrites.Value = previous);
    }

    /// <summary><paramref name="write"/> run inside <see cref="AppWrites"/>: the app's own synchronous write (a plan, an audit, a loop's file, an export).</summary>
    public static T AsApp<T>(Func<T> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        using (AppWrites())
        {
            return write();
        }
    }

    /// <summary>Whether an app-write scope is open on this flow.</summary>
    public static bool InAppWrites => s_appWrites.Value is { Open: true };

    /// <summary>The project file allowed for the call inside (the user said yes on the pane).</summary>
    public static IDisposable AllowProjectFile()
    {
        var scope = new Scope();
        var previous = s_projectAllowed.Value;
        s_projectAllowed.Value = scope;
        return new Closer(scope, () => s_projectAllowed.Value = previous);
    }

    /// <summary>
    /// Watches the call inside for a project-file refusal (<see cref="FileOutcome.ProjectFileAsk"/>): the slot names the file it refused,
    /// for the turn to ask about and run the call again. A mutable holder, since an async call's own flow never hands its values back.
    /// </summary>
    public static IDisposable Watch(ProjectFileSlot slot)
    {
        ArgumentNullException.ThrowIfNull(slot);
        var previous = s_slot.Value;
        s_slot.Value = slot;
        return new Closer(new Scope(), () => s_slot.Value = previous);
    }

    /// <summary>
    /// The refusal for a <paramref name="role"/> write onto <paramref name="full"/> by an instance rooted at <paramref name="root"/>, or
    /// null to let it through. Both full and normalized; <paramref name="full"/> inside <paramref name="root"/> by its spelling and links.
    /// <paramref name="shoppingGuarded"/> and <paramref name="databases"/> are the instance's own hooks when it has them (else the facts').
    /// </summary>
    public static FileOutcome? Judge(string root, string full, WriteRole role, Func<bool>? shoppingGuarded = null, Func<DatabaseGuard?>? databases = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(full);
        if (InAppWrites)
        {
            return null;
        }

        string judged = OperatingSystem.IsMacOS() ? full.Normalize(NormalizationForm.FormC) : full;
        string judgedRoot = OperatingSystem.IsMacOS() ? root.Normalize(NormalizationForm.FormC) : root;
        string relative = WorkingDirectory.IsInside(judgedRoot, judged) ? WorkingDirectory.Under(judgedRoot, judged) : "";
        foreach (string part in relative.Split(WorkingDirectory.Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            string bare = part.TrimEnd('.', ' ');
            if (string.Equals(bare, WorkingDirectory.GitFolderName, StringComparison.OrdinalIgnoreCase))
            {
                return FileOutcome.GitProtected;
            }

            if (string.Equals(bare, NeonFolderName, StringComparison.OrdinalIgnoreCase))
            {
                return FileOutcome.NeonFilesProtected;
            }
        }

        var facts = s_facts;
        if (facts is not null)
        {
            if (Trusted(facts.Home(), judgedRoot, judged))
            {
                return FileOutcome.AppFilesProtected;
            }

            if (facts.SkillRoots() is { } skills && skills.Any(skill => Trusted(skill, judgedRoot, judged)))
            {
                return FileOutcome.SkillFilesProtected;
            }

            if (IsProjectFile(facts.WorkingDirectory(), judgedRoot, judged) && s_projectAllowed.Value is not { Open: true })
            {
                if (s_slot.Value is { } slot)
                {
                    slot.Path = full;
                }

                return FileOutcome.ProjectFileAsk;
            }
        }

        if ((databases ?? facts?.Databases)?.Invoke() is { } guard && (guard.IsDatabaseFile(full) || DatabaseGuard.HasSqliteHeader(full)))
        {
            return FileOutcome.DatabaseProtected;
        }

        if (role == WriteRole.Write && (shoppingGuarded ?? facts?.ShoppingGuarded)?.Invoke() == true && Shopping.ShoppingFileGuard.IsShoppingListName(full))
        {
            return FileOutcome.ShoppingList;
        }

        return null;
    }

    /// <summary>Whether <paramref name="outcome"/> is one of the guard's refusals (rather than a path that is outside or missing).</summary>
    public static bool IsRefusal(FileOutcome outcome) => outcome is FileOutcome.GitProtected or FileOutcome.NeonFilesProtected or FileOutcome.AppFilesProtected
        or FileOutcome.SkillFilesProtected or FileOutcome.ProjectFileAsk or FileOutcome.DatabaseProtected or FileOutcome.ShoppingList;

    /// <summary>Whether <paramref name="full"/> is the project file the system prompt reads: one of its names right in the live working directory.</summary>
    public static bool IsProjectFile(string? workingDirectory, string root, string full)
    {
        if (string.IsNullOrEmpty(workingDirectory) || !SamePath(Trim(workingDirectory), root))
        {
            return false;
        }

        string? parent = Path.GetDirectoryName(full);
        return parent is not null && SamePath(parent, root)
            && Llm.ProjectFile.FileNames.Any(name => string.Equals(Path.GetFileName(full).TrimEnd('.', ' '), name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Whether <paramref name="full"/> lies in the guarded folder <paramref name="guarded"/> and not in this instance's own root that sits inside
    /// it (<c>&lt;profile&gt;\files</c> writes its own files; <c>--cwd %USERPROFILE%</c> doesn't write <c>.neonsidekick</c>).
    /// </summary>
    private static bool Trusted(string? guarded, string root, string full)
    {
        if (string.IsNullOrEmpty(guarded))
        {
            return false;
        }

        string folder = Trim(guarded);
        return WorkingDirectory.IsInside(folder, full) && !(WorkingDirectory.IsInside(folder, root) && WorkingDirectory.IsInside(root, full));
    }

    private static string Trim(string path)
    {
        try
        {
            string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            return OperatingSystem.IsMacOS() ? full.Normalize(NormalizationForm.FormC) : full;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    private static bool SamePath(string a, string b) => string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), CaseRule.Comparison(b));

    /// <summary>A scope's open flag, seen closed by any flow that captured it.</summary>
    private sealed class Scope
    {
        public volatile bool Open = true;
    }

    private sealed class Closer(Scope scope, Action restore) : IDisposable
    {
        public void Dispose()
        {
            scope.Open = false;
            restore();
        }
    }

    private sealed class Restore(WriteGuardFacts? previous) : IDisposable
    {
        public void Dispose() => s_facts = previous;
    }
}

/// <summary>Where <see cref="WriteGuard.Watch"/> leaves the project file a call was refused for.</summary>
public sealed class ProjectFileSlot
{
    /// <summary>The full path refused, or null when nothing was.</summary>
    public string? Path { get; set; }
}
