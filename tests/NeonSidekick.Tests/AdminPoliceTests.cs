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
    // The forms a review found past the police (2026-10-20): a quoted exe, schtasks' dashes, a ServiceController's methods, WMI, the Win32
    // API's service calls, a service's registry key, the scheduler's COM object.
    [InlineData("\"C:\\Windows\\System32\\sc.exe\" stop Spooler", AdminPolice.ServicesFamily)]
    [InlineData("'sc.exe' config Spooler start= disabled", AdminPolice.ServicesFamily)]
    [InlineData("\"net.exe\" stop Spooler", AdminPolice.ServicesFamily)]
    [InlineData("(Get-Service Spooler).Stop()", AdminPolice.ServicesFamily)]
    [InlineData("Get-Service Spooler | ForEach-Object { $_.Stop() }", AdminPolice.ServicesFamily)]
    [InlineData("$s = Get-Service Spooler\n$s.Stop()", AdminPolice.ServicesFamily)]
    [InlineData("$c = New-Object System.ServiceProcess.ServiceController('Spooler')\r\n$c.Start()", AdminPolice.ServicesFamily)]
    [InlineData("Invoke-CimMethod -ClassName Win32_Service -MethodName StopService", AdminPolice.ServicesFamily)]
    [InlineData("Get-CimInstance Win32_Service -Filter \"Name='Spooler'\" | Invoke-CimMethod -MethodName ChangeStartMode -Arguments @{StartMode='Disabled'}", AdminPolice.ServicesFamily)]
    [InlineData("wmic service where name='Spooler' call stopservice", AdminPolice.ServicesFamily)]
    [InlineData("import win32serviceutil\nwin32serviceutil.StopService('Spooler')", AdminPolice.ServicesFamily)]
    [InlineData("advapi.ControlService(handle, 1, ctypes.byref(status))", AdminPolice.ServicesFamily)]
    [InlineData("reg add HKLM\\SYSTEM\\CurrentControlSet\\Services\\Spooler /v Start /t REG_DWORD /d 4 /f", AdminPolice.ServicesFamily)]
    [InlineData("Set-ItemProperty HKLM:\\SYSTEM\\CurrentControlSet\\Services\\Spooler -Name Start -Value 4", AdminPolice.ServicesFamily)]
    [InlineData("schtasks -run -tn \\Backup\\X", AdminPolice.TasksFamily)]
    [InlineData("\"schtasks.exe\" /End /TN \\Backup\\X", AdminPolice.TasksFamily)]
    [InlineData("$s = New-Object -ComObject Schedule.Service; $s.Connect(); $s.GetFolder('\\Backup').GetTask('X').Run($null)", AdminPolice.TasksFamily)]
    [InlineData("$t = (New-Object -ComObject Schedule.Service).GetFolder('\\').GetTask('X')\n$t.Enabled = $false", AdminPolice.TasksFamily)]
    [InlineData("Get-CimInstance -Namespace Root/Microsoft/Windows/TaskScheduler MSFT_ScheduledTask | Invoke-CimMethod -MethodName Run", AdminPolice.TasksFamily)]
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
    [InlineData("(Get-Service Spooler).Status")]
    [InlineData("Get-CimInstance Win32_Service | Select-Object Name, StartMode, State")]
    [InlineData("reg query HKLM\\SYSTEM\\CurrentControlSet\\Services\\Spooler")]
    [InlineData("Get-ItemProperty HKLM:\\SYSTEM\\CurrentControlSet\\Services\\Spooler")]
    [InlineData("$s = New-Object -ComObject Schedule.Service; $s.Connect(); $s.GetFolder('\\').GetTasks(1).Count")]
    [InlineData("schtasks /query /tn my-run-task")]
    [InlineData("$p = Start-Process notepad -PassThru; $timer.Start()")]
    [InlineData("python -c \"import threading; t = threading.Thread(target=f); t.start()\"")]
    public void ARead_Passes(string line) => Assert.Null(AdminPolice.Find(line, All));

    /// <summary>A Mac's own directory writers (2026-10-18, the Mac AD plan): refused on a Mac, never looked for on Windows.</summary>
    [Theory]
    [InlineData("dscl \"/Active Directory/LAB/All Domains\" -create /Users/bob")]
    [InlineData("dscl '/Active Directory/LAB/All Domains' -passwd /Users/bob NewPass1!")]
    [InlineData("dscl /Search -delete /Groups/Ops GroupMembership bob")]
    [InlineData("sudo dsconfigad -remove -force -username admin")]
    [InlineData("dsconfigad -add lab.example -username admin")]
    public void AMacDirectoryChange_IsFoundOnAMac(string line) =>
        Assert.Equal(OperatingSystem.IsMacOS() ? AdminPolice.DirectoryFamily : null, AdminPolice.Find(line, All)?.Family);

    [Theory]
    [InlineData("dscl \"/Active Directory/LAB/All Domains\" -read /Users/bob")]
    [InlineData("dscl /Search -list /Users")]
    [InlineData("dscl . -create /Users/localbob")]
    [InlineData("dsconfigad -show")]
    public void AMacDirectoryRead_Passes(string line) => Assert.Null(AdminPolice.Find(line, All));

    /// <summary>
    /// The Mac's directory writers read as commands (2026-10-20, the code review): the node after the verb, an escaped space, the longer
    /// verbs, a verb without its dash, a <c>dscl</c> reading its commands from its input, every changing <c>dsconfigad</c> option and
    /// <c>dseditgroup</c> on a directory node. Pure, so every machine checks it.
    /// </summary>
    [Theory]
    [InlineData("dscl localhost -create \"/Active Directory/LAB/All Domains/Users/bob\"")]
    [InlineData("dscl /Active\\ Directory/LAB/All\\ Domains -create /Users/bob")]
    [InlineData("dscl \"/Active Directory/LAB/All Domains\" -createpl /Users/bob k p v")]
    [InlineData("dscl /Search -mcxset /Computers/x com.apple.x k always 1")]
    [InlineData("dscl /Search -changei /Users/bob RealName 1 Bob")]
    [InlineData("dscl \"/Active Directory/LAB\" create /Users/bob")]                 // no dash
    [InlineData("dscl -u admin -P secret /Search -deletepl /Users/bob k p")]
    [InlineData("dscl /LDAPv3/ldap.lab.example -create /Users/bob")]                // a write off the local node
    [InlineData("dscl . -create /Search/Users/bob")]                                 // names /Search: refused, erring safe
    [InlineData("n=\"/Active Directory/LAB\"; dscl \"$n\" -delete /Users/bob")]
    [InlineData("echo 'create /Users/bob' | dscl \"/Active Directory/LAB\"")]       // interactive, from its input
    [InlineData("dscl localhost < commands.txt")]
    [InlineData("sudo /usr/bin/dscl localhost")]
    [InlineData("dsconfigad -groups \"LAB\\Domain Admins\"")]
    [InlineData("dsconfigad -alldomains disable")]
    [InlineData("dseditgroup -o edit -n \"/Active Directory/LAB/All Domains\" -a bob -t user Ops")]
    [InlineData("dseditgroup -o create -n /LDAPv3/ldap.lab.example Ops")]
    public void AMacDirectoryChange_IsReadAsACommand(string line) => Assert.NotNull(AdminPolice.MacDirectoryChange(line));

    [Theory]
    [InlineData("dscl . -create /Users/localbob")]
    [InlineData("dscl . create /Users/localbob")]
    [InlineData("dscl -f /Volumes/x/var/db/dslocal/nodes/Default localonly -create /Local/Target/Users/bob")]
    [InlineData("dscl /Local/Default -delete /Users/old")]
    [InlineData("dscl \"/Active Directory/LAB/All Domains\" -readall /Users")]
    [InlineData("dscl /Search -search /Users RealName Bob")]
    [InlineData("dscl /Active\\ Directory/LAB -list /Groups")]
    [InlineData("which dscl && man dscl")]
    [InlineData("dsconfigad -show -xml")]
    [InlineData("dsconfigad")]
    [InlineData("dseditgroup -o read -n \"/Active Directory/LAB\" Ops")]
    [InlineData("dseditgroup -o edit -a bob -t user staff")]                         // the local node
    public void AMacDirectoryRead_OrALocalChange_IsNotRefused(string line) => Assert.Null(AdminPolice.MacDirectoryChange(line));

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
