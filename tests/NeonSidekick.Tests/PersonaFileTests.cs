using NeonSidekick.Diagnostics;
using NeonSidekick.Llm;

namespace NeonSidekick.Tests;

public class PersonaFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string FilePath => Path.Combine(_dir, PersonaFile.FileName);

    private void WriteFile(string text)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, text);
    }

    [Fact]
    public void TerminalArguments_ANewWindowInTheFolder_QuotedByTheArgvRules_TheSemicolonsEscaped()
    {
        // /terminal (2026-10-03): wt.exe's own command line, pinned.
        Assert.Equal(@"-w new -d ""D:\Repo\Neon""", PersonaFile.TerminalArguments(@"D:\Repo\Neon"));
        Assert.Equal(@"-w new -d ""C:\My Files\docs""", PersonaFile.TerminalArguments(@"C:\My Files\docs"));
        Assert.Equal(@"-w new -d ""D:\\""", PersonaFile.TerminalArguments(@"D:\"));   // the root's backslash doubled before the quote
        Assert.Equal(@"-w new -d ""D:\a\\b\\""", PersonaFile.TerminalArguments(@"D:\a\\b\"));
        Assert.Equal(@"-w new -d ""D:\one\;two""", PersonaFile.TerminalArguments(@"D:\one;two"));   // wt splits its commands at a bare ;
        Assert.Throws<ArgumentException>(() => PersonaFile.TerminalArguments(" "));
    }

    [Fact]
    public void FilePath_IsPersonaMdUnderTheDirectory()
    {
        var persona = new PersonaFile(_dir);
        Assert.Equal(FilePath, persona.FilePath);
        Assert.Equal("persona.md", PersonaFile.FileName);
        Assert.Equal(4000, PersonaFile.MaxLength);
    }

    [Fact]
    public void MissingFile_IsNull_AndNotActive()
    {
        var persona = new PersonaFile(_dir);
        Assert.Null(persona.Read());
        Assert.False(persona.IsActive);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \r\n\t\n")]
    public void BlankFile_IsNull(string content)
    {
        WriteFile(content);
        var persona = new PersonaFile(_dir);
        Assert.Null(persona.Read());
        Assert.False(persona.IsActive);
    }

    [Fact]
    public void Text_IsTrimmedAndNewlinesFolded_AndActive()
    {
        WriteFile("\r\n  You are Rex, a gruff pirate.\r\n\r\nYou answer in one sentence.  \r\n");
        var persona = new PersonaFile(_dir);
        Assert.Equal("You are Rex, a gruff pirate.\n\nYou answer in one sentence.", persona.Read());
        Assert.True(persona.IsActive);
    }

    [Fact]
    public void Utf8Bom_IsNotPartOfTheText()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "You are Rex.", new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Assert.Equal("You are Rex.", new PersonaFile(_dir).Read());
    }

    [Fact]
    public void CopyTo_CreatesTheDirectory_AndReplacesWhatIsThere()
    {
        // 2026-09-21: /persona copy <profile> [force] — byte for byte, the directory made, a file there replaced (the force rule is the caller's).
        WriteFile("You are Rex.");
        var persona = new PersonaFile(_dir);
        string other = Path.Combine(_dir, "other");
        Assert.False(Directory.Exists(other));

        persona.CopyTo(other);
        string copy = Path.Combine(other, PersonaFile.FileName);
        Assert.Equal("You are Rex.", File.ReadAllText(copy));
        Assert.Equal("You are Rex.", persona.Read());   // the source stays

        WriteFile("You are Morgan, a calm librarian.");
        persona.CopyTo(other);
        Assert.Equal("You are Morgan, a calm librarian.", File.ReadAllText(copy));

        File.Delete(FilePath);
        Assert.Throws<FileNotFoundException>(() => persona.CopyTo(other));   // an IOException: the command reports it
        Assert.Throws<ArgumentException>(() => persona.CopyTo(""));
    }

    [Fact]
    public void Rewrite_IsPickedUpOnTheNextRead_AndDeletionFallsBackToNull()
    {
        WriteFile("You are Rex.");
        var persona = new PersonaFile(_dir);
        Assert.Equal("You are Rex.", persona.Read());

        // A different length so the change is seen even within the file system's timestamp granularity.
        WriteFile("You are Morgan, a calm librarian.");
        Assert.Equal("You are Morgan, a calm librarian.", persona.Read());

        File.Delete(FilePath);
        Assert.Null(persona.Read());
        Assert.False(persona.IsActive);

        WriteFile("You are Rex.");
        Assert.Equal("You are Rex.", persona.Read());
    }

    [Fact]
    public void UnchangedFile_IsNotReReadButStillReturnsTheText()
    {
        WriteFile("You are Rex.");
        var persona = new PersonaFile(_dir);
        Assert.Equal("You are Rex.", persona.Read());
        Assert.Equal("You are Rex.", persona.Read());
        Assert.True(persona.IsActive);
    }

    [Fact]
    public void OverLength_IsCutWithAnEllipsis_WithOneWarning()
    {
        WriteFile(new string('a', PersonaFile.MaxLength + 500));
        var warnings = CaptureWarnings(out var stop);
        try
        {
            var persona = new PersonaFile(_dir);
            string? text = persona.Read();
            Assert.NotNull(text);
            Assert.Equal(PersonaFile.MaxLength, text.Length);
            Assert.EndsWith("…", text, StringComparison.Ordinal);
            Assert.Equal(text, persona.Read());
        }
        finally
        {
            stop();
        }

        Assert.Equal(PersonaFile.TruncatedWarning(PersonaFile.MaxLength + 500), Assert.Single(warnings));
        Assert.Contains("persona.md is 4500 characters; using the first 4000.", warnings[0]);
    }

    [Fact]
    public void ADirectoryNamedPersonaMd_CountsAsAbsent()
    {
        Directory.CreateDirectory(FilePath);
        var warnings = CaptureWarnings(out var stop);
        try
        {
            var persona = new PersonaFile(_dir);
            Assert.Null(persona.Read());
            Assert.False(persona.IsActive);
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
        WriteFile("You are Rex.");
        var warnings = CaptureWarnings(out var stop);
        try
        {
            var persona = new PersonaFile(_dir);
            using (new FileStream(FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.Null(persona.Read());
                Assert.Null(persona.Read());
                Assert.False(persona.IsActive);
            }

            Assert.Equal("You are Rex.", persona.Read());
            Assert.True(persona.IsActive);
        }
        finally
        {
            stop();
        }

        string warning = Assert.Single(warnings);
        Assert.StartsWith("Could not read persona.md; using the default persona: ", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureExists_CreatesTheFileWithTheDefaultPersona_Once()
    {
        var persona = new PersonaFile(_dir);
        Assert.True(persona.EnsureExists());
        Assert.Equal(Assistant.DefaultPersona.ReplaceLineEndings() + Environment.NewLine, File.ReadAllText(FilePath));   // the platform's breaks throughout (2026-10-03)
        // The seeded file is the default persona, so the prompt now carries it as a custom persona of the same words.
        Assert.Equal(Assistant.DefaultPersona, persona.Read());

        File.WriteAllText(FilePath, "You are Rex.");
        Assert.False(persona.EnsureExists());
        Assert.Equal("You are Rex.", File.ReadAllText(FilePath));
    }

    [Fact]
    public void Normalize_IsPinned()
    {
        Assert.Equal("a\nb\n\nc", PersonaFile.Normalize("  a\r\nb\r\r\nc \n"));
        Assert.Equal("", PersonaFile.Normalize("\r\n \t"));

        string cut = PersonaFile.Normalize(new string('x', PersonaFile.MaxLength + 1), out bool truncated);
        Assert.True(truncated);
        Assert.Equal(PersonaFile.MaxLength, cut.Length);
        Assert.EndsWith("…", cut, StringComparison.Ordinal);

        string exact = PersonaFile.Normalize(new string('x', PersonaFile.MaxLength), out truncated);
        Assert.False(truncated);
        Assert.Equal(PersonaFile.MaxLength, exact.Length);
    }

    [Fact]
    public async Task EditAndWaitAsync_RunsAConfiguredCommandThroughCmd_AndWaitsForIt()
    {
        // /draft's configured-editor leg (2026-09-19) without an editor: a command that writes the
        // file and exits — the wait ends after it did, so the text is there when the screen reads it.
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, "draft.txt");
        File.WriteAllText(path, "");

        await PersonaFile.EditAndWaitAsync(path, "cmd /c echo drafted>", CancellationToken.None);

        Assert.Equal("drafted\r\n", File.ReadAllText(path));
    }

    [Fact]
    public async Task EditAndWaitAsync_TheToken_EndsTheWait_AndLeavesTheProcess()
    {
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, "draft.txt");
        File.WriteAllText(path, "");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        // ping as a sleep (a couple of seconds); the token fires first and the wait throws, the process runs on.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PersonaFile.EditAndWaitAsync(path, "cmd /c ping -n 3 127.0.0.1 >nul & rem", cts.Token));
    }

    [Fact]
    public async Task EditAndWaitAsync_ChecksItsArguments()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => PersonaFile.EditAndWaitAsync(" ", "", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => PersonaFile.EditAndWaitAsync(@"C:\x.txt", null!, CancellationToken.None));
    }

    private static List<string> CaptureWarnings(out Action stop)
    {
        var warnings = new List<string>();
        Action<DiagnosticEvent> capture = e => { if (e.Category == PersonaFile.Category && e.Level == DiagnosticLevel.Warning) warnings.Add(e.Message); };
        DiagnosticLog.Emitted += capture;
        stop = () => DiagnosticLog.Emitted -= capture;
        return warnings;
    }
}
