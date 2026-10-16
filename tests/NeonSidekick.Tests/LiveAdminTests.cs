using System.Data.Common;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.ActiveDirectory;
using NeonSidekick.Computers;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Network;
using NeonSidekick.Services;
using NeonSidekick.Settings;
using NeonSidekick.Tasks;

namespace NeonSidekick.Tests;

/// <summary>
/// The admin families' live gates (2026-10-15, the admin-tools plan), each resolved once per assembly from its variable, Windows only for all
/// but the network's, skipped without, never a CI safety net:
/// <list type="bullet">
/// <item><c>NEONSIDEKICK_TEST_NETWORK_HOST</c>: a host on the network to look up, ping, and (when 443 is open) shake hands with.</item>
/// <item><c>NEONSIDEKICK_TEST_SERVICES_COMPUTER</c>: a computer whose services are read as you (<c>localhost</c> works); with
/// <c>NEONSIDEKICK_TEST_SERVICE_CONTROL</c>, a harmless service on this PC restarted — only when the tests run elevated.</item>
/// <item><c>NEONSIDEKICK_TEST_TASKS_COMPUTER</c>: a computer whose scheduled tasks are read; with <c>NEONSIDEKICK_TEST_TASK_CONTROL</c>, a task
/// you made on this PC run once, disabled and enabled again.</item>
/// <item><c>NEONSIDEKICK_TEST_AD_DOMAIN</c>: <c>Domain=…;Server=…;User=DOMAIN\name;Password=…</c>, written as the <c>lab</c> entry of a scratch
/// home's <c>ad.json</c> and read through it (the <c>samba_ad_dev</c> container: <c>Domain=lab.neon.test;Server=localhost;User=LAB\reader</c>
/// and the container's password).</item>
/// </list>
/// </summary>
internal static class LiveAdmin
{
    public const string NetworkVariable = "NEONSIDEKICK_TEST_NETWORK_HOST";
    public const string ServicesVariable = "NEONSIDEKICK_TEST_SERVICES_COMPUTER";
    public const string ServiceControlVariable = "NEONSIDEKICK_TEST_SERVICE_CONTROL";
    public const string TasksVariable = "NEONSIDEKICK_TEST_TASKS_COMPUTER";
    public const string TaskControlVariable = "NEONSIDEKICK_TEST_TASK_CONTROL";
    public const string DirectoryVariable = "NEONSIDEKICK_TEST_AD_DOMAIN";

    public static string? Read(string variable) => Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value ? value.Trim() : null;

    public static string? Unavailable(string variable, bool windowsOnly) =>
        windowsOnly && !OperatingSystem.IsWindows() ? "There is nothing of this kind to read off Windows."
        : Read(variable) is null ? $"{variable} is not set."
        : null;

    public static bool Elevated => OperatingSystem.IsWindows() && new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
}

public sealed class LiveNetworkFactAttribute : FactAttribute
{
    public LiveNetworkFactAttribute() => Skip = LiveAdmin.Unavailable(LiveAdmin.NetworkVariable, windowsOnly: false);
}

public sealed class LiveServicesFactAttribute : FactAttribute
{
    public LiveServicesFactAttribute() => Skip = LiveAdmin.Unavailable(LiveAdmin.ServicesVariable, windowsOnly: true);
}

public sealed class LiveTasksFactAttribute : FactAttribute
{
    public LiveTasksFactAttribute() => Skip = LiveAdmin.Unavailable(LiveAdmin.TasksVariable, windowsOnly: true);
}

public sealed class LiveDirectoryFactAttribute : FactAttribute
{
    public LiveDirectoryFactAttribute() => Skip = LiveAdmin.Unavailable(LiveAdmin.DirectoryVariable, windowsOnly: true);
}

