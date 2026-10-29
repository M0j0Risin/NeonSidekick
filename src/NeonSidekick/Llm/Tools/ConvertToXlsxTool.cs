using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Files;
using NeonSidekick.Office;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>convert_to_xlsx(path | columns+rows, to?, sheet?, overwrite?)</c> (2026-10-29, the Office writing plan, <c>convert_to_pdf</c>'s twin): an
/// Excel workbook in the working directory from a CSV, TSV or JSON file, or from a table the model has. Written by
/// <see cref="XlsxTarget"/> (a new workbook, a sheet added to one, or a sheet or the file replaced with <c>overwrite</c>), never by the
/// model's own bytes; no cell is ever a formula. One of the file tools, so *File tools* rules it; quiet, so the table a call carries never
/// reaches the transcript, only the result sentence. A database query's rows go through the query tool's own <c>to</c> instead.
/// </summary>
public sealed class ConvertToXlsxTool : FileTool
{
    public const string ToolName = "convert_to_xlsx";

    public const string ColumnsArgument = "columns";
    public const string RowsArgument = "rows";
    public const string ToArgument = "to";
    public const string SheetArgument = "sheet";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "A .csv, .tsv or .json file to make a workbook of, relative to the working directory. JSON is an array of objects or of arrays." },
            "columns": { "type": "array", "items": { "type": "string" }, "description": "Instead of path: your own table's column names." },
            "rows": { "type": "array", "items": { "type": "array", "items": { "type": ["string", "number", "boolean", "null"] } }, "description": "With columns: the rows, each an array of cells in the columns' order." },
            "to": { "type": "string", "description": "The .xlsx path or folder, relative to the working directory. Default: beside the source file, or table-<date>.xlsx at the top." },
            "sheet": { "type": "string", "description": "The sheet's name. Naming a new sheet in an existing workbook adds it; naming one there replaces it with overwrite. Default: the file's name." },
            "overwrite": { "type": "boolean", "description": "Replace the sheet named, or with no sheet the whole file. Default false." }
          }
        }
        """);

    private readonly TimeProvider _time;

    public ConvertToXlsxTool(WorkingDirectory files, TimeProvider? time = null) : base(files)
    {
        _time = time ?? TimeProvider.System;
    }

    public override string Name => ToolName;

    /// <summary>The description. Pinned.</summary>
    public const string DescriptionText =
        "Makes an Excel workbook (.xlsx) in the working directory from a table you have (columns and rows) or a CSV, TSV or JSON file (path). " +
        "Numbers, dates and true/false columns are typed; nothing is ever a formula. Give sheet to add a sheet to an existing workbook. " +
        "For a database query's rows, use the query tool's to instead. " +
        "This is the way to make a spreadsheet: never write .xlsx bytes yourself or run a script to make one.";

    public override string Description => DescriptionText;

    public override JsonElement JsonSchema => Schema;

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return new ValueTask<object?>(Convert(arguments, cancellationToken));
    }

    private string Convert(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        if (ReadOverwrite(arguments, out string raw) is not { } overwrite)
        {
            return FileText.BadBoolean(OverwriteArgument, raw);
        }

        string path = ReadPath(arguments).Trim();
        bool hasColumns = ToolArguments.Has(arguments, ColumnsArgument) && !IsNull(arguments, ColumnsArgument);
        bool hasRows = ToolArguments.Has(arguments, RowsArgument) && !IsNull(arguments, RowsArgument);
        if (path.Length > 0 == (hasColumns || hasRows))
        {
            return OfficeText.OneTableSource;
        }

        if (hasColumns != hasRows)
        {
            return OfficeText.ColumnsAndRows;
        }

        string? to = ToolArguments.ReadString(arguments, ToArgument).Trim() is { Length: > 0 } t ? t : null;
        string? sheet = ToolArguments.ReadString(arguments, SheetArgument).Trim() is { Length: > 0 } s ? s : null;
        string? sourceFull = null;
        string? sourceRelative = null;
        List<string>? header = null;
        List<IReadOnlyList<RawCell>>? rows = null;
        if (path.Length > 0)
        {
            var outcome = Files.Resolve(path, forWrite: false, out string full);
            if (outcome != FileOutcome.Ok)
            {
                return FileText.Error(outcome, path, "read");
            }

            if (Directory.Exists(full))
            {
                return FileText.Error(FileOutcome.IsDirectory, Files.Relative(full, isDirectory: true), "read");
            }

            if (!File.Exists(full))
            {
                return FileText.Error(FileOutcome.Missing, path, "read");
            }

            sourceRelative = Files.Relative(full);
            if (!TableSource.IsTablePath(full))
            {
                return OfficeText.NotATableFile(sourceRelative);
            }

            sourceFull = full;
        }
        else if (ReadTable(arguments, out header, out rows) is { } bad)
        {
            return bad;
        }

        string defaultSheet = sourceRelative is null ? "Sheet1" : Path.GetFileNameWithoutExtension(sourceRelative);
        using var target = XlsxTarget.Plan(Files, new XlsxRequest(to, sheet, overwrite, sourceRelative, "table", defaultSheet), _time, out string? refusal);
        if (target is null)
        {
            return refusal!;
        }

        try
        {
            var sink = target.Begin(out refusal);
            if (sink is null)
            {
                return refusal!;
            }

            string? failed = sourceFull is not null
                ? TableSource.Write(sourceFull, sourceRelative!, sink, cancellationToken)
                : TableSource.Cells(header!, rows!, sink, cancellationToken);
            return failed ?? target.Commit().Answer;
        }
        catch (Exception ex) when (WorkingDirectory.IsFileFailure(ex) || ex is OfficeReadException)
        {
            return ex is OfficeReadException read ? OfficeReader.Refusal(read, target.Relative) : FileText.CouldNot("make a workbook of", sourceRelative ?? "the table", ex.Message);
        }
    }

    private static bool IsNull(AIFunctionArguments arguments, string name) =>
        arguments.TryGetValue(name, out var value) && (value is null || value is JsonElement { ValueKind: JsonValueKind.Null });

    /// <summary>The model's table: the column names and the rows, each cell a string, number, boolean or null; the refusal otherwise.</summary>
    private static string? ReadTable(AIFunctionArguments arguments, out List<string> header, out List<IReadOnlyList<RawCell>> rows)
    {
        header = [];
        rows = [];
        if (!ToolArguments.TryReadStringList(arguments, ColumnsArgument, out var columns, out string raw))
        {
            return OfficeText.BadTableArgument(ColumnsArgument);
        }

        foreach (string column in columns)
        {
            if (ConversationCompactor.IsPrunedStub(column))
            {
                return FileText.PrunedStubSent(ColumnsArgument);
            }
        }

        header = [.. columns];
        if (Element(arguments, RowsArgument) is not { ValueKind: JsonValueKind.Array } list)
        {
            return OfficeText.BadTableArgument(RowsArgument);
        }

        int number = 0;
        foreach (var row in list.EnumerateArray())
        {
            number++;
            if (row.ValueKind != JsonValueKind.Array)
            {
                return OfficeText.BadCell(number, 1);
            }

            var cells = new List<RawCell>();
            int column = 0;
            foreach (var cell in row.EnumerateArray())
            {
                column++;
                if (cell.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    return OfficeText.BadCell(number, column);
                }

                var value = TableSource.Cell(cell);
                if (value.Kind == RawKind.Text && ConversationCompactor.IsPrunedStub(value.Text))
                {
                    return FileText.PrunedStubSent(RowsArgument);
                }

                cells.Add(value);
            }

            if (cells.Count > header.Count)
            {
                return OfficeText.RowTooLong(number, cells.Count, header.Count);
            }

            rows.Add(cells);
        }

        return null;
    }

    /// <summary>A JSON argument as an element: as sent, or a string holding JSON (a server's parser that handed the array over as text).</summary>
    private static JsonElement? Element(AIFunctionArguments arguments, string name)
    {
        if (!arguments.TryGetValue(name, out var sent) || sent is null)
        {
            return null;
        }

        string? text = sent switch
        {
            JsonElement { ValueKind: JsonValueKind.String } s => s.GetString(),
            JsonElement element => null,
            string s => s,
            _ => null,
        };
        if (sent is JsonElement { ValueKind: not JsonValueKind.String } direct)
        {
            return direct;
        }

        if (text is null)
        {
            return null;
        }

        using var document = LooseJson.TryParse(text);
        return document?.RootElement.Clone();
    }
}
