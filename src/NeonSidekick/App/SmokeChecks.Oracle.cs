using System.Globalization;
using NeonSidekick.Oracle;
using Oracle.ManagedDataAccess.Client;

namespace NeonSidekick.App;

public static partial class SmokeChecks
{
    /// <summary>
    /// <c>oracle:driver</c> (2026-09-30): the Oracle tools' driver on the published binary, with no server. The gate passes a
    /// WITH clause and refuses <c>WITH FUNCTION</c>; a command takes <see cref="OracleCommand.InitialLONGFetchSize"/> (the
    /// setter the trimmed assembly threw <see cref="NullReferenceException"/> from in the spike — the rooted one must not); then
    /// ODP.NET opens a loopback port nothing listens on with a one-second timeout — an <see cref="OracleException"/> is the
    /// proof that its connect path (the builder, the network layer, its error table) runs under NativeAOT; a type-initializer,
    /// not-supported or missing-metadata failure is the break. ODP.NET declares no AOT support: the JIT proves nothing, this
    /// line does, and <c>--oracle-check</c> against a server proves the rest.
    /// </summary>
    public static SmokeCheck ProbeOracle()
    {
        const string name = "oracle:driver";
        if (OracleReadOnlyGate.Check("WITH c AS (SELECT 1 AS x FROM dual) SELECT x FROM c") is { } refused)
        {
            return new SmokeCheck(name, false, "the gate refused a plain WITH clause: " + refused);
        }

        if (OracleReadOnlyGate.Check("WITH FUNCTION f RETURN NUMBER IS BEGIN RETURN 1; END; SELECT f FROM dual") is null)
        {
            return new SmokeCheck(name, false, "the gate let WITH FUNCTION through");
        }

        try
        {
            using (var command = new OracleCommand("SELECT 1 FROM dual") { InitialLONGFetchSize = OracleAccess.LongFetchChars, BindByName = true })
            {
                command.Parameters.Add(OracleAccess.Bind(new Sql.SqlParameterValue("probe", 1L)));
            }

            var builder = new OracleConnectionStringBuilder
            {
                DataSource = "127.0.0.1:1/neonsidekick_smoke",
                UserID = "neonsidekick_smoke",
                Password = Guid.NewGuid().ToString("N"),
                ConnectionTimeout = 1,
                Pooling = false,
            };
            using var connection = new OracleConnection(builder.ConnectionString);
            connection.Open();
            return new SmokeCheck(name, false, "a port nobody serves accepted a connection");
        }
        catch (OracleException ex)
        {
            return new SmokeCheck(name, true, $"gate ok; ODP.NET {typeof(OracleConnection).Assembly.GetName().Version} answered ORA-{ex.Number.ToString("D5", CultureInfo.InvariantCulture)}");
        }
        catch (Exception ex) when (ex is NullReferenceException or TypeInitializationException or NotSupportedException or MissingMethodException or MissingMemberException or TypeLoadException or InvalidCastException)
        {
            return new SmokeCheck(name, false, ex.GetType().Name + ": " + Diagnostics.LogText.Excerpt(ex.Message));
        }
    }
}
