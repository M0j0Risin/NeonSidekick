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
}
