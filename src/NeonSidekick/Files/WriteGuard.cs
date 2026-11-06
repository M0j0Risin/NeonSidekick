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
/// <param name="CommandRoots">
/// The custom commands folders and, for one that is a link, where it points (2026-10-31, review P2-2): both folders are in the home, which
/// guards them already, but a link's target outside it was guarded by nothing. Null for none.
/// </param>
/// <param name="InstallFolder">
/// A package's install folder (2026-11-04, self-update), decided once at start and kept, so deleting the manifest mid-run can't lift the guard: a changed staged file or a planted <c>swap.json</c> would otherwise run at the next launch.
/// Null for none: a dev copy, or an exe simply sitting in a shared folder such as Downloads.
/// </param>
public sealed record WriteGuardFacts(
    Func<string?> Home,
    Func<string?> WorkingDirectory,
    Func<IReadOnlyList<string>> SkillRoots,
    Func<DatabaseGuard?> Databases,
    Func<bool> ShoppingGuarded,
    Func<IRecycleBin?> RecycleBin,
    Func<IReadOnlyList<string>>? CommandRoots = null,
    Func<string?>? InstallFolder = null);

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
/// <item>A package's install folder (2026-11-04, self-update): the exe, its libraries, the manifest and <c>.update</c>. A working folder in it gets no write pass, and it's found by identity as well as spelling, as the home is; a home inside it keeps
/// the home's own rules, so its <c>files</c> sandbox still writes. Checked before the skill roots, so the external <c>.agents\skills</c> beside the binary doesn't keep a skill root's rules.</item>
/// <item>The skill roots (review P3): <c>skill_editor</c> is the model's one way to change a skill.</item>
/// <item>The custom commands folders and where a linked one points (2026-10-31, review P2-2): the user changes them with <c>/commands</c>.</item>
/// <item>A SQLite database while the SQLite tools are on (the old <see cref="DatabaseGuard"/> rule, now also by its header, review I9).</item>
/// <item>A shopping list's name while the shopping tools are offered (the old tripwire, now in one place, review H16).</item>
/// <item>The project file (<c>NEON.md</c>, <c>AGENTS.md</c>, <c>CLAUDE.md</c> at the working directory's root, review P1) asks the user:
/// <see cref="FileOutcome.ProjectFileAsk"/>, which the turn turns into the allow pane.</item>
/// </list>
/// A root that is itself inside the home or a skill root (the default <c>&lt;profile&gt;\files</c>, the images folder, the skill editor's
/// own folder) may write inside itself: "trusted" means inside the home or a skill root and not inside this instance's own root.
/// <para><b>The home, by name and by identity</b> (2026-10-23, the admin review's M1 and M2): the own-root pass never covers the home or a
/// profile folder itself — with the working directory there, only a profile's <c>files</c>, <c>images</c> and <c>skills</c> and the home's
/// <c>skills</c> may be written (<see cref="AppWritable"/>), so <c>configs\services.json</c>, <c>profile.json</c> and <c>persona.md</c> are
/// refused; before, <c>--cwd</c> at a profile let the model write a <c>readwrite</c>, <c>control: ["*"]</c> services.json. And the home is
/// found by more than its spelling: a <c>.neonsidekick</c> part anywhere in the path (another spelling of a home: a share, a mapped drive),
/// and the folder's own identity (<see cref="FileIdentity"/>: <c>\\localhost\C$</c>, <c>\\PC\Users</c>, <c>subst</c>).</para>
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
        if (GuardedFolder(relative) is { } folder)
        {
            return folder;
        }

        var facts = s_facts;
        if (facts is not null)
        {
            if (InHome(facts.Home(), judgedRoot, judged))
            {
                return FileOutcome.AppFilesProtected;
            }

            if (InInstall(facts.InstallFolder?.Invoke(), facts.Home(), judged))
            {
                return FileOutcome.InstallFilesProtected;
            }

            if (facts.SkillRoots() is { } skills && skills.Any(skill => Trusted(skill, judgedRoot, judged)))
            {
                return FileOutcome.SkillFilesProtected;
            }

            // A commands folder, or where a linked one points (2026-10-31, review P2-2); a working folder inside one may write in it, as in a
            // skill root it was pointed at (the user's own choice).
            if (facts.CommandRoots?.Invoke() is { } commands && commands.Any(folder => Trusted(folder, judgedRoot, judged)))
            {
                return FileOutcome.CommandFilesProtected;
            }

            // Claude Code's project servers (2026-11-02, review P12-2): a root .mcp.json, which /claude's claude -p loads from the working folder.
            if (IsRootFile(facts.WorkingDirectory(), judgedRoot, judged, ClaudeMcpFileName))
            {
                return FileOutcome.ClaudeFilesProtected;
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

        // By name, or by content — the file, or the database its journal sibling's name points at (2026-10-24, review J3), and never through a
        // link being removed (Q7).
        if ((databases ?? facts?.Databases)?.Invoke() is { } guard && (guard.IsDatabaseFile(full) || guard.Holds(full, throughLinks: role != WriteRole.Remove)))
        {
            return FileOutcome.DatabaseProtected;
        }

        if (role == WriteRole.Write && (shoppingGuarded ?? facts?.ShoppingGuarded)?.Invoke() == true && Shopping.ShoppingFileGuard.IsShoppingListName(full))
        {
            return FileOutcome.ShoppingList;
        }

        return null;
    }

    /// <summary>
    /// The refusal a path under the root earns by its names alone: a <c>.git</c>, <c>.neon</c> or <c>.claude</c> at any depth (trailing dots
    /// and spaces dropped, as Windows drops them), else null. Pure; <see cref="Judge"/>'s first test, and what a converter's made name asks
    /// (2026-10-29, review P5-3: <c>OutputNames</c> asked only the first folder, so <c>app/.neon/plans/x.md</c> wrote beside itself).
    /// <c>.claude</c> since 2026-11-02 (review P12-2): <c>/claude</c> runs <c>claude -p</c> in the working folder, which loads that folder's
    /// <c>.claude/settings.json</c> hooks and commands, so a model that wrote one there ran a command of its own choosing on the next <c>/claude</c>.
    /// </summary>
    public static FileOutcome? GuardedFolder(string relative)
    {
        ArgumentNullException.ThrowIfNull(relative);
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

            if (string.Equals(bare, ClaudeFolderName, StringComparison.OrdinalIgnoreCase))
            {
                return FileOutcome.ClaudeFilesProtected;
            }
        }

        return null;
    }

    /// <summary>Whether <paramref name="outcome"/> is one of the guard's refusals (rather than a path that is outside or missing).</summary>
    public static bool IsRefusal(FileOutcome outcome) => outcome is FileOutcome.GitProtected or FileOutcome.NeonFilesProtected or FileOutcome.AppFilesProtected
        or FileOutcome.SkillFilesProtected or FileOutcome.CommandFilesProtected or FileOutcome.ProjectFileAsk or FileOutcome.DatabaseProtected or FileOutcome.ShoppingList
        or FileOutcome.ClaudeFilesProtected or FileOutcome.InstallFilesProtected;

    /// <summary>Claude Code's folder of settings, hooks and commands (2026-11-02, review P12-2), refused at any depth.</summary>
    public const string ClaudeFolderName = ".claude";

    /// <summary>Claude Code's project servers file (2026-11-02, review P12-2), refused right in the working folder.</summary>
    public const string ClaudeMcpFileName = ".mcp.json";

    /// <summary>Whether <paramref name="full"/> is the file <paramref name="name"/> right in the live working directory (trailing dots and spaces dropped).</summary>
    private static bool IsRootFile(string? workingDirectory, string root, string full, string name)
    {
        if (string.IsNullOrEmpty(workingDirectory) || !SamePath(Trim(workingDirectory), root))
        {
            return false;
        }

        string? parent = Path.GetDirectoryName(full);
        return parent is not null && SamePath(parent, root) && string.Equals(Path.GetFileName(full).TrimEnd('.', ' '), name, StringComparison.OrdinalIgnoreCase);
    }

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

    /// <summary>The home's default folder name, looked for as a part of any path (M2).</summary>
    public const string HomeFolderName = ".neonsidekick";

    /// <summary>
    /// Whether <paramref name="full"/> is in the home <paramref name="home"/> where this instance may not write: by its spelling, by a
    /// <c>.neonsidekick</c> part, or by the identity of one of its folders (M2); the instance's own root writes inside itself only when that
    /// root is an app-writable folder (M1).
    /// </summary>
    private static bool InHome(string? home, string root, string full)
    {
        if (string.IsNullOrEmpty(home))
        {
            return false;
        }

        string folder = Trim(home);
        if (WorkingDirectory.IsInside(folder, full))
        {
            return !OwnRootWrites(folder, root, full);
        }

        if (SpelledHome(full) is { } spelled)
        {
            return !OwnRootWrites(spelled, root, full);
        }

        if (HomeIdentity(folder) is { } id && FileIdentity.AncestorWith(full, id) is { } found)
        {
            return !OwnRootWrites(found, root, full);
        }

        return false;
    }

    /// <summary>
    /// Whether <paramref name="full"/> is in the install folder and not in a home kept inside it (<c>NEONSIDEKICK_HOME</c> for a portable setup, or the fallback for a process with no profile folder), which <see cref="InHome"/> has judged by its own
    /// rules. By its spelling and by its identity (<c>subst</c>, <c>\\localhost\D$</c>), and with no pass for a working folder inside it, so <c>--cwd</c> at the folder can't undo the guard.
    /// </summary>
    internal static bool InInstall(string? install, string? home, string full)
    {
        if (string.IsNullOrEmpty(install))
        {
            return false;
        }

        string folder = Trim(install);
        bool inside = WorkingDirectory.IsInside(folder, full) || (InstallIdentity(folder) is { } id && FileIdentity.AncestorWith(full, id) is not null);
        return inside && !InHomeKeptInside(home, folder, full);
    }

    // The pass is for a home kept inside the install, and only that (2026-11-04, review P4-9): as "is it in a home" it also passed an install kept inside a home, so a package unpacked in the default sandbox had no guard, and the model could overwrite its exe.
    private static bool InHomeKeptInside(string? home, string install, string full)
    {
        if (string.IsNullOrEmpty(home))
        {
            return false;
        }

        string folder = Trim(home);
        bool keptInside = WorkingDirectory.IsInside(install, folder) || (InstallIdentity(install) is { } id && FileIdentity.AncestorWith(folder, id) is not null);
        return keptInside && (WorkingDirectory.IsInside(folder, full) || (HomeIdentity(folder) is { } homeId && FileIdentity.AncestorWith(full, homeId) is not null));
    }

    /// <summary>
    /// What a delete or move of <paramref name="directory"/> would take of the app's own folders, by spelling and by identity (2026-11-04, review P6-1): an ancestor of the install lost every unloaded DLL before failing on the running exe, and on a Mac the Trash took the whole of it. Null when it holds neither.
    /// </summary>
    internal static FileOutcome? HoldsAppFolders(string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        if (s_facts is not { } facts)
        {
            return null;
        }

        string folder = Trim(directory);
        var id = FileIdentity.Of(folder);
        if (Holds(folder, id, facts.InstallFolder?.Invoke()))
        {
            return FileOutcome.InstallFilesProtected;
        }

        return Holds(folder, id, facts.Home()) ? FileOutcome.AppFilesProtected : null;
    }

    private static bool Holds(string folder, FileId? id, string? target)
    {
        if (string.IsNullOrEmpty(target))
        {
            return false;
        }

        // IsInside takes the path itself too, under the folder's own case rule (2026-11-04, review P10-18): a case-blind equality here refused a delete of /Volumes/x/neon as holding the home /Volumes/x/Neon on a case-sensitive volume.
        string inside = Trim(target);
        return WorkingDirectory.IsInside(folder, inside) || (id is { } known && FileIdentity.AncestorWith(inside, known) is not null);
    }

    /// <summary>Whether <paramref name="folder"/> is <paramref name="target"/> or a folder above it, by spelling or identity: the shell police's question (2026-11-04, review P19-5), its home and install its own.</summary>
    internal static bool HoldsFolder(string folder, string? target)
    {
        string trimmed = Trim(folder);
        return Holds(trimmed, FileIdentity.Of(trimmed), target);
    }

    private static (string Path, FileId? Id) s_installIdentity;

    /// <summary>The install folder's identity, read once and kept, as the home's is.</summary>
    private static FileId? InstallIdentity(string install)
    {
        var cached = s_installIdentity;
        if (cached.Path == install && cached.Id is not null)
        {
            return cached.Id;
        }

        var id = FileIdentity.Of(install);
        s_installIdentity = (install, id);
        return id;
    }

    /// <summary>The part of <paramref name="full"/> up to and including a <c>.neonsidekick</c> folder, or null.</summary>
    private static string? SpelledHome(string full)
    {
        int end = 0;
        foreach (string part in full.Split(WorkingDirectory.Separators))
        {
            end += part.Length;
            if (string.Equals(part.TrimEnd('.', ' '), HomeFolderName, StringComparison.OrdinalIgnoreCase))
            {
                return full[..end];
            }

            end++;
        }

        return null;
    }

    private static bool OwnRootWrites(string home, string root, string full) =>
        WorkingDirectory.IsInside(home, root) && WorkingDirectory.IsInside(root, full) && AppWritable(WorkingDirectory.Under(home, full));

    /// <summary>
    /// Whether <paramref name="relative"/> (a path under the home) lies inside a folder the app lets the model write (M1): a profile's
    /// <c>files</c>, <c>images</c> or <c>skills</c>, or the home's <c>skills</c> — never one of those folders itself, nor the home, <c>profiles</c>,
    /// a profile folder, <c>configs</c> or <c>db</c>.
    /// </summary>
    public static bool AppWritable(string relative)
    {
        ArgumentNullException.ThrowIfNull(relative);
        var parts = relative.Split(WorkingDirectory.Separators, StringSplitOptions.RemoveEmptyEntries);
        return (parts.Length >= 4 && Is(parts[0], "profiles") && (Is(parts[2], "files") || Is(parts[2], "images") || Is(parts[2], "skills")))
            || (parts.Length >= 2 && Is(parts[0], "skills"));

        static bool Is(string part, string name) => string.Equals(part.TrimEnd('.', ' '), name, StringComparison.OrdinalIgnoreCase);
    }

    private static (string Path, FileId? Id) s_homeIdentity;

    /// <summary>The home's identity, read once per home and kept.</summary>
    private static FileId? HomeIdentity(string home)
    {
        var cached = s_homeIdentity;
        if (cached.Path == home && cached.Id is not null)
        {
            return cached.Id;
        }

        var id = FileIdentity.Of(home);
        s_homeIdentity = (home, id);
        return id;
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
