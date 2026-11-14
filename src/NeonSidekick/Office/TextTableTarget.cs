using System.Diagnostics;
using System.Globalization;
using NeonSidekick.Diagnostics;
using NeonSidekick.Files;

namespace NeonSidekick.Office;

/// <summary>
/// A query's file write, judged before any row is read and done after (2026-11-14, the database-tools plan's #13: <see cref="XlsxTarget"/>'s
/// three steps pulled into one shape so <c>ServerTool.ExportAsync</c> drives either): <see cref="Begin"/> opens the pending file and its rows'
/// sink, <see cref="Commit"/> finishes it and puts it in place, <see cref="IDisposable.Dispose"/> lets go of whatever is left.
/// </summary>
public interface ITableFileTarget : IDisposable
{
    /// <summary>The file's path, relative to the working directory.</summary>
    string Relative { get; }

    /// <summary>The write begun: the rows' sink, or null with the refusal.</summary>
    ISheetRows? Begin(out string? refusal);

    /// <summary>The file finished and put in place: the answer's sentence and the counts, or the failure; <paramref name="stillOpen"/> judged just before.</summary>
    (string Answer, SheetResult? Sheet) Commit(Func<string?>? stillOpen = null);
}

/// <summary>
/// A CSV, JSON or JSON Lines file of a query's rows (2026-11-14, the database-tools plan's #13). Always a named file — a folder or a name with
/// no extension is a workbook, <see cref="XlsxTarget"/>'s, which keeps the made-up name's <c>FirstFree</c> rule — written whole: there is no
/// sheet to add, so a file already there is replaced only with <c>overwrite</c>, and that rule is <see cref="WorkingDirectory.BeginWrite"/>'s
/// own (<see cref="FileOutcome.Exists"/>), so no second check can race it; <see cref="Plan"/> asks the same only to answer before the server
/// is reached. Every write goes through <see cref="WorkingDirectory.BeginWrite"/>, so the sandbox, the write guard and the file checkpoints
/// (<c>/undo</c>) hold.
/// </summary>
public sealed class TextTableTarget : ITableFileTarget
{
    private readonly WorkingDirectory _files;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly bool _overwrite;
    private PendingWrite? _pending;
    private TextRows? _rows;

    private TextTableTarget(WorkingDirectory files, string relative, TextTableFormat format, bool overwrite)
    {
        _files = files;
        Relative = relative;
        Format = format;
        _overwrite = overwrite;
    }

    public string Relative { get; }

    /// <summary>What the file holds.</summary>
    public TextTableFormat Format { get; }

    /// <summary>
    /// The columns whose date-times are UTC, filled by the export's header hook before <see cref="Begin"/>'s sink takes the header (the
    /// family's <c> (UTC)</c> taken off the name and carried as each value's <c>Z</c>).
    /// </summary>
    public HashSet<int> UtcColumns { get; } = [];

    /// <summary>The file's format by <paramref name="to"/>'s extension in any case (<c>.csv</c>, <c>.json</c>, <c>.jsonl</c>), or null for none of them.</summary>
    public static TextTableFormat? FormatOf(string? to)
    {
        string ending = Path.GetExtension((to ?? "").Trim());
        return ending.Equals(".csv", StringComparison.OrdinalIgnoreCase) ? TextTableFormat.Csv
            : ending.Equals(".json", StringComparison.OrdinalIgnoreCase) ? TextTableFormat.Json
            : ending.Equals(".jsonl", StringComparison.OrdinalIgnoreCase) ? TextTableFormat.JsonLines
            : null;
    }

    /// <summary>The extension a format's file ends in.</summary>
    public static string ExtensionOf(TextTableFormat format) => format switch
    {
        TextTableFormat.Csv => ".csv",
        TextTableFormat.Json => ".json",
        _ => ".jsonl",
    };

