using NeonSidekick.App;

namespace NeonSidekick.Tests;

/// <summary>
/// Exit 4's guard (2026-11-21, feature idea #41): every headless write goes through <c>SidekickApp.HeadlessOutput.cs</c>, whose line writers note
/// an <c>[error]</c> line as the input line's failure. So no other <c>SidekickApp*.cs</c> file may reach a writer of its own, or a command added
/// later could print a failure that exits 0.
/// </summary>
public class HeadlessFailuresTests
{
    private static readonly string[] Writers = ["Console.Out", "Console.Error", "Console.Write", "_replyOut", "_infoOut", "_headlessErrorWriter", ".Writer.Write"];

    private static string AppFolder()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NeonSidekick.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir.FullName, "src", "NeonSidekick", "App");
    }

    [Fact]
    public void NoErrorLine_BypassesTheCounter()
    {
        var files = Directory.EnumerateFiles(AppFolder(), "SidekickApp*.cs").ToList();
        Assert.Contains(files, f => f.EndsWith("SidekickApp.HeadlessOutput.cs", StringComparison.Ordinal));
        foreach (string file in files.Where(f => !f.EndsWith("SidekickApp.HeadlessOutput.cs", StringComparison.Ordinal)))
        {
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                string code = lines[i].TrimStart();
                if (code.StartsWith("///", StringComparison.Ordinal) || code.StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (string writer in Writers)
                {
                    Assert.False(code.Contains(writer, StringComparison.Ordinal), $"{Path.GetFileName(file)}:{i + 1} writes past the failure check: {code}");
                }
            }
        }
    }

    [Theory]
    [InlineData("[error] gone", "gone")]
    [InlineData("Neon: [error] gone", "gone")]
    [InlineData("[notice] gone", null)]
    [InlineData("Neon: fine", null)]
    [InlineData("an [error] in a reply", null)]
    public void AnErrorLine_IsTheTagAtTheStart_AfterTheReplyPrefixOrNot(string line, string? why) => Assert.Equal(why, SidekickApp.HeadlessErrorOf(line));

    // ── 2026-11-22, the review's #132, #97, #1, #29 ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheSameFailure_OnSeveralLines_IsCounted_NeverDropped()
    {
        var failures = new HeadlessFailures();
        failures.NextLine();
        failures.Add(HeadlessText.AMessage, "Model error");
        failures.Add("/ha", "a second on the same line is not counted");
        failures.NextLine();
        failures.Add(HeadlessText.AMessage, "Model error");
        failures.NextLine();
        failures.Add("/ha", "no such entity");

        Assert.Equal([(HeadlessText.AMessage, "Model error", 2), ("/ha", "no such entity", 1)], failures.Entries);
        Assert.Equal("3 failed: a message ×2 (Model error), /ha (no such entity)", failures.SummaryLine());
    }

    [Fact]
    public void ALoopsStop_ReplacesWhatAPassNotedOnItsLine()
    {
        var failures = new HeadlessFailures();
        failures.NextLine();
        failures.Add(HeadlessText.AMessage, "a pass's blip");
        failures.Replace("/plan ralph", HeadlessText.Stopped("pass limit"));

        Assert.Equal([("/plan ralph", "stopped: pass limit", 1)], failures.Entries);
    }

    [Fact]
    public void ARunWideFailure_IsSaidOnce_AndSurvivesAPassRunAgain()
    {
        var failures = new HeadlessFailures();
        int mark = failures.Mark();
        failures.NextLine();
        failures.AddRunWide(HeadlessText.TheSession, HeadlessText.NotSaved("database is locked"));
        failures.AddRunWide(HeadlessText.TheSession, HeadlessText.NotSaved("database is locked"));
        failures.BackTo(mark);

        Assert.Equal([(HeadlessText.TheSession, "not saved: database is locked", 1)], failures.Entries);
        Assert.True(failures.Any);
    }

    [Theory]
    [InlineData("[notice] hi", true)]
    [InlineData("Neon: [error] gone", true)]
    [InlineData("[warning] not back", true)]
    [InlineData("[tool] x -> y", true)]
    [InlineData("[the docs](https://example.com) say so", false)]
    [InlineData("Neon: [Docs](x) first", false)]
    [InlineData("Claude: [notice] its own words", false)]
    public void OnlyTheAppsTags_AreTheAppsLines(string line, bool tagged) => Assert.Equal(tagged, SidekickApp.HeadlessTagged(line));
}
