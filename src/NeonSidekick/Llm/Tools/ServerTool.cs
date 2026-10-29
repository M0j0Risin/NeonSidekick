using System.Data.Common;
using Microsoft.Extensions.AI;
using NeonSidekick.Files;
using NeonSidekick.Office;
using NeonSidekick.Settings;
using NeonSidekick.Sql;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// What every database family's tools share (2026-10-24, the database review's K8, decision DB5: <see cref="SqlTool"/>,
/// <see cref="OracleTool"/>, <see cref="MySqlTool"/>, <see cref="PostgresTool"/> and <see cref="SqliteTool"/> were hand copies of one base,
/// and a shared fix had to be made in each and had already drifted): the family's access door, the settings in force at the call, the
/// retired argument a call is refused for still sending (<c>database</c>, or Oracle's <c>schema</c>; SQLite has none), a blank-is-absent
/// string argument, and the boundary's names read. What differs — the timeout setting, the table lookup's shape, what the boundary asks —
/// stays in the family's base.
/// </summary>
/// <remarks>
/// The settings are read once per call (2026-10-24, the review's P7): the query and catalog tools read <c>Effective</c> three or four times
/// a call, so the row cap, the timeout and the result size could come from different moments of a setting being changed on the pane.
/// <see cref="InvokeCoreAsync"/> takes one snapshot for the whole call (an <see cref="AsyncLocal{T}"/> per tool, set inside an async
/// method so it never leaks to the caller); a direct call to a tool's own <c>RunAsync</c> (the tests', the slash commands') reads the
/// settings as it goes, as before.
/// </remarks>
public abstract class ServerTool<TAccess> : AIFunction where TAccess : class
{
    public const string ConnectionArgument = "connection";
    public const string TableArgument = "table";

    /// <summary>The query tools' export (2026-10-29, the Office writing plan): the workbook to write every row to, its sheet, and a replace.</summary>
    public const string ToArgument = "to";
    public const string SheetArgument = "sheet";
    public const string OverwriteArgument = "overwrite";

    /// <summary>The three export properties every query tool's schema carries.</summary>
    public const string ExportProperties =
        "\"to\": { \"type\": \"string\", \"description\": \"An .xlsx path or folder (relative to the working directory) to write every row to instead of returning them; the answer then shows the first rows.\" }, " +
        "\"sheet\": { \"type\": \"string\", \"description\": \"With to: the sheet's name; naming a new one adds it to an existing workbook. Default Query.\" }, " +
        "\"overwrite\": { \"type\": \"boolean\", \"description\": \"With to: replace the sheet named, or with no sheet the whole file. Default false.\" }";

    /// <summary>The sentence a query tool's description adds for <c>to</c>.</summary>
    public const string ExportSentence = " Give to (an .xlsx path or folder) to write every row to a spreadsheet instead of returning them.";

    private readonly Func<AppSettingsData> _effective;
    private readonly AsyncLocal<AppSettingsData?> _call = new();

    protected ServerTool(TAccess access, Func<AppSettingsData> effective)
    {
        Access = access ?? throw new ArgumentNullException(nameof(access));
        _effective = effective ?? throw new ArgumentNullException(nameof(effective));
    }

    /// <summary>The family's one door to its connections.</summary>
    protected TAccess Access { get; }

    /// <summary>
    /// Where an export may write (2026-10-29, the Office writing plan, the AD tools' <c>to</c> rule): the working directory while *File tools*
    /// is on and plan mode off, else the refusal; judged before the query and again just before the file is put in place. Null: no
    /// <c>to</c> here (a test's tool, the advisors').
    /// </summary>
    public Func<SaveRoot>? ExportRoot { get; init; }

    /// <summary>The export's clock: a default name's minute and the file's times.</summary>
    public TimeProvider Clock { get; init; } = TimeProvider.System;

    /// <summary>Whether a save root is open now: a cut chat query's sentence then offers <c>to</c>.</summary>
    protected bool CanSave => ExportRoot?.Invoke() is { Root: not null };

    /// <summary>The settings in force: the call's snapshot inside a model's call, else read now.</summary>
    protected AppSettingsData Effective => _call.Value ?? _effective();

    /// <summary>The argument a call is refused for still sending (the tools took it until 2026-10-09), or null for none.</summary>
    protected abstract string? RetiredArgument { get; }

    /// <summary>The refusal of <see cref="RetiredArgument"/>.</summary>
    protected abstract string RetiredRefusal { get; }

    /// <summary>
    /// Every tool's call: refused when it still names the retired argument (a model that remembers it would otherwise believe it reached
    /// another database or schema), else the tool's own <see cref="InvokeToolAsync"/> under one settings snapshot.
    /// </summary>
    protected sealed override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (RetiredArgument is { } retired && Optional(arguments, retired) is not null)
        {
            return RetiredRefusal;
        }

