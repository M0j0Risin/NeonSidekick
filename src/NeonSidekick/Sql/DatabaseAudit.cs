using System.Text.RegularExpressions;
using NeonSidekick.Files;
using NeonSidekick.Llm.Tools;

namespace NeonSidekick.Sql;

/// <summary>How an allowed change ended, as its audit line says it.</summary>
public enum DatabaseAuditOutcome
{
    /// <summary>The statement ran (committed).</summary>
    Done,

    /// <summary>The server refused it, maybe after part of it committed (a MyISAM table, a procedure that commits as it goes).</summary>
    Failed,

    /// <summary>It ran past the timeout and was stopped.</summary>
    TimedOut,

    /// <summary>The user stopped it under way.</summary>
    Canceled,

    /// <summary>Nothing reached the server (no connect, a read-only connection).</summary>
    NotRun,
}

/// <summary>
/// The one audit line of every database change the user allowed (2026-10-24, the database review's decision DB7, findings A8, B17, H3, J4,
/// G16): written whatever the change's end — done, failed, timed out, canceled — so a statement that committed part of its work and then
/// failed is in the log; the whole statement, its whitespace runs folded to one space so it is one line (H3: only the first non-blank line
/// was kept, so a leading comment or a multi-line WHERE was lost), its secrets redacted by <see cref="ToolRedaction.Text"/> (J4: an
/// <c>api_token = '…'</c> or a <c>CONNECTION='mysql://u:pw@…'</c> landed in the log window and the <c>--log</c> file), its hiding
/// characters escaped (<see cref="NameScrub"/>), and cut at <see cref="MaxStatementChars"/> without splitting a pair. Pure.
/// </summary>
public static partial class DatabaseAudit
{
    /// <summary>The most of a statement a line keeps.</summary>
    public const int MaxStatementChars = 2000;

    /// <summary>A run's outcome as the audit says it.</summary>
    public static DatabaseAuditOutcome OutcomeOf(SqlRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return run.Outcome switch
        {
            SqlOutcome.Ok => DatabaseAuditOutcome.Done,
            // A refusal before the statement (SqlRun.Refused: a session setting that did not hold, a mode kept) ran nothing (2026-10-24, the
            // review of these fixes: it was logged as failed, "part of it may have committed").
            SqlOutcome.Failed when run.Elapsed == TimeSpan.Zero && run.Grids.Count == 0 => DatabaseAuditOutcome.NotRun,
            SqlOutcome.Failed => DatabaseAuditOutcome.Failed,
            SqlOutcome.Timeout => DatabaseAuditOutcome.TimedOut,
            _ => DatabaseAuditOutcome.NotRun,
        };
    }

    /// <summary>A server family's line: <c>where: 2 rows changed by …</c>, <c>ran by …</c>, <c>failed by …</c>.</summary>
    public static string Line(string where, DatabaseAuditOutcome outcome, int? changes, string sql) =>
        ServerWriteText.AuditLogLine(where, Word(outcome, changes), Statement(sql));

    /// <summary>What happened, as the line's verb: the rows changed when the server counts them and it ran, else the outcome.</summary>
    public static string Word(DatabaseAuditOutcome outcome, int? changes) => outcome switch
    {
        DatabaseAuditOutcome.Done => changes is { } n ? SqlText.Count(n, "row") + " changed" : "ran",
        DatabaseAuditOutcome.Failed => "failed (part of it may have committed)",
        DatabaseAuditOutcome.TimedOut => "timed out (part of it may have committed)",
        DatabaseAuditOutcome.Canceled => "canceled (part of it may have committed)",
        _ => "not run",
    };

    /// <summary>The statement as a line keeps it: one line, redacted, scrubbed, cut.</summary>
    public static string Statement(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        string text = NameScrub.Display(ToolRedaction.Text(Whitespace().Replace(sql.Trim(), " ")));
        return text.Length <= MaxStatementChars ? text : TextCut.Safe(text, MaxStatementChars) + "…";
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
