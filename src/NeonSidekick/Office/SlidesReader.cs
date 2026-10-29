using System.Text;
using System.Xml;

namespace NeonSidekick.Office;

/// <summary>One slide read: its title (found by its placeholder), whether it is hidden, its text and, when asked, its speaker notes.</summary>
public sealed record SlideText(int Number, string Title, bool Hidden, string Body, string Notes);

/// <summary>
/// A presentation's slides (2026-10-28, the Office plan), in <c>presentation.xml</c>'s order (<c>sldIdLst</c> through the relationships, never
/// the parts' file names): the title found by its placeholder type (<c>title</c>/<c>ctrTitle</c>), not by position, then the text frames
/// and tables in the slide's shape order, a group's shapes read in place, markers for pictures, charts, diagrams, equations and embedded
/// objects. Text a slide shows only from its layout or master (a footer, a slide number) isn't read. Speaker notes on request; slide
/// comments aren't read in v1. <c>aloud</c> gives the title as a sentence and plain lines, no markers or table marks.
/// </summary>
public sealed class SlidesReader
{
    private readonly OfficePackage _package;
    private readonly string _main;
    private readonly CancellationToken _cancellation;
    private readonly OfficeXml _x = new();
    private List<string?>? _slides;
    private int _left = int.MaxValue;

    public SlidesReader(OfficePackage package, string main, CancellationToken cancellationToken)
    {
        _package = package ?? throw new ArgumentNullException(nameof(package));
        _main = main ?? throw new ArgumentNullException(nameof(main));
        _cancellation = cancellationToken;
    }

    /// <summary>How many slides the presentation lists.</summary>
    public int Count => Slides().Count;

