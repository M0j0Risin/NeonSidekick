using System.Text;
using NeonSidekick.Files;
using NeonSidekick.Pdf;

namespace NeonSidekick.Office;

/// <summary>What <c>read_office</c> asks of a file: a workbook's sheet, range and formulas; a presentation's slides and notes; where a long text goes on from.</summary>
public sealed record OfficeRequest(string? Sheet = null, string? Range = null, PdfPageRange? Slides = null, string SlidesSpec = "", bool Notes = false, bool Formulas = false, int FromChar = 0);

/// <summary>
/// The door to the three readers (2026-10-28, the Office plan), shared by <c>read_office</c> and <c>/read</c>: the refusals a name alone
/// decides (a lock file, a legacy or binary format, OpenDocument), then the file opened with <see cref="FileShare.ReadWrite"/> and
/// <see cref="FileShare.Delete"/> (a workbook open in Excel still reads), its first bytes judged (an OLE container is a password, encryption
/// or a label; anything but a zip isn't Office), the package's main part handed to <see cref="WordReader"/>, <see cref="ExcelReader"/> or
/// <see cref="SlidesReader"/>. Damage is one <see cref="OfficeText.Damaged"/> answer, never an exception out. No cache in v1: each call opens
/// the file again (parsing XML is cheap beside <c>read_pdf</c>'s headless browser); a cache would key on <c>PdfDocKey</c>'s rule, never the path.
/// </summary>
public static class OfficeReader
{
    /// <summary>The refusal <paramref name="path"/>'s name alone gives, or null when it names a file to open.</summary>
    public static string? RefuseByName(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (OfficeFile.IsLockFile(path))
        {
            string name = Path.GetFileName(path.Trim().TrimEnd('/', '\\'));
            string folder = path.Trim()[..^name.Length];
            return OfficeText.LockFile(path, folder + name[OfficeFile.LockFilePrefix.Length..]);
        }

        return OfficeFile.IsLegacyPath(path) ? OfficeText.Legacy(path)
            : OfficeFile.IsBinaryWorkbookPath(path) ? OfficeText.BinaryWorkbook(path)
            : OfficeFile.IsOpenDocumentPath(path) ? OfficeText.OpenDocument(path)
            : !OfficeFile.IsOfficePath(path) ? OfficeText.NotAnOfficePath(path)
            : null;
    }

    /// <summary><paramref name="path"/> in <paramref name="files"/> read as <paramref name="request"/> asks; the answer, or the sentence why not.</summary>
    public static string Read(WorkingDirectory files, string path, OfficeRequest request, CancellationToken cancellationToken, OfficeLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(request);
        limits ??= OfficeLimits.Default;
        if (Open(files, path, limits, out string display, out var refused) is not { } stream)
        {
            return refused!;
        }

        using (stream)
        {
            return Read(stream, display, OfficeFile.Kind(display) ?? OfficeKind.Word, request, limits, cancellationToken);
        }
    }

    /// <summary>
    /// The text <c>/read</c> speaks: a Word document's paragraphs or a presentation's slides (titles as sentences), no markers, cut at
    /// <see cref="WorkingDirectory.MaxReadChars"/> as a text file is (<c>Cut</c> then); a workbook refused. <c>Text</c> or <c>Error</c>.
    /// </summary>
    public static (string? Text, bool Cut, string? Error) ReadAloud(WorkingDirectory files, string path, CancellationToken cancellationToken, OfficeLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (OfficeFile.Kind(path) == OfficeKind.Excel)
        {
            return (null, false, OfficeText.NotReadAloud(path));
        }

        if (RefuseByName(path) is { } byName)
        {
            return (null, false, byName);
        }

        limits ??= OfficeLimits.Default;
        if (Open(files, path, limits, out string display, out var refused) is not { } stream)
        {
            return (null, false, refused);
        }

        using (stream)
        {
            try
            {
                using var package = Package(stream, display, limits, out string? main, out var kind, out string? error);
                if (error is not null)
                {
                    return (null, false, error);
                }

                string text = "";
                bool cut = false;
                if (kind == OfficeKind.Word)
                {
                    var read = new WordReader(package!, main!, WorkingDirectory.MaxReadChars + 1, aloud: true, cancellationToken).Read();
                    (text, cut) = (read.Text, read.More);
                }
                else if (kind == OfficeKind.Slides)
                {
                    var slides = new SlidesReader(package!, main!, cancellationToken);
                    var sb = new StringBuilder();
                    for (int n = 1; n <= slides.Count && sb.Length < WorkingDirectory.MaxReadChars; n++)
                    {
                        string spoken = SlidesReader.Aloud(slides.Read(n, notes: false, aloud: true));
                        if (spoken.Length > 0)
                        {
                            sb.Append(sb.Length > 0 ? "\n\n" : "").Append(spoken);
                        }
                    }

                    text = sb.ToString();
                    cut = sb.Length >= WorkingDirectory.MaxReadChars;
                }
                else
                {
                    return (null, false, OfficeText.NotReadAloud(display));
                }

                cut |= text.Length > WorkingDirectory.MaxReadChars;
                text = TextCut.Safe(text, WorkingDirectory.MaxReadChars).Trim();
                return text.Length == 0 ? (null, false, OfficeText.NothingToRead(display)) : (text, cut, null);
            }
            catch (OfficeDamagedException ex)
            {
                return (null, false, OfficeText.Damaged(display, ex.Part));
            }
            catch (OfficePartTooLargeException ex)
            {
                return (null, false, OfficeText.PartTooLarge(display, ex.Part));
            }
            catch (OfficeStreamLimitException ex)
            {
                return (null, false, OfficeText.PartTooLarge(display, ex.Part));
            }
        }
    }

