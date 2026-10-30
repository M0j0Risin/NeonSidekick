using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace NeonSidekick.Office;

/// <summary>
/// An Office file's zip, written (2026-10-29, the Office writing plan; the readers' twin, hand-written over <see cref="ZipArchive"/> and
/// <see cref="XmlWriter"/> since the Open XML SDK is AOT-unfriendly): parts written one after another (OPC takes them in any order), each
/// through an <see cref="XmlWriter"/> (UTF-8 with no BOM, characters checked) or as raw bytes; the content types and the relationships
/// gathered on the way and written last, once every part is known. The document's properties name no person: <c>dc:creator</c> and
/// <c>cp:lastModifiedBy</c> are left out, so no account or machine name rides along in a shared file (the pictures' metadata rule), and the
/// times (the properties' and every entry's) come from the injected clock. Transitional namespaces only. A part past 4 GB or a package
/// past 65,535 entries gets Zip64 from <see cref="ZipArchive"/> on its own. Not thread-safe; one write.
/// </summary>
public sealed class OfficePackageWriter : IDisposable
{
    public const string RelsNs = "http://schemas.openxmlformats.org/package/2006/relationships";
    public const string ContentTypesNs = "http://schemas.openxmlformats.org/package/2006/content-types";
    public const string RelTypeBase = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/";
    public const string CoreRelType = "http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties";
    public const string RNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    public const string RelsType = "application/vnd.openxmlformats-package.relationships+xml";
    public const string CoreType = "application/vnd.openxmlformats-package.core-properties+xml";
    public const string AppType = "application/vnd.openxmlformats-officedocument.extended-properties+xml";

    /// <summary>What <c>docProps/app.xml</c> names as the program that made the file.</summary>
    public const string Application = "NeonSidekick";

    private static readonly XmlWriterSettings Settings = new()
    {
        Encoding = new UTF8Encoding(false),
        CheckCharacters = true,
        CloseOutput = true,
        Indent = false,
    };

    private readonly ZipArchive _zip;
    private readonly DateTimeOffset _now;
    private readonly Dictionary<string, string> _defaults = new(StringComparer.OrdinalIgnoreCase) { ["rels"] = RelsType, ["xml"] = "application/xml" };
    private readonly List<(string Part, string Type)> _overrides = [];
    private readonly Dictionary<string, List<(string Id, string Type, string Target, bool External)>> _rels = new(StringComparer.Ordinal);
    private bool _finished;

    /// <param name="stream">Where the zip goes (left open): a <c>PendingWrite</c>'s stream.</param>
    public OfficePackageWriter(Stream stream, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(time);
        _zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
        _now = time.GetUtcNow();
    }

    /// <summary>The clock's time, as every part and entry carries it.</summary>
    public DateTimeOffset Now => _now;

    /// <summary>
    /// A new XML part <paramref name="name"/> (no leading <c>/</c>) of <paramref name="contentType"/> (null when an extension default covers
    /// it): the writer to fill and dispose before the next part is begun.
    /// </summary>
    public XmlWriter Part(string name, string? contentType)
    {
        var writer = XmlWriter.Create(Raw(name, contentType), Settings);
        writer.WriteStartDocument(standalone: true);
        return writer;
    }

    /// <summary>
    /// A new part's bytes (a picture, a copied entry): the stream to fill and dispose before the next part is begun. <paramref name="level"/>
    /// lets a copy store what is compressed already (2026-10-29, review P6-5).
    /// </summary>
    public Stream Raw(string name, string? contentType, CompressionLevel level = CompressionLevel.Optimal)
    {
        ArgumentNullException.ThrowIfNull(name);
        ObjectDisposedException.ThrowIf(_finished, this);
        if (contentType is not null)
        {
            _overrides.Add((name, contentType));
        }

        var entry = _zip.CreateEntry(name, level);
        entry.LastWriteTime = _now.ToLocalTime() < new DateTimeOffset(1980, 1, 2, 0, 0, 0, TimeSpan.Zero) ? new DateTimeOffset(1980, 1, 2, 0, 0, 0, TimeSpan.Zero) : _now;
        return entry.Open();
    }

    /// <summary>A content type for every part ending in <paramref name="extension"/> (no dot).</summary>
    public void Default(string extension, string contentType) => _defaults[extension] = contentType;

    /// <summary>A relationship of <paramref name="source"/> (<c>""</c> for the package): <paramref name="type"/> a short kind (<c>officeDocument</c>) or a full URI.</summary>
    public void Relate(string source, string id, string type, string target, bool external = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!_rels.TryGetValue(source, out var list))
        {
            _rels[source] = list = [];
        }

