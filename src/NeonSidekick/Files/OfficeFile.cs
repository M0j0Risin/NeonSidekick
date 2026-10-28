namespace NeonSidekick.Files;

/// <summary>Which of the three Office readers a file goes to (2026-10-28, the Office plan).</summary>
public enum OfficeKind
{
    Word,
    Excel,
    Slides,
}

/// <summary>
/// What makes a file an Office document (2026-10-28, the Office plan, the user's pick: <c>read_file</c> refused a <c>.docx</c> as not text and
/// nothing read one): the one list of the extensions <c>read_office</c> reads, which its hints, <c>read_file</c>'s and <c>/read</c> all ask,
/// so they can't drift — beside <see cref="PdfFile"/> and <see cref="ImageFile"/>, the way <see cref="FileText.NotText"/> asks them. The
/// formats it does not read (the legacy binary ones, <c>.xlsb</c>, OpenDocument) are named too, so each gets its own "save it as" hint. Pure.
/// </summary>
public static class OfficeFile
{
    /// <summary>Word's documents and templates, macro-enabled ones too (macros are never run: only the XML is read).</summary>
    public static readonly IReadOnlyList<string> WordExtensions = [".docx", ".docm", ".dotx", ".dotm"];

    /// <summary>Excel's workbooks and templates.</summary>
    public static readonly IReadOnlyList<string> ExcelExtensions = [".xlsx", ".xlsm", ".xltx", ".xltm"];

    /// <summary>PowerPoint's presentations, templates and shows.</summary>
    public static readonly IReadOnlyList<string> SlidesExtensions = [".pptx", ".pptm", ".potx", ".potm", ".ppsx", ".ppsm"];

    /// <summary>Every extension <c>read_office</c> reads.</summary>
    public static readonly IReadOnlyList<string> Extensions = [.. WordExtensions, .. ExcelExtensions, .. SlidesExtensions];

    /// <summary>The binary formats before 2007, refused with a "save it as" hint.</summary>
    public static readonly IReadOnlyList<string> LegacyExtensions = [".doc", ".xls", ".ppt"];

    /// <summary>Excel's binary workbook, refused with a "save it as .xlsx" hint.</summary>
    public const string BinaryWorkbookExtension = ".xlsb";

    /// <summary>OpenDocument's text, sheet and slides, not read yet.</summary>
    public static readonly IReadOnlyList<string> OpenDocumentExtensions = [".odt", ".ods", ".odp"];

    /// <summary>What starts the name of Office's lock file for an open document (<c>~$port.docx</c>).</summary>
    public const string LockFilePrefix = "~$";

    /// <summary>Whether <paramref name="path"/> names a file <c>read_office</c> reads, by its extension (any case).</summary>
    public static bool IsOfficePath(string path) => Kind(path) is not null;

    /// <summary>Which reader <paramref name="path"/>'s extension goes to; null for anything else.</summary>
    public static OfficeKind? Kind(string path)
    {
        string extension = Extension(path);
        return extension.Length == 0 ? null
            : Has(WordExtensions, extension) ? OfficeKind.Word
            : Has(ExcelExtensions, extension) ? OfficeKind.Excel
            : Has(SlidesExtensions, extension) ? OfficeKind.Slides
            : null;
    }

    /// <summary>Whether <paramref name="path"/> names a legacy binary Office file (<c>.doc</c>, <c>.xls</c>, <c>.ppt</c>).</summary>
    public static bool IsLegacyPath(string path) => Has(LegacyExtensions, Extension(path));

    /// <summary>Whether <paramref name="path"/> names an Excel binary workbook (<c>.xlsb</c>).</summary>
    public static bool IsBinaryWorkbookPath(string path) => Extension(path).Equals(BinaryWorkbookExtension, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="path"/> names an OpenDocument file.</summary>
    public static bool IsOpenDocumentPath(string path) => Has(OpenDocumentExtensions, Extension(path));

    /// <summary>Whether <paramref name="path"/> is any office-suite document, read or refused with a hint: what <c>read_file</c> points elsewhere.</summary>
    public static bool IsAnyOfficePath(string path) => IsOfficePath(path) || IsLegacyPath(path) || IsBinaryWorkbookPath(path) || IsOpenDocumentPath(path);

    /// <summary>Whether <c>/read</c> reads <paramref name="path"/> aloud: a Word document or a presentation (a sheet is no reading).</summary>
    public static bool IsReadAloudPath(string path) => Kind(path) is OfficeKind.Word or OfficeKind.Slides;

    /// <summary>Whether <paramref name="path"/> is Office's lock file for an open document: <c>~$</c> then an Office extension.</summary>
    public static bool IsLockFile(string path) =>
        IsOfficePath(path) && Path.GetFileName(path.Trim().TrimEnd('/', '\\')).StartsWith(LockFilePrefix, StringComparison.Ordinal);

    /// <summary>The first bytes of an OLE compound file: a legacy document, or an Office file locked with a password or a label.</summary>
    public static bool LooksLikeCompoundFile(ReadOnlySpan<byte> head) => head.Length >= 4 && head[0] == 0xD0 && head[1] == 0xCF && head[2] == 0x11 && head[3] == 0xE0;

    /// <summary>The first bytes of a zip: what every Office file this reads is.</summary>
    public static bool LooksLikeZip(ReadOnlySpan<byte> head) => head.Length >= 4 && head[0] == (byte)'P' && head[1] == (byte)'K' && head[2] == 3 && head[3] == 4;

    private static string Extension(string path) => string.IsNullOrWhiteSpace(path) ? "" : Path.GetExtension(path.Trim().TrimEnd('/', '\\'));

    private static bool Has(IReadOnlyList<string> list, string extension) => list.Any(e => e.Equals(extension, StringComparison.OrdinalIgnoreCase));
}
