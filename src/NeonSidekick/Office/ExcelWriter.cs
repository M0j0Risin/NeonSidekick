using System.Xml;

namespace NeonSidekick.Office;

/// <summary>What a workbook write did: a new file (or one replaced whole), a sheet added, a sheet replaced.</summary>
public enum WorkbookWrite
{
    New,
    Added,
    ReplacedSheet,
}

/// <summary>
/// A workbook being written with one sheet in it (2026-10-29, the Office writing plan): a new one (<see cref="ExcelWriter"/>) or an existing
/// one gaining or replacing a sheet (<see cref="WorkbookAppender"/>). Fill <see cref="Sheet"/>, then <see cref="Finish"/>; disposed
/// unfinished, the package is left half-written for the caller's <c>PendingWrite</c> to throw away.
/// </summary>
public interface IWorkbookJob : IDisposable
{
    /// <summary>The sheet to fill: <see cref="ISheetRows.Begin"/>, then the rows.</summary>
    SheetWriter Sheet { get; }

    /// <summary>The sheet closed and the rest of the package written.</summary>
    SheetResult Finish();
}

/// <summary>
/// A new workbook of one sheet (2026-10-29, the Office writing plan): <c>xl/workbook.xml</c> (a <c>bookViews</c>, the sheet, the filter's
/// hidden <c>_xlnm._FilterDatabase</c> name as Excel writes it), its relationships, the styles (a normal and a bold font, number formats
/// <see cref="FirstFormatId"/>+ for <c>yyyy-mm-dd</c>, <c>yyyy-mm-dd hh:mm:ss</c>, <c>hh:mm:ss</c> and <c>[h]:mm:ss</c>, which
/// <see cref="NumberFormats"/> reads back as dates and times), the sheet, and the properties.
/// </summary>
public sealed class ExcelWriter : IWorkbookJob
{
    public const string WorkbookType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml";
    public const string StylesType = "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml";

    /// <summary>The first custom number format's id: 0–163 are Excel's own.</summary>
    public const int FirstFormatId = 164;

    /// <summary>The four number formats, in <see cref="SheetStyles"/>' order after the header: date, date-time, time, duration.</summary>
    public static readonly IReadOnlyList<string> FormatCodes = ["yyyy-mm-dd", "yyyy-mm-dd hh:mm:ss", "hh:mm:ss", "[h]:mm:ss"];

    /// <summary>Excel's built-in format 1, <c>0</c>: a whole number with every digit. Built in, so no <c>numFmt</c> declares it.</summary>
    public const int IntegerFormatId = 1;

    private const string Ns = SheetWriter.Ns;
    private const string SheetPart = "xl/worksheets/sheet1.xml";

    private readonly OfficePackageWriter _package;
    private readonly string _sheetName;
    private readonly string? _title;
    private readonly XmlWriter _sheetXml;

    /// <param name="output">Where the file goes (left open).</param>
    /// <param name="sheetName">The sheet's name, already judged by <see cref="SheetName"/>.</param>
    /// <param name="title">The document's title for its properties; null for none.</param>
    public ExcelWriter(Stream output, string sheetName, TimeProvider time, string? title = null)
    {
        _package = new OfficePackageWriter(output, time);
        _sheetName = sheetName ?? throw new ArgumentNullException(nameof(sheetName));
        _title = title;
        using (var w = _package.Part("xl/styles.xml", StylesType))
        {
            WriteStyles(w);
        }

        _sheetXml = _package.Part(SheetPart, SheetWriter.ContentType);
        Sheet = new SheetWriter(_sheetXml, new SheetStyles(1, 2, 3, 4, 5, 6), date1904: false, selected: true);
    }

    public SheetWriter Sheet { get; }

    public SheetResult Finish()
    {
        var result = Sheet.Finish();
        _sheetXml.Dispose();
        using (var w = _package.Part("xl/workbook.xml", WorkbookType))
        {
            w.WriteStartElement("workbook", Ns);
            w.WriteAttributeString("xmlns", "r", null, OfficePackageWriter.RNs);
            w.WriteStartElement("bookViews", Ns);
            w.WriteStartElement("workbookView", Ns);
            w.WriteEndElement();
            w.WriteEndElement();
            w.WriteStartElement("sheets", Ns);
            w.WriteStartElement("sheet", Ns);
            w.WriteAttributeString("name", _sheetName);
            w.WriteAttributeString("sheetId", "1");
            w.WriteAttributeString("id", OfficePackageWriter.RNs, "rId1");
            w.WriteEndElement();
            w.WriteEndElement();
            w.WriteStartElement("definedNames", Ns);
            FilterName(w, 0, _sheetName, result.FilterRange);
            w.WriteEndElement();
            w.WriteEndElement();
        }

        _package.Relate("xl/workbook.xml", "rId1", "worksheet", "worksheets/sheet1.xml");
        _package.Relate("xl/workbook.xml", "rId2", "styles", "styles.xml");
        _package.Relate("", _package.NextId(""), "officeDocument", "xl/workbook.xml");
        _package.Properties(_title);
        _package.Finish();
        return result;
    }

    public void Dispose()
    {
        _sheetXml.Dispose();
        _package.Dispose();
    }

