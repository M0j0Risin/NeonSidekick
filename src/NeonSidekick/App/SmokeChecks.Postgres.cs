using Npgsql;
using NeonSidekick.Postgres;

namespace NeonSidekick.App;

public static partial class SmokeChecks
{
    /// <summary>
    /// <c>postgres:driver</c> (2026-10-04): the PostgreSQL tools' driver on the published binary, with no server. The gate passes a WITH
    /// clause and refuses a dollar-quoted <c>DO</c> and a data-modifying CTE; a command takes a bound parameter; the slim data source
    /// builds with every opt-in the tools use; then it opens a loopback port nothing listens on with a one-second timeout — an
    /// <see cref="NpgsqlException"/> is the proof that its connect path runs under NativeAOT; a type-initializer, not-supported or
    /// missing-metadata failure is the break. <c>--postgres-check</c> against a server proves the rest.
    /// </summary>
    public static SmokeCheck ProbePostgres()
    {
        const string name = "postgres:driver";
        if (PostgresReadOnlyGate.Check("WITH c AS (SELECT 1 AS x) SELECT x FROM c") is { } refused)
        {
            return new SmokeCheck(name, false, "the gate refused a plain WITH clause: " + refused);
        }

        if (PostgresReadOnlyGate.Check("DO $$ BEGIN DROP TABLE t; END $$") is null || PostgresReadOnlyGate.Check("WITH d AS (DELETE FROM t RETURNING *) SELECT * FROM d") is null)
        {
            return new SmokeCheck(name, false, "the gate let a DO block or a data-modifying CTE through");
        }

        try
        {
            using (var command = new NpgsqlCommand("SELECT @probe"))
            {
                command.Parameters.Add(PostgresAccess.Bind("probe", 1L));
            }

            var builder = new NpgsqlConnectionStringBuilder
            {
                Host = "127.0.0.1",
                Port = 1,
                Username = "neonsidekick_smoke",
                Password = Guid.NewGuid().ToString("N"),
                Timeout = 1,
                Pooling = false,
            };
            using var source = PostgresAccess.Build(builder.ConnectionString);
            using var connection = source.OpenConnection();
            return new SmokeCheck(name, false, "a port nobody serves accepted a connection");
        }
        catch (NpgsqlException ex)
        {
            return new SmokeCheck(name, true, $"gate ok; Npgsql {typeof(NpgsqlConnection).Assembly.GetName().Version} answered {ex.GetType().Name} ({Diagnostics.LogText.Excerpt(ex.Message, 80)})");
        }
        catch (Exception ex) when (ex is NullReferenceException or TypeInitializationException or NotSupportedException or MissingMethodException or MissingMemberException or TypeLoadException or InvalidCastException or InvalidOperationException)
        {
            return new SmokeCheck(name, false, ex.GetType().Name + ": " + Diagnostics.LogText.Excerpt(ex.Message));
        }
    }
}
