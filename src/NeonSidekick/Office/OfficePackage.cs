using System.IO.Compression;
using System.Xml;
using NeonSidekick.Files;

namespace NeonSidekick.Office;

/// <summary>
/// The size limits of one read (2026-10-28, the Office plan): the file, a part held whole in memory (shared strings, styles, the workbook,
/// relationships, numbering, comments, footnotes) and a streamed body (a worksheet, the document, a slide), whose bytes are counted as they
/// inflate. The tests pass small ones; the app reads with <see cref="Default"/>.
/// </summary>
public sealed record OfficeLimits(long MaxFileBytes, long MaxWholePartBytes, long MaxStreamedPartBytes)
{
    /// <summary>100 MB a file, 64 MB a part held whole, 1 GB a streamed body.</summary>
    public static readonly OfficeLimits Default = new(100L * 1024 * 1024, 64L * 1024 * 1024, 1024L * 1024 * 1024);
}

/// <summary>A part that is not a well-formed zip entry, not well-formed XML, missing though named, or indexed past its end.</summary>
public sealed class OfficeDamagedException(string part, Exception? inner = null) : Exception("damaged: " + part, inner)
{
    /// <summary>The part's name in the package (<c>xl/worksheets/sheet2.xml</c>).</summary>
    public string Part { get; } = part;
}

/// <summary>A part held whole that is larger than <see cref="OfficeLimits.MaxWholePartBytes"/>, or a zip bomb's.</summary>
public sealed class OfficePartTooLargeException(string part) : Exception("too large: " + part)
{
    public string Part { get; } = part;
}

/// <summary>A streamed body that inflated past <see cref="OfficeLimits.MaxStreamedPartBytes"/>: the reader keeps what it has.</summary>
public sealed class OfficeStreamLimitException(string part) : Exception("stream limit: " + part)
{
    public string Part { get; } = part;
}

/// <summary>One relationship of a part: its id, its type (a URI), the part it names (resolved inside the package) or, when external, the target as written.</summary>
public sealed record OfficeRel(string Id, string Type, string Target, bool External)
{
    /// <summary>Whether the type ends in <c>/<paramref name="kind"/></c>, Transitional and Strict alike.</summary>
    public bool Is(string kind) => Type.EndsWith("/" + kind, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// An Office file's zip, read-only (2026-10-28, the Office plan): parts found through their relationships, each opened through a stream
/// that counts what it inflates. A part held whole is refused past its cap or the zip-bomb ratio (<see cref="WorkingDirectory.MaxCompressionRatio"/>
/// past <see cref="WorkingDirectory.BombEntryBytes"/>, <c>unzip</c>'s rule), the count the real check since a Stored entry's header can lie;
/// a streamed body has no cap of its own but stops at the streamed bound. Nothing outside the zip is ever opened: an external relationship
/// is a string. Not thread-safe; one read.
/// </summary>
public sealed class OfficePackage : IDisposable
{
    /// <summary>The package's own relationships.</summary>
    public const string PackageRels = "_rels/.rels";

    private readonly ZipArchive _zip;
    private readonly OfficeLimits _limits;
    private readonly Dictionary<string, ZipArchiveEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<OfficeRel>> _rels = new(StringComparer.OrdinalIgnoreCase);

    private OfficePackage(ZipArchive zip, OfficeLimits limits)
    {
        _zip = zip;
        _limits = limits;
        foreach (var entry in zip.Entries)
        {
            _entries.TryAdd(entry.FullName.Replace('\\', '/').TrimStart('/'), entry);
        }
    }

    /// <summary>The zip over <paramref name="stream"/> (left open); a stream that isn't a readable zip throws <see cref="OfficeDamagedException"/>.</summary>
    public static OfficePackage Open(Stream stream, OfficeLimits limits)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(limits);
        try
        {
            return new OfficePackage(new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true), limits);
        }
        catch (InvalidDataException ex)
        {
            throw new OfficeDamagedException(OfficeText.ZipPart, ex);
        }
    }

    /// <summary>Whether the package holds <paramref name="part"/>.</summary>
    public bool Has(string part) => _entries.ContainsKey(part);

    /// <summary>The main part (<c>word/document.xml</c>, <c>xl/workbook.xml</c>, <c>ppt/presentation.xml</c>) the package's relationships name; null when none.</summary>
    public string? MainPart() =>
        Rels("").FirstOrDefault(r => !r.External && r.Is("officeDocument") && Has(r.Target))?.Target;

    /// <summary>
    /// <paramref name="part"/>'s relationships (<c>dir/_rels/name.rels</c>; <c>""</c> for the package's own), targets resolved inside the
    /// package; none when it has no relationships part. Read once.
    /// </summary>
    public IReadOnlyList<OfficeRel> Rels(string part)
    {
        if (_rels.TryGetValue(part, out var known))
        {
            return known;
        }

        string relsPart = part.Length == 0 ? PackageRels : RelsPartOf(part);
        var list = new List<OfficeRel>();
        if (Has(relsPart))
        {
            Guard(relsPart, () =>
            {
                using var reader = Whole(relsPart);
                while (reader.Read())
                {
                    if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "Relationship")
                    {
                        string id = reader.GetAttribute("Id") ?? "";
                        string type = reader.GetAttribute("Type") ?? "";
                        string target = reader.GetAttribute("Target") ?? "";
                        bool external = string.Equals(reader.GetAttribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase);
                        list.Add(new OfficeRel(id, type, external ? target : Resolve(part, target), external));
                    }
                }

                return 0;
            });
        }

