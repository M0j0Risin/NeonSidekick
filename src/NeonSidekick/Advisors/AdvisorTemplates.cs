using System.Text;
using System.Text.Json;
using NeonSidekick.Diagnostics;
using NeonSidekick.Files;
using NeonSidekick.Llm;
using NeonSidekick.Settings;

namespace NeonSidekick.Advisors;

/// <summary>
/// One advisor template (2026-11-22, the user's ask): a persona and the role, description and avatar an advisor made from it starts
/// with. <paramref name="Id"/> is its folder's name and a valid profile name (a new advisor is named after it); <paramref name="Avatar"/>
/// is "" for none; <paramref name="Yours"/> marks one from <c>&lt;home&gt;/advisors</c>. A seed, copied once: a profile's
/// <c>persona.md</c> written from it is the profile's own from then on.
/// </summary>
public sealed record AdvisorTemplate(string Id, string Role, string Description, string Avatar, string Persona, bool Yours);

/// <summary>A template folder or file that was left out or read in part: how the warning names it, and why.</summary>
public sealed record AdvisorTemplateProblem(string Shown, string Problem);

/// <summary>The templates one <see cref="AdvisorTemplates.All"/> found, by id, and what their files got wrong.</summary>
public sealed record AdvisorTemplateScan(IReadOnlyList<AdvisorTemplate> Templates, IReadOnlyList<AdvisorTemplateProblem> Problems)
{
    /// <summary>The ids, in list order.</summary>
    public IReadOnlyList<string> Ids => Templates.Select(t => t.Id).ToList();

