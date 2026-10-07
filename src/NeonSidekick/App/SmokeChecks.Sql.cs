using System.Globalization;
using Microsoft.Data.SqlClient;
using NeonSidekick.Sql;

namespace NeonSidekick.App;

public static partial class SmokeChecks
{
    /// <summary>
    /// <c>sql:parse-and-sni</c> (2026-09-23): the SQL tools' two libraries on the published binary, with no server.
    /// ScriptDom parses a SELECT the gate lets through and refuses the <c>mcp-mssql-read</c> bypass
    /// <c>SELECT 1 DELETE FROM t</c>; then SqlClient opens a named pipe nothing listens on, with a one-second
    /// timeout — a <see cref="SqlException"/> naming the pipe provider is the proof that the native SNI layer
    /// (<see cref="SqlAccess.NativeLibraryFileName"/>) loaded and the globalization SqlClient demands is there
    /// (the exe under <c>InvariantGlobalization</c> threw <c>NotSupportedException</c> from the constructor in the
    /// spike); a <see cref="DllNotFoundException"/>, a type-initializer or a not-supported failure is the break.
    /// SqlClient declares no AOT support: the JIT proves nothing, this line does.
    /// </summary>
    public static SmokeCheck ProbeSql()
    {
        const string name = "sql:parse-and-sni";
        if (SqlReadOnlyGate.Check("WITH c AS (SELECT 1 AS x) SELECT x FROM c") is { } refused)
        {
            return new SmokeCheck(name, false, "the gate refused a plain CTE: " + refused);
        }

        if (SqlReadOnlyGate.Check("SELECT 1 DELETE FROM t") is null)
        {
            return new SmokeCheck(name, false, "the gate let SELECT 1 DELETE FROM t through");
        }

        // Off Windows (2026-10-06, the macOS build) SqlClient's SNI is managed and has no named pipes, and integrated
        // sign-in would be Kerberos: a TCP port nobody serves on the loopback, signed in by name, is the same proof there.
        var builder = OperatingSystem.IsWindows()
            ? new SqlConnectionStringBuilder
            {
                DataSource = @"np:\\.\pipe\neonsidekick-smoke-" + Guid.NewGuid().ToString("N") + @"\sql\query",
                IntegratedSecurity = true,
                ConnectTimeout = 1,
                Encrypt = SqlConnectionEncryptOption.Optional,
                Pooling = false,
            }
            : new SqlConnectionStringBuilder
            {
                DataSource = "tcp:127.0.0.1,9",
                UserID = "smoke",
                Password = "smoke",
                ConnectTimeout = 1,
                Encrypt = SqlConnectionEncryptOption.Optional,
                Pooling = false,
            };
        try
        {
            using var connection = new SqlConnection(builder.ConnectionString);
            connection.Open();
            return new SmokeCheck(name, false, "a pipe nobody serves accepted a connection");
        }
        catch (SqlException ex)
        {
            return new SmokeCheck(name, true, $"ScriptDom gate ok; SqlClient {typeof(SqlConnection).Assembly.GetName().Version} answered error {ex.Number.ToString(CultureInfo.InvariantCulture)} through the {(OperatingSystem.IsWindows() ? "native" : "managed")} SNI");
        }
        catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException or NotSupportedException or EntryPointNotFoundException or BadImageFormatException)
        {
            return new SmokeCheck(name, false, ex.GetType().Name + ": " + Diagnostics.LogText.Excerpt(ex.Message));
        }
    }

    /// <summary>
    /// <c>sql:credentials</c> (later on 2026-09-23): the SQL tools' three Win32 doors on the published binary
    /// (<see cref="WindowsCredentials"/>, <c>LibraryImport</c> over typed pointers — the first place their marshalling
    /// runs for real). A DPAPI round trip; a Generic credential written, read back and deleted under a throwaway target;
    /// and a <c>NEW_CREDENTIALS</c> logon for an account that does not exist (Windows checks nothing at that logon —
    /// the server would, at the sign-in) with the process's own name unchanged inside the impersonation, which is what
    /// <c>runas /netonly</c> promises: the local identity stays, only network sign-ins change.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static SmokeCheck ProbeCredentials()
    {
        const string name = "sql:credentials";
        const string secret = "smoke-päss-🔑";
        var encrypted = WindowsCredentials.Protect(secret);
        if (encrypted.Value is not { } value || WindowsCredentials.Unprotect(value).Value != secret)
        {
            return new SmokeCheck(name, false, "DPAPI round trip failed: " + (encrypted.Error ?? WindowsCredentials.Unprotect(encrypted.Value ?? "").Error));
        }

        string target = "NeonSidekick.smoke/" + Guid.NewGuid().ToString("N");
        try
        {
            if (WindowsCredentials.WriteGeneric(target, @"SMOKE\nobody", secret).Error is { } writeError)
            {
                return new SmokeCheck(name, false, "Credential Manager write failed: " + writeError);
            }

            var read = WindowsCredentials.ReadGeneric(target);
            if (read.Value != secret)
            {
                return new SmokeCheck(name, false, "Credential Manager read gave back something else: " + read.Error);
            }
        }
        finally
        {
            WindowsCredentials.DeleteGeneric(target);
        }

        if (!WindowsCredentials.ReadGeneric(target).NotFound)
        {
            return new SmokeCheck(name, false, "Credential Manager delete left the entry");
        }

        using var token = WindowsCredentials.LogonNetOnly(@"NEONSIDEKICK-SMOKE\nobody", secret, out string? logonError);
        if (token is null)
        {
            return new SmokeCheck(name, false, "NEW_CREDENTIALS logon failed: " + logonError);
        }

        string outside = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
        string inside = System.Security.Principal.WindowsIdentity.RunImpersonated(token, () => System.Security.Principal.WindowsIdentity.GetCurrent().Name);
        return inside == outside
            ? new SmokeCheck(name, true, $"DPAPI, Credential Manager and a netonly logon ok; still {outside} locally")
            : new SmokeCheck(name, false, $"the netonly token changed the local identity: {outside} became {inside}");
    }

    /// <summary>
    /// <c>culture:invariant</c> (2026-09-23): <see cref="CulturePin"/> held — the current culture is the invariant one,
    /// and a number and a date print as they did under <c>InvariantGlobalization</c>. The flag is off since SqlClient
    /// refuses it; this is the line that says the process still formats the way it always did.
    /// </summary>
    public static SmokeCheck ProbeCulture()
    {
        const string name = "culture:invariant";
        string sample = string.Format(CultureInfo.CurrentCulture, "{0} {1:d}", 1234.5, new DateTime(2009, 1, 7));
        bool ok = CulturePin.Holds && sample == "1234.5 01/07/2009";
        return new SmokeCheck(name, ok, ok ? "current culture invariant; " + sample : $"current culture '{CultureInfo.CurrentCulture.Name}', sample '{sample}'");
    }
}
