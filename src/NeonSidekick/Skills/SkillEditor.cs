using System.Text;
using NeonSidekick.Files;

namespace NeonSidekick.Skills;

/// <summary>What a <see cref="SkillEditor"/> act came to.</summary>
public enum SkillEditOutcome
{
    Created,
    Updated,
    BadName,
    Exists,
    Missing,
    EmptyDescription,
    DescriptionTooLong,
    EmptyInstructions,
    InstructionsTooLong,
    NothingToChange,
    Unparseable,
    Failed,

    /// <summary>A create for a name that is already a skill in another writable root (the result's scope is where it lives): a copy would hide it (2026-09-16).</summary>
    ExistsElsewhere,

    /// <summary>A create or update for a name that is a skill in the external folder, which the app never writes (2026-09-16).</summary>
    ExternalReadOnly,

    /// <summary>The pane moved the folder between the profile and global roots (<see cref="SkillEditor.Move"/>, 2026-09-18); the result's scope is the destination.</summary>
    Moved,

    /// <summary>The pane removed the folder and everything in it (<see cref="SkillEditor.Delete"/>, 2026-09-18), after a confirmation.</summary>
    Deleted,

    /// <summary>The pane renamed the folder and rewrote the frontmatter's <c>name</c> line (<see cref="SkillEditor.Rename"/>, 2026-09-21); the result's name is the new one.</summary>
    Renamed,

    /// <summary><c>write_file</c> wrote a supporting file beside the SKILL.md (2026-09-27); the result's detail is the file tools' sentence (<c>FileText.Wrote</c>).</summary>
    FileWritten,

    /// <summary><c>edit_file</c> changed a supporting file (2026-09-27); the detail is <c>FileText.Edited</c>'s sentence.</summary>
    FileEdited,

    /// <summary>A file action named the SKILL.md, the app's <c>.neon-source.json</c> or a path in a folder the skill never keeps (<see cref="SkillCatalog.SkippedFolders"/>); the result's path is what was named.</summary>
    ProtectedFile,

    /// <summary>A file action the file layer refused (outside the folder, too long, <c>old_text</c> not found…); the detail is its sentence.</summary>
    FileRefused,

    /// <summary>The skill is locked (2026-10-07, <see cref="SkillLock"/>): no change of any kind until the user unlocks it.</summary>
    Locked,
}

/// <summary>
/// The outcome, the skill's name and scope, the bytes written, a detail for the failures; and
/// <paramref name="Summary"/> — the model's own sentence on what changed, from the tool's
/// <c>summary</c> argument (2026-09-19; empty when it gave none), never part of the skill.
/// <paramref name="Path"/> is the supporting file a <c>write_file</c> / <c>edit_file</c> named (2026-09-27), relative to the skill folder.
/// </summary>
public sealed record SkillEditResult(SkillEditOutcome Outcome, string Name, SkillScope Scope, long Bytes = 0, string Detail = "", int Length = 0, string Summary = "", string Path = "")
{
    /// <summary>
    /// What a write replaced (2026-10-02, the revisions the Skills pane's revert puts back): the SKILL.md's text before an update, a
    /// supporting file's before a <c>write_file</c> / <c>edit_file</c>. Null when nothing was there (<see cref="Existed"/> false) or the old
    /// text was longer than <see cref="SkillRecordStore.MaxRevisionChars"/> and not read.
    /// </summary>
    public string? Previous { get; init; }

    /// <summary>Whether the file the write replaced was there before it (false for a create, a new supporting file).</summary>
    public bool Existed { get; init; }
}

