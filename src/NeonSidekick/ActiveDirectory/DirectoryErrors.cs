using System.Globalization;

namespace NeonSidekick.ActiveDirectory;

/// <summary>
/// An LDAP result code as a sentence (2026-10-15, the admin-tools plan). <c>LDAP_SERVER_DOWN</c> names the server and port; invalid
/// credentials the account; a strong-auth refusal says signing is required (it should not happen: the bind signs and seals); an NTLM block from
/// this PC's policy says a joined PC or Kerberos is needed. Pure.
/// </summary>
public static class DirectoryErrors
{
    public static string Sentence(int code, string domain, string server, int port, string account, string detail) => code switch
    {
        0x51 or 0x5b => $"Error: {server}:{port.ToString(CultureInfo.InvariantCulture)} did not answer LDAP. Check the server's name, that it is a domain controller, and that the port is open.",
        0x31 when detail.Contains("NTLM", StringComparison.OrdinalIgnoreCase) => $"Error: this PC's policy blocks NTLM; a domain-joined PC or Kerberos is needed to read {domain}.",
        // The Mac's held refusal names the other cause too (2026-10-23, the admin review's D3): the KDC knew the account and gave no ticket.
        0x31 when detail.StartsWith(DirectoryText.NoTicketPrefix, StringComparison.Ordinal) => $"Error: {domain}'s KDC knows {account} but gave this Mac no ticket: most likely the password is wrong. It is not tried again until the entry's password changes or the app restarts, so the account is not locked out; if the password is right, the KDC and this Mac may share no encryption type (AES).",
        0x31 when SubCode(detail) is { } sub && SubCodeWords(sub) is { } why => $"Error: {domain} refused the sign-in as {account}: {why}.",
        0x31 => $"Error: {domain} refused the sign-in as {account}: the user name or password is wrong, or the account is locked or disabled.",
        Ldap.LdapResultCodes.AccountRevoked => $"Error: {domain} refused the sign-in as {account}: its KDC says the account is locked, disabled or expired. Once it is unlocked or enabled, try again.",
        0x08 => $"Error: {server} requires a signed connection and refused this one.",
        0x32 => $"Error: {account} may not read that in {domain}.",
        0x20 => $"Error: {domain} has no such object (check baseDn in ad.json).",
        0x55 or 0x03 => $"Error: {server} took too long to answer.",
        0x56 when OperatingSystem.IsMacOS() => $"Error: {server} offered no signed or sealed connection this Mac takes; on 389 the connection is always signed or sealed.",
        0x56 => $"Error: the sign-in to {domain} failed: no authentication method both sides know (Negotiate). From a PC outside the domain, NTLM must be allowed.",
        0x34 => $"Error: {server} is unavailable (it may be shutting down or not yet ready).",
        // The managed client's own (2026-10-18, the Mac AD plan): a Kerberos sign-in that never reached the bind.
        Ldap.LdapResultCodes.KdcUnreachable => $"Error: the Kerberos sign-in to {domain} could not reach its KDC on {server} (port 88). Check the network or VPN, and that the realm's KDC can be found (its DNS records, or a krb5.conf).",
        // The server's name first, kinit only for the Mac's own sign-in (2026-10-23, the admin review's V11: a runas login never uses the kinit
        // cache, and an SPN or server-name failure was sent to kinit).
        Ldap.LdapResultCodes.KerberosFailed => $"Error: the Kerberos sign-in to {domain} as {account} failed. Check that {server} is a DC of {domain} named by its own name (not an alias, localhost or an IP) and that the clock is within 5 minutes; for this Mac's own sign-in (no runas), run kinit for a ticket, the realm in capitals.",
        Ldap.LdapResultCodes.NotSealed => $"Error: the connection to {server} was not signed and sealed after the sign-in, so it was closed and nothing was read.",
        Ldap.LdapResultCodes.TlsUnanswered => $"Error: {server}:{port.ToString(CultureInfo.InvariantCulture)} did not answer LDAPS, or answered with a certificate this PC does not trust (Windows says the same for both). Check the name and the port, that the DC's certificate authority is trusted here, and that the certificate names the server as written.",
        // 2026-10-20, the code review: the DC's KDC answered with a time Kerberos refuses (detail: DirectoryText.ClockOff's phrase).
        Ldap.LdapResultCodes.ClockSkew => $"Error: this computer's clock is {detail} {server}'s, more than the 5 minutes Kerberos allows, so the sign-in to {domain} cannot work. Set the clock to the network's time and try again.",
        // 2026-10-20, the code review: the DC's KDC (port 88 on the address connected to) does not know the account; never held.
        Ldap.LdapResultCodes.UnknownAccount => $"Error: the KDC on {server} does not know {account}. Check the account and its realm (name@REALM), and that {server} is a DC of {domain} whose own KDC answers on port 88.",
        Ldap.LdapResultCodes.TlsRefused => $"Error: {server}:{port.ToString(CultureInfo.InvariantCulture)} answered, but its certificate was refused: {detail}. Trust the DC's certificate authority here (on a Mac, in the Keychain), and name the DC in server as its certificate does.",
        // The detail holds the server's own words (its diagnostic, its error text): fenced, one line, cut (2026-10-23, F5).
        _ => $"Error: the directory {domain} failed (LDAP code 0x{code.ToString("X2", CultureInfo.InvariantCulture)})" + (detail.Length > 0 ? ": " + Llm.Tools.UntrustedText.Fenced(Llm.Tools.UntrustedText.Cut(Llm.Tools.UntrustedText.OneLine(detail), 300)) : "."),
    };

    /// <summary>
    /// The sub-code in AD's server error text (2026-10-23, the admin review's J5, B8): <c>…AcceptSecurityContext error, data 52e, v4563</c>
    /// gives <c>52e</c>, lower case; null when the text has none.
    /// </summary>
    public static string? SubCode(string detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        var match = System.Text.RegularExpressions.Regex.Match(detail, @"\bdata ([0-9a-fA-F]{1,8})\b");
        return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
    }

    /// <summary>What a sub-code means, in a clause; null for one this does not know.</summary>
    public static string? SubCodeWords(string sub) => sub switch
    {
        "52e" => "the password is wrong (or the user name)",
        "525" => "no such user",
        "530" => "the account may not sign in at this time",
        "531" => "the account may not sign in from this computer",
        "532" => "the account's password has expired",
        "533" => "the account is disabled",
        "568" => "the account is in too many groups to sign in",
        "701" => "the account has expired",
        "773" => "the account must change its password before it can sign in",
        "775" => "the account is locked out. Once it is unlocked, try again",
        _ => null,
    };
}
