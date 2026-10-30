namespace NeonSidekick.Files;

/// <summary>
/// The pictures a document shows, read from the sandbox only (2026-10-03 as <c>PdfConverter</c>'s private <c>Inliner</c>; moved here
/// 2026-10-29, the Office writing plan, so <c>convert_to_pdf</c> and <c>convert_to_docx</c> find the same pictures): an address relative to
/// <c>folder</c> (the source's own) or a <c>file:</c> URL, <c>?</c> and <c>#</c> cut, judged by
/// <see cref="WorkingDirectory.Resolve(string, bool, out string)"/>; a web address, a missing file or one outside is null. Counts what it
/// hands out against <paramref name="budget"/>; past it <see cref="OverBudget"/> is set and nothing more is handed out.
/// </summary>
public sealed class SandboxPictures(WorkingDirectory files, string folder, long budget)
{
    private long _total;

    public bool OverBudget { get; private set; }

    /// <summary>A Markdown picture as a <c>data:</c> URI for a page, or null; a <c>data:image/…</c> address passes through as it is.</summary>
    public string? Picture(string address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
        {
            return address;
        }

        string? full = Locate(address);
        if (full is null || Pdf.PdfHtml.ImageMediaType(full) is not { } type)
        {
            return null;
        }

        return Read(full, ImageFile.MaxFileBytes) is { } bytes ? Pdf.PdfHtml.DataUri(bytes, type) : null;
    }

    /// <summary>A file a page names, its bytes, or null (missing, outside, over <see cref="ImageFile.MaxFileBytes"/> or the budget).</summary>
    public byte[]? Bytes(string address) => Bytes(address, accept: null);

    /// <summary>
    /// <see cref="Bytes(string)"/> for only the files <paramref name="accept"/> takes by their path, judged before anything is read or counted
    /// (2026-10-29, review P3-1: a document's WebP and video links spent the budget though none could be embedded).
    /// </summary>
    public byte[]? Bytes(string address, Func<string, bool>? accept) =>
        Locate(address) is { } full && (accept is null || accept(full)) ? Read(full, ImageFile.MaxFileBytes) : null;

    /// <summary>
    /// Bytes from elsewhere (a <c>data:</c> picture decoded) counted against the same caps: false when over a picture's cap or the budget,
    /// which sets <see cref="OverBudget"/>.
    /// </summary>
    public bool Count(long length)
    {
        if (length > ImageFile.MaxFileBytes)
        {
            return false;
        }

        if (_total + length > budget)
        {
            OverBudget = true;
            return false;
        }

        _total += length;
        return true;
    }

    private string? Locate(string address)
    {
        string text = address.Trim();
        int cut = text.IndexOfAny(['?', '#']);
        if (cut >= 0)
        {
            text = text[..cut];
        }

        if (text.Length == 0)
        {
            return null;
        }

        string candidate;
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && !(text.Length > 1 && text[1] == ':'))
        {
            if (!uri.IsFile)
            {
                return null;
            }

            candidate = uri.LocalPath;
        }
        else
        {
            try
            {
                candidate = Path.Combine(folder, Uri.UnescapeDataString(text).Replace('/', Path.DirectorySeparatorChar));
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        return files.Resolve(candidate, forWrite: false, out string full) == FileOutcome.Ok && File.Exists(full) ? full : null;
    }

    private byte[]? Read(string full, long cap)
    {
        try
        {
            long length = new FileInfo(full).Length;
            if (length > cap)
            {
                return null;
            }

            if (_total + length > budget)
            {
                OverBudget = true;
                return null;
            }

            _total += length;
            return RegularFiles.ReadAllBytes(full);
        }
        catch (Exception ex) when (WorkingDirectory.IsFileFailure(ex))
        {
            return null;
        }
    }
}
