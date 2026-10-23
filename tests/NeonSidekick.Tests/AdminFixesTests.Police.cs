using NeonSidekick.Settings;
using NeonSidekick.Shell;

namespace NeonSidekick.Tests;

/// <summary>
/// Phase 4 of the admin review's fixes (2026-10-23): the shell police's Windows pass (mechanism 7). By the user's choice (option 2), the new
/// patterns are held here by the lines they must still let through, the existing <see cref="AdminPoliceTests"/> keep the refusals they had,
/// and the pass's own pieces (unescaping, the lookups, the decoder) are tested on their own.
/// </summary>
public sealed partial class AdminFixesTests
{
    private static readonly AdminGuard EveryFamily = new(true, true, true, EventLog: true);

    /// <summary>DA7, T16: a lookup, a search, a -WhatIf and a read of every family pass.</summary>
    [Theory]
    [InlineData("Get-Help Stop-Service -Full")]
    [InlineData("Get-Command Restart-Service")]
    [InlineData("help Set-Service")]
    [InlineData("findstr /i \"sc stop\" notes.txt")]
    [InlineData("Select-String -Path log.txt -Pattern 'Disable-ScheduledTask'")]
    [InlineData("rg -n Clear-EventLog .")]
    [InlineData("grep -r \"wevtutil cl\" scripts")]
    [InlineData("Stop-Service Spooler -WhatIf")]
    [InlineData("Disable-ScheduledTask -TaskName x -WhatIf:$true")]
    [InlineData("wevtutil qe System /c:5 /f:text")]
    [InlineData("wevtutil gl Security")]
    [InlineData("Get-WinEvent -LogName System -MaxEvents 5")]
    [InlineData("Get-EventLog -LogName Application -Newest 5")]
    [InlineData("auditpol /get /category:*")]
    [InlineData("net localgroup \"Event Log Readers\"")]
    [InlineData("setspn -L svc_web")]
    [InlineData("dsacls \"CN=Bob,DC=lab,DC=example\"")]
    [InlineData("Set-AdminAuditLogConfig -AdminAuditLogEnabled $true")]
    [InlineData("Get-ScheduledJob")]
    [InlineData("Get-JobTrigger -Name nightly")]
    [InlineData("Get-CimInstance -Namespace Root/Microsoft/Windows/TaskScheduler PS_ScheduledTask")]
    [InlineData("reg query HKLM/SYSTEM/CurrentControlSet/Services/Spooler")]
    [InlineData("Get-Service Spooler | Select-Object Status")]
    [InlineData("taskkill /im notepad.exe")]
    [InlineData("echo the start time was 9:00 and the lock screen showed")]
    public void Police_TheseStillPass(string line) => Assert.Null(AdminPolice.Find(line, EveryFamily));

    /// <summary>T6: the escapes cmd and PowerShell take off, and the quotes, are off in the read the police also judges.</summary>
    [Fact]
    public void Police_TheShellsReading_UndoesEscapesAndQuotes()
    {
        Assert.Equal("abcde", AdminPolice.Unescaped("a^b`c\"d'e"));
        Assert.Equal("one two", AdminPolice.Unescaped("one ^\r\ntwo"));
        Assert.Equal("one two", AdminPolice.Unescaped("one `\ntwo"));
    }

    /// <summary>DA7: the statements that only look something up leave the text the police judges; the rest stay, a line each.</summary>
    [Fact]
    public void Police_TheLookups_LeaveTheJudgedText()
    {
        Assert.Equal("dir |;\n Get-Date", AdminPolice.WithoutLookups("dir | findstr x; Get-Help y\n Get-Date"));
        Assert.Equal("", AdminPolice.WithoutLookups("Get-Help x -Full"));
        Assert.Equal("& Get-Date", AdminPolice.WithoutLookups("C:\\Tools\\rg.exe pattern & Get-Date").Trim());
    }

    /// <summary>DP7 (the branch review, 2026-10-23): a statement that evaluates, a -WhatIf in a comment and a lookup that runs a program stay judged.</summary>
    [Theory]
    [InlineData("help (Get-Date)")]
    [InlineData("Get-Help $(Get-Date)")]
    [InlineData("Get-Date # -WhatIf")]
    [InlineData("Get-Date -WhatIf { }")]
    [InlineData("rg --pre cat x .")]
    [InlineData("rg --pre=cat x .")]
    [InlineData("man -P cat ls")]
    public void Police_WhatStillRuns_StaysJudged(string statement) =>
        Assert.Equal(statement, AdminPolice.WithoutLookups(statement));

    /// <summary>DP7: a -e past -Command or -File is the command's own flag, not an encoded command.</summary>
    [Theory]
    [InlineData("pwsh -c \"docker run -e FOO=bar img\"")]
    [InlineData("powershell -Command grep -e pattern file")]
    [InlineData("pwsh -File run.ps1 -e x")]
    public void Police_AnotherProgramsFlag_IsNoEncodedCommand(string line) =>
        Assert.Null(AdminPolice.Find(line, EveryFamily));

    /// <summary>DA4: an encoded command is read from UTF-16LE base64 and judged; a harmless one passes; one that will not decode is refused.</summary>
    [Fact]
    public void Police_EncodedCommands_AreRead()
    {
        string encoded = ShellCommandLine.Encode("Get-Date");
        Assert.Equal("Get-Date", AdminPolice.Decode(encoded));
        Assert.Null(AdminPolice.Decode("not base64!"));
        Assert.Null(AdminPolice.Decode(new string('A', AdminPolice.MaxEncodedChars + 4)));
        Assert.Null(AdminPolice.Find("powershell -NoProfile -e " + encoded, EveryFamily));
        Assert.Null(AdminPolice.Find("pwsh /ec " + encoded, EveryFamily));
        Assert.Equal(AdminPolice.EncodedFamily, AdminPolice.Find("powershell -enc ###", EveryFamily)?.Family);
        Assert.Null(AdminPolice.Find("powershell -enc ###", new AdminGuard(false, false, false)));
    }

    /// <summary>L1: the event logs are guarded while their tools are on.</summary>
    [Fact]
    public void Police_TheEventLogsAreGuarded_WithTheirTools()
    {
        Assert.Equal(new AdminGuard(false, false, false, EventLog: true), AdminPolice.For(new AppSettingsData { EventLogTools = true }));
        Assert.Null(AdminPolice.For(new AppSettingsData()));
        Assert.Contains("eventlog_", ShellText.AdminPoliced(AdminPolice.EventLogFamily));
    }
}
