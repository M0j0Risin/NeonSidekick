using NeonSidekick.Diagnostics;
using NeonSidekick.Files;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// The wording after the 2026-10-22 fixes (the fix plan's Phase 11): a name never disguises itself or reaches the terminal (NameScrub, in
/// every file sentence and the log), a destination's refusal names the destination, a slicing bug is no file failure, and the sentences
/// name only what is offered.
/// </summary>
public sealed class WordingFixesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", "wording-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly WorkingDirectory _files;

    public WordingFixesTests()
    {
        _root = Path.Combine(_dir, "work");
        Directory.CreateDirectory(_root);
        _files = new WorkingDirectory(() => _root, new ManualTimeProvider());
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void AName_ShowsItsHiddenCharacters_AsEscapes()
    {
        Assert.Equal("invoice\\u202Efdp.exe", NameScrub.Display("invoice\u202Efdp.exe"));
        Assert.Equal("a\\u001B]52;c;x\\u0007b", NameScrub.Display("a\u001B]52;c;x\u0007b"));
        Assert.Equal("z\\u200Bw", NameScrub.Display("z\u200Bw"));
        Assert.Equal("line\\u000Abreak", NameScrub.Display("line\nbreak"));
        Assert.Equal("line\nbreak", NameScrub.Display("line\nbreak", keepLineBreaks: true));
        Assert.Equal("plain é name", NameScrub.Display("plain é name"));
        string same = "x.txt";
        Assert.Same(same, NameScrub.Display(same));
    }

    [Fact]
    public void FileSentences_AndTheLog_ShowNamesScrubbed()
    {
        Assert.Contains("\\u202E", FileText.Error(FileOutcome.Missing, "a\u202Eb.txt", "read"), StringComparison.Ordinal);
        Assert.DoesNotContain("\u202E", FileText.Error(FileOutcome.Missing, "a\u202Eb.txt", "read"), StringComparison.Ordinal);

        string? seen = null;
        Action<DiagnosticEvent> capture = e => { if (e.Category == "wording-test") seen = e.Message; };
        DiagnosticLog.Emitted += capture;
        try
        {
            DiagnosticLog.Info("wording-test", "deleted \u001B[2Jx\nnext");
        }
        finally
        {
            DiagnosticLog.Emitted -= capture;
        }

        Assert.Equal("deleted \\u001B[2Jx\nnext", seen);
    }

    [Fact]
    public void ADestinationsRefusal_NamesTheDestination()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "x");
        string copied = FileText.Copied(_files.Copy("a.txt", "../outside.txt", overwrite: false));
        Assert.Contains("outside.txt", copied, StringComparison.Ordinal);
        Assert.DoesNotContain("'a.txt'", copied, StringComparison.Ordinal);
    }

    [Fact]
    public void ASlicingBug_IsNoFileFailure()
    {
        Assert.False(WorkingDirectory.IsFileFailure(new ArgumentOutOfRangeException("x")));
        Assert.False(WorkingDirectory.IsFileFailure(new ArgumentNullException("x")));
        Assert.True(WorkingDirectory.IsFileFailure(new ArgumentException("bad path")));
        Assert.True(WorkingDirectory.IsFileFailure(new IOException("busy")));
    }

    [Fact]
    public void TheSentences_NameOnlyWhatIsOffered()
    {
        Assert.Contains("while Image tools is on", FileText.ViewImageHint, StringComparison.Ordinal);
        Assert.Contains("read-write mode", FileText.DatabaseProtected("x.db"), StringComparison.Ordinal);
        Assert.Equal(FileText.ImageTooLarge("big.png"), ImageFile.TooLarge("big.png"));
    }
}
