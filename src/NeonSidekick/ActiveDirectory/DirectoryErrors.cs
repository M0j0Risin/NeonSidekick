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
        0x31 => $"Error: {domain} refused the sign-in as {account}: the user name or password is wrong, or the account is locked or disabled.",
        0x08 => $"Error: {server} requires a signed connection and refused this one.",
        0x32 => $"Error: {account} may not read that in {domain}.",
        0x20 => $"Error: {domain} has no such object (check baseDn in ad.json).",
        0x55 or 0x03 => $"Error: {server} took too long to answer.",
        0x56 when OperatingSystem.IsMacOS() => $"Error: {server} offered no signed or sealed connection this Mac takes; on 389 the connection is always signed or sealed.",
        0x56 => $"Error: the sign-in to {domain} failed: no authentication method both sides know (Negotiate). From a PC outside the domain, NTLM must be allowed.",
        0x34 => $"Error: {server} is unavailable (it may be shutting down or not yet ready).",
        // The managed client's own (2026-10-18, the Mac AD plan): a Kerberos sign-in that never reached the bind.
        Ldap.LdapResultCodes.KdcUnreachable => $"Error: the Kerberos sign-in to {domain} could not reach its KDC on {server} (port 88). Check the network or VPN, and that the realm's KDC can be found (its DNS records, or a krb5.conf).",
        Ldap.LdapResultCodes.KerberosFailed => $"Error: the Kerberos sign-in to {domain} as {account} failed. Run kinit for a ticket (the realm in capitals), check the clock is within 5 minutes, and that {server} is a DC of {domain}.",
        _ => $"Error: the directory {domain} failed (LDAP code 0x{code.ToString("X2", CultureInfo.InvariantCulture)})" + (detail.Length > 0 ? ": " + detail : "."),
    };
}
