using System.Globalization;
using MySqlConnector;
using NeonSidekick.MySql;

namespace NeonSidekick.App;

public static partial class SmokeChecks
{
    /// <summary>
    /// <c>mysql:driver</c> (2026-09-30): the MySQL tools' driver on the published binary, with no server. The gate passes a WITH
    /// clause and refuses an executable comment; a command takes a bound parameter; then MySqlConnector opens a loopback port
    /// nothing listens on with a one-second timeout — a <see cref="MySqlException"/> is the proof that its connect path runs under
    /// NativeAOT; a type-initializer, not-supported or missing-metadata failure is the break. <c>--mysql-check</c> against a server
    /// proves the rest.
    /// </summary>
    public static SmokeCheck ProbeMySql()
    {
        const string name = "mysql:driver";
        if (MySqlReadOnlyGate.Check("WITH c AS (SELECT 1 AS x) SELECT x FROM c") is { } refused)
        {
            return new SmokeCheck(name, false, "the gate refused a plain WITH clause: " + refused);
        }

        if (MySqlReadOnlyGate.Check("SELECT /*! 1; DROP TABLE t */ 1") is null)
        {
            return new SmokeCheck(name, false, "the gate let an executable comment through");
        }

        try
        {
            using (var command = new MySqlCommand("SELECT @probe"))
            {
                command.Parameters.Add(MySqlAccess.Bind(new Sql.SqlParameterValue("probe", 1L)));
            }

            var builder = new MySqlConnectionStringBuilder
            {
                Server = "127.0.0.1",
                Port = 1,
                UserID = "neonsidekick_smoke",
                Password = Guid.NewGuid().ToString("N"),
                ConnectionTimeout = 1,
                Pooling = false,
                AllowLoadLocalInfile = false,
                AllowUserVariables = false,
            };
            using var connection = new MySqlConnection(builder.ConnectionString);
            connection.Open();
            return new SmokeCheck(name, false, "a port nobody serves accepted a connection");
        }
        catch (MySqlException ex)
        {
            return new SmokeCheck(name, true, $"gate ok; MySqlConnector {typeof(MySqlConnection).Assembly.GetName().Version} answered {ex.ErrorCode} ({ex.Number.ToString(CultureInfo.InvariantCulture)})");
        }
        catch (Exception ex) when (ex is NullReferenceException or TypeInitializationException or NotSupportedException or MissingMethodException or MissingMemberException or TypeLoadException or InvalidCastException)
        {
            return new SmokeCheck(name, false, ex.GetType().Name + ": " + Diagnostics.LogText.Excerpt(ex.Message));
        }
    }
}
