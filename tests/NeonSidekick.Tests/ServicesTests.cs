using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Computers;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Services;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// The services family's portable parts (2026-10-15, the admin-tools plan) over <see cref="FakeServiceSystem"/>: a computer's entry and its
/// problems, the one order of write checks (a test per step), the change's flow (approved, declined, nobody to ask, running dependents, the
/// protected lists, a driver, the start type), what the model reads (fenced, redacted) and the four tools' arguments.
/// </summary>
public sealed class ServicesTests : IDisposable
{
    private readonly ManualTimeProvider _time = new();
    private readonly FakeServiceSystem _system = new();
    private readonly AppSettingsData _settings = new() { ServiceTools = true, ServiceWrites = true };
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", "services-" + Guid.NewGuid().ToString("N"));
    private ServiceComputerConfig _local = new() { Access = "readwrite", Control = ["Spooler", "MyApp*", "RpcSs", "MSSQL*", "Drv"], Protected = ["MSSQL*"] };
    private readonly List<string> _asked = [];
    private bool? _answer = true;

    public ServicesTests()
    {
        _system.Add("Spooler", "Print Spooler", image: @"C:\Windows\System32\spoolsv.exe");
        _system.Add("MyAppWorker", "My App Worker", image: @"C:\apps\worker.exe --password=hunter2 --port 80", description: "Runs ⟪the⟫ jobs");
        _system.Add("MyAppWeb", "My App Web", state: ServiceRunState.Stopped, start: ServiceStartType.Manual, exit: 1066);
        _system.Add("RpcSs", "Remote Procedure Call (RPC)");
        _system.Add("MSSQLSERVER", "SQL Server (MSSQLSERVER)");
        _system.Add("Drv", "A driver", type: ServiceKinds.KernelDriver);
        _system.Services["Spooler"].DependsOnMe.Add("MyAppWorker");
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private ServiceAccess Access(IServiceSystem? system = null, ServiceComputerConfig? local = null, params Computer<ServiceComputerConfig>[] more) =>
        new(system ?? _system, () => new ComputerCatalog<ServiceComputerConfig>([new Computer<ServiceComputerConfig>("local", local ?? _local), .. more], [], ServiceConfigFile.Info),
            () => ["local", .. more.Select(c => c.Name)], _time);

    private Task<bool?> Confirm(string question, CancellationToken cancellationToken)
    {
        _asked.Add(question);
        return Task.FromResult(_answer);
    }

    private async Task<string> Change(string name, string action, ServiceStartChoice? choice = null, bool writesOn = true, bool pane = true)
    {
        var access = Access();
        var (source, error) = access.Computer(null, forceRedacted: false);
        Assert.Null(error);
        return await ServiceChanges.RunAsync(access, source!, name, action, choice, writesOn, byUser: false, pane ? Confirm : null, CancellationToken.None);
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

    // ── The entry ──────────────────────────────────────────────────────────

    [Fact]
    public void Problem_LocalTakesNoHost_ARemoteNeedsOne_AndTheWordsAreChecked()
    {
        var info = ServiceConfigFile.Info;
        Assert.Equal(ComputerText.LocalTakesNoHost, new ServiceComputerConfig { Host = "pc" }.Problem("local", info));
        Assert.Null(new ServiceComputerConfig { Access = "readwrite", Control = ["*"] }.Problem("local", info));
        Assert.Equal(ComputerText.NoHost, new ServiceComputerConfig().Problem("app01", info));
        Assert.Equal(ComputerText.BadAction("reboot", info), new ServiceComputerConfig { Host = "app01", ControlActions = ["start", "reboot"] }.Problem("app01", info));
        Assert.Equal(ComputerText.BadDetail("some"), new ServiceComputerConfig { Host = "app01", Detail = "some" }.Problem("app01", info));
        Assert.Equal(ComputerText.RunAsNoUser, new ServiceComputerConfig { Host = "app01", Auth = "runas" }.Problem("app01", info));
        Assert.Equal(ComputerText.CredmanWithWindows, new ServiceComputerConfig { Host = "app01", PasswordStore = "credman" }.Problem("app01", info));
        Assert.Null(new ServiceComputerConfig { Host = "app01", Auth = "runas", User = "CORP\\ops", PasswordStore = "credman" }.Problem("app01", info));
    }

    [Fact]
    public void ControlActions_AbsentIsAll_EmptyIsNone()
    {
        Assert.Equal(ServiceConfigFile.ActionWords, new ServiceComputerConfig().AllowedActions(ServiceConfigFile.Info));
        Assert.Empty(new ServiceComputerConfig { ControlActions = [] }.AllowedActions(ServiceConfigFile.Info));
        Assert.Equal(["start", "restart"], new ServiceComputerConfig { ControlActions = ["RESTART", "start"] }.AllowedActions(ServiceConfigFile.Info));
        Assert.False(new ServiceComputerConfig().InControl("Spooler", NameGlob.NoSeparator));   // absent control: nothing
    }

    [Fact]
    public void EmptyText_ItsExamples_AreUsableEntriesWhenUncommented()
    {
        var lines = ServiceConfigFile.EmptyText.Split('\n');
        var entries = new List<string>();
        foreach (string line in lines)
        {
            string body = line.TrimStart();
            if (!body.StartsWith("// ", StringComparison.Ordinal))
            {
                continue;
            }

            string code = body[3..];
            if (code.StartsWith('"') && code.Contains("\": {", StringComparison.Ordinal))
            {
                entries.Add(code);
            }
            else if (code.StartsWith("  \"", StringComparison.Ordinal) && entries.Count > 0 && !entries[^1].TrimEnd().EndsWith("},", StringComparison.Ordinal))
            {
                entries[^1] += code;
            }
        }

        Assert.Equal(3, entries.Count);
        Directory.CreateDirectory(_folder);
        string path = Path.Combine(_folder, ServiceConfigFile.FileName);
        File.WriteAllText(path, "{ \"computers\": { " + string.Join(" ", entries).TrimEnd().TrimEnd(',') + " } }");
        var (computers, problems) = ServiceConfigFile.Family.Load(path);
        Assert.Empty(problems);
        Assert.Equal(["local", "app01", "app02"], computers.Select(c => c.Name));
        Assert.Equal(["MSSQL*"], computers[1].Config.Protected);
        using var _ = JsonDocument.Parse(ServiceConfigFile.EmptyText, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
    }

    [Fact]
    public void Computer_ARunAsWithoutItsPassword_CannotSignIn()
    {
        var app02 = new Computer<ServiceComputerConfig>("app02", new ServiceComputerConfig { Host = "app02", Auth = "runas", User = "CORP\\ops" }, Path.Combine(_folder, "services.json"));
        var (source, error) = Access(more: app02).Computer("app02", forceRedacted: false);
        Assert.Null(source);
        Assert.Equal(ComputerText.CannotSignIn("app02", ComputerText.NoPassword("app02", ServiceConfigFile.Info)), error);
        Assert.StartsWith("Error: ", Access().Computer("nope", false).Error);
        Assert.True(Access().Computer(null, forceRedacted: true).Source!.Redacted);
        Assert.False(Access(local: new ServiceComputerConfig { Detail = "full" }).Computer(null, false).Source!.Redacted);
    }

    // ── The order of checks ────────────────────────────────────────────────

    private static ServiceConfiguration Config(string name, uint type = ServiceKinds.Win32OwnProcess) =>
        new(name, name, null, type, ServiceStartType.Automatic, false, "x.exe", "LocalSystem", null, [], null);

    [Fact]
    public void WriteRefusal_EachStepInItsOrder()
    {
        var local = new Computer<ServiceComputerConfig>("local", _local);
        Assert.Equal(ServiceText.WritesOff, ServiceAccess.WriteRefusal(local, "Spooler", "stop", null, writesOn: false));
        var read = new Computer<ServiceComputerConfig>("local", new ServiceComputerConfig { Control = ["*"] });
        Assert.Equal(ComputerText.ReadOnlyComputer(ComputerText.ThisPc, ServiceConfigFile.Info), ServiceAccess.WriteRefusal(read, "Spooler", "stop", null, true));
        var narrow = new Computer<ServiceComputerConfig>("local", new ServiceComputerConfig { Access = "readwrite", Services = ["My*"], Control = ["*"] });
        Assert.Equal(ComputerText.NotVisible("Spooler", ComputerText.ThisPc, ServiceConfigFile.Info), ServiceAccess.WriteRefusal(narrow, "Spooler", "stop", null, true));
        Assert.Equal(ComputerText.NotInControl("Other", ComputerText.ThisPc, ServiceConfigFile.Info), ServiceAccess.WriteRefusal(local, "Other", "stop", null, true));
        Assert.Equal(ServiceText.Driver("Drv"), ServiceAccess.WriteRefusal(local, "Drv", "start", Config("Drv", ServiceKinds.KernelDriver), true));
        Assert.Equal(ServiceText.ProtectedByApp("RpcSs"), ServiceAccess.WriteRefusal(local, "RpcSs", "restart", null, true));
        Assert.Null(ServiceAccess.WriteRefusal(local, "RpcSs", "start", null, true));   // starting a protected one is allowed
        Assert.Equal(ServiceText.ProtectedByFile("MSSQLSERVER"), ServiceAccess.WriteRefusal(local, "MSSQLSERVER", "start_type", null, true));
        var startOnly = new Computer<ServiceComputerConfig>("local", new ServiceComputerConfig { Access = "readwrite", Control = ["*"], ControlActions = ["start"] });
        Assert.Equal(ComputerText.ActionNotAllowed(["start"], ServiceConfigFile.Info), ServiceAccess.WriteRefusal(startOnly, "Spooler", "stop", null, true));
        Assert.Null(ServiceAccess.WriteRefusal(local, "Spooler", "stop", Config("Spooler"), true));
    }

    [Fact]
    public void Guard_TheAppsListCannotBeUndone_TheFilesAdds()
    {
        Assert.Equal(ServiceProtection.App, ServiceGuard.Protection("rpcss", new ServiceComputerConfig()));
        Assert.Equal(ServiceProtection.App, ServiceGuard.Protection("EventLog", new ServiceComputerConfig { Protected = [] }));
        Assert.Equal(ServiceProtection.File, ServiceGuard.Protection("MSSQL$EXPRESS", new ServiceComputerConfig { Protected = ["MSSQL*"] }));
        Assert.Equal(ServiceProtection.None, ServiceGuard.Protection("Spooler", new ServiceComputerConfig()));
        Assert.Equal(ServiceGuard.Protected.Count, ServiceGuard.Protected.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    // ── A change ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Change_Approved_AsksWithTheImagePath_ThenWaitsTheStateOut()
    {
        string answer = await Change("MyAppWeb", "start");

        Assert.Equal(ServiceText.Done("start", "MyAppWeb", ComputerText.ThisPc, ServiceRunState.Running), answer);
        Assert.Equal(["start MyAppWeb"], _system.Changes);
        Assert.Single(_asked);
        Assert.Contains("MyAppWeb", _asked[0]);
        Assert.Equal(ServiceRunState.Running, _system.Services["MyAppWeb"].Entry.State);
    }

    [Fact]
    public async Task Change_Declined_OrNobodyToAsk_ChangesNothing()
    {
        _answer = false;
        Assert.Equal(ComputerText.Declined, await Change("MyAppWeb", "start"));
        _answer = null;
        Assert.Equal(ComputerText.Declined, await Change("MyAppWeb", "start"));
        Assert.Equal(ComputerText.NoPaneToAsk, await Change("MyAppWeb", "start", pane: false));
        Assert.Empty(_system.Changes);
    }

    [Fact]
    public async Task Change_AStopWithRunningDependents_NamesThem_AndAsksNothing()
    {
        Assert.Equal(ServiceText.DependentsRunning("Spooler", ["MyAppWorker"]), await Change("Spooler", "stop"));
        Assert.Empty(_asked);
        Assert.Empty(_system.Changes);
        _system.Services["MyAppWorker"].Entry = _system.Services["MyAppWorker"].Entry with { State = ServiceRunState.Stopped };
        Assert.Equal(ServiceText.Done("stop", "Spooler", ComputerText.ThisPc, ServiceRunState.Stopped), await Change("Spooler", "stop"));
    }

    [Fact]
    public async Task Change_ProtectedDriverAndOffRefuseBeforeTheQuestion()
    {
        Assert.Equal(ServiceText.ProtectedByApp("RpcSs"), await Change("RpcSs", "restart"));
        Assert.Equal(ServiceText.ProtectedByFile("MSSQLSERVER"), await Change("MSSQLSERVER", "stop"));
        Assert.Equal(ServiceText.Driver("Drv"), await Change("Drv", "start"));
        Assert.Equal(ServiceText.WritesOff, await Change("Spooler", "start", writesOn: false));
        Assert.Empty(_asked);
        Assert.Empty(_system.Changes);
    }

    [Fact]
    public async Task Change_TheStartType_AndARestart()
    {
        Assert.Equal(ServiceText.StartTypeDone("MyAppWeb", ComputerText.ThisPc, ServiceText.ChoiceWord(ServiceStartChoice.Delayed)), await Change("MyAppWeb", "start_type", ServiceStartChoice.Delayed));
        Assert.True(_system.Services["MyAppWeb"].Config.DelayedAutoStart);
        Assert.Equal(ServiceText.Done("restart", "MyAppWorker", ComputerText.ThisPc, ServiceRunState.Running), await Change("MyAppWorker", "restart"));
        Assert.Equal(["start_type MyAppWeb Delayed", "stop MyAppWorker", "start MyAppWorker"], _system.Changes);
    }

    [Fact]
    public async Task Change_ARefusalByTheSystem_IsItsSentence()
    {
        _system.ChangeFailsWith = 5;
        string answer = await Change("MyAppWeb", "start");
        Assert.StartsWith("Error: ", answer);
        Assert.Empty(_system.Changes);
        var none = new ServiceAccess(null, () => ServiceConfigFile.Family.LocalOnly, () => null, _time);
        Assert.Equal(ServiceText.NoneHere, (await none.ReadAsync(none.Computer(null, false).Source!, null, s => s.List(false), CancellationToken.None)).Error);
    }

    // ── What the model reads ───────────────────────────────────────────────

    [Fact]
    public void Detail_FencesWhatOthersWrote_AndRedactsTheImageUnderRedacted()
    {
        var s = _system.Services["MyAppWorker"];
        var detail = new ServiceDetail(s.Config, new ServiceStatus(ServiceRunState.Running, 1234, 0, 0, 0, 0, 1), []);

        string redacted = ServiceRender.Detail("this PC", detail, redacted: true);
        Assert.Contains("image: ⟪C:\\apps\\worker.exe --password=" + ToolRedaction.Placeholder + " --port 80⟫", redacted);
        Assert.Contains("description: ⟪Runs ‹‹the›› jobs⟫", redacted);
        Assert.Contains("secret-like values hidden as " + ToolRedaction.Placeholder, redacted);
        Assert.EndsWith(UntrustedText.QuoteNote, redacted);
        Assert.DoesNotContain("hunter2", redacted);
        Assert.Contains("hunter2", ServiceRender.Detail("this PC", detail, redacted: false));
        Assert.Contains("needed by: " + ServiceText.DependentsUnknown, ServiceRender.Detail("this PC", detail with { DependentsKnown = false }, redacted: true));
    }

    [Fact]
    public void List_PagesAndSaysTheNextCall()
    {
        var entries = Enumerable.Range(0, 7).Select(i => new ServiceEntry("Svc" + i, "Service " + i, ServiceRunState.Running, 10 + i, ServiceKinds.Win32OwnProcess, 0, 0)).ToList();
        string text = ServiceRender.List("app01", entries, 9, 0, 5, new ServiceFilter("running"), "app01");
        Assert.StartsWith("Services on app01: 5 of 7 matching (9 in all), state running", text);
        Assert.Contains("\n2 more services; pass filter or state, or next: service_list(offset 5, computer app01)", text);
        Assert.True(ServiceRender.Failed(new ServiceEntry("x", "x", ServiceRunState.Stopped, 0, 0x10, 1066, 3)));
        Assert.False(ServiceRender.Failed(new ServiceEntry("x", "x", ServiceRunState.Stopped, 0, 0x10, 1077, 0)));
        Assert.Equal("service error 3", ServiceRender.ExitCode(1066, 3));
    }

    // ── The tools ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ListTool_FiltersAndReadsTheVisibleOnly()
    {
        var narrow = Access(local: new ServiceComputerConfig { Services = ["MyApp*", "Drv"] });
        string list = await Call(new ServiceListTool(narrow, () => _settings));
        Assert.StartsWith("Services on this PC: 2 of 2 matching (2 in all)", list);
        Assert.Contains("MyAppWeb  ⟪My App Web⟫  stopped exit service error 0", list);
        Assert.Contains("1 of", await Call(new ServiceListTool(narrow, () => _settings), ("drivers", true), ("filter", "drv")));
        Assert.Contains("MyAppWeb", await Call(new ServiceListTool(Access(), () => _settings), ("state", "failed")));
        Assert.Contains("MyAppWeb", await Call(new ServiceListTool(Access(), () => _settings), ("start", "manual")));
        Assert.Equal(ServiceText.BadState("paused"), await Call(new ServiceListTool(Access(), () => _settings), ("state", "paused")));
        Assert.Equal(ServiceText.OutOfRange("limit", "501", 1, 500), await Call(new ServiceListTool(Access(), () => _settings), ("limit", 501)));
    }

    [Fact]
    public async Task InfoTool_RefusesAHiddenService_AndTheTwinReadsRedacted()
    {
        var full = Access(local: new ServiceComputerConfig { Detail = "full", Services = ["MyApp*"] });
        Assert.Equal(ComputerText.NotVisible("Spooler", ComputerText.ThisPc, ServiceConfigFile.Info), await Call(new ServiceInfoTool(full, () => _settings), ("name", "Spooler")));
        var tool = new ServiceInfoTool(full, () => _settings);
        Assert.Contains("hunter2", await Call(tool, ("name", "MyAppWorker")));
        Assert.DoesNotContain("hunter2", await Call(tool.AsRedacted(), ("name", "MyAppWorker")));
        var twin = tool.AsRedacted();
        Assert.Same(twin, ((ServiceTool)twin).AsRedacted());
        Assert.Equal(ServiceText.Missing("name"), await Call(tool));
    }

    [Fact]
    public async Task ControlTools_CheckTheirWords_AndGoThroughTheOrder()
    {
        var control = new ServiceControlTool(Access(), () => _settings, Confirm);
        Assert.Equal(ServiceText.BadAction("pause"), await Call(control, ("name", "Spooler"), ("action", "pause")));
        Assert.Equal(ServiceText.Missing("action"), await Call(control, ("name", "Spooler")));
        Assert.Equal(ServiceText.ProtectedByApp("RpcSs"), await Call(control, ("name", "RpcSs"), ("action", "stop")));
        _settings.ServiceWrites = false;
        Assert.Equal(ServiceText.WritesOff, await Call(control, ("name", "MyAppWeb"), ("action", "start")));
        _settings.ServiceWrites = true;
        var startType = new ServiceStartTypeTool(Access(), () => _settings, null);
        Assert.Equal(ServiceText.BadStartChoice("sometimes"), await Call(startType, ("name", "MyAppWeb"), ("start_type", "sometimes")));
        Assert.Equal(ComputerText.NoPaneToAsk, await Call(startType, ("name", "MyAppWeb"), ("start_type", "manual")));
        Assert.Same(control, control.AsRedacted());
        Assert.Equal(ServiceStartChoice.Delayed, ServiceChanges.Choice("Delayed-Auto"));
        Assert.Null(ServiceChanges.Choice("boot"));
    }

    [Fact]
    public void ControlTool_ItsEnumIsWhatEveryReadWriteComputerAllows()
    {
        var restartOnly = new Computer<ServiceComputerConfig>("app01", new ServiceComputerConfig { Host = "app01", Access = "readwrite", ControlActions = ["restart", "start_type"] });
        var tool = new ServiceControlTool(Access(local: new ServiceComputerConfig(), more: restartOnly), () => _settings, Confirm);
        Assert.Equal(["restart"], tool.AgreedActions());
        Assert.Contains("\"enum\": [\"restart\"]", tool.JsonSchema.ToString());
        var both = new ServiceControlTool(Access(local: new ServiceComputerConfig { Access = "readwrite" }, more: restartOnly), () => _settings, Confirm);
        Assert.Equal(ServiceActions.Control, both.AgreedActions());   // they disagree: all three, each call checked
    }

    [Fact]
    public async Task TestAsync_ListsUnderTheAccount()
    {
        var (ok, text) = await ServiceAccess.TestAsync(_system, new Computer<ServiceComputerConfig>("local", new ServiceComputerConfig()), _time, CancellationToken.None);
        Assert.True(ok);
        Assert.Equal(ServiceText.TestOk("local", 5, 4), text);
        _system.FailWith = 1722;
        Assert.False((await ServiceAccess.TestAsync(_system, new Computer<ServiceComputerConfig>("local", new ServiceComputerConfig()), _time, CancellationToken.None)).Ok);
    }
}
