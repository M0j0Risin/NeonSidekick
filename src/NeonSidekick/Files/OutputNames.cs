using System.Globalization;

namespace NeonSidekick.Files;

/// <summary>
/// A converter's output name (2026-10-29, the Office writing plan; <c>PdfPlan.OutputFor</c>'s rule of 2026-10-03, lifted so <c>.pdf</c>,
/// <c>.docx</c> and <c>.xlsx</c> are named one way): a <c>to</c> naming a file is kept, its extension added when it has another ending
/// (<c>report.v2</c> → <c>report.v2.xlsx</c>); otherwise a name is made — the source's stem beside it (a source under <c>.neon/</c>, a plan
/// or an audit, at the top of the working folder, since <c>WriteGuard</c> refuses <c>.neon</c>), a web page's host and last segment, or a
/// title (else <paramref name="fallbackStem"/>) and the minute — inside the folder <c>to</c> names, beside the source, or at the top. Pure.
/// </summary>
public static class OutputNames
{
    /// <summary>The output's path relative to the working directory; <paramref name="isFolder"/> asks the sandbox.</summary>
    public static string For(string? to, string? sourceRelative, Uri? url, string? title, DateTimeOffset now, Func<string, bool> isFolder, string extension,
        string fallbackStem = "reply", string suffix = "")
    {
        ArgumentNullException.ThrowIfNull(isFolder);
        ArgumentNullException.ThrowIfNull(extension);
        string target = (to ?? "").Trim();
        if (!IntoFolder(target, isFolder))
        {
            return WithExtension(target, extension);
        }

        string name;
        string folder;
        if (sourceRelative is not null)
        {
            name = Path.GetFileNameWithoutExtension(sourceRelative) + suffix;
            string? parent = Path.GetDirectoryName(sourceRelative);
            folder = target.Length > 0 ? target : string.IsNullOrEmpty(parent) || UnderNeon(sourceRelative) ? "" : parent.Replace('\\', '/');
        }
        else
        {
            name = url is not null ? Pdf.PdfPlan.UrlName(url) : Stamped(title, now, fallbackStem);
            folder = target;
        }

        folder = folder is "." ? "" : folder.TrimEnd('/', '\\');
        string file = Llm.Tools.DownloadFileTool.Sanitise(name);
        if (file.Length == 0)
        {
            file = Stamped(null, now, fallbackStem);
        }

        return (folder.Length == 0 ? "" : folder + "/") + file + extension;
    }

    /// <summary>Whether <paramref name="to"/> names a folder to make a name in: blank, <c>.</c>, a trailing separator, or an existing folder.</summary>
    public static bool IntoFolder(string? to, Func<string, bool> isFolder)
    {
        ArgumentNullException.ThrowIfNull(isFolder);
        string target = (to ?? "").Trim();
        return target.Length == 0 || target is "." || target.EndsWith('/') || target.EndsWith('\\') || isFolder(target);
    }

    /// <summary><paramref name="target"/> ending in <paramref name="extension"/> (any case): kept when it does, the extension added when not.</summary>
    public static string WithExtension(string target, string extension)
    {
        ArgumentNullException.ThrowIfNull(target);
        return target.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ? target : target + extension;
    }

    /// <summary>A name for text with no file: the title (when not the reply's) or <paramref name="fallbackStem"/>, and the time to the minute.</summary>
    public static string Stamped(string? title, DateTimeOffset now, string fallbackStem = "reply")
    {
        string stem = string.IsNullOrWhiteSpace(title) || string.Equals(title.Trim(), Printing.PrintText.ReplyTitle, StringComparison.Ordinal)
            ? fallbackStem
            : Llm.Tools.DownloadFileTool.Sanitise(title.Trim().Replace(' ', '-'));
        return (stem.Length == 0 ? fallbackStem : stem) + "-" + now.ToString("yyyy-MM-dd-HHmm", CultureInfo.InvariantCulture);
    }

    /// <summary>Whether <paramref name="relative"/> lies under the working folder's <c>.neon</c> (any case, either separator).</summary>
    public static bool UnderNeon(string relative)
    {
        ArgumentNullException.ThrowIfNull(relative);
        string first = relative.Replace('\\', '/').TrimStart('/').Split('/')[0];
        return first.Equals(WriteGuard.NeonFolderName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The refusal for a <c>to</c> ending in another document type the app knows (an Office file, a PDF, a CSV or TSV) than
    /// <paramref name="extension"/>, so <c>out.xlsm</c> is never <c>out.xlsm.xlsx</c>; or naming Office's <c>~$</c> lock-file prefix. Null
    /// when the name is fine or a folder.
    /// </summary>
    public static string? Refusal(string? to, string extension, Func<string, bool> isFolder)
    {
        ArgumentNullException.ThrowIfNull(isFolder);
        string target = (to ?? "").Trim();
        if (IntoFolder(target, isFolder))
        {
            return null;
        }

        if (Path.GetFileName(target.TrimEnd('/', '\\')).StartsWith(OfficeFile.LockFilePrefix, StringComparison.Ordinal))
        {
            return Office.OfficeText.ReservedName(target);
        }

        string ending = Path.GetExtension(target);
        bool known = OfficeFile.IsAnyOfficePath(target) || PdfFile.IsPdfPath(target)
            || ending.Equals(".csv", StringComparison.OrdinalIgnoreCase) || ending.Equals(".tsv", StringComparison.OrdinalIgnoreCase);
        return known && !ending.Equals(extension, StringComparison.OrdinalIgnoreCase) ? Office.OfficeText.WrongTarget(target, extension) : null;
    }
}
