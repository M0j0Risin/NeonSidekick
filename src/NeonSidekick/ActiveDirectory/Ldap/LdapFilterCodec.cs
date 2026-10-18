using System.Formats.Asn1;
using System.Text;

namespace NeonSidekick.ActiveDirectory.Ldap;

/// <summary>
/// An RFC 4515 filter string as RFC 4511's BER <c>Filter</c> (2026-10-18, the Mac AD plan): what <see cref="LdapFilter"/> and
/// <see cref="DirectoryQueries"/> build — <c>&amp;</c>, <c>|</c>, <c>!</c>, equality, <c>&gt;=</c>, <c>&lt;=</c>, <c>~=</c>, present
/// (<c>=*</c>), substrings, and extensible match (<c>userAccountControl:1.2.840.113556.1.4.803:=2</c>). A value's <c>\XX</c> escapes become
/// its bytes, so a binary value such as an <c>objectSid</c> survives; other characters are UTF-8. Anything malformed, or nested past
/// <see cref="MaxDepth"/>, is a <see cref="DirectoryException"/> before a byte is sent. Pure.
/// </summary>
public static class LdapFilterCodec
{
    /// <summary>How deep <c>&amp;</c>, <c>|</c> and <c>!</c> may nest.</summary>
    public const int MaxDepth = 32;

    /// <summary>The longest filter taken.</summary>
    public const int MaxLength = 64 * 1024;

    /// <summary>The filter's BER encoding.</summary>
    public static byte[] Encode(string filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (filter.Length is 0 or > MaxLength)
        {
            throw Malformed(filter, "empty or too long");
        }

        var writer = new AsnWriter(AsnEncodingRules.BER);
        int at = 0;
        WriteFilter(writer, filter, ref at, 0);
        if (at != filter.Length)
        {
            throw Malformed(filter, "text after the filter's end");
        }

        return writer.Encode();
    }

