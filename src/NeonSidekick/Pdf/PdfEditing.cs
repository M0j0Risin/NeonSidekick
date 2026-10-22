using NeonSidekick.Diagnostics;
using NeonSidekick.Files;
using NeonSidekick.Images;
using NeonSidekick.Web;

namespace NeonSidekick.Pdf;

/// <summary>
/// The one door to editing PDFs (2026-10-14, the PDF plan's phase 4b, the user's pick): <c>pdf_edit</c>, <c>/pdf merge|extract|rotate|delete</c>
/// and headless all come through here. pdf-lib in the headless browser does the work (<see cref="IHeadlessBrowser.EditPdfAsync"/>) whatever
/// <c>PDF engine</c> says — Microsoft Print to PDF can't edit one. Every refusal comes before the browser starts: the request's own
/// (<see cref="PdfEditPlan.Check"/>), each PDF (in the working directory, a PDF by its header, <see cref="BrowserPdfReader.MaxBytes"/> at most,
/// all of them <see cref="BrowserPdfEditor.MaxOutputBytes"/> together), a <c>to</c> outside the working directory or onto a file there
/// without <c>overwrite</c>. The new PDF never replaces its source unasked: it is named after it (<see cref="PdfEditPlan.Suffix"/>, the first
/// free of <c>-2</c>, <c>-3</c>… as <c>image_edit</c>'s are, gap 67), and only a <c>to</c> naming a file with <c>overwrite</c> writes over one,
/// the source included. Every failure is an <c>Error:</c> sentence, never a throw.
/// </summary>
public sealed class PdfEditing
{
    private readonly IHeadlessBrowser _browser;
    private readonly WorkingDirectory _files;
    private readonly Func<string?> _executable;
    private readonly TimeProvider _time;

    /// <param name="executable">The browser to run, found at each call (<c>Web browser path</c>, else the installed ones); null = none.</param>
    public PdfEditing(IHeadlessBrowser browser, WorkingDirectory files, Func<string?> executable, TimeProvider? time = null)
    {
        _browser = browser ?? throw new ArgumentNullException(nameof(browser));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _executable = executable ?? throw new ArgumentNullException(nameof(executable));
        _time = time ?? TimeProvider.System;
    }

    public async Task<string> EditAsync(PdfEditRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (PdfEditPlan.Check(request) is { } refused)
        {
            return refused;
        }

        var paths = request.Paths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).ToList();
        var displays = new List<string>(paths.Count);
        var inputs = new List<byte[]>(paths.Count);
        long total = 0;
        foreach (string path in paths)
        {
            var (bytes, display, error) = Input(path);
            if (bytes is null)
            {
                return error!;
            }

            total += bytes.Length;
            if (total > BrowserPdfEditor.MaxOutputBytes)
            {
                return PdfText.TooBigTogether(BrowserPdfEditor.MaxOutputBytes / (1024 * 1024));
            }

            displays.Add(display);
            inputs.Add(bytes);
        }

        string? to = string.IsNullOrWhiteSpace(request.To) ? null : request.To.Trim();
        bool named = to is not null && !PdfPlan.IntoFolder(to, IsFolder);
        if (named && (request.ByUser ? WriteGuard.AsApp(() => Target(PdfPlan.WithPdf(to!), request.Overwrite)) : Target(PdfPlan.WithPdf(to!), request.Overwrite)) is { } badTarget)
        {
            return badTarget;
        }

        if (_executable() is not { } executable)
        {
            return PdfText.NoBrowserToEdit;
        }

        string first = displays[0];
        var job = new PdfEditJob(request.Op, inputs, count => PdfEditPlan.PagesFor(request, first, count), PdfEditPlan.AngleOf(request.Angle) ?? 0, request.Fields, request.Flatten);
        var edit = await _browser.EditPdfAsync(executable, job, cancellationToken).ConfigureAwait(false);
        if (Refusal(edit, displays, executable) is { } notMade)
        {
            return notMade;
        }

