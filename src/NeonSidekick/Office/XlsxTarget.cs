using System.Diagnostics;
using System.Globalization;
using NeonSidekick.Diagnostics;
using NeonSidekick.Files;

namespace NeonSidekick.Office;

/// <summary>What a workbook write is asked for: where (<paramref name="To"/>), which sheet, whether it may replace, and how a made-up name is built.</summary>
/// <param name="SourceRelative">A source file's path (a CSV beside which the workbook goes); null for rows with no file.</param>
/// <param name="FallbackStem">A made-up name's stem with no source: <c>table</c>, <c>query-&lt;name&gt;</c>.</param>
/// <param name="DefaultSheet">The sheet's name when none is given, made fit.</param>
public sealed record XlsxRequest(string? To, string? Sheet, bool Overwrite, string? SourceRelative, string FallbackStem, string DefaultSheet, string? Title = null);

/// <summary>
/// One workbook write, judged before any row is read and done after (2026-10-29, the Office writing plan's shared target rules, for
/// <c>convert_to_xlsx</c> and the six query tools' <c>to</c>): the name (<see cref="OutputNames"/>: <c>.xlsx</c> added, another document
/// type or <c>~$</c> refused, a made-up name never replacing anything), the sheet's name (<see cref="SheetName"/>), then what is there —
/// nothing (a new workbook), a workbook and a new sheet (added, no <c>overwrite</c> needed: nothing is replaced and <c>/undo</c> takes it
/// back), a workbook and its sheet (replaced only with <c>overwrite</c>), a workbook and no sheet (replaced whole only with <c>overwrite</c>),
/// or a file that isn't really a workbook (never added to; replaced whole only with <c>overwrite</c>). <see cref="Begin"/> opens the
/// <see cref="PendingWrite"/> and the sheet; <see cref="Commit"/> puts the file in place. Every write goes through
/// <see cref="WorkingDirectory.BeginWrite"/>, so the sandbox, the write guard and the file checkpoints hold.
/// </summary>
public sealed class XlsxTarget : IDisposable
{
    public const string Extension = ".xlsx";

    private readonly WorkingDirectory _files;
    private readonly TimeProvider _time;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private WorkbookAppender? _appender;
    private WorkbookSheet? _replaced;
    private FileStream? _existing;
    private PendingWrite? _pending;
    private IWorkbookJob? _job;

    private XlsxTarget(WorkingDirectory files, TimeProvider time, string relative, bool replaceFile, string sheet)
    {
        _files = files;
        _time = time;
        Relative = relative;
        ReplaceFile = replaceFile;
        SheetName = sheet;
    }

    /// <summary>The workbook's path, relative to the working directory.</summary>
    public string Relative { get; }

    /// <summary>Whether a file is there to replace (whole, or by a sheet added or replaced).</summary>
    public bool ReplaceFile { get; }

    /// <summary>The sheet written.</summary>
    public string SheetName { get; }

    /// <summary>What the write does.</summary>
    public WorkbookWrite Mode => _appender is null ? WorkbookWrite.New : _replaced is null ? WorkbookWrite.Added : WorkbookWrite.ReplacedSheet;

    /// <summary>The workbook's date system: an added sheet's dates match the workbook's.</summary>
    public bool Date1904 => _appender?.Date1904 ?? false;

