using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using NeonSidekick.Files;
using NeonSidekick.Sql;

namespace NeonSidekick.Office;

/// <summary>What a Word read gave: the text up to the asked length, whether there is more past it, and a note on where it stopped short.</summary>
public sealed record WordText(string Text, bool More, string? Note);

/// <summary>
/// A Word document's body as Markdown (2026-10-28, the Office plan), streamed from <c>word/document.xml</c>: headings from the <c>Heading N</c>
/// and <c>Title</c> styles (and a style's outline level), list paragraphs as <c>-</c>/<c>1.</c>, tables as Markdown tables (a merged cell
/// repeated blank, a table in a cell flattened with <c> / </c>), hyperlinks as <c>[text](url)</c>, content controls and text boxes inline,
/// markers for what isn't text (<see cref="OfficeText.Picture"/>, chart, diagram, equation, embedded object or document). Tracked
/// insertions kept and deletions dropped, a move read at its new place only; a field shows its result, never its code; hidden text marked
/// (<see cref="OfficeText.HiddenOpen"/>). Footnotes, endnotes and comments (with author) follow the body; headers and footers are page
/// furniture and left out. <c>aloud</c> gives plain paragraphs for <c>/read</c>: no marks, markers, hidden text or notes.
/// Stops once the asked length is passed. Nothing outside the package is opened: a link is its URL as text.
/// </summary>
public sealed partial class WordReader
{
    private const int MaxCellChars = ExcelReader.MaxCellChars;
    private const int MaxStyleDepth = 10;