        string target = PdfPlan.OutputFor(to, first, null, null, _time.GetLocalNow(), IsFolder, PdfEditPlan.Suffix(request.Op, edit.Pages));
        // The user's own /pdf writes with the app's pass (2026-10-22, the write guard); the model's pdf_edit is judged.
        var written = request.ByUser ? WriteGuard.AsApp(() => Write(target, edit.Output!, named, request.Overwrite)) : Write(target, edit.Output!, named, request.Overwrite);
        string result = PdfText.Edited(written, edit.PageCount, PdfEditPlan.WhatWasDone(request, displays, edit.Pages));
        DiagnosticLog.Info(PdfText.Category, result);
        return result;
    }

    private bool IsFolder(string relative) => _files.IsExistingDirectory(relative);

    /// <summary>Whether <paramref name="relative"/> is a file in the working directory: <c>/pdf</c>'s test of a first word that could be a verb.</summary>
    public bool IsFile(string relative) =>
        _files.Resolve(relative, forWrite: false, out string full) == FileOutcome.Ok && File.Exists(full);

    // One PDF read for the edit: in the working directory, a file, a PDF by its header, not too large.
    private (byte[]? Bytes, string Display, string? Error) Input(string path)
    {
        var outcome = _files.Resolve(path, forWrite: false, out string full);
        if (outcome != FileOutcome.Ok)
        {
            return (null, path, FileText.Error(outcome, path, "read"));
        }

        string display = _files.Relative(full);
        if (Directory.Exists(full))
        {
            return (null, display, FileText.Error(FileOutcome.IsDirectory, _files.Relative(full, isDirectory: true), "read"));
        }

        if (!File.Exists(full))
        {
            return (null, display, FileText.Error(FileOutcome.Missing, display, "read"));
        }

        if (!PdfFile.IsPdfFile(full))
        {
            return (null, display, PdfText.NotAPdf(display));
        }

        var read = _files.ReadBytes(path, BrowserPdfReader.MaxBytes);
        return read.Outcome switch
        {
            FileOutcome.Ok => (read.Bytes, display, null),
            FileOutcome.TooBig => (null, display, PdfText.TooBigToEdit(display)),
            _ => (null, display, FileText.Error(read.Outcome, read.Relative, "read", read.Detail)),
        };
    }

    // A named output checked before the browser runs: in the working directory, and onto a file only with overwrite.
    private string? Target(string target, bool overwrite)
    {
        var outcome = _files.Resolve(target, forWrite: true, out string full);
        if (outcome != FileOutcome.Ok)
        {
            return FileText.Error(outcome, target, "write");
        }

        return !overwrite && File.Exists(full) ? FileText.Error(FileOutcome.Exists, _files.Relative(full), "write") : null;
    }

    // Why nothing was made, as the sentence to answer; null when the edit made its PDF.
    private static string? Refusal(PdfEdit edit, IReadOnlyList<string> displays, string executable)
    {
        string first = displays[0];
        if (edit.LockedInput is { } locked)
        {
            return PdfText.LockedToEdit(displays[Math.Clamp(locked, 0, displays.Count - 1)]);
        }

        if (edit.Refused is { } refused)
        {
            return refused;
        }

        if (edit.Xfa)
        {
            return PdfText.XfaForm(first);
        }

        if (edit.NoFields)
        {
            return PdfText.NoFormFields(first);
        }

        if (edit.UnknownFields.Count > 0)
        {
            return PdfText.UnknownFields(first, edit.UnknownFields, edit.FieldNames);
        }

        if (edit.BadFields.Count > 0)
        {
            return PdfText.BadFieldValues(first, edit.BadFields);
        }

        return edit.Ok && edit.Output is not null ? null : PdfText.EditFailed(PdfText.BrowserName(executable), edit.Detail.Length > 0 ? edit.Detail : "it made no PDF");
    }

    // The new PDF written: at a named path (over a file only with overwrite), or under the first free made-up name, -2, -3…, which
    // never replaces anything (image_edit's loop), overwrite or not: overwrite is for a named to alone (2026-10-20, review: overwrite
    // with no to wrote over a file already called report-rotated.pdf).
    private WriteResult Write(string target, byte[] bytes, bool named, bool overwrite)
    {
        if (named)
        {
            return _files.WriteBytes(target, bytes, overwrite);
        }

        WriteResult written;
        int n = 1;
        do
        {
            written = _files.WriteBytes(ImageOutput.Numbered(target, n), bytes, overwrite: false);
        }
        while (written.Outcome == FileOutcome.Exists && ++n < 100);
        return written;
    }
}
