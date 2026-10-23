using Microsoft.Extensions.AI;
using NeonSidekick.Computers;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Services;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>Phase 5 of the admin review's fixes (2026-10-23): the services' changes, their questions, their waits and their list.</summary>
public sealed partial class AdminFixesTests
{
    private static FakeServiceSystem Services()
    {
        var system = new FakeServiceSystem();
        system.Add("App", "My App", state: ServiceRunState.Stopped, start: ServiceStartType.Manual, image: @"C:\apps\app.exe");
        system.Add("Helper", "App Helper", state: ServiceRunState.Stopped, start: ServiceStartType.Manual, image: @"C:\apps\helper.exe");
        system.Add("Shared", "Shared Host", image: @"C:\Windows\System32\svchost.exe -k netsvcs");
        system.Services["Shared"].Dll = @"%SystemRoot%\System32\shared.dll";
        system.Services["App"].Config = system.Services["App"].Config with { Dependencies = ["Helper", "+SomeGroup"], Account = @"NT AUTHORITY\LocalService" };
        system.Services["Helper"].DependsOnMe.Add("App");
        return system;
    }

    private (ServiceAccess Access, ServiceSource Source) ServiceDoor(FakeServiceSystem system, ServiceComputerConfig? local = null)
    {
        var config = local ?? new ServiceComputerConfig { Access = "readwrite", Control = ["App", "Helper", "Shared"] };
        var access = new ServiceAccess(system, () => new ComputerCatalog<ServiceComputerConfig>([new Computer<ServiceComputerConfig>("local", config)], [], ServiceConfigFile.Info), () => ["local"], _time);
        return (access, access.Computer(null, forceRedacted: false).Source!);
    }

    /// <summary>M3, G3: a start that starts its stopped dependency names it and the service's own account; one the checks refuse stops it.</summary>
    [Fact]
    public async Task M3_G3_AStart_NamesWhatItAlsoStarts_AndItsAccount()
    {
        var system = Services();
        var (access, source) = ServiceDoor(system);
        string? asked = null;
        string answer = await ServiceChanges.RunAsync(access, source, "App", ServiceActions.Start, null, true, false, (q, _) => { asked = q; return Task.FromResult<bool?>(true); }, CancellationToken.None);
        Assert.Contains("This also starts: Helper", asked);
        Assert.Contains(@"runs as NT AUTHORITY\LocalService", asked);
        Assert.StartsWith("Started", answer);

        var narrow = Services();
        var (narrowAccess, narrowSource) = ServiceDoor(narrow, new ServiceComputerConfig { Access = "readwrite", Control = ["App"] });
        string refused = await ServiceChanges.RunAsync(narrowAccess, narrowSource, "App", ServiceActions.Start, null, true, false, (_, _) => Task.FromResult<bool?>(true), CancellationToken.None);
        Assert.StartsWith("Error: starting App would also start Helper", refused);
        Assert.Empty(narrow.Changes);
    }

    /// <summary>DP7 (the branch review, 2026-10-23): a dependency that cannot be read is left to Windows, never the start's own error.</summary>
    [Fact]
    public async Task DP7_AnUnreadableDependency_IsLeftToWindows()
    {
        var system = Services();
        system.Services["App"].Config = system.Services["App"].Config with { Dependencies = ["NoSuchService"] };
        var (access, source) = ServiceDoor(system);
        string answer = await ServiceChanges.RunAsync(access, source, "App", ServiceActions.Start, null, true, false, (_, _) => Task.FromResult<bool?>(true), CancellationToken.None);
        Assert.StartsWith("Started", answer);
    }

    /// <summary>M4, L2, T10: a disable is refused while dependents run, its question shows the program, a shared service's DLL, and its dependents.</summary>
    [Fact]
    public async Task M4_L2_T10_AStartTypeQuestion_ShowsWhatRuns()
    {
        var system = Services();
        system.Services["Helper"].Entry = system.Services["Helper"].Entry with { State = ServiceRunState.Running };
        system.Services["App"].Entry = system.Services["App"].Entry with { State = ServiceRunState.Running };
        var (access, source) = ServiceDoor(system);
        string refused = await ServiceChanges.RunAsync(access, source, "Helper", ServiceActions.StartType, ServiceStartChoice.Disabled, true, false, (_, _) => Task.FromResult<bool?>(true), CancellationToken.None);
        Assert.StartsWith("Error: Helper cannot be stopped while services that depend on it run", refused);

        string? asked = null;
        await ServiceChanges.RunAsync(access, source, "Shared", ServiceActions.StartType, ServiceStartChoice.Automatic, true, false, (q, _) => { asked = q; return Task.FromResult<bool?>(false); }, CancellationToken.None);
        Assert.Contains("svchost.exe -k netsvcs with %SystemRoot%\\System32\\shared.dll", asked);
        system.Services["Shared"].Dll = null;
        await ServiceChanges.RunAsync(access, source, "Shared", ServiceActions.Stop, null, true, false, (q, _) => { asked = q; return Task.FromResult<bool?>(false); }, CancellationToken.None);
        Assert.Contains(ServiceText.DllUnread, asked);
    }

