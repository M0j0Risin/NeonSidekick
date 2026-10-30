using System.Diagnostics;
using System.Globalization;
using System.Xml;
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
    private (long Length, DateTime Written)? _stamp;

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

    /// <summary>
    /// Whether <paramref name="ex"/> is a workbook that won't read or write (2026-10-29, review P1-4/P7-1: the one filter the query tools'
    /// export and <c>convert_to_xlsx</c> share): the file system failing, a part damaged or too large, XML or a zip that won't parse. Never a
    /// driver's error: the export wraps the plan, the begin and the commit, not the query's run.
    /// </summary>
    public static bool Failure(Exception ex) =>
        WorkingDirectory.IsFileFailure(ex) || ex is OfficeReadException or XmlException or InvalidDataException;

    /// <summary>The sentence for a <see cref="Failure"/>: damage or a part too large as the readers say it, else the file system's words.</summary>
    public static string FailureText(Exception ex, string? display)
    {
        ArgumentNullException.ThrowIfNull(ex);
        string name = string.IsNullOrWhiteSpace(display) ? OfficeText.TheWorkbook : display;
        return ex switch
        {
            OfficeReadException read => OfficeReader.Refusal(read, name),
            XmlException or InvalidDataException => OfficeText.Damaged(name, OfficeText.ThePackage),
            _ => FileText.CouldNot("write", name, ex.Message),
        };
    }

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
                // Not a zip: never added to. A compound file is an encrypted (or a legacy) workbook, which TOOLS.md promises isn't changed, so it
                // is refused even with overwrite (2026-10-30, review F93); anything else (text named .xlsx) is replaced whole when asked.
                bool encrypted = OfficeFile.LooksLikeCompoundFile(head[..read]);
                if (!request.Overwrite || encrypted)
                {
                    refusal = OfficeText.NotAWorkbook(relative, encrypted);
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

                // Replaced whole only when nothing in it is protected (2026-10-30, review F93): read through the appender's own checks — a password
                // to modify, a protected structure, macros — and each sheet's protection, then let go before the write starts. A zip that is no
                // workbook at all is replaced, as one that isn't a zip.
                using (var scanned = WorkbookAppender.Open(existing, relative, OfficeLimits.Default, out refusal, out bool noWorkbook))
                {
                    if (scanned is null && !noWorkbook)
                    {
                        return null;
                    }

                    refusal = scanned?.ReplaceWholeProblem();
                    if (refusal is not null)
                    {
                        return null;
                    }
                }

                return new XlsxTarget(files, time, relative, replaceFile: true, Office.SheetName.Fit(request.DefaultSheet, []));
            }

            var appender = WorkbookAppender.Open(existing, relative, OfficeLimits.Default, out refusal, out bool notAWorkbook);
            if (appender is null)
            {
                if (notAWorkbook && request.Overwrite)
                {
                    // A zip that is no workbook (a Word file named .xlsx, a damaged package): replaced whole when asked, as one that isn't a zip.
                    refusal = null;
                    return new XlsxTarget(files, time, relative, replaceFile: true, Office.SheetName.Fit(sheet, []));
                }

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
            return new XlsxTarget(files, time, relative, replaceFile: true, there?.Name ?? sheet)
            {
                _appender = appender,
                _replaced = there,
                _existing = existing,
                _stamp = Stamp(files, relative),
            };
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
    /// a part that won't copy). Always the model's write: no caller is the user's own hand (2026-10-30, review F56 dropped the unused
    /// <c>byUser</c> and its <see cref="WriteGuard.AsApp"/> pass).
    /// </summary>
    public ISheetRows? Begin(out string? refusal)
    {
        refusal = null;
        var begun = _files.BeginWrite(Relative, ReplaceFile, out var pending);
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
            return (closed == FileText.SaveMovedLate ? closed : FileText.SaveClosedLate(closed), sheet);
        }

        if (_appender is not null && Stamp(_files, Relative) != _stamp)
        {
            // Saved elsewhere while the rows were written (Excel for Mac holds no lock, a sync client): the copy is of the old file, so
            // putting it in place would lose that save (2026-10-29, review P2-1).
            _pending!.Dispose();
            return (OfficeText.ChangedMeanwhile(Relative), sheet);
        }

        var result = _pending!.Commit();
        if (result.Outcome != FileOutcome.Ok)
        {
            return (OfficeText.WriteFailed(result), sheet);
        }

        DiagnosticLog.Debug(OfficeText.Category, $"xlsx {Mode} {result.Relative}: {sheet.Rows.ToString(CultureInfo.InvariantCulture)} rows × {sheet.Columns.ToString(CultureInfo.InvariantCulture)} columns, {result.Bytes.ToString(CultureInfo.InvariantCulture)} bytes, {_clock.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture)} ms");
        return (OfficeText.Workbook(result, Mode, SheetName, sheet), sheet);
    }

    /// <summary>The workbook's length and last write, or null when it is gone.</summary>
    private static (long Length, DateTime Written)? Stamp(WorkingDirectory files, string relative) =>
        files.TryStamp(relative) is { } stamp ? (stamp.Length, stamp.Written) : null;

    public void Dispose()
    {
        _job?.Dispose();
        _appender?.Dispose();
        _pending?.Dispose();
        _existing?.Dispose();
    }
}
