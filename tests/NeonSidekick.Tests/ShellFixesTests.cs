using NeonSidekick.Files;
using NeonSidekick.Settings;
using NeonSidekick.Shell;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// The shell after the 2026-10-22 fixes (the fix plan's Phase 10): a cmd line with an unclosed quote and a PowerShell command defined
/// through its drive both ask; a script found after its interpreter, refused while it isn't there yet, read for the forbidden strings;
/// a drive-relative path judged; the output buffer counting bytes and cutting whole characters; ids never handed out twice; the trusted
/// files' tripwire.
/// </summary>
public sealed class ShellFixesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", "shellfix-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly WorkingDirectory _files;

    public ShellFixesTests()
    {
        _root = Path.Combine(_dir, "work");
        Directory.CreateDirectory(_root);
        _files = new WorkingDirectory(() => _root, new ManualTimeProvider());
    }

    public void Dispose()
    {
        try { Junction.DeleteTree(_dir); } catch { /* best effort */ }
    }

    [Fact]
    public void ACmdLineWithAnUnclosedQuote_Asks()
    {
        Assert.Contains(CommandPrefix.Unreadable, CommandPrefix.All("echo \"a & del x", ShellKind.Cmd));
        Assert.DoesNotContain(CommandPrefix.Unreadable, CommandPrefix.All("echo \"a\" & dir", ShellKind.Cmd));
        Assert.DoesNotContain(CommandPrefix.Unreadable, CommandPrefix.All("echo ^\" & dir", ShellKind.Cmd));   // an escaped quote opens nothing
        Assert.True(CommandPrefix.CmdQuoteOpen("a \"b"));
        Assert.False(CommandPrefix.CmdQuoteOpen("a \"b\" ^\""));
    }

    [Fact]
    public void ACommandDefinedThroughItsDrive_Asks()
    {
        Assert.Contains(CommandPrefix.Unreadable, CommandPrefix.All("${function:git} = ${function:Remove-Item}; git status", ShellKind.PowerShell));
        Assert.Contains(CommandPrefix.Unreadable, CommandPrefix.All("$alias:ls = 'Remove-Item'; ls x", ShellKind.PowerShell));
        Assert.DoesNotContain(CommandPrefix.Unreadable, CommandPrefix.All("$x = 1; git status", ShellKind.PowerShell));
    }

    [Fact]
    public void AScript_IsFoundAfterItsInterpreter_AndOneNotThereYet_IsNamed()
    {
        File.WriteAllText(Path.Combine(_root, "job"), "print('x')");
        Assert.Equal(["job"], SqlitePolice.ScriptFiles("python job --fast", _root, _root).Select(s => s.Name));
        Assert.Equal(["a.sh"], SqlitePolice.MissingScripts("echo rm -rf x > a.sh && bash a.sh", _root, _root));
        Assert.Equal(["b.ps1"], SqlitePolice.MissingScripts((OperatingSystem.IsWindows() ? ".\\" : "./") + "b.ps1 -Force", _root, _root));   // off Windows "\" is no separator
        Assert.Empty(SqlitePolice.MissingScripts("python job && touch c.sh", _root, _root));
    }

    [Fact]
    public void TheForbiddenStrings_AreReadInAScriptTheLineRuns()
    {
        File.WriteAllText(Path.Combine(_root, "run.ps1"), "Write-Host hi\nformat c:");
        var effective = new AppSettingsData { ShellPolice = true, ShellPoliceForbiddenStrings = ["format c:"] };
        var refused = ShellPoliceChecks.ScriptChecks(new CommandRequest("powershell", "pwsh -File run.ps1", []), "pwsh -File run.ps1", _root, _files, effective);
        Assert.Equal(ShellText.Forbidden, refused!.ModelText);
        Assert.Null(ShellPoliceChecks.ScriptChecks(new CommandRequest("powershell", "pwsh -File run.ps1", []), "pwsh -File run.ps1", _root, _files, new AppSettingsData { ShellPolice = false, ShellPoliceForbiddenStrings = ["format c:"] }));
    }

    [WindowsFact]
    public void ADriveRelativePath_IsJudged()
    {
        char other = char.ToUpperInvariant(_root[0]) == 'Q' ? 'R' : 'Q';
        Assert.NotNull(PathPolice.Judge("type " + other + ":secret.txt", _files, _root, isScript: false, "cmd"));
        Assert.Null(PathPolice.Judge("type " + _root[0] + ":notes.txt", _files, _root, isScript: false, "cmd"));
        Assert.NotNull(PathPolice.Judge("type " + _root[0] + ":..\\..\\x.txt", _files, _root, isScript: false, "cmd"));
        Assert.NotNull(PathPolice.Judge("Get-Content $PROFILE", _files, _root, isScript: false, "powershell"));
    }

    [Fact]
    public void TheOutputBuffer_CountsBytes_AndCutsWholeCharacters()
    {
        var buffer = new OutputBuffer(maxLines: 100, maxBytes: 20_000);
        buffer.Append(new string('a', 6_000), false);
        buffer.Append(new string('b', 6_000), false);
        var kept = new List<OutputLine>();
        buffer.CopyFrom(1, kept);
        Assert.Single(kept);   // 24,000 bytes held as UTF-16 is past 20,000

        string emoji = string.Concat(Enumerable.Repeat("😀", OutputBuffer.MaxLineChars));
        buffer.Append(emoji, false);
        kept.Clear();
        buffer.CopyFrom(1, kept);
        string cut = kept[^1].Text;
        Assert.False(char.IsHighSurrogate(cut[^(OutputBuffer.CutMark.Length + 1)]));
    }

    [Fact]
    public void AnId_IsNeverHandedOutTwice()
    {
        string first = ShellRunner.NewId(new Random(7), "run_");
        string second = ShellRunner.NewId(new Random(7), "run_");
        Assert.NotEqual(first, second);
        Assert.Matches("^run_[0-9a-f]{6}$", second);
    }

    [Fact]
    public void TheTrustedFiles_AreATripwire_ButABareGitIsNot()
    {
        var guard = new TrustedFilesPolice.Guard(Path.Combine(_dir, "home"), _root);
        Assert.Equal(".git\\config", TrustedFilesPolice.Find("type .git\\config", guard));
        Assert.Equal(".neon/plans/x.md", TrustedFilesPolice.Find("echo x > .neon/plans/x.md", guard));
        Assert.Equal("CLAUDE.md", TrustedFilesPolice.Find("Set-Content CLAUDE.md 'x'", guard));
        Assert.NotNull(TrustedFilesPolice.Find("copy x " + Path.Combine(_dir, "home", "profiles", "p.json"), guard));
        Assert.NotNull(TrustedFilesPolice.Find("cat ~/.neonsidekick/configs/sql.json", guard));
        Assert.Null(TrustedFilesPolice.Find("git add . && git commit -m x", guard));
        Assert.Null(TrustedFilesPolice.Find("cat .gitignore .github/workflows/x.yml", guard));
        Assert.Equal("--git-dir=.git", TrustedFilesPolice.Find("hub --git-dir=.git x", guard));
    }
}
