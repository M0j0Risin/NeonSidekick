using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Audits;
using NeonSidekick.Files;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Plans;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// <c>present_audit</c> (2026-10-18): its description and schema (prompt text, pinned), every check in its order, evidence matched where the
/// finding points, redaction, the report read fresh each call, open/settled/retracted, and what it records in the loop.
/// </summary>
public sealed class PresentAuditToolTests : IDisposable
{
    private const string Source = "using System;\n\nclass X\n{\n    void Run(string name)\n    {\n        var sql = \"SELECT * FROM t WHERE n = '\" + name + \"'\";\n        Execute(sql);\n    }\n}\n";

    private readonly AuditSandbox _box = new();
    private readonly AuditSession _audit;
    private readonly PresentAuditTool _tool;
    private readonly List<AuditCallReport> _reported = [];
    private readonly string _path;
    private PlanGapLoop _loop;

    public PresentAuditToolTests()
    {
        _box.Write("src/x.cs", Source);
        _box.Write("src/y.cs", string.Concat(Enumerable.Range(1, 60).Select(i => $"line {i}\n")));
        _box.Write(".neon/plans/p.md", "plan");
        _audit = new AuditSession(_box.Files);
        _tool = new PresentAuditTool(_audit, _box.Files, _box.Time, _reported.Add);
        var scope = AuditScopes.Resolve(_box.Files, _box.Git, new AuditScopeRequest(AuditScopeKind.Tree, []), CancellationToken.None).Scope!;
        _path = AuditFiles.Choose(_box.Files, scope.NameLabel, _box.Time.GetLocalNow());
        AuditFiles.Save(_box.Files, _path, new AuditReport { Label = scope.Label }, _box.Time.GetLocalNow());
        _loop = Start(scope);
    }

    public void Dispose() => _box.Dispose();

    private PlanGapLoop Start(AuditScope scope, int limit = 5, int clean = 2)
    {
        var loop = _audit.Start(_path, scope, AuditFocus.Dimensions, "", PlanReviewer.Model, limit, clean);
        loop.BeginPass(Report().StallBody());
        return loop;
    }

    private AuditReport Report() => AuditFiles.Read(_box.Files, _path)!;

