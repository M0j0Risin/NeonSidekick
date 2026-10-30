using System.Globalization;
using System.IO.Compression;
using System.Xml;
using NeonSidekick.Files;

namespace NeonSidekick.Office;

/// <summary>One sheet of a workbook being added to: its name, its <c>sheetId</c>, its relationship and the part that holds it.</summary>
public sealed record WorkbookSheet(string Name, long SheetId, string RelId, string? Part, bool Worksheet);

/// <summary>
/// One sheet added to an existing <c>.xlsx</c>, or one plain sheet replaced (2026-10-29, the Office writing plan). <see cref="Open"/> reads
/// what is there the way <c>read_office</c> opens a file (<see cref="OfficePackage"/>, its size limits) and refuses what can't be written
/// to: a file that isn't a zip (an encrypted workbook is a compound file), Strict OOXML, a workbook whose structure is locked, a damaged or
/// oversized part. <see cref="Start"/> then writes the new package: every other entry copied as stored (names as they are, the bytes
/// re-deflated through <see cref="OfficePackage.CopyTo"/>'s counted read); <c>xl/workbook.xml</c> gaining its <c>sheet</c>, its relationships
/// one more, <c>[Content_Types].xml</c> an override and <c>xl/styles.xml</c> our fonts, number formats (ids clear of theirs) and
/// <c>cellXfs</c> appended with the counts recomputed, each rewritten faithfully (<see cref="XmlCopy"/>), insertions in the schema's order;
/// the workbook's own date system (<c>date1904</c>) kept, so serials match. A replaced sheet keeps its position, <c>sheetId</c> and
/// <c>r:id</c>; only a plain one is replaced (no chart, table, picture, comment or pivot table of its own); <c>xl/calcChain.xml</c> is
/// dropped (Excel rebuilds it), as is that sheet's filter name; <c>docProps/app.xml</c> is left as it is.
/// </summary>
public sealed class WorkbookAppender : IWorkbookJob
{
    private const string Ns = SheetWriter.Ns;

    /// <summary><c>CT_Workbook</c>'s elements after <c>definedNames</c>: the new one goes before the first of these.</summary>
    private static readonly HashSet<string> AfterDefinedNames = ["calcPr", "oleSize", "customWorkbookViews", "pivotCaches", "smartTagPr", "smartTagTypes",
        "webPublishing", "fileRecoveryPr", "webPublishObjects", "extLst"];

    /// <summary>A replaced sheet's relationships that belong to its old cells alone and go with them.</summary>
    private static readonly string[] Droppable = ["printerSettings", "hyperlink"];

    /// <summary>
    /// What the bold header font keeps of the workbook's first font (2026-10-29, review P1-14b: an underline, a strike or an outline came along
    /// with the size and the face).
    /// </summary>
    private static readonly HashSet<string> HeaderFontParts = new(StringComparer.Ordinal) { "sz", "color", "name", "family", "charset", "scheme" };

    private readonly string _display;
    private readonly string _main;
    private readonly string _mainStored;
    private readonly string? _relsStored;
    private readonly string _typesStored;
    private readonly string? _stylesPart;
    private readonly string? _calcChainPart;
    private readonly byte[] _workbook;
    private readonly byte[]? _rels;
    private readonly byte[] _types;
    private readonly byte[]? _styles;
    private readonly List<string> _stored;
    private readonly Dictionary<string, string> _partOf = new(StringComparer.Ordinal);
    private readonly HashSet<string> _overrides;
    private string _ownStylesPart = "";
    private readonly List<int> _formatIds;
    private readonly int _fontCount;
    private readonly int _xfCount;
    private readonly int _fillCount;
    private readonly int _borderCount;
    private readonly int _styleXfCount;
    private readonly List<(string Local, string Ns, List<(string Prefix, string Local, string Ns, string Value)> Attributes)> _firstFont;
    private readonly IReadOnlyList<OfficeRel> _workbookRels;
    private OfficePackage? _source;
    private OfficePackageWriter? _package;
    private XmlWriter? _sheetXml;
    private SheetWriter? _sheet;
    private string _sheetName = "";
    private string _sheetPart = "";
    private string _sheetRelId = "";
    private int _position;
    private bool _replace;
    private string? _droppedSheetRels;
    private int _firstFormat;

