using System.Security.Cryptography;

namespace NeonSidekick.Skills;

/// <summary>
/// What a reflection's <c>skill_editor</c> may write (2026-10-02, the reflection audit; one guard per reflection, never the main chat's):
/// <list type="bullet">
/// <item>a rewrite of a skill's instructions, or a supporting file written or edited, only for a skill its <c>load_skill</c> served in this
/// reflection (<see cref="Loaded"/>) — the instruction had always asked for that, nothing held a model to it, and a body rewritten blind
/// loses what the model never read;</item>
/// <item>and only while the SKILL.md is what that load served: a turn (or the user) that changed the skill while the reflection ran
/// would otherwise lose its change to the reflection's stale copy;</item>
/// <item>nothing at all in a skill installed with <c>/skills add</c> under <see cref="ReflectionInstalledPolicy.ReadOnly"/>.</item>
/// </list>
/// A description-only update passes the first two (the catalog shows the description). Each refusal is an <c>Error:</c> sentence the
/// model reads and may act on within <c>Reflection max requests</c>; the load counts by the SKILL.md's SHA-256.
/// </summary>
public sealed class ReflectionWriteGuard
{
    private readonly Func<SkillRoots> _roots;
    private readonly bool _external;
    private readonly ReflectionInstalledPolicy _installed;
    private readonly Dictionary<string, string> _loaded = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="roots">The roots the reflection writes under (fixed at its decision).</param>
    /// <param name="external">Whether the external root is read, as the editor reads it.</param>
    /// <param name="installed">What the reflection may do to an installed skill (<c>Reflection installed skills</c>).</param>
    public ReflectionWriteGuard(Func<SkillRoots> roots, bool external, ReflectionInstalledPolicy installed)
    {
        _roots = roots ?? throw new ArgumentNullException(nameof(roots));
        _external = external;
        _installed = installed;
    }

    /// <summary>The policy in force for installed skills.</summary>
    public ReflectionInstalledPolicy Installed => _installed;

    /// <summary><c>load_skill</c> served <paramref name="skill"/>'s instructions: what its SKILL.md held then is what a rewrite may replace.</summary>
    public void Loaded(Skill skill)
    {
        ArgumentNullException.ThrowIfNull(skill);
        if (Hash(skill.FilePath) is { } hash)
        {
            _loaded[skill.FolderName] = hash;
        }
    }

    /// <summary>
    /// Null when the write may go ahead; else the refusal's sentence. <paramref name="rewrite"/> is true for new instructions and the file
    /// actions, false for a description alone. A name that is no skill in a writable root passes: the editor answers it.
    /// </summary>
    public string? Refusal(string name, bool rewrite)
    {
        ArgumentNullException.ThrowIfNull(name);
        name = name.Trim();
        var roots = _roots();
        if (SkillEditor.Find(roots, name, _external) is not { } scope || scope == SkillScope.External)
        {
            return null;
        }

        string directory = Path.Combine(roots.Of(scope), name);
        if (_installed == ReflectionInstalledPolicy.ReadOnly && SkillProvenance.Read(directory) is { } origin)
        {
            return SkillText.InstalledReadOnly(name, origin.Repo);
        }

        if (!rewrite)
        {
            return null;
        }

        if (!_loaded.TryGetValue(name, out string? seen))
        {
            return SkillText.LoadBeforeRewrite(name);
        }

        return Hash(Path.Combine(directory, SkillCatalog.FileName)) is { } now && !string.Equals(now, seen, StringComparison.Ordinal)
            ? SkillText.ChangedSinceLoad(name)
            : null;
    }

    /// <summary>The file's SHA-256 as hex, or null when it cannot be read.</summary>
    private static string? Hash(string file)
    {
        try
        {
            return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