/// <summary>
/// The write side of the skills, used by <c>skill_editor</c> (<see cref="Create"/>, <see cref="Update"/>)
/// and, since 2026-09-18, by the <c>/skill</c> pane (<see cref="Move"/>, <see cref="Delete"/>, and <see cref="Rename"/> since 2026-09-21):
/// <see cref="Create"/> makes <c>&lt;root&gt;\&lt;name&gt;\SKILL.md</c> from a description and the
/// instructions, <see cref="Update"/> rewrites one or both in an existing file and carries its other
/// frontmatter lines through, <see cref="Move"/> renames the folder under the other writable root and
/// <see cref="Delete"/> removes it with everything in it; <see cref="WriteFile"/> and <see cref="EditFile"/>
/// (2026-09-27) write the files beside the <c>SKILL.md</c>, never it. Only the profile and global roots are written
/// (<see cref="SkillScopes.Writable"/>); the external folder is other clients' and read-only here. The
/// model's tool never deletes or moves a skill — the pane does, after a confirmation, and only a folder
/// that sits right under its root (<see cref="Move"/> and <see cref="Delete"/> check before they act).
/// The file is written whole to a temp name and moved over (the <c>MemoryStore</c> shape), UTF-8 without
/// a BOM. A model's mistake is an outcome, never an exception.
///
/// <para>A name is one skill across the roots (2026-09-16, after a model asked to update a global
/// skill wrote a profile copy that shadowed it): <see cref="Create"/> refuses a name that is a skill
/// in any root the catalog reads (<see cref="Find"/>), and <see cref="Update"/> changes the skill
/// where it lives when the named root has none — the result carries the real scope.</para>
///
/// <para>A name may be any valid one, a built-in command's word included: from 2026-09-17 until
/// later on 2026-09-18 <see cref="Create"/> refused <c>help</c>, <c>learn</c>, <c>exit</c> and the
/// rest, since under <c>Skill slash commands</c> every loaded skill was a <c>/&lt;name&gt;</c>
/// command that a base command won; the commands went with the switch (the user's call: the
/// <c>#</c>-mention never collides), and the rule with them.</para>
/// </summary>
public static class SkillEditor
{
    /// <summary>Characters of instructions accepted at most — the specification wants a body under 500 lines.</summary>
    public const int MaxInstructionChars = 64_000;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Where <paramref name="name"/> is already a skill (a folder holding <c>SKILL.md</c>), checked
    /// in precedence order; null when nowhere. The external root only while <paramref name="external"/>
    /// says the catalog reads it — a skill there is invisible otherwise, and no reason to refuse.
    /// </summary>
    public static SkillScope? Find(SkillRoots roots, string name, bool external)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(name);
        foreach (var scope in new[] { SkillScope.Profile, SkillScope.Global, SkillScope.External })
        {
            if (scope == SkillScope.External && !external)
            {
                continue;
            }

            if (File.Exists(Path.Combine(roots.Of(scope), name, SkillCatalog.FileName)))
            {
                return scope;
            }
        }

