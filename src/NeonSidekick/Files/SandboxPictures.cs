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

    /// <summary>
    /// A Markdown picture as a <c>data:</c> URI for a page, or null. A <c>data:</c> address is decoded, held to the types a page shows and
    /// counted against the caps, then written again (2026-10-30, review F92: it passed through as it was, any type and any size, past the
    /// budget); one that won't decode is null.
    /// </summary>
    public string? Picture(string address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return OverBudget ? null : DataPicture(address);
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

    /// <summary>
    /// A <c>data:</c> address as <see cref="Picture"/> takes it: its media type one <see cref="Pdf.PdfHtml.IsImageMediaType"/> knows (its
    /// parameters cut), its body base64 or percent-encoded, counted by <see cref="Count"/>; null otherwise.
    /// </summary>
    private string? DataPicture(string address)
    {
        int comma = address.IndexOf(',', StringComparison.Ordinal);
        if (comma < 0)
        {
            return null;
        }

        string[] head = address["data:".Length..comma].Split(';');
        string type = head[0].Trim();
        if (!Pdf.PdfHtml.IsImageMediaType(type))
        {
            return null;
        }

        string body = address[(comma + 1)..];
        byte[] bytes;
        if (head.Skip(1).Any(p => p.Trim().Equals("base64", StringComparison.OrdinalIgnoreCase)))
        {
            body = body.Trim();
            if (body.Length / 4L * 3 > ImageFile.MaxFileBytes)
            {
                return null;
            }

            var buffer = new byte[body.Length / 4 * 3 + 3];
            if (!Convert.TryFromBase64String(body, buffer, out int written))
            {
                return null;
            }

            bytes = buffer[..written];
        }
        else
        {
            if (body.Length > ImageFile.MaxFileBytes * 3L)
            {
                return null;
            }

            bytes = System.Text.Encoding.UTF8.GetBytes(Uri.UnescapeDataString(body));
        }

        return Count(bytes.LongLength) ? Pdf.PdfHtml.DataUri(bytes, type.ToLowerInvariant()) : null;
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
