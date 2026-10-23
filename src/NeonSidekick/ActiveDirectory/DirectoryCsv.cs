using System.Globalization;
using System.Text;
using NeonSidekick.Files;
using NeonSidekick.Images;
using NeonSidekick.Llm.Tools;

namespace NeonSidekick.ActiveDirectory;

/// <summary>
/// Where a <c>to</c> save may write, asked per call (2026-10-15, the AD gaps plan, §1b): the working directory, or why not (the model's tools need
/// *File tools* and no plan mode; a redacted twin has no root at all).
/// </summary>
public readonly record struct DirectorySaveRoot(WorkingDirectory? Root, string? Refusal)
{
    public static DirectorySaveRoot Refused(string why) => new(null, why);
}

/// <summary>A <c>to</c> checked before anything is read: the root, the path inside it, and whether the name was the caller's or is made up.</summary>
public sealed record DirectorySaveTarget(WorkingDirectory Root, string Relative, bool Named);

/// <summary>
/// The <c>to</c> save (2026-10-15, the AD gaps plan, §1b, the user's choice for lists too long for an answer): an answer's list tables as one CSV —
/// a group's members, a find's results, an OU's contents, never a card's link lists — the card's columns in its order and <c>dn</c> last; RFC
/// 4180 quoting and CRLF line ends; a cell starting <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>, a tab or CR guarded by a leading <c>'</c> (the values
/// are a stranger's text); UTF-8 with a BOM so Excel reads it; dates as <c>yyyy-MM-dd HH:mm</c> in the PC's zone, named in the column's header,
/// and a date column holds a date or nothing, so it sorts as dates. Two list tables are one sheet with a <c>section</c> column. The write goes
/// through <see cref="WorkingDirectory.WriteBytes"/> (the sandbox and the database guard hold), never over a file. Pure but for the write.
/// </summary>
public static class DirectoryCsv
{
    public const string Extension = ".csv";

    /// <summary>The longest made-up name, before its extension.</summary>
    public const int MaxNameChars = 60;

    /// <summary>The CSV's text, or null when the answer has no list rows.</summary>
    public static string? Text(DirectoryAnswer answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        var tables = answer.Tables.Where(t => t.IsList).ToList();
        if (tables.Sum(t => t.Rows.Count) == 0)
        {
            return null;
        }

        var columns = new List<DirectoryColumn>();
        foreach (var column in tables.SelectMany(t => t.Columns))
        {
            if (!columns.Any(c => c.Name == column.Name))
            {
                columns.Add(column);
            }
        }

        bool sections = tables.Count > 1;
        var csv = new StringBuilder();
        // The zone by name; each cell carries its own date's offset (K5).
        var header = columns.Select(c => c.Date ? c.Name + " (" + ZoneName(answer.Zone) + ")" : c.Name).ToList();
        if (sections)
        {
            header.Add(DirectoryText.ColSection);
        }

        header.Add(DirectoryText.ColDn);
        Append(csv, header);
        for (int t = 0; t < tables.Count; t++)
        {
            var table = tables[t];
            foreach (var row in table.Rows)
            {
                var cells = new List<string>(columns.Count + 2);
                foreach (var column in columns)
                {
                    int at = IndexOf(table.Columns, column.Name);
                    var cell = (at >= 0 && at < row.Cells.Count ? row.Cells[at] : null) ?? DirectoryCell.None;
                    cells.Add(cell.When is { } when ? DirectoryRender.Date(when, answer.Zone) : column.Date ? "" : cell.Text ?? "");
                }

                if (sections)
                {
                    cells.Add(t == 0 ? "" : table.Section ?? table.Title ?? "");
                }

                cells.Add(row.Dn ?? "");
                Append(csv, cells);
            }
        }

        return csv.ToString();
    }

    /// <summary>A date column's zone in the header: <c>UTC</c>, or the zone's id (2026-10-23, K5: today's offset was the label).</summary>
    public static string ZoneName(TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return zone == TimeZoneInfo.Utc || zone.Id is "UTC" or "Etc/UTC" ? "UTC" : zone.Id;
    }

    /// <summary>One cell as RFC 4180 writes it, scrubbed and guarded against a spreadsheet reading it as a formula.</summary>
    public static string Cell(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        string text = UntrustedText.Scrub(value);
        if (text.Length > 0 && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            text = "'" + text;
        }

        return text.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : text;
    }