        return null;
    }

    /// <summary>A new skill; refused when the folder is already there, or the name is a skill in any other root the catalog reads.</summary>
    /// <param name="external">Whether the external root is read (the setting <c>Use external skills</c>): a skill there blocks the name too.</param>
    public static SkillEditResult Create(SkillRoots roots, SkillScope scope, string name, string description, string instructions, bool external)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(instructions);
        name = name.Trim();
        if (!SkillFrontmatter.IsValidName(name))
        {
            return new SkillEditResult(SkillEditOutcome.BadName, name, scope);
        }

        string flat = SkillFrontmatter.Flatten(description);
        if (Check(flat, instructions, name, scope) is { } refused)
        {
            return refused;
        }

        string directory = Path.Combine(roots.Of(scope), name);
        if (Directory.Exists(directory) || File.Exists(directory))
        {
            return new SkillEditResult(SkillEditOutcome.Exists, name, scope);
        }

        // The name is one skill across the roots: a copy here would shadow the one there.
        switch (Find(roots, name, external))
        {
            case SkillScope.External:
                return new SkillEditResult(SkillEditOutcome.ExternalReadOnly, name, SkillScope.External);
            case { } elsewhere:
                return new SkillEditResult(SkillEditOutcome.ExistsElsewhere, name, elsewhere);
        }

        return Write(directory, name, scope, SkillFrontmatter.Write(name, flat, [], instructions), SkillEditOutcome.Created);
    }

    /// <summary>
    /// A new description, new instructions or both for an existing skill. When the named root has no
    /// such skill the one in the other writable root is changed instead (the result's scope says which);
    /// a skill in the external folder alone is refused. A file whose frontmatter cannot be read is
    /// rewritten only when both are given (its other lines are lost then).
    /// </summary>
    /// <param name="external">Whether the external root is read: a skill only there answers <see cref="SkillEditOutcome.ExternalReadOnly"/>, not <see cref="SkillEditOutcome.Missing"/>.</param>
    public static SkillEditResult Update(SkillRoots roots, SkillScope scope, string name, string? description, string? instructions, bool external)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(name);
        name = name.Trim();
        if (!SkillFrontmatter.IsValidName(name))
        {
            return new SkillEditResult(SkillEditOutcome.BadName, name, scope);
        }

        string directory = Path.Combine(roots.Of(scope), name);
        string file = Path.Combine(directory, SkillCatalog.FileName);
        if (!File.Exists(file))
        {
            // Not here: where it lives, if anywhere — never a hint to create a copy of a skill that exists.
            switch (Find(roots, name, external))
            {
                case SkillScope.External:
                    return new SkillEditResult(SkillEditOutcome.ExternalReadOnly, name, SkillScope.External);
                case { } found:
                    scope = found;
                    directory = Path.Combine(roots.Of(scope), name);
                    file = Path.Combine(directory, SkillCatalog.FileName);
                    break;
                default:
                    return new SkillEditResult(SkillEditOutcome.Missing, name, scope);
            }
        }

        if (SkillLock.IsLocked(directory))
        {
            return new SkillEditResult(SkillEditOutcome.Locked, name, scope);
        }

        bool hasDescription = !string.IsNullOrWhiteSpace(description);
        bool hasInstructions = !string.IsNullOrWhiteSpace(instructions);
        if (!hasDescription && !hasInstructions)
        {
            return new SkillEditResult(SkillEditOutcome.NothingToChange, name, scope);
        }

        string? flat = hasDescription ? SkillFrontmatter.Flatten(description!) : null;
        // The absent half is checked as a stand-in: only what was given can be wrong.
        if (Check(flat ?? "x", hasInstructions ? instructions! : "x", name, scope) is { } refused)
        {
            return refused;
        }

        string text;
        try
        {
            text = WorkingDirectory.Decode(File.ReadAllBytes(file), out _);
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return new SkillEditResult(SkillEditOutcome.Failed, name, scope, Detail: ex.Message);
        }

        IReadOnlyList<string> other = [];
        string body = "";
        string current = "";
        if (SkillFrontmatter.TryParse(text, out var frontmatter, out body, out string? problem))
        {
            other = frontmatter!.OtherLines;
            current = frontmatter.Description;
        }
        else if (!hasDescription || !hasInstructions)
        {
            return new SkillEditResult(SkillEditOutcome.Unparseable, name, scope, Detail: problem ?? "");
        }

        var written = Write(directory, name, scope, SkillFrontmatter.Write(name, flat ?? current, other, hasInstructions ? instructions! : body), SkillEditOutcome.Updated);
        return written.Outcome == SkillEditOutcome.Updated ? written with { Previous = Kept(text), Existed = true } : written;
    }

    /// <summary>A replaced text as a revision keeps it: null past <see cref="SkillRecordStore.MaxRevisionChars"/>.</summary>
    private static string? Kept(string? text) => text is null || text.Length > SkillRecordStore.MaxRevisionChars ? null : text;

    /// <summary>
    /// The revert (2026-10-02; the Skills pane's since 2026-10-04, <see cref="SkillRecords.Restore"/>): <paramref name="content"/> put back as the whole of <paramref name="path"/> (<c>SKILL.md</c> or a
    /// supporting file, relative to the folder) of the skill in <paramref name="directory"/>, or the file removed when
    /// <paramref name="content"/> is null (the write being undone created it). Raw text, no frontmatter check: the revision was the
    /// file as it was. The external root is never written (<see cref="SkillEditOutcome.ExternalReadOnly"/>); a path outside the
    /// folder is <see cref="SkillEditOutcome.FileRefused"/>. The result carries what it replaced, as every write does.
    /// </summary>
    public static SkillEditResult Restore(SkillRoots roots, SkillScope scope, string directory, string name, string path, string? content)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(path);
        if (scope == SkillScope.External)
        {
            return new SkillEditResult(SkillEditOutcome.ExternalReadOnly, name, SkillScope.External);
        }

        if (SkillLock.IsLocked(directory))
        {
            return new SkillEditResult(SkillEditOutcome.Locked, name, scope);
        }

        bool skillFile = string.Equals(path, SkillCatalog.FileName, StringComparison.OrdinalIgnoreCase);
        try
        {
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            string full = Path.GetFullPath(Path.Combine(root, path));
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return new SkillEditResult(SkillEditOutcome.FileRefused, name, scope, Detail: path, Path: path);
            }

            bool existed = File.Exists(full);
            string? previous = existed ? Kept(WorkingDirectory.Decode(File.ReadAllBytes(full), out _)) : null;
            var done = skillFile ? SkillEditOutcome.Updated : SkillEditOutcome.FileWritten;
            if (content is null)
            {
                if (skillFile)
                {
                    // A skill always has its SKILL.md; a revision never says it had none.
                    return new SkillEditResult(SkillEditOutcome.FileRefused, name, scope, Detail: path, Path: path);
                }

                File.Delete(full);
                return new SkillEditResult(done, name, scope, Path: path) { Previous = previous, Existed = existed };
            }

            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            string temp = $"{full}.{Guid.NewGuid():N}.tmp";
            byte[] bytes = Utf8NoBom.GetBytes(content);
            try
            {
                File.WriteAllBytes(temp, bytes);
                File.Move(temp, full, overwrite: true);
            }
            catch
            {
                try { File.Delete(temp); } catch { /* best effort */ }
                throw;
            }

            return new SkillEditResult(done, name, scope, bytes.Length, Path: skillFile ? "" : path) { Previous = previous, Existed = existed };
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return new SkillEditResult(SkillEditOutcome.Failed, name, scope, Detail: ex.Message, Path: path);
        }
    }

    /// <summary>
    /// <c>skill_editor</c>'s <c>write_file</c> (2026-09-27, the user's ask: a skill's data files — a
    /// mapping, examples — were readable through <c>load_skill</c> and changeable by nothing): a file
    /// beside the SKILL.md of an existing skill, created or replaced whole. The skill is found as
    /// <see cref="Update"/> finds it (the other writable root when the named one has none; external
    /// read-only); the write goes through a <see cref="WorkingDirectory"/> rooted at the skill folder,
    /// so the sandbox's confinement, its <see cref="WorkingDirectory.MaxWriteChars"/> cap and its atomic
    /// write are the file tools' own. Never the SKILL.md (<see cref="Create"/> and <see cref="Update"/>
    /// write that, frontmatter checked), the app's sidecar or a folder the skill never keeps
    /// (<see cref="SkillEditOutcome.ProtectedFile"/>). The previous version is replaced for good (its copy into the
    /// skill's own <c>.trash</c> under <c>File safe edits</c> went with that setting on 2026-10-01, the user's call).
    /// </summary>
    public static SkillEditResult WriteFile(SkillRoots roots, SkillScope scope, string name, string path, string content, bool external, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(content);
        return InFile(roots, scope, name, path, external, time, (files, relative, where) =>
        {
            var written = files.WriteText(relative, content, overwrite: true);
            return new SkillEditResult(written.Outcome == FileOutcome.Ok ? SkillEditOutcome.FileWritten : SkillEditOutcome.FileRefused, where.Name, where.Scope, written.Bytes, FileText.Wrote(written), Path: relative);
        });
    }

    /// <summary>
    /// <c>skill_editor</c>'s <c>edit_file</c> (2026-09-27): <paramref name="oldText"/> replaced with
    /// <paramref name="newText"/> in a supporting file — <c>patch_file</c>'s <see cref="WorkingDirectory.EditText"/>
    /// with its fuzzy match, line endings and BOM kept — under <see cref="WriteFile"/>'s rules.
    /// </summary>
    public static SkillEditResult EditFile(SkillRoots roots, SkillScope scope, string name, string path, string oldText, string newText, bool replaceAll, bool external, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(oldText);
        ArgumentNullException.ThrowIfNull(newText);
        return InFile(roots, scope, name, path, external, time, (files, relative, where) =>
        {
            var edited = files.EditText(relative, oldText, newText, replaceAll);
            return new SkillEditResult(edited.Outcome == FileOutcome.Ok ? SkillEditOutcome.FileEdited : SkillEditOutcome.FileRefused, where.Name, where.Scope, 0, FileText.Edited(edited), Path: relative);
        });
    }

    /// <summary>The skill's folder found as <see cref="Update"/> finds it, the path checked, then <paramref name="act"/> over a <see cref="WorkingDirectory"/> rooted there.</summary>
    private static SkillEditResult InFile(SkillRoots roots, SkillScope scope, string name, string path, bool external, TimeProvider time, Func<WorkingDirectory, string, (string Name, SkillScope Scope), SkillEditResult> act)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(time);
        name = name.Trim();
        string relative = path.Trim();
        if (!SkillFrontmatter.IsValidName(name))
        {
            return new SkillEditResult(SkillEditOutcome.BadName, name, scope);
        }

        if (!File.Exists(Path.Combine(roots.Of(scope), name, SkillCatalog.FileName)))
        {
            // Not here: where it lives, if anywhere — a file never makes a skill.
            switch (Find(roots, name, external))
            {
                case SkillScope.External:
                    return new SkillEditResult(SkillEditOutcome.ExternalReadOnly, name, SkillScope.External);
                case { } found:
                    scope = found;
                    break;
                default:
                    return new SkillEditResult(SkillEditOutcome.Missing, name, scope);
            }
        }

        string directory = Path.Combine(roots.Of(scope), name);
        if (SkillLock.IsLocked(directory))
        {
            return new SkillEditResult(SkillEditOutcome.Locked, name, scope);
        }

        var files = new WorkingDirectory(() => directory, time);
        var resolved = files.Resolve(relative, forWrite: true, out string full);
        if (resolved != FileOutcome.Ok)
        {
            return new SkillEditResult(SkillEditOutcome.FileRefused, name, scope, Detail: FileText.Error(resolved, relative, "write"), Path: relative);
        }

        if (IsProtected(files.Root, full))
        {
            return new SkillEditResult(SkillEditOutcome.ProtectedFile, name, scope, Path: relative);
        }

        // What the write replaces, read first (2026-10-02, the revisions): a file past the revision cap is not read at all.
        bool existed = false;
        string? previous = null;
        try
        {
            var info = new FileInfo(full);
            existed = info.Exists;
            if (existed && info.Length <= SkillRecordStore.MaxRevisionChars * 4L)
            {
                previous = Kept(WorkingDirectory.Decode(File.ReadAllBytes(full), out _));
            }
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            previous = null;
        }

        var result = act(files, relative, (name, scope));
        return result.Outcome is SkillEditOutcome.FileWritten or SkillEditOutcome.FileEdited ? result with { Previous = previous, Existed = existed } : result;
    }

    /// <summary>
    /// Whether <paramref name="full"/> is a file a file action never writes: the folder itself, the
    /// SKILL.md or the sidecar right under it (any case — the disk's), or anything in a
    /// <see cref="SkillCatalog.SkippedFolders"/> folder at any depth.
    /// </summary>
    private static bool IsProtected(string root, string full)
    {
        string relative = Path.GetRelativePath(root, full);
        if (relative == ".")
        {
            return true;
        }

        var segments = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 1
            && (string.Equals(segments[0], SkillCatalog.FileName, StringComparison.OrdinalIgnoreCase) || string.Equals(segments[0], SkillProvenance.FileName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(segments[0], SkillLock.FileName, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return segments.Take(segments.Length - 1).Any(s => SkillCatalog.SkippedFolders.Contains(s, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The <c>/skill</c> pane's move (2026-09-18): <paramref name="skill"/>'s folder renamed under
    /// the root of <paramref name="to"/>, the skill's files with it. Refused for the external root as
    /// the source or the destination (<see cref="SkillEditOutcome.ExternalReadOnly"/>), for the scope it
    /// is in already (<see cref="SkillEditOutcome.NothingToChange"/>), for a folder that is not right
    /// under its root or has no <c>SKILL.md</c> any more (<see cref="SkillEditOutcome.Missing"/>) and
    /// when the destination root already holds the folder's name (<see cref="SkillEditOutcome.Exists"/>,
    /// the result's scope the destination) — by the folder's name, since a skill's name may differ
    /// from its folder. A file failure is <see cref="SkillEditOutcome.Failed"/> with the detail.
    /// </summary>
    public static SkillEditResult Move(SkillRoots roots, Skill skill, SkillScope to)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(skill);
        if (skill.Scope == SkillScope.External || to == SkillScope.External)
        {
            return new SkillEditResult(SkillEditOutcome.ExternalReadOnly, skill.Name, SkillScope.External);
        }

        if (SkillLock.IsLocked(skill.Directory))
        {
            return new SkillEditResult(SkillEditOutcome.Locked, skill.Name, skill.Scope);
        }

        if (to == skill.Scope)
        {
            return new SkillEditResult(SkillEditOutcome.NothingToChange, skill.Name, skill.Scope);
        }

        if (!IsUnderItsRoot(roots, skill))
        {
            return new SkillEditResult(SkillEditOutcome.Missing, skill.Name, skill.Scope);
        }

        string destination = Path.Combine(roots.Of(to), skill.FolderName);
        if (Directory.Exists(destination) || File.Exists(destination))
        {
            return new SkillEditResult(SkillEditOutcome.Exists, skill.Name, to);
        }

        try
        {
            Directory.CreateDirectory(roots.Of(to));
            Directory.Move(skill.Directory, destination);
            return new SkillEditResult(SkillEditOutcome.Moved, skill.Name, to);
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return new SkillEditResult(SkillEditOutcome.Failed, skill.Name, to, Detail: ex.Message);
        }
    }

    /// <summary>
    /// The <c>/skill</c> pane's delete (2026-09-18, behind a confirmation; behind <c>Allow skill
    /// delete</c> too until that setting went on 2026-09-23, the user's call):<paramref name="skill"/>'s folder and everything in it. Refused for the external
    /// root (<see cref="SkillEditOutcome.ExternalReadOnly"/>) and for a folder that is not right under
    /// its root or has no <c>SKILL.md</c> any more (<see cref="SkillEditOutcome.Missing"/>) — the
    /// guard a recursive delete owes. A file failure is <see cref="SkillEditOutcome.Failed"/>.
    /// </summary>
    public static SkillEditResult Delete(SkillRoots roots, Skill skill)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(skill);
        if (skill.Scope == SkillScope.External)
        {
            return new SkillEditResult(SkillEditOutcome.ExternalReadOnly, skill.Name, SkillScope.External);
        }

        if (SkillLock.IsLocked(skill.Directory))
        {
            return new SkillEditResult(SkillEditOutcome.Locked, skill.Name, skill.Scope);
        }

        if (!IsUnderItsRoot(roots, skill))
        {
            return new SkillEditResult(SkillEditOutcome.Missing, skill.Name, skill.Scope);
        }

        try
        {
            Directory.Delete(skill.Directory, recursive: true);
            return new SkillEditResult(SkillEditOutcome.Deleted, skill.Name, skill.Scope);
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return new SkillEditResult(SkillEditOutcome.Failed, skill.Name, skill.Scope, Detail: ex.Message);
        }
    }

    /// <summary>
    /// The <c>/skills</c> pane's rename (2026-09-21, the user's ask): <paramref name="skill"/>'s folder
    /// renamed to <paramref name="newName"/> under its own root and the frontmatter's <c>name</c> line
    /// rewritten to match, the description, the other lines and the body carried through. Refused for
    /// the external root (<see cref="SkillEditOutcome.ExternalReadOnly"/>), a name that is not a skill
    /// name (<see cref="SkillEditOutcome.BadName"/> — the pane kebab-cases what was typed first), the
    /// name it has (<see cref="SkillEditOutcome.NothingToChange"/>), a folder not right under its root
    /// or without a <c>SKILL.md</c> (<see cref="SkillEditOutcome.Missing"/>), a folder of that name in
    /// its root already (<see cref="SkillEditOutcome.Exists"/>; on a case-insensitive disk a change of
    /// case alone is that too, and a kebab name is lower case anyway), a skill of that name in any root
    /// (<see cref="SkillEditOutcome.ExistsElsewhere"/>, the external one read whatever the setting says —
    /// the rename would shadow it the moment the switch flips) and a <c>SKILL.md</c> whose frontmatter
    /// cannot be read (<see cref="SkillEditOutcome.Unparseable"/>: the name line could not be rewritten).
    /// The folder moves first — the likely failure (a locked file) fails whole; a write failure after
    /// it leaves the folder renamed with the old <c>name</c> line, which the catalog still loads under
    /// its name-mismatch warning, so the list shows what happened. A file failure is
    /// <see cref="SkillEditOutcome.Failed"/> with the detail.
    /// </summary>
    public static SkillEditResult Rename(SkillRoots roots, Skill skill, string newName)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(skill);
        ArgumentNullException.ThrowIfNull(newName);
        newName = newName.Trim();
        if (skill.Scope == SkillScope.External)
        {
            return new SkillEditResult(SkillEditOutcome.ExternalReadOnly, skill.Name, SkillScope.External);
        }

        if (SkillLock.IsLocked(skill.Directory))
        {
            return new SkillEditResult(SkillEditOutcome.Locked, skill.Name, skill.Scope);
        }

        if (!SkillFrontmatter.IsValidName(newName))
        {
            return new SkillEditResult(SkillEditOutcome.BadName, newName, skill.Scope);
        }

        if (string.Equals(newName, skill.Name, StringComparison.Ordinal) && string.Equals(newName, skill.FolderName, StringComparison.Ordinal))
        {
            return new SkillEditResult(SkillEditOutcome.NothingToChange, skill.Name, skill.Scope);
        }

        if (!IsUnderItsRoot(roots, skill))
        {
            return new SkillEditResult(SkillEditOutcome.Missing, skill.Name, skill.Scope);
        }

        string destination = Path.Combine(roots.Of(skill.Scope), newName);
        if (Directory.Exists(destination) || File.Exists(destination))
        {
            return new SkillEditResult(SkillEditOutcome.Exists, newName, skill.Scope);
        }

        if (Find(roots, newName, external: true) is { } elsewhere)
        {
            return new SkillEditResult(elsewhere == SkillScope.External ? SkillEditOutcome.ExternalReadOnly : SkillEditOutcome.ExistsElsewhere, newName, elsewhere);
        }

        string text;
        try
        {
            text = WorkingDirectory.Decode(File.ReadAllBytes(skill.FilePath), out _);
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return new SkillEditResult(SkillEditOutcome.Failed, skill.Name, skill.Scope, Detail: ex.Message);
        }

        if (!SkillFrontmatter.TryParse(text, out var frontmatter, out string body, out string? problem))
        {
            return new SkillEditResult(SkillEditOutcome.Unparseable, skill.Name, skill.Scope, Detail: problem ?? "");
        }

        try
        {
            Directory.Move(skill.Directory, destination);
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return new SkillEditResult(SkillEditOutcome.Failed, skill.Name, skill.Scope, Detail: ex.Message);
        }

        return Write(destination, newName, skill.Scope, SkillFrontmatter.Write(newName, frontmatter!.Description, frontmatter.OtherLines, body), SkillEditOutcome.Renamed);
    }

    /// <summary>Whether <paramref name="skill"/>'s folder holds a <c>SKILL.md</c> and sits right under the root of its scope — what a move or a delete acts on, never a folder a hand-built record points elsewhere.</summary>
    private static bool IsUnderItsRoot(SkillRoots roots, Skill skill)
    {
        try
        {
            if (!File.Exists(skill.FilePath) || Path.GetDirectoryName(Path.GetFullPath(skill.Directory)) is not string parent)
            {
                return false;
            }

            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(roots.Of(skill.Scope)));
            return string.Equals(Path.TrimEndingDirectorySeparator(parent), root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return false;
        }
    }

    private static SkillEditResult? Check(string description, string instructions, string name, SkillScope scope)
    {
        if (description.Length == 0)
        {
            return new SkillEditResult(SkillEditOutcome.EmptyDescription, name, scope);
        }

        if (description.Length > SkillFrontmatter.MaxDescriptionLength)
        {
            return new SkillEditResult(SkillEditOutcome.DescriptionTooLong, name, scope, Length: description.Length);
        }

        if (string.IsNullOrWhiteSpace(instructions))
        {
            return new SkillEditResult(SkillEditOutcome.EmptyInstructions, name, scope);
        }

        if (instructions.Length > MaxInstructionChars)
        {
            return new SkillEditResult(SkillEditOutcome.InstructionsTooLong, name, scope, Length: instructions.Length);
        }

        return null;
    }

    private static SkillEditResult Write(string directory, string name, SkillScope scope, string content, SkillEditOutcome done)
    {
        string file = Path.Combine(directory, SkillCatalog.FileName);
        string temp = $"{file}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(directory);
            byte[] bytes = Utf8NoBom.GetBytes(content);
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, file, overwrite: true);
            return new SkillEditResult(done, name, scope, bytes.Length);
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            try { File.Delete(temp); } catch { /* best effort */ }
            return new SkillEditResult(SkillEditOutcome.Failed, name, scope, Detail: ex.Message);
        }
    }

    private static bool IsFileFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException;
}
