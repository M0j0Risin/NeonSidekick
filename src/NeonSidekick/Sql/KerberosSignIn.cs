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
/// sign-in failed instead.
///
/// <para>One instance per connection, which SqlClient may call for more than one handshake (code review of 522af7e, 2026-10-11): one
/// SPN after another when the first fails, a read-only intent's routing to an Availability Group secondary, the reconnect behind
/// <c>ConnectRetryCount</c> mid-batch. So each handshake gets a context of its own (<see cref="GenerateContext"/> starts afresh on an
/// empty incoming blob or another target, as SqlClient's own provider does after a failure), a failed one let go at once.
/// <see cref="Dispose"/> lets go of the one held; the object stays usable, since <see cref="SqlAccess"/> disposes it when the open
/// returns while the connection, still pointing at it, runs the batch.</para>
/// </summary>
public sealed class KerberosSignIn : SspiContextProvider, IDisposable
{
    /// <summary>The <see cref="NegotiateAuthenticationClientOptions.Package"/>: Kerberos alone, never an NTLM fallback.</summary>
    public const string Package = "Kerberos";

    private readonly NetworkCredential _credential;
    private NegotiateAuthentication? _auth;
    private string? _target;

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
        // A handshake's first step has nothing incoming: a new SPN, a routed or a reconnecting login. Each starts its own context.
        if (_auth is null || incomingBlob.IsEmpty || !string.Equals(_target, authParams.Resource, StringComparison.Ordinal))
        {
            Release();
            _target = authParams.Resource;
            _auth = new NegotiateAuthentication(new NegotiateAuthenticationClientOptions
            {
                Package = Package,
                Credential = _credential,
                TargetName = authParams.Resource,
            });
        }

        byte[]? blob = _auth.GetOutgoingBlob(incomingBlob, out var status);
        if (status is not (NegotiateAuthenticationStatusCode.Completed or NegotiateAuthenticationStatusCode.ContinueNeeded))
        {
            Failure = status;
            Release();
            return false;
        }

        if (blob is not null)
        {
            outgoingBlobWriter.Write(blob);
        }

        if (status == NegotiateAuthenticationStatusCode.Completed)
        {
            // Signed in: an earlier SPN's failure is no longer the story. The context is kept (a late server token still reaches it, as
            // in the spike's sign-ins) until Dispose or the next handshake's first step.
            Failure = null;
        }

        return true;
    }

    private void Release()
    {
        _auth?.Dispose();
        _auth = null;
        _target = null;
    }

    /// <summary>Lets go of a handshake left half-finished; the provider stays usable for a later one (see the class's remarks).</summary>
    public void Dispose() => Release();
}
