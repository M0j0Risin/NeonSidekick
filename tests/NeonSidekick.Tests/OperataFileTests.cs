using NeonSidekick.Diagnostics;
using NeonSidekick.Llm;

namespace NeonSidekick.Tests;

/// <summary>The operating-rules file: the <see cref="PromptFile"/> mechanics over <c>operata.md</c>, with its own strings, default and cap.</summary>
public class OperataFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string FilePath => Path.Combine(_dir, OperataFile.FileName);

    private void WriteFile(string text)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, text);
    }

    [Fact]
    public void FilePath_IsOperataMdUnderTheDirectory()
    {
        var operata = new OperataFile(_dir);
        Assert.Equal(FilePath, operata.FilePath);
        Assert.Equal("operata.md", OperataFile.FileName);
        Assert.Equal(8000, OperataFile.MaxLength);
        Assert.Equal("Operata", OperataFile.Category);
        Assert.IsAssignableFrom<PromptFile>(operata);
    }

    [Fact]
    public void MissingFile_IsNull_AndNotActive()
    {
        var operata = new OperataFile(_dir);
        Assert.Null(operata.Read());
        Assert.False(operata.IsActive);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \r\n\t\n")]
    public void BlankFile_IsNull(string content)
    {
        WriteFile(content);
        var operata = new OperataFile(_dir);
        Assert.Null(operata.Read());
        Assert.False(operata.IsActive);
    }

    [Fact]
    public void Text_IsTrimmedAndNewlinesFolded_AndActive()
    {
        WriteFile("\r\n  Answer in haiku.\r\n\r\nNever use a tool.  \r\n");
        var operata = new OperataFile(_dir);
        Assert.Equal("Answer in haiku.\n\nNever use a tool.", operata.Read());
        Assert.True(operata.IsActive);
    }

    [Fact]
    public void Rewrite_IsPickedUpOnTheNextRead_AndDeletionFallsBackToNull()
    {
        WriteFile("Answer in haiku.");
        var operata = new OperataFile(_dir);
        Assert.Equal("Answer in haiku.", operata.Read());

        // A different length so the change is seen even within the file system's timestamp granularity.
        WriteFile("Answer in limericks, always.");
        Assert.Equal("Answer in limericks, always.", operata.Read());

        File.Delete(FilePath);
        Assert.Null(operata.Read());
        Assert.False(operata.IsActive);

        WriteFile("Answer in haiku.");
        Assert.Equal("Answer in haiku.", operata.Read());
    }

    [Fact]
    public void OverLength_IsCutWithAnEllipsis_WithOneWarning()
    {
        WriteFile(new string('a', OperataFile.MaxLength + 500));
        var warnings = CaptureWarnings(out var stop);
        try
        {
            var operata = new OperataFile(_dir);
            string? text = operata.Read();
            Assert.NotNull(text);
            Assert.Equal(OperataFile.MaxLength, text.Length);
            Assert.EndsWith("…", text, StringComparison.Ordinal);
            Assert.Equal(text, operata.Read());
        }
        finally
        {
            stop();
        }

        Assert.Equal(OperataFile.TruncatedWarning(OperataFile.MaxLength + 500), Assert.Single(warnings));
        Assert.Equal("operata.md is 8500 characters; using the first 8000.", warnings[0]);
    }

    [Fact]
    public void ADirectoryNamedOperataMd_CountsAsAbsent()
    {
        Directory.CreateDirectory(FilePath);
        var warnings = CaptureWarnings(out var stop);
        try
        {
            var operata = new OperataFile(_dir);
            Assert.Null(operata.Read());
            Assert.False(operata.IsActive);
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
            var operata = new OperataFile(_dir);
            using (new FileStream(FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.Null(operata.Read());
                Assert.Null(operata.Read());
                Assert.False(operata.IsActive);
            }

            Assert.Equal("Answer in haiku.", operata.Read());
            Assert.True(operata.IsActive);
        }
        finally
        {
            stop();
        }

        string warning = Assert.Single(warnings);
        Assert.StartsWith("Could not read operata.md; using the default operating rules: ", warning, StringComparison.Ordinal);
        Assert.Equal("Could not read operata.md; using the default operating rules: x", OperataFile.UnreadableWarning("x"));
    }

    [Fact]
    public void TheInfoLines_NameTheRules()
    {
        var infos = new List<string>();
        Action<DiagnosticEvent> capture = e => { if (e.Category == OperataFile.Category && e.Level == DiagnosticLevel.Info) infos.Add(e.Message); };
        DiagnosticLog.Emitted += capture;
        try
        {
            var operata = new OperataFile(_dir);
            WriteFile("Answer in haiku.");
            operata.Read();
            WriteFile(" ");
            operata.Read();
        }
        finally
        {
            DiagnosticLog.Emitted -= capture;
        }

        Assert.Equal(["Operating rules loaded from operata.md (16 characters).", "operata.md is blank; using the default operating rules."], infos);
    }

    [Fact]
    public void EnsureExists_CreatesTheFileWithTheDefaultRules_Once()
    {
        var operata = new OperataFile(_dir);
        Assert.True(operata.EnsureExists());
        Assert.Equal(Assistant.OperatingRules + Environment.NewLine, File.ReadAllText(FilePath));
        // The seeded file is the default rules, so the prompt now carries them as custom rules of the same words.
        Assert.Equal(Assistant.OperatingRules, operata.Read());

        File.WriteAllText(FilePath, "Answer in haiku.");
        Assert.False(operata.EnsureExists());
        Assert.Equal("Answer in haiku.", File.ReadAllText(FilePath));
    }

    [Fact]
    public void EnsureExists_WithASeed_WritesTheSeed_AndLeavesAnExistingFileAlone()
    {
        // /operata's seed (2026-10-04): the rules in use now, in the default's place; LF lines get the platform's breaks.
        var operata = new OperataFile(_dir);
        Assert.True(operata.EnsureExists("Reply briefly.\nUse a tool when it helps."));
        Assert.Equal("Reply briefly." + Environment.NewLine + "Use a tool when it helps." + Environment.NewLine, File.ReadAllText(FilePath));

        Assert.False(operata.EnsureExists("Something else."));
        Assert.Equal("Reply briefly.\nUse a tool when it helps.", operata.Read());
    }

    [Fact]
    public void Normalize_IsPinned()
    {
        Assert.Equal("a\nb\n\nc", OperataFile.Normalize("  a\r\nb\r\r\nc \n"));
        Assert.Equal("", OperataFile.Normalize("\r\n \t"));

        string cut = OperataFile.Normalize(new string('x', OperataFile.MaxLength + 1), out bool truncated);
        Assert.True(truncated);
        Assert.Equal(OperataFile.MaxLength, cut.Length);
        Assert.EndsWith("…", cut, StringComparison.Ordinal);

        string exact = OperataFile.Normalize(new string('x', OperataFile.MaxLength), out truncated);
        Assert.False(truncated);
        Assert.Equal(OperataFile.MaxLength, exact.Length);
    }

    private static List<string> CaptureWarnings(out Action stop)
    {
        var warnings = new List<string>();
        Action<DiagnosticEvent> capture = e => { if (e.Category == OperataFile.Category && e.Level == DiagnosticLevel.Warning) warnings.Add(e.Message); };
        DiagnosticLog.Emitted += capture;
        stop = () => DiagnosticLog.Emitted -= capture;
        return warnings;
    }
}
