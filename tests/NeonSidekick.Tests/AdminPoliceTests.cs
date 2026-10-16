using NeonSidekick.Settings;
using NeonSidekick.Shell;

namespace NeonSidekick.Tests;

/// <summary>
/// The shell police's admin rule (2026-10-15, the admin-tools plan): a harmful line per pinned pattern and the reads each family must let
/// through; <c>net user|group … /domain</c> judged by its words; a family guarded only while its tools are on; a script file read once.
/// </summary>
public sealed class AdminPoliceTests
{
    private static readonly AdminGuard All = new(true, true, true);

    private static readonly Lazy<IReadOnlyList<(string Name, string Text)>> NoScripts = new(() => []);

    [Theory]
    [InlineData("sc stop Spooler", AdminPolice.ServicesFamily)]
    [InlineData(@"sc.exe \\app01 config MyApp start= disabled", AdminPolice.ServicesFamily)]
    [InlineData("SC DELETE MyApp", AdminPolice.ServicesFamily)]
    [InlineData("net stop wuauserv", AdminPolice.ServicesFamily)]
    [InlineData("net1 start Spooler", AdminPolice.ServicesFamily)]
    [InlineData("Get-Service Spooler | Restart-Service -Force", AdminPolice.ServicesFamily)]
    [InlineData("Set-Service -Name MyApp -StartupType Disabled", AdminPolice.ServicesFamily)]
    [InlineData("schtasks /run /tn \\Backup\\Nightly", AdminPolice.TasksFamily)]
    [InlineData("schtasks.exe /S app01 /Change /TN x /DISABLE", AdminPolice.TasksFamily)]
    [InlineData("schtasks /create /sc daily /tn x /tr calc.exe", AdminPolice.TasksFamily)]
    [InlineData("Disable-ScheduledTask -TaskName x", AdminPolice.TasksFamily)]
    [InlineData("Register-ScheduledTask -TaskName x -Action $a", AdminPolice.TasksFamily)]
    [InlineData("Unlock-ADAccount carol", AdminPolice.DirectoryFamily)]
    [InlineData("Set-ADUser bob -Enabled $true", AdminPolice.DirectoryFamily)]
    [InlineData("Add-ADGroupMember Ops bob", AdminPolice.DirectoryFamily)]
    [InlineData("dsmod user \"CN=Bob,DC=x\" -disabled no", AdminPolice.DirectoryFamily)]
    [InlineData("ldapmodify -H ldap://dc1 -f change.ldif", AdminPolice.DirectoryFamily)]
    [InlineData("ldifde -i -f users.ldf", AdminPolice.DirectoryFamily)]
    [InlineData("net user bob NewPass1! /domain", AdminPolice.DirectoryFamily)]
    [InlineData("net user bob /active:yes /domain", AdminPolice.DirectoryFamily)]
    [InlineData("net group \"Domain Admins\" bob /add /domain", AdminPolice.DirectoryFamily)]
    public void AChange_IsFound_ForItsFamily(string line, string family) => Assert.Equal(family, AdminPolice.Find(line, All)?.Family);

    [Theory]
    [InlineData("sc query Spooler")]
    [InlineData("sc.exe qc Spooler")]
    [InlineData("Get-Service | Where-Object Status -eq Running")]
    [InlineData("net start")]
    [InlineData("schtasks /query /fo LIST /v /tn \\Backup\\Nightly")]
    [InlineData("Get-ScheduledTask -TaskPath \\Backup\\ | Get-ScheduledTaskInfo")]
    [InlineData("Get-ADUser bob -Properties LockedOut")]
    [InlineData("Search-ADAccount -LockedOut")]
    [InlineData("net user bob /domain")]
    [InlineData("net user \"Bob Jones\" /domain")]
    [InlineData("net group \"Domain Admins\" /domain")]
    [InlineData("csvde -f out.csv")]
    [InlineData("ldapsearch -H ldap://dc1 -b dc=lab,dc=example")]
    [InlineData("echo the scheduler and the services are fine")]
    public void ARead_Passes(string line) => Assert.Null(AdminPolice.Find(line, All));

    [Fact]
    public void AFamily_IsGuardedOnlyWhileItsToolsAreOn()
    {
        Assert.Null(AdminPolice.For(new AppSettingsData()));
        var guard = AdminPolice.For(new AppSettingsData { TaskTools = true })!;
        Assert.Equal(new AdminGuard(false, true, false), guard);
        Assert.Null(AdminPolice.Find("sc stop Spooler", guard));
        Assert.Equal(AdminPolice.TasksFamily, AdminPolice.Find("schtasks /end /tn x", guard)?.Family);
    }

    [Fact]
    public void AScriptFile_IsReadOnce_AndNamed()
    {
        int reads = 0;
        var scripts = new Lazy<IReadOnlyList<(string Name, string Text)>>(() =>
        {
            reads++;
            return [("ok.ps1", "Get-Service"), ("fix.ps1", "Stop-Service Spooler")];
        });

        var verdict = AdminPolice.Judge("powershell -File fix.ps1", scripts, All);
        Assert.Equal((AdminPolice.ServicesFamily, "Stop-Service", "fix.ps1"), verdict);
        Assert.Null(AdminPolice.Judge("powershell -File ok.ps1", new Lazy<IReadOnlyList<(string, string)>>(() => [("ok.ps1", "Get-Service")]), All));
        Assert.Equal((AdminPolice.ServicesFamily, "sc stop", (string?)null), AdminPolice.Judge("sc stop Spooler", scripts, All));
        Assert.Equal(1, reads);
        Assert.Null(AdminPolice.Judge("dir", NoScripts, All));
    }

    [Fact]
    public void TheWords_AreSaidToTheModelAndTheUser()
    {
        Assert.StartsWith(ShellText.AdminHead, ShellText.AdminPoliced(AdminPolice.ServicesFamily));
        Assert.Contains("service_control", ShellText.AdminPoliced(AdminPolice.ServicesFamily));
        Assert.Contains("task_control", ShellText.AdminPoliced(AdminPolice.TasksFamily));
        Assert.Equal("Services: 'Stop-Service' in fix.ps1 — not run", ShellText.AdminShown(AdminPolice.ServicesFamily, "Stop-Service", "fix.ps1"));
        Assert.Equal("Tasks: 'schtasks /run' — not run", ShellText.AdminShown(AdminPolice.TasksFamily, "schtasks /run", null));
    }
}
