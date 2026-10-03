using NeonSidekick.Diagnostics;
using NeonSidekick.Llm;

namespace NeonSidekick.Tests;

/// <summary>The voice-directive file: the <see cref="PromptFile"/> mechanics over <c>vocalia.md</c>, with its own strings, default and cap.</summary>
public class VocaliaFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string FilePath => Path.Combine(_dir, VocaliaFile.FileName);

    private void WriteFile(string text)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, text);
    }

    [Fact]
    public void FilePath_IsVocaliaMdUnderTheDirectory()
    {
        var vocalia = new VocaliaFile(_dir);
        Assert.Equal(FilePath, vocalia.FilePath);
        Assert.Equal("vocalia.md", VocaliaFile.FileName);
        Assert.Equal(4000, VocaliaFile.MaxLength);
        Assert.Equal("Vocalia", VocaliaFile.Category);
        Assert.IsAssignableFrom<PromptFile>(vocalia);
    }

    [Fact]
    public void MissingFile_IsNull_AndNotActive()
    {
        var vocalia = new VocaliaFile(_dir);
        Assert.Null(vocalia.Read());
        Assert.False(vocalia.IsActive);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \r\n\t\n")]
    public void BlankFile_IsNull(string content)
    {
        WriteFile(content);
        var vocalia = new VocaliaFile(_dir);
        Assert.Null(vocalia.Read());
        Assert.False(vocalia.IsActive);
    }

    [Fact]
    public void Text_IsTrimmedAndNewlinesFolded_AndActive()
    {
        WriteFile("\r\n  Answer in haiku.\r\n\r\nNever use a tool.  \r\n");
        var vocalia = new VocaliaFile(_dir);
        Assert.Equal("Answer in haiku.\n\nNever use a tool.", vocalia.Read());
        Assert.True(vocalia.IsActive);
    }

    [Fact]
    public void Rewrite_IsPickedUpOnTheNextRead_AndDeletionFallsBackToNull()
    {
        WriteFile("Answer in haiku.");
        var vocalia = new VocaliaFile(_dir);
        Assert.Equal("Answer in haiku.", vocalia.Read());

        // A different length so the change is seen even within the file system's timestamp granularity.
        WriteFile("Answer in limericks, always.");
        Assert.Equal("Answer in limericks, always.", vocalia.Read());

        File.Delete(FilePath);
        Assert.Null(vocalia.Read());
        Assert.False(vocalia.IsActive);

        WriteFile("Answer in haiku.");
        Assert.Equal("Answer in haiku.", vocalia.Read());
    }

    [Fact]
    public void OverLength_IsCutWithAnEllipsis_WithOneWarning()
    {
        WriteFile(new string('a', VocaliaFile.MaxLength + 500));
        var warnings = CaptureWarnings(out var stop);
        try
        {
            var vocalia = new VocaliaFile(_dir);
            string? text = vocalia.Read();
            Assert.NotNull(text);
            Assert.Equal(VocaliaFile.MaxLength, text.Length);
            Assert.EndsWith("…", text, StringComparison.Ordinal);
            Assert.Equal(text, vocalia.Read());
        }
        finally
        {
            stop();
        }

        Assert.Equal(VocaliaFile.TruncatedWarning(VocaliaFile.MaxLength + 500), Assert.Single(warnings));
        Assert.Equal("vocalia.md is 4500 characters; using the first 4000.", warnings[0]);
    }

    [Fact]
    public void ADirectoryNamedVocaliaMd_CountsAsAbsent()
    {
        Directory.CreateDirectory(FilePath);
        var warnings = CaptureWarnings(out var stop);
        try
        {
            var vocalia = new VocaliaFile(_dir);
            Assert.Null(vocalia.Read());
            Assert.False(vocalia.IsActive);
        }
        finally
        {
            stop();
        }

        Assert.Empty(warnings);
    }

    [Fact]
    public void ALockedFile_WarnsOnce_IsNull_AndIsReadOnceReleased()
    {
        WriteFile("Answer in haiku.");
        var warnings = CaptureWarnings(out var stop);
        try
        {
            var vocalia = new VocaliaFile(_dir);
            using (new FileStream(FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.Null(vocalia.Read());
                Assert.Null(vocalia.Read());
                Assert.False(vocalia.IsActive);
            }

            Assert.Equal("Answer in haiku.", vocalia.Read());
            Assert.True(vocalia.IsActive);
        }
        finally
        {
            stop();
        }

        string warning = Assert.Single(warnings);
        Assert.StartsWith("Could not read vocalia.md; using no voice directive: ", warning, StringComparison.Ordinal);
        Assert.Equal("Could not read vocalia.md; using no voice directive: x", VocaliaFile.UnreadableWarning("x"));
    }

    [Fact]
    public void TheInfoLines_NameTheDirective()
    {
        var infos = new List<string>();
        Action<DiagnosticEvent> capture = e => { if (e.Category == VocaliaFile.Category && e.Level == DiagnosticLevel.Info) infos.Add(e.Message); };
        DiagnosticLog.Emitted += capture;
        try
        {
            var vocalia = new VocaliaFile(_dir);
            WriteFile("Answer in haiku.");
            vocalia.Read();
            WriteFile(" ");
            vocalia.Read();
        }
        finally
        {
            DiagnosticLog.Emitted -= capture;
        }

        Assert.Equal(["Voice directive loaded from vocalia.md (16 characters).", "vocalia.md is blank; using no voice directive."], infos);
    }

    [Fact]
    public void EnsureExists_CreatesAnEmptyFile_Once()
    {
        // No default voice directive since 2026-10-03 (the user's call): the file starts empty, and empty is no directive.
        var vocalia = new VocaliaFile(_dir);
        Assert.True(vocalia.EnsureExists());
        Assert.Equal("", File.ReadAllText(FilePath));
        Assert.Null(vocalia.Read());
        Assert.False(vocalia.IsActive);
        Assert.Equal("no voice directive", vocalia.DefaultInUse);

        File.WriteAllText(FilePath, "Answer in haiku.");
        Assert.False(vocalia.EnsureExists());
        Assert.Equal("Answer in haiku.", File.ReadAllText(FilePath));
    }

    [Fact]
    public void Normalize_IsPinned()
    {
        Assert.Equal("a\nb\n\nc", VocaliaFile.Normalize("  a\r\nb\r\r\nc \n"));
        Assert.Equal("", VocaliaFile.Normalize("\r\n \t"));

        string cut = VocaliaFile.Normalize(new string('x', VocaliaFile.MaxLength + 1), out bool truncated);
        Assert.True(truncated);
        Assert.Equal(VocaliaFile.MaxLength, cut.Length);
        Assert.EndsWith("…", cut, StringComparison.Ordinal);

        string exact = VocaliaFile.Normalize(new string('x', VocaliaFile.MaxLength), out truncated);
        Assert.False(truncated);
        Assert.Equal(VocaliaFile.MaxLength, exact.Length);
    }

    private static List<string> CaptureWarnings(out Action stop)
    {
        var warnings = new List<string>();
        Action<DiagnosticEvent> capture = e => { if (e.Category == VocaliaFile.Category && e.Level == DiagnosticLevel.Warning) warnings.Add(e.Message); };
        DiagnosticLog.Emitted += capture;
        stop = () => DiagnosticLog.Emitted -= capture;
        return warnings;
    }
}
