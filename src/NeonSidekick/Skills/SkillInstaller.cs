using System.Text;
using NeonSidekick.Files;

namespace NeonSidekick.Skills;

/// <summary>What an install may do, before it is asked (<see cref="SkillInstaller.Check"/>).</summary>
public enum SkillInstallOption
{
    /// <summary>No skill has the name: either writable root.</summary>
    New,

    /// <summary>The same skill, installed from the same source, is in <see cref="SkillInstallCheck.Scope"/>: a replace there, nowhere else.</summary>
    Update,

    /// <summary>A skill of the name that did not come from this source is in <see cref="SkillInstallCheck.Scope"/>: refused.</summary>
    Taken,

    /// <summary>A skill of the name is in the external folder the catalog reads: refused, the app never writes there.</summary>
    ExternalReadOnly,
}

/// <summary>The check's answer: what may be done and, for all but <see cref="SkillInstallOption.New"/>, the scope the name lives in.</summary>
public sealed record SkillInstallCheck(SkillInstallOption Option, SkillScope? Scope = null);

/// <summary>How an install ended: the scope and folder it went to, or the error.</summary>
public sealed record SkillInstallResult(bool Ok, bool Updated, SkillScope Scope, string Directory, string? Error = null)
{
    /// <summary>The SKILL.md an update replaced (2026-10-02, kept as a revision for <c>/skills revert</c>); null for a new install or one too long to keep.</summary>
    public string? Previous { get; init; }
}

/// <summary>
/// The file side of <c>/skills add</c> (2026-09-26), <see cref="SkillEditor"/>'s rules for a whole
/// folder. A name is one skill across the roots, as there: a skill of the name anywhere the catalog
/// reads blocks a new install — except a skill whose <see cref="SkillProvenance"/> says it came from
/// the same repository and path, which is replaced where it lives (an update; a copy in the other
/// root would only shadow it). A hand-made or learned skill is never overwritten: the user renames
/// or deletes it on <c>/skills</c> first.
///
/// <para><see cref="Install"/> stages the folder beside the root (<c>.skill-install-&lt;guid&gt;</c>:
/// the same volume, so the move is a rename, and outside the root, so a scan never sees half a
/// skill), checks every target lies inside the stage, writes the sidecar last and moves the stage
/// into place; a replace parks the old folder first and puts it back when the move fails. The
/// folder is always named after the frontmatter's <c>name</c>, so the catalog loads it without a
/// warning. A file failure is an error, never an exception.</para>
/// </summary>
public static class SkillInstaller
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>What installing <paramref name="name"/> from <paramref name="origin"/> may do.</summary>
    /// <param name="external">Whether the catalog reads the external root (the <c>Use external skills</c> setting).</param>
    public static SkillInstallCheck Check(SkillRoots roots, string name, SkillProvenance origin, bool external)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(origin);
        if (SkillEditor.Find(roots, name, external) is not { } scope)
        {
            return new SkillInstallCheck(SkillInstallOption.New);
        }

        if (scope == SkillScope.External)
        {
            return new SkillInstallCheck(SkillInstallOption.ExternalReadOnly, scope);
        }

        var installed = SkillProvenance.Read(Path.Combine(roots.Of(scope), name));
        return installed is not null && installed.SameOrigin(origin)
            ? new SkillInstallCheck(SkillInstallOption.Update, scope)
            : new SkillInstallCheck(SkillInstallOption.Taken, scope);
    }

    /// <summary>
    /// Writes <paramref name="candidate"/> from <paramref name="archive"/> as <c>&lt;root&gt;\&lt;name&gt;</c>
    /// with its sidecar; <paramref name="replace"/> replaces a folder already there (an
    /// <see cref="SkillInstallOption.Update"/>), which is refused otherwise.
    /// </summary>
    public static SkillInstallResult Install(SkillRoots roots, SkillScope scope, SkillArchive archive, SkillCandidate candidate, SkillProvenance provenance, bool replace)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(provenance);
        if (scope == SkillScope.External)
        {
            throw new ArgumentException("The external root is never written.", nameof(scope));
        }

        string name = candidate.Name;
        string root = Path.GetFullPath(roots.Of(scope));
        string target = Path.Combine(root, name);
        if (candidate.Refusal is { } refusal)
        {
            return new SkillInstallResult(false, false, scope, target, refusal);
        }

        if (!SkillFrontmatter.IsValidName(name))
        {
            return new SkillInstallResult(false, false, scope, target, SkillInstallText.BadNameRefusal(name));
        }

        bool exists = Directory.Exists(target) || File.Exists(target);
        if (exists && !replace)
        {
            return new SkillInstallResult(false, false, scope, target, SkillInstallText.TakenError(name, scope));
        }

        string parent = Path.GetDirectoryName(root) ?? root;
        string stage = Path.Combine(parent, ".skill-install-" + Guid.NewGuid().ToString("N"));
        string? parked = null;
        bool moved = false;
        try
        {
            Directory.CreateDirectory(stage);
            foreach (var file in candidate.Files)
            {
                string path = Path.GetFullPath(Path.Combine(stage, file.Path.Replace('/', Path.DirectorySeparatorChar)));
                if (!WorkingDirectory.IsInside(stage, path) || path.Length == stage.Length)
                {
                    return new SkillInstallResult(false, false, scope, target, SkillInstallText.UnsafePathRefusal(file.Path));
                }

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (!archive.Extract(file, path))
                {
                    return new SkillInstallResult(false, false, scope, target, SkillInstallText.FileTooBigRefusal(file.Path, file.Length, SkillArchive.MaxSkillFileBytes));
                }
            }

            File.WriteAllText(Path.Combine(stage, SkillProvenance.FileName), provenance.ToJson(), Utf8NoBom);
            Directory.CreateDirectory(root);
            string? previous = null;
            if (exists)
            {
                // What the update replaces, for the revision (2026-10-02): the SKILL.md alone, the supporting files go with the folder.
                string old = Path.Combine(target, SkillCatalog.FileName);
                if (File.Exists(old) && new FileInfo(old).Length <= SkillRecordStore.MaxRevisionChars)
                {
                    previous = WorkingDirectory.Decode(File.ReadAllBytes(old), out _);
                }

                parked = Path.Combine(parent, ".skill-old-" + Guid.NewGuid().ToString("N"));
                Directory.Move(target, parked);
            }

            try
            {
                Directory.Move(stage, target);
                moved = true;
            }
            catch (Exception) when (parked is not null)
            {
                Directory.Move(parked, target);
                parked = null;
                throw;
            }

            return new SkillInstallResult(true, exists, scope, target) { Previous = previous };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or InvalidDataException)
        {
            return new SkillInstallResult(false, false, scope, target, SkillInstallText.FailedError(name, ex.Message));
        }
        finally
        {
            TryDelete(stage);
            if (parked is not null && moved)
            {
                TryDelete(parked);
            }
            else if (parked is not null)
            {
                // The old folder could not be put back: left where it is, never deleted.
                Diagnostics.DiagnosticLog.Warn(SkillCatalog.Category, $"The previous copy of '{name}' is kept at {parked}.");
            }
        }
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostics.DiagnosticLog.Warn(SkillCatalog.Category, $"Could not remove {directory}: {ex.Message}");
        }
    }
}
