using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using NeonSidekick.Audits;
using NeonSidekick.Diagnostics;
using NeonSidekick.Files;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// What one <c>present_audit</c> call changed (2026-10-18), for the screen's notices: the pass, the findings added, reopened and revised,
/// the retractions with their reasons, the nits and questions, and for a call with nothing new the streak, its target, what it checked and
/// whether it ended the audit.
/// </summary>
public sealed record AuditCallReport(
    int Pass,
    IReadOnlyList<AuditFinding> Added,
    IReadOnlyList<AuditFinding> Reopened,
    IReadOnlyList<(string Id, string Reason)> Retracted,
    IReadOnlyList<AuditFinding> Revised,
    int Minor,
    IReadOnlyList<string> Questions,
    bool Clean,
    int CleanCount,
    int CleanNeeded,
    IReadOnlyList<string> Checked,
    bool Converged);

/// <summary>
/// A code audit's report tool (2026-10-18, <c>/code-audit</c>): <c>present_audit(findings?, retract?, revise?, minor?, questions?, covered?,
/// checked?, summary?)</c> records a pass's changes in the report the app writes (<see cref="AuditReport"/>), so the model needs no write tool and
/// every finding gets a stable id and one shape. Offered only during a pass, appended where <c>present_plan</c> is, in no <c>PlanTools</c> list.
/// <para>One call, in order: the report read fresh (an edit made in an editor meanwhile survives); every argument checked as sent — a whole list
/// sent as a pruned stub, the lists read, at most <see cref="AuditText.MaxFindingsPerCall"/> findings, a stub in any field, the required
/// fields, the words, the file (in the sandbox, not <c>.neon/</c>, read this session by <c>read_file</c>: <see cref="WorkingDirectory.WasViewed"/>),
/// the line, the evidence (found within ±5 lines of the line, whitespace forgiven) and the ids; each text field redacted
/// (<see cref="ToolRedaction.Text"/>), then cut to its cap; the duplicates on the capped titles; retractions, then revisions, then reopenings and
/// new findings; the size; the save; and only then the coverage sidecar, the loop (<see cref="Plans.PlanGapLoop.Record"/>, or a clean report)
/// and the <c>reported</c> seam. A refusal at any step leaves the file, its sidecar and the loop untouched (2026-10-19, the review: the sidecar
/// was written before the size check, so a call refused as full had still changed it). A secret-looking file's finding takes no evidence
/// (<see cref="AuditScopes.LooksSecret"/>, <see cref="AuditText.SecretEvidenceResult"/>).</para>
/// </summary>
public sealed partial class PresentAuditTool : AIFunction, IRefusesPrunedStubs, IArgumentCeiling
{
    /// <inheritdoc/>
    public IReadOnlyCollection<string> StubArguments { get; } = [.. ListArguments, SummaryArgument];

    /// <summary>
    /// The part of the arguments' ceiling every call gets (2026-10-29, session 832's runaway retry): <see cref="AuditText.MaxFindingsPerCall"/>
    /// findings at their field caps, the summary and the notes come to about 62k characters, doubled for JSON's escapes and rounded up.
    /// </summary>
    public const int FixedArgumentChars = 150_000;

    /// <summary>
    /// The part of the ceiling each file in the audit's scope adds (the review of e7f741e8: a fixed 200k stopped a big repository's honest
    /// pass, the lists having no count cap): one covered path and one checked line at <see cref="MaxNoteChars"/>, doubled for escapes.
    /// </summary>
    public const int PerScopeFileChars = 700;

    /// <summary>
    /// The arguments' ceiling: <see cref="FixedArgumentChars"/> and <see cref="PerScopeFileChars"/> for each file in the scope, so past it the
    /// model is repeating itself, not reporting. Read as a call is named, so the audit running then sets it.
    /// </summary>
    public int MaxArgumentChars => FixedArgumentChars + PerScopeFileChars * (_session.Scope?.Files.Count ?? 0);

    public const string ToolName = "present_audit";

    public const string FindingsArgument = "findings";
    public const string RetractArgument = "retract";
    public const string ReviseArgument = "revise";
    public const string MinorArgument = "minor";
    public const string QuestionsArgument = "questions";
    public const string CoveredArgument = "covered";
    public const string CheckedArgument = "checked";
    public const string SummaryArgument = "summary";

    public const int MaxTitleChars = 160;
    public const int MaxDetailChars = 1_500;
    public const int MaxFixChars = 800;
    public const int MaxNoteChars = 300;
    public const int MaxEvidenceChars = 400;
    public const int MaxEvidenceLines = 8;
    public const int MaxSummaryChars = 2_000;

