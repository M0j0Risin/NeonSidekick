using System.Diagnostics;
using Microsoft.Data.Sqlite;
using NeonSidekick.MySql;
using NeonSidekick.Oracle;
using NeonSidekick.Postgres;
using NeonSidekick.Sql;
using NeonSidekick.Sqlite;

namespace NeonSidekick.Tests;

/// <summary>Phase 6 of the database review's fixes (2026-10-24): transport and sessions — decision DB1.</summary>
public sealed class DatabaseSessionTests
{
    /// <summary>E3 and DB1: over an unchecked certificate PostgreSQL refuses to send the password in clear; under verify-full it may.</summary>
    [Fact]
    public void Postgres_RefusesACleartextPassword_UnlessTheCertificateIsChecked()
    {
        var prefer = new PostgresConnectionConfig { Host = "h", Database = "d", User = "u" };
        Assert.False(prefer.TlsChecked);
        Assert.Equal(PostgresConnectionConfig.NoCleartextPassword, prefer.Builder("p", 30).RequireAuth);
        var checkedTls = new PostgresConnectionConfig { Host = "h", Database = "d", User = "u", SslMode = "Verify-Full" };
        Assert.True(checkedTls.TlsChecked);
        Assert.Null(checkedTls.Builder("p", 30).RequireAuth);
        Assert.Equal(Npgsql.SslMode.VerifyFull, checkedTls.Builder("p", 30).SslMode);
    }

    /// <summary>DB1: a connections line says when the certificate is not checked.</summary>
    [Fact]
    public void AConnectionsLine_SaysAnUncheckedCertificate()
    {
        Assert.EndsWith(SqlText.TlsUnchecked, MySqlText.ConnectionLine(new MySqlNamedConnection("shop", new MySqlConnectionConfig { Host = "h", Database = "d", User = "u" }, "x")));
        Assert.DoesNotContain(SqlText.TlsUnchecked, MySqlText.ConnectionLine(new MySqlNamedConnection("shop", new MySqlConnectionConfig { Host = "h", Database = "d", User = "u", SslMode = "verify-full" }, "x")));
        Assert.EndsWith(SqlText.TlsUnchecked, PostgresText.ConnectionLine(new PostgresNamedConnection("shop", new PostgresConnectionConfig { Host = "h", Database = "d", User = "u", SslMode = "require" }, "x")));
    }

    /// <summary>H1: the read-back settings refuse the call when one did not hold.</summary>
    [Fact]
    public void PostgresSession_ReadBack_RefusesWhatDidNotHold()
    {
        Assert.Null(PostgresText.SessionProblem("on", "on", "30s", "sales", readOnly: true, "sales"));
        Assert.Equal(PostgresText.SessionNotSet("default_transaction_read_only"), PostgresText.SessionProblem("off", "on", "30s", "public", readOnly: true, null));
        Assert.Null(PostgresText.SessionProblem("off", "on", "30s", "public", readOnly: false, null));
        Assert.Equal(PostgresText.SessionNotSet("standard_conforming_strings"), PostgresText.SessionProblem("on", "off", "30s", "public", readOnly: true, null));
        Assert.Equal(PostgresText.SessionNotSet("statement_timeout"), PostgresText.SessionProblem("on", "on", "0", "public", readOnly: true, null));
        Assert.Equal(PostgresText.SessionNotSet("the search path"), PostgresText.SessionProblem("on", "on", "30s", "\"$user\",public", readOnly: true, "sales"));
    }

    /// <summary>E2: the session's schema is quoted from its normalized spelling as it stands, never folded again.</summary>
    [Fact]
    public void OracleSchema_IsQuotedAsNormalized()
    {
        var quoted = new OracleConnectionConfig { DataSource = "x", User = "u", Schema = "\"Hr\"" };
        Assert.Equal("Hr", OracleAccess.Place(quoted));
        Assert.Equal("\"Hr\"", OracleIdentifier.QuoteNormal(OracleAccess.Place(quoted)));
        Assert.Equal("\"HR\"", OracleIdentifier.Quote("hr"));
        Assert.Null(OracleIdentifier.QuoteNormal("a\"b"));
        Assert.Null(OracleIdentifier.QuoteNormal(""));
        Assert.Equal(0, OracleAccess.Release("unknown"));
    }

    /// <summary>O12: a runas sign-in never reconnects on its own, outside its token.</summary>
    [Fact]
    public void ARunAsConnection_NeverReconnectsOnItsOwn()
    {
        var runas = new SqlConnectionConfig { Server = "s", Database = "d", Auth = "runas", User = @"CONTOSO\svc" };
        Assert.Equal(0, runas.Builder().ConnectRetryCount);
    }

    /// <summary>O13: one deadline for a call of several statements.</summary>
    [Fact]
    public void ACallsDeadline_IsShared()
    {
        var watch = Stopwatch.StartNew();
        Assert.Equal(30, SqlDeadline.Left(watch, 30));
        Assert.Equal(0, SqlDeadline.Left(new Stopwatch(), 0) - 1);   // a zero timeout is read as one second
        Assert.Equal(1, SqlDeadline.Left(watch, 1));
    }

    /// <summary>O2: a SQLite read on a file another connection holds ends at the call's timeout, not the driver's 30 seconds, and Esc ends it at once.</summary>
    [Fact]
    public async Task ASqliteRead_OnAHeldFile_EndsAtItsTimeout_AndOnEsc()
    {
        string dir = Directory.CreateTempSubdirectory("neon-o2-").FullName;
        string path = Path.Combine(dir, "held.db");
        try
        {
            using (var make = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ConnectionString))
            {
                make.Open();
                using var create = make.CreateCommand();
                create.CommandText = "CREATE TABLE t (a); INSERT INTO t VALUES (1);";
                create.ExecuteNonQuery();
            }

            using var holder = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ConnectionString);
            holder.Open();
            using (var hold = holder.CreateCommand())
            {
                hold.CommandText = "BEGIN EXCLUSIVE";
                hold.ExecuteNonQuery();
            }

            var target = new SqliteTarget("held", path);
            var watch = Stopwatch.StartNew();
            var run = await Task.Run(() => SqliteAccess.Run(target, ["SELECT a FROM t"], [], 10, 2, CancellationToken.None));
            Assert.Equal(SqlOutcome.Timeout, run.Outcome);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "ran " + watch.Elapsed);

            using var esc = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            watch.Restart();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Run(() => SqliteAccess.Run(target, ["SELECT a FROM t"], [], 10, 60, esc.Token)));
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "ran " + watch.Elapsed);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