    /// <summary>The template <paramref name="id"/> (any case), or null.</summary>
    public AdvisorTemplate? Find(string? id) =>
        Templates.FirstOrDefault(t => string.Equals(t.Id, (id ?? "").Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The template whose persona <paramref name="persona"/> is (both through <see cref="PersonaFile.Normalize(string)"/>: line endings,
    /// trimming and the cut at <see cref="PersonaFile.MaxLength"/>, as a profile's <c>Read</c> gives it), or null. Yours are seen first.
    /// </summary>
    public AdvisorTemplate? Matching(string? persona)
    {
        if (string.IsNullOrWhiteSpace(persona))
        {
            return null;
        }

        string wanted = PersonaFile.Normalize(persona);
        return Templates.OrderByDescending(t => t.Yours).FirstOrDefault(t => string.Equals(PersonaFile.Normalize(t.Persona), wanted, StringComparison.Ordinal));
    }
}

/// <summary>
/// The advisor templates (2026-11-22, the user's ask): the built-ins, every <c>assets/prompts/advisors/&lt;id&gt;/</c> embedded by the
/// csproj as <see cref="ResourcePrefix"/><c>&lt;id&gt;/persona.md</c> and <c>advisor.json</c> and read once, merged with your own in
/// <c>&lt;home&gt;/advisors/&lt;id&gt;/</c> (<see cref="DirectoryName"/>), read afresh at every <see cref="All"/> (small, and a folder
/// dropped in shows the next time a list opens). Yours win on an id; the list is by id, A to Z. <see cref="ThemeLibrary"/> and
/// <see cref="UI.ThemeCatalog"/>'s shape: a built-in that does not load is left out with a warning, never fatal; a home scan returns
/// its problems and never warns itself (<see cref="Report"/> says them, called only by the lists the user asks for), so completion
/// can read the ids on every keystroke without a warning each time. A folder without <c>persona.md</c> or whose name is no profile
/// name is skipped as a problem, a <c>.</c>-folder silently; a broken <c>advisor.json</c> reads as no role, description or avatar,
/// and an avatar that isn't one emoji is none.
/// </summary>
public static class AdvisorTemplates
{
    /// <summary>The csproj's <c>LogicalName</c> prefix for the template files.</summary>
    public const string ResourcePrefix = "prompts/advisors/";

    /// <summary>Your templates' folder under the home.</summary>
    public const string DirectoryName = "advisors";

    /// <summary>A template's persona file.</summary>
    public const string PersonaFileName = PersonaFile.FileName;

    /// <summary>A template's role, description and avatar.</summary>
    public const string InfoFileName = "advisor.json";

    private static readonly Lazy<AdvisorTemplateScan> s_embedded = new(LoadEmbedded, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The built-ins, by id.</summary>
    public static AdvisorTemplateScan BuiltIn => s_embedded.Value;

    /// <summary>Your templates' folder in <paramref name="home"/>.</summary>
    public static string Directory(string home) => Path.Combine(home, DirectoryName);

    /// <summary>The built-ins and <paramref name="home"/>'s own, yours winning on an id, by id; with the home scan's problems.</summary>
    public static AdvisorTemplateScan All(string? home)
    {
        var builtIns = BuiltIn.Templates;
        if (string.IsNullOrEmpty(home))
        {
            return new AdvisorTemplateScan(builtIns, []);
        }

        var problems = new List<AdvisorTemplateProblem>();
        var mine = ReadHome(Directory(home), problems);
        var ids = mine.Select(t => t.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var templates = mine.Concat(builtIns.Where(t => !ids.Contains(t.Id)))
            .OrderBy(t => t.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new AdvisorTemplateScan(templates, problems);
    }

    /// <summary>The template <paramref name="id"/> in <paramref name="home"/>'s list, or null.</summary>
    public static AdvisorTemplate? Find(string? home, string? id) => All(home).Find(id);

    /// <summary>Says each of <paramref name="scan"/>'s problems as a warning (the screen shows warnings in the transcript).</summary>
    public static void Report(AdvisorTemplateScan scan)
    {
        ArgumentNullException.ThrowIfNull(scan);
        foreach (var problem in scan.Problems)
        {
            DiagnosticLog.Warn(AdvisorText.Category, AdvisorText.TemplateProblem(problem.Shown, problem.Problem));
        }
    }

    /// <summary>
    /// The built-ins from <paramref name="resources"/>: logical name (<see cref="ResourcePrefix"/><c>&lt;id&gt;/&lt;file&gt;</c>, a
    /// backslash taken as a slash) to its text. The seam the tests feed; <see cref="BuiltIn"/> is this over the manifest. A file loose
    /// at the root or deeper than one folder is left out as a problem, as is an id that is no profile name and one with no persona.
    /// </summary>
    public static AdvisorTemplateScan Load(IEnumerable<KeyValuePair<string, string>> resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        var problems = new List<AdvisorTemplateProblem>();
        var personas = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var infos = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (resource, text) in resources.Select(r => (Normalize(r.Key), r.Value)).Where(r => r.Item1.StartsWith(ResourcePrefix, StringComparison.Ordinal)))
        {
            string shown = resource[ResourcePrefix.Length..];
            string[] parts = shown.Split('/');
            if (parts.Length != 2)
            {
                problems.Add(new AdvisorTemplateProblem(shown, AdvisorText.TemplateNotInFolder));
                continue;
            }

            if (string.Equals(parts[1], PersonaFileName, StringComparison.OrdinalIgnoreCase))
            {
                personas[parts[0]] = text;
            }
            else if (string.Equals(parts[1], InfoFileName, StringComparison.OrdinalIgnoreCase))
            {
                infos[parts[0]] = text;
            }
        }

        var templates = new List<AdvisorTemplate>();
        foreach (string id in personas.Keys.Concat(infos.Keys).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(i => i, StringComparer.OrdinalIgnoreCase))
        {
            if (!Profiles.IsValidName(id))
            {
                problems.Add(new AdvisorTemplateProblem(id, AdvisorText.TemplateBadId));
                continue;
            }

            if (!personas.TryGetValue(id, out string? persona) || persona.Trim().Length == 0)
            {
                problems.Add(new AdvisorTemplateProblem(id, AdvisorText.TemplateNoPersona));
                continue;
            }

            templates.Add(Build(id, persona, infos.GetValueOrDefault(id), yours: false, problems));
        }

        return new AdvisorTemplateScan(templates, problems);
    }

    /// <summary>
    /// <c>advisor.json</c>'s text read: null with <paramref name="problem"/> set when it isn't the shape (not JSON, not an object). Pure.
    /// </summary>
    public static AdvisorTemplateFile? Parse(string text, out string? problem)
    {
        problem = null;
        try
        {
            var file = JsonSerializer.Deserialize(text, AdvisorTemplateJsonContext.Default.AdvisorTemplateFile);
            if (file is null)
            {
                problem = AdvisorText.TemplateInfoNotObject;
            }

            return file;
        }
        catch (JsonException ex)
        {
            problem = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// Writes your template <paramref name="id"/> to <paramref name="home"/>'s folder: <c>persona.md</c> (the text with the platform's
    /// line endings and a final newline, as a profile's is written) and <c>advisor.json</c>, each replaced whole. A blank
    /// description or avatar is left out of the file. Throws <see cref="ArgumentException"/> for an id that is no profile name, and
    /// <see cref="IOException"/> / <see cref="UnauthorizedAccessException"/> like any write; the caller reports.
    /// </summary>
    public static void SaveYours(string home, string id, string persona, string role, string? description, string? avatar)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(home);
        ArgumentNullException.ThrowIfNull(persona);
        if (!Profiles.IsValidName(id))
        {
            throw new ArgumentException(Profiles.NameError, nameof(id));
        }

        string folder = Path.Combine(Directory(home), id);
        var info = new AdvisorTemplateFile
        {
            Role = (role ?? "").Trim(),
            Description = string.IsNullOrWhiteSpace(description) ? null : AdvisorText.Flat(description),
            Avatar = Emojis.AvatarRules.Normalize(avatar) is { Length: > 0 } face ? face : null,
        };
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        WorkingDirectory.WriteAtomically(Path.Combine(folder, PersonaFileName), utf8.GetBytes(persona.Trim().ReplaceLineEndings() + Environment.NewLine));
        string json = JsonSerializer.Serialize(info, AdvisorTemplateJsonContext.Default.AdvisorTemplateFile);
        WorkingDirectory.WriteAtomically(Path.Combine(folder, InfoFileName), utf8.GetBytes(json.ReplaceLineEndings("\n") + "\n"));
    }

    /// <summary>Whether your template <paramref name="id"/> has a folder in <paramref name="home"/>.</summary>
    public static bool HasYours(string home, string id) =>
        Profiles.IsValidName(id) && System.IO.Directory.Exists(Path.Combine(Directory(home), id));

    /// <summary>
    /// Deletes your template <paramref name="id"/>'s folder through the app's one delete (<see cref="FileDeletes.Remove"/>: to
    /// <paramref name="bin"/> whole when there is one, directly on a drive with none; a link removed itself). Returns how it went.
    /// Throws <see cref="DirectoryNotFoundException"/> for none, <see cref="IOException"/> / <see cref="UnauthorizedAccessException"/>
    /// like any delete (the bin's refusal among them, nothing deleted); the caller reports.
    /// </summary>
    public static DeleteWay DeleteYours(string home, string id, IRecycleBin? bin)
    {
        if (!HasYours(home, id))
        {
            throw new DirectoryNotFoundException(Path.Combine(Directory(home), id));
        }

        string full = Path.GetFullPath(Path.Combine(Directory(home), id));
        bool isLink = new DirectoryInfo(full).LinkTarget is not null;
        return FileDeletes.Remove(full, isDirectory: true, isLink, bin);
    }

    /// <summary>The first sentence of <paramref name="persona"/>'s first line that isn't a heading, for a template's band. Pure.</summary>
    public static string FirstSentence(string persona)
    {
        string line = (persona ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#')) ?? "";
        int stop = line.IndexOf(". ", StringComparison.Ordinal);
        return stop < 0 ? line : line[..(stop + 1)];
    }

    /// <summary>What a template's band says: its description, else its persona's first sentence. Pure.</summary>
    public static string Band(AdvisorTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        return template.Description.Length > 0 ? template.Description : FirstSentence(template.Persona);
    }

    private static AdvisorTemplate Build(string id, string persona, string? infoText, bool yours, List<AdvisorTemplateProblem> problems)
    {
        AdvisorTemplateFile? info = null;
        if (infoText is not null)
        {
            info = Parse(infoText, out string? problem);
            if (info is null)
            {
                problems.Add(new AdvisorTemplateProblem(id + "/" + InfoFileName, AdvisorText.TemplateInfoBroken(problem!)));
            }
        }

        return new AdvisorTemplate(
            id,
            AdvisorText.Flat(info?.Role ?? ""),
            AdvisorText.Flat(info?.Description ?? ""),
            Emojis.AvatarRules.Normalize(info?.Avatar),
            persona,
            yours);
    }

    /// <summary>The home folder's templates: each first-level folder with a <c>persona.md</c>, the problems added. A missing folder is none.</summary>
    private static List<AdvisorTemplate> ReadHome(string directory, List<AdvisorTemplateProblem> problems)
    {
        var templates = new List<AdvisorTemplate>();
        List<string> folders;
        try
        {
            if (!System.IO.Directory.Exists(directory))
            {
                return templates;
            }

            folders = System.IO.Directory.EnumerateDirectories(directory)
                .Where(d => !Path.GetFileName(d).StartsWith('.'))
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add(new AdvisorTemplateProblem(DirectoryName, AdvisorText.TemplateUnreadable(ex.Message)));
            return templates;
        }

        foreach (string folder in folders)
        {
            string id = Path.GetFileName(folder);
            if (!Profiles.IsValidName(id))
            {
                problems.Add(new AdvisorTemplateProblem(id, AdvisorText.TemplateBadId));
                continue;
            }

            try
            {
                string personaPath = Path.Combine(folder, PersonaFileName);
                if (!File.Exists(personaPath) || File.ReadAllText(personaPath) is not { } persona || persona.Trim().Length == 0)
                {
                    problems.Add(new AdvisorTemplateProblem(id, AdvisorText.TemplateNoPersona));
                    continue;
                }

                string infoPath = Path.Combine(folder, InfoFileName);
                templates.Add(Build(id, persona, File.Exists(infoPath) ? File.ReadAllText(infoPath) : null, yours: true, problems));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add(new AdvisorTemplateProblem(id, AdvisorText.TemplateUnreadable(ex.Message)));
            }
        }

        return templates;
    }

    private static string Normalize(string resource) => resource.Replace('\\', '/');

    /// <summary>The manifest's template files, loaded; each problem said as a warning.</summary>
    private static AdvisorTemplateScan LoadEmbedded()
    {
        var assembly = typeof(AdvisorTemplates).Assembly;
        var resources = new List<KeyValuePair<string, string>>();
        foreach (string resource in assembly.GetManifestResourceNames().Where(n => Normalize(n).StartsWith(ResourcePrefix, StringComparison.Ordinal)))
        {
            resources.Add(new(resource, App.EmbeddedText.Read(resource, "Advisor template")));
        }

        var loaded = Load(resources);
        Report(loaded);
        return loaded;
    }
}