    /// <summary>Writes the BER encoding of <paramref name="filter"/> into <paramref name="writer"/> (a SearchRequest's filter field).</summary>
    public static void Write(AsnWriter writer, string filter)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteEncodedValue(Encode(filter));
    }

    private static void WriteFilter(AsnWriter writer, string text, ref int at, int depth)
    {
        if (depth > MaxDepth)
        {
            throw Malformed(text, "nested too deep");
        }

        Expect(text, ref at, '(');
        if (at >= text.Length)
        {
            throw Malformed(text, "unfinished");
        }

        switch (text[at])
        {
            case '&':
            case '|':
            {
                int tag = text[at] == '&' ? 0 : 1;
                at++;
                using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, tag, isConstructed: true)))
                {
                    int count = 0;
                    while (at < text.Length && text[at] == '(')
                    {
                        WriteFilter(writer, text, ref at, depth + 1);
                        count++;
                    }

                    if (count == 0)
                    {
                        throw Malformed(text, "an empty " + (tag == 0 ? "&" : "|"));
                    }
                }

                break;
            }

            case '!':
                at++;
                using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 2, isConstructed: true)))
                {
                    WriteFilter(writer, text, ref at, depth + 1);
                }

                break;
            default:
                WriteItem(writer, text, ref at);
                break;
        }

        Expect(text, ref at, ')');
    }

    private static void WriteItem(AsnWriter writer, string text, ref int at)
    {
        int start = at;
        while (at < text.Length && text[at] is not ('=' or '~' or '>' or '<' or ':' or '(' or ')'))
        {
            at++;
        }

        string attribute = text[start..at];
        if (at >= text.Length)
        {
            throw Malformed(text, "an item without an operator");
        }

        if (text[at] == ':')
        {
            WriteExtensible(writer, text, ref at, attribute);
            return;
        }

        if (!IsAttribute(attribute))
        {
            throw Malformed(text, "a bad attribute name '" + attribute + "'");
        }

        int kind;
        if (text[at] == '=')
        {
            kind = 3;
            at++;
        }
        else
        {
            kind = text[at] switch { '~' => 8, '>' => 5, _ => 6 };
            at++;
            Expect(text, ref at, '=');
        }

        int valueStart = at;
        while (at < text.Length && text[at] != ')')
        {
            if (text[at] == '(')
            {
                throw Malformed(text, "an unescaped '('");
            }

            at++;
        }

        string raw = text[valueStart..at];
        if (kind == 3 && raw == "*")
        {
            writer.WriteOctetString(Encoding.UTF8.GetBytes(attribute), new Asn1Tag(TagClass.ContextSpecific, 7));
            return;
        }

        if (kind == 3 && raw.Contains('*', StringComparison.Ordinal))
        {
            WriteSubstrings(writer, text, attribute, raw);
            return;
        }

        using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, kind, isConstructed: true)))
        {
            writer.WriteOctetString(Encoding.UTF8.GetBytes(attribute));
            writer.WriteOctetString(Unescape(text, raw));
        }
    }

    private static void WriteSubstrings(AsnWriter writer, string text, string attribute, string raw)
    {
        string[] parts = raw.Split('*');
        using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 4, isConstructed: true)))
        {
            writer.WriteOctetString(Encoding.UTF8.GetBytes(attribute));
            using (writer.PushSequence())
            {
                for (int i = 0; i < parts.Length; i++)
                {
                    if (parts[i].Length == 0)
                    {
                        if (i > 0 && i < parts.Length - 1)
                        {
                            throw Malformed(text, "'**' in a value");
                        }

                        continue;
                    }

                    int tag = i == 0 ? 0 : i == parts.Length - 1 ? 2 : 1;
                    writer.WriteOctetString(Unescape(text, parts[i]), new Asn1Tag(TagClass.ContextSpecific, tag));
                }
            }
        }
    }

    /// <summary><c>attr[:dn][:rule]:=value</c>, or <c>[:dn]:rule:=value</c>.</summary>
    private static void WriteExtensible(AsnWriter writer, string text, ref int at, string attribute)
    {
        string? rule = null;
        bool dn = false;
        bool assigned = false;
        while (at < text.Length && text[at] == ':')
        {
            at++;
            if (at < text.Length && text[at] == '=')
            {
                at++;
                assigned = true;
                break;
            }

            int start = at;
            while (at < text.Length && text[at] is not (':' or ')' or '('))
            {
                at++;
            }

            string part = text[start..at];
            if (string.Equals(part, "dn", StringComparison.OrdinalIgnoreCase))
            {
                dn = true;
            }
            else if (rule is null && IsAttribute(part))
            {
                rule = part;
            }
            else
            {
                throw Malformed(text, "a bad extensible match '" + part + "'");
            }
        }

        if (!assigned)
        {
            throw Malformed(text, "an extensible match without ':='");
        }

        if (attribute.Length > 0 && !IsAttribute(attribute))
        {
            throw Malformed(text, "a bad attribute name '" + attribute + "'");
        }

        if (attribute.Length == 0 && rule is null)
        {
            throw Malformed(text, "an extensible match with neither attribute nor rule");
        }

        int valueStart = at;
        while (at < text.Length && text[at] != ')')
        {
            at++;
        }

        byte[] value = Unescape(text, text[valueStart..at]);
        using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 9, isConstructed: true)))
        {
            if (rule is not null)
            {
                writer.WriteOctetString(Encoding.UTF8.GetBytes(rule), new Asn1Tag(TagClass.ContextSpecific, 1));
            }

            if (attribute.Length > 0)
            {
                writer.WriteOctetString(Encoding.UTF8.GetBytes(attribute), new Asn1Tag(TagClass.ContextSpecific, 2));
            }

            writer.WriteOctetString(value, new Asn1Tag(TagClass.ContextSpecific, 3));
            if (dn)
            {
                writer.WriteBoolean(true, new Asn1Tag(TagClass.ContextSpecific, 4));
            }
        }
    }

    /// <summary>A value's bytes: <c>\XX</c> as that byte, anything else as UTF-8; a bare <c>\</c> or a bad hex pair is refused.</summary>
    public static byte[] Unescape(string text, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var bytes = new List<byte>(value.Length);
        var run = new StringBuilder();
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\')
            {
                run.Append(value[i]);
                continue;
            }

            if (i + 2 >= value.Length || !IsHex(value[i + 1]) || !IsHex(value[i + 2]))
            {
                throw Malformed(text, "a '\\' without two hex digits");
            }

            bytes.AddRange(Encoding.UTF8.GetBytes(run.ToString()));
            run.Clear();
            bytes.Add(Convert.ToByte(value.Substring(i + 1, 2), 16));
            i += 2;
        }

        bytes.AddRange(Encoding.UTF8.GetBytes(run.ToString()));
        return [.. bytes];
    }

    private static bool IsHex(char c) => c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F');

    /// <summary>An attribute description or OID: letters, digits, <c>-</c>, <c>.</c> and <c>;</c> options.</summary>
    private static bool IsAttribute(string name) =>
        name.Length > 0 && name.Length <= 256 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or ';');

    private static void Expect(string text, ref int at, char c)
    {
        if (at >= text.Length || text[at] != c)
        {
            throw Malformed(text, "'" + c + "' expected at " + at.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        at++;
    }

    private static DirectoryException Malformed(string filter, string why) =>
        new(LdapResultCodes.FilterError, "the filter could not be encoded (" + why + "): " + (filter.Length > 200 ? filter[..200] + "…" : filter));
}
