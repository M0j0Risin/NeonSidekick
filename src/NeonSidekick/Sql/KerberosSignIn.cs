using System.Buffers;
using System.Net;
using System.Net.Security;
using Microsoft.Data.SqlClient;

namespace NeonSidekick.Sql;

/// <summary>
/// A <c>runas</c> connection's sign-in on a Mac (2026-10-11, the user's ask: keep the other account's password in the Keychain and
/// have the app get its ticket, no <c>kinit</c> by hand): SqlClient 7.1's <see cref="SqlConnection.SspiContextProvider"/> hook with
/// .NET's own <see cref="NegotiateAuthentication"/> given the account's <see cref="NetworkCredential"/>, which on macOS is GSS.framework's
/// <c>gss_acquire_cred_with_password</c> — in-process, so no process-start site (a <c>kinit</c> child would have been one). What the
/// spike against the user's QUAL server settled, NativeAOT-published:
/// <list type="bullet">
/// <item>It signs in as the account given, never the cache's default: a made-up account was refused although the user held a
/// valid ticket.</item>
/// <item>Package <c>Kerberos</c>, not <c>Negotiate</c>: under Negotiate a failed Kerberos sign-in fell back to NTLM with the
/// password, and the user asked for Kerberos.</item>
/// <item>macOS keeps what it gets: each sign-in leaves that account's ticket and the server's in its own cache of the system's
/// collection (<c>klist -l</c>), whatever <c>KRB5CCNAME</c> says; the default account (the <c>*</c>) stays as it was. The user
/// accepted that. Signing in as the default's own account renews the default's ticket.</item>
/// <item>The realm goes upper case (<see cref="Principal"/>): a lower-case one signed in too, but left an empty cache under that
/// spelling each time.</item>
/// </list>
/// When the sign-in fails here SqlClient says only "The target principal name is incorrect. Cannot generate SSPI context.", which
/// BUILD.md reads as a trust or SPN problem; <see cref="Failure"/> keeps .NET's status so <see cref="SqlAccess"/> can say whose
/// sign-in failed instead. One instance per connection open, disposed after it.
/// </summary>
public sealed class KerberosSignIn : SspiContextProvider, IDisposable
{
    /// <summary>The <see cref="NegotiateAuthenticationClientOptions.Package"/>: Kerberos alone, never an NTLM fallback.</summary>
    public const string Package = "Kerberos";

    private readonly NetworkCredential _credential;
    private NegotiateAuthentication? _auth;

    public KerberosSignIn(string principal, string password)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(password);
        _credential = new NetworkCredential(principal, password);
    }

    /// <summary>Where <c>runas</c> signs in this way: macOS (Windows has its netonly token; elsewhere it is untried).</summary>
    public static bool Offered => OperatingSystem.IsMacOS();

    /// <summary>The status of the step that failed, or null while none has.</summary>
    public NegotiateAuthenticationStatusCode? Failure { get; private set; }

    /// <summary>
    /// <paramref name="account"/> as a Kerberos principal: <c>name@realm</c> with the realm in capitals (Active Directory's always are).
    /// Null for anything else — <c>DOMAIN\name</c> above all, since a NetBIOS domain is no realm a Mac can find a KDC for.
    /// </summary>
    public static string? Principal(string? account)
    {
        string text = account?.Trim() ?? "";
        int at = text.IndexOf('@');
        if (at <= 0 || at == text.Length - 1 || text.IndexOf('@', at + 1) >= 0 || text.Contains('\\') || text.Any(char.IsWhiteSpace))
        {
            return null;
        }

        return text[..at] + "@" + text[(at + 1)..].ToUpperInvariant();
    }

    protected override bool GenerateContext(ReadOnlySpan<byte> incomingBlob, IBufferWriter<byte> outgoingBlobWriter, SspiAuthenticationParameters authParams)
    {
        ArgumentNullException.ThrowIfNull(outgoingBlobWriter);
        ArgumentNullException.ThrowIfNull(authParams);
        _auth ??= new NegotiateAuthentication(new NegotiateAuthenticationClientOptions
        {
            Package = Package,
            Credential = _credential,
            TargetName = authParams.Resource,
        });
        byte[]? blob = _auth.GetOutgoingBlob(incomingBlob, out var status);
        if (status is not (NegotiateAuthenticationStatusCode.Completed or NegotiateAuthenticationStatusCode.ContinueNeeded))
        {
            Failure = status;
            return false;
        }

        if (blob is not null)
        {
            outgoingBlobWriter.Write(blob);
        }

        return true;
    }

    public void Dispose() => _auth?.Dispose();
}