    private WorkbookAppender(OfficePackage source, string display, string main, bool date1904, List<WorkbookSheet> sheets, byte[] workbook)
    {
        _source = source;
        _display = display;
        _main = main;
        _mainStored = source.StoredName(main)!;
        Date1904 = date1904;
        Sheets = sheets;
        _workbook = workbook;
        _stored = [.. source.StoredNames];
        foreach (string stored in _stored)
        {
            _partOf[stored] = OfficePackage.PartOf(stored);
        }

        _workbookRels = source.Rels(main);
        string relsPart = RelsPartOf(main);
        _relsStored = source.StoredName(relsPart);
        _rels = _relsStored is null ? null : source.Bytes(relsPart);
        _typesStored = source.StoredName("[Content_Types].xml") ?? throw new OfficeDamagedException("[Content_Types].xml");
        _types = source.Bytes("[Content_Types].xml");
        // Read here, so damage is a refusal before anything is written (2026-10-29, review P7-1), and every part it names counts as taken.
        _overrides = OfficePackage.Guard("[Content_Types].xml", () => OverridesOf(_types));
        _stylesPart = source.Related(main, "styles");
        _calcChainPart = source.Related(main, "calcChain");
        _styles = _stylesPart is null ? null : source.Bytes(_stylesPart);
        (_formatIds, _fontCount, _xfCount, _fillCount, _borderCount, _styleXfCount, _firstFont) = _styles is null ? ([], 0, 0, 0, 0, 0, []) : ScanStyles(_styles, _stylesPart!);
    }

    /// <summary>The workbook's sheets, in their order.</summary>
    public IReadOnlyList<WorkbookSheet> Sheets { get; }

    /// <summary>Whether the workbook counts dates from 1904.</summary>
    public bool Date1904 { get; }

    /// <summary>The sheet named <paramref name="name"/>, ignoring case; null when there is none.</summary>
    public WorkbookSheet? Find(string name) => Sheets.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public SheetWriter Sheet => _sheet ?? throw new InvalidOperationException("Start comes first.");

    /// <summary>
    /// The workbook in <paramref name="existing"/> (left open, read until <see cref="Finish"/> or disposal) judged and read; null with the
    /// refusal when it can't be added to. <paramref name="display"/> names it in the refusals.
    /// </summary>
    public static WorkbookAppender? Open(Stream existing, string display, OfficeLimits limits, out string? refusal) =>
        Open(existing, display, limits, out refusal, out _);

    /// <summary>
    /// As <see cref="Open(Stream, string, OfficeLimits, out string?)"/>, saying whether the refusal is that the file is no workbook at all (no
    /// workbook part, another document's root, damaged past reading): such a file is replaced whole with <c>overwrite</c>, as one that isn't a
    /// zip (2026-10-29, review P2-2: a <c>.xlsx</c> that was really a Word file was refused even with <c>overwrite: true</c>).
    /// </summary>
    public static WorkbookAppender? Open(Stream existing, string display, OfficeLimits limits, out string? refusal, out bool notAWorkbook)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(limits);
        refusal = null;
        notAWorkbook = false;
        if (existing.Length > limits.MaxFileBytes)
        {
            refusal = OfficeText.TooBig(display, limits.MaxFileBytes);
            return null;
        }

        Span<byte> head = stackalloc byte[8];
        int read = existing.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        existing.Position = 0;
        if (!OfficeFile.LooksLikeZip(head[..read]))
        {
            notAWorkbook = true;
            refusal = OfficeText.NotAWorkbook(display, OfficeFile.LooksLikeCompoundFile(head[..read]));
            return null;
        }

