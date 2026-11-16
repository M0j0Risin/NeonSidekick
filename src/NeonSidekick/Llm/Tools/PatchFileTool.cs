using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Files;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>patch_file(path, old_text, new_text, replace_all? | edits)</c>: the one editing tool since 2026-09-19
/// (<c>edit_file</c> and the line-addressed <c>edit_lines</c> before it). One occurrence of
/// <c>old_text</c> replaced — every one with <c>replace_all</c> — found by <see cref="FuzzyMatch"/>'s
/// chain: as written first, then with spacing, indentation, escapes and typographic characters
/// tolerated, last by similarity; the result names the strategy when it was not exact. The match
/// runs over LF-normalised text, so what a read shows matches a CRLF file; the file keeps its line
/// ending and its BOM. The result names the edited lines and shows them numbered. Since 2026-11-15 (feature idea #30) <c>edits</c> makes
/// several changes in one call, in order, all or none (<see cref="WorkingDirectory.EditTexts"/>).
/// </summary>
public sealed class PatchFileTool : FileTool, IRefusesPrunedStubs
{
    /// <inheritdoc/>
    public IReadOnlyCollection<string> StubArguments { get; } = [NewTextArgument];

    public const string ToolName = "patch_file";
    public const string OldTextArgument = "old_text";
    public const string NewTextArgument = "new_text";
    public const string ReplaceAllArgument = "replace_all";
    public const string EditsArgument = "edits";

    /// <summary>
    /// The names an <c>edits</c> item takes (2026-11-15, the tool-arguments review's #12, #70: one definition, which the item schema, the
    /// reader and <c>unc_patch</c> all take). Before <see cref="Schema"/>: statics run in order.
    /// </summary>
    internal static readonly string[] EditKeys = [OldTextArgument, NewTextArgument, ReplaceAllArgument];

    /// <summary>An <c>edits</c> item's schema, built from <see cref="EditKeys"/> (#141, #148), for <c>patch_file</c> and <c>unc_patch</c> alike.</summary>
    internal static readonly string EditItemSchema =
        "{ \"type\": \"object\", \"properties\": { "
        + string.Join(", ", EditKeys.Select(key => "\"" + key + "\": { \"type\": \"" + (key == ReplaceAllArgument ? "boolean" : "string") + "\" }"))
        + " }, \"required\": [\"" + OldTextArgument + "\", \"" + NewTextArgument + "\"] }";

