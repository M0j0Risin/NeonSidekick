using System.Text;
using NeonSidekick.Files;
using NeonSidekick.Obsidian;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// Text as the file holds it (2026-10-22, the fix plan's Phase 3: mechanisms 2, 3 and 7): the encoding found and kept, cuts that never split a
/// surrogate pair, the line breaks <c>patch_file</c> knows, the seen stamps, the append and the replace that keep the file's own form.
/// </summary>
public sealed class TextFileTests : IDisposable
{
    private static readonly Encoding Cp1252 = CodePagesEncoding(1252);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly WorkingDirectory _files;

    public TextFileTests()
    {
        Directory.CreateDirectory(_root);
        _files = new WorkingDirectory(() => _root, new ManualTimeProvider());
    }

    public void Dispose()
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            /* best effort */
        }
    }

    private static Encoding CodePagesEncoding(int codePage)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(codePage);
    }

    private string Full(string relative) => Path.Combine(_root, relative);

    // ── the codec ────────────────────────────────────────────────────────────

    [Fact]
    public void EveryByte_RoundTrips_ThroughTheLegacyCodePage()
    {
        byte[] all = [.. Enumerable.Range(0, 256).Where(b => b != 0).Select(b => (byte)b)];
        string text = TextFile.Decode(all, out var codec);
        Assert.Equal(TextForm.Legacy, codec.Form);
        Assert.True(codec.Guessed);
        Assert.Equal(all, codec.Encode(text));
    }

    [Theory]
    [InlineData(new byte[] { 0xEF, 0xBB, 0xBF, (byte)'h', (byte)'i' }, TextForm.Utf8Bom, "hi")]
    [InlineData(new byte[] { 0xFF, 0xFE, (byte)'h', 0, (byte)'i', 0 }, TextForm.Utf16Le, "hi")]
    [InlineData(new byte[] { 0xFE, 0xFF, 0, (byte)'h', 0, (byte)'i' }, TextForm.Utf16Be, "hi")]
    [InlineData(new byte[] { 0xFF, 0xFE, 0, 0, (byte)'h', 0, 0, 0 }, TextForm.Utf32Le, "h")]
    [InlineData(new byte[] { (byte)'h', (byte)'i' }, TextForm.Utf8, "hi")]
    public void AMark_SaysTheEncoding(byte[] bytes, TextForm form, string text)
    {
        Assert.False(TextFile.LooksBinary(bytes));
        Assert.Equal(text, TextFile.Decode(bytes, out var codec));
        Assert.Equal(form, codec.Form);
        Assert.Equal(bytes, codec.Encode(text));
    }

    /// <summary>UTF-16 without a mark (PowerShell 5.1's <c>&gt;</c> writes one; other tools don't) is text by its NULs (review Q4).</summary>
    [Fact]
    public void Utf16WithoutAMark_IsText()
    {
        byte[] bytes = new UnicodeEncoding(false, false).GetBytes("line one of a log\r\nline two of a log\r\n");
        Assert.False(TextFile.LooksBinary(bytes));
        Assert.Equal(TextForm.Utf16Le, TextFile.Detect(bytes).Form);
        Assert.True(TextFile.LooksBinary(new byte[] { 1, 0, 2, 0xFF, 0, 0x88, 9, 0, 0, 0, 7, 3 }));
    }

    [Fact]
    public void ALegacyCodePage_RefusesWhatItCannotHold()
    {
        var codec = TextFile.Detect(Cp1252.GetBytes("café"));
        Assert.Null(codec.Encode("smile \U0001F600"));
        Assert.NotNull(codec.Encode("café crème"));
    }

    // ── the cuts ─────────────────────────────────────────────────────────────

    [Fact]
    public void ACut_NeverSplitsAPair()
    {
        string text = "ab\U0001F600cd";
        Assert.Equal("ab", TextCut.Safe(text, 3));
        Assert.Equal("ab\U0001F600", TextCut.Safe(text, 4));
        Assert.Equal(4, TextCut.SafeStart(text, 3));
        Assert.Equal(text, TextCut.Safe(text, 100));
    }

    // ── read_file ────────────────────────────────────────────────────────────

    /// <summary>A Windows-1252 file reads as its own characters, and the header says it (review Q1).</summary>
    [Fact]
    public void ACodePageFile_ReadsAsItIs_AndSaysSo()
    {
        File.WriteAllBytes(Full("menu.txt"), Cp1252.GetBytes("café crème\n"));
        var read = _files.ReadText("menu.txt", null, null);
        Assert.Equal("café crème", read.Text);
        Assert.Contains("read as " + TextFile.LegacyName + " (a guess)", FileText.ReadHeader(read), StringComparison.Ordinal);
    }

    [Fact]
    public void AUtf16File_ReadsAndIsSearched()
    {
        File.WriteAllBytes(Full("ps.log"), [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("Get-Item failed\r\nall done\r\n")]);
        Assert.Equal("Get-Item failed\nall done", _files.ReadText("ps.log", null, null).Text);
        Assert.Single(_files.Search("failed", "", null, false, CancellationToken.None).Hits);
    }

    /// <summary>The window never wraps (review G20) and a cut line is said with its length (Q2), never half an emoji.</summary>
    [Fact]
    public void AHugeWindow_AndALongLine_ReadRight()
    {
        File.WriteAllText(Full("a.txt"), "one\ntwo\n");
        Assert.Equal("one\ntwo", _files.ReadText("a.txt", 1, int.MaxValue).Text);

        string line = new string('x', WorkingDirectory.MaxReadChars - 1) + "\U0001F600tail";
        File.WriteAllText(Full("long.txt"), line);
        var read = _files.ReadText("long.txt", null, null);
        Assert.Equal(WorkingDirectory.MaxReadChars - 1, read.Text.Length);
        Assert.Equal(1, read.CutLine);
        Assert.Contains("line 1 is " + line.Length.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " characters long, cut at", FileText.ReadHeader(read), StringComparison.Ordinal);
        Assert.False(read.Whole);
    }

    /// <summary>A form feed and U+2028 are no line breaks (review N9): a read shows what patch_file matches.</summary>
    [Fact]
    public void OnlyCrAndLf_BreakLines()
    {
        Assert.Equal(["a\fb\u2028c", "d", "e", "f"], WorkingDirectory.SplitLines("a\fb\u2028c\r\nd\re\nf\n"));
        Assert.Equal([""], WorkingDirectory.SplitLines("\n"));
        Assert.Empty(WorkingDirectory.SplitLines(""));
    }

    // ── writes ───────────────────────────────────────────────────────────────

    /// <summary>A patch keeps a code page's bytes (review G4), and refuses a character it can't hold.</summary>
    [Fact]
    public void APatch_KeepsTheFilesEncoding()
    {
        File.WriteAllBytes(Full("menu.txt"), Cp1252.GetBytes("café au lait\r\n"));
        Assert.Equal(FileOutcome.Ok, _files.EditText("menu.txt", "au lait", "crème").Outcome);
        Assert.Equal(Cp1252.GetBytes("café crème\r\n"), File.ReadAllBytes(Full("menu.txt")));
        var refused = _files.EditText("menu.txt", "crème", "\U0001F600");
        Assert.Equal(FileOutcome.NotEncodable, refused.Outcome);
        Assert.Equal(Cp1252.GetBytes("café crème\r\n"), File.ReadAllBytes(Full("menu.txt")));
    }

    /// <summary>An overwrite keeps the mark and the CRLF (review Q3).</summary>
    [Fact]
    public void AnOverwrite_KeepsTheMarkAndTheLineEnding()
    {
        File.WriteAllBytes(Full("run.ps1"), [0xEF, 0xBB, 0xBF, .. "Write-Host 'a'\r\n"u8.ToArray()]);
        Assert.Equal(FileOutcome.Ok, _files.WriteText("run.ps1", "Write-Host 'b'\nWrite-Host 'c'\n", overwrite: true).Outcome);
        Assert.Equal([0xEF, 0xBB, 0xBF, .. "Write-Host 'b'\r\nWrite-Host 'c'\r\n"u8.ToArray()], File.ReadAllBytes(Full("run.ps1")));
    }

    /// <summary>An append keeps the file's encoding and line ending, refuses a binary file, and stops at the cap (review G12, P10).</summary>
    [Fact]
    public void AnAppend_KeepsTheFilesForm()
    {
        File.WriteAllBytes(Full("ps.log"), [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("first\r\nsecond")]);
        var appended = _files.AppendText("ps.log", "third");
        Assert.Equal(FileOutcome.Ok, appended.Outcome);
        Assert.Equal([0xFF, 0xFE, .. Encoding.Unicode.GetBytes("first\r\nsecond\r\nthird")], File.ReadAllBytes(Full("ps.log")));
        Assert.Equal(3, appended.Lines);

        File.WriteAllBytes(Full("x.bin"), [1, 0, 2, 0xFF, 3, 0, 0, 0, 9, 9, 0, 1]);
        Assert.Equal(FileOutcome.NotText, _files.AppendText("x.bin", "more").Outcome);

        File.WriteAllText(Full("big.log"), new string('x', (int)WorkingDirectory.MaxReadFileBytes - 10));
        Assert.Equal(FileOutcome.TooBig, _files.AppendText("big.log", new string('y', 100)).Outcome);
        Assert.Equal(WorkingDirectory.MaxReadFileBytes - 10, new FileInfo(Full("big.log")).Length);
        Assert.Empty(Directory.GetFiles(_root, "*" + WorkingDirectory.TempSuffix));
    }

    /// <summary>A read-only file is never written over (review O2), on either system.</summary>
    [Fact]
    public void AReadOnlyFile_IsRefused()
    {
        File.WriteAllText(Full("locked.txt"), "keep");
        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(Full("locked.txt"), FileAttributes.ReadOnly);
        }
        else
        {
            File.SetUnixFileMode(Full("locked.txt"), UnixFileMode.UserRead);
        }

        var written = _files.WriteText("locked.txt", "lost", overwrite: true);
        Assert.Equal(FileOutcome.Failed, written.Outcome);
        Assert.Equal(FileText.ReadOnlyDetail, written.Detail);
        Assert.Equal("keep", File.ReadAllText(Full("locked.txt")));
    }

    /// <summary>A file's mode survives a replace on a Mac (review I4: a script lost its +x).</summary>
    [UnixFact]
    public void AReplace_KeepsTheMode_Unix()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.WriteAllText(Full("run.sh"), "echo a\n");
        File.SetUnixFileMode(Full("run.sh"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Assert.Equal(FileOutcome.Ok, _files.WriteText("run.sh", "echo b\n", overwrite: true).Outcome);
        Assert.True(File.GetUnixFileMode(Full("run.sh")).HasFlag(UnixFileMode.UserExecute));
    }

    /// <summary>A hard-linked file is written in place, so its other name sees the change (review I4).</summary>
    [WindowsFact]
    public void AHardLinkedFile_KeepsItsLinks()
    {
        File.WriteAllText(Full("a.txt"), "one");
        using (var child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /H \"{Full("b.txt")}\" \"{Full("a.txt")}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!)
        {
            child.StandardOutput.ReadToEnd();
            child.WaitForExit();
        }

        if (!File.Exists(Full("b.txt")))
        {
            return;   // no hard links on this volume
        }

        Assert.Equal(2, FileLinks.Count(Full("a.txt")));
        Assert.Equal(FileOutcome.Ok, _files.WriteText("a.txt", "two", overwrite: true).Outcome);
        Assert.Equal("two", File.ReadAllText(Full("b.txt")));
    }

    [Fact]
    public void ATempSibling_IsShort_AndKnown()
    {
        string temp = WorkingDirectory.TempSibling(Full(new string('n', 240) + ".txt"));
        Assert.Equal(43, Path.GetFileName(temp).Length);   // review K9: the target's name no longer repeats in it
        Assert.True(WorkingDirectory.IsTempSibling(Path.GetFileName(temp)));
        Assert.False(WorkingDirectory.IsTempSibling("notes.tmp"));
    }

    // ── seen stamps ──────────────────────────────────────────────────────────

    /// <summary>A same-size change in the same tick is still a change (review Q11), and the stamp is the read's own (J12).</summary>
    [Fact]
    public void ASeenFile_ChangedInTheSameTick_IsNotSeen()
    {
        File.WriteAllText(Full("a.txt"), "aaaa");
        var read = _files.ReadText("a.txt", null, null);
        _files.MarkSeen("a.txt", read.Seen);
        Assert.True(_files.WasSeen("a.txt"));

        var written = File.GetLastWriteTimeUtc(Full("a.txt"));
        File.WriteAllText(Full("a.txt"), "bbbb");
        File.SetLastWriteTimeUtc(Full("a.txt"), written);
        Assert.False(_files.WasSeen("a.txt"));
    }

    // ── search ───────────────────────────────────────────────────────────────

    [Fact]
    public void AMultiLineNeedle_IsRefused()
    {
        File.WriteAllText(Full("a.txt"), "one\ntwo\n");
        var result = _files.Search("one\ntwo", "", null, false, CancellationToken.None);
        Assert.Equal(FileOutcome.OneLineOnly, result.Outcome);
        Assert.Equal(FileText.SearchOneLine, FileText.SearchHits(result, "one\ntwo"));
    }

    // ── the vault ────────────────────────────────────────────────────────────

    private ObsidianVault Vault(out string root)
    {
        root = Path.Combine(_root, "vault");
        Directory.CreateDirectory(Path.Combine(root, ".obsidian"));
        string vault = root;
        return new ObsidianVault(() => vault, new ManualTimeProvider());
    }

    /// <summary>A CR-only note's links are rewritten in place by a move (review H1).</summary>
    [Fact]
    public void AMove_RewritesACrOnlyNotesLinks()
    {
        var vault = Vault(out string root);
        Directory.CreateDirectory(Path.Combine(root, "Notes"));
        File.WriteAllText(Path.Combine(root, "Notes", "Plan.md"), "# Plan\n");
        File.WriteAllText(Path.Combine(root, "Index.md"), "top\rsee [[Notes/Plan]] here\rend\r");
        Assert.StartsWith("moved Notes/Plan.md", vault.Move("Notes/Plan", "Archive/Plan"), StringComparison.Ordinal);
        Assert.Equal("top\rsee [[Archive/Plan]] here\rend\r", File.ReadAllText(Path.Combine(root, "Index.md")));
    }

    /// <summary>vault_read: a first line past the cap is cut, not empty (K3); a huge max_lines reads (I16); a start past a section is said (M10).</summary>
    [Fact]
    public void AVaultRead_HandlesItsEdges()
    {
        var vault = Vault(out string root);
        File.WriteAllText(Path.Combine(root, "Long.md"), new string('x', WorkingDirectory.MaxReadChars + 50) + "\nsecond\n");
        Assert.Contains(new string('x', 1000), vault.Read("Long", "", null, null), StringComparison.Ordinal);

        File.WriteAllText(Path.Combine(root, "Note.md"), "# A\none\ntwo\n# B\nthree\n");
        Assert.Contains("one\ntwo", vault.Read("Note", "", null, int.MaxValue), StringComparison.Ordinal);
        Assert.StartsWith("Error: start_line 9 is past the section 'A'", vault.Read("Note", "A", 9, null), StringComparison.Ordinal);
        Assert.Contains("three", vault.Read("Note", "", -1, null), StringComparison.Ordinal);
    }
}
