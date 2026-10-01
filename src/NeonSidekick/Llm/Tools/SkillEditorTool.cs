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
///
/// <para><c>write_file</c> and <c>edit_file</c> (2026-09-27, the user's ask: a skill's data files were
/// readable through <c>load_skill</c> and changeable by nothing) write the files beside the SKILL.md of an
/// existing skill — whole, or <c>old_text</c> → <c>new_text</c> as <c>patch_file</c> does — through
/// <see cref="SkillEditor.WriteFile"/> / <see cref="SkillEditor.EditFile"/>; never the SKILL.md itself, never a
/// delete. Offered only with a <see cref="SkillFileAccess"/>: the main chat always, a reflection when
/// <c>Reflection edit supporting files</c> is on; without it the schema names create and update alone.</para>
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
    public const string PathArgument = "path";
    public const string ContentArgument = "content";
    public const string OldTextArgument = "old_text";
    public const string NewTextArgument = "new_text";
    public const string ReplaceAllArgument = "replace_all";

    public const string CreateAction = "create";
    public const string UpdateAction = "update";
    public const string WriteFileAction = "write_file";
    public const string EditFileAction = "edit_file";

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

    private static readonly JsonElement FileSchema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "action": { "type": "string", "enum": ["create", "update", "write_file", "edit_file"], "description": "create writes a new skill (the name must not be a skill in any folder yet); update changes an existing one's description, instructions or both, wherever it lives; write_file creates or replaces a supporting file of an existing skill (a mapping, examples, a script) with content; edit_file replaces old_text with new_text in one." },
            "scope": { "type": "string", "enum": ["profile", "global"], "description": "Where a new skill goes: profile (the default) keeps it for this profile only, global for every profile. Optional; ignored for the other actions, which work on the skill where it already is." },
            "name": { "type": "string", "description": "The skill's name and folder: 1 to 64 lowercase letters, digits and hyphens (pdf-processing), not starting or ending with a hyphen." },
            "description": { "type": "string", "description": "One or two sentences on what the skill does and when to use it, with the words a task would contain; at most 1,024 characters. Required for create." },
            "instructions": { "type": "string", "description": "The skill's Markdown body: the steps to follow, examples, edge cases. Required for create; replaces the whole body on update." },
            "path": { "type": "string", "description": "write_file / edit_file: the supporting file, relative to the skill folder (data/mapping.json), as load_skill lists it. Never SKILL.md; change that with update." },
            "content": { "type": "string", "description": "write_file: the whole new content of the file." },
            "old_text": { "type": "string", "description": "edit_file: the text to replace, copied from the file as load_skill shows it; it must occur exactly once unless replace_all is true. Small differences in spacing, indentation, quotes and dashes are tolerated." },
            "new_text": { "type": "string", "description": "edit_file: the text to put in its place. Empty removes old_text." },
            "replace_all": { "type": "boolean", "description": "edit_file: true replaces every occurrence of old_text. Default false: exactly one." },
            "summary": { "type": "string", "description": "One or two sentences on what you changed and why, for the user to read; never part of the skill. Optional." }
          },
          "required": ["action", "name"]
        }
        """);

    private readonly Func<SkillRoots> _roots;
    private readonly Func<bool> _external;
    private readonly SkillFileAccess? _files;
    private readonly Action<SkillRoots, SkillEditResult>? _edited;

    /// <param name="roots">Read per call: the profile root moves with a profile switch.</param>
    /// <param name="external">Read per call: whether the external folder is in the catalog (<c>Agent skills</c> and <c>Use external skills</c> both on), so a skill there blocks its name.</param>
    /// <param name="files">What the file actions need, or null to offer create and update alone (2026-09-27).</param>
    /// <param name="edited">
    /// Told every result with the roots it was written under (2026-09-30, the skill records, <see cref="SkillRecords.Edited"/>):
    /// a refusal is told as well, and the listener keeps only the writes.
    /// </param>
    public SkillEditorTool(Func<SkillRoots> roots, Func<bool> external, SkillFileAccess? files = null, Action<SkillRoots, SkillEditResult>? edited = null)
    {
        _roots = roots ?? throw new ArgumentNullException(nameof(roots));
        _edited = edited;
        _external = external ?? throw new ArgumentNullException(nameof(external));
        _files = files;
    }

    /// <summary>Whether <c>write_file</c> and <c>edit_file</c> are offered.</summary>
    public bool OffersFiles => _files is not null;

    public override string Name => ToolName;

    public override string Description =>
        "Creates or updates a skill: a named folder of instructions kept for later sessions, listed in the system prompt from the next reply on and loaded with " + LoadSkillTool.ToolName + ". " +
        "Use it when the user asks you to remember how to do something, or when a procedure you worked out is worth keeping. " +
        "The description should say what the skill does and when to use it; the instructions the steps. " +
        "To change a skill that exists, use update — never create a copy under another scope. Never deletes anything." +
        (_files is null ? "" : " " + FilesSentence);

    /// <summary>The description's file-actions sentence, only when they are offered (2026-09-27).</summary>
    public const string FilesSentence =
        "A skill's supporting files (the ones load_skill lists beside its instructions) are kept current with write_file (the whole file) or edit_file (old_text to new_text), paths relative to the skill folder; the SKILL.md itself only through create and update.";

    public override JsonElement JsonSchema => _files is null ? Schema : FileSchema;

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
            default: return SkillText.BadAction(action, _files is not null);
        }

        // Blank is the profile; an update redirects to where the skill lives, so its scope is never refused.
        if (!SkillScopes.TryParseWritable(scope, out var where) && create && !string.IsNullOrWhiteSpace(scope))
        {
            return SkillText.BadScope(scope);
        }

        bool external = _external();
        var roots = _roots();
        var result = create
            ? SkillEditor.Create(roots, where, name, description ?? "", instructions ?? "", external)
            : SkillEditor.Update(roots, where, name, description, instructions, external);
        LastResult = result with { Summary = SkillText.CleanSummary(summary) };
        _edited?.Invoke(roots, result);
        return SkillText.Edited(result);
    }

    /// <summary><c>write_file</c>: <paramref name="content"/> as the whole of <paramref name="path"/> in skill <paramref name="name"/> (2026-09-27).</summary>
    public string DescribeWrite(string name, string path, string content, string? summary = null) =>
        DescribeFile(path, files => SkillEditor.WriteFile(_roots(), SkillScope.Profile, name, path, content, _external(), files.SafeEdits(), files.Time), summary);

    /// <summary><c>edit_file</c>: <paramref name="oldText"/> → <paramref name="newText"/> in <paramref name="path"/> of skill <paramref name="name"/> (2026-09-27).</summary>
    public string DescribeEdit(string name, string path, string oldText, string newText, bool replaceAll, string? summary = null) =>
        DescribeFile(path, files => SkillEditor.EditFile(_roots(), SkillScope.Profile, name, path, oldText, newText, replaceAll, _external(), files.SafeEdits(), files.Time), summary);

    /// <summary>A file action under the tool's rules: refused as an unknown action when files are not offered, and for a blank path; the skill is found where it lives (the profile first).</summary>
    private string DescribeFile(string path, Func<SkillFileAccess, SkillEditResult> act, string? summary)
    {
        ArgumentNullException.ThrowIfNull(path);
        LastResult = null;
        if (_files is null)
        {
            return SkillText.BadAction(WriteFileAction);
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return SkillText.NoPath;
        }

        var result = act(_files);
        LastResult = result with { Summary = SkillText.CleanSummary(summary) };
        _edited?.Invoke(_roots(), result);
        return SkillText.Edited(result);
    }

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        switch (ToolArguments.ReadString(arguments, ActionArgument).Trim().ToLowerInvariant())
        {
            case WriteFileAction when _files is not null:
                return new ValueTask<object?>(DescribeWrite(
                    ToolArguments.ReadString(arguments, NameArgument),
                    ToolArguments.ReadString(arguments, PathArgument),
                    ToolArguments.ReadString(arguments, ContentArgument),
                    ToolArguments.ReadString(arguments, SummaryArgument)));
            case EditFileAction when _files is not null:
                if (!ToolArguments.TryReadBoolean(arguments, ReplaceAllArgument, out bool? replaceAll, out string raw))
                {
                    return new ValueTask<object?>(Files.FileText.BadBoolean(ReplaceAllArgument, raw));
                }

                return new ValueTask<object?>(DescribeEdit(
                    ToolArguments.ReadString(arguments, NameArgument),
                    ToolArguments.ReadString(arguments, PathArgument),
                    ToolArguments.ReadString(arguments, OldTextArgument),
                    ToolArguments.ReadString(arguments, NewTextArgument),
                    replaceAll ?? false,
                    ToolArguments.ReadString(arguments, SummaryArgument)));
        }

        return new ValueTask<object?>(Describe(
            ToolArguments.ReadString(arguments, ActionArgument),
            ToolArguments.ReadString(arguments, ScopeArgument),
            ToolArguments.ReadString(arguments, NameArgument),
            ToolArguments.ReadString(arguments, DescriptionArgument),
            ToolArguments.ReadString(arguments, InstructionsArgument),
            ToolArguments.ReadString(arguments, SummaryArgument)));
    }
}

/// <summary>
/// What <c>skill_editor</c>'s file actions need beyond the roots (2026-09-27): <c>File safe edits</c>,
/// read at each call (the previous version into the skill's own <c>.trash</c>), and the clock behind the trash stamp.
/// </summary>
public sealed record SkillFileAccess(Func<bool> SafeEdits, TimeProvider Time);
