using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Skills;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>load_skill(name [, file])</c>: the second and third tiers of the Agent Skills disclosure.
/// With a name alone, the skill's full instructions — the SKILL.md body, frontmatter stripped,
/// wrapped in a tag that names it, with the folder and its bundled files listed
/// (<see cref="SkillText.Content"/>). With <c>file</c>, one bundled text file. The catalog in the
/// system prompt (<see cref="SkillsPrompt"/>) is what makes the model call it; the schema's
/// <c>enum</c> of names is rebuilt when the catalog's set changes, so the model cannot ask for a
/// skill that is not there — the specification's tip. Offered only while at least one skill is
/// installed (<c>ChatScreen.PrepareTurn</c>). A quiet tool: the transcript shows <see cref="Note"/>.
/// <para>With <c>preloaded</c> (2026-09-30, code review: a <c>/botchat</c> preloaded skill, its content already in the prompt, was
/// left out of the list but still loaded again when the model asked — the picture writer's directive even tells it to load the
/// skill the topic names, which is the one preloaded) a name alone for one of those answers <see cref="SkillText.AlreadyLoaded"/>,
/// not the content again. Its files are still read: those past the preload's cap stay listed by name, and may be wanted. The
/// schema keeps every name, so such a read stays valid.</para>
/// </summary>
public sealed class LoadSkillTool : AIFunction
{
    public const string ToolName = "load_skill";
    public const string NameArgument = "name";
    public const string FileArgument = "file";

    private readonly SkillCatalog _catalog;
    private readonly HashSet<string> _preloaded;
    private JsonElement _schema;
    private int _schemaVersion = -1;

    /// <param name="catalog">The skills it loads.</param>
    /// <param name="preloaded">The skills whose content the prompt already carries (any case): a name alone for one is answered <see cref="SkillText.AlreadyLoaded"/>.</param>
    public LoadSkillTool(SkillCatalog catalog, IReadOnlyCollection<string>? preloaded = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _preloaded = new HashSet<string>(preloaded ?? [], StringComparer.OrdinalIgnoreCase);
    }

    public override string Name => ToolName;

    public override string Description =>
        "Loads a skill's full instructions by name — the skills are listed in the system prompt with what each is for; call this before doing a task one covers. " +
        "With file, reads one of the text files bundled with the skill (a path from the list the instructions came with, relative to the skill's folder).";

    /// <summary>The schema with the offered names as the <c>enum</c> of <c>name</c>; rebuilt when the catalog's set changed.</summary>
    public override JsonElement JsonSchema
    {
        get
        {
            int version = _catalog.Version;
            if (version != _schemaVersion)
            {
                _schema = Schema(_catalog.Skills.Select(s => s.Name).ToList());
                _schemaVersion = version;
            }

            return _schema;
        }
    }

    /// <summary>The schema for <paramref name="names"/>. Pure; pinned by the tests.</summary>
    public static JsonElement Schema(IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        string list = string.Join(", ", names.Select(n => "\"" + JsonEncodedText.Encode(n).ToString() + "\""));
        return ToolSchema.Parse(
            $$"""
            {
              "type": "object",
              "properties": {
                "name": { "type": "string", "enum": [{{list}}], "description": "The skill to load, exactly as listed." },
                "file": { "type": "string", "description": "A file bundled with the skill to read instead of its instructions, relative to the skill's folder (references/guide.md). Leave it out for the instructions." }
              },
              "required": ["name"]
            }
            """);
    }

    /// <summary>The result for <paramref name="name"/> (and <paramref name="file"/>): the content, a file, or an <c>Error:</c> sentence.</summary>
    public string Describe(string name, string? file)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return SkillText.NoName;
        }

        var skill = _catalog.Find(name);
        if (skill is null)
        {
            return SkillText.Unknown(name, _catalog.Skills.Select(s => s.Name).ToList());
        }

        if (!string.IsNullOrWhiteSpace(file))
        {
            string relative = file.Trim();
            var read = SkillCatalog.ReadResource(skill, relative);
            return read.Outcome == SkillCatalog.ReadOutcome.Ok ? SkillText.File(skill.Name, relative, read.Text, read.Truncated) : SkillText.ReadError(skill, relative, read);
        }

        if (_preloaded.Contains(skill.Name))
        {
            return SkillText.AlreadyLoaded(skill.Name);
        }

        var body = SkillCatalog.ReadBody(skill);
        if (body.Outcome != SkillCatalog.ReadOutcome.Ok)
        {
            return SkillText.ReadError(skill, null, body);
        }

        var resources = SkillCatalog.Resources(skill, out bool more);
        return SkillText.Content(skill.Name, body.Text, skill.Directory, resources, more, body.Truncated);
    }

    /// <summary>
    /// The transcript's one dim line for a result: <c>loaded skill 'x' (1,234 characters)</c>,
    /// <c>read references/a.md of skill 'x'</c>, or the error sentence as it is. Pinned.
    /// </summary>
    public static string Note(string result)
    {
        ArgumentNullException.ThrowIfNull(result);
        const string content = "<skill_content name=\"";
        const string file = "<skill_file skill=\"";
        if (result.StartsWith(content, StringComparison.Ordinal))
        {
            string name = Attribute(result, content.Length);
            return $"loaded skill '{name}' ({result.Length.ToString("N0", CultureInfo.InvariantCulture)} characters)";
        }

        if (result.StartsWith(file, StringComparison.Ordinal))
        {
            string name = Attribute(result, file.Length);
            const string path = "\" path=\"";
            int at = result.IndexOf(path, StringComparison.Ordinal);
            string relative = at < 0 ? "" : Attribute(result, at + path.Length);
            return $"read {relative} of skill '{name}'";
        }

        return result;
    }

    private static string Attribute(string text, int from)
    {
        int end = text.IndexOf('"', from);
        return end < 0 ? text[from..] : text[from..end];
    }

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return new ValueTask<object?>(Describe(ToolArguments.ReadString(arguments, NameArgument), ToolArguments.ReadString(arguments, FileArgument)));
    }
}