        _rels[part] = list;
        return list;
    }

    /// <summary>The relationship <paramref name="id"/> of <paramref name="part"/>; null when it has none by that id.</summary>
    public OfficeRel? Rel(string part, string id) => Rels(part).FirstOrDefault(r => r.Id == id);

    /// <summary>The first part <paramref name="part"/> names by a relationship of <paramref name="kind"/> that the package holds; null when none.</summary>
    public string? Related(string part, string kind) => Rels(part).FirstOrDefault(r => !r.External && r.Is(kind) && Has(r.Target))?.Target;

    /// <summary>
    /// A part held whole: refused (<see cref="OfficePartTooLargeException"/>) when its header says it is over the cap or a bomb's ratio, and
    /// counted as it inflates, so a header that lies is caught there.
    /// </summary>
    public XmlReader Whole(string part)
    {
        var entry = Entry(part);
        if (entry.Length > _limits.MaxWholePartBytes
            || (entry.Length > WorkingDirectory.BombEntryBytes && entry.CompressedLength > 0 && entry.Length / entry.CompressedLength > WorkingDirectory.MaxCompressionRatio))
        {
            throw new OfficePartTooLargeException(part);
        }

        return Reader(part, entry, _limits.MaxWholePartBytes, streamed: false);
    }

    /// <summary>A streamed body: counted as it inflates, <see cref="OfficeStreamLimitException"/> past the streamed bound.</summary>
    public XmlReader Streamed(string part) => Reader(part, Entry(part), _limits.MaxStreamedPartBytes, streamed: true);

    /// <summary>
    /// <paramref name="read"/> with the ways a part can be broken turned into <see cref="OfficeDamagedException"/> naming it: XML that isn't
    /// well-formed (or holds a DTD), an entry that won't inflate, an end that comes too soon.
    /// </summary>
    public static T Guard<T>(string part, Func<T> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is XmlException or InvalidDataException or EndOfStreamException)
        {
            throw new OfficeDamagedException(part, ex);
        }
    }

    /// <summary>
    /// <paramref name="target"/> resolved against <paramref name="source"/>'s folder by OPC's rules: a leading <c>/</c> is the package's root,
    /// <c>..</c> and <c>.</c> folded, escapes undone. Never a file-system path.
    /// </summary>
    public static string Resolve(string source, string target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        string spelled = target.Replace('\\', '/');
        int hash = spelled.IndexOf('#', StringComparison.Ordinal);
        if (hash >= 0)
        {
            spelled = spelled[..hash];
        }

        try
        {
            spelled = Uri.UnescapeDataString(spelled);
        }
        catch (UriFormatException)
        {
            // Left as written.
        }

        string folder = source.Contains('/', StringComparison.Ordinal) ? source[..source.LastIndexOf('/')] : "";
        var parts = new List<string>();
        foreach (string segment in (spelled.StartsWith('/') ? spelled : folder + "/" + spelled).Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (parts.Count > 0)
                {
                    parts.RemoveAt(parts.Count - 1);
                }

                continue;
            }

            parts.Add(segment);
        }

        return string.Join('/', parts);
    }

    public void Dispose() => _zip.Dispose();

    private static string RelsPartOf(string part)
    {
        int slash = part.LastIndexOf('/');
        return slash < 0 ? "_rels/" + part + ".rels" : part[..slash] + "/_rels/" + part[(slash + 1)..] + ".rels";
    }

    private ZipArchiveEntry Entry(string part) =>
        _entries.TryGetValue(part, out var entry) ? entry : throw new OfficeDamagedException(part);

    private static readonly XmlReaderSettings Settings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        IgnoreWhitespace = false,
        CheckCharacters = true,
        CloseInput = true,
    };

    private static XmlReader Reader(string part, ZipArchiveEntry entry, long cap, bool streamed)
    {
        Stream inner;
        try
        {
            inner = entry.Open();
        }
        catch (InvalidDataException ex)
        {
            throw new OfficeDamagedException(part, ex);
        }

        return XmlReader.Create(new CountingStream(inner, part, cap, streamed), Settings);
    }

    /// <summary>
    /// The bytes a part inflates to, counted (2026-10-28, the Office plan: "sizes are counted, never trusted"): past <c>cap</c> it throws,
    /// <see cref="OfficeStreamLimitException"/> for a streamed body, <see cref="OfficePartTooLargeException"/> for a part held whole; an
    /// entry that won't inflate is <see cref="OfficeDamagedException"/>.
    /// </summary>
    private sealed class CountingStream(Stream inner, string part, long cap, bool streamed) : Stream
    {
        private long _count;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _count; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int read;
            try
            {
                read = inner.Read(buffer);
            }
            catch (InvalidDataException ex)
            {
                throw new OfficeDamagedException(part, ex);
            }

            _count += read;
            if (_count > cap)
            {
                throw streamed ? new OfficeStreamLimitException(part) : new OfficePartTooLargeException(part);
            }

            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
