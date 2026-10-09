using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NeonSidekick.Diagnostics;
using static NeonSidekick.Printing.CupsNative;

namespace NeonSidekick.Printing;

/// <summary>
/// The Mac's <see cref="IPrintSpooler"/> (2026-10-08, the user's ask; libcups' C API over <c>lp</c>, the user's pick, so no
/// process-start site). The printers are CUPS' destinations (<c>cupsGetDests2</c>: the queue name, the default, <c>printer-info</c>);
/// a surface is a <see cref="MacPdfDocument"/> on the printer's default paper (<c>cupsCopyDestInfo</c> +
/// <c>cupsGetDestMediaDefault</c>, which talk to the printer, so only here, never in <see cref="Printers"/>; Letter or A4 by the
/// Mac's region when it will not say); a job is that PDF drawn in memory and sent as <c>application/pdf</c>:
/// <c>cupsCreateJob</c>, <c>cupsStartDocument</c>, <c>cupsWriteRequestData</c> in 64 KB chunks, <c>cupsFinishDocument</c>, all on
/// a connection of the job's own. A cancel before the job exists sends nothing; during the writing it closes that connection
/// (the half-sent document is never finished) and purges the job with <c>cupsCancelJob2</c> on the thread's default one, then
/// throws as Windows' <c>AbortDoc</c> path does; after <c>cupsFinishDocument</c> the job is CUPS'. A job with an output file
/// writes the PDF there and submits nothing (the smoke's way, and print-to-file's meaning here). A PDF goes to CUPS as it is
/// (<see cref="ShellPrint(string, string, int, IReadOnlyList{int}?, CancellationToken)"/>: printer, copies and pages as IPP
/// options); nothing else the app does not draw has a printer path on a Mac. Excluded from coverage: the decisions are
/// <see cref="MacPrintRules"/>'; the smoke's <c>print:cups</c>, the Mac facts and a held-and-cancelled live fact prove the rest.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed unsafe class MacPrintSpooler : IPrintSpooler
{
    private const int Chunk = 64 * 1024;

    private readonly IReadOnlyList<KeyValuePair<string, string>> _extraOptions;
    private readonly Action<int>? _jobCreated;

    /// <param name="extraOptions">More IPP options for every job (the live fact's <c>job-hold-until=indefinite</c>, so nothing reaches paper).</param>
    /// <param name="jobCreated">Told each job's id once CUPS has made it (the live fact cancels it, or cancels the print there).</param>
    public MacPrintSpooler(IReadOnlyList<KeyValuePair<string, string>>? extraOptions = null, Action<int>? jobCreated = null)
    {
        _extraOptions = extraOptions ?? [];
        _jobCreated = jobCreated;
    }

    public IReadOnlyList<PrinterInfo> Printers()
    {
        try
        {
            CupsDest* dests = null;
            int count = cupsGetDests2(0, &dests);
            try
            {
                var printers = new List<PrinterInfo>(count);
                for (int i = 0; i < count; i++)
                {
                    // An instance (name/instance) is a set of saved options on the same queue: the queue itself is listed once.
                    if (dests[i].Instance is not null || Text(dests[i].Name) is not { Length: > 0 } name)
                    {
                        continue;
                    }

                    var options = new Dictionary<string, string>(StringComparer.Ordinal);
                    for (int j = 0; j < dests[i].OptionCount; j++)
                    {
                        if (Text(dests[i].Options[j].Name) is { } key)
                        {
                            options[key] = Text(dests[i].Options[j].Value) ?? "";
                        }
                    }

                    printers.Add(MacPrintRules.Printer(name, dests[i].IsDefault != 0, options));
                }

                return printers;
            }
            finally
            {
                cupsFreeDests(count, dests);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            DiagnosticLog.Warn(PrintText.Category, "The printers could not be listed: " + ex.Message);
            return [];
        }
    }

    public IPrintSurface? Open(string printer, bool landscape, out string error)
    {
        ArgumentNullException.ThrowIfNull(printer);
        var paper = Paper(printer, out error);
        return paper is null ? null : new MacPdfDocument(paper, landscape);
    }

    /// <summary>The printer's default paper, the Mac's region's when it will not say; null with the error when there is no such printer.</summary>
    private static MacPaper? Paper(string printer, out string error)
    {
        CupsDest* dests = null;
        int count = cupsGetDests2(0, &dests);
        try
        {
            CupsDest* dest = cupsGetDest(printer, null, count, dests);
            if (dest is null)
            {
                error = PrintText.CannotOpen(printer, "CUPS has no such printer");
                return null;
            }

            error = "";
            nint info = cupsCopyDestInfo(0, dest);
            if (info != 0)
            {
                try
                {
                    CupsSize size;
                    if (cupsGetDestMediaDefault(0, dest, info, 0, &size) != 0 && size.Width > 0 && size.Length > 0)
                    {
                        return MacPrintRules.FromCups(Text(size.Media) ?? "", size.Width, size.Length, size.Bottom, size.Left, size.Right, size.Top);
                    }
                }
                finally
                {
                    cupsFreeDestInfo(info);
                }
            }

            DiagnosticLog.Info(PrintText.Category, $"'{printer}' gave no default paper ({LastError()}); the region's is used.");
            return MacPrintRules.ForRegion(MacPdfDocument.Region());
        }
        finally
        {
            cupsFreeDests(count, dests);
        }
    }

    public string? Print(PrintJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        var paper = Paper(job.Printer, out string error);
        if (paper is null)
        {
            return error;
        }

        byte[] pdf;
        using (var document = new MacPdfDocument(paper, job.Landscape))
        {
            pdf = document.Render(job.Pages, job.Copies, cancellationToken);
        }

        if (job.OutputFile is not null)
        {
            try
            {
                File.WriteAllBytes(job.OutputFile, pdf);
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return PrintText.Failed(job.Printer, ex.Message);
            }
        }

        using var stream = new MemoryStream(pdf, writable: false);
        return Submit(job.Printer, job.Title, [.. MacPrintRules.JobOptions(paper), .. _extraOptions], stream, cancellationToken);
    }

    /// <summary>
    /// One document to CUPS on a connection of its own: the job made, the data written in chunks with the token looked at
    /// between them, the document finished. Null when CUPS took it, else why not; a cancel withdraws the job and throws.
    /// </summary>
    private string? Submit(string printer, string title, IReadOnlyList<KeyValuePair<string, string>> options, Stream data, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        nint http = httpConnect2(cupsServer(), ippPort(), 0, 0, cupsEncryption(), 1, 30_000, null);
        if (http == 0)
        {
            return PrintText.Failed(printer, "cannot reach CUPS: " + LastError());
        }

        int jobId = 0;
        bool finished = false;
        try
        {
            CupsOption* list = null;
            int count = 0;
            foreach (var (name, value) in options)
            {
                count = cupsAddOption(name, value, count, &list);
            }

            try
            {
                jobId = cupsCreateJob(http, printer, title, count, list);
            }
            finally
            {
                cupsFreeOptions(count, list);
            }

            if (jobId == 0)
            {
                return PrintText.Failed(printer, LastError());
            }

            _jobCreated?.Invoke(jobId);
            if (cupsStartDocument(http, printer, jobId, title, "application/pdf", 1) != HttpContinue)
            {
                return Withdraw(ref http, printer, jobId, PrintText.Failed(printer, LastError()));
            }

            var buffer = new byte[Chunk];
            int read;
            while ((read = data.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    Withdraw(ref http, printer, jobId, null);
                    cancellationToken.ThrowIfCancellationRequested();
                }

                fixed (byte* bytes = buffer)
                {
                    if (cupsWriteRequestData(http, bytes, (nuint)read) != HttpContinue)
                    {
                        return Withdraw(ref http, printer, jobId, PrintText.Failed(printer, LastError()));
                    }
                }
            }

            if (cancellationToken.IsCancellationRequested)
            {
                Withdraw(ref http, printer, jobId, null);
                cancellationToken.ThrowIfCancellationRequested();
            }

            int status = cupsFinishDocument(http, printer);
            finished = true;
            return status < IppFirstError ? null : Withdraw(ref http, printer, jobId, PrintText.Failed(printer, LastError()));
        }
        catch (Exception) when (jobId != 0 && !finished && http != 0)
        {
            // A throw mid-document (an unreadable file): the half-sent job is withdrawn, never printed.
            Withdraw(ref http, printer, jobId, null);
            throw;
        }
        finally
        {
            if (http != 0)
            {
                httpClose(http);
            }
        }
    }

    /// <summary>The job's connection closed (a half-sent document dropped) and the job purged on the thread's default connection; <paramref name="error"/> back.</summary>
    private static string? Withdraw(ref nint http, string printer, int jobId, string? error)
    {
        if (http != 0)
        {
            httpClose(http);
            http = 0;
        }

        if (cupsCancelJob2(0, printer, jobId, 1) >= IppFirstError)
        {
            DiagnosticLog.Warn(PrintText.Category, $"Job {jobId} on '{printer}' could not be withdrawn: {LastError()}");
        }
        else
        {
            DiagnosticLog.Info(PrintText.Category, $"Job {jobId} on '{printer}' was withdrawn.");
        }

        return error;
    }

    public bool CanShellPrint(string path) => MacPrintRules.IsPdf(path);

    public bool ShellPrintTakesOptions => true;

    public int ShellPageCount(string path) => MacPrintRules.IsPdf(path) ? MacPdfDocument.PageCount(path) : 0;

    /// <summary>A PDF on the default printer, one copy, every page: the plain form, kept for the seam.</summary>
    public string? ShellPrint(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (Printers().FirstOrDefault(p => p.IsDefault) is not { } printer)
        {
            return PrintText.MacNoPrinter;
        }

        return ShellPrint(path, printer.Name, 1, null, CancellationToken.None);
    }

    public string? ShellPrint(string path, string printer, int copies, IReadOnlyList<int>? pages, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(printer);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, Chunk);
            return Submit(printer, Path.GetFileName(path), [.. MacPrintRules.FileOptions(copies, pages), .. _extraOptions], stream, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return PrintText.Failed(printer, ex.Message);
        }
    }

    /// <summary>The active jobs on <paramref name="printer"/> as (id, state): the live fact's look at a held job. Empty when none.</summary>
    public static IReadOnlyList<(int Id, int State)> ActiveJobs(string printer)
    {
        CupsJob* jobs = null;
        int count = cupsGetJobs2(0, &jobs, printer, 1, WhichJobsActive);
        try
        {
            var list = new List<(int, int)>(Math.Max(0, count));
            for (int i = 0; i < count; i++)
            {
                list.Add((jobs[i].Id, jobs[i].State));
            }

            return list;
        }
        finally
        {
            if (count > 0)
            {
                cupsFreeJobs(count, jobs);
            }
        }
    }

    /// <summary>Purges a job: the live fact's tidy-up. True when CUPS took the cancel.</summary>
    public static bool Cancel(string printer, int jobId) => cupsCancelJob2(0, printer, jobId, 1) < IppFirstError;

    /// <summary>Whether libcups binds here: the smoke's first look.</summary>
    public static bool Available => NativeLibrary.TryLoad(MacPrintRules.CupsLibrary, out _);
}
