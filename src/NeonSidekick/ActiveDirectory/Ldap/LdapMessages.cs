using System.Formats.Asn1;
using System.Text;

namespace NeonSidekick.ActiveDirectory.Ldap;

/// <summary>
/// The result codes the managed client raises (2026-10-18, the Mac AD plan): the server's own LDAP result codes as they come, and for what
/// happens on this side the same numbers wldap32 gives, so <see cref="DirectoryErrors"/> words both backends alike. Two are this client's
/// own, for a Kerberos sign-in that never reached the bind.
/// </summary>
public static class LdapResultCodes
{
    public const int Success = 0;
    public const int TimeLimitExceeded = 0x03;
    public const int SizeLimitExceeded = 0x04;
    public const int Referral = 0x0A;
    public const int SaslBindInProgress = 0x0E;
    public const int NoSuchObject = 0x20;
    public const int InvalidCredentials = 0x31;

    /// <summary>wldap32's LDAP_SERVER_DOWN: the connection closed, or the server said it is leaving.</summary>
    public const int ServerDown = 0x51;

    /// <summary>wldap32's LDAP_DECODING_ERROR: a reply this client could not read, or one too large.</summary>
    public const int DecodingError = 0x54;

    /// <summary>wldap32's LDAP_TIMEOUT.</summary>
    public const int Timeout = 0x55;

    /// <summary>wldap32's LDAP_AUTH_UNKNOWN: no sign-in both sides know (here: the server offered no SASL layer this client takes).</summary>
    public const int AuthUnknown = 0x56;

    /// <summary>wldap32's LDAP_FILTER_ERROR.</summary>
    public const int FilterError = 0x57;

    /// <summary>wldap32's LDAP_CONNECT_ERROR.</summary>
    public const int ConnectError = 0x5B;

    /// <summary>This client's own: the Kerberos sign-in failed before the bind and the DC's KDC (port 88) could not be reached.</summary>
    public const int KdcUnreachable = 0x1001;

    /// <summary>This client's own: the Kerberos sign-in failed before the bind for another reason (no ticket, an unknown service name).</summary>
    public const int KerberosFailed = 0x1002;
}

/// <summary>Which reply a message is (RFC 4511's protocolOp tags).</summary>
public enum LdapOp
{
    BindResponse = 1,
    SearchResultEntry = 4,
    SearchResultDone = 5,
    SearchResultReference = 19,
    ExtendedResponse = 24,
}

/// <summary>
/// One reply read: its message id and kind; for a result, its code, diagnostic and (a bind's) SASL credentials or (a search's) paged
/// cookie; for an entry, its DN and attributes, each attribute's name as the server returned it (a <c>;range=</c> option kept).
/// </summary>
public sealed record LdapReply(
    int MessageId,
    LdapOp Op,
    int ResultCode = 0,
    string Diagnostic = "",
    byte[]? SaslCredentials = null,
    byte[]? Cookie = null,
    string? Dn = null,
    IReadOnlyList<(string Name, IReadOnlyList<byte[]> Values)>? Attributes = null);

/// <summary>
/// The LDAP messages the client sends and reads (2026-10-18, the Mac AD plan), BER through <see cref="AsnWriter"/>/<see cref="AsnReader"/>.
/// It writes only a SASL bind, a search and an unbind: no add, modify, delete, rename, compare or extended request exists here, which
/// <c>LdapManagedTests</c> pins by reading this file, so the client is read-only by construction. ENUMERATED values go through
/// <see cref="AsnWriter.WriteEncodedValue"/>, since <c>WriteInteger</c> refuses that tag (Phase 0).
/// </summary>
public static class LdapMessages
{
    /// <summary>The paged-results control (RFC 2696).</summary>
    public const string PagedResultsOid = "1.2.840.113556.1.4.319";

    /// <summary>The SASL mechanism.</summary>
    public const string Gssapi = "GSSAPI";

