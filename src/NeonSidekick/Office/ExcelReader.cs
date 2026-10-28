using System.Globalization;
using System.Text;
using System.Xml;
using NeonSidekick.Files;
using NeonSidekick.Sql;

namespace NeonSidekick.Office;

/// <summary>
/// An Excel workbook (2026-10-28, the Office plan). With no sheet: every sheet's name, used range and hidden mark (<c>hidden</c>, and
/// <c>veryHidden</c> — one Excel's own Unhide can't show — as very hidden; chart sheets listed, not read), the defined names with their
/// references (not resolved in v1: the model passes the reference), then the first visible sheet's first rows. With a sheet and a range:
/// a Markdown table with row numbers and column letters, sparse rows kept in place, a cell made safe by <see cref="SqlText.TableCell"/> and
/// cut past <see cref="MaxCellChars"/> (a one-cell range gives it whole), at most <see cref="MaxColumnsShown"/> columns; a cut names the next
/// range, always with its sheet. Hidden rows and columns are read and named on one line above the table. A worksheet is streamed and stops
/// once the asked rows are passed; a cell's value is shown as <see cref="NumberFormats"/> says, a formula's cached value (the formula when
/// none is cached), <c>formulas: true</c> adding it. Notes, comments, charts, pictures and pivot caches aren't read in v1.
/// </summary>
public sealed class ExcelReader
{
    /// <summary>A table cell's longest text; a one-cell range gives the whole.</summary>
    public const int MaxCellChars = 500;

    /// <summary>The most columns one table shows.</summary>
    public const int MaxColumnsShown = 40;

    /// <summary>The most rows the overview shows of the first sheet.</summary>
    public const int OverviewRows = 20;

    /// <summary>The most defined names the overview lists.</summary>
    public const int MaxNamesListed = 50;

    private const string WorkbookRels = "xl/_rels/workbook.xml.rels";

    private readonly OfficePackage _package;
    private readonly string _main;
    private readonly CancellationToken _cancellation;
    private readonly OfficeXml _x = new();
    private readonly List<Sheet> _sheets = [];
    private readonly List<(string Name, string Reference, int? Scope)> _names = [];
    private readonly Dictionary<string, string> _sharedFirst = new(StringComparer.Ordinal);
    private bool _date1904;
    private List<string>? _strings;
    private string? _stringsPart;
    private List<CellFormat>? _formats;
    private string? _stylesPart;

    public ExcelReader(OfficePackage package, string main, CancellationToken cancellationToken)
    {
        _package = package ?? throw new ArgumentNullException(nameof(package));
        _main = main ?? throw new ArgumentNullException(nameof(main));
        _cancellation = cancellationToken;
    }

    private sealed record Sheet(int Number, string Name, string State, string? Part, bool IsWorksheet)
    {
        public bool Visible => State is not ("hidden" or "veryHidden");
    }

    /// <summary>The answer for <c>read_office</c>: the overview with no <paramref name="sheet"/> or <paramref name="range"/>, else the range's table.</summary>
    public string Read(string display, string? sheet, string? range, bool formulas, int budget)
    {
        LoadWorkbook();
        string? rangeSheet = null;
        CellRange? asked = null;
        string rangeText = "";
        if (range is { Length: > 0 })
        {
            switch (CellReference.TryParseRange(range, out rangeSheet, out asked))
            {
                case RangeProblem.Bad:
                    return OfficeText.BadRange(range);
                case RangeProblem.PastTheEdge:
                    return OfficeText.RangePastTheEdge(range);
                case RangeProblem.Name:
                    return OfficeText.NameAsRange(range.Trim());
            }

            rangeText = range.Contains('!', StringComparison.Ordinal) ? range[(range.LastIndexOf('!') + 1)..].Replace("$", "", StringComparison.Ordinal).Trim() : range.Replace("$", "", StringComparison.Ordinal).Trim();
        }

        if (sheet is null && rangeSheet is null)
        {
            return asked is null ? Overview(display, formulas, budget) : ReadRange(display, _sheets.FirstOrDefault(s => s.IsWorksheet && s.Visible) ?? _sheets.FirstOrDefault(), asked, rangeText, formulas, budget);
        }

        Sheet? chosen = sheet is null ? null : Find(sheet);
        if (sheet is not null && chosen is null)
        {
            return OfficeText.NoSuchSheet(display, sheet, _sheets.Select(s => s.Name));
        }

        if (rangeSheet is not null)
        {
            var named = _sheets.FirstOrDefault(s => s.Name.Equals(rangeSheet, StringComparison.OrdinalIgnoreCase));
            if (named is null)
            {
                return OfficeText.NoSuchSheet(display, rangeSheet, _sheets.Select(s => s.Name));
            }

            if (chosen is not null && chosen != named)
            {
                return OfficeText.SheetsDisagree(sheet!, rangeSheet);
            }

            chosen = named;
        }

        return ReadRange(display, chosen, asked, rangeText, formulas, budget);
    }

