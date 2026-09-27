using NeonSidekick.Skills;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

public class SkillInstallTextTests
{
    private static SkillCandidate Candidate(IEnumerable<(string, string)> files, string root = "repo-main/")
    {
        using var archive = SkillArchive.TryOpen(SkillZip.Build(files, root: root), out _)!;
        return archive.Candidates().Single();
    }

    [Fact]
    public void SanitizeForTerminal_StripsControls_KeepsTabsAndNewlines()
    {
        // The ESC goes, so what is left of a sequence is text the terminal prints and never obeys.
        Assert.Equal("[31mred[0m\ttext\nnext", SkillInstallText.SanitizeForTerminal("\u001b[31mred\u001b[0m\ttext\r\nnext\u0007"));
        Assert.Equal("abc", SkillInstallText.SanitizeForTerminal("a‮b⁦c\u009b"));
    }

    [Fact]
    public void Preview_NamesTheSourceCommitFilesAndScripts_AndFencesTheBody()
    {
        var candidate = Candidate(
        [
            ("skills/pdf/SKILL.md", SkillZip.SkillMd("pdf", "Reads \u001b[2JPDFs.", "Use ``` fences\nline 2", "allowed-tools: Bash(python:*)\nlicense: MIT\n")),
            ("skills/pdf/scripts/fill.py", "x"),
        ]);
        SkillSource.TryParse("https://github.com/anthropics/skills/tree/main", out var source, out _);

        string preview = SkillInstallText.PreviewMarkdown(candidate, source!, SkillZip.Commit);

        Assert.True(preview.StartsWith("**pdf** — Reads [2JPDFs.\n\n- Source: anthropics/skills@main @ 3337550\n- Folder: skills/pdf\n- allowed-tools: Bash(python:*)\n- license: MIT\n- Files: 2, ", StringComparison.Ordinal), preview);
        Assert.Contains("  - scripts/fill.py (1 B)\n", preview);
        Assert.Contains("- Contains scripts: scripts/fill.py — they run only through run_command's approval\n", preview);
        Assert.EndsWith("\n````markdown\nUse ``` fences\nline 2\n````", preview, StringComparison.Ordinal);
        Assert.DoesNotContain('\u001b', preview);
    }

    [Fact]
    public void Preview_CutsALongFileListAndBody()
    {
        var files = Enumerable.Range(0, SkillInstallText.PreviewFiles + 3).Select(i => ($"s/f{i:D2}.md", "x"))
            .Append(("s/SKILL.md", SkillZip.SkillMd("s", body: string.Join('\n', Enumerable.Range(0, 60).Select(i => "line " + i)))));

        string preview = SkillInstallText.PreviewMarkdown(Candidate(files), new SkillSource(SkillSourceKind.GitHub, Owner: "o", Repo: "r"), null);

        Assert.Contains("  - …and 4 more\n", preview);
        Assert.Contains("line 39\n```", preview);
        Assert.DoesNotContain("line 40", preview);
        Assert.EndsWith("```\n…", preview, StringComparison.Ordinal);
        Assert.Contains("- Source: o/r\n", preview);
    }

    [Fact]
    public void Rows_AndLines_ReadAsPinned()
    {
        // In columns (2026-09-26, the user's ask): each padded to its longest, the counts right-aligned.
        SkillsShSkill[] hits =
        [
            new() { Id = "obra/superpowers/test-driven-development", Source = "obra/superpowers", Name = "test-driven-development", Installs = 237612 },
            new() { Id = "mattpocock/skills/tdd", Source = "mattpocock/skills", Name = "tdd", Installs = 970348 },
            new() { Id = "o/r/one", Source = "o/r", Name = "one", Installs = 1 },
        ];
        Assert.Equal(
        [
            "test-driven-development  obra/superpowers   237,612 installs",
            "tdd                      mattpocock/skills  970,348 installs",
            "one                      o/r                      1 install",
        ], SkillInstallText.HitRows(hits));
        Assert.Equal(
        [
            "  obra/superpowers/test-driven-development  237,612 installs",
            "  mattpocock/skills/tdd                     970,348 installs",
            "  o/r/one                                         1 install",
        ], SkillInstallText.HeadlessHitLines(hits));
        Assert.Empty(SkillInstallText.HitRows([]));

        using var archive = SkillArchive.TryOpen(SkillZip.Build([("skills/pdf/SKILL.md", SkillZip.SkillMd("pdf")), ("x/SKILL.md", SkillZip.SkillMd("Bad_Name"))]), out _)!;
        var candidates = archive.Candidates();
        string refusal = SkillInstallText.BadNameRefusal("Bad_Name");
        Assert.Equal(["pdf       skills/pdf", "Bad_Name  x           ✗ " + refusal], SkillInstallText.CandidateRows(candidates));
        Assert.Equal(["  o/r/pdf", "  o/r/Bad_Name  (" + refusal + ")"], SkillInstallText.HeadlessCandidateLines(new SkillSource(SkillSourceKind.GitHub, Owner: "o", Repo: "r"), candidates));
        Assert.Equal("pdf is not a skill there (found: a, b)", SkillInstallText.NoSuchSkillError("pdf", ["a", "b"]));
        Assert.Equal("pdf is not a skill there", SkillInstallText.NoSuchSkillError("pdf", []));
    }
}