    /// <summary>
    /// Slide <paramref name="number"/> (1-based) read, its text kept to about <paramref name="limit"/> characters (2026-10-28, review P5-03:
    /// one huge run is never held whole; the reader reads on past the rest unkept).
    /// </summary>
    public SlideText Read(int number, bool notes, bool aloud, int limit = int.MaxValue)
    {
        _cancellation.ThrowIfCancellationRequested();
        _left = Math.Max(limit, 1);
        string part = Slides()[number - 1] ?? throw new OfficeDamagedException(OfficePackage.Resolve(_main, "_rels/presentation.xml.rels"));
        var body = new List<string>();
        string title = "";
        bool hidden = false;
        OfficePackage.Guard(part, () =>
        {
            using var reader = _package.Streamed(part);
            reader.MoveToContent();
            hidden = _x.Attr(reader, "show") is "0" or "false";
            OfficeXml.Children(reader, () =>
            {
                if (_x.Is(reader, OfficeXml.P, "cSld"))
                {
                    OfficeXml.Children(reader, () =>
                    {
                        if (_x.Is(reader, OfficeXml.P, "spTree"))
                        {
                            Shapes(reader, part, body, aloud, ref title, titles: true);
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
            return 0;
        });

        string notesText = notes && !aloud && _package.Related(part, "notesSlide") is { } notesPart ? Notes(notesPart) : "";
        return new SlideText(number, title, hidden, string.Join("\n", body.Where(b => b.Length > 0)), notesText);
    }

    /// <summary>The slide as <c>read_office</c> shows it: its heading, its text, its notes.</summary>
    public static string Render(SlideText slide)
    {
        ArgumentNullException.ThrowIfNull(slide);
        var sb = new StringBuilder(OfficeText.SlideHeading(slide.Number, slide.Title, slide.Hidden));
        if (slide.Body.Length > 0)
        {
            sb.Append('\n').Append(slide.Body);
        }

        if (slide.Notes.Length > 0)
        {
            sb.Append('\n').Append(OfficeText.NotesHeading).Append('\n').Append(slide.Notes);
        }

        return sb.ToString();
    }

    /// <summary>The slide read aloud: the title as a sentence, then its lines.</summary>
    public static string Aloud(SlideText slide)
    {
        ArgumentNullException.ThrowIfNull(slide);
        string title = slide.Title.Length == 0 ? "" : slide.Title.TrimEnd() is var t && t.Length > 0 && char.IsPunctuation(t[^1]) ? t : slide.Title.TrimEnd() + ".";
        return string.Join("\n", new[] { title, slide.Body }.Where(s => s.Length > 0));
    }

    private List<string?> Slides()
    {
        if (_slides is not null)
        {
            return _slides;
        }

        var ids = new List<string>();
        OfficePackage.Guard(_main, () =>
        {
            using var reader = _package.Whole(_main);
            reader.MoveToContent();
            OfficeXml.Children(reader, () =>
            {
                if (_x.Is(reader, OfficeXml.P, "sldIdLst"))
                {
                    OfficeXml.Children(reader, () =>
                    {
                        if (_x.Is(reader, OfficeXml.P, "sldId") && _x.Attr(reader, "id", OfficeXml.R) is { } id)
                        {
                            ids.Add(id);
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

        _slides = [.. ids.Select(id => _package.Rel(_main, id) is { External: false } rel ? rel.Target : null)];
        return _slides;
    }

    /// <summary>A shape tree's shapes in order; the first title placeholder's text goes to <paramref name="title"/> when <paramref name="titles"/>.</summary>
    private void Shapes(XmlReader reader, string part, List<string> body, bool aloud, ref string title, bool titles)
    {
        string found = title;
        OfficeXml.Children(reader, () => Shape(reader, part, body, aloud, ref found, titles));
        title = found;
    }

    private void Shape(XmlReader reader, string part, List<string> body, bool aloud, ref string title, bool titles)
    {
        _cancellation.ThrowIfCancellationRequested();
        string ns = _x.Key(reader.NamespaceURI);
        if (ns == OfficeXml.Mc && reader.LocalName == "AlternateContent")
        {
            string found = title;
            _x.FirstChoice(reader, () => Shape(reader, part, body, aloud, ref found, titles));
            title = found;
            return;
        }

        if (ns != OfficeXml.P)
        {
            OfficeXml.Skip(reader);
            return;
        }

        switch (reader.LocalName)
        {
            case "sp":
                string? placeholder = null;
                var text = new List<string>();
                OfficeXml.Children(reader, () =>
                {
                    if (_x.Is(reader, OfficeXml.P, "nvSpPr"))
                    {
                        placeholder = Placeholder(reader);
                    }
                    else if (_x.Is(reader, OfficeXml.P, "txBody"))
                    {
                        TextBody(reader, text, aloud);
                    }
                    else
                    {
                        OfficeXml.Skip(reader);
                    }
                });

                string joined = string.Join("\n", text.Where(t => t.Length > 0));
                if (titles && title.Length == 0 && placeholder is "title" or "ctrTitle" && joined.Length > 0)
                {
                    title = joined.Replace('\n', ' ');
                }
                else if (joined.Length > 0)
                {
                    body.Add(joined);
                }

                return;
            case "grpSp":
                Shapes(reader, part, body, aloud, ref title, titles);
                return;
            case "pic":
                string alt = "";
                using (var sub = reader.ReadSubtree())
                {
                    while (sub.Read())
                    {
                        if (sub.NodeType == XmlNodeType.Element && sub.LocalName == "cNvPr" && alt.Length == 0)
                        {
                            alt = OfficeXml.AltText(sub);
                        }
                    }
                }

                if (!aloud)
                {
                    body.Add(OfficeText.Picture(alt));
                }

                return;
            case "graphicFrame":
                GraphicFrame(reader, body, aloud);
                return;
            default:
                OfficeXml.Skip(reader);
                return;
        }
    }

    private string? Placeholder(XmlReader reader)
    {
        string? type = null;
        using var sub = reader.ReadSubtree();
        while (sub.Read())
        {
            if (sub.NodeType == XmlNodeType.Element && _x.Is(sub, OfficeXml.P, "ph"))
            {
                type = sub.GetAttribute("type") ?? "body";
            }
        }

        return type;
    }

    /// <summary>A text body's paragraphs, each a line: runs, fields' shown text, line breaks; an equation as its marker.</summary>
    private void TextBody(XmlReader reader, List<string> lines, bool aloud) =>
        OfficeXml.Children(reader, () =>
        {
            if (!_x.Is(reader, OfficeXml.A, "p"))
            {
                OfficeXml.Skip(reader);
                return;
            }

            var sb = new StringBuilder();
            Paragraph(reader, sb, aloud);
            lines.Add(sb.ToString().Trim());
        });

    private void Paragraph(XmlReader reader, StringBuilder sb, bool aloud) => OfficeXml.Children(reader, () => ParagraphChild(reader, sb, aloud));

    private void ParagraphChild(XmlReader reader, StringBuilder sb, bool aloud)
    {
        string ns = _x.Key(reader.NamespaceURI);
        if (ns == OfficeXml.A && reader.LocalName is "r" or "fld")
        {
            OfficeXml.Children(reader, () =>
            {
                if (_x.Is(reader, OfficeXml.A, "t"))
                {
                    string text = OfficeXml.Text(reader, _left);
                    _left = Math.Max(_left - text.Length, 0);
                    sb.Append(text);
                }
                else
                {
                    OfficeXml.Skip(reader);
                }
            });
            return;
        }

        if (ns == OfficeXml.A && reader.LocalName == "br")
        {
            sb.Append('\n');
        }
        else if (ns == OfficeXml.Mc && reader.LocalName == "AlternateContent")
        {
            _x.FirstChoice(reader, () => ParagraphChild(reader, sb, aloud));
            return;
        }
        else if ((ns == OfficeXml.M && reader.LocalName is "oMath" or "oMathPara") || reader.LocalName == "m")
        {
            if (!aloud)
            {
                sb.Append(sb.Length > 0 && !char.IsWhiteSpace(sb[^1]) ? " " : "").Append(OfficeText.Equation).Append(' ');
            }
        }

        OfficeXml.Skip(reader);
    }

    /// <summary>A table, a chart, a diagram or an embedded object, by its graphic's kind.</summary>
    private void GraphicFrame(XmlReader reader, List<string> body, bool aloud)
    {
        string alt = "";
        string kind = "";
        string program = "";
        var rows = new List<List<string>>();
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
                if (sub.LocalName == "cNvPr" && ns == OfficeXml.P && alt.Length == 0)
                {
                    alt = OfficeXml.AltText(sub);
                }
                else if (ns == OfficeXml.A && sub.LocalName == "graphicData" && kind.Length == 0)
                {
                    kind = sub.GetAttribute("uri") ?? "";
                }
                else if (ns == OfficeXml.P && sub.LocalName == "oleObj" && program.Length == 0)
                {
                    program = sub.GetAttribute("progId") ?? "";
                    OfficeXml.Skip(sub);
                }
                else if (ns == OfficeXml.A && sub.LocalName == "tr")
                {
                    var cells = new List<string>();
                    OfficeXml.Children(sub, () =>
                    {
                        if (_x.Is(sub, OfficeXml.A, "tc"))
                        {
                            bool merged = OfficeXml.On(sub.GetAttribute("hMerge"), false) || OfficeXml.On(sub.GetAttribute("vMerge"), false);
                            var lines = new List<string>();
                            OfficeXml.Children(sub, () =>
                            {
                                if (_x.Is(sub, OfficeXml.A, "txBody"))
                                {
                                    TextBody(sub, lines, aloud);
                                }
                                else
                                {
                                    OfficeXml.Skip(sub);
                                }
                            });
                            cells.Add(merged ? "" : string.Join("\n", lines.Where(l => l.Length > 0)));
                        }
                        else
                        {
                            OfficeXml.Skip(sub);
                        }
                    });
                    rows.Add(cells);
                }
                else if (ns == OfficeXml.Mc && sub.LocalName == "Fallback")
                {
                    OfficeXml.Skip(sub);
                }
            }
        }

        if (rows.Count > 0)
        {
            body.Add(aloud ? string.Join("\n", rows.SelectMany(r => r).Where(c => c.Length > 0)) : Table(rows));
        }
        else if (aloud)
        {
            return;
        }
        else if (program.Length > 0 || kind.EndsWith("/ole", StringComparison.OrdinalIgnoreCase))
        {
            body.Add(OfficeText.EmbeddedObject(program));
        }
        else if (kind.Contains("/chart", StringComparison.OrdinalIgnoreCase))
        {
            body.Add(OfficeText.Chart(alt));
        }
        else if (kind.Contains("/diagram", StringComparison.OrdinalIgnoreCase))
        {
            body.Add(OfficeText.Diagram(alt));
        }
        else if (kind.EndsWith("/picture", StringComparison.OrdinalIgnoreCase))
        {
            body.Add(OfficeText.Picture(alt));
        }
    }

    private static string Table(List<List<string>> rows)
    {
        int width = rows.Max(r => r.Count);
        var sb = new StringBuilder();
        for (int i = 0; i < rows.Count; i++)
        {
            if (i > 0)
            {
                sb.Append('\n');
            }

            OfficeXml.TableRow(sb, rows[i], width);
            if (i == 0)
            {
                sb.Append('\n');
                OfficeXml.TableSeparator(sb, width);
            }
        }

        return sb.ToString();
    }

    /// <summary>The notes slide's body placeholder text.</summary>
    private string Notes(string part)
    {
        var lines = new List<string>();
        OfficePackage.Guard(part, () =>
        {
            using var reader = _package.Streamed(part);
            while (reader.Read())
            {
                if (_x.Is(reader, OfficeXml.P, "sp"))
                {
                    string? placeholder = null;
                    var text = new List<string>();
                    OfficeXml.Children(reader, () =>
                    {
                        if (_x.Is(reader, OfficeXml.P, "nvSpPr"))
                        {
                            placeholder = Placeholder(reader);
                        }
                        else if (_x.Is(reader, OfficeXml.P, "txBody"))
                        {
                            TextBody(reader, text, aloud: false);
                        }
                        else
                        {
                            OfficeXml.Skip(reader);
                        }
                    });

                    if (placeholder == "body")
                    {
                        lines.AddRange(text.Where(t => t.Length > 0));
                    }
                }
            }

            return 0;
        });

        return string.Join("\n", lines);
    }
}