    /// <summary>How far from its line a finding's evidence may be found.</summary>
    public const int EvidenceSlack = 5;

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "findings": { "type": "array", "description": "New findings, at most 20 a call.", "items": { "type": "object", "properties": {
              "severity": { "type": "string", "enum": ["critical", "high", "medium", "low"], "description": "critical, high, medium or low; a nit goes in minor instead." },
              "dimension": { "type": "string", "enum": ["security", "correctness", "performance", "maintainability", "tests"], "description": "security, correctness, performance, maintainability or tests." },
              "file": { "type": "string", "description": "The file's path in the working directory, read with read_file first." },
              "line": { "type": "integer", "description": "The first line of the issue." },
              "title": { "type": "string", "description": "One line naming the problem." },
              "detail": { "type": "string", "description": "What is wrong and why it matters." },
              "fix": { "type": "string", "description": "Optional: how to fix it." },
              "evidence": { "type": "string", "description": "Optional: the cited code quoted verbatim, at most 8 lines." }
            }, "required": ["severity", "dimension", "file", "title", "detail"] } },
            "retract": { "type": "array", "description": "Open findings withdrawn, by id, each with the reason.", "items": { "type": "object", "properties": {
              "id": { "type": "string", "description": "The finding's id, like A3." },
              "reason": { "type": "string", "description": "Why it is withdrawn." }
            }, "required": ["id", "reason"] } },
            "revise": { "type": "array", "description": "Open findings corrected in place, by id: a new severity, line, detail, fix or evidence.", "items": { "type": "object", "properties": {
              "id": { "type": "string", "description": "The finding's id, like A3." },
              "severity": { "type": "string", "description": "critical, high, medium or low." },
              "line": { "type": "integer", "description": "The first line of the issue." },
              "detail": { "type": "string", "description": "What is wrong and why it matters." },
              "fix": { "type": "string", "description": "How to fix it." },
              "evidence": { "type": "string", "description": "The cited code quoted verbatim, at most 8 lines." }
            }, "required": ["id"] } },
            "minor": { "type": "array", "items": { "type": "string" }, "description": "Nits: one short line each, never findings." },
            "questions": { "type": "array", "items": { "type": "string" }, "description": "What only the user can answer, one line each." },
            "covered": { "type": "array", "items": { "type": "string" }, "description": "The areas or files this pass looked at." },
            "checked": { "type": "array", "items": { "type": "string" }, "description": "Required when findings, retract and revise are all empty: one short line for each area you checked." },
            "summary": { "type": "string", "description": "Optional: the audit's summary so far, a few lines; the latest one wins." }
          }
        }
        """);

    private static readonly string[] ListArguments = [FindingsArgument, RetractArgument, ReviseArgument, MinorArgument, QuestionsArgument, CoveredArgument, CheckedArgument];

    private readonly AuditSession _session;
    private readonly WorkingDirectory _files;
    private readonly TimeProvider _time;
    private readonly Action<AuditCallReport>? _reported;

    /// <param name="reported">Each call that changed the report, for the screen's notices; null (headless) prints the generic tool lines.</param>
    public PresentAuditTool(AuditSession session, WorkingDirectory files, TimeProvider time, Action<AuditCallReport>? reported = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _reported = reported;
    }

    public override string Name => ToolName;

    public override string Description => AuditText.ToolDescription;

    public override JsonElement JsonSchema => Schema;

    /// <summary>One new finding as checked, before redaction and caps.</summary>
    private sealed record NewFinding(int Index, Audits.AuditSeverity Severity, string Dimension, string File, int? Line, string Title, string Detail, string Fix, string Evidence);

    /// <summary>One revision as checked.</summary>
    private sealed record Revision(int Index, string Id, Audits.AuditSeverity? Severity, int? Line, string? Detail, string? Fix, string? Evidence);

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return new ValueTask<object?>(Invoke(arguments));
    }

    private string Invoke(AIFunctionArguments arguments)
    {
        var read = new Dictionary<string, EvidenceFile?>(AuditPath.Comparer);
        if (_session.Running is not { Pending: false } loop || _session.Path is not { } path || _session.Scope is not { } scope)
        {
            return AuditText.NotAuditingResult;
        }

        if (loop.Converged)
        {
            return AuditText.AlreadyConvergedResult;
        }

        // The working directory changed under the audit (2026-10-19): the files read since are another folder's, so nothing is recorded.
        if (_session.Moved)
        {
            return AuditText.WorkdirMovedResult;
        }

        // Another NeonSidekick took the report's lock over (2026-10-29): its read-merge-save would overwrite that one's findings, so nothing is written.
        if (!_session.StillHolds())
        {
            return AuditText.CouldNotSaveResult(AuditText.LockedError(path));
        }

        // 1. The report read fresh: an edit made in an editor meanwhile (a tick, a reworded detail, a finding deleted) survives. The report is the
        // audit's own folder's (_session.Files, pinned as it started); the code it cites is read through the working directory, the model's.
        var reports = _session.Files;
        if (AuditFiles.ReadWhole(reports, path) is not { } text)
        {
            return AuditFiles.Exists(reports, path) ? AuditText.CouldNotSaveResult(AuditText.ReportUnreadable(path)) : AuditText.ReportGoneResult(path);
        }

        var report = AuditReport.Parse(text);

        // 2.1 A whole list sent as a stub string (a model copying its own stubbed call), before the list read would call it no list.
        foreach (string list in ListArguments)
        {
            if (arguments.TryGetValue(list, out object? sent) && sent switch
            {
                JsonElement { ValueKind: JsonValueKind.String } element => ConversationCompactor.IsPrunedStub(element.GetString()),
                string s => ConversationCompactor.IsPrunedStub(s),
                _ => false,
            })
            {
                return AuditText.StubSentResult(list);
            }
        }

        // 2.2 The lists.
        if (!ToolArguments.TryReadObjectList(arguments, FindingsArgument, out var findingItems, out string raw))
        {
            return AuditText.ListInvalidResult(FindingsArgument, raw);
        }

        if (!ToolArguments.TryReadObjectList(arguments, RetractArgument, out var retractItems, out raw))
        {
            return AuditText.ListInvalidResult(RetractArgument, raw);
        }

        if (!ToolArguments.TryReadObjectList(arguments, ReviseArgument, out var reviseItems, out raw))
        {
            return AuditText.ListInvalidResult(ReviseArgument, raw);
        }

        var strings = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (string list in (string[])[MinorArgument, QuestionsArgument, CoveredArgument, CheckedArgument])
        {
            if (!ToolArguments.TryReadStringList(arguments, list, out var values, out raw))
            {
                return AuditText.StringListInvalidResult(list, raw);
            }

            strings[list] = Lines(values);
        }

        string summary = ToolArguments.ReadString(arguments, SummaryArgument).Trim();

        // 2.3 The count.
        if (findingItems.Count > AuditText.MaxFindingsPerCall)
        {
            return AuditText.TooManyFindingsResult(findingItems.Count);
        }

        // 2.4 A stub in any field.
        if ((StubField(findingItems, FindingsArgument) ?? StubField(retractItems, RetractArgument) ?? StubField(reviseItems, ReviseArgument)) is { } stubbed)
        {
            return AuditText.StubSentResult(stubbed);
        }

        foreach (var (list, values) in strings)
        {
            for (int i = 0; i < values.Count; i++)
            {
                if (ConversationCompactor.IsPrunedStub(values[i]))
                {
                    return AuditText.StubSentResult($"{list}[{N(i)}]");
                }
            }
        }

        if (ConversationCompactor.IsPrunedStub(summary))
        {
            return AuditText.StubSentResult(SummaryArgument);
        }

        // 2.5 The required fields, then 2.6 the words, the file, the line, the evidence and the ids. Parse makes every id unique; the first
        // wins here all the same, so no hand edit can throw.
        var open = new Dictionary<string, AuditFinding>(StringComparer.OrdinalIgnoreCase);
        foreach (var finding in report.Findings.Where(f => f.State == AuditFindingState.Open))
        {
            open.TryAdd(finding.Id, finding);
        }

        var retractions = new List<(string Id, string Reason)>();
        for (int i = 0; i < retractItems.Count; i++)
        {
            string field = $"{RetractArgument}[{N(i)}]";
            string id = Field(retractItems[i], "id").Trim();
            string reason = Field(retractItems[i], "reason").Trim();
            if (id.Length == 0)
            {
                return AuditText.MissingFieldResult(field + ".id");
            }

            if (reason.Length == 0)
            {
                return AuditText.MissingFieldResult(field + ".reason");
            }

            if (!open.TryGetValue(id, out var finding) || retractions.Any(r => r.Id == finding.Id))
            {
                return AuditText.UnknownIdResult(field + ".id", id);
            }

            retractions.Add((finding.Id, reason));
        }

        var revisions = new List<Revision>();
        for (int i = 0; i < reviseItems.Count; i++)
        {
            string field = $"{ReviseArgument}[{N(i)}]";
            var item = reviseItems[i];
            string id = Field(item, "id").Trim();
            if (id.Length == 0)
            {
                return AuditText.MissingFieldResult(field + ".id");
            }

            if (!open.TryGetValue(id, out var finding) || retractions.Any(r => r.Id == finding.Id) || revisions.Any(r => r.Id == finding.Id))
            {
                return AuditText.UnknownIdResult(field + ".id", id);
            }

            Audits.AuditSeverity? severity = null;
            if (Field(item, "severity").Trim() is { Length: > 0 } word)
            {
                if (SeverityOf(word, field + ".severity", out var error) is not { } parsed)
                {
                    return error!;
                }

                severity = parsed;
            }

            if (!TryLine(item, field + ".line", out int? line, out string? lineError))
            {
                return lineError!;
            }

            string? detail = Optional(item, "detail");
            string? fix = Optional(item, "fix");
            string? evidence = Optional(item, "evidence");
            if (evidence is not null && EvidenceTooLong(evidence))
            {
                return AuditText.EvidenceTooLongResult(field + ".evidence");
            }

            if (evidence is not null && AuditScopes.LooksSecret(finding.File))
            {
                return AuditText.SecretEvidenceResult(field + ".evidence", finding.File);
            }

            if (line is not null || evidence is not null)
            {
                // A moved line or a new quote is checked again where the finding now points: the quote given, else the finding's own (saved
                // redacted, so its placeholder matches any run on its line).
                if (!_files.WasViewed(finding.File))
                {
                    return AuditText.ReadFirstResult(finding.File);
                }

                // No line on either side: the quote may stand anywhere in the file (2026-10-19, the second review: a 0 here held a
                // line-less finding's new quote to lines 1 to 6).
                int? at = line ?? finding.Line;
                if (FileLines(read, finding.File) is not { } source)
                {
                    return AuditText.NoFileResult(finding.File);
                }

                if (line is { } moved && moved > source.Lines.Count)
                {
                    return AuditText.LineOutOfRangeResult(field + ".line", finding.File, moved, source.Lines.Count);
                }

                string? quote = evidence ?? finding.Evidence;
                if (quote is { Length: > 0 } && EvidenceProblem(finding.File, source, quote, at, wildcard: evidence is null) is { } problem)
                {
                    return problem;
                }
            }

            revisions.Add(new Revision(i, finding.Id, severity, line, detail, fix, evidence));
        }

        var added = new List<NewFinding>();
        for (int i = 0; i < findingItems.Count; i++)
        {
            string field = $"{FindingsArgument}[{N(i)}]";
            var item = findingItems[i];
            foreach (string required in (string[])["severity", "dimension", "file", "title", "detail"])
            {
                if (Field(item, required).Trim().Length == 0)
                {
                    return AuditText.MissingFieldResult(field + "." + required);
                }
            }

            if (SeverityOf(Field(item, "severity"), field + ".severity", out var severityError) is not { } severity)
            {
                return severityError!;
            }

            string dimensionWord = Field(item, "dimension").Trim();
            if (DimensionOf(dimensionWord) is not { } dimension)
            {
                return AuditText.BadDimensionResult(field + ".dimension", dimensionWord);
            }

            if (!TryLine(item, field + ".line", out int? line, out string? lineError))
            {
                return lineError!;
            }

            string sentFile = Field(item, "file").Trim();
            if (AuditPath.Of(_files, sentFile, out _) is not { Length: > 0 } file)
            {
                return AuditText.OutsideResult(sentFile);
            }

            if (AuditPath.InAppFolder(file))
            {
                return AuditText.AppFolderFindingResult(file);
            }

            string evidence = Field(item, "evidence");
            if (evidence.Trim().Length == 0)
            {
                evidence = "";
            }

            if (evidence.Length > 0 && EvidenceTooLong(evidence))
            {
                return AuditText.EvidenceTooLongResult(field + ".evidence");
            }

            if (evidence.Length > 0 && AuditScopes.LooksSecret(file))
            {
                return AuditText.SecretEvidenceResult(field + ".evidence", file);
            }

            if (scope.Deleted.Contains(file, AuditPath.Comparer) && !AuditFiles.Exists(_files, file))
            {
                // A file the change deletes (2026-10-18): no read to ask for, so no line and no quote either.
                if (line is not null || evidence.Length > 0)
                {
                    return AuditText.DeletedFileResult(file);
                }
            }
            else
            {
                if (!AuditFiles.Exists(_files, file))
                {
                    return AuditText.NoFileResult(file);
                }

                if (!_files.WasViewed(file))
                {
                    return AuditText.ReadFirstResult(file);
                }

                if (FileLines(read, file) is not { } source)
                {
                    return AuditText.NoFileResult(file);
                }

                if (line is { } at && at > Math.Max(1, source.Lines.Count))
                {
                    return AuditText.LineOutOfRangeResult(field + ".line", file, at, source.Lines.Count);
                }

                if (evidence.Length > 0 && EvidenceProblem(file, source, evidence, line, wildcard: false) is { } problem)
                {
                    return problem;
                }
            }

            added.Add(new NewFinding(i, severity, dimension, file, line, Field(item, "title"), Field(item, "detail"), Field(item, "fix"), evidence));
        }

        var @checked = strings[CheckedArgument];
        bool changes = added.Count > 0 || retractions.Count > 0 || revisions.Count > 0;
        if (!changes && @checked.Count == 0)
        {
            return AuditText.CheckedRequiredResult;
        }

        // 3. Redacted, then 4. cut to the caps: every limit measures the text that is saved.
        var minor = strings[MinorArgument].Select(m => Cap(ToolRedaction.Text(m), MaxNoteChars)).ToList();
        var questions = strings[QuestionsArgument].Select(q => Cap(ToolRedaction.Text(q), MaxNoteChars)).ToList();
        var checkedLines = @checked.Select(c => Cap(ToolRedaction.Text(c), MaxNoteChars)).ToList();
        var shaped = added.Select(f => f with
        {
            Title = Cap(AuditReport.Flatten(ToolRedaction.Text(f.Title)), MaxTitleChars),
            Detail = Cap(ToolRedaction.Text(f.Detail).Trim(), MaxDetailChars),
            Fix = Cap(ToolRedaction.Text(f.Fix).Trim(), MaxFixChars),
            Evidence = ToolRedaction.Text(f.Evidence),
        }).ToList();

        // 5. The duplicates, on the capped titles, after this call's retractions: an open one refused, a settled one reopened.
        var retractedIds = retractions.Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var reopen = new List<(NewFinding Sent, AuditFinding Settled)>();
        var fresh = new List<NewFinding>();
        foreach (var finding in shaped)
        {
            string field = $"{FindingsArgument}[{N(finding.Index)}]";
            var same = report.Findings.FirstOrDefault(f => !retractedIds.Contains(f.Id) && AuditPath.Comparer.Equals(f.File, finding.File) && string.Equals(f.Title, finding.Title, StringComparison.OrdinalIgnoreCase));
            if (same is { State: AuditFindingState.Open })
            {
                return AuditText.DuplicateResult(field, same.Id);
            }

            if (fresh.Any(f => AuditPath.Comparer.Equals(f.File, finding.File) && string.Equals(f.Title, finding.Title, StringComparison.OrdinalIgnoreCase)))
            {
                return AuditText.DuplicateResult(field, $"{FindingsArgument}[{N(fresh.First(f => AuditPath.Comparer.Equals(f.File, finding.File) && string.Equals(f.Title, finding.Title, StringComparison.OrdinalIgnoreCase)).Index)}]");
            }

            if (same is not null)
            {
                if (reopen.Any(r => r.Settled.Id == same.Id))
                {
                    return AuditText.DuplicateResult(field, same.Id);
                }

                reopen.Add((finding, same));
            }
            else
            {
                fresh.Add(finding);
            }
        }

        // 6. Retractions, then revisions, then reopenings and new findings.
        int pass = loop.Pass;
        var keys = new List<string>();
        var retractedNotice = new List<(string Id, string Reason)>();
        foreach (var (id, reason) in retractions)
        {
            var finding = report.Findings.First(f => f.Id == id);
            report.Findings.Remove(finding);
            string why = Cap(ToolRedaction.Text(reason), MaxNoteChars);
            report.Retracted.Add(finding with { Retracted = true, RetractReason = $"retracted in pass {N(pass)}: {why}" });
            retractedNotice.Add((id, why));
            keys.Add("retract " + id);
        }

        var revised = new List<AuditFinding>();
        foreach (var revision in revisions)
        {
            int at = report.Findings.FindIndex(f => f.Id == revision.Id);
            var before = report.Findings[at];
            var said = new List<string>();
            var after = before;
            if (revision.Severity is { } severity && severity != before.Severity)
            {
                said.Add($"severity {AuditFinding.Word(before.Severity)} → {AuditFinding.Word(severity)}");
                after = after with { Severity = severity };
            }

            if (revision.Line is { } line && line != before.Line)
            {
                said.Add(before.Line is { } was ? $"line {N(was)} → {N(line)}" : $"line {N(line)}");
                after = after with { Line = line };
            }

            if (revision.Detail is { } detail)
            {
                said.Add("detail");
                after = after with { Detail = Cap(ToolRedaction.Text(detail).Trim(), MaxDetailChars) };
            }

            if (revision.Fix is { } fix)
            {
                said.Add("fix");
                after = after with { Fix = Cap(ToolRedaction.Text(fix).Trim(), MaxFixChars) };
            }

            if (revision.Evidence is { } evidence)
            {
                said.Add("evidence");
                after = after with { Evidence = ToolRedaction.Text(evidence) };
            }

            after = after with { Notes = [.. before.Notes, $"revised in pass {N(pass)}: " + (said.Count > 0 ? string.Join("; ", said) : "confirmed as it stands")] };
            report.Findings[at] = after;
            revised.Add(after);
            // The key names what the finding became (2026-10-20, a review: "revise A3" twice in a row, though the second moved its line, read to
            // the stall check as the same gaps, and the audit stopped as stalled): two passes match only when they left it the same.
            keys.Add("revise " + revision.Id + " " + ContentKey(after));
        }

        var reopened = new List<AuditFinding>();
        foreach (var (sent, settled) in reopen)
        {
            int at = report.Findings.FindIndex(f => f.Id == settled.Id);
            // The report as sent now stands (2026-10-19, the second review: the settled one's severity, line and quote were kept, so the done
            // table and the fix plan pointed at the old place); what it said before is named in the note. An old quote goes with an old line.
            string first = AuditReport.Flatten(sent.Detail);
            var was = new List<string>();
            if (sent.Severity != settled.Severity)
            {
                was.Add(AuditFinding.Word(settled.Severity));
            }

            if (sent.Line != settled.Line && settled.Line is { } oldLine)
            {
                was.Add("line " + N(oldLine));
            }

            string? evidence = sent.Evidence.Length > 0 ? sent.Evidence : sent.Line == settled.Line ? settled.Evidence : null;
            var again = settled with
            {
                Settled = false,
                Severity = sent.Severity,
                Dimension = sent.Dimension,
                Line = sent.Line,
                Detail = sent.Detail,
                Fix = sent.Fix.Length > 0 ? sent.Fix : settled.Fix,
                Evidence = evidence,
                Notes = [.. settled.Notes, $"reopened in pass {N(pass)}: " + Cap(first, MaxNoteChars) + (was.Count > 0 ? $" (was {string.Join(", ", was)})" : "")],
            };
            report.Findings[at] = again;
            reopened.Add(again);
            keys.Add("reopen " + settled.Id + " " + ContentKey(again));
        }

        var made = new List<AuditFinding>();
        int next = report.NextIdNumber;
        foreach (var finding in fresh)
        {
            var created = new AuditFinding(AuditFinding.IdFor(next++), finding.Severity, finding.Dimension, finding.File, finding.Line, finding.Title, finding.Detail,
                finding.Fix.Length == 0 ? null : finding.Fix, finding.Evidence.Length == 0 ? null : finding.Evidence, pass, false, false, null, []);
            report.Findings.Add(created);
            made.Add(created);
            keys.Add(created.GapKey);
        }

        report.Header = report.Header with { NextId = next, Status = AuditStatus.Auditing };
        foreach (string nit in minor.Where(n => !report.Nits.Contains(n, StringComparer.OrdinalIgnoreCase)))
        {
            report.Nits.Add(nit);
        }

        foreach (string question in questions.Where(q => !report.Questions.Contains(q, StringComparer.OrdinalIgnoreCase)))
        {
            report.Questions.Add(question);
        }

        foreach (string area in strings[CoveredArgument].Select(Area).Where(a => a.Length > 0))
        {
            if (!report.Areas.Contains(area, AuditPath.Comparer))
            {
                report.Areas.Add(area);
            }
        }

        if (!changes)
        {
            report.Checked.AddRange(checkedLines.Select(c => $"pass {N(pass)}: {c}"));
        }

        if (summary.Length > 0)
        {
            report.Summary = Cap(ToolRedaction.Text(summary), MaxSummaryChars);
        }

        // The coverage counts the sidecar and this session's reads alike, so it is known before the sidecar is merged, after the save.
        var coverage = _session.Coverage();
        report.CoverageRead = coverage.Read;
        report.CoverageScope = coverage.Scope;
        bool converging = !changes && loop.WouldConverge;
        int cleanNow = changes ? 0 : loop.CleanCount;
        report.Header = report.Header with { Passes = AuditText.PassesLine(pass, cleanNow, coverage.Read, coverage.Scope) };

        // 7. The size, 8. the save: a refusal or a failure records nothing.
        if (AuditFiles.Save(reports, path, report, _time.GetLocalNow()) is { } failed)
        {
            return failed == AuditText.ReportFullResult ? failed : AuditText.CouldNotSaveResult(failed);
        }

        // 9. The coverage sidecar, the loop, and the seam. The report is saved: a sidecar that can't be written is logged, never a refusal
        // (the loop must follow the report), and the next call merges it again.
        if (AuditFiles.MergeCoverage(reports, path, coverage.Viewed) is { } notSaved)
        {
            DiagnosticLog.Warn("App", AuditText.CoverageNotSavedLogLine(notSaved));
        }

        _session.Changed();
        int cleanCount = loop.CleanCount;
        if (changes)
        {
            loop.Record(keys, minor);
        }
        else if (converging)
        {
            loop.MarkConverged(checkedLines, minor);
        }
        else
        {
            loop.MarkClean(checkedLines, minor);
        }

        DiagnosticLog.Info("App", AuditText.CallLogLine(pass, made.Select(f => f.Id + " " + f.Title).ToList(), reopened.Select(f => f.Id).ToList(),
            retractedNotice.Select(r => r.Id).ToList(), revised.Select(f => f.Id).ToList(), minor.Count, !changes));
        _reported?.Invoke(new AuditCallReport(pass, made, reopened, retractedNotice, revised, minor.Count, questions, !changes, cleanCount, loop.CleanPasses, checkedLines, converging));
        if (changes)
        {
            return AuditText.RecordedResult(path, made.Select(f => f.Id).ToList(), reopened.Select(f => f.Id).ToList(), retractedNotice.Select(r => r.Id).ToList(),
                revised.Select(f => f.Id).ToList(), report.OpenCount, coverage.Read, coverage.Scope);
        }

        return converging
            ? AuditText.ConvergedResult(pass, cleanCount, coverage.Read, coverage.Scope)
            : AuditText.CleanResult(cleanCount, loop.CleanPasses, coverage.Read, coverage.Scope);
    }

    /// <summary>A covered entry in the one path form when it names a file or folder here, else its words on one line.</summary>
    private string Area(string entry)
    {
        string flat = Cap(AuditReport.Flatten(ToolRedaction.Text(entry)), MaxNoteChars);
        return AuditCommand.LooksLikePath(flat) && AuditPath.Of(_files, flat, out _) is { Length: > 0 } path
            && _files.Resolve(path, forWrite: false, out string full) == FileOutcome.Ok && (File.Exists(full) || Directory.Exists(full))
            ? path
            : flat;
    }

    /// <summary>A cited file as the evidence check reads it: its lines, and their text joined once (2026-10-20, a review: joined again for each quote).</summary>
    private sealed record EvidenceFile(List<string> Lines, string Text);

    /// <summary>
    /// The file's lines (BOM dropped), or null when it cannot be read as text: read once per call into <paramref name="read"/> (2026-10-19, the
    /// second review: twenty findings in one large file read it twenty times).
    /// </summary>
    private EvidenceFile? FileLines(Dictionary<string, EvidenceFile?> read, string file)
    {
        if (!read.TryGetValue(file, out var source))
        {
            var text = _files.ReadText(file, null, null, int.MaxValue);
            if (text.Outcome == FileOutcome.Ok)
            {
                var lines = WorkingDirectory.SplitLines(text.Text);
                source = new EvidenceFile(lines, Joined(lines));
            }

            read[file] = source;
        }

        return source;
    }

    /// <summary>
    /// Why the quote is refused, or null when it stands within <see cref="EvidenceSlack"/> lines of <paramref name="line"/> (the whole file with
    /// no line): whitespace runs collapsed, line endings and a byte-order mark ignored; under <paramref name="wildcard"/> each
    /// <see cref="ToolRedaction.Placeholder"/> matches any run of characters on its line. A hit elsewhere is refused naming its line; a search that
    /// ran out of time says so (2026-10-20, a review: it was read as no match, and a quote that stood was refused as not found).
    /// </summary>
    private static string? EvidenceProblem(string file, EvidenceFile source, string evidence, int? line, bool wildcard)
    {
        var found = EvidenceLinesIn(source.Text, evidence, wildcard, out bool timedOut);
        if (timedOut)
        {
            return AuditText.EvidenceTimedOutResult(file);
        }

        if (found.Count == 0)
        {
            return AuditText.EvidenceNotFoundResult(line is { } at ? file + ":" + N(at) : file);
        }

        if (line is not { } target || found.Any(f => Math.Abs(f - target) <= EvidenceSlack))
        {
            return null;
        }

        return AuditText.EvidenceElsewhereResult(file, target, found.OrderBy(f => Math.Abs(f - target)).First());
    }

    /// <summary>The 1-based lines the quote starts on in <paramref name="lines"/> (at most a few), whitespace forgiven; none when the search ran out of time. Pure.</summary>
    public static IReadOnlyList<int> EvidenceLines(IReadOnlyList<string> lines, string evidence, bool wildcard = false)
    {
        ArgumentNullException.ThrowIfNull(lines);
        return EvidenceLinesIn(Joined(lines), evidence, wildcard, out _);
    }

    /// <summary>The lines joined as the quote is searched in, a byte-order mark dropped.</summary>
    private static string Joined(IReadOnlyList<string> lines) => string.Join('\n', lines).TrimStart('﻿');

    /// <summary>
    /// <see cref="EvidenceLines"/> over the file's joined text, <paramref name="timedOut"/> when the search ran past <see cref="WorkingDirectory.RegexTimeout"/>.
    /// Each hit's line is counted on from the one before (2026-10-20, a review: counted from the file's start each time, 20 hits on a large file
    /// scanned it 20 times).
    /// </summary>
    private static IReadOnlyList<int> EvidenceLinesIn(string text, string evidence, bool wildcard, out bool timedOut)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        timedOut = false;
        string[] tokens = evidence.TrimStart('﻿').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return [];
        }

        var pattern = new StringBuilder();
        for (int i = 0; i < tokens.Length; i++)
        {
            if (i > 0)
            {
                pattern.Append(@"\s+");
            }

            string escaped = Regex.Escape(tokens[i]);
            pattern.Append(wildcard ? escaped.Replace(Regex.Escape(ToolRedaction.Placeholder), @"[^\n]*?", StringComparison.Ordinal) : escaped);
        }

        var starts = new List<int>();
        int counted = 0;
        int newlines = 0;
        try
        {
            var expression = new Regex(pattern.ToString(), RegexOptions.CultureInvariant, WorkingDirectory.RegexTimeout);
            foreach (Match match in expression.Matches(text))
            {
                newlines += text.AsSpan(counted, match.Index - counted).Count('\n');
                counted = match.Index;
                starts.Add(newlines + 1);
                if (starts.Count >= 20)
                {
                    break;
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            timedOut = true;
            return [];
        }

        return starts;
    }

    /// <summary>A short key of what a finding says (its severity, line, detail, fix and quote): two revisions of it match only when they leave it the same.</summary>
    private static string ContentKey(AuditFinding finding)
    {
        string said = string.Join('\u001f', AuditFinding.Word(finding.Severity), finding.Line?.ToString(CultureInfo.InvariantCulture) ?? "", finding.Detail, finding.Fix ?? "", finding.Evidence ?? "");
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(said)).AsSpan(0, 4));
    }

    private static bool EvidenceTooLong(string evidence) =>
        evidence.Length > MaxEvidenceChars || AuditReport.Normalize(evidence).Trim('\n').Split('\n').Length > MaxEvidenceLines;

    private static Audits.AuditSeverity? SeverityOf(string word, string field, out string? error)
    {
        error = null;
        if (AuditFinding.IsNitWord(word))
        {
            error = AuditText.NitSeverityResult(field, word.Trim());
            return null;
        }

        if (AuditFinding.ParseSeverity(word) is { } severity)
        {
            return severity;
        }

        error = AuditText.BadSeverityResult(field, word.Trim());
        return null;
    }

    /// <summary>A dimension's word, case aside, with the few synonyms models use.</summary>
    private static string? DimensionOf(string word) => AuditFocus.Parse(word) ?? word.Trim().ToLowerInvariant() switch
    {
        "bug" or "bugs" or "correct" or "logic" => AuditFocus.Correctness,
        "perf" or "speed" or "efficiency" => AuditFocus.Performance,
        "maintenance" or "readability" or "design" => AuditFocus.Maintainability,
        "test" or "testing" or "coverage" => AuditFocus.Tests,
        "secure" or "safety" => AuditFocus.Security,
        _ => null,
    };

    /// <summary><c>line</c> as an integer, or a string that starts with one (<c>"42"</c>, <c>"42-50"</c> → 42); absent or null is none.</summary>
    private static bool TryLine(JsonElement item, string field, out int? line, out string? error)
    {
        line = null;
        error = null;
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("line", out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) && number > 0)
        {
            line = number;
            return true;
        }

        if (value.ValueKind == JsonValueKind.String && LeadingNumber().Match(value.GetString() ?? "") is { Success: true } match
            && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) && parsed > 0)
        {
            line = parsed;
            return true;
        }

        if (value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString()))
        {
            return true;
        }

        error = AuditText.BadLineResult(field, value.GetRawText());
        return false;
    }

    /// <summary>An object's string field: its text, a number's or a boolean's raw text, empty when absent or null.</summary>
    private static string Field(JsonElement item, string name)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(name, out var value))
        {
            return "";
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Null or JsonValueKind.Undefined => "",
            _ => value.GetRawText(),
        };
    }

    private static string? Optional(JsonElement item, string name) => Field(item, name) is { } text && text.Trim().Length > 0 ? text : null;

    /// <summary>The first field of <paramref name="items"/> holding a pruned stub, named <c>findings[2].detail</c>; null for none.</summary>
    private static string? StubField(IReadOnlyList<JsonElement> items, string list)
    {
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i].ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var property in items[i].EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String && ConversationCompactor.IsPrunedStub(property.Value.GetString()))
                {
                    return $"{list}[{N(i)}].{property.Name}";
                }
            }
        }

        return null;
    }

    private static List<string> Lines(IEnumerable<string> sent) => sent.Select(line => line.Trim()).Where(line => line.Length > 0).ToList();

    /// <summary><paramref name="text"/> cut to <paramref name="max"/> characters with an ellipsis.</summary>
    private static string Cap(string text, int max) => text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^\s*(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingNumber();
}
