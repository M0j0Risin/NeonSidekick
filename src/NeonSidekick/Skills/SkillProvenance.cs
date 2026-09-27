using System.Text.Json;
using System.Text.Json.Serialization;

namespace NeonSidekick.Skills;

/// <summary>
/// Where an installed skill came from (<c>/skills add</c>, 2026-09-26): the sidecar
/// <see cref="FileName"/> written last into the skill's folder. It decides the collision rule — a
/// skill installed from the same repository and path may be replaced by a new install (an update),
/// one without it (hand-made, learned, dropped in) never is — and carries what a later
/// <c>/skills update</c> would need: the ref, the commit, the archive's URL. Namespaced so a
/// skill's own files never collide with it; <see cref="SkillCatalog.Resources"/> leaves it out of
/// the files <c>load_skill</c> lists.
/// </summary>
public sealed class SkillProvenance
{
    /// <summary>The sidecar's name.</summary>
    public const string FileName = ".neon-source.json";

    /// <summary><see cref="GitHubKind"/> or <see cref="ZipKind"/>.</summary>
    public const string GitHubKind = "github";
    public const string ZipKind = "zip";

    [JsonPropertyName("source")]
    public string Source { get; set; } = GitHubKind;

    /// <summary><c>owner/repo</c>, or the zip's URL.</summary>
    [JsonPropertyName("repo")]
    public string Repo { get; set; } = "";

    [JsonPropertyName("ref")]
    public string Ref { get; set; } = SkillSource.DefaultRef;

    [JsonPropertyName("commit")]
    public string? Commit { get; set; }

    /// <summary>The skill folder's path in the archive, forward slashes; <c>""</c> for its root.</summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    /// <summary>The archive downloaded, or — fetched file by file from the listing — the skill's folder on github.com at the commit.</summary>
    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    /// <summary>skills.sh's id when a search found it (<c>owner/repo/skill</c>).</summary>
    [JsonPropertyName("skillsShId")]
    public string? SkillsShId { get; set; }

    [JsonPropertyName("installedAt")]
    public DateTimeOffset InstalledAt { get; set; }

    [JsonPropertyName("files")]
    public int Files { get; set; }

    [JsonPropertyName("bytes")]
    public long Bytes { get; set; }

    /// <summary>Whether a new install from <paramref name="other"/> is this skill again: the same source, repository and path (ordinal ignoring case, as GitHub does).</summary>
    public bool SameOrigin(SkillProvenance other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return string.Equals(Source, other.Source, StringComparison.Ordinal)
            && string.Equals(Repo, other.Repo, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path, other.Path, StringComparison.Ordinal);
    }

    /// <summary>The sidecar in <paramref name="skillDirectory"/>, or null when there is none or it does not read. Never throws.</summary>
    public static SkillProvenance? Read(string skillDirectory)
    {
        ArgumentNullException.ThrowIfNull(skillDirectory);
        try
        {
            string path = System.IO.Path.Combine(skillDirectory, FileName);
            return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllText(path), SkillsJsonContext.Default.SkillProvenance) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The sidecar's text.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, SkillsJsonContext.Default.SkillProvenance);
}

/// <summary>One hit of skills.sh's search (<see cref="SkillHub.SearchUrl"/>). No description: the endpoint gives none.</summary>
public sealed class SkillsShSkill
{
    /// <summary><c>owner/repo/skill</c>.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    /// <summary><c>owner/repo</c>.</summary>
    [JsonPropertyName("source")]
    public string Source { get; set; } = "";

    [JsonPropertyName("skillId")]
    public string SkillId { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("installs")]
    public long Installs { get; set; }
}

/// <summary>The search's answer; the fields not read are ignored.</summary>
public sealed class SkillsShSearchResponse
{
    [JsonPropertyName("skills")]
    public List<SkillsShSkill>? Skills { get; set; }
}

/// <summary>One entry of the Git Trees API's listing (<see cref="SkillHub.OpenAsync"/>): a <c>blob</c> (a file; mode <c>120000</c> a symbolic link), a <c>tree</c> or a <c>commit</c> (a submodule). The fields not read are ignored.</summary>
public sealed class GitHubTreeEntry
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("size")]
    public long Size { get; set; }
}

/// <summary>The Git Trees API's answer; <see cref="Truncated"/> when the listing was cut (100,000 entries or 7 MB).</summary>
public sealed class GitHubTree
{
    [JsonPropertyName("truncated")]
    public bool Truncated { get; set; }

    [JsonPropertyName("tree")]
    public List<GitHubTreeEntry>? Tree { get; set; }
}

/// <summary>AOT-safe JSON context for the skill installer's types, a sibling of <see cref="Speech.SpeechJsonContext"/>. Reflection serialisation is off project-wide.</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(SkillProvenance))]
[JsonSerializable(typeof(SkillsShSearchResponse))]
[JsonSerializable(typeof(GitHubTree))]
internal sealed partial class SkillsJsonContext : JsonSerializerContext;
