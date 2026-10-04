namespace NeonSidekick.Printing;

/// <summary>Where a print-to-file job's output stands: a whole PDF, no file, a file still being written, or a file that is not a PDF.</summary>
public enum PdfFileState
{
    Ready,
    Missing,
    Unfinished,
    NotPdf,
}

/// <summary>
/// Waiting for a print-to-file printer's output (2026-10-03, out of the <c>print:spooler</c> smoke probe for the PDF fallback of
/// <c>convert_to_pdf</c>). Microsoft Print to PDF writes the file after <c>EndDoc</c> has returned — the spooler renders the job
/// on its own time — so the file is looked for, not assumed. It is whole when it is there, not empty, the same length on two polls
/// running, and opens with nobody else writing it; then its head says whether it is a PDF. The smoke probe waited for a length
/// above zero alone, which is enough for a magic check but not for a copy: a copy of a half-written file is a broken PDF.
/// </summary>
public static class PrintToFile
{
    /// <summary>How often the file is looked at.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>Whether <paramref name="path"/> starts with <c>%PDF</c>; false when it is missing, short or locked.</summary>
    public static bool IsPdf(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        try
        {
            byte[] head = new byte[4];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return stream.ReadAtLeast(head, 4, throwOnEndOfStream: false) == 4
                && head[0] == (byte)'%' && head[1] == (byte)'P' && head[2] == (byte)'D' && head[3] == (byte)'F';
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Polls <paramref name="path"/> on <paramref name="time"/> until it is whole or <paramref name="budget"/> runs out.</summary>
    public static async Task<PdfFileState> WaitForPdfAsync(string path, TimeSpan budget, TimeProvider time, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(time);
        long start = time.GetTimestamp();
        long last = -1;
        while (true)
        {
            long length = LengthOf(path);
            if (length > 0 && length == last && OpensAlone(path))
            {
                return IsPdf(path) ? PdfFileState.Ready : PdfFileState.NotPdf;
            }

            last = length;
            if (time.GetElapsedTime(start) >= budget)
            {
                return length > 0 ? PdfFileState.Unfinished : PdfFileState.Missing;
            }

            await Task.Delay(PollInterval, time, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary><see cref="WaitForPdfAsync"/> on the real clock, blocking: the smoke probe's form.</summary>
    public static PdfFileState WaitForPdf(string path, TimeSpan budget) =>
        WaitForPdfAsync(path, budget, TimeProvider.System, CancellationToken.None).GetAwaiter().GetResult();

    private static long LengthOf(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : -1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return -1;
        }
    }

    /// <summary>Whether the file opens with reading shared and writing not: nobody is still writing it.</summary>
    private static bool OpensAlone(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