    /// <summary>The file opened for reading, other programs' handles allowed; null with the refusal (outside the sandbox, missing, a folder, too large).</summary>
    private static FileStream? Open(WorkingDirectory files, string path, OfficeLimits limits, out string display, out string? refused)
    {
        display = path;
        refused = null;
        var outcome = files.Resolve(path, forWrite: false, out string full);
        if (outcome != FileOutcome.Ok)
        {
            refused = FileText.Error(outcome, path, "read");
            return null;
        }

        display = files.Relative(full);
        if (Directory.Exists(full))
        {
            refused = FileText.Error(FileOutcome.IsDirectory, files.Relative(full, isDirectory: true), "read");
            return null;
        }

        if (!File.Exists(full))
        {
            refused = FileText.Error(FileOutcome.Missing, display, "read");
            return null;
        }

        try
        {
            var stream = RegularFiles.OpenRead(full, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > limits.MaxFileBytes)
            {
                stream.Dispose();
                refused = OfficeText.TooBig(display, limits.MaxFileBytes);
                return null;
            }

            return stream;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            refused = FileText.Error(FileOutcome.Failed, display, "read", ex.Message);
            return null;
        }
    }

    /// <summary>The read of an open stream: the package judged, then the reader for its kind.</summary>
    public static string Read(Stream stream, string display, OfficeKind kind, OfficeRequest request, OfficeLimits limits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(limits);
        if (stream.Length > limits.MaxFileBytes)
        {
            return OfficeText.TooBig(display, limits.MaxFileBytes);
        }

        try
        {
            using var package = Package(stream, display, limits, out string? main, out var actual, out string? error);
            if (error is not null)
            {
                return error;
            }

            if (actual != kind)
            {
                return OfficeText.NotOffice(display);
            }

            return kind switch
            {
                OfficeKind.Word => Word(package!, main!, display, request, cancellationToken),
                OfficeKind.Excel => new ExcelReader(package!, main!, cancellationToken).Read(display, request.Sheet, request.Range, request.Formulas, WorkingDirectory.MaxReadChars),
                _ => Slides(package!, main!, display, request, cancellationToken),
            };
        }
        catch (OfficeDamagedException ex)
        {
            return OfficeText.Damaged(display, ex.Part);
        }
        catch (OfficePartTooLargeException ex)
        {
            return OfficeText.PartTooLarge(display, ex.Part);
        }
        catch (OfficeStreamLimitException ex)
        {
            return OfficeText.PartTooLarge(display, ex.Part);
        }
    }

