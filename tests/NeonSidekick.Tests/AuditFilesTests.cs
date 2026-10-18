using NeonSidekick.Audits;
using NeonSidekick.Files;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>The code audit's reports on disk (2026-10-18): names, saves, the list newest first, the coverage sidecar, Discard.</summary>
public sealed class AuditFilesTests : IDisposable
{
    private readonly AuditSandbox _box = new();

    public void Dispose() => _box.Dispose();

    private string Save(string label, DateTimeOffset updated, AuditStatus status = AuditStatus.Converged)
    {
        string path = AuditFiles.Choose(_box.Files, label, updated);
        var report = new AuditReport { Label = label, Header = new AuditHeader(status, "1 pass", "", AuditFocus.Dimensions, "", "model", 1, updated, updated) };
        Assert.Null(AuditFiles.Save(_box.Files, path, report, updated));
        return path;
    }

    [Fact]
    public void Choose_NamesByLabelAndDate_ThenNumbers()
    {
        var day = new DateTimeOffset(2026, 10, 18, 9, 0, 0, TimeSpan.Zero);
        Assert.Equal(".neon/audits/src-web-2026-10-18.md", Save("src/Web", day));
        Assert.Equal(".neon/audits/src-web-2026-10-18-2.md", Save("src/Web", day));
        Assert.Equal(".neon/audits/tree-2026-10-18.md", AuditFiles.Choose(_box.Files, "tree", day));
    }

    [Fact]
    public void List_NewestFirstByUpdated_ThenWriteTime_ATieByName()
    {
        var older = Save("a", new DateTimeOffset(2026, 10, 17, 9, 0, 0, TimeSpan.Zero));
        var newer = Save("b", new DateTimeOffset(2026, 10, 18, 9, 0, 0, TimeSpan.Zero));
        var tie = Save("c", new DateTimeOffset(2026, 10, 18, 9, 0, 0, TimeSpan.Zero));
        // A hand-made report with no header: its write time decides.
        string bare = Path.Combine(_box.Root, ".neon", "audits", "hand.md");
        File.WriteAllText(bare, "# Code audit: by hand\n\n## Findings\n");
        File.SetLastWriteTime(bare, new DateTime(2026, 10, 1));
        var listed = AuditFiles.List(_box.Files).Select(l => l.Path).ToList();
        Assert.Equal([newer, tie, older, ".neon/audits/hand.md"], listed);
        // A sidecar is never listed.
        AuditFiles.MergeCoverage(_box.Files, newer, ["src/a.cs"]);
        Assert.Equal(4, AuditFiles.List(_box.Files).Count);
    }

    [Fact]
    public void List_AnAuditingReportWithNoLiveLoop_ListsAsStopped()
    {
        string path = Save("x", DateTimeOffset.UtcNow, AuditStatus.Auditing);
        var listing = AuditFiles.List(_box.Files).Single();
        Assert.Equal("stopped", AuditText.StatusLabel(listing));
        Assert.Equal("auditing", AuditText.StatusLabel(AuditFiles.List(_box.Files, path).Single()));
    }

    [Fact]
    public void Resolve_TakesTheNameInItsForms()
    {
        string path = Save("x", DateTimeOffset.UtcNow);
        string name = Path.GetFileNameWithoutExtension(path);
        Assert.Equal(path, AuditFiles.Resolve(_box.Files, name));
        Assert.Equal(path, AuditFiles.Resolve(_box.Files, name + ".md"));
        Assert.Equal(path, AuditFiles.Resolve(_box.Files, "audits/" + name));
        Assert.Equal(path, AuditFiles.Resolve(_box.Files, path));
        Assert.Null(AuditFiles.Resolve(_box.Files, "nope"));
        Assert.Null(AuditFiles.Resolve(_box.Files, "../x"));
    }

    [Fact]
    public void Coverage_RoundTrips_GroupedByFolder_AndMergesAcrossRuns()
    {
        string path = Save("x", DateTimeOffset.UtcNow);
        AuditFiles.MergeCoverage(_box.Files, path, ["src/a.cs", "README.md"]);
        AuditFiles.MergeCoverage(_box.Files, path, ["src/b.cs", "src/a.cs"]);
        var read = AuditFiles.ReadCoverage(_box.Files, path);
        Assert.Equal(3, read.Count);
        Assert.Contains("src/b.cs", read);
        string text = File.ReadAllText(Path.Combine(_box.Root, ".neon", "audits", Path.GetFileNameWithoutExtension(path) + ".coverage.txt"));
        Assert.Equal("./\n  README.md\nsrc/\n  a.cs\n  b.cs\n", text.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    [Fact]
    public void Coverage_FiveThousandPaths_FitUnderTheCap()
    {
        var paths = Enumerable.Range(0, 5000).Select(i => $"src/Area{i % 40}/Sub{i % 7}/File{i}.cs").ToList();
        string text = AuditFiles.RenderCoverage(paths);
        Assert.True(text.Length < WorkingDirectory.MaxWriteChars, text.Length.ToString());
        Assert.Equal(5000, AuditFiles.ParseCoverage(text).Count);
    }

    [Fact]
    public void Discard_RemovesTheReportAndItsSidecar()
    {
        string path = Save("x", DateTimeOffset.UtcNow);
        AuditFiles.MergeCoverage(_box.Files, path, ["a.cs"]);
        Assert.Equal(FileOutcome.Ok, AuditFiles.Discard(_box.Files, path).Outcome);
        Assert.False(AuditFiles.Exists(_box.Files, path));
        Assert.False(AuditFiles.Exists(_box.Files, AuditFiles.CoveragePath(path)));
    }

    [Fact]
    public void Save_RefusesAReportPastTheCap_BeforeWriting()
    {
        string path = AuditFiles.Choose(_box.Files, "big", DateTimeOffset.UtcNow);
        var report = new AuditReport { Summary = new string('x', WorkingDirectory.MaxWriteChars + 1) };
        Assert.Equal(AuditText.ReportFullResult, AuditFiles.Save(_box.Files, path, report, DateTimeOffset.UtcNow));
        Assert.False(AuditFiles.Exists(_box.Files, path));
    }
}