    private Sheet? Find(string sheet)
    {
        string name = sheet.Trim();
        return _sheets.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? (int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out int number) && number >= 1 && number <= _sheets.Count ? _sheets[number - 1] : null);
    }

    private string Overview(string display, bool formulas, int budget)
    {
        var sb = new StringBuilder();
        sb.Append(OfficeText.SheetsHeading);
        var used = new Dictionary<Sheet, CellRange?>();
        foreach (var sheet in _sheets)
        {
            _cancellation.ThrowIfCancellationRequested();
            used[sheet] = sheet.IsWorksheet ? UsedRange(sheet) : null;
            sb.Append('\n').Append(OfficeText.SheetLine(sheet.Number, sheet.Name, used[sheet]?.ToString(), sheet.State, !sheet.IsWorksheet));
        }

        var names = _names.Where(n => n.Reference.Length > 0).ToList();
        if (names.Count > 0)
        {
            sb.Append("\n\n").Append(OfficeText.NamesHeading);
            foreach (var (name, reference, scope) in names.Take(MaxNamesListed))
            {
                sb.Append("\n- ").Append(name).Append(": ").Append(reference);
                if (scope is { } index && index >= 0 && index < _sheets.Count)
                {
                    sb.Append(" (in ").Append(_sheets[index].Name).Append(')');
                }
            }

            if (names.Count > MaxNamesListed)
            {
                sb.Append("\n(and ").Append((names.Count - MaxNamesListed).ToString(CultureInfo.InvariantCulture)).Append(" more)");
            }
        }

        var first = _sheets.FirstOrDefault(s => s.IsWorksheet && s.Visible && used[s] is not null);
        if (first is null)
        {
            sb.Append("\n\n").Append(OfficeText.NoVisibleSheet);
            return OfficeText.WorkbookHeader(display, _sheets.Count, null, null, null) + "\n" + sb;
        }

        var range = used[first]!;
        var table = Table(first, range, formulas, Math.Max(budget - sb.Length, 2_000), OverviewRows, out var shown, out var next);
        if (shown is null)
        {
            sb.Append("\n\n").Append(OfficeText.FirstRows(first.Name)).Append('\n').Append(OfficeText.NoValues(display, first.Name, range.ToString(), range.ToString()));
            return OfficeText.WorkbookHeader(display, _sheets.Count, null, null, null) + "\n" + sb;
        }

        sb.Append("\n\n").Append(OfficeText.FirstRows(first.Name)).Append('\n').Append(table);
        return OfficeText.WorkbookHeader(display, _sheets.Count, CellReference.WithSheet(first.Name, shown), range.ToString(), next is null ? null : CellReference.WithSheet(first.Name, next)) + "\n" + sb;
    }

    private string ReadRange(string display, Sheet? sheet, CellRange? asked, string rangeText, bool formulas, int budget)
    {
        if (sheet is null)
        {
            return OfficeText.NoVisibleSheet;
        }

        if (!sheet.IsWorksheet)
        {
            return OfficeText.ChartSheet(sheet.Name);
        }

        string quoted = CellReference.QuoteSheet(sheet.Name);
        var used = UsedRange(sheet);
        var bound = used is null ? null : asked is null ? used : asked.Intersect(used);
        if (bound is null)
        {
            return OfficeText.NoValues(display, quoted, asked is null ? "it" : rangeText, used?.ToString());
        }

        if (asked is { IsOneCell: true })
        {
            var rows = Cells(sheet, bound, formulas, int.MaxValue, 1, whole: true, out _, out _, out _, out string? oneNote);
            string value = rows.Count > 0 && rows[0].Cells.TryGetValue(bound.FirstColumn, out string? v) ? v : "";
            value = value.Length > WorkingDirectory.MaxReadChars ? TextCut.Safe(value, WorkingDirectory.MaxReadChars) + "…" : value;
            return OfficeText.RangeHeader(display, quoted, bound.ToString(), used!.ToString(), null) + "\n" + OfficeText.OneCell(bound.ToString(), value) + (oneNote is null ? "" : "\n" + oneNote);
        }

        string table = Table(sheet, bound, formulas, budget, int.MaxValue, out var shown, out var next);
        if (shown is null)
        {
            return table.Length > 0 ? table : OfficeText.NoValues(display, quoted, asked is null ? "it" : rangeText, used!.ToString());
        }

        return OfficeText.RangeHeader(display, quoted, shown.ToString(), used!.ToString(), next is null ? null : CellReference.WithSheet(sheet.Name, next)) + "\n" + table;
    }

    /// <summary>The table of <paramref name="bound"/>'s values: the hidden line, the rows. <paramref name="shown"/> null when it holds none.</summary>
    private string Table(Sheet sheet, CellRange bound, bool formulas, int budget, int maxRows, out CellRange? shown, out CellRange? next)
    {
        shown = null;
        next = null;
        int lastColumn = Math.Min(bound.LastColumn, bound.FirstColumn + MaxColumnsShown - 1);
        var view = bound with { LastColumn = lastColumn };
        var rows = Cells(sheet, view, formulas, budget, maxRows, whole: false, out var hiddenColumns, out var hiddenRows, out int? nextRow, out string? note);
        if (rows.Count == 0)
        {
            return note ?? "";
        }

        int lastRow = nextRow is { } cut ? cut - 1 : bound.LastRow;
        shown = new CellRange(bound.FirstColumn, bound.FirstRow, lastColumn, lastRow);
        if (nextRow is { } nr)
        {
            next = new CellRange(bound.FirstColumn, nr, bound.LastColumn, bound.LastRow);
        }
        else if (bound.LastColumn > lastColumn)
        {
            next = new CellRange(lastColumn + 1, bound.FirstRow, bound.LastColumn, bound.LastRow);
        }

        var sb = new StringBuilder();
        var hiddenParts = new List<string>();
        var rowsHidden = hiddenRows.Where(r => r <= lastRow).ToList();
        if (rowsHidden.Count > 0)
        {
            hiddenParts.Add((rowsHidden.Count == 1 ? "row " : "rows ") + CellReference.Runs(rowsHidden, n => n.ToString(CultureInfo.InvariantCulture)));
        }

        var columnsHidden = hiddenColumns.Where(c => c >= bound.FirstColumn && c <= lastColumn).Order().ToList();
        if (columnsHidden.Count > 0)
        {
            hiddenParts.Add((columnsHidden.Count == 1 ? "column " : "columns ") + CellReference.Runs(columnsHidden, CellReference.Column));
        }

        if (hiddenParts.Count > 0)
        {
            sb.Append(OfficeText.HiddenLine(string.Join(", ", hiddenParts))).Append('\n');
        }

        sb.Append("| |");
        for (int c = bound.FirstColumn; c <= lastColumn; c++)
        {
            sb.Append(' ').Append(CellReference.Column(c)).Append(" |");
        }

        sb.Append("\n| --- |").Append(string.Concat(Enumerable.Repeat(" --- |", lastColumn - bound.FirstColumn + 1)));
        foreach (var (row, cells) in rows)
        {
            sb.Append("\n| ").Append(row.ToString(CultureInfo.InvariantCulture)).Append(" |");
            for (int c = bound.FirstColumn; c <= lastColumn; c++)
            {
                string value = cells.TryGetValue(c, out string? v) ? v : "";
                sb.Append(' ').Append(SqlText.TableCell(value.Length <= MaxCellChars ? value : TextCut.Safe(value, MaxCellChars) + "…")).Append(" |");
            }
        }

        if (note is not null)
        {
            sb.Append('\n').Append(note);
        }

        return sb.ToString();
    }

    private void LoadWorkbook()
    {
        OfficePackage.Guard(_main, () =>
        {
            using var reader = _package.Whole(_main);
            reader.MoveToContent();
            OfficeXml.Children(reader, () =>
            {
                if (_x.Is(reader, OfficeXml.S, "workbookPr"))
                {
                    _date1904 = OfficeXml.On(_x.Attr(reader, "date1904"), false);
                    OfficeXml.Skip(reader);
                }
                else if (_x.Is(reader, OfficeXml.S, "sheets"))
                {
                    OfficeXml.Children(reader, () =>
                    {
                        if (_x.Is(reader, OfficeXml.S, "sheet"))
                        {
                            string name = OfficeXml.Unescape(_x.Attr(reader, "name") ?? "");
                            string state = _x.Attr(reader, "state") ?? "visible";
                            string? id = _x.Attr(reader, "id", OfficeXml.R);
                            var rel = id is null ? null : _package.Rel(_main, id);
                            _sheets.Add(new Sheet(_sheets.Count + 1, name, state, rel is { External: false } ? rel.Target : null, rel is null || rel.Is("worksheet")));
                        }

                        OfficeXml.Skip(reader);
                    });
                }
                else if (_x.Is(reader, OfficeXml.S, "definedNames"))
                {
                    OfficeXml.Children(reader, () =>
                    {
                        if (_x.Is(reader, OfficeXml.S, "definedName") && !OfficeXml.On(_x.Attr(reader, "hidden"), false))
                        {
                            string name = _x.Attr(reader, "name") ?? "";
                            int? scope = int.TryParse(_x.Attr(reader, "localSheetId"), NumberStyles.None, CultureInfo.InvariantCulture, out int s) ? s : null;
                            _names.Add((name, OfficeXml.Text(reader).Trim(), scope));
                            return;
                        }

                        OfficeXml.Skip(reader);
                    });
                }
                else
                {
                    OfficeXml.Skip(reader);
                }
            });
            return 0;
        });
    }

    /// <summary>Thrown to stop a sheet's stream once what was asked is read.</summary>
    private sealed class StopSheet : Exception
    {
    }

    /// <summary>The sheet's worksheet streamed to <paramref name="read"/> from its root's children; <see cref="StopSheet"/> ends it early.</summary>
    private void Stream(Sheet sheet, Action<XmlReader> read)
    {
        string part = sheet.Part ?? throw new OfficeDamagedException(WorkbookRels);
        try
        {
            OfficePackage.Guard(part, () =>
            {
                using var reader = _package.Streamed(part);
                reader.MoveToContent();
                read(reader);
                return 0;
            });
        }
        catch (StopSheet)
        {
            // What was asked is read.
        }
    }

    /// <summary>The sheet's used range: its <c>&lt;dimension&gt;</c>, else the cells it holds; null for none.</summary>
    private CellRange? UsedRange(Sheet sheet)
    {
        CellRange? dimension = null;
        bool found = false;
        Stream(sheet, reader => OfficeXml.Children(reader, () =>
        {
            if (_x.Is(reader, OfficeXml.S, "dimension"))
            {
                dimension = CellReference.ParseDimension(_x.Attr(reader, "ref"));
                found = dimension is not null;
                throw new StopSheet();
            }

            if (_x.Is(reader, OfficeXml.S, "sheetData"))
            {
                throw new StopSheet();
            }

            OfficeXml.Skip(reader);
        }));

        if (found)
        {
            return dimension;
        }

        int c1 = int.MaxValue, r1 = int.MaxValue, c2 = 0, r2 = 0;
        Stream(sheet, reader => OfficeXml.Children(reader, () =>
        {
            if (!_x.Is(reader, OfficeXml.S, "sheetData"))
            {
                OfficeXml.Skip(reader);
                return;
            }

            int previousRow = 0;
            OfficeXml.Children(reader, () =>
            {
                _cancellation.ThrowIfCancellationRequested();
                if (!_x.Is(reader, OfficeXml.S, "row"))
                {
                    OfficeXml.Skip(reader);
                    return;
                }

                int row = RowNumber(reader, previousRow);
                previousRow = row;
                int previousColumn = 0;
                OfficeXml.Children(reader, () =>
                {
                    if (_x.Is(reader, OfficeXml.S, "c"))
                    {
                        int column = ColumnNumber(reader, previousColumn);
                        previousColumn = column;
                        c1 = Math.Min(c1, column);
                        c2 = Math.Max(c2, column);
                        r1 = Math.Min(r1, row);
                        r2 = Math.Max(r2, row);
                    }

                    OfficeXml.Skip(reader);
                });
            });
            throw new StopSheet();
        }));

        return c2 == 0 ? null : new CellRange(c1, r1, Math.Min(c2, CellReference.MaxColumn), Math.Min(r2, CellReference.MaxRow));
    }

    private int RowNumber(XmlReader reader, int previous) =>
        int.TryParse(_x.Attr(reader, "r"), NumberStyles.None, CultureInfo.InvariantCulture, out int r) && r >= 1 ? r : previous + 1;

    private int ColumnNumber(XmlReader reader, int previous) =>
        CellReference.TryParseCell(_x.Attr(reader, "r"), out int column, out _) ? column : previous + 1;

    /// <summary>
    /// The values in <paramref name="bound"/>, row by row until <paramref name="budget"/> characters or <paramref name="maxRows"/> rows
    /// (<paramref name="nextRow"/> the first left out); the hidden columns and rows met on the way. Damage after some rows, or the stream's
    /// bound, ends it with <paramref name="note"/>.
    /// </summary>
    private List<(int Row, Dictionary<int, string> Cells)> Cells(Sheet sheet, CellRange bound, bool formulas, int budget, int maxRows, bool whole,
        out HashSet<int> hiddenColumns, out List<int> hiddenRows, out int? nextRow, out string? note)
    {
        var rows = new List<(int Row, Dictionary<int, string> Cells)>();
        var columnsHidden = new HashSet<int>();
        var rowsHidden = new List<int>();
        int? cutAt = null;
        int chars = 0;
        note = null;
        try
        {
            Stream(sheet, reader => OfficeXml.Children(reader, () =>
            {
                if (_x.Is(reader, OfficeXml.S, "cols"))
                {
                    OfficeXml.Children(reader, () =>
                    {
                        if (_x.Is(reader, OfficeXml.S, "col") && OfficeXml.On(_x.Attr(reader, "hidden"), false)
                            && int.TryParse(_x.Attr(reader, "min"), NumberStyles.None, CultureInfo.InvariantCulture, out int min)
                            && int.TryParse(_x.Attr(reader, "max"), NumberStyles.None, CultureInfo.InvariantCulture, out int max))
                        {
                            for (int c = Math.Max(min, bound.FirstColumn); c <= Math.Min(max, bound.LastColumn); c++)
                            {
                                columnsHidden.Add(c);
                            }
                        }

                        OfficeXml.Skip(reader);
                    });
                    return;
                }

                if (!_x.Is(reader, OfficeXml.S, "sheetData"))
                {
                    OfficeXml.Skip(reader);
                    return;
                }

                int previousRow = 0;
                OfficeXml.Children(reader, () =>
                {
                    _cancellation.ThrowIfCancellationRequested();
                    if (!_x.Is(reader, OfficeXml.S, "row"))
                    {
                        OfficeXml.Skip(reader);
                        return;
                    }

                    int row = RowNumber(reader, previousRow);
                    previousRow = row;
                    bool hidden = OfficeXml.On(_x.Attr(reader, "hidden"), false);
                    if (row > bound.LastRow)
                    {
                        throw new StopSheet();
                    }

                    var cells = Row(reader, row, bound, formulas, inBound: row >= bound.FirstRow);
                    if (row < bound.FirstRow || cells.Count == 0)
                    {
                        if (hidden && row >= bound.FirstRow)
                        {
                            rowsHidden.Add(row);
                        }

                        return;
                    }

                    int size = 8 + cells.Values.Sum(v => Math.Min(v.Length, whole ? v.Length : MaxCellChars) + 4);
                    if (rows.Count > 0 && (chars + size > budget || rows.Count >= maxRows))
                    {
                        cutAt = row;
                        throw new StopSheet();
                    }

                    chars += size;
                    rows.Add((row, cells));
                    if (hidden)
                    {
                        rowsHidden.Add(row);
                    }
                });
                throw new StopSheet();
            }));
        }
        catch (OfficeDamagedException ex) when (rows.Count > 0)
        {
            note = OfficeText.DamagedPast(ex.Part);
        }
        catch (OfficeStreamLimitException ex)
        {
            note = OfficeText.RestTooLarge(ex.Part);
        }

        hiddenColumns = columnsHidden;
        hiddenRows = rowsHidden;
        nextRow = cutAt;
        return rows;
    }

    /// <summary>One row's values inside the bound's columns; outside the bound only the shared formulas' first cells are noted.</summary>
    private Dictionary<int, string> Row(XmlReader reader, int row, CellRange bound, bool formulas, bool inBound)
    {
        var cells = new Dictionary<int, string>();
        int previousColumn = 0;
        OfficeXml.Children(reader, () =>
        {
            if (!_x.Is(reader, OfficeXml.S, "c"))
            {
                OfficeXml.Skip(reader);
                return;
            }

            int column = ColumnNumber(reader, previousColumn);
            previousColumn = column;
            bool wanted = inBound && column >= bound.FirstColumn && column <= bound.LastColumn;
            if (!wanted && !formulas)
            {
                OfficeXml.Skip(reader);
                return;
            }

            string value = CellValue(reader, CellReference.Cell(column, row), formulas, wanted);
            if (wanted && value.Length > 0)
            {
                cells[column] = value;
            }
        });
        return cells;
    }

    /// <summary>A cell's value as shown: a shared or inline string, a number by its format, a boolean, an error, a formula's result.</summary>
    private string CellValue(XmlReader reader, string reference, bool formulas, bool wanted)
    {
        string? type = _x.Attr(reader, "t");
        string? style = _x.Attr(reader, "s");
        string? value = null, formula = null, formulaType = null, shared = null, inline = null;
        OfficeXml.Children(reader, () =>
        {
            if (_x.Is(reader, OfficeXml.S, "v"))
            {
                value = OfficeXml.Text(reader);
            }
            else if (_x.Is(reader, OfficeXml.S, "f"))
            {
                formulaType = _x.Attr(reader, "t");
                shared = _x.Attr(reader, "si");
                formula = OfficeXml.Text(reader);
            }
            else if (_x.Is(reader, OfficeXml.S, "is"))
            {
                inline = RichText(reader);
            }
            else
            {
                OfficeXml.Skip(reader);
            }
        });

        string? sharedWith = null;
        if (formulaType == "shared" && shared is not null)
        {
            if (formula is { Length: > 0 })
            {
                _sharedFirst.TryAdd(shared, reference);
            }
            else if (_sharedFirst.TryGetValue(shared, out string? first))
            {
                sharedWith = first;
            }
        }

        if (!wanted)
        {
            return "";
        }

        string shown = type switch
        {
            "s" => SharedString(value),
            "inlineStr" => inline ?? "",
            "str" => OfficeXml.Unescape(value ?? ""),
            "b" => value is null ? "" : value.Trim() is "1" or "true" ? "TRUE" : "FALSE",
            "e" or "d" => value ?? "",
            _ => value is null ? "" : double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) ? NumberFormats.Format(number, Format(style), _date1904) : value,
        };

        if (formula is { Length: > 0 })
        {
            return formulas || shown.Length == 0 ? OfficeText.WithFormula(shown, formula) : shown;
        }

        return formulas && sharedWith is not null ? OfficeText.WithShared(shown, sharedWith) : shown;
    }

    /// <summary>A string item's text (<c>&lt;si&gt;</c> or <c>&lt;is&gt;</c>): its runs joined, phonetic guides left out, escapes undone.</summary>
    private string RichText(XmlReader reader)
    {
        var sb = new StringBuilder();
        OfficeXml.Children(reader, () =>
        {
            if (_x.Is(reader, OfficeXml.S, "t"))
            {
                sb.Append(OfficeXml.Text(reader));
            }
            else if (_x.Is(reader, OfficeXml.S, "r"))
            {
                OfficeXml.Children(reader, () =>
                {
                    if (_x.Is(reader, OfficeXml.S, "t"))
                    {
                        sb.Append(OfficeXml.Text(reader));
                    }
                    else
                    {
                        OfficeXml.Skip(reader);
                    }
                });
            }
            else
            {
                OfficeXml.Skip(reader);
            }
        });
        return OfficeXml.Unescape(sb.ToString());
    }

    private string SharedString(string? index)
    {
        if (_strings is null)
        {
            _strings = [];
            _stringsPart = _package.Related(_main, "sharedStrings");
            if (_stringsPart is { } part)
            {
                OfficePackage.Guard(part, () =>
                {
                    using var reader = _package.Whole(part);
                    reader.MoveToContent();
                    OfficeXml.Children(reader, () =>
                    {
                        if (_x.Is(reader, OfficeXml.S, "si"))
                        {
                            _strings.Add(RichText(reader));
                        }
                        else
                        {
                            OfficeXml.Skip(reader);
                        }
                    });
                    return 0;
                });
            }
        }

        return int.TryParse(index, NumberStyles.None, CultureInfo.InvariantCulture, out int i) && i < _strings.Count
            ? _strings[i]
            : throw new OfficeDamagedException(_stringsPart ?? "xl/sharedStrings.xml");
    }

    private CellFormat Format(string? style)
    {
        if (_formats is null)
        {
            _formats = [];
            _stylesPart = _package.Related(_main, "styles");
            if (_stylesPart is { } part)
            {
                var codes = new Dictionary<int, string>();
                var ids = new List<int>();
                OfficePackage.Guard(part, () =>
                {
                    using var reader = _package.Whole(part);
                    reader.MoveToContent();
                    OfficeXml.Children(reader, () =>
                    {
                        if (_x.Is(reader, OfficeXml.S, "numFmts"))
                        {
                            OfficeXml.Children(reader, () =>
                            {
                                if (_x.Is(reader, OfficeXml.S, "numFmt") && int.TryParse(_x.Attr(reader, "numFmtId"), NumberStyles.None, CultureInfo.InvariantCulture, out int id))
                                {
                                    codes[id] = _x.Attr(reader, "formatCode") ?? "";
                                }

                                OfficeXml.Skip(reader);
                            });
                        }
                        else if (_x.Is(reader, OfficeXml.S, "cellXfs"))
                        {
                            OfficeXml.Children(reader, () =>
                            {
                                if (_x.Is(reader, OfficeXml.S, "xf"))
                                {
                                    ids.Add(int.TryParse(_x.Attr(reader, "numFmtId"), NumberStyles.None, CultureInfo.InvariantCulture, out int id) ? id : 0);
                                }

                                OfficeXml.Skip(reader);
                            });
                        }
                        else
                        {
                            OfficeXml.Skip(reader);
                        }
                    });
                    return 0;
                });
                _formats.AddRange(ids.Select(id => NumberFormats.Classify(id, codes.TryGetValue(id, out string? code) ? code : null)));
            }
        }

        if (style is null)
        {
            return _formats.Count > 0 ? _formats[0] : CellFormat.General;
        }

        return int.TryParse(style, NumberStyles.None, CultureInfo.InvariantCulture, out int index) && index < _formats.Count
            ? _formats[index]
            : index == 0 && _formats.Count == 0 ? CellFormat.General : throw new OfficeDamagedException(_stylesPart ?? "xl/styles.xml");
    }
}