        list.Add((id, type.Contains(':', StringComparison.Ordinal) ? type : RelTypeBase + type, target, external));
    }

    /// <summary>The next free relationship id of <paramref name="source"/>: <c>rId1</c>, <c>rId2</c>….</summary>
    public string NextId(string source) =>
        "rId" + ((_rels.TryGetValue(source, out var list) ? list.Count : 0) + 1).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// <c>docProps/core.xml</c> (the title, the created and modified times as W3CDTF UTC, no person named) and <c>docProps/app.xml</c>
    /// (<see cref="Application"/>), with the package's relationships to them.
    /// </summary>
    public void Properties(string? title)
    {
        const string cp = "http://schemas.openxmlformats.org/package/2006/metadata/core-properties";
        const string dc = "http://purl.org/dc/elements/1.1/";
        const string dcterms = "http://purl.org/dc/terms/";
        const string xsi = "http://www.w3.org/2001/XMLSchema-instance";
        string stamp = _now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        using (var w = Part("docProps/core.xml", CoreType))
        {
            w.WriteStartElement("cp", "coreProperties", cp);
            w.WriteAttributeString("xmlns", "dc", null, dc);
            w.WriteAttributeString("xmlns", "dcterms", null, dcterms);
            w.WriteAttributeString("xmlns", "dcmitype", null, "http://purl.org/dc/dcmitype/");
            w.WriteAttributeString("xmlns", "xsi", null, xsi);
            if (!string.IsNullOrWhiteSpace(title))
            {
                w.WriteElementString("dc", "title", dc, XlsxValue.XmlSafe(title.Trim()));
            }

            foreach (string name in new[] { "created", "modified" })
            {
                w.WriteStartElement("dcterms", name, dcterms);
                w.WriteAttributeString("xsi", "type", xsi, "dcterms:W3CDTF");
                w.WriteString(stamp);
                w.WriteEndElement();
            }

            w.WriteEndElement();
        }

        using (var w = Part("docProps/app.xml", AppType))
        {
            w.WriteStartElement("Properties", "http://schemas.openxmlformats.org/officeDocument/2006/extended-properties");
            w.WriteAttributeString("xmlns", "vt", null, "http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes");
            w.WriteElementString("Application", "http://schemas.openxmlformats.org/officeDocument/2006/extended-properties", Application);
            w.WriteEndElement();
        }

        Relate("", NextId(""), CoreRelType, "docProps/core.xml");
        Relate("", NextId(""), "extended-properties", "docProps/app.xml");
    }

    /// <summary>The relationships parts and <c>[Content_Types].xml</c>, then the zip's directory: the package is done.</summary>
    public void Finish()
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        foreach (var (source, list) in _rels)
        {
            int slash = source.LastIndexOf('/');
            string part = source.Length == 0 ? "_rels/.rels" : slash < 0 ? "_rels/" + source + ".rels" : source[..slash] + "/_rels/" + source[(slash + 1)..] + ".rels";
            using var w = Part(part, null);
            w.WriteStartElement("Relationships", RelsNs);
            foreach (var (id, type, target, external) in list)
            {
                w.WriteStartElement("Relationship", RelsNs);
                w.WriteAttributeString("Id", id);
                w.WriteAttributeString("Type", type);
                w.WriteAttributeString("Target", target);
                if (external)
                {
                    w.WriteAttributeString("TargetMode", "External");
                }

                w.WriteEndElement();
            }

            w.WriteEndElement();
        }

        using (var w = Part("[Content_Types].xml", null))
        {
            w.WriteStartElement("Types", ContentTypesNs);
            foreach (var (extension, type) in _defaults)
            {
                w.WriteStartElement("Default", ContentTypesNs);
                w.WriteAttributeString("Extension", extension);
                w.WriteAttributeString("ContentType", type);
                w.WriteEndElement();
            }

            foreach (var (part, type) in _overrides)
            {
                w.WriteStartElement("Override", ContentTypesNs);
                w.WriteAttributeString("PartName", "/" + part);
                w.WriteAttributeString("ContentType", type);
                w.WriteEndElement();
            }

            w.WriteEndElement();
        }

        _finished = true;
        _zip.Dispose();
    }

    public void Dispose()
    {
        if (!_finished)
        {
            _finished = true;
            _zip.Dispose();
        }
    }
}
