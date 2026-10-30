using System.Data.Common;
using System.Globalization;
using System.Text.Json;
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

    /// <summary>A query's row cap in a schema's words: <see cref="AppSettingsData.MaxSqlQueryMaxRows"/>, never a literal of its own (review F55).</summary>
    protected static string MaxRowsWords => AppSettingsData.MaxSqlQueryMaxRows.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// A query tool's last members: <c>max_rows</c>, and with <paramref name="export"/> its words of <c>to</c> (Excel's last row,
    /// <see cref="SheetWriter.MaxDataRows"/>) and <see cref="ExportProperties"/> (2026-10-30, review F55: the caps were literals, and the
    /// plain schema was cut out of the export one by exact strings).
    /// </summary>
    protected static string QueryMembers(bool export) =>
        "\"max_rows\": { \"type\": \"integer\", \"description\": \"How many rows at most, 1 to " + MaxRowsWords
        + (export ? " (1 to " + SheetWriter.MaxDataRows.ToString(CultureInfo.InvariantCulture) + " with to)" : "")
        + ". Leave it out for the user's default" + (export ? " (with to, every row)" : "") + ".\" }"
        + (export ? ", " + ExportProperties : "");

    /// <summary>
    /// A query tool's two schemas (2026-10-29, review P1-20: <c>to</c> was offered in plan mode and with *File tools* off, and each call was
    /// refused): <paramref name="json"/> given <see cref="QueryMembers"/> with the export, and without it.
    /// </summary>
    protected static (JsonElement Export, JsonElement Plain) QuerySchemas(Func<string, string> json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return (ToolSchema.Parse(json(QueryMembers(export: true))), ToolSchema.Parse(json(QueryMembers(export: false))));
    }

    /// <summary>The schema in force: with <c>to</c> while a save root is open (read fresh per request, so plan mode and *File tools* follow).</summary>
    protected JsonElement QuerySchema((JsonElement Export, JsonElement Plain) schemas) => CanSave ? schemas.Export : schemas.Plain;

    /// <summary><see cref="ExportSentence"/> while a save root is open, else nothing.</summary>
    protected string SaveSentence => CanSave ? ExportSentence : "";

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

    /// <summary>
    /// A query's row cap (2026-10-29, review P1-17: the six tools each spelled it out): without <c>to</c>, <paramref name="given"/> or
    /// <paramref name="defaultRows"/> within <paramref name="min"/>–<paramref name="max"/>; with it, 1 to Excel's last row when given, and when
    /// not, one row past it (review P2-4), so the sheet itself stops and the answer says "Excel's last row", not "max_rows". Null when it
    /// fits, else the refusal.
    /// </summary>
    protected static string? RowCap(ExportTarget? export, int? given, int defaultRows, int min, int max, out int rows)
    {
        if (export is null)
        {
            rows = given ?? defaultRows;
            return rows < min || rows > max ? SqlText.BadMaxRows(min, max) : null;
        }

        rows = given ?? SqlExport.MaxRows + 1;
        return given is < 1 or > SqlExport.MaxRows ? SqlText.BadMaxRows(1, SqlExport.MaxRows) : null;
    }

    /// <summary>
    /// A query's rows written to a workbook (2026-10-29, the Office writing plan's export; the shared half, so the six families can't drift):
    /// the save root and the target (the name, the sheet, what is there) judged before <paramref name="run"/> reaches the server; the
    /// workbook begun only when the first result set arrives (review P2-8: a query that fails to connect or parse copies nothing and leaves
    /// no checkpoint entry); the rows streamed into the pending file by the family's own session through a <see cref="SqlExportJob"/>; the
    /// save root judged again just before the file is put in place (a late close, or the working folder moved, discards it); then the save
    /// sentence (the transcript's note), the cut note, and the columns with the first rows. A failed or timed-out run writes nothing and says
    /// how many rows it had read. A workbook that won't read or write is answered, never thrown (review P1-4); the run's own errors stay the
    /// family's. <paramref name="name"/> is the connection's or database's, for a made-up file name; <paramref name="given"/> the call's own
    /// <c>max_rows</c>, which the cut note names only when it was given.
    /// </summary>
    protected async Task<string> ExportAsync(ExportTarget export, string name, int rows, int? given, int timeoutSetting, SqlCellReader cell,
        Func<SqlExportJob, int, CancellationToken, Task<SqlRun>> run, Func<SqlRun, string> error, CancellationToken cancellationToken,
        Func<DbDataReader, int, string>? header = null)
    {
        ArgumentNullException.ThrowIfNull(export);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(error);
        var root = ExportRoot?.Invoke() ?? SaveRoot.Refused(FileText.SaveNotHere);
        if (root.Root is not { } files)
        {
            return root.Refusal ?? FileText.SaveNotHere;
        }

        string folder = files.Root;
        var request = new XlsxRequest(export.To, export.Sheet, export.Overwrite, null, SqlExport.FileStem(name), SqlExportText.DefaultSheet);
        XlsxTarget? planned;
        string? refusal;
        try
        {
            planned = XlsxTarget.Plan(files, request, Clock, out refusal);
        }
        catch (Exception ex) when (XlsxTarget.Failure(ex))
        {
            return XlsxTarget.FailureText(ex, export.To);
        }

        using var target = planned;
        if (target is null)
        {
            return refusal!;
        }

        var job = new SqlExportJob(() =>
        {
            // Inside the family's session: a workbook that won't copy is the job's refusal, never a throw its catch would call the server's.
            try
            {
                return (target.Begin(out string? refused), refused);
            }
            catch (Exception ex) when (XlsxTarget.Failure(ex))
            {
                return (null, XlsxTarget.FailureText(ex, target.Relative));
            }
        }, cell, header, ex => XlsxTarget.FailureText(ex, target.Relative));
        var result = await run(job, SqlExport.TimeoutFor(timeoutSetting), cancellationToken).ConfigureAwait(false);
        if (job.Refusal is { } refusedRows)
        {
            return refusedRows;
        }

        if (result.Outcome != SqlOutcome.Ok)
        {
            return error(result) + SqlExportText.NotWritten(job.Rows, result.Outcome == SqlOutcome.Timeout);
        }

        if (!job.Read)
        {
            return SqlExportText.NoResultSet;
        }

        var grid = result.Grids.Count > 0 ? result.Grids[0] : new SqlGrid([], [], false);
        string answer;
        try
        {
            (answer, _) = target.Commit(() => StillOpen(folder));
        }
        catch (Exception ex) when (XlsxTarget.Failure(ex))
        {
            return XlsxTarget.FailureText(ex, target.Relative) + SqlExportText.NotWritten(job.Rows, timedOut: false);
        }

        return SqlExportText.Answer(answer, grid, given);
    }

    /// <summary>
    /// The save root asked again just before the file is put in place: its refusal, or <see cref="FileText.SaveMovedLate"/> when the working
    /// folder is another than the one the export began in (2026-10-29, review P7-7: a <c>/cwd</c> mid-export wrote into the old folder and
    /// answered a path relative to the new one); null when it may go.
    /// </summary>
    private string? StillOpen(string folder)
    {
        var now = ExportRoot?.Invoke() ?? SaveRoot.Refused(FileText.SaveNotHere);
        if (now.Root is not { } files)
        {
            return now.Refusal ?? FileText.SaveNotHere;
        }

        return Plans.RalphSnapshot.SameRoot(files.Root, folder) ? null : FileText.SaveMovedLate;
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
