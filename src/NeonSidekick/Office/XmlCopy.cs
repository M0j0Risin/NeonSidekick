using System.Xml;

namespace NeonSidekick.Office;

/// <summary>What a rewrite's element hook did with the element the reader stands on.</summary>
public enum XmlCopyStep
{
    /// <summary>Copy it as it is.</summary>
    Copy,

    /// <summary>The hook wrote its start (its attributes changed); its children and end are copied as usual.</summary>
    Started,

    /// <summary>The hook consumed it whole (dropped or rewritten), the reader left on its end.</summary>
    Skipped,
}

/// <summary>
/// Another program's XML part copied node by node (2026-10-29, the Office writing plan's faithful rewrite): every prefix and namespace
/// declaration exactly as written, since <c>mc:Ignorable="x14ac xr …"</c> names prefixes and a re-prefixed copy makes Excel call the file
/// corrupt; unknown elements and <c>extLst</c> passed through untouched. A hook sees each element first, another each end (an empty
/// element's too) before it is closed, so a caller inserts in the schema's order and recounts. Read with the readers' safe settings.
/// </summary>
public static class XmlCopy
{
    /// <param name="element">Each element's start, the reader on it: what was done with it.</param>
    /// <param name="end">Each element's end (local name, namespace, depth), before it is closed: where children are added last.</param>
    public static void Rewrite(byte[] source, XmlWriter w, Func<XmlReader, XmlCopyStep> element, Action<string, string, int> end)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(end);
        using var reader = XmlReader.Create(new MemoryStream(source, writable: false), OfficePackage.ReaderSettings);
        var open = new Stack<(string Local, string Ns, int Depth)>();
        while (reader.Read())
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                    var step = element(reader);
                    if (step == XmlCopyStep.Skipped)
                    {
                        break;
                    }

                    if (step == XmlCopyStep.Copy)
                    {
                        Start(reader, w);
                    }

                    if (reader.IsEmptyElement)
                    {
                        end(reader.LocalName, reader.NamespaceURI, reader.Depth);
                        w.WriteEndElement();
                    }
                    else
                    {
                        open.Push((reader.LocalName, reader.NamespaceURI, reader.Depth));
                    }

                    break;
                case XmlNodeType.EndElement:
                    var (local, ns, depth) = open.Pop();
                    end(local, ns, depth);
                    w.WriteFullEndElement();
                    break;
                case XmlNodeType.Text:
                    w.WriteString(reader.Value);
                    break;
                case XmlNodeType.CDATA:
                    w.WriteCData(reader.Value);
                    break;
                case XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace:
                    w.WriteWhitespace(reader.Value);
                    break;
            }
        }
    }

    /// <summary>The element's start as written, its attributes too, but those <paramref name="replace"/> gives (by local name, unqualified) another value.</summary>
    public static void Start(XmlReader reader, XmlWriter w, IReadOnlyDictionary<string, string>? replace = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(w);
        w.WriteStartElement(reader.Prefix, reader.LocalName, reader.NamespaceURI);
        HashSet<string>? seen = replace is null ? null : new(StringComparer.Ordinal);
        if (reader.MoveToFirstAttribute())
        {
            do
            {
                string value = reader.Value;
                if (replace is not null && reader.NamespaceURI.Length == 0 && replace.TryGetValue(reader.LocalName, out string? instead))
                {
                    value = instead;
                    seen!.Add(reader.LocalName);
                }

                w.WriteAttributeString(reader.Prefix, reader.LocalName, reader.NamespaceURI, value);
            }
            while (reader.MoveToNextAttribute());

            reader.MoveToElement();
        }

        // A key the element didn't carry is added (2026-10-29, review P4-1: a calcPr with no fullCalcOnLoad).
        foreach (var (key, value) in replace ?? new Dictionary<string, string>())
        {
            if (!seen!.Contains(key))
            {
                w.WriteAttributeString(key, value);
            }
        }
    }

    /// <summary>The element the reader stands on, as attributes and text, the reader left on its end: a <c>definedName</c>.</summary>
    public static (List<(string Prefix, string Local, string Ns, string Value)> Attributes, string Text) Capture(XmlReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var attributes = new List<(string, string, string, string)>();
        if (reader.MoveToFirstAttribute())
        {
            do
            {
                attributes.Add((reader.Prefix, reader.LocalName, reader.NamespaceURI, reader.Value));
            }
            while (reader.MoveToNextAttribute());

            reader.MoveToElement();
        }

        return (attributes, OfficeXml.Text(reader));
    }

    /// <summary>A captured element written back under <paramref name="local"/> in <paramref name="ns"/>.</summary>
    public static void Write(XmlWriter w, string local, string ns, IEnumerable<(string Prefix, string Local, string Ns, string Value)> attributes, string text)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(attributes);
        w.WriteStartElement(local, ns);
        foreach (var (prefix, name, space, value) in attributes)
        {
            if (space != "http://www.w3.org/2000/xmlns/")
            {
                w.WriteAttributeString(prefix, name, space, value);
            }
        }

        if (text.Length > 0)
        {
            w.WriteString(text);
        }

        w.WriteEndElement();
    }
}
