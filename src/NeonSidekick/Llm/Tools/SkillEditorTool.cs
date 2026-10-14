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

    // The two schemas while the profile has Global skills off (2026-10-13): the scope enum is the profile alone, so the model is never
    // offered a root this profile cannot see. The editor reads a global scope as the profile's anyway; a create is refused.
    private static readonly JsonElement ProfileSchema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "action": { "type": "string", "enum": ["create", "update"], "description": "create writes a new skill (the name must not be a skill in any folder yet); update changes an existing one's description, instructions or both, wherever it lives." },
            "scope": { "type": "string", "enum": ["profile"], "description": "Where a new skill goes: profile, this profile's skills. Optional; ignored for update, which changes the skill where it already is." },
            "name": { "type": "string", "description": "The skill's name and folder: 1 to 64 lowercase letters, digits and hyphens (pdf-processing), not starting or ending with a hyphen." },
            "description": { "type": "string", "description": "One or two sentences on what the skill does and when to use it, with the words a task would contain; at most 1,024 characters. Required for create." },
            "instructions": { "type": "string", "description": "The skill's Markdown body: the steps to follow, examples, edge cases. Required for create; replaces the whole body on update." },
            "summary": { "type": "string", "description": "One or two sentences on what you changed and why, for the user to read; never part of the skill. Optional." }
          },
          "required": ["action", "name"]
        }
        """);

    private static readonly JsonElement ProfileFileSchema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "action": { "type": "string", "enum": ["create", "update", "write_file", "edit_file"], "description": "create writes a new skill (the name must not be a skill in any folder yet); update changes an existing one's description, instructions or both, wherever it lives; write_file creates or replaces a supporting file of an existing skill (a mapping, examples, a script) with content; edit_file replaces old_text with new_text in one." },
            "scope": { "type": "string", "enum": ["profile"], "description": "Where a new skill goes: profile, this profile's skills. Optional; ignored for the other actions, which work on the skill where it already is." },
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
    private readonly ReflectionWriteGuard? _guard;
    private readonly Func<SkillProposal, CancellationToken, Task<bool>>? _confirm;

    /// <param name="roots">Read per call: the profile root moves with a profile switch.</param>
    /// <param name="external">Read per call: whether the external folder is in the catalog (<c>Agent skills</c> and <c>Use external skills</c> both on), so a skill there blocks its name.</param>
    /// <param name="files">What the file actions need, or null to offer create and update alone (2026-09-27).</param>
    /// <param name="edited">
    /// Told every result with the roots it was written under (2026-09-30, the skill records, <see cref="SkillRecords.Edited"/>):
    /// a refusal is told as well, and the listener keeps only the writes.
    /// </param>
    /// <param name="guard">A reflection's rules on what it may write (2026-10-02, <see cref="ReflectionWriteGuard"/>): a change it refuses never reaches the editor. Null in the main chat.</param>
    /// <param name="confirm">
    /// A reflection's ask (2026-10-12, <c>Reflection confirm mode</c>): told every write the guard let through, before the editor makes it — a
    /// create for a name no root holds, an update or a file action on a skill that is there — and awaited; false writes nothing and answers
    /// <see cref="SkillEditOutcome.Declined"/>. The host decides which kinds it asks the user about and says yes to the rest at once. The
    /// guard's checks come first and its load hash is read again at the write, after the wait. Null in the main chat.
    /// </param>
    public SkillEditorTool(Func<SkillRoots> roots, Func<bool> external, SkillFileAccess? files = null, Action<SkillRoots, SkillEditResult>? edited = null, ReflectionWriteGuard? guard = null,
        Func<SkillProposal, CancellationToken, Task<bool>>? confirm = null)
    {
        _confirm = confirm;
        _roots = roots ?? throw new ArgumentNullException(nameof(roots));
        _edited = edited;
        _guard = guard;
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

    /// <summary>The schema for the live roots (2026-10-13): the profile-only twins while the profile has <c>Global skills</c> off.</summary>
    public override JsonElement JsonSchema => _roots().GlobalOn
        ? (_files is null ? Schema : FileSchema)
        : (_files is null ? ProfileSchema : ProfileFileSchema);

    /// <summary>
    /// The editor's result of the last call, for a host that runs the tool itself
    /// (<see cref="Skills.SkillLearner"/> stops after the first successful write); null before the
    /// first call and after one refused for its action or scope.
    /// </summary>
    public SkillEditResult? LastResult { get; private set; }

    /// <summary>The result for the parsed arguments: the sentence for the outcome, or the argument error; <paramref name="summary"/> rides <see cref="LastResult"/> through <see cref="SkillText.CleanSummary"/>.</summary>
    public string Describe(string action, string scope, string name, string? description, string? instructions, string? summary = null) =>
        DescribeAsync(action, scope, name, description, instructions, summary, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary><see cref="Describe"/> with the reflection's ask awaited before the write (2026-10-12); without one it completes at once.</summary>
    public async Task<string> DescribeAsync(string action, string scope, string name, string? description, string? instructions, string? summary, CancellationToken cancellationToken)
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
        var roots = _roots();
        if (!SkillScopes.TryParseWritable(scope, out var where) && create && !string.IsNullOrWhiteSpace(scope))
        {
            return SkillText.BadScope(scope, roots.GlobalOn);
        }

        // A new skill in the global root while this profile has it off (2026-10-13): refused before any ask. An update's scope is only
        // a first guess, which the editor reads as the profile's (SkillEditor.Update).
        if (create && roots.Hides(where))
        {
            return SkillText.GlobalSkillsOff;
        }

        // A reflection's update first passes its guard (2026-10-02): an instructions rewrite only over what its load served.
        if (!create && _guard?.Refusal(name, !string.IsNullOrWhiteSpace(instructions)) is { } refusal)
        {
            return refusal;
        }

        bool external = _external();
        if (_confirm is not null && SkillEditor.Find(roots, name.Trim(), external) is var found && (create ? found is null : found is { } at && at != SkillScope.External))
        {
            // The ask (2026-10-12): a create for a free name, an update for a skill that is there; a call the editor would refuse anyway
            // is refused unasked.
            var proposal = create
                ? new SkillProposal(SkillChangeKind.New, CreateAction, name.Trim(), description, instructions, Summary: SkillText.CleanSummary(summary))
                : new SkillProposal(SkillChangeKind.Edit, UpdateAction, name.Trim(), string.IsNullOrWhiteSpace(description) ? null : description,
                    string.IsNullOrWhiteSpace(instructions) ? null : instructions, Before: ReadSkillMd(roots, found!.Value, name.Trim()), Summary: SkillText.CleanSummary(summary));
            if (await Declined(proposal, create ? where : found!.Value, cancellationToken).ConfigureAwait(false) is { } no)
            {
                return no;
            }

            // The guard again after the wait (2026-10-13, the code review): the ask may wait through a whole reply, and a change made
            // meanwhile (the main chat's skill_editor, the user's editor) must not be overwritten by the reflection's stale copy.
            if (!create && _guard?.Refusal(name, !string.IsNullOrWhiteSpace(instructions)) is { } late)
            {
                return late;
            }
        }

        var result = create
            ? SkillEditor.Create(roots, where, name, description ?? "", instructions ?? "", external)
            : SkillEditor.Update(roots, where, name, description, instructions, external);
        LastResult = result with { Summary = SkillText.CleanSummary(summary) };
        _edited?.Invoke(roots, LastResult);   // with the summary: the skill records keep it as the event's detail (2026-10-02)
        return SkillText.Edited(result);
    }

    /// <summary><c>write_file</c>: <paramref name="content"/> as the whole of <paramref name="path"/> in skill <paramref name="name"/> (2026-09-27).</summary>
    public string DescribeWrite(string name, string path, string content, string? summary = null) =>
        DescribeWriteAsync(name, path, content, summary, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary><see cref="DescribeWrite"/> with the reflection's ask awaited before the write (2026-10-12).</summary>
    public Task<string> DescribeWriteAsync(string name, string path, string content, string? summary, CancellationToken cancellationToken) =>
        DescribeFileAsync(name, path, files => SkillEditor.WriteFile(_roots(), SkillScope.Profile, name, path, content, _external(), files.Time), summary,
            new SkillProposal(SkillChangeKind.Edit, WriteFileAction, name.Trim(), Path: path, Content: content, Summary: SkillText.CleanSummary(summary)), cancellationToken);

    /// <summary><c>edit_file</c>: <paramref name="oldText"/> → <paramref name="newText"/> in <paramref name="path"/> of skill <paramref name="name"/> (2026-09-27).</summary>
    public string DescribeEdit(string name, string path, string oldText, string newText, bool replaceAll, string? summary = null) =>
        DescribeEditAsync(name, path, oldText, newText, replaceAll, summary, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary><see cref="DescribeEdit"/> with the reflection's ask awaited before the write (2026-10-12).</summary>
    public Task<string> DescribeEditAsync(string name, string path, string oldText, string newText, bool replaceAll, string? summary, CancellationToken cancellationToken) =>
        DescribeFileAsync(name, path, files => SkillEditor.EditFile(_roots(), SkillScope.Profile, name, path, oldText, newText, replaceAll, _external(), files.Time), summary,
            new SkillProposal(SkillChangeKind.Edit, EditFileAction, name.Trim(), Path: path, OldText: oldText, NewText: newText, Summary: SkillText.CleanSummary(summary)), cancellationToken);

    /// <summary>
    /// The ask (2026-10-12): null when the host said yes; the declined sentence otherwise, <see cref="LastResult"/> the
    /// <see cref="SkillEditOutcome.Declined"/> result (its detail the action, for <see cref="SkillText.Declined"/>), nothing written or told.
    /// </summary>
    private async Task<string?> Declined(SkillProposal proposal, SkillScope scope, CancellationToken cancellationToken)
    {
        if (await _confirm!(proposal, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        LastResult = new SkillEditResult(SkillEditOutcome.Declined, proposal.Name, scope, Detail: proposal.Action, Summary: proposal.Summary, Path: proposal.Path ?? "");
        return SkillText.Edited(LastResult);
    }

    /// <summary>A skill's SKILL.md as it stands, for an update's ask (2026-10-12); null when it cannot be read.</summary>
    private static string? ReadSkillMd(SkillRoots roots, SkillScope scope, string name)
    {
        try
        {
            return File.ReadAllText(Path.Combine(roots.Of(scope), name, SkillCatalog.FileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>A file action under the tool's rules: refused as an unknown action when files are not offered, and for a blank path; the skill is found where it lives (the profile first).</summary>
    private async Task<string> DescribeFileAsync(string name, string path, Func<SkillFileAccess, SkillEditResult> act, string? summary, SkillProposal proposal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);
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

        if (_guard?.Refusal(name, rewrite: true) is { } refusal)
        {
            return refusal;
        }

        if (_confirm is not null && SkillEditor.Find(_roots(), name.Trim(), _external()) is { } at && at != SkillScope.External)
        {
            if (await Declined(proposal, at, cancellationToken).ConfigureAwait(false) is { } no)
            {
                return no;
            }

            // The guard again after the wait (2026-10-13, the code review), as DescribeAsync's.
            if (_guard?.Refusal(name, rewrite: true) is { } late)
            {
                return late;
            }
        }

        var result = act(_files);
        LastResult = result with { Summary = SkillText.CleanSummary(summary) };
        _edited?.Invoke(_roots(), LastResult);
        return SkillText.Edited(result);
    }

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        switch (ToolArguments.ReadString(arguments, ActionArgument).Trim().ToLowerInvariant())
        {
            case WriteFileAction when _files is not null:
                return await DescribeWriteAsync(
                    ToolArguments.ReadString(arguments, NameArgument),
                    ToolArguments.ReadString(arguments, PathArgument),
                    ToolArguments.ReadString(arguments, ContentArgument),
                    ToolArguments.ReadString(arguments, SummaryArgument),
                    cancellationToken).ConfigureAwait(false);
            case EditFileAction when _files is not null:
                if (!ToolArguments.TryReadBoolean(arguments, ReplaceAllArgument, out bool? replaceAll, out string raw))
                {
                    return Files.FileText.BadBoolean(ReplaceAllArgument, raw);
                }

                return await DescribeEditAsync(
                    ToolArguments.ReadString(arguments, NameArgument),
                    ToolArguments.ReadString(arguments, PathArgument),
                    ToolArguments.ReadString(arguments, OldTextArgument),
                    ToolArguments.ReadString(arguments, NewTextArgument),
                    replaceAll ?? false,
                    ToolArguments.ReadString(arguments, SummaryArgument),
                    cancellationToken).ConfigureAwait(false);
        }

        return await DescribeAsync(
            ToolArguments.ReadString(arguments, ActionArgument),
            ToolArguments.ReadString(arguments, ScopeArgument),
            ToolArguments.ReadString(arguments, NameArgument),
            ToolArguments.ReadString(arguments, DescriptionArgument),
            ToolArguments.ReadString(arguments, InstructionsArgument),
            ToolArguments.ReadString(arguments, SummaryArgument),
            cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// What <c>skill_editor</c>'s file actions need beyond the roots (2026-09-27): the clock the skill folder's
/// <see cref="Files.WorkingDirectory"/> runs on; present at all, the file actions are offered. <c>File safe edits</c>, read at
/// each call, rode beside it until 2026-10-01, when that setting went (the user's call).
/// </summary>
public sealed record SkillFileAccess(TimeProvider Time);