    /// <summary>The filter's hidden name, as Excel writes it for a sheet's autofilter: scoped to the sheet at <paramref name="position"/> (hidden sheets count).</summary>
    public static void FilterName(XmlWriter w, int position, string sheet, string range)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.WriteStartElement("definedName", Ns);
        w.WriteAttributeString("name", "_xlnm._FilterDatabase");
        w.WriteAttributeString("localSheetId", position.ToString(System.Globalization.CultureInfo.InvariantCulture));
        w.WriteAttributeString("hidden", "1");
        w.WriteString("'" + sheet.Replace("'", "''", StringComparison.Ordinal) + "'!" + range);
        w.WriteEndElement();
    }

    /// <summary>One custom number format.</summary>
    public static void NumFmt(XmlWriter w, int id, string code)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.WriteStartElement("numFmt", Ns);
        w.WriteAttributeString("numFmtId", id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        w.WriteAttributeString("formatCode", code);
        w.WriteEndElement();
    }

    /// <summary>One cell format: a number format and a font over the default fill, border and cell style.</summary>
    public static void Xf(XmlWriter w, int numFmtId, int fontId)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.WriteStartElement("xf", Ns);
        w.WriteAttributeString("numFmtId", numFmtId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        w.WriteAttributeString("fontId", fontId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        w.WriteAttributeString("fillId", "0");
        w.WriteAttributeString("borderId", "0");
        w.WriteAttributeString("xfId", "0");
        if (numFmtId != 0)
        {
            w.WriteAttributeString("applyNumberFormat", "1");
        }

        if (fontId != 0)
        {
            w.WriteAttributeString("applyFont", "1");
        }

        w.WriteEndElement();
    }

    /// <summary>A Calibri 11 font, bold when asked.</summary>
    public static void Font(XmlWriter w, bool bold)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.WriteStartElement("font", Ns);
        if (bold)
        {
            w.WriteStartElement("b", Ns);
            w.WriteEndElement();
        }

        foreach (var (name, value) in new[] { ("sz", "11"), ("name", "Calibri"), ("family", "2") })
        {
            w.WriteStartElement(name, Ns);
            w.WriteAttributeString("val", value);
            w.WriteEndElement();
        }

        w.WriteEndElement();
    }

    /// <summary>The styles part: <c>cellXfs</c> 0 plain, 1 the bold header, 2–5 the four formats, 6 a whole number in full (built-in format 1, <c>0</c>).</summary>
    public static void WriteStyles(XmlWriter w)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.WriteStartElement("styleSheet", Ns);
        w.WriteStartElement("numFmts", Ns);
        w.WriteAttributeString("count", FormatCodes.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        for (int i = 0; i < FormatCodes.Count; i++)
        {
            NumFmt(w, FirstFormatId + i, FormatCodes[i]);
        }

        w.WriteEndElement();
        w.WriteStartElement("fonts", Ns);
        w.WriteAttributeString("count", "2");
        Font(w, bold: false);
        Font(w, bold: true);
        w.WriteEndElement();
        w.WriteStartElement("fills", Ns);
        w.WriteAttributeString("count", "2");
        foreach (string pattern in new[] { "none", "gray125" })
        {
            w.WriteStartElement("fill", Ns);
            w.WriteStartElement("patternFill", Ns);
            w.WriteAttributeString("patternType", pattern);
            w.WriteEndElement();
            w.WriteEndElement();
        }

        w.WriteEndElement();
        w.WriteStartElement("borders", Ns);
        w.WriteAttributeString("count", "1");
        w.WriteStartElement("border", Ns);
        foreach (string side in new[] { "left", "right", "top", "bottom", "diagonal" })
        {
            w.WriteStartElement(side, Ns);
            w.WriteEndElement();
        }

        w.WriteEndElement();
        w.WriteEndElement();
        w.WriteStartElement("cellStyleXfs", Ns);
        w.WriteAttributeString("count", "1");
        w.WriteStartElement("xf", Ns);
        w.WriteAttributeString("numFmtId", "0");
        w.WriteAttributeString("fontId", "0");
        w.WriteAttributeString("fillId", "0");
        w.WriteAttributeString("borderId", "0");
        w.WriteEndElement();
        w.WriteEndElement();
        w.WriteStartElement("cellXfs", Ns);
        w.WriteAttributeString("count", (3 + FormatCodes.Count).ToString(System.Globalization.CultureInfo.InvariantCulture));
        Xf(w, 0, 0);
        Xf(w, 0, 1);
        for (int i = 0; i < FormatCodes.Count; i++)
        {
            Xf(w, FirstFormatId + i, 0);
        }

        Xf(w, IntegerFormatId, 0);

        w.WriteEndElement();
        w.WriteStartElement("cellStyles", Ns);
        w.WriteAttributeString("count", "1");
        w.WriteStartElement("cellStyle", Ns);
        w.WriteAttributeString("name", "Normal");
        w.WriteAttributeString("xfId", "0");
        w.WriteAttributeString("builtinId", "0");
        w.WriteEndElement();
        w.WriteEndElement();
        w.WriteEndElement();
    }
}