    private static AIFunctionArguments Args(string json)
    {
        using var document = JsonDocument.Parse(json);
        return new AIFunctionArguments(document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone()));
    }

    /// <summary>A JSON string literal of <paramref name="text"/>.</summary>
    private static string Q(string text) => "\"" + JsonEncodedText.Encode(text, System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping).Value + "\"";

    private string Call(string json) => (string)_tool.InvokeAsync(Args(json)).AsTask().GetAwaiter().GetResult()!;

    private void Read(string path = "src/x.cs") => new ReadFileTool(_box.Files).Describe(path, null, null);

    private static string FindingJson(string file = "src/x.cs", int? line = 7, string title = "SQL built from input", string severity = "high", string dimension = "security",
        string detail = "The name is joined into the query.", string? evidence = null) =>
        "{\"severity\":\"" + severity + "\",\"dimension\":\"" + dimension + "\",\"file\":" + Q(file)
        + (line is { } l ? ",\"line\":" + l : "") + ",\"title\":" + Q(title)
        + ",\"detail\":" + Q(detail)
        + (evidence is null ? "" : ",\"evidence\":" + Q(evidence)) + "}";

    private string Report1(string finding) => Call("{\"findings\":[" + finding + "]}");

    [Fact]
    public void Description_AndSchema_ArePinned()
    {
        Assert.Equal(AuditText.ToolDescription, _tool.Description);
        Assert.Contains("Report in batches of at most 20 findings a call.", _tool.Description);
        Assert.Contains("A pass that finds nothing new calls it with checked.", _tool.Description);
        string schema = _tool.JsonSchema.GetRawText();
        foreach (string text in new[] { AuditText.FindingsDescription, AuditText.SeverityDescription, AuditText.DimensionDescription, AuditText.FileDescription, AuditText.LineDescription, AuditText.TitleDescription,
                     AuditText.DetailDescription, AuditText.FixDescription, AuditText.EvidenceDescription, AuditText.RetractDescription, AuditText.ReviseDescription, AuditText.MinorDescription,
                     AuditText.QuestionsDescription, AuditText.CoveredDescription, AuditText.CheckedDescription, AuditText.SummaryDescription })
        {
            Assert.Contains(text, schema);
        }

        Assert.Contains("\"required\": [\"severity\", \"dimension\", \"file\", \"title\", \"detail\"]", schema);
    }

    [Fact]
    public void AFinding_OnAFileRead_IsSaved_WithAnId_AndRecorded()
    {
        Read();
        string result = Report1(FindingJson(evidence: "var sql = \"SELECT * FROM t WHERE n = '\" + name + \"'\";"));
        Assert.StartsWith("Recorded in " + _path + ": A1 new. 1 finding open;", result);
        var finding = Assert.Single(Report().Findings);
        Assert.Equal(("A1", "src/x.cs", 7, 1), (finding.Id, finding.File, finding.Line!.Value, finding.Pass));
        Assert.Equal(["src/x.cs — SQL built from input"], _loop.PassGaps);
        Assert.Equal(2, Report().NextIdNumber);
        var call = Assert.Single(_reported);
        Assert.Equal("A1", Assert.Single(call.Added).Id);
        Assert.Equal("🔎 Pass 1: A1 [high] src/x.cs:7 — SQL built from input", AuditText.FindingNotice(1, call.Added[0]));
    }

    [Fact]
    public void ASecretLookingFile_TakesNoEvidence()
    {
        // 2026-10-19, the review: the quote went into the report verbatim, redacted only by pattern.
        _box.Write(".env", "API_TOKEN=abc123xyz\n");
        Read(".env");
        Assert.Equal(AuditText.SecretEvidenceResult("findings[0].evidence", ".env"),
            Report1(FindingJson(file: ".env", line: 1, title: "A token in .env", evidence: "API_TOKEN=abc123xyz")));
        Assert.StartsWith("Recorded", Report1(FindingJson(file: ".env", line: 1, title: "A token in .env")));
        Assert.Equal(AuditText.SecretEvidenceResult("revise[0].evidence", ".env"),
            Call("{\"revise\":[{\"id\":\"A1\",\"evidence\":\"API_TOKEN=abc123xyz\"}]}"));
        Assert.DoesNotContain("abc123xyz", File.ReadAllText(_box.Full(_path)));
    }

    [Fact]
    public void ADuplicateId_FromAHandEdit_NeverStopsTheTool()
    {
        // 2026-10-19, the review: a copied block made the open findings' map throw on every later call.
        Read();
        Report1(FindingJson());
        string text = File.ReadAllText(_box.Full(_path));
        string block = AuditReport.FindingText(Report().Findings[0], retracted: false);
        File.WriteAllText(_box.Full(_path), text.Replace(block, block + "\n" + block.Replace("SQL built from input", "A copy", StringComparison.Ordinal), StringComparison.Ordinal));
        Assert.StartsWith("Recorded", Call("{\"retract\":[{\"id\":\"A1\",\"reason\":\"a duplicate\"}]}"));
        var copy = Assert.Single(Report().Findings);
        Assert.Equal(("A2", "A copy"), (copy.Id, copy.Title));
        Assert.Equal(3, Report().NextIdNumber);
    }

    [Fact]
    public void AWindowedRead_IsEnough_ButNoReadIsRefused_AndADiffIsNoRead()
    {
        Assert.Equal(AuditText.ReadFirstResult("src/x.cs"), Report1(FindingJson()));
        new ReadFileTool(_box.Files).Describe("src/x.cs", 5, 3);
        Assert.True(_box.Files.WasViewed("src/x.cs"));
        Assert.False(_box.Files.WasSeen("src/x.cs"));
        Assert.StartsWith("Recorded", Report1(FindingJson()));
    }

    [Fact]
    public void OneFile_InEverySpelling_IsOnePath_AndADuplicate()
    {
        Read();
        Assert.StartsWith("Recorded", Report1(FindingJson(file: "src\\x.cs")));
        Assert.Equal("src/x.cs", Report().Findings[0].File);
        Assert.Equal(AuditText.DuplicateResult("findings[0]", "A1"), Report1(FindingJson(file: "./src/x.cs", title: "sql BUILT from input")));
        Assert.Equal(AuditText.DuplicateResult("findings[0]", "A1"), Report1(FindingJson(file: _box.Full("src/x.cs"))));
        Assert.Equal(AuditText.OutsideResult("../outside.cs"), Report1(FindingJson(file: "../outside.cs")));
    }

    [WindowsFact]
    public void ACaseVariant_IsTheSameFile_OnWindows()
    {
        Read();
        Report1(FindingJson());
        Assert.Equal(AuditText.DuplicateResult("findings[0]", "A1"), Report1(FindingJson(file: "SRC/X.cs")));
    }

    [UnixFact]
    public void ACaseVariant_IsAnotherFile_OffWindows()
    {
        Read();
        Report1(FindingJson());

        // A case-sensitive volume has no SRC/X.cs; a Mac's, case-insensitive by default, finds src/x.cs under it, which was never read by
        // that name (2026-10-18, the merge into mac-pass2: this failed on the Mac). Either way the finding is refused.
        bool sameFile = File.Exists(_box.Full("SRC/X.cs"));
        Assert.Equal(sameFile ? AuditText.ReadFirstResult("SRC/X.cs") : AuditText.NoFileResult("SRC/X.cs"), Report1(FindingJson(file: "SRC/X.cs")));
    }

    [Fact]
    public void TheRefusals_InTheirOrder()
    {
        Read();
        Assert.Equal(AuditText.TooManyFindingsResult(21), Call("{\"findings\":[" + string.Join(",", Enumerable.Range(1, 21).Select(i => FindingJson(title: "T" + i))) + "]}"));
        Assert.Equal(AuditText.StubSentResult("findings"), Call("{\"findings\":" + Q(ConversationCompactor.PrunedArgumentStub(3000)) + "}"));
        Assert.Equal(AuditText.StubSentResult("findings[0].detail"), Report1(FindingJson(detail: ConversationCompactor.PrunedArgumentStub(2500))));
        Assert.StartsWith("Error: findings must be a list of objects", Call("{\"findings\":\"not a list\"}"));
        Assert.StartsWith("Error: findings must be a list of objects", Call("{\"findings\":[\"a\",\"b\"]}"));
        Assert.Equal(AuditText.MissingFieldResult("findings[0].title"), Report1(FindingJson(title: "")));
        Assert.Equal(AuditText.MissingFieldResult("findings[0].detail"), Report1(FindingJson(detail: "   ")));
        Assert.Equal(AuditText.BadSeverityResult("findings[0].severity", "dire"), Report1(FindingJson(severity: "dire")));
        Assert.Equal(AuditText.NitSeverityResult("findings[0].severity", "nit"), Report1(FindingJson(severity: "nit")));
        Assert.Equal(AuditText.BadDimensionResult("findings[0].dimension", "vibes"), Report1(FindingJson(dimension: "vibes")));
        Assert.Equal(AuditText.AppFolderFindingResult(".neon/plans/p.md"), Report1(FindingJson(file: ".neon/plans/p.md")));
        Assert.Equal(AuditText.LineOutOfRangeResult("findings[0].line", "src/x.cs", 99, 10), Report1(FindingJson(line: 99)));
        Assert.Equal(AuditText.UnknownIdResult("retract[0].id", "A9"), Call("{\"retract\":[{\"id\":\"A9\",\"reason\":\"no\"}]}"));
        Assert.Equal(AuditText.CheckedRequiredResult, Call("{\"minor\":[\"a nit\"]}"));
        Assert.Empty(Report().Findings);
        Assert.Empty(_loop.PassGaps);
    }

    [Fact]
    public void AListSentAsAJsonString_IsRead()
    {
        Read();
        Assert.StartsWith("Recorded", Call("{\"findings\":" + Q("[" + FindingJson() + "]") + "}"));
    }

    [Fact]
    public void Twenty_AreAccepted()
    {
        Read();
        Read("src/y.cs");
        Assert.StartsWith("Recorded", Call("{\"findings\":[" + string.Join(",", Enumerable.Range(1, 20).Select(i => FindingJson(file: "src/y.cs", line: i, title: "T" + i))) + "]}"));
        Assert.Equal(20, Report().Findings.Count);
    }

    [Theory]
    [InlineData("severe", AuditSeverity.High)]
    [InlineData("MAJOR", AuditSeverity.High)]
    [InlineData("Critical", AuditSeverity.Critical)]
    [InlineData("minor", AuditSeverity.Low)]
    public void SeveritySynonyms(string word, AuditSeverity expected)
    {
        Read();
        Report1(FindingJson(severity: word));
        Assert.Equal(expected, Report().Findings[0].Severity);
    }

    [Theory]
    [InlineData("\"7\"", 7)]
    [InlineData("\"7-9\"", 7)]
    [InlineData("7", 7)]
    public void Line_AsANumberOrAStringThatStartsWithOne(string raw, int expected)
    {
        Read();
        Report1(FindingJson().Replace("\"line\":7", "\"line\":" + raw, StringComparison.Ordinal));
        Assert.Equal(expected, Report().Findings[0].Line);
    }

    [Fact]
    public void Line_ThatIsNoNumber_IsRefused()
    {
        Read();
        Assert.Equal(AuditText.BadLineResult("findings[0].line", "\"about here\""), Report1(FindingJson().Replace("\"line\":7", "\"line\":\"about here\"", StringComparison.Ordinal)));
    }

    [Fact]
    public void Evidence_MatchedWhitespaceForgiven_ElseRefused()
    {
        Read();
        Read("src/y.cs");
        Assert.StartsWith("Recorded", Report1(FindingJson(evidence: "Execute(sql);\r\n    }")));
        Assert.StartsWith("Recorded", Report1(FindingJson(line: 5, title: "Two", evidence: "void   Run(string\t  name)")));
        Assert.Equal(AuditText.EvidenceNotFoundResult("src/x.cs:7"), Report1(FindingJson(title: "Three", evidence: "DropTable(name);")));
        Assert.Equal(AuditText.EvidenceElsewhereResult("src/y.cs", 10, 50), Report1(FindingJson(file: "src/y.cs", line: 10, title: "Far", evidence: "line 50")));
        Assert.Equal(AuditText.EvidenceTooLongResult("findings[0].evidence"), Report1(FindingJson(title: "Long", evidence: new string('x', 401))));
        Assert.Equal(AuditText.EvidenceTooLongResult("findings[0].evidence"), Report1(FindingJson(title: "Tall", evidence: string.Join('\n', Enumerable.Repeat("a", 9)))));
        Assert.StartsWith("Recorded", Report1(FindingJson(line: null, title: "Whole file, no quote")));
    }

    [Fact]
    public void Evidence_OnLineOne_OfAFileWithABom()
    {
        File.WriteAllBytes(_box.Full("src/bom.cs"), [0xEF, 0xBB, 0xBF, .. "namespace Bom;\nclass B { }\n"u8]);
        var scope = AuditScopes.Resolve(_box.Files, _box.Git, new AuditScopeRequest(AuditScopeKind.Tree, []), CancellationToken.None).Scope!;
        _loop = Start(scope);
        Read("src/bom.cs");
        Assert.StartsWith("Recorded", Report1(FindingJson(file: "src/bom.cs", line: 1, evidence: "﻿namespace Bom;")));
    }

    [Fact]
    public void Secrets_AreMatchedRaw_ButSavedAndShownRedacted()
    {
        _box.Write("src/conf.cs", "var url = \"https://admin:hunter2@db.example/x\";\n// Authorization: Bearer abc.def-ghi\n");
        Read("src/conf.cs");
        string result = Report1(FindingJson(file: "src/conf.cs", line: 1, title: "Committed password",
            detail: "The URL https://admin:hunter2@db.example/x and Authorization: Bearer abc.def-ghi are in the source.",
            evidence: "var url = \"https://admin:hunter2@db.example/x\";"));
        Assert.StartsWith("Recorded", result);
        string text = File.ReadAllText(_box.Full(_path));
        Assert.DoesNotContain("hunter2", text);
        Assert.DoesNotContain("abc.def-ghi", text);
        Assert.Contains(ToolRedaction.Placeholder, text);
        Assert.DoesNotContain("hunter2", AuditText.FindingNotice(1, _reported[0].Added[0]));
        // Revising its line re-checks the saved, redacted quote: the placeholder matches the secret on its line.
        _box.Write("src/conf.cs", "\n" + File.ReadAllText(_box.Full("src/conf.cs")));
        Read("src/conf.cs");
        Assert.StartsWith("Recorded", Call("{\"revise\":[{\"id\":\"A1\",\"line\":2}]}"));
    }

    [Fact]
    public void EvidenceLines_CountsEachHitsLine_OnFromTheLast()
    {
        // 2026-10-20, a review: each hit counted its line from the file's start.
        Assert.Equal([2, 4, 5], PresentAuditTool.EvidenceLines(["a {", "}", "b {", "}", "}"], "}"));
        Assert.Equal([1, 3], PresentAuditTool.EvidenceLines(["a {", "}", "a {", "}"], "a {\n }"));
    }

    [Fact]
    public void Revise_ReRatesInPlace_MovedLineReChecked_UnknownIdRefused()
    {
        Read();
        Report1(FindingJson(evidence: "Execute(sql);"));
        Assert.Equal(AuditText.UnknownIdResult("revise[0].id", "A7"), Call("{\"revise\":[{\"id\":\"A7\",\"severity\":\"low\"}]}"));
        Assert.StartsWith("Recorded in " + _path + ": A1 revised.", Call("{\"revise\":[{\"id\":\"A1\",\"severity\":\"medium\",\"line\":8}]}"));
        var finding = Report().Findings[0];
        Assert.Equal((AuditSeverity.Medium, 8), (finding.Severity, finding.Line!.Value));
        Assert.Contains("revised in pass 1: severity high → medium; line 7 → 8", finding.Notes);
        // The key names what the revision left (2026-10-20, a review): two passes that revise A1 differently are not the same gaps.
        Assert.Contains(_loop.PassGaps, g => g.StartsWith("revise A1 ", StringComparison.Ordinal));
        Assert.Equal(AuditText.EvidenceElsewhereResult("src/x.cs", 1, 8), Call("{\"revise\":[{\"id\":\"A1\",\"line\":1}]}"));
    }

    [Fact]
    public void RetractThenReAdd_InOneCall_AndARetractedOneComesBackUnderANewId()
    {
        Read();
        Report1(FindingJson());
        string both = Call("{\"retract\":[{\"id\":\"A1\",\"reason\":\"wrong line\"}],\"findings\":[" + FindingJson(line: 8) + "]}");
        Assert.StartsWith("Recorded in " + _path + ": A2 new; A1 retracted.", both);
        var report = Report();
        Assert.Equal("A2", Assert.Single(report.Findings).Id);
        Assert.Equal("retracted in pass 1: wrong line", Assert.Single(report.Retracted).RetractReason);
        Assert.Equal(3, report.NextIdNumber);
    }

    [Fact]
    public void ASettledFinding_ReportedAgain_IsReopenedWithItsId()
    {
        Read();
        Report1(FindingJson());
        File.WriteAllText(_box.Full(_path), File.ReadAllText(_box.Full(_path)).Replace("- [ ] **A1**", "- [x] **A1**", StringComparison.Ordinal));
        Assert.Equal(AuditFindingState.Settled, Report().Findings[0].State);
        Assert.StartsWith("Recorded in " + _path + ": A1 reopened.", Report1(FindingJson(detail: "Still joined after the fix.")));
        var finding = Report().Findings[0];
        Assert.Equal(AuditFindingState.Open, finding.State);
        Assert.Contains("reopened in pass 1: Still joined after the fix.", finding.Notes);
        Assert.Contains(_loop.PassGaps, g => g.StartsWith("reopen A1 ", StringComparison.Ordinal));
    }

    [Fact]
    public void ASettledFinding_ReportedAgainElsewhere_TakesTheNewSeverityLineAndEvidence()
    {
        // 2026-10-19, the second review: the reopened finding kept its old severity, line and quote; the re-report's were checked and thrown away.
        Read();
        Report1(FindingJson(severity: "low", line: 7, evidence: "var sql = \"SELECT * FROM t WHERE n = '\" + name + \"'\";"));
        File.WriteAllText(_box.Full(_path), File.ReadAllText(_box.Full(_path)).Replace("- [ ] **A1**", "- [x] **A1**", StringComparison.Ordinal));
        Assert.StartsWith("Recorded in " + _path + ": A1 reopened.", Report1(FindingJson(severity: "critical", line: 8, detail: "Still joined.", evidence: "Execute(sql);")));
        var finding = Report().Findings[0];
        Assert.Equal((AuditSeverity.Critical, 8, "Execute(sql);", "Still joined."), (finding.Severity, finding.Line!.Value, finding.Evidence, finding.Detail));
        Assert.Contains("reopened in pass 1: Still joined. (was low, line 7)", finding.Notes);
    }

    [Fact]
    public void Revise_AFindingWithNoLine_TakesEvidenceFromAnywhereInTheFile()
    {
        // 2026-10-19, the second review: the target fell back to line 0, so a quote past line 6 was refused as elsewhere.
        Read("src/y.cs");
        Report1(FindingJson(file: "src/y.cs", line: null, title: "Whole file"));
        Assert.StartsWith("Recorded", Call("{\"revise\":[{\"id\":\"A1\",\"evidence\":\"line 50\"}]}"));
        Assert.Equal("line 50", Report().Findings[0].Evidence);
        Assert.Null(Report().Findings[0].Line);
    }

    [Fact]
    public void EditsOnDisk_BetweenCalls_AreKept()
    {
        Read();
        Read("src/y.cs");
        Report1(FindingJson());
        string text = File.ReadAllText(_box.Full(_path)).Replace("The name is joined into the query.", "Reworded by hand.", StringComparison.Ordinal);
        File.WriteAllText(_box.Full(_path), text.Replace("\n", "\r\n", StringComparison.Ordinal));
        Report1(FindingJson(file: "src/y.cs", line: 3, title: "Another"));
        var report = Report();
        Assert.Equal("Reworded by hand.", report.Findings[0].Detail);
        Assert.Equal(2, report.Findings.Count);
    }

    [Fact]
    public void TheReportGone_EndsThePass_AndAFailedSave_RecordsNothing()
    {
        Read();
        // Windows refuses to replace a read-only file; a Mac's save writes beside it and renames over it, which only the folder's own
        // write permission stops (2026-10-18, the merge into mac-pass2: this failed on the Mac), so there the folder is made read-only.
        string report = _box.Full(_path);
        string folder = Path.GetDirectoryName(report)!;
        UnixFileMode? kept = null;
        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(report, FileAttributes.ReadOnly);
        }
        else
        {
            kept = File.GetUnixFileMode(folder);
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }

        try
        {
            string failed = Report1(FindingJson());
            Assert.StartsWith("Error: the report could not be saved", failed);
            Assert.Empty(_loop.PassGaps);
            Assert.Empty(_reported);
        }
        finally
        {
            if (OperatingSystem.IsWindows())
            {
                File.SetAttributes(report, FileAttributes.Normal);
            }
            else
            {
                File.SetUnixFileMode(folder, kept!.Value);
            }
        }

        File.Delete(_box.Full(_path));
        Assert.Equal(AuditText.ReportGoneResult(_path), Report1(FindingJson()));
    }

    [Fact]
    public void Clean_ThenConverged_ThenRefused()
    {
        Read();
        Assert.StartsWith("Nothing new this pass (1 of 2 clean passes in a row)", Call("{\"checked\":[\"the queries\"],\"minor\":[\"a typo\"]}"));
        Assert.True(_loop.PassClean);
        Assert.Equal(["pass 1: the queries"], Report().Checked);
        Assert.Equal(["a typo"], Report().Nits);
        Assert.Null(_loop.Next(endedOnItsOwn: true, userSpoke: false, Report().StallBody()));
        _loop.BeginPass(Report().StallBody());
        Assert.StartsWith("Nothing new: the audit converged after 2 passes (2 clean passes in a row)", Call("{\"checked\":[\"the rest\"]}"));
        Assert.True(_loop.Converged);
        Assert.Equal(AuditText.AlreadyConvergedResult, Call("{\"checked\":[\"more\"]}"));
        Assert.True(_reported[^1].Converged);
    }

    [Fact]
    public void SeveralCalls_AddUp_InOnePass_AndNitsKeepTheStreak()
    {
        Read();
        Read("src/y.cs");
        Report1(FindingJson());
        Report1(FindingJson(file: "src/y.cs", line: 2, title: "Second"));
        Assert.Equal(2, _loop.PassGaps.Count);
        Assert.Equal(2, _reported.Count);
    }

    [Fact]
    public void OutsideAPass_ItAnswersNotAuditing()
    {
        _audit.Stop();
        Assert.Equal(AuditText.NotAuditingResult, Call("{\"checked\":[\"x\"]}"));
    }

    [Fact]
    public void ADeletedFile_InAGitScope_IsTakenWithoutARead_NoLineNoQuote()
    {
        GitAccessTests.Init(_box.Root);
        GitAccessTests.CommitFile(_box.Root, "old.cs", "gone soon", "first");
        File.Delete(_box.Full("old.cs"));
        _box.Write("kept.cs", "x");
        var scope = AuditScopes.Resolve(_box.Files, _box.Git, new AuditScopeRequest(AuditScopeKind.Diff, []), CancellationToken.None).Scope!;
        Assert.Equal(["old.cs"], scope.Deleted);
        _loop = Start(scope);
        Assert.StartsWith("Recorded", Report1(FindingJson(file: "old.cs", line: null, title: "Its check went with it")));
        Assert.Equal(AuditText.DeletedFileResult("old.cs"), Report1(FindingJson(file: "old.cs", line: 1, title: "With a line")));
    }

    [Fact]
    public void FieldCaps_CutTheText_AndAFullReportIsRefused()
    {
        Read();
        Report1(FindingJson(title: new string('t', 300), detail: new string('d', 3000)));
        var finding = Report().Findings[0];
        Assert.Equal(PresentAuditTool.MaxTitleChars, finding.Title.Length);
        Assert.EndsWith("…", finding.Title);
        Assert.Equal(PresentAuditTool.MaxDetailChars, finding.Detail.Length);

        // A report near the cap: a call that would pass it is refused, the file untouched.
        var report = Report();
        report.Summary = new string('s', WorkingDirectory.MaxWriteChars - report.Render().Length - 50);
        AuditFiles.Save(_box.Files, _path, report, _box.Time.GetLocalNow());
        string before = File.ReadAllText(_box.Full(_path));

        // The sidecar too (2026-10-19, the review: it was merged before the size check).
        Read("src/y.cs");
        string sidecar = File.ReadAllText(_box.Full(AuditFiles.CoveragePath(_path)));
        Assert.Equal(AuditText.ReportFullResult, Report1(FindingJson(title: "One more", detail: new string('d', 1000))));
        Assert.Equal(before, File.ReadAllText(_box.Full(_path)));
        Assert.Equal(sidecar, File.ReadAllText(_box.Full(AuditFiles.CoveragePath(_path))));
        Assert.DoesNotContain("y.cs", sidecar);
    }

    [Fact]
    public void TheDirective_ListsSettledFindingsByCountOnly()
    {
        Read();
        Report1(FindingJson());
        Report1(FindingJson(line: 8, title: "Second"));
        File.WriteAllText(_box.Full(_path), File.ReadAllText(_box.Full(_path)).Replace("- [ ] **A1**", "- [x] **A1**", StringComparison.Ordinal));
        string directive = _audit.Turn(_tool, askUser: true, 40, 10_000)!.Directive;
        Assert.Contains("A2 ⟪Second", directive);
        Assert.DoesNotContain("A1 SQL", directive);
        Assert.Contains("- Settled findings (ticked in the report): 1;", directive);
    }
}