    /// <summary>A SASL GSSAPI bind with <paramref name="credentials"/> (empty for the exchange's empty response); no DN, no authorization id.</summary>
    public static byte[] Bind(int messageId, ReadOnlySpan<byte> credentials)
    {
        var writer = new AsnWriter(AsnEncodingRules.BER);
        using (writer.PushSequence())
        {
            writer.WriteInteger(messageId);
            using (writer.PushSequence(new Asn1Tag(TagClass.Application, 0, isConstructed: true)))
            {
                writer.WriteInteger(3);
                writer.WriteOctetString([]);
                using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 3, isConstructed: true)))
                {
                    writer.WriteOctetString(Encoding.UTF8.GetBytes(Gssapi));
                    writer.WriteOctetString(credentials);
                }
            }
        }

        return writer.Encode();
    }

    /// <summary>
    /// A search: aliases never dereferenced (wldap32's default), types and values, the attributes named verbatim (never <c>*</c>); with a page
    /// size, the paged-results control, not critical, so a server without paging answers unpaged under the size limit.
    /// </summary>
    public static byte[] Search(int messageId, string baseDn, DirectoryScope scope, string filter, IReadOnlyList<string> attributes, int sizeLimit, int timeLimitSeconds, int? pageSize = null, byte[]? cookie = null)
    {
        ArgumentNullException.ThrowIfNull(baseDn);
        ArgumentNullException.ThrowIfNull(attributes);
        byte[] encodedFilter = LdapFilterCodec.Encode(filter);
        var writer = new AsnWriter(AsnEncodingRules.BER);
        using (writer.PushSequence())
        {
            writer.WriteInteger(messageId);
            using (writer.PushSequence(new Asn1Tag(TagClass.Application, 3, isConstructed: true)))
            {
                writer.WriteOctetString(Encoding.UTF8.GetBytes(baseDn));
                WriteEnumerated(writer, (int)scope);
                WriteEnumerated(writer, 0);
                writer.WriteInteger(Math.Max(0, sizeLimit));
                writer.WriteInteger(Math.Max(0, timeLimitSeconds));
                writer.WriteBoolean(false);
                writer.WriteEncodedValue(encodedFilter);
                using (writer.PushSequence())
                {
                    foreach (string attribute in attributes)
                    {
                        writer.WriteOctetString(Encoding.UTF8.GetBytes(attribute));
                    }
                }
            }

            if (pageSize is { } size)
            {
                using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true)))
                using (writer.PushSequence())
                {
                    writer.WriteOctetString(Encoding.UTF8.GetBytes(PagedResultsOid));
                    var value = new AsnWriter(AsnEncodingRules.BER);
                    using (value.PushSequence())
                    {
                        value.WriteInteger(size);
                        value.WriteOctetString(cookie ?? []);
                    }

                    writer.WriteOctetString(value.Encode());
                }
            }
        }

        return writer.Encode();
    }

    /// <summary>The unbind (an application-tagged NULL).</summary>
    public static byte[] Unbind(int messageId)
    {
        var writer = new AsnWriter(AsnEncodingRules.BER);
        using (writer.PushSequence())
        {
            writer.WriteInteger(messageId);
            writer.WriteNull(new Asn1Tag(TagClass.Application, 2));
        }

        return writer.Encode();
    }

    /// <summary>
    /// The whole length of the BER message starting <paramref name="buffer"/> (header and content), once its header is all there; null while
    /// more bytes are needed. A length past <paramref name="max"/>, an indefinite length or a header that is no message is refused.
    /// </summary>
    public static int? MessageLength(ReadOnlySpan<byte> buffer, int max)
    {
        if (buffer.Length < 2)
        {
            return null;
        }

        if (buffer[0] != 0x30)
        {
            throw new DirectoryException(LdapResultCodes.DecodingError, "a reply that is not an LDAP message");
        }

        int first = buffer[1];
        if (first < 0x80)
        {
            return 2 + first;
        }

        int octets = first & 0x7F;
        if (octets is 0 or > 4)
        {
            throw new DirectoryException(LdapResultCodes.DecodingError, "a reply with an unsupported length");
        }

        if (buffer.Length < 2 + octets)
        {
            return null;
        }

        long length = 0;
        for (int i = 0; i < octets; i++)
        {
            length = (length << 8) | buffer[2 + i];
        }

        long total = 2 + octets + length;
        return total > max ? throw new DirectoryException(LdapResultCodes.DecodingError, "a reply larger than this client takes") : (int)total;
    }

    /// <summary>One whole message read.</summary>
    public static LdapReply Read(ReadOnlyMemory<byte> message)
    {
        try
        {
            var outer = new AsnReader(message, AsnEncodingRules.BER).ReadSequence();
            int id = (int)outer.ReadInteger();
            var tag = outer.PeekTag();
            if (tag.TagClass != TagClass.Application)
            {
                throw new DirectoryException(LdapResultCodes.DecodingError, "a reply with no protocol operation");
            }

            var op = (LdapOp)tag.TagValue;
            var body = outer.ReadEncodedValue();
            byte[]? cookie = outer.HasData ? Cookie(outer.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true))) : null;
            return op switch
            {
                LdapOp.SearchResultEntry => Entry(id, body),
                LdapOp.SearchResultReference => new LdapReply(id, op),
                LdapOp.BindResponse or LdapOp.SearchResultDone or LdapOp.ExtendedResponse => Result(id, op, body) with { Cookie = cookie },
                _ => throw new DirectoryException(LdapResultCodes.DecodingError, "a reply of an unexpected kind (" + tag.TagValue.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")"),
            };
        }
        catch (AsnContentException ex)
        {
            throw new DirectoryException(LdapResultCodes.DecodingError, "a reply that could not be read: " + ex.Message);
        }
    }

    private static LdapReply Result(int id, LdapOp op, ReadOnlyMemory<byte> body)
    {
        var reader = new AsnReader(body, AsnEncodingRules.BER).ReadSequence(new Asn1Tag(TagClass.Application, (int)op, isConstructed: true));
        int code = ReadEnumerated(reader);
        reader.ReadOctetString();
        string diagnostic = Encoding.UTF8.GetString(reader.ReadOctetString());
        byte[]? sasl = null;
        while (reader.HasData)
        {
            var next = reader.PeekTag();
            if (op == LdapOp.BindResponse && next.TagClass == TagClass.ContextSpecific && next.TagValue == 7)
            {
                sasl = reader.ReadOctetString(new Asn1Tag(TagClass.ContextSpecific, 7));
            }
            else
            {
                reader.ReadEncodedValue();   // a referral [3], an extended response's name or value
            }
        }

        return new LdapReply(id, op, code, diagnostic, sasl);
    }

    private static LdapReply Entry(int id, ReadOnlyMemory<byte> body)
    {
        var reader = new AsnReader(body, AsnEncodingRules.BER).ReadSequence(new Asn1Tag(TagClass.Application, (int)LdapOp.SearchResultEntry, isConstructed: true));
        string dn = Encoding.UTF8.GetString(reader.ReadOctetString());
        var list = reader.ReadSequence();
        var attributes = new List<(string, IReadOnlyList<byte[]>)>();
        while (list.HasData)
        {
            var attribute = list.ReadSequence();
            string name = Encoding.UTF8.GetString(attribute.ReadOctetString());
            var set = attribute.ReadSetOf(skipSortOrderValidation: true);
            var values = new List<byte[]>();
            while (set.HasData)
            {
                values.Add(set.ReadOctetString());
            }

            attributes.Add((name, values));
        }

        return new LdapReply(id, LdapOp.SearchResultEntry, Dn: dn, Attributes: attributes);
    }

    private static byte[]? Cookie(AsnReader controls)
    {
        byte[]? cookie = null;
        while (controls.HasData)
        {
            var control = controls.ReadSequence();
            string oid = Encoding.UTF8.GetString(control.ReadOctetString());
            if (control.HasData && control.PeekTag().HasSameClassAndValue(Asn1Tag.Boolean))
            {
                control.ReadBoolean();
            }

            byte[] value = control.HasData ? control.ReadOctetString() : [];
            if (oid == PagedResultsOid && value.Length > 0)
            {
                var paged = new AsnReader(value, AsnEncodingRules.BER).ReadSequence();
                paged.ReadInteger();
                cookie = paged.ReadOctetString();
            }
        }

        return cookie;
    }

    private static void WriteEnumerated(AsnWriter writer, int value)
    {
        var inner = new AsnWriter(AsnEncodingRules.BER);
        inner.WriteInteger(value);
        byte[] integer = inner.Encode();
        integer[0] = 0x0A;   // the same content under ENUMERATED's tag
        writer.WriteEncodedValue(integer);
    }

    private static int ReadEnumerated(AsnReader reader)
    {
        var bytes = reader.ReadEnumeratedBytes().Span;
        int value = bytes.Length > 0 && (bytes[0] & 0x80) != 0 ? -1 : 0;
        foreach (byte b in bytes)
        {
            value = (value << 8) | b;
        }

        return value;
    }
}
