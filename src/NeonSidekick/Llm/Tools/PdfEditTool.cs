using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Files;
using NeonSidekick.Pdf;
using NeonSidekick.Web;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>pdf_edit(action, path?, paths?, pages?, angle?, fields?, flatten?, to?, overwrite?)</c> (2026-10-14, the PDF plan's phase 4b, the
/// user's pick): merge PDFs, take pages out into a new one, leave pages out, turn pages, or fill in a form, through <see cref="PdfEditing"/>
/// (pdf-lib in the headless browser), so the model never writes PDF bytes or a script for it. One of the file tools, after
/// <c>convert_to_pdf</c>: it reads and writes the working directory, nothing else, so <c>File tools</c> rules it. The new PDF goes beside the
/// first one under a name of its own; the original is replaced only when <c>to</c> names it with <c>overwrite</c>. No confirm: a sandbox
/// write like <c>write_file</c>'s. Not in plan mode. Local only: a PDF on a UNC share is <c>unc_fetch</c>'s to bring in first (gap 9).
/// </summary>
public sealed class PdfEditTool : FileTool
{
    public const string ToolName = "pdf_edit";

    public const string ActionArgument = "action";
    public const string PathsArgument = "paths";
    public const string PagesArgument = "pages";
    public const string AngleArgument = "angle";
    public const string FieldsArgument = "fields";
    public const string FlattenArgument = "flatten";
    public const string ToArgument = "to";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "action": { "type": "string", "enum": ["merge", "extract", "delete", "rotate", "fill_form"], "description": "merge: join the PDFs in paths, in order. extract: copy the pages given into a new PDF. delete: the PDF without the pages given. rotate: turn pages by angle. fill_form: set form fields to values." },
            "path": { "type": "string", "description": "The PDF to edit, relative to the working directory (every action but merge)." },
            "paths": { "type": "array", "items": { "type": "string" }, "description": "merge: the PDFs to join, in order, relative to the working directory." },
            "pages": { "type": "string", "description": "extract, delete, and rotate (default every page): physical page numbers from 1, \"3\", \"2-5\", \"5-\" (to the end) or \"1,3,7-9\"." },
            "angle": { "type": "integer", "description": "rotate (required for it): degrees clockwise, a quarter turn: 90, 180, 270, or -90 the other way." },
            "fields": { "type": "object", "description": "fill_form: each field's name and its value: text, true or false for a check box, an option's name for a choice or radio group, a list for a multi-choice list. read_pdf with fields: true lists them." },
            "flatten": { "type": "boolean", "description": "fill_form: true bakes the values into the pages, so they can no longer be changed. Default false." },
            "to": { "type": "string", "description": "The new PDF's path or folder, relative to the working directory. Default: beside the (first) PDF, named after it (report-p2-5.pdf, report-rotated.pdf)." },
            "overwrite": { "type": "boolean", "description": "Replace the file at to if one is there, the original PDF included. Default false." }
          },
          "required": ["action"]
        }
        """);

    private readonly PdfEditing _editing;

    public PdfEditTool(WorkingDirectory files, PdfEditing editing) : base(files)
    {
        _editing = editing ?? throw new ArgumentNullException(nameof(editing));
    }

    public override string Name => ToolName;

    public override string Description => DescriptionText;

    /// <summary>The description. Pinned.</summary>
    public const string DescriptionText =
        "Edits PDFs in the working directory (the user's cwd / current directory), writing the result as a new PDF beside the first one (or at to): " +
        "merge joins the PDFs in paths in order; extract copies the pages given into a new PDF; delete leaves the pages given out; " +
        "rotate turns pages (every page by default) by angle, clockwise; fill_form sets a form's fields (read_pdf with fields: true lists them), and flatten: true bakes them in. " +
        "pages takes physical page numbers (\"3\", \"2-5\", \"5-\", \"1,3,7-9\"). The original is only replaced when to names it and overwrite is true. " +
        "This is the way to change a PDF: never write PDF bytes yourself or run a script to do it.";

    public override JsonElement JsonSchema => Schema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        string actionText = ToolArguments.ReadString(arguments, ActionArgument).Trim();
        if (!PdfEditPlan.TryAction(actionText, out var op))
        {
            return FileText.BadChoice(ActionArgument, actionText, string.Join(", ", PdfEditPlan.ActionWords));
        }

        if (ReadOverwrite(arguments, out string raw) is not { } overwrite)
        {
            return FileText.BadBoolean(OverwriteArgument, raw);
        }

        if (ReadFlag(arguments, FlattenArgument, out raw) is not { } flatten)
        {
            return FileText.BadBoolean(FlattenArgument, raw);
        }

        if (!ToolArguments.TryReadStringList(arguments, PathsArgument, out var listed, out raw))
        {
            return FileText.BadStringList(PathsArgument, raw);
        }

        if (!ToolArguments.TryReadInt32(arguments, AngleArgument, out int? angle, out raw))
        {
            return PdfText.BadAngleText(raw);
        }

        if (!TryReadFields(arguments, out var fields, out string? fieldsError))
        {
            return fieldsError;
        }

        // A merge's PDFs are paths; a lone path given with them leads. Every other action takes path, or a list of one.
        string path = ReadPath(arguments).Trim();
        var paths = new List<string>();
        if (path.Length > 0)
        {
            paths.Add(path);
        }

        paths.AddRange(listed.Where(p => !string.IsNullOrWhiteSpace(p)));
        string pages = ToolArguments.ReadString(arguments, PagesArgument).Trim();
        string to = ToolArguments.ReadString(arguments, ToArgument).Trim();
        var request = new PdfEditRequest(op, paths, pages.Length > 0 ? pages : null, angle ?? 0, fields, flatten, to.Length > 0 ? to : null, overwrite);
        return await _editing.EditAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary><c>fields</c> read: an object of names to values, each text, a number, true or false, null (cleared), or a list of text.</summary>
    private static bool TryReadFields(AIFunctionArguments arguments, out IReadOnlyList<PdfFieldValue> fields, out string? error)
    {
        fields = [];
        error = null;
        if (!ToolArguments.TryReadObjectList(arguments, FieldsArgument, out var objects, out string raw) || objects.Count > 1)
        {
            error = PdfText.BadFields(raw);
            return false;
        }

        if (objects.Count == 0)
        {
            return true;
        }

        var list = new List<PdfFieldValue>();
        var named = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in objects[0].EnumerateObject())
        {
            // One value a field (2026-10-22, review T8: a repeated key sent two values for one field and counted two filled).
            if (!named.Add(property.Name))
            {
                error = PdfText.FieldTwice(property.Name);
                return false;
            }

            var value = property.Value;
            bool fits = value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null
                || (value.ValueKind == JsonValueKind.Array && value.EnumerateArray().All(x => x.ValueKind is JsonValueKind.String or JsonValueKind.Number));
            if (!fits)
            {
                error = PdfText.BadFieldValue(property.Name);
                return false;
            }

            list.Add(new PdfFieldValue(property.Name, value.Clone()));
        }

        fields = list;
        return true;
    }
}