    /// <summary>The write judged: the target, or null with the refusal. Nothing is written yet.</summary>
    public static XlsxTarget? Plan(WorkingDirectory files, XlsxRequest request, TimeProvider time, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(time);
        refusal = OutputNames.Refusal(request.To, Extension, files.IsExistingDirectory);
        if (refusal is not null)
        {
            return null;
        }

        string? sheet = string.IsNullOrWhiteSpace(request.Sheet) ? null : request.Sheet.Trim();
        if (sheet is not null && Office.SheetName.Problem(sheet))
        {
            refusal = OfficeText.BadSheetName(sheet);
            return null;
        }

        string target = OutputNames.For(request.To, request.SourceRelative, null, request.Title, time.GetLocalNow(), files.IsExistingDirectory, Extension, request.FallbackStem);
        bool named = !OutputNames.IntoFolder(request.To, files.IsExistingDirectory);
        var outcome = files.Resolve(target, forWrite: true, out string full);
        if (outcome != FileOutcome.Ok)
        {
            refusal = FileText.Error(outcome, target, "write");
            return null;
        }

        if (!named)
        {
            // A made-up name never replaces anything (the FirstFree rule).
            full = WorkingDirectory.FirstFree(full);
        }

        string relative = files.Relative(full);
        if (Directory.Exists(full))
        {
            refusal = FileText.Error(FileOutcome.IsDirectory, files.Relative(full, isDirectory: true), "write");
            return null;
        }

        if (!File.Exists(full))
        {
            return new XlsxTarget(files, time, relative, replaceFile: false, Office.SheetName.Fit(sheet ?? request.DefaultSheet, []));
        }

        FileStream existing;
        try
        {
            existing = RegularFiles.OpenRead(full, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            refusal = FileText.Error(FileOutcome.Failed, relative, "read", ex.Message);
            return null;
        }

        bool keep = false;
        try
        {
            Span<byte> head = stackalloc byte[8];
            int read = existing.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            existing.Position = 0;
            if (!OfficeFile.LooksLikeZip(head[..read]))
            {
                // Not really a workbook: never added to, replaced whole only when asked.
                if (!request.Overwrite)
                {
                    refusal = OfficeText.NotAWorkbook(relative, OfficeFile.LooksLikeCompoundFile(head[..read]));
                    return null;
                }

                return new XlsxTarget(files, time, relative, replaceFile: true, Office.SheetName.Fit(sheet ?? request.DefaultSheet, []));
            }

            if (sheet is null)
            {
                if (!request.Overwrite)
                {
                    refusal = OfficeText.WorkbookExists(relative);
                    return null;
                }

                return new XlsxTarget(files, time, relative, replaceFile: true, Office.SheetName.Fit(request.DefaultSheet, []));
            }

            var appender = WorkbookAppender.Open(existing, relative, OfficeLimits.Default, out refusal);
            if (appender is null)
            {
                return null;
            }

            var there = appender.Find(sheet);
            if (there is not null)
            {
                refusal = !request.Overwrite ? OfficeText.SheetExists(relative, there.Name) : appender.ReplaceProblem(there);
                if (refusal is not null)
                {
                    appender.Dispose();
                    return null;
                }
            }

            keep = true;
            return new XlsxTarget(files, time, relative, replaceFile: true, there?.Name ?? sheet) { _appender = appender, _replaced = there, _existing = existing };
        }
        finally
        {
            if (!keep)
            {
                existing.Dispose();
            }
        }
    }

    /// <summary>
    /// The write begun: the pending file opened and the workbook's sheet ready for rows; null with the refusal (the target changed since,
    /// a part that won't copy). <paramref name="byUser"/> writes with the app's pass (<see cref="WriteGuard.AsApp"/>).
    /// </summary>
    public ISheetRows? Begin(out string? refusal, bool byUser = false)
    {
        refusal = null;
        PendingWrite? pending = null;
        var begun = byUser ? WriteGuard.AsApp(() => _files.BeginWrite(Relative, ReplaceFile, out pending)) : _files.BeginWrite(Relative, ReplaceFile, out pending);
        if (pending is null)
        {
            refusal = FileText.Error(begun.Outcome, begun.Relative, "write", begun.Detail);
            return null;
        }

        _pending = pending;
        if (_appender is null)
        {
            _job = new ExcelWriter(pending.Stream, SheetName, _time, Path.GetFileNameWithoutExtension(Relative));
            return _job.Sheet;
        }

        refusal = _appender.Start(pending.Stream, SheetName, _replaced, _time);
        if (refusal is not null)
        {
            return null;
        }

        _job = _appender;
        return _job.Sheet;
    }

    /// <summary>
    /// The workbook finished and put in place: the result sentence (<see cref="OfficeText.Workbook"/>) and the sheet's counts, or the failure
    /// (the sharing-violation sentence when Excel holds the file). <paramref name="stillOpen"/> judges the save root again just before the
    /// file is committed (the admin review's F7 rule): a refusal then discards it and answers <see cref="FileText.SaveClosedLate"/>.
    /// </summary>
    public (string Answer, SheetResult? Sheet) Commit(Func<string?>? stillOpen = null)
    {
        var job = _job ?? throw new InvalidOperationException("Begin comes first.");
        var sheet = job.Finish();
        _existing?.Dispose();
        _existing = null;
        if (stillOpen?.Invoke() is { } closed)
        {
            _pending!.Dispose();
            return (FileText.SaveClosedLate(closed), sheet);
        }

        var result = _pending!.Commit();
        if (result.Outcome != FileOutcome.Ok)
        {
            return (OfficeText.WriteFailed(result), sheet);
        }

        DiagnosticLog.Debug(OfficeText.Category, $"xlsx {Mode} {result.Relative}: {sheet.Rows.ToString(CultureInfo.InvariantCulture)} rows × {sheet.Columns.ToString(CultureInfo.InvariantCulture)} columns, {result.Bytes.ToString(CultureInfo.InvariantCulture)} bytes, {_clock.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture)} ms");
        return (OfficeText.Workbook(result, Mode, SheetName, sheet), sheet);
    }

    public void Dispose()
    {
        _job?.Dispose();
        _appender?.Dispose();
        _pending?.Dispose();
        _existing?.Dispose();
    }
}
