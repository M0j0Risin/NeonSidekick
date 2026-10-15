namespace NeonSidekick.Files;

/// <summary>
/// An attached PDF's text, read once when it was pasted (2026-10-14, the PDF plan's phase 3): its pages under their headings, how many
/// the file has and how many were read, whether it is locked, and why there is none (<see cref="Problem"/>: no browser, a failed read).
/// What an endpoint that takes no PDF (a local server, the Claude Code server) is sent in its place, and what a stored session keeps.
/// </summary>
public sealed record PdfDocumentText(string Text, int PageCount, int PagesRead, bool Locked = false, string? Problem = null)
{
    /// <summary>No text at all: a scan, a failed read, a lock.</summary>
    public bool Empty => Text.Trim().Length == 0;
}

/// <summary>
/// What makes a file a PDF (2026-10-14, the PDF plan's foundations), in two strengths. <see cref="LooksLikePdf"/> is the strict one,
/// <c>%PDF-</c> and a digit at the very first byte: it is what <see cref="WorkingDirectory.LooksBinary"/> adds to its NUL probe, so a PDF
/// whose first 8 KB hold no NUL is never read as text (its raw syntax went to the model until then, and Windows' <c>print_file</c>
/// printed it as a listing) — and only there, because the probe also judges source files, diffs, notes and skills, and a script or a
/// test that mentions <c>%PDF-</c> on its third line must stay text (the plan's gap 1). <see cref="HasPdfHeader"/> is the tolerant one,
/// <c>%PDF-</c> anywhere in the first <see cref="HeaderWindow"/> bytes as readers accept it, for wherever an extension or a media type
/// already says PDF: <c>read_pdf</c>, an attachment, an edit, a web answer (gap 53). Pure.
/// </summary>
public static class PdfFile
{
    /// <summary>The extension a PDF is known by.</summary>
    public const string Extension = ".pdf";

    /// <summary>A PDF's media type.</summary>
    public const string MediaType = "application/pdf";

    /// <summary>How far into a file <see cref="HasPdfHeader"/> looks for the header: junk ahead of it is allowed up to here.</summary>
    public const int HeaderWindow = 1024;

    private static ReadOnlySpan<byte> Magic => "%PDF-"u8;

    /// <summary>Whether <paramref name="path"/> names a PDF by its extension (any case).</summary>
    public static bool IsPdfPath(string path) =>
        !string.IsNullOrEmpty(path) && Path.GetExtension(path.Trim()).Equals(Extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="bytes"/> start with <c>%PDF-</c> and a digit: a PDF by its first byte, never a text that mentions one.</summary>
    public static bool LooksLikePdf(ReadOnlySpan<byte> bytes) =>
        bytes.Length > Magic.Length && bytes.StartsWith(Magic) && char.IsAsciiDigit((char)bytes[Magic.Length]);

    /// <summary>Whether <c>%PDF-</c> appears within the first <see cref="HeaderWindow"/> bytes of <paramref name="bytes"/>.</summary>
    public static bool HasPdfHeader(ReadOnlySpan<byte> bytes) =>
        bytes[..Math.Min(bytes.Length, HeaderWindow + Magic.Length)].IndexOf(Magic) is >= 0 and <= HeaderWindow;

    /// <summary>Whether the file at <paramref name="full"/> is a PDF: its extension says so and its head holds the header. A file that cannot be read is not.</summary>
    public static bool IsPdfFile(string full)
    {
        if (!IsPdfPath(full))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, HeaderWindow + 16);
            Span<byte> head = stackalloc byte[HeaderWindow + 16];
            int read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            return HasPdfHeader(head[..read]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
