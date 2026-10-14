using NeonSidekick.App;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

public class DraftFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));

    public DraftFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Create_MakesAnEmptyTextFile_NamedForTheApp_UnderTheFolder()
    {
        string path = DraftFile.Create(_dir);
        string again = DraftFile.Create(_dir);

        Assert.Equal(_dir, Path.GetDirectoryName(path));
        Assert.StartsWith("neon-draft-", Path.GetFileName(path));
        Assert.EndsWith(".txt", path);
        Assert.Equal("neon-draft-", DraftFile.FilePrefix);
        Assert.Equal(".txt", DraftFile.Extension);
        Assert.Equal("", File.ReadAllText(path));
        Assert.NotEqual(path, again);   // a fresh id each time
        Assert.Equal(Path.GetFileName(path), DraftFile.Name(path));
        Assert.Equal(11 + 32 + 4, Path.GetFileName(path).Length);   // the prefix, a 32-hex GUID, the extension
    }

    /// <summary>The overload with a text (2026-10-13, /prompts' editor round trip): the prompt in the file, its line breaks the platform's.</summary>
    [Fact]
    public void Create_WithAText_WritesItFirst_ItsLineBreaksThePlatforms()
    {
        string path = DraftFile.Create(_dir, "one\ntwo");

        Assert.StartsWith(DraftFile.FilePrefix, Path.GetFileName(path));
        Assert.Equal("one" + Environment.NewLine + "two", File.ReadAllText(path));
        Assert.False(DraftFile.IsBlank(File.ReadAllText(path)));
        Assert.True(DraftFile.TryDelete(path));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void CommandLine_IsTheCmdSCForm_WithTheOuterQuotesItStrips()
    {
        // /s makes cmd strip the first and last quote, so a command that opens with a quoted executable path keeps its own.
        Assert.Equal("/s /c \"code --wait \"C:\\t\\x.txt\"\"", DraftFile.CommandLine("code --wait", @"C:\t\x.txt"));
        Assert.Equal("/s /c \"\"C:\\Program Files\\Npp\\notepad++.exe\" -multiInst \"C:\\Users\\me\\AppData\\Local\\Temp\\neon-draft-1.txt\"\"",
            DraftFile.CommandLine("\"C:\\Program Files\\Npp\\notepad++.exe\" -multiInst", @"C:\Users\me\AppData\Local\Temp\neon-draft-1.txt"));
        Assert.Throws<ArgumentException>(() => DraftFile.CommandLine("", @"C:\t\x.txt"));
        Assert.Throws<ArgumentException>(() => DraftFile.CommandLine("code", ""));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("\n\n", true)]
    [InlineData("x", false)]
    [InlineData("  a line  ", false)]
    public void IsBlank_IsWhitespaceAlone(string text, bool blank) => Assert.Equal(blank, DraftFile.IsBlank(text));

    [Fact]
    public void TryDelete_RemovesTheFile_AndIsQuietWhenItIsGone()
    {
        string path = DraftFile.Create(_dir);

        Assert.True(DraftFile.TryDelete(path));
        Assert.False(File.Exists(path));
        Assert.True(DraftFile.TryDelete(path));   // File.Delete on a missing file is a no-op
        Assert.True(DraftFile.TryDelete(Path.Combine(_dir, "never-there.txt")));
    }

    [Fact]
    public void Events_AreOnePaste_ThenEnter()
    {
        var events = DraftFile.Events("one\ntwo");

        Assert.Equal(2, events.Count);
        Assert.Equal(new InputEvent.Paste("one\ntwo"), events[0]);
        var enter = Assert.IsType<InputEvent.Key>(events[1]);
        Assert.Equal(ConsoleKey.Enter, enter.Info.Key);
        Assert.Throws<ArgumentNullException>(() => DraftFile.Events(null!));
    }
}