    /// <summary>The write judged: the target, or null with the refusal. Nothing is written yet.</summary>
    public static TextTableTarget? Plan(WorkingDirectory files, string to, TextTableFormat format, string? sheet, bool overwrite, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(to);
        if (!string.IsNullOrWhiteSpace(sheet))
        {
            refusal = OfficeText.SheetNeedsXlsx(ExtensionOf(format));
            return null;
        }

        string target = to.Trim();
        var outcome = files.Resolve(target, forWrite: true, out string full);
        if (outcome != FileOutcome.Ok)
        {
            refusal = FileText.Error(outcome, target, "write");
            return null;
        }

        string relative = files.Relative(full);
        if (Directory.Exists(full))
        {
            refusal = FileText.Error(FileOutcome.IsDirectory, files.Relative(full, isDirectory: true), "write");
            return null;
        }

        if (File.Exists(full) && !overwrite)
        {
            refusal = OfficeText.TextFileExists(relative);
            return null;
        }

        refusal = null;
        return new TextTableTarget(files, relative, format, overwrite);
    }

    public ISheetRows? Begin(out string? refusal)
    {
        refusal = null;
        var begun = _files.BeginWrite(Relative, _overwrite, out var pending);
        if (pending is null)
        {
            refusal = begun.Outcome == FileOutcome.Exists ? OfficeText.TextFileExists(begun.Relative) : FileText.Error(begun.Outcome, begun.Relative, "write", begun.Detail);
            return null;
        }

        _pending = pending;
        _rows = Format == TextTableFormat.Csv ? new CsvRows(pending.Stream, UtcColumns) : new JsonRows(pending.Stream, Format == TextTableFormat.JsonLines, UtcColumns);
        return _rows;
    }

    /// <summary>
    /// <see cref="XlsxTarget.Commit"/>'s order: the file's end written (a throw from it, a full disk, leaves the pending file uncommitted, for
    /// <see cref="Dispose"/>), <paramref name="stillOpen"/> judged again (a refusal discards the file), then the file put in place.
    /// </summary>
    public (string Answer, SheetResult? Sheet) Commit(Func<string?>? stillOpen = null)
    {
        var rows = _rows ?? throw new InvalidOperationException("Begin comes first.");
        var sheet = rows.Finish();
        if (stillOpen?.Invoke() is { } closed)
        {
            _pending!.Dispose();
            return (closed == FileText.SaveMovedLate ? closed : FileText.SaveClosedLate(closed), sheet);
        }

        var result = _pending!.Commit();
        if (result.Outcome != FileOutcome.Ok)
        {
            return (OfficeText.WriteFailed(result), sheet);
        }

        string format = ExtensionOf(Format).TrimStart('.');
        DiagnosticLog.Debug(OfficeText.Category, $"{format} {result.Relative}: {sheet.Rows.ToString(CultureInfo.InvariantCulture)} rows × {sheet.Columns.ToString(CultureInfo.InvariantCulture)} columns, {result.Bytes.ToString(CultureInfo.InvariantCulture)} bytes, {_clock.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture)} ms");
        return (OfficeText.TextFile(result, sheet), sheet);
    }

    /// <summary>
    /// The writer, then the pending file, each let go in its own try (2026-11-02, review P13-2's rule, <see cref="XlsxTarget.Dispose"/>'s): a
    /// throw from the writer's flush on a full disk never leaves the pending <c>.neon-tmp</c> held open. Logged, never rethrown.
    /// </summary>
    public void Dispose()
    {
        Release(_rows, "writer");
        Release(_pending, "pending file");
    }

    /// <summary>A test's seam: the writer's dispose replaced, so a test can make it throw.</summary>
    internal Action? ReleaseRowsOverride { get; set; }

    private void Release(IDisposable? part, string what)
    {
        try
        {
            if (part is TextRows && ReleaseRowsOverride is { } over)
            {
                over();
            }
            else
            {
                part?.Dispose();
            }
        }
        catch (Exception ex) when (XlsxTarget.Failure(ex) || ex is InvalidOperationException or ObjectDisposedException)
        {
            DiagnosticLog.Warn(OfficeText.Category, OfficeText.ReleaseFailedLogLine(what, Relative, ex.GetType().Name, LogText.Excerpt(ex.Message)));
        }
    }
}