    /// <summary>The zip opened and its main part found and judged by its root element; <paramref name="error"/> when it isn't an Office document.</summary>
    private static OfficePackage? Package(Stream stream, string display, OfficeLimits limits, out string? main, out OfficeKind kind, out string? error)
    {
        main = null;
        kind = OfficeKind.Word;
        error = null;
        Span<byte> head = stackalloc byte[8];
        int read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        stream.Position = 0;
        if (OfficeFile.LooksLikeCompoundFile(head[..read]))
        {
            error = OfficeText.Protected(display);
            return null;
        }

        if (!OfficeFile.LooksLikeZip(head[..read]))
        {
            error = OfficeText.NotOffice(display);
            return null;
        }

        var package = OfficePackage.Open(stream, limits);
        main = package.MainPart();
        if (main is null)
        {
            package.Dispose();
            error = OfficeText.NotOffice(display);
            return null;
        }

        string part = main;
        var x = new OfficeXml();
        var root = OfficePackage.Guard(part, () =>
        {
            using var reader = package.Streamed(part);
            reader.MoveToContent();
            return (Local: reader.LocalName, Ns: x.Key(reader.NamespaceURI));
        });

        OfficeKind? found = root switch
        {
            ("document", OfficeXml.W) => OfficeKind.Word,
            ("workbook", OfficeXml.S) => OfficeKind.Excel,
            ("presentation", OfficeXml.P) => OfficeKind.Slides,
            _ => null,
        };

        if (found is null)
        {
            package.Dispose();
            error = OfficeText.NotOffice(display);
            return null;
        }

        kind = found.Value;
        return package;
    }

    private static string Word(OfficePackage package, string main, string display, OfficeRequest request, CancellationToken cancellationToken)
    {
        int budget = WorkingDirectory.MaxReadChars;
        var read = new WordReader(package, main, request.FromChar + budget + 1, aloud: false, cancellationToken).Read();
        string text = read.Text;
        if (request.FromChar > 0 && request.FromChar >= text.Length)
        {
            return OfficeText.PastTheText(display, text.Length, request.FromChar);
        }

        int start = request.FromChar > 0 ? TextCut.SafeStart(text, Math.Min(request.FromChar, text.Length)) : 0;
        int length = TextCut.SafeLength(text.AsSpan(start), Math.Min(budget, text.Length - start));
        int end = start + length;
        bool more = end < text.Length || read.More;
        string header = OfficeText.WordHeader(display, start, end, more ? null : text.Length, more ? end : null);
        string body = text.Substring(start, length);
        return header + (body.Length > 0 ? "\n" + body : "") + (read.Note is { } note ? "\n" + note : "");
    }

    private static string Slides(OfficePackage package, string main, string display, OfficeRequest request, CancellationToken cancellationToken)
    {
        var reader = new SlidesReader(package, main, cancellationToken);
        int count = reader.Count;
        if (count == 0)
        {
            return OfficeText.NoSlides(display);
        }

        var asked = request.Slides?.Resolve(count) ?? [.. Enumerable.Range(1, count)];
        if (asked.Count == 0)
        {
            return OfficeText.SlidesPastTheEnd(display, count, request.SlidesSpec);
        }

        int budget = WorkingDirectory.MaxReadChars;
        var body = new StringBuilder();
        var shown = new List<int>();
        string? note = null;
        foreach (int number in asked)
        {
            string text;
            try
            {
                text = SlidesReader.Render(reader.Read(number, request.Notes, aloud: false));
            }
            catch (OfficeDamagedException ex) when (shown.Count > 0)
            {
                note = OfficeText.DamagedPast(ex.Part);
                break;
            }

            int skipped = 0;
            if (request.FromChar > 0)
            {
                if (request.FromChar >= text.Length)
                {
                    return OfficeText.PastTheText(display, text.Length, request.FromChar);
                }

                skipped = TextCut.SafeStart(text, request.FromChar);
                text = text[skipped..];
            }

            int room = budget - body.Length - 1;
            if (shown.Count > 0 && text.Length > room)
            {
                break;
            }

            body.Append(body.Length > 0 ? "\n\n" : "");
            if (text.Length > Math.Max(room, 0) && shown.Count == 0)
            {
                int cut = TextCut.SafeLength(text, Math.Min(Math.Max(room, 1000), text.Length));
                body.Append(text.AsSpan(0, cut)).Append('\n').Append(OfficeText.SlideCut(number, skipped + cut, skipped + text.Length));
            }
            else
            {
                body.Append(text);
            }

            shown.Add(number);
        }

        var rest = asked.Skip(shown.Count).ToList();
        string? next = rest.Count == 0 ? null : PdfText.Spec(rest);
        return OfficeText.SlidesHeader(display, shown, count, next) + "\n" + body + (note is null ? "" : "\n" + note);
    }
}
