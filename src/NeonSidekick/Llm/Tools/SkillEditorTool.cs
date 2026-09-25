using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Skills;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>skill_editor(action, scope, name, description, instructions, summary)</c>: the model writes a skill
/// for later sessions — a new one (<c>create</c>) or a changed description and/or body for an
/// existing one (<c>update</c>) — under the profile's or the global skills folder, never the
/// external one. The tool writes the frontmatter itself (<see cref="SkillFrontmatter.Write"/>), so
/// every skill it makes is one the catalog reads; the folder is the name. Structured fields, not a
/// raw file: a model that writes YAML by hand writes invalid YAML often enough. The new skill is in
/// the catalog from the next turn on (<c>ChatScreen.PrepareTurn</c> rescans). Quiet: the result is
/// the dim line. A name is one skill across the folders (2026-09-16): <c>create</c> is refused for a
/// name that is a skill anywhere the catalog reads, <c>update</c> changes the skill where it lives
/// whatever scope is passed — so the model can never shadow a skill with a copy. <c>summary</c>
/// (2026-09-19, the user's ask) is the model's sentence on what changed, kept on
/// <see cref="LastResult"/> for the reflection's transcript line (printed whenever it is given);
/// the tool's own answer never repeats it, and a normal turn's call may pass it unread. <c>scope</c> is
/// optional since 2026-09-24 (the user's report: models sent <c>""</c> more often than not, were refused and
/// retried): blank means <c>profile</c>, and <c>update</c> takes any value, since it changes the skill where it
/// lives anyway; only a <c>create</c> naming a root that is not writable is still refused.
/// </summary>
public sealed class SkillEditorTool : AIFunction
{
    public const string ToolName = "skill_editor";
    public const string ActionArgument = "action";
    public const string ScopeArgument = "scope";
    public const string NameArgument = "name";
    public const string DescriptionArgument = "description";
    public const string InstructionsArgument = "instructions";
    public const string SummaryArgument = "summary";

    public const string CreateAction = "create";
    public const string UpdateAction = "update";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "action": { "type": "string", "enum": ["create", "update"], "description": "create writes a new skill (the name must not be a skill in any folder yet); update changes an existing one's description, instructions or both, wherever it lives." },
            "scope": { "type": "string", "enum": ["profile", "global"], "description": "Where a new skill goes: profile (the default) keeps it for this profile only, global for every profile. Optional; ignored for update, which changes the skill where it already is." },
            "name": { "type": "string", "description": "The skill's name and folder: 1 to 64 lowercase letters, digits and hyphens (pdf-processing), not starting or ending with a hyphen." },
            "description": { "type": "string", "description": "One or two sentences on what the skill does and when to use it, with the words a task would contain; at most 1,024 characters. Required for create." },
            "instructions": { "type": "string", "description": "The skill's Markdown body: the steps to follow, examples, edge cases. Required for create; replaces the whole body on update." },
            "summary": { "type": "string", "description": "One or two sentences on what you changed and why, for the user to read; never part of the skill. Optional." }
          },
          "required": ["action", "name"]
        }
        """);

    private readonly Func<SkillRoots> _roots;
    private readonly Func<bool> _external;

    /// <param name="roots">Read per call: the profile root moves with a profile switch.</param>
    /// <param name="external">Read per call: whether the external folder is in the catalog (<c>Agent skills</c> and <c>Use external skills</c> both on), so a skill there blocks its name.</param>
    public SkillEditorTool(Func<SkillRoots> roots, Func<bool> external)
    {
        _roots = roots ?? throw new ArgumentNullException(nameof(roots));
        _external = external ?? throw new ArgumentNullException(nameof(external));
    }

    public override string Name => ToolName;

    public override string Description =>
        "Creates or updates a skill: a named folder of instructions kept for later sessions, listed in the system prompt from the next reply on and loaded with " + LoadSkillTool.ToolName + ". " +
        "Use it when the user asks you to remember how to do something, or when a procedure you worked out is worth keeping. " +
        "The description should say what the skill does and when to use it; the instructions the steps. " +
        "To change a skill that exists, use update — never create a copy under another scope. Never deletes anything.";

    public override JsonElement JsonSchema => Schema;

    /// <summary>
    /// The editor's result of the last call, for a host that runs the tool itself
    /// (<see cref="Skills.SkillLearner"/> stops after the first successful write); null before the
    /// first call and after one refused for its action or scope.
    /// </summary>
    public SkillEditResult? LastResult { get; private set; }

    /// <summary>The result for the parsed arguments: the sentence for the outcome, or the argument error; <paramref name="summary"/> rides <see cref="LastResult"/> through <see cref="SkillText.CleanSummary"/>.</summary>
    public string Describe(string action, string scope, string name, string? description, string? instructions, string? summary = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(name);
        LastResult = null;
        bool create;
        switch (action.Trim().ToLowerInvariant())
        {
            case CreateAction: create = true; break;
            case UpdateAction: create = false; break;
            default: return SkillText.BadAction(action);
        }

        // Blank is the profile; an update redirects to where the skill lives, so its scope is never refused.
        if (!SkillScopes.TryParseWritable(scope, out var where) && create && !string.IsNullOrWhiteSpace(scope))
        {
            return SkillText.BadScope(scope);
        }

        bool external = _external();
        var result = create
            ? SkillEditor.Create(_roots(), where, name, description ?? "", instructions ?? "", external)
            : SkillEditor.Update(_roots(), where, name, description, instructions, external);
        LastResult = result with { Summary = SkillText.CleanSummary(summary) };
        return SkillText.Edited(result);
    }

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return new ValueTask<object?>(Describe(
            ToolArguments.ReadString(arguments, ActionArgument),
            ToolArguments.ReadString(arguments, ScopeArgument),
            ToolArguments.ReadString(arguments, NameArgument),
            ToolArguments.ReadString(arguments, DescriptionArgument),
            ToolArguments.ReadString(arguments, InstructionsArgument),
            ToolArguments.ReadString(arguments, SummaryArgument)));
    }
}