    private readonly OfficePackage _package;
    private readonly string _main;
    private readonly bool _aloud;
    private readonly int _limit;
    private readonly CancellationToken _cancellation;
    private readonly OfficeXml _x = new();
    private readonly StringBuilder _out = new();
    private readonly List<bool> _fields = [];
    private readonly Dictionary<string, int> _headings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string NumId, int Level)> _styleLists = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<int, bool>> _numbering = new(StringComparer.Ordinal);
    private string _part;
    private bool _lastWasList;

    /// <param name="limit">How many characters to read before stopping (the caller's <c>from_char</c> plus its budget, and one more to know there is more).</param>
    public WordReader(OfficePackage package, string main, int limit, bool aloud, CancellationToken cancellationToken)
    {
        _package = package ?? throw new ArgumentNullException(nameof(package));
        _main = main ?? throw new ArgumentNullException(nameof(main));
        _limit = limit;
        _aloud = aloud;
        _cancellation = cancellationToken;
        _part = main;
    }

    private bool InFieldCode => _fields.Contains(true);

    /// <summary>The document's text up to the limit. Damage before any text throws <see cref="OfficeDamagedException"/>; after some, the text stands with a note.</summary>
    public WordText Read()
    {
        LoadStyles();
        LoadNumbering();
        bool more = false;
        string? note = null;
        try
        {
            OfficePackage.Guard(_main, () =>
            {
                using var reader = _package.Streamed(_main);
                reader.MoveToContent();
                OfficeXml.Children(reader, () =>
                {
                    if (_x.Is(reader, OfficeXml.W, "body"))
                    {
                        Blocks(reader, Body, nested: false);
                    }
                    else
                    {
                        OfficeXml.Skip(reader);
                    }
                });
                return 0;
            });

            if (!_aloud)
            {
                Notes("footnotes", "footnote", OfficeText.FootnotesHeading, "^");
                Notes("endnotes", "endnote", OfficeText.EndnotesHeading, "^e");
                Comments();
            }
        }
        catch (StopReading)
        {
            more = true;
        }
        catch (OfficeDamagedException ex) when (_out.Length > 0)
        {
            note = OfficeText.DamagedPast(ex.Part);
        }
        catch (OfficeStreamLimitException ex)
        {
            note = OfficeText.RestTooLarge(ex.Part);
        }

        return new WordText(_out.ToString(), more, note);
    }

    /// <summary>Thrown once the limit is passed, to stop the stream at once.</summary>
    private sealed class StopReading : Exception
    {
    }

    private void Body(string text, bool list)
    {
        if (_out.Length > 0)
        {
            _out.Append(list && _lastWasList ? "\n" : "\n\n");
        }

        _out.Append(text);
        _lastWasList = list;
        if (_out.Length >= _limit)
        {
            throw new StopReading();
        }
    }

    /// <summary>
    /// The block children of the element the reader stands on: paragraphs, tables, content controls, tracked insertions. A table inside a
    /// table's cell (<paramref name="nested"/>) is flattened into one line.
    /// </summary>
    private void Blocks(XmlReader reader, Action<string, bool> emit, bool nested) =>
        OfficeXml.Children(reader, () => Block(reader, emit, nested));

    private void Block(XmlReader reader, Action<string, bool> emit, bool nested)
    {
        _cancellation.ThrowIfCancellationRequested();
        string ns = _x.Key(reader.NamespaceURI);
        if (ns == OfficeXml.W)
        {
            switch (reader.LocalName)
            {
                case "p":
                    var (text, list) = Paragraph(reader);
                    if (text.Length > 0)
                    {
                        emit(text, list);
                    }

                    return;
                case "tbl":
                    Table(reader, emit, nested);
                    return;
                case "sdt" or "sdtContent" or "customXml" or "ins" or "moveTo" or "smartTag" or "txbxContent":
                    Blocks(reader, emit, nested);
                    return;
                case "altChunk":
                    if (!_aloud)
                    {
                        emit(OfficeText.EmbeddedDocument, false);
                    }

                    OfficeXml.Skip(reader);
                    return;
            }
        }
        else if (ns == OfficeXml.Mc && reader.LocalName == "AlternateContent")
        {
            Alternate(reader, () => Block(reader, emit, nested));
            return;
        }

        OfficeXml.Skip(reader);
    }

    /// <summary>An <c>mc:AlternateContent</c>: the first <c>Choice</c>'s children read, every other choice and the <c>Fallback</c> skipped, so nothing reads twice.</summary>
    private void Alternate(XmlReader reader, Action child)
    {
        bool chosen = false;
        OfficeXml.Children(reader, () =>
        {
            if (!chosen && _x.Is(reader, OfficeXml.Mc, "Choice"))
            {
                chosen = true;
                OfficeXml.Children(reader, child);
            }
            else
            {
                OfficeXml.Skip(reader);
            }
        });
    }

    /// <summary>A paragraph rendered: its heading's <c>#</c>s or its list mark, the text; empty when it holds none.</summary>
    private (string Text, bool List) Paragraph(XmlReader reader)
    {
        var line = new Line(this);
        string? style = null;
        string? numId = null;
        int? level = null;
        OfficeXml.Children(reader, () =>
        {
            if (_x.Is(reader, OfficeXml.W, "pPr"))
            {
                OfficeXml.Children(reader, () =>
                {
                    if (_x.Is(reader, OfficeXml.W, "pStyle"))
                    {
                        style = _x.Attr(reader, "val", OfficeXml.W);
                    }
                    else if (_x.Is(reader, OfficeXml.W, "numPr"))
                    {
                        OfficeXml.Children(reader, () =>
                        {
                            if (_x.Is(reader, OfficeXml.W, "numId"))
                            {
                                numId = _x.Attr(reader, "val", OfficeXml.W);
                            }
                            else if (_x.Is(reader, OfficeXml.W, "ilvl") && int.TryParse(_x.Attr(reader, "val", OfficeXml.W), NumberStyles.Integer, CultureInfo.InvariantCulture, out int l))
                            {
                                level = l;
                            }

                            OfficeXml.Skip(reader);
                        });
                        return;
                    }

                    OfficeXml.Skip(reader);
                });
                return;
            }

            Inline(reader, line);
        });

        string text = line.Finish();
        if (text.Length == 0 || _aloud)
        {
            return (text, false);
        }

        if (style is not null && _headings.TryGetValue(style, out int heading))
        {
            return (new string('#', Math.Min(heading, 6)) + " " + text, false);
        }

        if (numId is null && style is not null && _styleLists.TryGetValue(style, out var fromStyle))
        {
            numId = fromStyle.NumId;
            level ??= fromStyle.Level;
        }

        if (numId is null or "0")
        {
            return (text, false);
        }

        int depth = Math.Clamp(level ?? 0, 0, 8);
        bool ordered = _numbering.TryGetValue(numId, out var levels) && levels.TryGetValue(depth, out bool o) && o;
        return (new string(' ', depth * 2) + (ordered ? "1. " : "- ") + text, true);
    }

    /// <summary>One inline child of a paragraph (or of a hyperlink, a content control, a tracked insertion) into <paramref name="line"/>.</summary>
    private void Inline(XmlReader reader, Line line)
    {
        string ns = _x.Key(reader.NamespaceURI);
        if (ns == OfficeXml.W)
        {
            switch (reader.LocalName)
            {
                case "r":
                    Run(reader, line);
                    return;
                case "hyperlink":
                    Hyperlink(reader, line);
                    return;
                case "ins" or "moveTo" or "smartTag" or "sdt" or "sdtContent" or "customXml" or "fldSimple" or "dir" or "bdo":
                    OfficeXml.Children(reader, () => Inline(reader, line));
                    return;
            }
        }
        else if (ns == OfficeXml.M && reader.LocalName is "oMath" or "oMathPara")
        {
            line.Marker(OfficeText.Equation, false);
        }
        else if (ns == OfficeXml.Mc && reader.LocalName == "AlternateContent")
        {
            Alternate(reader, () => Inline(reader, line));
            return;
        }

        OfficeXml.Skip(reader);
    }

    /// <summary>A run: its text, tabs, breaks and hyphens; fields' codes skipped; drawings, VML and objects as markers; hidden when <c>w:vanish</c> says so.</summary>
    private void Run(XmlReader reader, Line line)
    {
        bool hidden = false;
        OfficeXml.Children(reader, () => RunChild(reader, line, ref hidden));
    }

    private void RunChild(XmlReader reader, Line line, ref bool hidden)
    {
        string ns = _x.Key(reader.NamespaceURI);
        if (ns == OfficeXml.W)
        {
            switch (reader.LocalName)
            {
                case "rPr":
                    hidden = RunHidden(reader);
                    return;
                case "t":
                    line.Add(OfficeXml.Text(reader), hidden);
                    return;
                case "fldChar":
                    Field(_x.Attr(reader, "fldCharType", OfficeXml.W));
                    OfficeXml.Skip(reader);
                    return;
                case "tab":
                    line.Add("\t", hidden);
                    break;
                case "br" or "cr":
                    line.Add("\n", hidden);
                    break;
                case "noBreakHyphen":
                    line.Add("-", hidden);
                    break;
                case "drawing":
                    Drawing(reader, line, hidden);
                    return;
                case "pict":
                    Vml(reader, line, hidden);
                    return;
                case "object":
                    OleObject(reader, line, hidden);
                    return;
                case "footnoteReference" or "endnoteReference":
                    if (!_aloud)
                    {
                        line.Add("[" + (reader.LocalName == "footnoteReference" ? "^" : "^e") + _x.Attr(reader, "id", OfficeXml.W) + "]", hidden);
                    }

                    break;
                case "ruby":
                    OfficeXml.Children(reader, () =>
                    {
                        if (_x.Is(reader, OfficeXml.W, "rubyBase"))
                        {
                            OfficeXml.Children(reader, () => Inline(reader, line));
                        }
                        else
                        {
                            OfficeXml.Skip(reader);
                        }
                    });
                    return;
            }
        }
        else if (ns == OfficeXml.M && reader.LocalName is "oMath" or "oMathPara")
        {
            line.Marker(OfficeText.Equation, hidden);
        }
        else if (ns == OfficeXml.Mc && reader.LocalName == "AlternateContent")
        {
            bool inner = hidden;
            Alternate(reader, () => RunChild(reader, line, ref inner));
            return;
        }

        OfficeXml.Skip(reader);
    }

    private void Field(string? type)
    {
        switch (type)
        {
            case "begin":
                _fields.Add(true);
                break;
            case "separate" when _fields.Count > 0:
                _fields[^1] = false;
                break;
            case "end" when _fields.Count > 0:
                _fields.RemoveAt(_fields.Count - 1);
                break;
        }
    }

    private bool RunHidden(XmlReader reader)
    {
        bool hidden = false;
        OfficeXml.Children(reader, () =>
        {
            if (_x.Is(reader, OfficeXml.W, "vanish"))
            {
                hidden = OfficeXml.On(_x.Attr(reader, "val", OfficeXml.W));
            }

            OfficeXml.Skip(reader);
        });
        return hidden;
    }

    private void Hyperlink(XmlReader reader, Line line)
    {
        string? id = _x.Attr(reader, "id", OfficeXml.R);
        string? url = id is null ? null : _package.Rel(_part, id) is { External: true } rel ? rel.Target : null;
        var inner = new Line(this);
        OfficeXml.Children(reader, () => Inline(reader, inner));
        string text = inner.Finish();
        line.Raw(url is { Length: > 0 } && !_aloud && text.Length > 0 && !InFieldCode ? "[" + text + "](" + url + ")" : text);
    }

    /// <summary>A DrawingML drawing: a picture, chart or diagram marker from its <c>docPr</c> alt text, a shape's text box read inline.</summary>
    private void Drawing(XmlReader reader, Line line, bool hidden)
    {
        string alt = "";
        string kind = "";
        var markers = new List<string>();
        var texts = new List<string>();
        using (var sub = reader.ReadSubtree())
        {
            sub.Read();
            while (sub.Read())
            {
                if (sub.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                string ns = _x.Key(sub.NamespaceURI);
                if (ns == OfficeXml.Wp && sub.LocalName == "docPr")
                {
                    alt = AltText(sub);
                }
                else if (ns == OfficeXml.A && sub.LocalName == "graphicData" && kind.Length == 0)
                {
                    kind = sub.GetAttribute("uri") ?? "";
                }
                else if (sub.LocalName == "cNvPr" && ns.EndsWith("picture", StringComparison.Ordinal) && !kind.EndsWith("/picture", StringComparison.Ordinal))
                {
                    markers.Add(OfficeText.Picture(AltText(sub)));   // a picture in a group or canvas
                }
                else if (ns == OfficeXml.W && sub.LocalName == "txbxContent")
                {
                    Blocks(sub, (t, _) => texts.Add(t), nested: true);
                }
                else if (ns == OfficeXml.M && sub.LocalName is "oMath" or "oMathPara")
                {
                    markers.Add(OfficeText.Equation);
                    OfficeXml.Skip(sub);
                }
                else if (ns == OfficeXml.Mc && sub.LocalName == "Fallback")
                {
                    OfficeXml.Skip(sub);
                }
            }
        }

        if (kind.Contains("/chart", StringComparison.OrdinalIgnoreCase))
        {
            markers.Insert(0, OfficeText.Chart(alt));
        }
        else if (kind.Contains("/diagram", StringComparison.OrdinalIgnoreCase))
        {
            markers.Insert(0, OfficeText.Diagram(alt));
        }
        else if (kind.EndsWith("/picture", StringComparison.OrdinalIgnoreCase))
        {
            markers.Insert(0, OfficeText.Picture(alt));
        }

        foreach (string marker in markers)
        {
            line.Marker(marker, hidden);
        }

        foreach (string text in texts)
        {
            line.Piece(text, hidden);
        }
    }

    /// <summary>A VML picture (<c>w:pict</c>, older documents): an image's marker, a text box's text.</summary>
    private void Vml(XmlReader reader, Line line, bool hidden)
    {
        string shapeAlt = "";
        using var sub = reader.ReadSubtree();
        sub.Read();
        while (sub.Read())
        {
            if (sub.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            string ns = _x.Key(sub.NamespaceURI);
            if (ns == OfficeXml.Vml && sub.LocalName is "shape" or "rect" or "roundrect" or "oval")
            {
                shapeAlt = sub.GetAttribute("alt") ?? "";
            }
            else if (ns == OfficeXml.Vml && sub.LocalName == "imagedata")
            {
                string title = _x.Attr(sub, "title", OfficeXml.VmlOffice) ?? "";
                line.Marker(OfficeText.Picture(shapeAlt.Length > 0 ? shapeAlt : title), hidden);
            }
            else if (ns == OfficeXml.W && sub.LocalName == "txbxContent")
            {
                Blocks(sub, (t, _) => line.Piece(t, hidden), nested: true);
            }
        }
    }

    /// <summary>An embedded or linked OLE object: its program's marker, never opened.</summary>
    private void OleObject(XmlReader reader, Line line, bool hidden)
    {
        string program = "";
        using (var sub = reader.ReadSubtree())
        {
            sub.Read();
            while (sub.Read() && program.Length == 0)
            {
                if (sub.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                string ns = _x.Key(sub.NamespaceURI);
                if (ns == OfficeXml.VmlOffice && sub.LocalName == "OLEObject")
                {
                    program = sub.GetAttribute("ProgID") ?? "";
                }
                else if (ns == OfficeXml.W && sub.LocalName is "objectEmbed" or "objectLink")
                {
                    program = _x.Attr(sub, "progId", OfficeXml.W) ?? "";
                }
            }

            while (sub.Read())
            {
                // The rest of the object (its preview picture) unread.
            }
        }

        line.Marker(OfficeText.EmbeddedObject(program), hidden);
    }

    private string AltText(XmlReader reader)
    {
        string described = reader.GetAttribute("descr") ?? "";
        string titled = reader.GetAttribute("title") ?? "";
        return Plain(described.Length > 0 ? described : titled);
    }

    /// <summary>
    /// A table: Markdown rows (the first as the header), a cell's paragraphs joined by line breaks and made safe by
    /// <see cref="SqlText.TableCell"/>, long ones cut; a span repeated blank. Read aloud, each cell's text is its own paragraph; nested, the
    /// cells joined with <c> / </c>.
    /// </summary>
    private void Table(XmlReader reader, Action<string, bool> emit, bool nested)
    {
        var rows = new List<List<string>>();
        Rows(reader, rows);
        if (nested || _aloud)
        {
            var cells = rows.SelectMany(r => r).Where(c => c.Length > 0).ToList();
            if (nested && cells.Count > 0)
            {
                emit(string.Join(" / ", cells), false);
            }
            else
            {
                cells.ForEach(c => emit(c, false));
            }

            return;
        }

        if (rows.Count == 0 || rows.All(r => r.All(c => c.Length == 0)))
        {
            return;
        }

        int width = rows.Max(r => r.Count);
        var sb = new StringBuilder();
        for (int i = 0; i < rows.Count; i++)
        {
            sb.Append(i == 0 ? "" : "\n").Append('|');
            for (int c = 0; c < width; c++)
            {
                string cell = c < rows[i].Count ? rows[i][c] : "";
                sb.Append(' ').Append(SqlText.TableCell(Cut(cell))).Append(" |");
            }

            if (i == 0)
            {
                sb.Append("\n|").Append(string.Concat(Enumerable.Repeat(" --- |", width)));
            }
        }

        emit(sb.ToString(), false);
    }

    private void Rows(XmlReader reader, List<List<string>> rows) =>
        OfficeXml.Children(reader, () =>
        {
            if (_x.Is(reader, OfficeXml.W, "tr"))
            {
                var cells = new List<string>();
                CellsOf(reader, cells);
                rows.Add(cells);
            }
            else if (_x.Key(reader.NamespaceURI) == OfficeXml.W && reader.LocalName is "sdt" or "sdtContent" or "customXml" or "ins")
            {
                Rows(reader, rows);
            }
            else
            {
                OfficeXml.Skip(reader);
            }
        });

    private void CellsOf(XmlReader reader, List<string> cells) =>
        OfficeXml.Children(reader, () =>
        {
            if (_x.Is(reader, OfficeXml.W, "tc"))
            {
                Cell(reader, cells);
            }
            else if (_x.Key(reader.NamespaceURI) == OfficeXml.W && reader.LocalName is "sdt" or "sdtContent" or "customXml" or "ins")
            {
                CellsOf(reader, cells);
            }
            else
            {
                OfficeXml.Skip(reader);
            }
        });

    private void Cell(XmlReader reader, List<string> cells)
    {
        int span = 1;
        bool continued = false;
        var parts = new List<string>();
        OfficeXml.Children(reader, () =>
        {
            if (_x.Is(reader, OfficeXml.W, "tcPr"))
            {
                OfficeXml.Children(reader, () =>
                {
                    if (_x.Is(reader, OfficeXml.W, "gridSpan") && int.TryParse(_x.Attr(reader, "val", OfficeXml.W), NumberStyles.Integer, CultureInfo.InvariantCulture, out int s))
                    {
                        span = Math.Clamp(s, 1, 64);
                    }
                    else if (_x.Is(reader, OfficeXml.W, "vMerge"))
                    {
                        continued = _x.Attr(reader, "val", OfficeXml.W) is null or "continue";
                    }

                    OfficeXml.Skip(reader);
                });
                return;
            }

            Block(reader, (t, _) => parts.Add(t), nested: true);
        });

        cells.Add(continued ? "" : string.Join("\n", parts));
        for (int i = 1; i < span; i++)
        {
            cells.Add("");
        }
    }

    private static string Cut(string cell) => cell.Length <= MaxCellChars ? cell : TextCut.Safe(cell, MaxCellChars) + "…";

    /// <summary>Footnotes or endnotes after the body: <c>[^id]: text</c>, the separators Word keeps among them left out.</summary>
    private void Notes(string kind, string element, string heading, string mark)
    {
        if (_package.Related(_main, kind) is not { } part)
        {
            return;
        }

        var lines = new List<string>();
        Whole(part, reader => OfficeXml.Children(reader, () =>
        {
            if (_x.Is(reader, OfficeXml.W, element) && _x.Attr(reader, "type", OfficeXml.W) is null or "normal")
            {
                string id = _x.Attr(reader, "id", OfficeXml.W) ?? "";
                var texts = new List<string>();
                Blocks(reader, (t, _) => texts.Add(t), nested: true);
                if (texts.Count > 0)
                {
                    lines.Add("[" + mark + id + "]: " + string.Join(" ", texts));
                }
            }
            else
            {
                OfficeXml.Skip(reader);
            }
        }));

        Section(heading, lines);
    }

    /// <summary>Comments after the body, each with its author.</summary>
    private void Comments()
    {
        if (_package.Related(_main, "comments") is not { } part)
        {
            return;
        }

        var lines = new List<string>();
        Whole(part, reader => OfficeXml.Children(reader, () =>
        {
            if (_x.Is(reader, OfficeXml.W, "comment"))
            {
                string author = Plain(_x.Attr(reader, "author", OfficeXml.W) ?? "");
                var texts = new List<string>();
                Blocks(reader, (t, _) => texts.Add(t), nested: true);
                lines.Add("- " + (author.Length > 0 ? author + ": " : "") + string.Join(" ", texts));
            }
            else
            {
                OfficeXml.Skip(reader);
            }
        }));

        Section(heading: OfficeText.CommentsHeading, lines);
    }

    private void Section(string heading, List<string> lines)
    {
        if (lines.Count == 0)
        {
            return;
        }

        _lastWasList = false;
        Body(heading + "\n" + string.Join("\n", lines), false);
    }

    /// <summary>A part held whole, read from its root element's children, its fields' state its own.</summary>
    private void Whole(string part, Action<XmlReader> read)
    {
        string before = _part;
        _part = part;
        _fields.Clear();
        try
        {
            OfficePackage.Guard(part, () =>
            {
                using var reader = _package.Whole(part);
                reader.MoveToContent();
                read(reader);
                return 0;
            });
        }
        finally
        {
            _part = before;
        }
    }

    /// <summary>The paragraph styles' heading levels and lists, followed down their <c>basedOn</c> chains.</summary>
    private void LoadStyles()
    {
        var styles = new Dictionary<string, (string? Name, string? BasedOn, int? Outline, string? NumId, int? Level)>(StringComparer.Ordinal);
        if (_package.Related(_main, "styles") is { } part)
        {
            Whole(part, reader => OfficeXml.Children(reader, () =>
            {
                if (_x.Is(reader, OfficeXml.W, "style") && _x.Attr(reader, "type", OfficeXml.W) is null or "paragraph" && _x.Attr(reader, "styleId", OfficeXml.W) is { } id)
                {
                    string? name = null, basedOn = null, numId = null;
                    int? outline = null, level = null;
                    OfficeXml.Children(reader, () =>
                    {
                        if (_x.Is(reader, OfficeXml.W, "name"))
                        {
                            name = _x.Attr(reader, "val", OfficeXml.W);
                        }
                        else if (_x.Is(reader, OfficeXml.W, "basedOn"))
                        {
                            basedOn = _x.Attr(reader, "val", OfficeXml.W);
                        }
                        else if (_x.Is(reader, OfficeXml.W, "pPr"))
                        {
                            OfficeXml.Children(reader, () =>
                            {
                                if (_x.Is(reader, OfficeXml.W, "outlineLvl"))
                                {
                                    outline = Int(_x.Attr(reader, "val", OfficeXml.W));
                                }
                                else if (_x.Is(reader, OfficeXml.W, "numPr"))
                                {
                                    OfficeXml.Children(reader, () =>
                                    {
                                        if (_x.Is(reader, OfficeXml.W, "numId"))
                                        {
                                            numId = _x.Attr(reader, "val", OfficeXml.W);
                                        }
                                        else if (_x.Is(reader, OfficeXml.W, "ilvl"))
                                        {
                                            level = Int(_x.Attr(reader, "val", OfficeXml.W));
                                        }

                                        OfficeXml.Skip(reader);
                                    });
                                    return;
                                }

                                OfficeXml.Skip(reader);
                            });
                            return;
                        }

                        OfficeXml.Skip(reader);
                    });
                    styles[id] = (name, basedOn, outline, numId, level);
                    return;
                }

                OfficeXml.Skip(reader);
            }));
        }

        foreach (string id in styles.Keys)
        {
            string? current = id;
            for (int depth = 0; current is not null && depth < MaxStyleDepth && styles.TryGetValue(current, out var style); depth++)
            {
                int? level = HeadingOf(style.Name, current) ?? (style.Outline is >= 0 and < 9 ? style.Outline + 1 : null);
                if (level is { } found)
                {
                    _headings[id] = found;
                    break;
                }

                current = style.BasedOn;
            }

            current = id;
            for (int depth = 0; current is not null && depth < MaxStyleDepth && styles.TryGetValue(current, out var style); depth++)
            {
                if (style.NumId is { } numId)
                {
                    _styleLists[id] = (numId, style.Level ?? 0);
                    break;
                }

                current = style.BasedOn;
            }
        }

        if (styles.Count == 0)
        {
            // No styles part: Word's own ids still say heading.
            foreach (string id in new[] { "Title", "Heading1", "Heading2", "Heading3", "Heading4", "Heading5", "Heading6" })
            {
                _headings[id] = HeadingOf(null, id)!.Value;
            }
        }
    }

    private static int? HeadingOf(string? name, string id)
    {
        foreach (string candidate in new[] { name ?? "", id })
        {
            if (candidate.Equals("Title", StringComparison.OrdinalIgnoreCase))
            {
                return 1;
            }

            if (HeadingName().Match(candidate) is { Success: true } match)
            {
                return int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            }
        }

        return null;
    }

    [GeneratedRegex("^heading ?([1-9])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HeadingName();

    /// <summary>Which lists number their items: <c>numId</c> → its abstract list's levels, ordered unless a bullet (or none).</summary>
    private void LoadNumbering()
    {
        if (_package.Related(_main, "numbering") is not { } part)
        {
            return;
        }

        var abstracts = new Dictionary<string, Dictionary<int, bool>>(StringComparer.Ordinal);
        var nums = new Dictionary<string, string>(StringComparer.Ordinal);
        Whole(part, reader => OfficeXml.Children(reader, () =>
        {
            if (_x.Is(reader, OfficeXml.W, "abstractNum") && _x.Attr(reader, "abstractNumId", OfficeXml.W) is { } abstractId)
            {
                var levels = new Dictionary<int, bool>();
                OfficeXml.Children(reader, () =>
                {
                    if (_x.Is(reader, OfficeXml.W, "lvl") && Int(_x.Attr(reader, "ilvl", OfficeXml.W)) is { } ilvl)
                    {
                        OfficeXml.Children(reader, () =>
                        {
                            if (_x.Is(reader, OfficeXml.W, "numFmt"))
                            {
                                levels[ilvl] = _x.Attr(reader, "val", OfficeXml.W) is not (null or "bullet" or "none");
                            }

                            OfficeXml.Skip(reader);
                        });
                        return;
                    }

                    OfficeXml.Skip(reader);
                });
                abstracts[abstractId] = levels;
                return;
            }

            if (_x.Is(reader, OfficeXml.W, "num") && _x.Attr(reader, "numId", OfficeXml.W) is { } numId)
            {
                OfficeXml.Children(reader, () =>
                {
                    if (_x.Is(reader, OfficeXml.W, "abstractNumId") && _x.Attr(reader, "val", OfficeXml.W) is { } abstractNumId)
                    {
                        nums[numId] = abstractNumId;
                    }

                    OfficeXml.Skip(reader);
                });
                return;
            }

            OfficeXml.Skip(reader);
        }));

        foreach (var (numId, abstractId) in nums)
        {
            if (abstracts.TryGetValue(abstractId, out var levels))
            {
                _numbering[numId] = levels;
            }
        }
    }

    private static int? Int(string? value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : null;

    /// <summary>An attribute's text for one line: line breaks as spaces.</summary>
    private static string Plain(string text) => text.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ').Trim();

    /// <summary>One paragraph's text as it builds: hidden runs wrapped in their marks, a field's code left out, markers spaced from the words.</summary>
    private sealed class Line(WordReader owner)
    {
        private readonly StringBuilder _text = new();
        private bool _hidden;
        private bool _spaceNext;

        public void Add(string text, bool hidden)
        {
            if (text.Length == 0 || owner.InFieldCode || (hidden && owner._aloud))
            {
                return;
            }

            if (hidden != _hidden && !owner._aloud)
            {
                _text.Append(hidden ? OfficeText.HiddenOpen : OfficeText.HiddenClose);
                _hidden = hidden;
            }

            if (_spaceNext && !char.IsWhiteSpace(text[0]) && text[0] is not ('.' or ',' or ';' or ':' or '!' or '?' or ')' or ']' or '}'))
            {
                _text.Append(' ');
            }

            _spaceNext = false;
            _text.Append(text);
        }

        /// <summary>Text already built (a link's), placed outside any hidden mark.</summary>
        public void Raw(string text) => Add(text, false);

        /// <summary>A marker for what isn't text; none read aloud.</summary>
        public void Marker(string marker, bool hidden)
        {
            if (!owner._aloud)
            {
                Piece(marker, hidden);
            }
        }

        /// <summary>A piece that stands apart from the words around it: a space before and after.</summary>
        public void Piece(string text, bool hidden)
        {
            if (text.Length == 0 || owner.InFieldCode || (hidden && owner._aloud))
            {
                return;
            }

            if (_text.Length > 0 && !char.IsWhiteSpace(_text[^1]))
            {
                _spaceNext = true;
            }

            Add(text, hidden);
            _spaceNext = true;
        }

        public string Finish()
        {
            if (_hidden)
            {
                _text.Append(OfficeText.HiddenClose);
                _hidden = false;
            }

            return _text.ToString().Trim();
        }
    }
}
