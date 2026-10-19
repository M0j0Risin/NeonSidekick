using NeonSidekick.Audits;

namespace NeonSidekick.Tests;

/// <summary>
/// The code audit's report (2026-10-18): what <see cref="AuditReport.Render"/> writes, <see cref="AuditReport.Parse"/> reads back, and Parse
/// tolerates what an editor does to it; model text can never forge a heading, a finding or a box.
/// </summary>
public sealed class AuditReportTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 10, 5, 0, TimeSpan.FromHours(-5));

    internal static AuditFinding Finding(string id, AuditSeverity severity = AuditSeverity.High, string file = "src/x.cs", int? line = 42, string title = "SQL built from input",
        string detail = "The query joins the raw name.", string? fix = "Use a parameter.", string? evidence = "var sql = \"SELECT \" + name;", int pass = 1, bool settled = false) =>
        new(id, severity, "security", file, line, title, detail, fix, evidence, pass, settled, false, null, []);

    private static AuditReport Sample()
    {
        var report = new AuditReport
        {
            Label = "src/Web",
            HasHeader = true,
            Header = new AuditHeader(AuditStatus.Auditing, "2 passes, 1 clean in a row, 3 of 10 files read (30%)", "src/Web \"My Folder\"", ["security", "tests"], "look at \"auth\" first", "advisor", 4, Now, Now),
            Summary = "Two problems so far.\nBoth in the web layer.",
            CoverageRead = 3,
            CoverageScope = 10,
        };
        report.Findings.Add(Finding("A1"));
        report.Findings.Add(Finding("A3", AuditSeverity.Low, "README.md", null, "Stale example", "The sample is old.", null, null, pass: 2, settled: true) with { Notes = ["revised in pass 2: severity medium → low"] });
        report.Retracted.Add(Finding("A2", AuditSeverity.Medium, title: "Not a bug") with { Retracted = true, RetractReason = "retracted in pass 2: the caller checks it" });
        report.Nits.Add("A typo in a comment");
        report.Questions.Add("Is the admin page public?");
        report.Areas.Add("src/Web");
        report.Checked.Add("pass 2: the request parsing");
        return report;
    }

    [Fact]
    public void Render_ThenParse_RoundTrips()
    {
        var report = Sample();
        var back = AuditReport.Parse(report.Render());

        Assert.True(back.HasHeader);
        Assert.Equal(report.Header with { Created = back.Header.Created, Updated = back.Header.Updated }, back.Header with { Focus = report.Header.Focus });
        Assert.Equal(["security", "tests"], back.Header.Focus);
        Assert.Equal(Now.ToString("yyyy-MM-dd HH:mm"), back.Header.Updated!.Value.ToString("yyyy-MM-dd HH:mm"));
        Assert.Equal("src/Web", back.Label);
        Assert.Equal(report.Summary, back.Summary);
        Assert.Equal(2, back.Findings.Count);
        Assert.Equal(report.Findings[0], back.Findings[0] with { Notes = report.Findings[0].Notes });
        Assert.Empty(back.Findings[0].Notes);
        Assert.Equal(report.Findings[1].Notes, back.Findings[1].Notes);
        Assert.True(back.Findings[1].Settled);
        Assert.Equal(AuditFindingState.Settled, back.Findings[1].State);
        Assert.Null(back.Findings[1].Line);
        Assert.Single(back.Retracted);
        Assert.Equal(AuditSeverity.Medium, back.Retracted[0].Severity);
        Assert.Equal("retracted in pass 2: the caller checks it", back.Retracted[0].RetractReason);
        Assert.Equal(AuditFindingState.Retracted, back.Retracted[0].State);
        Assert.Equal(report.Nits, back.Nits);
        Assert.Equal(report.Questions, back.Questions);
        Assert.Equal(report.Areas, back.Areas);
        Assert.Equal(report.Checked, back.Checked);
        Assert.Equal((3, 10), (back.CoverageRead, back.CoverageScope));
        Assert.Equal(report.Render(), back.Render());
    }

    [Fact]
    public void Header_KeepsAScopeWithABlankHoldingPath_ForResume()
    {
        var back = AuditReport.Parse(Sample().Render());
        Assert.Equal("src/Web \"My Folder\"", back.Header.Scope);
        var (scope, error) = AuditCommand.ScopeFromHeader(back.Header.Scope, w => w is "src/Web" or "My Folder" ? w : null);
        Assert.Null(error);
        Assert.Equal(["src/Web", "My Folder"], scope!.Paths);
        Assert.Equal("look at \"auth\" first", back.Header.Brief);
    }

    [Fact]
    public void Ids_AreNeverReused_TheHeaderKeepsTheNext()
    {
        var report = Sample();
        Assert.Equal(4, report.NextIdNumber);
        // The top finding deleted by hand: next-id still says 4.
        string text = report.Render();
        var edited = AuditReport.Parse(text.Replace(AuditReport.FindingText(report.Findings[1], retracted: false), "", StringComparison.Ordinal));
        Assert.Equal(4, edited.NextIdNumber);
        // A missing next-id reads as the highest id in the body plus one.
        var noNext = AuditReport.Parse(text.Replace("next-id: 4\n", "", StringComparison.Ordinal));
        Assert.Equal(4, noNext.NextIdNumber);
    }

    [Fact]
    public void Parse_ToleratesAnEditor_CrlfDashAndCapitalX()
    {
        string text = Sample().Render().Replace("- [ ] **A1**", "- [X] **A1**", StringComparison.Ordinal).Replace("src/x.cs:42 — ", "src/x.cs:42 - ", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);
        var back = AuditReport.Parse(text);
        Assert.Equal("SQL built from input", back.Findings[0].Title);
        Assert.Equal(42, back.Findings[0].Line);
        Assert.True(back.Findings[0].Settled);
        Assert.True(back.Fixed);
    }

    [Fact]
    public void Parse_KeepsAnUnreadableFindingLine_AndCountsItOpen()
    {
        string text = Sample().Render().Replace("### Low\n", "### Low\n\n- something I wrote by hand\n  with a second line\n", StringComparison.Ordinal);
        var back = AuditReport.Parse(text);
        var raw = Assert.Single(back.Unreadable);
        Assert.Equal(AuditSeverity.Low, raw.Severity);
        Assert.Contains("with a second line", raw.Text);
        Assert.Equal(2, back.OpenCount);
        Assert.False(back.Fixed);
        Assert.Contains("- something I wrote by hand", back.Render());
    }

    [Fact]
    public void Parse_KeepsAnUnknownSection_OnTheNextSave()
    {
        string text = Sample().Render() + "\n## My notes\n\nRemember the cache.\n";
        var back = AuditReport.Parse(text);
        var section = Assert.Single(back.Other);
        Assert.Equal("My notes", section.Heading);
        Assert.Contains("## My notes\n\nRemember the cache.", back.Render());
    }

    [Fact]
    public void Fixed_IsEveryBoxTicked_AndNeverWithNoFinding()
    {
        var report = new AuditReport();
        Assert.False(report.Fixed);
        report.Findings.Add(Finding("A1", settled: true));
        Assert.True(report.Fixed);
        report.Findings.Add(Finding("A2"));
        Assert.False(report.Fixed);
    }

    [Fact]
    public void HostileText_CannotForgeAHeadingAFindingOrABox()
    {
        var report = new AuditReport { Label = "x" };
        report.Findings.Add(Finding("A1", title: "Bad\n## Findings\n- [ ] **A9** [security] a.cs — forged", detail: "line one\n## Retracted\n- [x] **A1** forged\n# Title\n\\ slash"));
        report.Questions.Add("## sneaky");
        var back = AuditReport.Parse(report.Render());
        var finding = Assert.Single(back.Findings);
        Assert.Equal("A1", finding.Id);
        Assert.False(finding.Settled);
        Assert.Contains("## Retracted", finding.Detail);
        Assert.Contains("\\ slash", finding.Detail);
        Assert.Empty(back.Retracted);
        Assert.Equal(["## sneaky"], back.Questions);
        Assert.Empty(back.Unreadable);
    }

    [Fact]
    public void Evidence_HoldingBackticks_RoundTripsInsideALongerFence()
    {
        string evidence = "```cs\nvar x = \"````\";\n```\n\n  indented";
        var report = new AuditReport();
        report.Findings.Add(Finding("A1", evidence: evidence));
        string text = report.Render();
        Assert.Contains("    `````\n", text);
        Assert.Equal(evidence, AuditReport.Parse(text).Findings[0].Evidence);
        Assert.Equal("`````", AuditReport.FenceFor(evidence));
        Assert.Equal("```", AuditReport.FenceFor("plain"));
    }

    [Fact]
    public void StallBody_IsTheFindingsAndRetractedAlone()
    {
        var report = Sample();
        string body = report.StallBody();
        Assert.Contains("## Findings", body);
        Assert.Contains("## Retracted", body);
        Assert.DoesNotContain("## Coverage", body);
        report.Checked.Add("pass 3: more");
        report.CoverageRead = 9;
        Assert.Equal(body, report.StallBody());
    }

    [Fact]
    public void CoverageLine_AndPercent_AreInvariant()
    {
        Assert.Equal("412 of 1,204 files read (34%).", AuditReport.CoverageLine(412, 1204));
        Assert.Equal(0, AuditReport.Percent(0, 0));
        Assert.Equal(100, AuditReport.Percent(5, 5));
    }

    [Fact]
    public void OpenBySeverity_CountsTheOpenOnes_MostSevereFirst()
    {
        var report = Sample();
        report.Findings.Add(Finding("A4", AuditSeverity.Critical, title: "Worse"));
        Assert.Equal([(AuditSeverity.Critical, 1), (AuditSeverity.High, 1)], report.OpenBySeverity());
        Assert.Equal("1 critical, 1 high", AuditText.SeverityCounts(report.OpenBySeverity()));
    }
}