        OfficePackage? package = null;
        try
        {
            package = OfficePackage.Open(existing, limits);
            string? main = package.MainPart();
            if (main is null)
            {
                notAWorkbook = true;
                refusal = OfficeText.NotAWorkbook(display, encrypted: false);
                return null;
            }

            byte[] workbook = package.Bytes(main);
            var x = new OfficeXml();
            bool strict = false;
            bool isWorkbook = false;
            bool locked = false;
            bool reserved = false;
            bool date1904 = false;
            var sheets = new List<WorkbookSheet>();
            var rels = package.Rels(main);
            OfficePackage.Guard(main, () =>
            {
                using var reader = XmlReader.Create(new MemoryStream(workbook, writable: false), OfficePackage.ReaderSettings);
                reader.MoveToContent();
                isWorkbook = reader.LocalName == "workbook" && x.Key(reader.NamespaceURI) == OfficeXml.S;
                strict = reader.NamespaceURI.StartsWith("http://purl.oclc.org/ooxml/", StringComparison.Ordinal)
                    || string.Equals(reader.GetAttribute("conformance"), "strict", StringComparison.OrdinalIgnoreCase);
                if (!isWorkbook || strict)
                {
                    return 0;
                }

                OfficeXml.Children(reader, () =>
                {
                    if (x.Is(reader, OfficeXml.S, "workbookPr"))
                    {
                        date1904 = OfficeXml.On(x.Attr(reader, "date1904"), false);
                        OfficeXml.Skip(reader);
                    }
                    else if (x.Is(reader, OfficeXml.S, "fileSharing"))
                    {
                        // A password to modify (2026-10-29, review P4-5): Excel opens it read-only without one, so it is not written over.
                        reserved = x.Attr(reader, "reservationPassword") is not null || x.Attr(reader, "hashValue") is not null || x.Attr(reader, "algorithmName") is not null;
                        OfficeXml.Skip(reader);
                    }
                    else if (x.Is(reader, OfficeXml.S, "workbookProtection"))
                    {
                        locked = OfficeXml.On(x.Attr(reader, "lockStructure"), false) || OfficeXml.On(x.Attr(reader, "lockRevision"), false);
                        OfficeXml.Skip(reader);
                    }
                    else if (x.Is(reader, OfficeXml.S, "sheets"))
                    {
                        OfficeXml.Children(reader, () =>
                        {
                            if (x.Is(reader, OfficeXml.S, "sheet"))
                            {
                                string id = x.Attr(reader, "id", OfficeXml.R) ?? "";
                                var rel = rels.FirstOrDefault(r => r.Id == id && !r.External);
                                sheets.Add(new WorkbookSheet(
                                    x.Attr(reader, "name") ?? "",
                                    long.TryParse(x.Attr(reader, "sheetId"), NumberStyles.None, CultureInfo.InvariantCulture, out long sheetId) ? sheetId : 0,
                                    id,
                                    rel?.Target is { } target && package.Has(target) ? target : null,
                                    rel?.Is("worksheet") == true));
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

            if (strict)
            {
                refusal = OfficeText.StrictWorkbook(display);
                return null;
            }

            if (!isWorkbook)
            {
                notAWorkbook = true;
                refusal = OfficeText.NotAWorkbook(display, encrypted: false);
                return null;
            }

            if (reserved)
            {
                refusal = OfficeText.WriteReserved(display);
                return null;
            }

            if (locked)
            {
                refusal = OfficeText.StructureLocked(display);
                return null;
            }

            if (HoldsMacros(package))
            {
                refusal = OfficeText.MacroWorkbook(display);
                return null;
            }

            if (UnsafeEntry(package) is { } unsafeEntry)
            {
                refusal = OfficeText.BadEntryName(display, unsafeEntry);
                return null;
            }

            var appender = new WorkbookAppender(package, display, main, date1904, sheets, workbook);
            package = null;
            return appender;
        }
        catch (Exception ex) when (ex is OfficeReadException or IOException or UnauthorizedAccessException)
        {
            notAWorkbook = ex is OfficeDamagedException;
            refusal = OfficeReader.Refusal(ex, display);
            return null;
        }
        finally
        {
            package?.Dispose();
        }
    }

    /// <summary>
    /// Whether the package holds macros (2026-10-30, review F93: TOOLS.md promises a macro workbook isn't changed, and nothing looked): a
    /// <c>vbaProject.bin</c> part anywhere, or a macro-enabled content type in <c>[Content_Types].xml</c>.
    /// </summary>
    private static bool HoldsMacros(OfficePackage package)
    {
        if (package.StoredNames.Any(name => name.EndsWith("vbaProject.bin", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return package.StoredName("[Content_Types].xml") is not null
            && System.Text.Encoding.UTF8.GetString(package.Bytes("[Content_Types].xml")).Contains("macroEnabled", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The first entry that makes the package unsafe to copy (2026-10-30, review F62/F83/F90): a name repeated, exactly or in another case, once
    /// read as the reader reads it (a backslash a slash, a leading slash gone), or one with a <c>..</c> segment or a drive letter. The reader
    /// kept the first of a repeat and the copy wrote both; a name reaching outside the package is no part. Null when every name is sound.
    /// </summary>
    private static string? UnsafeEntry(OfficePackage package)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string stored in package.StoredNames)
        {
            string part = OfficePackage.PartOf(stored);
            bool drive = part.Length >= 2 && part[1] == ':' && char.IsAsciiLetter(part[0]);
            if (!seen.Add(part) || drive || part.Split('/').Contains(".."))
            {
                return stored;
            }
        }

        return null;
    }

    /// <summary>
    /// Why the workbook can't be replaced whole (2026-10-30, review F93: <c>overwrite</c> with no sheet replaced a protected workbook
    /// unread): a protected sheet; null when none is. <see cref="Open(Stream, string, OfficeLimits, out string?, out bool)"/> has already
    /// refused a password to modify, a protected structure and macros.
    /// </summary>
    public string? ReplaceWholeProblem()
    {
        var source = _source ?? throw new ObjectDisposedException(nameof(WorkbookAppender));
        try
        {
            return Sheets.FirstOrDefault(s => s.Worksheet && s.Part is not null && Protected(source, s.Part)) is { } sheet
                ? OfficeText.ProtectedSheetInside(_display, sheet.Name)
                : null;
        }
        catch (OfficeReadException ex)
        {
            return OfficeReader.Refusal(ex, _display);
        }
    }

    /// <summary>
    /// Why <paramref name="sheet"/> can't be replaced: a chart sheet, or a sheet with parts of its own beyond its cells; null when it is a
    /// plain worksheet. The refusal suggests a new sheet's name.
    /// </summary>
    public string? ReplaceProblem(WorkbookSheet sheet)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        string suggestion = SheetName.Fit(sheet.Name + " new", Sheets.Select(s => s.Name));
        if (!sheet.Worksheet || sheet.Part is null)
        {
            return OfficeText.NotAWorksheet(_display, sheet.Name, suggestion);
        }

        try
        {
            var own = _source!.Rels(sheet.Part).Where(r => !Droppable.Any(r.Is)).ToList();
            if (own.Count > 0)
            {
                return OfficeText.SheetNotPlain(_display, sheet.Name, OfficeText.SheetPartName(own[0].Type), suggestion);
            }

            return Protected(_source, sheet.Part) ? OfficeText.SheetProtected(_display, sheet.Name, suggestion) : null;
        }
        catch (OfficeReadException ex)
        {
            return OfficeReader.Refusal(ex, _display);
        }
    }

    /// <summary>
    /// Whether the sheet in <paramref name="part"/> is protected (<c>sheetProtection sheet="1"</c>), read as a stream with its cells skipped
    /// (2026-10-29, review P4-5: a replace dropped the protection with the cells).
    /// </summary>
    private static bool Protected(OfficePackage source, string part) => OfficePackage.Guard(part, () =>
    {
        var x = new OfficeXml();
        bool found = false;
        using var reader = source.Streamed(part);
        reader.MoveToContent();
        OfficeXml.Children(reader, () =>
        {
            if (x.Is(reader, OfficeXml.S, "sheetProtection"))
            {
                found |= OfficeXml.On(x.Attr(reader, "sheet"), false);
            }

            OfficeXml.Skip(reader);
        });
        return found;
    });

    /// <summary>The part names <c>[Content_Types].xml</c>'s overrides name, without the leading <c>/</c>.</summary>
    private static HashSet<string> OverridesOf(byte[] types)
    {
        var parts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = XmlReader.Create(new MemoryStream(types, writable: false), OfficePackage.ReaderSettings);
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "Override" && reader.GetAttribute("PartName") is { } name)
            {
                parts.Add(name.TrimStart('/'));
            }
        }

        return parts;
    }

    /// <summary>
    /// The new package begun in <paramref name="output"/> (left open): the other entries copied, the styles rewritten, the sheet open for
    /// rows. <paramref name="replace"/> names the sheet replaced (judged by <see cref="ReplaceProblem"/> first); null adds <paramref name="name"/>.
    /// A part that fails to read is the refusal, nothing written past it.
    /// </summary>
    public string? Start(Stream output, string name, WorkbookSheet? replace, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(name);
        var source = _source ?? throw new ObjectDisposedException(nameof(WorkbookAppender));
        _replace = replace is not null;
        if (replace is not null)
        {
            _sheetName = replace.Name;
            _sheetPart = replace.Part!;
            _sheetRelId = replace.RelId;
            _position = Sheets.ToList().IndexOf(replace);
            _droppedSheetRels = source.StoredName(RelsPartOf(_sheetPart));
        }
        else
        {
            _sheetName = name;
            _position = Sheets.Count;
            _sheetPart = FreePart("worksheets/sheet", 1);

            var ids = new HashSet<string>(_workbookRels.Select(r => r.Id), StringComparer.OrdinalIgnoreCase);
            for (int n = 1; ; n++)
            {
                string candidate = "rId" + n.ToString(CultureInfo.InvariantCulture);
                if (!ids.Contains(candidate))
                {
                    _sheetRelId = candidate;
                    break;
                }
            }
        }

        _firstFormat = Math.Max(ExcelWriter.FirstFormatId, _formatIds.Count == 0 ? 0 : _formatIds.Max() + 1);
        if (_stylesPart is null)
        {
            // A free name (2026-10-29, review P1-14a: a stray styles.xml with no relationship to it made a second entry of that name).
            _ownStylesPart = FreePart("styles", 0);
        }

        try
        {
            _package = new OfficePackageWriter(output, time);
            // Every entry goes out under its part name (2026-10-30, review F62/F83/F90): a backslash or a leading slash, which the reader takes,
            // is never written back, and the skip set compares those names ignoring case, as the reader finds parts (Open refused a repeat).
            var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { OfficePackage.PartOf(_mainStored), OfficePackage.PartOf(_typesStored) };
            foreach (string? stored in new[] { _relsStored, _stylesPart is null ? null : source.StoredName(_stylesPart), _calcChainPart is null ? null : source.StoredName(_calcChainPart), _droppedSheetRels, _replace ? source.StoredName(_sheetPart) : null })
            {
                if (stored is not null)
                {
                    skip.Add(OfficePackage.PartOf(stored));
                }
            }

            foreach (string stored in _stored.Where(s => !skip.Contains(OfficePackage.PartOf(s))))
            {
                using var target = _package.Raw(OfficePackage.PartOf(stored), null, CopyLevel(source.Stored(stored)));
                if (!stored.EndsWith('/'))
                {
                    source.CopyTo(stored, target);
                }
            }

            int header;
            string stylesStored = OfficePackage.PartOf(_stylesPart is null ? _ownStylesPart : source.StoredName(_stylesPart)!);
            header = OfficePackage.Guard(stylesStored, () =>
            {
                using var w = _package!.Part(stylesStored, null);
                return WriteStyles(w);
            });

            _sheetXml = _package.Part(OfficePackage.PartOf(_replace ? source.StoredName(_sheetPart)! : _sheetPart), null);
            _sheet = new SheetWriter(_sheetXml, new SheetStyles(header, header + 1, header + 2, header + 3, header + 4, header + 5), Date1904, selected: false);
            return null;
        }
        catch (Exception ex) when (ex is OfficeReadException or IOException or UnauthorizedAccessException)
        {
            return OfficeReader.Refusal(ex, _display);
        }
    }

    public SheetResult Finish()
    {
        var package = _package ?? throw new InvalidOperationException("Start comes first.");
        var result = Sheet.Finish();
        _sheetXml!.Dispose();
        // Each rewrite's damage is the part's (2026-10-29, review P7-1): an OfficeDamagedException both callers answer, never a raw XmlException.
        OfficePackage.Guard(_mainStored, () =>
        {
            using var w = package.Part(OfficePackage.PartOf(_mainStored), null);
            WriteWorkbook(w, result.FilterRange);
            return 0;
        });
        string rels = OfficePackage.PartOf(_relsStored ?? RelsPartOf(_main));
        OfficePackage.Guard(rels, () =>
        {
            using var w = package.Part(rels, null);
            WriteRels(w);
            return 0;
        });
        OfficePackage.Guard(_typesStored, () =>
        {
            using var w = package.Part(OfficePackage.PartOf(_typesStored), null);
            WriteTypes(w);
            return 0;
        });

        package.Dispose();
        _source?.Dispose();
        _source = null;
        return result;
    }

    public void Dispose()
    {
        _sheetXml?.Dispose();
        _package?.Dispose();
        _source?.Dispose();
        _source = null;
    }

    private static string RelsPartOf(string part)
    {
        int slash = part.LastIndexOf('/');
        return slash < 0 ? "_rels/" + part + ".rels" : part[..slash] + "/_rels/" + part[(slash + 1)..] + ".rels";
    }

    /// <summary>
    /// A part beside the workbook, <paramref name="stem"/> and a number from <paramref name="from"/> (0: none the first time), whose name, its
    /// relationships' name and no override of <c>[Content_Types].xml</c> is taken, ignoring case.
    /// </summary>
    private string FreePart(string stem, int from)
    {
        string folder = _main.Contains('/', StringComparison.Ordinal) ? _main[..(_main.LastIndexOf('/') + 1)] : "";
        var taken = new HashSet<string>(_partOf.Values, StringComparer.OrdinalIgnoreCase);
        taken.UnionWith(_overrides);
        for (int n = from; ; n++)
        {
            if (n == 1 && from == 0)
            {
                continue;   // styles.xml, then styles2.xml
            }

            string candidate = folder + stem + (n == 0 ? "" : n.ToString(CultureInfo.InvariantCulture)) + ".xml";
            if (!taken.Contains(candidate) && !taken.Contains(RelsPartOf(candidate)))
            {
                return candidate;
            }
        }
    }

    /// <summary>The target of a part beside the workbook, as its relationships write it.</summary>
    private string TargetOf(string part)
    {
        string folder = _main.Contains('/', StringComparison.Ordinal) ? _main[..(_main.LastIndexOf('/') + 1)] : "";
        return part.StartsWith(folder, StringComparison.Ordinal) ? part[folder.Length..] : "/" + part;
    }

    /// <summary>
    /// The styles part counted: its number formats' ids, and how many fonts, cell formats, fills, borders and cell style formats it holds (the
    /// last three since 2026-10-30, review F18, so <see cref="WriteStyles"/> knows which collections it must add), and its first font's parts.
    /// </summary>
    private static (List<int> Formats, int Fonts, int Xfs, int Fills, int Borders, int StyleXfs, List<(string, string, List<(string, string, string, string)>)> FirstFont) ScanStyles(byte[] styles, string part)
    {
        var x = new OfficeXml();
        var formats = new List<int>();
        int fonts = 0;
        int xfs = 0;
        int fills = 0;
        int borders = 0;
        int styleXfs = 0;
        var first = new List<(string, string, List<(string, string, string, string)>)>();
        OfficePackage.Guard(part, () =>
        {
            using var reader = XmlReader.Create(new MemoryStream(styles, writable: false), OfficePackage.ReaderSettings);
            reader.MoveToContent();
            OfficeXml.Children(reader, () =>
            {
                if (x.Is(reader, OfficeXml.S, "numFmts"))
                {
                    OfficeXml.Children(reader, () =>
                    {
                        if (x.Is(reader, OfficeXml.S, "numFmt") && int.TryParse(x.Attr(reader, "numFmtId"), NumberStyles.None, CultureInfo.InvariantCulture, out int id))
                        {
                            formats.Add(id);
                        }

                        OfficeXml.Skip(reader);
                    });
                }
                else if (x.Is(reader, OfficeXml.S, "fonts"))
                {
                    OfficeXml.Children(reader, () =>
                    {
                        if (fonts++ == 0)
                        {
                            OfficeXml.Children(reader, () =>
                            {
                                first.Add((reader.LocalName, reader.NamespaceURI, XmlCopy.Capture(reader).Attributes));
                            });
                        }
                        else
                        {
                            OfficeXml.Skip(reader);
                        }
                    });
                }
                else if (x.Is(reader, OfficeXml.S, "cellXfs") || x.Is(reader, OfficeXml.S, "fills") || x.Is(reader, OfficeXml.S, "borders") || x.Is(reader, OfficeXml.S, "cellStyleXfs"))
                {
                    string collection = reader.LocalName;
                    OfficeXml.Children(reader, () =>
                    {
                        switch (collection)
                        {
                            case "cellXfs": xfs++; break;
                            case "fills": fills++; break;
                            case "borders": borders++; break;
                            default: styleXfs++; break;
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
        return (formats, fonts, xfs, fills, borders, styleXfs, first);
    }

    /// <summary>The collections <see cref="WriteStyles"/> adds to or makes, in CT_Stylesheet's order (2026-10-30, review F18).</summary>
    private static readonly string[] StyleCollections = ["numFmts", "fonts", "fills", "borders", "cellStyleXfs", "cellXfs"];

    /// <summary>Every child of CT_Stylesheet in its order: a missing collection is made before the first element that comes after it.</summary>
    private static readonly string[] StyleOrder = [.. StyleCollections, "cellStyles", "dxfs", "tableStyles", "colors", "extLst"];

    /// <summary>
    /// The styles, theirs with ours appended; the header's <c>cellXfs</c> index, the four formats' and the whole number's after it. Never
    /// replaced whole (2026-10-30, review F18/F116: a part without fonts or cell formats was swapped for ours, dropping its <c>dxfs</c>, which
    /// conditional formats name by index): a missing collection is made in its place in CT_Stylesheet's order, an empty one gets the defaults
    /// the added cell formats point at (a plain font and cell format, the <c>none</c> and <c>gray125</c> fills, an empty border, a cell style
    /// format), and everything else — <c>cellStyles</c>, <c>dxfs</c>, <c>tableStyles</c>, colors, extensions — is kept as it is.
    /// </summary>
    private int WriteStyles(XmlWriter w)
    {
        if (_styles is null)
        {
            WriteOwnStyles(w);
            return 1;
        }

        bool plainFont = _fontCount == 0;
        bool plainXf = _xfCount == 0;
        int headerFont = plainFont ? 1 : _fontCount;
        int header = plainXf ? 1 : _xfCount;
        var written = new HashSet<string>(StringComparer.Ordinal);
        var keys = new OfficeXml();

        // Each collection's count once ours are in.
        int CountOf(string collection) => collection switch
        {
            "numFmts" => _formatIds.Count + ExcelWriter.FormatCodes.Count,
            "fonts" => _fontCount + (plainFont ? 2 : 1),
            "fills" => _fillCount == 0 ? 2 : _fillCount,
            "borders" => _borderCount == 0 ? 1 : _borderCount,
            "cellStyleXfs" => _styleXfCount == 0 ? 1 : _styleXfCount,
            _ => _xfCount + (plainXf ? 1 : 0) + 2 + ExcelWriter.FormatCodes.Count,
        };

        // The collections that come before position `before` and aren't there, made whole.
        void Missing(int before, string ns)
        {
            for (int i = 0; i < Math.Min(before, StyleCollections.Length); i++)
            {
                string collection = StyleCollections[i];
                if (written.Add(collection))
                {
                    w.WriteStartElement(collection, ns);
                    w.WriteAttributeString("count", CountOf(collection).ToString(CultureInfo.InvariantCulture));
                    Fill(w, collection, ns, plainFont, plainXf, headerFont);
                    w.WriteEndElement();
                }
            }
        }

        XmlCopy.Rewrite(_styles, w, reader =>
        {
            if (reader.Depth != 1 || keys.Key(reader.NamespaceURI) != OfficeXml.S || Array.IndexOf(StyleOrder, reader.LocalName) is not (>= 0 and var at))
            {
                return XmlCopyStep.Copy;
            }

            Missing(at, reader.NamespaceURI);
            if (at >= StyleCollections.Length || !written.Add(reader.LocalName))
            {
                return XmlCopyStep.Copy;
            }

            return Started(reader, w, CountOf(reader.LocalName));
        }, (local, ns, depth) =>
        {
            if (depth == 0)
            {
                // A stylesheet whose children all come earlier, or that has none: what is still missing goes last.
                Missing(StyleCollections.Length, ns);
                return;
            }

            if (depth == 1 && keys.Key(ns) == OfficeXml.S)
            {
                Fill(w, local, ns, plainFont, plainXf, headerFont);
            }
        });
        return header;
    }

    /// <summary>
    /// What one of <see cref="StyleCollections"/> gets at its end: the formats, the header font (after a plain one when it had none), the default
    /// fills, border and cell style format when it had none, and the cell formats (after a plain one when it had none).
    /// </summary>
    private void Fill(XmlWriter w, string local, string ns, bool plainFont, bool plainXf, int headerFont)
    {
        switch (local)
        {
            case "numFmts":
                WriteFormats(w);
                break;
            case "fonts":
                if (plainFont)
                {
                    ExcelWriter.Font(w, bold: false);
                }

                w.WriteStartElement("font", ns);
                w.WriteStartElement("b", ns);
                w.WriteEndElement();
                foreach (var (name, space, attributes) in _firstFont.Where(f => HeaderFontParts.Contains(f.Local)))
                {
                    XmlCopy.Write(w, name, space, attributes, "");
                }

                w.WriteEndElement();
                break;
            case "fills" when _fillCount == 0:
                ExcelWriter.DefaultFills(w);
                break;
            case "borders" when _borderCount == 0:
                ExcelWriter.DefaultBorder(w);
                break;
            case "cellStyleXfs" when _styleXfCount == 0:
                ExcelWriter.DefaultStyleXf(w);
                break;
            case "cellXfs":
                if (plainXf)
                {
                    ExcelWriter.Xf(w, 0, 0);
                }

                ExcelWriter.Xf(w, 0, headerFont);
                for (int i = 0; i < ExcelWriter.FormatCodes.Count; i++)
                {
                    ExcelWriter.Xf(w, _firstFormat + i, 0);
                }

                ExcelWriter.Xf(w, ExcelWriter.IntegerFormatId, 0);
                break;
        }
    }

    /// <summary>
    /// How a copied entry is compressed (2026-10-29, review P6-5: every one was deflated again at Optimal): stored when it barely compressed
    /// (a picture, an embedded file: its own compression already), else Fastest, since its bytes were the source's choice to begin with.
    /// </summary>
    public static CompressionLevel CopyLevel((long Length, long Compressed)? sizes) =>
        sizes is { Length: > 0 } s && s.Compressed >= s.Length * 0.95 ? CompressionLevel.NoCompression : CompressionLevel.Fastest;

    private static XmlCopyStep Started(XmlReader reader, XmlWriter w, int count)
    {
        XmlCopy.Start(reader, w, Count(count));
        return XmlCopyStep.Started;
    }

    private static Dictionary<string, string> Count(int count) => new() { ["count"] = count.ToString(CultureInfo.InvariantCulture) };

    private void WriteFormats(XmlWriter w)
    {
        for (int i = 0; i < ExcelWriter.FormatCodes.Count; i++)
        {
            ExcelWriter.NumFmt(w, _firstFormat + i, ExcelWriter.FormatCodes[i]);
        }
    }

    /// <summary>A new workbook's styles part, the one shape both share: its header is <c>cellXfs</c> 1, its formats from 164. Only for a workbook with no styles part.</summary>
    private void WriteOwnStyles(XmlWriter w)
    {
        _firstFormat = ExcelWriter.FirstFormatId;
        ExcelWriter.WriteStyles(w);
    }

    private void WriteWorkbook(XmlWriter w, string filterRange)
    {
        bool namesWritten = false;
        // A replaced sheet marks the workbook to recalculate on open (2026-10-29, review P4-1: another sheet's formulas over it kept their old
        // values until a change); calcPr follows definedNames in CT_Workbook, so one is written there when the workbook has none.
        bool calcDone = !_replace;
        var keys = new OfficeXml();
        XmlCopy.Rewrite(_workbook, w, reader =>
        {
            if (reader.Depth != 1)
            {
                return XmlCopyStep.Copy;
            }

            bool main = keys.Key(reader.NamespaceURI) == OfficeXml.S;
            if (main && reader.LocalName == "definedNames")
            {
                namesWritten = true;
                string ns = reader.NamespaceURI;
                var names = new List<(List<(string, string, string, string)> Attributes, string Text)>();
                OfficeXml.Children(reader, () =>
                {
                    if (reader.LocalName == "definedName")
                    {
                        names.Add(XmlCopy.Capture(reader));
                    }
                    else
                    {
                        OfficeXml.Skip(reader);
                    }
                });
                WriteNames(w, ns, names, filterRange);
                return XmlCopyStep.Skipped;
            }

            if (main && !namesWritten && AfterDefinedNames.Contains(reader.LocalName))
            {
                namesWritten = true;
                WriteNames(w, reader.NamespaceURI, [], filterRange);
            }

            if (main && !calcDone && reader.LocalName == "calcPr")
            {
                calcDone = true;
                XmlCopy.Start(reader, w, FullCalc);
                return XmlCopyStep.Started;
            }

            if (main && !calcDone && namesWritten && AfterDefinedNames.Contains(reader.LocalName))
            {
                calcDone = true;
                WriteCalc(w, reader.NamespaceURI);
            }

            return XmlCopyStep.Copy;
        }, (local, ns, depth) =>
        {
            if (depth == 1 && local == "sheets" && !_replace)
            {
                w.WriteStartElement("sheet", ns);
                w.WriteAttributeString("name", _sheetName);
                w.WriteAttributeString("sheetId", ((Sheets.Count == 0 ? 0 : Sheets.Max(s => s.SheetId)) + 1).ToString(CultureInfo.InvariantCulture));
                string rPrefix = w.LookupPrefix(OfficePackageWriter.RNs) ?? "r";
                w.WriteAttributeString(rPrefix, "id", OfficePackageWriter.RNs, _sheetRelId);
                w.WriteEndElement();
            }
            else if (depth == 0)
            {
                if (!namesWritten)
                {
                    namesWritten = true;
                    WriteNames(w, ns, [], filterRange);
                }

                if (!calcDone)
                {
                    calcDone = true;
                    WriteCalc(w, ns);
                }
            }
        });
    }

    private static readonly Dictionary<string, string> FullCalc = new(StringComparer.Ordinal) { ["fullCalcOnLoad"] = "1" };

    private static void WriteCalc(XmlWriter w, string ns)
    {
        w.WriteStartElement("calcPr", ns);
        w.WriteAttributeString("fullCalcOnLoad", "1");
        w.WriteEndElement();
    }

    /// <summary>The defined names: theirs (a replaced sheet's filter name left out), then the new sheet's filter name.</summary>
    private void WriteNames(XmlWriter w, string ns, List<(List<(string, string, string, string)> Attributes, string Text)> names, string filterRange)
    {
        string position = _position.ToString(CultureInfo.InvariantCulture);
        w.WriteStartElement("definedNames", ns);
        foreach (var (attributes, text) in names)
        {
            bool filter = attributes.Any(a => a.Item2 == "name" && a.Item3.Length == 0 && a.Item4 == "_xlnm._FilterDatabase");
            bool here = attributes.Any(a => a.Item2 == "localSheetId" && a.Item3.Length == 0 && a.Item4.Trim() == position);
            if (filter && here)
            {
                continue;
            }

            XmlCopy.Write(w, "definedName", ns, attributes, text);
        }

        ExcelWriter.FilterName(w, _position, _sheetName, filterRange);
        w.WriteEndElement();
    }

    private void WriteRels(XmlWriter w)
    {
        if (_rels is null)
        {
            w.WriteStartElement("Relationships", OfficePackageWriter.RelsNs);
            NewRels(w, OfficePackageWriter.RelsNs);
            w.WriteEndElement();
            return;
        }

        XmlCopy.Rewrite(_rels, w, reader =>
        {
            if (reader.LocalName == "Relationship" && (reader.GetAttribute("Type") ?? "").EndsWith("/calcChain", StringComparison.OrdinalIgnoreCase))
            {
                OfficeXml.Skip(reader);
                return XmlCopyStep.Skipped;
            }

            return XmlCopyStep.Copy;
        }, (local, ns, depth) =>
        {
            if (depth == 0)
            {
                NewRels(w, ns);
            }
        });
    }

    private void NewRels(XmlWriter w, string ns)
    {
        void Rel(string id, string type, string target)
        {
            w.WriteStartElement("Relationship", ns);
            w.WriteAttributeString("Id", id);
            w.WriteAttributeString("Type", OfficePackageWriter.RelTypeBase + type);
            w.WriteAttributeString("Target", target);
            w.WriteEndElement();
        }

        if (!_replace)
        {
            Rel(_sheetRelId, "worksheet", TargetOf(_sheetPart));
        }

        if (_stylesPart is null)
        {
            var ids = new HashSet<string>(_workbookRels.Select(r => r.Id).Append(_sheetRelId), StringComparer.OrdinalIgnoreCase);
            int n = 1;
            while (ids.Contains("rId" + n.ToString(CultureInfo.InvariantCulture)))
            {
                n++;
            }

            Rel("rId" + n.ToString(CultureInfo.InvariantCulture), "styles", TargetOf(_ownStylesPart));
        }
    }

    private void WriteTypes(XmlWriter w)
    {
        string? calcChain = _calcChainPart is null ? null : "/" + _calcChainPart;
        XmlCopy.Rewrite(_types, w, reader =>
        {
            if (calcChain is not null && reader.LocalName == "Override" && string.Equals(reader.GetAttribute("PartName"), calcChain, StringComparison.OrdinalIgnoreCase))
            {
                OfficeXml.Skip(reader);
                return XmlCopyStep.Skipped;
            }

            return XmlCopyStep.Copy;
        }, (local, ns, depth) =>
        {
            if (depth != 0)
            {
                return;
            }

            void Override(string part, string type)
            {
                w.WriteStartElement("Override", ns);
                w.WriteAttributeString("PartName", "/" + part);
                w.WriteAttributeString("ContentType", type);
                w.WriteEndElement();
            }

            if (!_replace)
            {
                Override(_sheetPart, SheetWriter.ContentType);
            }

            if (_stylesPart is null)
            {
                Override(_ownStylesPart, ExcelWriter.StylesType);
            }
        });
    }
}
