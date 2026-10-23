using System.Data.Common;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.ActiveDirectory;
using NeonSidekick.ActiveDirectory.Ldap;
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
/// and the container's password). On a Mac too since 2026-10-18 (the Mac AD plan's phase 3), through the managed LDAP client: the user is
/// <c>reader@LAB.NEON.TEST</c>, and the test realm's KDC is named by a <c>krb5.conf</c> that <c>KRB5_CONFIG</c> points at.</item>
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
    public LiveDirectoryFactAttribute() => Skip = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? LiveAdmin.Unavailable(LiveAdmin.DirectoryVariable, windowsOnly: false)
        : "There is no directory client here.";   // wldap32 on Windows, the managed client on a Mac (2026-10-18)
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

    /// <summary>The system the app builds here: wldap32 on Windows, the managed LDAP client on a Mac (2026-10-18).</summary>
    private static IDirectorySystem DirectorySystem() => OperatingSystem.IsWindows() ? new WindowsDirectorySystem() : new ManagedDirectorySystem();

    private DirectoryAccess DirectoryFromScratchHome(string? password = null)
    {
        var spec = new DbConnectionStringBuilder { ConnectionString = LiveAdmin.Read(LiveAdmin.DirectoryVariable)! };
        string Part(string key) => spec.TryGetValue(key, out object? value) ? (string)value : throw new InvalidOperationException(LiveAdmin.DirectoryVariable + " has no " + key + ".");
        var config = new DirectoryDomainConfig { Domain = Part("Domain"), Server = Part("Server"), Auth = "runas", User = Part("User"), Description = "the live gate's domain" };
        string path = DirectoryConfigFile.ProfilePath(_root);
        Assert.Null(DirectoryConfigFile.AddDomain(path, "lab", config));
        var saved = DirectoryConfigFile.SavePassword(DirectoryConfigFile.LoadCatalog(_root, null, null).Domains.Single(d => d.Name == "lab"), password ?? Part("Password"));
        Assert.True(saved.Saved, saved.Notice);
        string file = File.ReadAllText(path);
        Assert.Contains(OperatingSystem.IsWindows() ? "\"dpapi:" : "\"keychain:", file);
        Assert.DoesNotContain(password ?? Part("Password"), file);
        return new DirectoryAccess(DirectorySystem(), () => DirectoryConfigFile.LoadCatalog(_root, null, null), () => ["lab"], () => null, TimeProvider.System);
    }

    [LiveDirectoryFact]
    public async Task Directory_TheFiveQuestions()
    {
        var access = DirectoryFromScratchHome();
        string domain = await Call(new AdDomainTool(access));
        Assert.Contains("PDC emulator", domain);
        Assert.Contains("lockout: after ", domain);
        string bob = await Call(new AdUserTool(access), ("name", "bob"));
        Assert.StartsWith("User ⟪bob⟫ in ", bob);   // account names fenced (2026-10-23, the admin review's C9)
        Assert.Contains(", nested)", bob);   // tokenGroups
        string staff = await Call(new AdGroupTool(access), ("name", "Staff"), ("nested", true));
        Assert.Contains("(user ⟪bob⟫)", staff);   // through Engineers
        Assert.Contains(": disabled", await Call(new AdComputerTool(access), ("name", "WS01")));
        Assert.Contains("dave", await Call(new AdFindTool(access), ("kind", "stale_users")));
        Assert.Contains("alice", await Call(new AdFindTool(access), ("kind", "name"), ("words", "ali")));
        Assert.StartsWith("User ⟪carol⟫ in ", await Call(new AdUserTool(access), ("name", "carol")));
        var (ok, text) = await DirectoryAccess.TestAsync(DirectorySystem(), access.Catalog().Domains.Single(), TimeProvider.System, CancellationToken.None);
        Assert.True(ok, text);
        if (OperatingSystem.IsMacOS())
        {
            Assert.Contains(", Kerberos as ldap/", text);   // the managed client names how it connected: signed and sealed on 389
            Assert.EndsWith(", sealed.", text);
        }
    }

    /// <summary>
    /// A refused password, on both systems (2026-10-18, the Mac AD plan's hold): the first call reaches the DC and is refused, worded as wldap32's
    /// 0x31 (on a Mac the DC's KDC, asked with no password, knew the account; 2026-10-20, the code review); the second is held here and never
    /// reaches it. Costs the account one bad password, under any lockout threshold. On a Mac the probe asks port 88 of the address connected to:
    /// with the emulated Samba's KDC on 1088 that is macOS's own KDC, which knows no domain account, so nothing may be held there.
    /// </summary>
    [LiveDirectoryFact]
    public async Task Directory_ARefusedPassword_IsWorded_ThenHeld()
    {
        var access = DirectoryFromScratchHome("not-the-password-" + Guid.NewGuid().ToString("N"));
        var domain = access.Catalog().Domains.Single();
        string first = await Call(new AdDomainTool(access));
        if (OperatingSystem.IsMacOS() && first.Contains("does not know", StringComparison.Ordinal))
        {
            Assert.DoesNotContain("not tried again", await Call(new AdDomainTool(access)), StringComparison.Ordinal);
            return;
        }

        Assert.Contains("refused the sign-in as " + domain.Account, first);
        Assert.Equal(DirectoryText.Held(domain.DomainName, domain.Account), await Call(new AdDomainTool(access)));
    }

    /// <summary>
    /// The AD gaps plan's reads against Samba (2026-10-15), over the seed recorded in the <c>live-ad-tests-samba</c> memory (OU=Neon › Engineering
    /// and Sales; erin, frank, grace; Tier0 ⊂ Domain Admins; All-Hands with the contact Vendor Rep; Unused): Samba answers the walk's forward reads
    /// and DN batches, <c>primaryGroupID</c>, <c>directReports</c>, <c>canonicalName</c>, <c>showInAdvancedViewOnly</c> and GeneralizedTime filters,
    /// each search under the call's deadline, and a list saves as a CSV.
    /// </summary>
    [LiveDirectoryFact]
    public async Task Directory_SearchesBrowsesAndNests()
    {
        var access = DirectoryFromScratchHome();
        Func<DirectorySaveRoot> root = () => new DirectorySaveRoot(new Files.WorkingDirectory(() => _root, TimeProvider.System), null);
        var find = new AdFindTool(access, saveRoot: root);
        Assert.Contains("⟪Neon/Engineering⟫", await Call(find, ("kind", "name"), ("words", "eng"), ("type", "ou")));
        Assert.Contains("⟪Company-wide mail list⟫", await Call(find, ("kind", "name"), ("words", "mail list"), ("type", "group")));
        string staff = await Call(new AdGroupTool(access), ("name", "Staff"), ("nested", true), ("members", "users"));
        Assert.Contains("(user ⟪bob⟫) via ⟪Engineers⟫", staff);
        Assert.Contains("(user ⟪carol⟫) direct", staff);
        Assert.Contains(" by primary group)", await Call(new AdGroupTool(access), ("name", "Domain Users"), ("limit", 5)));
        Assert.Contains("⟪Vendor Rep⟫ (contact)", await Call(new AdGroupTool(access), ("name", "All-Hands")));
        string tree = await Call(new AdOuTool(access));
        Assert.Contains("⟪Neon/Engineering⟫", tree);
        Assert.DoesNotContain("⟪System⟫", tree);   // advanced view only
        Assert.Contains("⟪erin⟫  user  ⟪Erin Ng⟫", await Call(new AdOuTool(access), ("ou", "Neon/Engineering")));
        Assert.Contains("⟪erin⟫  ⟪Erin Ng⟫", await Call(find, ("kind", "password_never_expires")));
        Assert.Contains("⟪frank⟫  ⟪Frank Ode⟫  expires ", await Call(find, ("kind", "account_expiring")));
        Assert.Contains("⟪erin⟫  user  ⟪Erin Ng⟫  created ", await Call(find, ("kind", "created"), ("days", 3650), ("ou", "Neon")));
        Assert.Contains("⟪erin⟫  ⟪Erin Ng⟫  ⟪Engineering⟫", await Call(find, ("kind", "field"), ("field", "department"), ("words", "engin")));
        string privileged = await Call(find, ("kind", "privileged"));
        Assert.Contains("⟪grace⟫  user  ⟪Grace Hu⟫  ", privileged);
        Assert.Contains("via ⟪Tier0⟫", privileged);
        Assert.Contains("⟪Unused⟫", await Call(find, ("kind", "empty_groups")));
        Assert.Contains("direct reports (1): ⟪Frank Ode⟫", await Call(new AdUserTool(access), ("name", "erin")));
        Assert.Contains("manager: ⟪Erin Ng⟫", await Call(new AdUserTool(access), ("name", "frank")));
        Assert.Contains("⟪ShortPasswords⟫  " + DirectoryText.PolicyUnreadable, await Call(new AdDomainTool(access)));   // listed, its settings unreadable as reader
        string saved = await Call(new AdGroupTool(access, saveRoot: root), ("name", "Staff"), ("nested", true), ("to", "staff.csv"));
        Assert.Contains(DirectoryText.SavedPrefix, saved);
        Assert.StartsWith("name,kind,account,via,dn", File.ReadAllLines(Path.Combine(_root, "staff.csv"))[0]);
    }
}