    /// <summary>
    /// The made-up file name for a folder <c>to</c>: <c>ad-</c> and the question's words, lower-cased, a character a file name cannot hold (and a
    /// space) as <c>-</c>, cut to <see cref="MaxNameChars"/>, <c>.csv</c> (<c>ad-group-staff.csv</c>). Pure.
    /// </summary>
    public static string NameFor(string question)
    {
        ArgumentNullException.ThrowIfNull(question);
        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var name = new StringBuilder();
        foreach (char c in ("ad-" + question.Trim()).ToLowerInvariant())
        {
            name.Append(char.IsWhiteSpace(c) || invalid.Contains(c) || char.IsControl(c) ? '-' : c);
        }

        string text = name.ToString();
        while (text.Contains("--", StringComparison.Ordinal))
        {
            text = text.Replace("--", "-", StringComparison.Ordinal);
        }

        text = text.Trim('-', '.');
        return (text.Length > MaxNameChars ? text[..MaxNameChars].TrimEnd('-', '.') : text) + Extension;
    }

    /// <summary>
    /// A <c>to</c> checked before anything is read (2026-10-15, the AD gaps plan): a <c>.csv</c> path inside the working directory that names no
    /// file yet, or an existing folder (the file named from <paramref name="question"/> inside it, numbered at the write when taken); the
    /// refusal otherwise.
    /// </summary>
    public static (DirectorySaveTarget? Target, string? Error) Check(WorkingDirectory root, string to, string question)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(to);
        string path = to.Trim();
        if (path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
        {
            var outcome = root.Resolve(path, forWrite: true, out string full);
            if (outcome != FileOutcome.Ok)
            {
                return (null, FileText.Error(outcome, path, "write"));
            }

            return File.Exists(full) ? (null, FileText.Error(FileOutcome.Exists, root.Relative(full), "write") + DirectoryText.NameAnother) : (new DirectorySaveTarget(root, path, true), null);
        }

        if (root.IsExistingDirectory(path))
        {
            string folder = path.Replace('\\', '/').TrimEnd('/');
            string relative = folder is "" or "." ? NameFor(question) : folder + "/" + NameFor(question);
            return (new DirectorySaveTarget(root, relative, false), null);
        }

        return (null, DirectoryText.ToNeedsCsv(path));
    }

    /// <summary>
    /// The answer saved at <paramref name="target"/>: its line for the answer (<see cref="DirectoryText.Saved"/>), or why nothing was written (no rows,
    /// a refusal). A made-up name takes <c>-2</c>, <c>-3</c>… when taken (<c>pdf_edit</c>'s loop, up to 100), so nothing is written over.
    /// </summary>
    public static string Save(DirectorySaveTarget target, DirectoryAnswer answer, bool stopped)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(answer);
        if (Text(answer) is not { } text)
        {
            return DirectoryText.NothingSaved;
        }

        byte[] bytes = [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text)];
        WriteResult written;
        int n = 1;
        do
        {
            written = target.Root.WriteBytes(target.Named ? target.Relative : ImageOutput.Numbered(target.Relative, n), bytes, overwrite: false);
        }
        while (!target.Named && written.Outcome == FileOutcome.Exists && ++n < 100);
        if (written.Outcome != FileOutcome.Ok)
        {
            return FileText.Error(written.Outcome, written.Relative, "write", written.Detail);
        }

        int rows = answer.Tables.Where(t => t.IsList).Sum(t => t.Rows.Count);
        Diagnostics.DiagnosticLog.Debug(DirectoryText.Category, $"saved {rows.ToString(CultureInfo.InvariantCulture)} rows to {written.Relative}");
        return DirectoryText.Saved(rows, written.Relative, stopped);
    }

    private static int IndexOf(IReadOnlyList<DirectoryColumn> columns, string name)
    {
        for (int i = 0; i < columns.Count; i++)
        {
            if (columns[i].Name == name)
            {
                return i;
            }
        }

        return -1;
    }

    private static void Append(StringBuilder csv, IEnumerable<string> cells)
    {
        csv.Append(string.Join(',', cells.Select(Cell))).Append("\r\n");
    }
}