    /// <summary>The cap as the <c>edits</c> descriptions write it (#150): from <see cref="WorkingDirectory.MaxEdits"/>, never by hand.</summary>
    internal static readonly string MaxEditsText = WorkingDirectory.MaxEdits.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static readonly JsonElement Schema = ToolSchema.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "The file to change, relative to the working directory." },
            "old_text": { "type": "string", "description": "The text to replace, copied from the file as read_file shows it (without the line numbers an edit result shows). It must occur exactly once unless replace_all is true; include surrounding lines to make it unique. Small differences in spacing, indentation, quotes and dashes are tolerated." },
            "new_text": { "type": "string", "description": "The text to put in its place. Empty removes old_text." },
            "replace_all": { "type": "boolean", "description": "true replaces every occurrence of old_text (a rename across the file), matched as written or with spacing and indentation tolerated, never by similarity. Default false: exactly one." },
            "edits": { "type": "array", "items": {{EditItemSchema}}, "description": "Several changes to this file in one call (at most {{MaxEditsText}}), each {old_text, new_text, replace_all?} as above, applied in order, each to the text the one before left; all or none. Leave old_text and new_text out when you give this." }
          },
          "required": ["path"]
        }
        """);

    public PatchFileTool(WorkingDirectory files) : base(files)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Changes part of a text file under the working directory (the user's cwd / current directory): replaces one occurrence of old_text with new_text, " +
        "or every occurrence with replace_all. old_text is matched as written first, then with spacing, indentation, \\n escapes and typographic quotes or dashes tolerated, " +
        "and last by the similarity of its lines — so text copied from a read lands even when its indentation was guessed; the result says how it matched. " +
        "old_text must appear exactly once unless replace_all (which never matches by similarity) — include enough surrounding text to make it unique; an empty new_text removes it. " +
        "The rest of the file is untouched; the result shows the edited lines with their numbers and the file's new line and word counts. An edit already in the file answers as done. To rewrite or append a whole file use write_file. " +
        "For several changes to one file, send edits instead (each {old_text, new_text, replace_all?}), applied in order, all or none." + FileText.GuardedPathsSentence;

    public override JsonElement JsonSchema => Schema;

    public string Describe(string path, string oldText, string newText, bool replaceAll = false) =>
        FileText.Edited(Files.EditText(path, oldText, newText, replaceAll));

    /// <summary>
    /// Which form a call is (2026-11-15). A key counts only with a real value (the tool-arguments review's #22, #69, #84: a schema-filling
    /// <c>old_text: ""</c> beside <c>edits</c> was "both forms"): a non-blank <c>old_text</c>, a non-empty <c>new_text</c> or
    /// <c>replace_all: true</c> for the single form, a non-empty <c>edits</c> for the list. Both is the refusal. With neither, a call that sent a
    /// single-form key keeps its own answers (old_text empty, new_text required), one that sent <c>edits</c> its empty-list answer, and one
    /// that sent nothing the form refusal. Null otherwise, <paramref name="list"/> saying which.
    /// </summary>
    internal static string? EditForm(AIFunctionArguments arguments, out bool list)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        bool single = ToolArguments.ReadString(arguments, OldTextArgument).Trim().Length > 0
            || ToolArguments.ReadString(arguments, NewTextArgument).Length > 0
            || (ToolArguments.TryReadBoolean(arguments, ReplaceAllArgument, out bool? all, out _) && all == true);
        list = arguments.TryGetValue(EditsArgument, out var edits) && !ToolArgumentCheck.Harmless(edits);
        if (single && list)
        {
            return FileText.EditFormsBoth;
        }

        if (single || list)
        {
            return null;
        }

        if (EditKeys.Any(name => ToolArguments.Has(arguments, name)))
        {
            return null;
        }

        list = ToolArguments.Has(arguments, EditsArgument);
        return list ? null : FileText.EditFormRequired;
    }

    /// <summary>
    /// The <c>edits</c> list (2026-11-15): a list of objects, a lone object, or either written as text (<see cref="ToolArguments.TryReadObjectList"/>).
    /// Refused whole when empty or over <see cref="WorkingDirectory.MaxEdits"/>, before anything is read; an item refused for an unknown key (the
    /// nearest name), a missing <c>new_text</c>, text that is no string, a pruned stub as its <c>new_text</c>, or a bad <c>replace_all</c>, each
    /// naming the edit. A missing <c>old_text</c> is blank, which <see cref="WorkingDirectory.EditTexts"/> answers <see cref="FileText.EditEmpty"/>.
    /// </summary>
    internal static bool TryReadEdits(AIFunctionArguments arguments, out IReadOnlyList<EditStep> steps, out string error)
    {
        var found = new List<EditStep>();
        steps = found;
        error = "";
        if (!ToolArguments.TryReadObjectList(arguments, EditsArgument, out var items, out string raw))
        {
            error = FileText.BadObjectList(EditsArgument, raw);
            return false;
        }

        if (items.Count == 0)
        {
            error = FileText.EditsEmpty;
            return false;
        }

        if (items.Count > WorkingDirectory.MaxEdits)
        {
            error = FileText.TooManyEdits(items.Count, WorkingDirectory.MaxEdits);
            return false;
        }

        for (int i = 0; i < items.Count; i++)
        {
            // Each item judged once (#13): its refusal, or its step.
            string itemError = ItemError(items[i], i + 1);
            if (itemError.Length > 0)
            {
                error = FileText.EditItem(i + 1, items.Count, itemError);
                return false;
            }

            var item = items[i];
            found.Add(new EditStep(
                item.TryGetProperty(OldTextArgument, out var o) && o.ValueKind == JsonValueKind.String ? o.GetString() ?? "" : "",
                item.GetProperty(NewTextArgument).GetString() ?? "",
                item.TryGetProperty(ReplaceAllArgument, out var r) && (r.ValueKind == JsonValueKind.True || (r.ValueKind == JsonValueKind.String && bool.TryParse(r.GetString()?.Trim(), out bool b) && b))));
        }

        return true;

        static string ItemError(JsonElement item, int number)
        {
            // An unknown key through the call check's own rules (#12): a harmless value or a commentary key left aside, any other hinted.
            var sent = item.EnumerateObject().Where(p => EditKeys.Contains(p.Name, StringComparer.Ordinal) && !ToolArgumentCheck.Harmless(p.Value)).Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var property in item.EnumerateObject())
            {
                if (!EditKeys.Contains(property.Name, StringComparer.Ordinal) && !ToolArgumentCheck.Unread(property.Name, property.Value))
                {
                    return FileText.EditUnknownKey(ArgumentText.Echo(property.Name), ToolArgumentCheck.Hint(property.Name, EditKeys, sent));
                }
            }

            if (!item.TryGetProperty(NewTextArgument, out var newText) || newText.ValueKind == JsonValueKind.Null)
            {
                return FileText.NewTextRequired;
            }

            if (newText.ValueKind != JsonValueKind.String
                || (item.TryGetProperty(OldTextArgument, out var oldText) && oldText.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)))
            {
                return FileText.EditNotText;
            }

            // A pruned stub copied back, in either text (#13: its old_text too).
            if (ConversationCompactor.IsPrunedStub(newText.GetString() ?? ""))
            {
                return FileText.PrunedStubSent(FileText.EditField(number, NewTextArgument));
            }

            if (item.TryGetProperty(OldTextArgument, out var oldStub) && oldStub.ValueKind == JsonValueKind.String && ConversationCompactor.IsPrunedStub(oldStub.GetString() ?? ""))
            {
                return FileText.PrunedStubSent(FileText.EditField(number, OldTextArgument));
            }

            if (item.TryGetProperty(ReplaceAllArgument, out var all)
                && all.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null)
                && !(all.ValueKind == JsonValueKind.String && (string.IsNullOrWhiteSpace(all.GetString()) || bool.TryParse(all.GetString()!.Trim(), out _))))
            {
                return FileText.BadBoolean(ReplaceAllArgument, all.ValueKind == JsonValueKind.String ? all.GetString() ?? "" : all.GetRawText());
            }

            return "";
        }
    }

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (ReadFlag(arguments, ReplaceAllArgument, out string raw) is not { } replaceAll)
        {
            return FileText.BadBoolean(ReplaceAllArgument, raw);
        }

        if (!RequirePath(arguments, PathArgument, out string path, out string error))
        {
            return error;
        }

        if (ShoppingListRefusal(path) is { } refused)
        {
            return refused;
        }

        if (EditForm(arguments, out bool list) is { } form)
        {
            return form;
        }

        if (list)
        {
            if (!TryReadEdits(arguments, out var steps, out error))
            {
                return error;
            }

            // Off the turn's thread (2026-11-15: up to 50 match chains over a file of up to 2 MB), Esc between edits; nothing is written until
            // the end, so a cancel leaves the file as it was.
            return await Task.Run(() =>
            {
                var many = Files.EditTexts(path, steps, cancellationToken);
                if (many.Outcome == FileOutcome.Ok)
                {
                    Files.MarkSeen(path);
                }

                return ToolDiffResult.Of(FileText.Edited(many), many.Diff);
            }, cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        // A missing new_text is not "" (2026-10-17): read as empty it removed old_text.
        if (!ToolArguments.Has(arguments, NewTextArgument))
        {
            return FileText.NewTextRequired;
        }

        if (WriteFileTool.StubSent(arguments, NewTextArgument) is { } stub)
        {
            return stub;
        }

        // The diff beside the sentence for the transcript (2026-10-03, ToolDiffResult); the model gets the sentence alone.
        var edited = Files.EditText(
            path,
            ToolArguments.ReadString(arguments, OldTextArgument),
            ToolArguments.ReadString(arguments, NewTextArgument),
            replaceAll);
        if (edited.Outcome == FileOutcome.Ok)
        {
            Files.MarkSeen(path);   // as edited: write_file may replace it without a mode (2026-10-10)
        }

        return ToolDiffResult.Of(FileText.Edited(edited), edited.Diff);
    }
}