        _call.Value = _effective();
        return await InvokeToolAsync(arguments, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The tool's own call, its arguments read.</summary>
    protected abstract ValueTask<object?> InvokeToolAsync(AIFunctionArguments arguments, CancellationToken cancellationToken);

    /// <summary>What a query's <c>to</c> asks: the workbook, its sheet, and whether to replace.</summary>
    public sealed record ExportTarget(string To, string? Sheet, bool Overwrite);

    /// <summary>
    /// The export's arguments read: the target, or null with no <c>to</c>; the refusal for an <c>overwrite</c> that is not a boolean, or a
    /// <c>sheet</c> or <c>overwrite</c> sent with no <c>to</c> (they would otherwise be dropped, and the rows returned to the chat).
    /// </summary>
    protected static string? ReadExport(AIFunctionArguments arguments, out ExportTarget? export)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        export = null;
        if (!ToolArguments.TryReadBoolean(arguments, OverwriteArgument, out var overwrite, out string raw))
        {
            return FileText.BadBoolean(OverwriteArgument, raw);
        }

        string? sheet = Optional(arguments, SheetArgument);
        if (Optional(arguments, ToArgument) is not { } to)
        {
            return sheet is not null ? OfficeText.NeedsTo(SheetArgument) : overwrite == true ? OfficeText.NeedsTo(OverwriteArgument) : null;
        }

        export = new ExportTarget(to, sheet, overwrite ?? false);
        return null;
    }

    /// <summary>A query's row cap with <c>to</c>: 1 to Excel's last row, everything up to it when not given; null when out of range.</summary>
    protected static int? ExportRows(int? maxRows) => maxRows switch
    {
        null => SqlExport.MaxRows,
        >= 1 and <= SqlExport.MaxRows => maxRows,
        _ => null,
    };

    /// <summary>
    /// A query's rows written to a workbook (2026-10-29, the Office writing plan's export; the shared half, so the six families can't drift):
    /// the save root and the target (the name, the sheet, what is there) judged before <paramref name="run"/> reaches the server; the rows
    /// streamed into the pending file by the family's own session through a <see cref="SqlExportJob"/>; the save root judged again just
    /// before the file is put in place (a late close discards it, <see cref="FileText.SaveClosedLate"/>); then the save sentence (the
    /// transcript's note), the cut note, and the columns with the first rows. A failed or timed-out run writes nothing and says how many
    /// rows it had read. <paramref name="name"/> is the connection's or database's, for a made-up file name.
    /// </summary>
    protected async Task<string> ExportAsync(ExportTarget export, string name, int maxRows, SqlCellReader cell, Func<SqlExportJob, CancellationToken, Task<SqlRun>> run,
        Func<SqlRun, string> error, CancellationToken cancellationToken, Func<DbDataReader, int, string>? header = null)
    {
        ArgumentNullException.ThrowIfNull(export);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(error);
        var root = ExportRoot?.Invoke() ?? SaveRoot.Refused(FileText.SaveNotHere);
        if (root.Root is null)
        {
            return root.Refusal ?? FileText.SaveNotHere;
        }

        var request = new XlsxRequest(export.To, export.Sheet, export.Overwrite, null, "query-" + FileNames.Safe(name), SqlExportText.DefaultSheet);
        using var target = XlsxTarget.Plan(root.Root, request, Clock, out string? refusal);
        if (target is null)
        {
            return refusal!;
        }

        var sink = target.Begin(out refusal);
        if (sink is null)
        {
            return refusal!;
        }

        var job = new SqlExportJob(sink, cell, header);
        var result = await run(job, cancellationToken).ConfigureAwait(false);
        if (job.Refusal is { } refused)
        {
            return refused;
        }

        if (result.Outcome != SqlOutcome.Ok)
        {
            return error(result) + SqlExportText.NotWritten(job.Rows, result.Outcome == SqlOutcome.Timeout);
        }

        var grid = result.Grids.Count > 0 ? result.Grids[0] : new SqlGrid([], [], false);
        var (answer, _) = target.Commit(() => (ExportRoot?.Invoke() ?? SaveRoot.Refused(FileText.SaveNotHere)).Refusal);
        return SqlExportText.Answer(answer, grid, maxRows);
    }

    /// <summary>A string argument, or null when blank.</summary>
    protected static string? Optional(AIFunctionArguments arguments, string name) =>
        ToolArguments.ReadString(arguments, name).Trim() is { Length: > 0 } text ? text : null;

    /// <summary>
    /// The names the boundary checks a statement against, read through <paramref name="names"/> (kept a minute per connection): the rows,
    /// or the family's <c>Error:</c> sentence for a read that failed — never a pass (each family's boundary has said so since 2026-10-09).
    /// </summary>
    protected static async Task<(IReadOnlyList<string[]>? Rows, string? Error)> ReadNamesAsync(BoundaryNames names, string key, Func<CancellationToken, Task<SqlRun>> read, Func<SqlRun, string> error, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(error);
        var (rows, failed) = await names.GetAsync(key, read, cancellationToken).ConfigureAwait(false);
        return failed is not null ? (null, error(failed)) : (rows, null);
    }
}