    /// <summary>G15: a question nobody answered is said and audited as not asked, never as the user's no.</summary>
    [Fact]
    public async Task G15_NobodyToAnswer_IsNotADecline()
    {
        var (access, source) = ServiceDoor(Services());
        string answer = await ServiceChanges.RunAsync(access, source, "App", ServiceActions.Start, null, true, false, (_, _) => Task.FromResult<bool?>(null), CancellationToken.None);
        Assert.Equal(ComputerText.NoPaneToAsk, answer);
    }

    /// <summary>G1: a start that ends stopped with an exit code is a failed start.</summary>
    [Fact]
    public async Task G1_AStartThatStopsAgain_Failed()
    {
        var system = Services();
        var (access, source) = ServiceDoor(system);
        var result = await access.ChangeAsync(source, "Helper", s => { }, _ => true, CancellationToken.None);
        Assert.NotNull(result.Value);
        system.Services["Helper"].Entry = system.Services["Helper"].Entry with { Win32ExitCode = 1066 };
        Assert.Contains("failed: it stopped again", ServiceText.StartFailed(ServiceActions.Start, "Helper", "this PC", 1066, 3));
    }

    /// <summary>K3: the wait ends by the wait hint's progress; a service that never moves ends at its polls, never minutes.</summary>
    [Fact]
    public void K3_TheWait_EndsWithoutProgress()
    {
        var system = Services();
        var (_, end) = system.Run(ServiceTarget.Local, session =>
            ServiceAccess.WaitFor(session, "App", new ServiceStatus(ServiceRunState.StartPending, 0, 0, 0, 0, 0, 1), _ => false, _time, new CancellationToken(canceled: true)));
        Assert.Equal(ServiceWaitEnd.Cancelled, end);
    }

    /// <summary>A7, E4, S9: the list stops reading start types once the page is full, says "at least", and its next call keeps every filter.</summary>
    [Fact]
    public async Task A7_E4_TheListsStartFilter_StopsEarly_AndItsNextCallKeepsTheFilters()
    {
        var system = new FakeServiceSystem();
        for (int i = 0; i < 30; i++)
        {
            system.Add("svc" + i.ToString("D2", System.Globalization.CultureInfo.InvariantCulture), "Service " + i, start: ServiceStartType.Manual);
        }

        var (access, _) = ServiceDoor(system, new ServiceComputerConfig());
        var tool = new ServiceListTool(access, () => new AppSettingsData { ServiceTools = true });
        var args = new AIFunctionArguments { ["start"] = "manual", ["state"] = "running", ["limit"] = 5 };
        string text = (string)(await tool.InvokeAsync(args))!;
        Assert.Contains("5 of at least 6 matching", text);
        Assert.Contains("next: service_list(offset 5, state running, start manual, limit 5)", text);
    }

    /// <summary>J6, J7: a runas entry for this PC is refused; a remote write's access denied is about the service's own rights.</summary>
    [Fact]
    public void J6_J7_RunAsOnThisPc_AndRemoteWriteRights()
    {
        var entry = new ServiceComputerConfig { Host = "localhost", Auth = "runas", User = @"CORP\ops" };
        Assert.Equal(ComputerText.RunAsOnThisPc, entry.Problem("me", ServiceConfigFile.Info));
        Assert.True(ComputerConfig.IsThisPc(Environment.MachineName));
        Assert.False(ComputerConfig.IsThisPc("app01.corp.example"));
        Assert.Contains("its own permissions", ServiceErrors.Sentence(5, new ServiceErrorContext("app01", "Spooler", Remote: true, @"CORP\ops", Write: true), ""));
    }

    /// <summary>G5: a domain controller's own services are protected.</summary>
    [Theory]
    [InlineData("kdc")]
    [InlineData("NTDS")]
    [InlineData("DNS")]
    [InlineData("DFSR")]
    [InlineData("WinRM")]
    public void G5_ADomainControllersServices_AreProtected(string name) =>
        Assert.Equal(ServiceProtection.App, ServiceGuard.Protection(name, new ServiceComputerConfig()));

    /// <summary>R6: a read-only computer refuses a change before any password is looked for.</summary>
    [Fact]
    public async Task R6_AReadOnlyComputer_RefusesBeforeThePassword()
    {
        var remote = new Computer<ServiceComputerConfig>("app01", new ServiceComputerConfig { Host = "app01", Auth = "runas", User = @"CORP\ops", PasswordStore = "credman", Credential = "NeonSidekick/services/none-such" });
        var access = new ServiceAccess(Services(), () => new ComputerCatalog<ServiceComputerConfig>([remote], [], ServiceConfigFile.Info), () => ["app01"], _time);
        var tool = new ServiceControlTool(access, () => new AppSettingsData { ServiceTools = true, ServiceWrites = true }, (_, _) => Task.FromResult<bool?>(true));
        string text = (string)(await tool.InvokeAsync(new AIFunctionArguments { ["name"] = "App", ["action"] = "start", ["computer"] = "app01" }))!;
        Assert.Equal(ComputerText.ReadOnlyComputer("app01", ServiceConfigFile.Info), text);
    }
}
