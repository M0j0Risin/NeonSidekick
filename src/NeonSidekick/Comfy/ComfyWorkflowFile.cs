using System.Globalization;
using System.Text;

namespace NeonSidekick.Comfy;

/// <summary>The sidecar's values as the wizard writes them (<see cref="ComfyWorkflowFile.Sidecar"/>).</summary>
public sealed record ComfySidecar(string Description, ComfyFamily Family, int? Width, int? Height, int? Steps, double? Cfg, double? Denoise, string Negative);

/// <summary>
/// Writing a new workflow into a <c>comfy</c> folder (later on 2026-09-24, the add-workflow wizard's save): the sidecar
/// <c>&lt;name&gt;.md</c> first, then <c>&lt;name&gt;.json</c> — the catalog lists a workflow by its <c>.json</c>, so it
/// never sees one half written. A name must be a plain file stem (<see cref="IsValidName"/>) and free in every folder
/// the catalog reads, so the new workflow shadows nothing and nothing shadows it.
/// </summary>
public static class ComfyWorkflowFile
{
    /// <summary>The rule, in the words the wizard refuses a name with. Pinned.</summary>
    public const string NameRule = "1 to 64 lowercase letters, digits, hyphens and underscores, starting with a letter or digit";

    /// <summary>A name that is a plain, portable file stem: <see cref="NameRule"/>.</summary>
    public static bool IsValidName(string? name) =>
        !string.IsNullOrEmpty(name) && name.Length <= 64 && (char.IsAsciiLetterLower(name[0]) || char.IsAsciiDigit(name[0]))
        && name.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '_');

    /// <summary>A name as the wizard suggests it from a checkpoint or a file: lower-cased, anything else a hyphen, the runs folded, cut to 64.</summary>
    public static string Suggest(string from)
    {
        ArgumentNullException.ThrowIfNull(from);
        string stem = Path.GetFileNameWithoutExtension(from).ToLowerInvariant();
        var sb = new StringBuilder(stem.Length);
        foreach (char c in stem)
        {
            char k = char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_' ? c : '-';
            if (!(k == '-' && (sb.Length == 0 || sb[^1] == '-')))
            {
                sb.Append(k);
            }
        }

        string name = sb.ToString().Trim('-');
        return name.Length > 64 ? name[..64].TrimEnd('-') : name;
    }

    /// <summary>The sidecar text: the fences around the values set, then the description's tips line when given. Pure; pinned.</summary>
    public static string Sidecar(ComfySidecar values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var sb = new StringBuilder("---\n");
        if (values.Description.Length > 0)
        {
            sb.Append("description: ").Append(Skills.SkillFrontmatter.Scalar(values.Description)).Append('\n');
        }

        sb.Append("family: ").Append(ComfyFamilies.Name(values.Family)).Append('\n');
        if (values.Width is { } w) sb.Append("width: ").Append(w.ToString(CultureInfo.InvariantCulture)).Append('\n');
        if (values.Height is { } h) sb.Append("height: ").Append(h.ToString(CultureInfo.InvariantCulture)).Append('\n');
        if (values.Steps is { } s) sb.Append("steps: ").Append(s.ToString(CultureInfo.InvariantCulture)).Append('\n');
        if (values.Cfg is { } c) sb.Append("cfg: ").Append(c.ToString(CultureInfo.InvariantCulture)).Append('\n');
        if (values.Denoise is { } d) sb.Append("denoise: ").Append(d.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("negative: ").Append(values.Negative.Length == 0 ? "\"\"" : Skills.SkillFrontmatter.Scalar(values.Negative)).Append('\n');
        return sb.Append("---\n").ToString();
    }

    /// <summary>
    /// Writes <paramref name="name"/> into <paramref name="folder"/> (made when missing): null when done, else the
    /// sentence — a bad name, a name already in one of <paramref name="catalogRoots"/>, or the IO failure.
    /// </summary>
    public static string? Add(string folder, IReadOnlyList<string> catalogRoots, string name, string graphJson, string sidecar)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(catalogRoots);
        ArgumentNullException.ThrowIfNull(graphJson);
        ArgumentNullException.ThrowIfNull(sidecar);
        if (!IsValidName(name))
        {
            return "the name must be " + NameRule;
        }

        if (Taken(catalogRoots.Append(folder), name) is { } where)
        {
            return ComfyText.NameTaken(name, where);
        }

        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, name + ".md"), sidecar);
            File.WriteAllText(Path.Combine(folder, name + ".json"), graphJson);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    /// <summary>The folder among <paramref name="roots"/> already holding <c>&lt;name&gt;.json</c>, or null.</summary>
    public static string? Taken(IEnumerable<string> roots, string name)
    {
        ArgumentNullException.ThrowIfNull(roots);
        foreach (string root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(Path.Combine(root, name + ".json")))
            {
                return root;
            }
        }

        return null;
    }
}