/// <summary>The four live gates' facts: reads always, a change only where its variable names a harmless thing to change.</summary>
public sealed class LiveAdminTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("neon-admin-live-").FullName;
    private readonly AppSettingsData _settings = new() { NetworkTools = true, NetworkToolsReach = "both", ServiceTools = true, ServiceWrites = true, TaskTools = true, TaskWrites = true, DirectoryTools = true };

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static async Task<string> Call(AIFunction tool, params (string Name, object? Value)[] arguments)
    {
        var args = new AIFunctionArguments();
        foreach (var (name, value) in arguments)
        {
            args[name] = value;
        }

        return (string)(await tool.InvokeAsync(args))!;
    }

    // ── Network ────────────────────────────────────────────────────────────

    [LiveNetworkFact]
    public async Task Network_LookUpPingAndShakeHands()
    {
        string host = LiveAdmin.Read(LiveAdmin.NetworkVariable)!;
        IPinger pinger = OperatingSystem.IsWindows() ? new WindowsPinger() : new IcmpSocketPinger();
        var access = new NetworkAccess(() => _settings, pinger, TimeProvider.System);
        string dns = await Call(new NetDnsTool(access, new NetworkBudget()), ("name", host));
        Assert.DoesNotContain("Error:", dns);
        Assert.StartsWith("Ping " + host, await Call(new NetPingTool(access, new NetworkBudget()), ("host", host), ("count", 2)));
        string port = await Call(new NetPortTool(access, new NetworkBudget()), ("host", host), ("ports", JsonDocument.Parse("[443]").RootElement));
        if (port.Contains("open on", StringComparison.Ordinal))
        {
            string tls = await Call(new NetTlsTool(access, new NetworkBudget()), ("host", host));
            Assert.Contains("TLS 1.", tls);
        }
    }

    // ── Services ───────────────────────────────────────────────────────────

    [SupportedOSPlatform("windows")]
    [LiveServicesFact]
    public async Task Services_ReadOverTheComputersOwnSession()
    {
        string host = LiveAdmin.Read(LiveAdmin.ServicesVariable)!;
        var computer = new Computer<ServiceComputerConfig>("live", new ServiceComputerConfig { Host = host });
        var (ok, text) = await ServiceAccess.TestAsync(new WindowsServiceSystem(), computer, TimeProvider.System, CancellationToken.None);
        Assert.True(ok, text);
        var access = new ServiceAccess(new WindowsServiceSystem(), () => new ComputerCatalog<ServiceComputerConfig>([computer], [], ServiceConfigFile.Info), () => ["live"], TimeProvider.System);
        Assert.Contains("Service EventLog on live: running", await Call(new ServiceInfoTool(access, () => _settings), ("name", "EventLog"), ("computer", "live")));
    }

    [SupportedOSPlatform("windows")]
    [LiveServicesFact]
    public async Task Services_TheNamedHarmlessService_Restarts_WhenElevated()
    {
        if (LiveAdmin.Read(LiveAdmin.ServiceControlVariable) is not { } name || !LiveAdmin.Elevated)
        {
            return;   // only with a service named and an elevated run: a restart needs SERVICE_STOP and SERVICE_START
        }

        var local = new ServiceComputerConfig { Access = "readwrite", Control = [name], ControlActions = ["restart"] };
        var access = new ServiceAccess(new WindowsServiceSystem(), () => new ComputerCatalog<ServiceComputerConfig>([new Computer<ServiceComputerConfig>("local", local)], [], ServiceConfigFile.Info), () => null, TimeProvider.System);
        var tool = new ServiceControlTool(access, () => _settings, (_, _) => Task.FromResult<bool?>(true));
        Assert.Equal(ServiceText.Done("restart", name, Computers.ComputerText.ThisPc, ServiceRunState.Running), await Call(tool, ("name", name), ("action", "restart")));
    }

    // ── Tasks ──────────────────────────────────────────────────────────────

    [SupportedOSPlatform("windows")]
    [LiveTasksFact]
    public async Task Tasks_ReadOverTheComputersOwnConnection()
    {
        string host = LiveAdmin.Read(LiveAdmin.TasksVariable)!;
        var computer = new Computer<TaskComputerConfig>("live", new TaskComputerConfig { Host = host });
        var (ok, text) = await TaskAccess.TestAsync(new WindowsTaskSystem(), computer, TimeProvider.System, CancellationToken.None);
        Assert.True(ok, text);
        var access = new TaskAccess(new WindowsTaskSystem(), () => new ComputerCatalog<TaskComputerConfig>([computer], [], TaskConfigFile.Info), () => ["live"], TimeProvider.System);
        Assert.Contains("Tasks on " + host, await Call(new TaskListTool(access, () => _settings), ("computer", "live"), ("include_microsoft", true), ("limit", 5)));
    }

    [SupportedOSPlatform("windows")]
    [LiveTasksFact]
    public async Task Tasks_TheNamedTask_RunsAndIsDisabledAndEnabledAgain()
    {
        if (LiveAdmin.Read(LiveAdmin.TaskControlVariable) is not { } path)
        {
            return;   // only with a task of your own named
        }

        var local = new TaskComputerConfig { Access = "readwrite", Control = [path], Privileged = true };
        var access = new TaskAccess(new WindowsTaskSystem(), () => new ComputerCatalog<TaskComputerConfig>([new Computer<TaskComputerConfig>("local", local)], [], TaskConfigFile.Info), () => null, TimeProvider.System);
        var tool = new TaskControlTool(access, () => _settings, (_, _) => Task.FromResult<bool?>(true));
        Assert.DoesNotContain("Error:", await Call(tool, ("path", path), ("action", "run")));
        Assert.Contains("disabled", await Call(tool, ("path", path), ("action", "disable")));
        Assert.DoesNotContain("Error:", await Call(tool, ("path", path), ("action", "enable")));
    }

    // ── The directory ──────────────────────────────────────────────────────

    [SupportedOSPlatform("windows")]
    private DirectoryAccess DirectoryFromScratchHome()
    {
        var spec = new DbConnectionStringBuilder { ConnectionString = LiveAdmin.Read(LiveAdmin.DirectoryVariable)! };
        string Part(string key) => spec.TryGetValue(key, out object? value) ? (string)value : throw new InvalidOperationException(LiveAdmin.DirectoryVariable + " has no " + key + ".");
        var config = new DirectoryDomainConfig { Domain = Part("Domain"), Server = Part("Server"), Auth = "runas", User = Part("User"), Description = "the live gate's domain" };
        string path = DirectoryConfigFile.ProfilePath(_root);
        Assert.Null(DirectoryConfigFile.AddDomain(path, "lab", config));
        var saved = DirectoryConfigFile.SavePassword(DirectoryConfigFile.LoadCatalog(_root, null, null).Domains.Single(d => d.Name == "lab"), Part("Password"));
        Assert.True(saved.Saved, saved.Notice);
        string file = File.ReadAllText(path);
        Assert.Contains("\"dpapi:", file);
        Assert.DoesNotContain(Part("Password"), file);
        return new DirectoryAccess(new WindowsDirectorySystem(), () => DirectoryConfigFile.LoadCatalog(_root, null, null), () => ["lab"], () => null, TimeProvider.System);
    }

    [SupportedOSPlatform("windows")]
    [LiveDirectoryFact]
    public async Task Directory_TheFiveQuestions()
    {
        var access = DirectoryFromScratchHome();
        string domain = await Call(new AdDomainTool(access));
        Assert.Contains("PDC emulator", domain);
        Assert.Contains("lockout: after ", domain);
        string bob = await Call(new AdUserTool(access), ("name", "bob"));
        Assert.StartsWith("User bob in ", bob);
        Assert.Contains(", nested)", bob);   // tokenGroups
        string staff = await Call(new AdGroupTool(access), ("name", "Staff"), ("nested", true));
        Assert.Contains("(user bob)", staff);   // through Engineers
        Assert.Contains(": disabled", await Call(new AdComputerTool(access), ("name", "WS01")));
        Assert.Contains("dave", await Call(new AdFindTool(access), ("kind", "stale_users")));
        Assert.Contains("alice", await Call(new AdFindTool(access), ("kind", "name"), ("words", "ali")));
        Assert.StartsWith("User carol in ", await Call(new AdUserTool(access), ("name", "carol")));
        var (ok, text) = await DirectoryAccess.TestAsync(new WindowsDirectorySystem(), access.Catalog().Domains.Single(), TimeProvider.System, CancellationToken.None);
        Assert.True(ok, text);
    }
}
