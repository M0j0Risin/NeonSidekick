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
    private IDisposable? _writer;

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
    /// The text files a query writes, by extension (2026-11-15, the database-tools review's #45: one table in place of four lists that could
    /// drift), read by <see cref="FormatOf"/>, <see cref="ExtensionOf"/> and <see cref="OutputNames.QueryKinds"/>. A format's first entry
    /// is its own name, so <c>.ndjson</c> reads as JSON Lines and keeps its name while the messages say <c>.jsonl</c>.
    /// </summary>
    public static readonly IReadOnlyList<(string Extension, TextTableFormat Format)> Kinds =
    [
        (".csv", TextTableFormat.Csv),
        (".json", TextTableFormat.Json),
        (".jsonl", TextTableFormat.JsonLines),
        (".ndjson", TextTableFormat.JsonLines),
    ];

    /// <summary>The file's format by <paramref name="to"/>'s extension in any case (<see cref="Kinds"/>), or null for none of them.</summary>
    public static TextTableFormat? FormatOf(string? to)
    {
        string ending = Path.GetExtension((to ?? "").Trim());
        foreach (var (extension, format) in Kinds)
        {
            if (ending.Equals(extension, StringComparison.OrdinalIgnoreCase))
            {
                return format;
            }
        }

        return null;
    }

    /// <summary>The extension a format's file ends in: its first in <see cref="Kinds"/>.</summary>
    public static string ExtensionOf(TextTableFormat format) => Kinds.First(k => k.Format == format).Extension;

    /// <summary>
    /// The write judged: the target, or null with the refusal. Nothing is written yet. <paramref name="to"/> names a file, never a folder:
    /// the caller has judged that once (2026-11-15, the database-tools review's #128, <see cref="OutputNames.IntoFolder"/>), and a folder made
    /// meanwhile fails <see cref="WorkingDirectory.BeginWrite"/>.
    /// </summary>
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
        _rows = Format == TextTableFormat.Csv ? new CsvRows(pending.Stream) : new JsonRows(pending.Stream, Format == TextTableFormat.JsonLines);
        _writer = _rows;
        return _rows;
    }

    /// <summary>
    /// <see cref="XlsxTarget.Commit"/>'s order: the file's end written (a throw from it, a full disk, leaves the pending file uncommitted, for
    /// <see cref="Dispose"/>), then the writer let go before anything else (2026-11-15, the database-tools review's #18/#130: released only at
    /// <see cref="Dispose"/>, after the file was put in place, a writer could flush into a closed stream), then
    /// <paramref name="stillOpen"/> judged again (a refusal discards the file) and the file put in place (<see cref="TableFileWrite.Put"/>).
    /// </summary>
    public (string Answer, SheetResult? Sheet) Commit(Func<string?>? stillOpen = null)
    {
        var rows = _rows ?? throw new InvalidOperationException("Begin comes first.");
        var sheet = rows.Finish();
        var writer = _writer;
        _writer = null;
        writer?.Dispose();
        var (refused, result) = TableFileWrite.Put(_pending!, stillOpen);
        if (result is null)
        {
            return (refused!, sheet);
        }

        string format = FormatName;
        DiagnosticLog.Debug(OfficeText.Category, $"{format} {result.Relative}: {sheet.Rows.ToString(CultureInfo.InvariantCulture)} rows × {sheet.Columns.ToString(CultureInfo.InvariantCulture)} columns, {result.Bytes.ToString(CultureInfo.InvariantCulture)} bytes, {_clock.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture)} ms");
        return (OfficeText.TextFile(result, sheet), sheet);
    }

    /// <summary>The format's name in the log: its extension without the dot.</summary>
    private string FormatName => ExtensionOf(Format).TrimStart('.');

    /// <summary>
    /// The writer (when <see cref="Commit"/> didn't get to it), then the pending file, each let go in its own try (2026-11-02, review P13-2's
    /// rule, <see cref="XlsxTarget.Dispose"/>'s): a throw from the writer's flush on a full disk never leaves the pending <c>.neon-tmp</c> held
    /// open. Logged, never rethrown.
    /// </summary>
    public void Dispose()
    {
        TableFileWrite.Release(_writer, FormatName, "writer", Relative);
        _writer = null;
        TableFileWrite.Release(_pending, FormatName, "pending file", Relative);
    }

    /// <summary>A test's seam (2026-11-15, #130, <see cref="XlsxTarget.WrapJob"/>'s shape): the begun writer wrapped, so a test can make its close throw. Nothing before <see cref="Begin"/>.</summary>
    internal void WrapWriter(Func<IDisposable, IDisposable> wrap) => _writer = _writer is null ? null : wrap(_writer);
}
