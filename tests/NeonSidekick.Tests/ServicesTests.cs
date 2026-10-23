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

    private async Task<string> Change(string name, string action, ServiceStartChoice? choice = null, bool writesOn = true, bool pane = true, ServiceComputerConfig? local = null,
        Func<string, CancellationToken, Task<bool?>>? confirm = null, CancellationToken cancellationToken = default)
    {
        var access = Access(local: local);
        var (source, error) = access.Computer(null, forceRedacted: false);
        Assert.Null(error);
        return await ServiceChanges.RunAsync(access, source!, name, action, choice, writesOn, byUser: false, pane ? confirm ?? Confirm : null, cancellationToken);
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

    /// <summary>
    /// A name no service has is refused before every other check but the switch (2026-10-20, a review: "TermService\0x" passed the guard's
    /// list, which compares whole names, and Windows opened TermService by the name up to the NUL).
    /// </summary>
    [Fact]
    public async Task Change_ANameHoldingANul_IsRefused_BeforeWindowsReadsIt()
    {
        _system.Add("TermService", "Remote Desktop Services");
        var everything = new ServiceComputerConfig { Access = "readwrite", Control = ["*"] };
        Assert.Equal(ServiceText.BadName("TermService\0x"), await Change("TermService\0x", "stop", local: everything));
        Assert.Equal(ServiceText.BadName("Spooler\0"), await Change("Spooler\0", "stop", local: everything));
        Assert.Empty(_asked);
        Assert.Empty(_system.Changes);
        Assert.Equal(ServiceRunState.Running, _system.Services["TermService"].Entry.State);
        Assert.Equal(ServiceText.ProtectedByApp("TermService"), await Change("TermService", "stop", local: everything));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a/b")]
    [InlineData(@"a\b")]
    [InlineData("Spooler\tx")]
    public void NameProblem_RefusesWhatNoServiceNameHolds(string name) => Assert.Equal(ServiceText.BadName(name), ServiceAccess.NameProblem(name));

    [Fact]
    public async Task NameProblem_PassesAName_RefusesALongOne_AndTheInfoToolAsks()
    {
        Assert.Null(ServiceAccess.NameProblem("MSSQL$EXPRESS"));
        Assert.NotNull(ServiceAccess.NameProblem(new string('a', ServiceAccess.MaxNameLength + 1)));
        Assert.Equal(ServiceText.BadName("Spooler\0"), await Call(new ServiceInfoTool(Access(), () => _settings), ("name", "Spooler\0")));
        Assert.DoesNotContain('\0', ServiceText.BadName("Spooler\0"));   // scrubbed (a string "\0" is ignorable to a culture's comparison, so the char)
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
        // A question nobody answered is not the user's no (2026-10-23, the admin review's G15).
        Assert.Equal(ComputerText.NoPaneToAsk, await Change("MyAppWeb", "start"));
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

    /// <summary>
    /// A restart whose stop does not end, or whose wait is stopped, is not started (2026-10-20, a review: StartService on a service still
    /// stopping failed with 1056 and left it down).
    /// </summary>
    [Fact]
    public async Task Change_ARestartWhoseStopDoesNotEnd_IsNotStarted()
    {
        _system.StopsSlowly = true;
        using var stop = new CancellationTokenSource();
        Task<bool?> YesThenEsc(string question, CancellationToken cancellationToken)
        {
            // The user's Esc while the stop is waited for: a moment after the yes, once the change has begun (2026-10-23, the admin review's
            // B2: an Esc already pressed when the change would begin now stops it before anything is done).
            stop.CancelAfter(TimeSpan.FromMilliseconds(300));
            return Task.FromResult<bool?>(true);
        }

        string answer = await Change("MyAppWorker", "restart", confirm: YesThenEsc, cancellationToken: stop.Token);
        // The reason named (2026-10-23, the admin review's T14): the user stopped the wait, not "did not stop in time".
        Assert.Equal(ServiceText.RestartNotStarted("MyAppWorker", ComputerText.ThisPc, ServiceRunState.StopPending, RestartStop.Cancelled), answer);
        Assert.Equal(["stop MyAppWorker"], _system.Changes);
    }

    /// <summary>A start type set whose delayed flag was refused is said to be in part (2026-10-20, a review: it read as a plain failure).</summary>
    [Fact]
    public async Task Change_AStartTypeMadeInPart_SaysWhatTookEffect()
    {
        _system.DelayedFailsWith = 87;
        string answer = await Change("MyAppWeb", "start_type", ServiceStartChoice.Delayed);
        Assert.StartsWith("Error: MyAppWeb on " + ComputerText.ThisPc + " now has automatic start, but the rest of the change failed. Error: ", answer);
        Assert.Equal(ServiceStartType.Automatic, _system.Services["MyAppWeb"].Config.StartType);
    }

    /// <summary>A change made whose status could not be read after is said so, never "failed" (2026-10-20, a review).</summary>
    [Fact]
    public async Task Change_MadeButNotReadBack_IsDoneUnread()
    {
        _system.StatusFailsAfterChange = true;
        Assert.Equal(ServiceText.DoneUnread("start", "MyAppWeb", ComputerText.ThisPc), await Change("MyAppWeb", "start"));
        Assert.Equal(["start MyAppWeb"], _system.Changes);
    }

    /// <summary>
    /// The related services a narrowed <c>services</c> list hides are counted, never named (2026-10-20, a review), in a refused stop and in
    /// <c>service_info</c>.
    /// </summary>
    [Fact]
    public async Task Change_AndInfo_NameOnlyTheVisibleRelatedServices()
    {
        var narrow = new ServiceComputerConfig { Access = "readwrite", Services = ["Spooler"], Control = ["*"] };
        string refused = await Change("Spooler", "stop", local: narrow);
        Assert.Equal(ServiceText.DependentsRunning("Spooler", [], 1), refused);
        Assert.DoesNotContain("MyAppWorker", refused);
        Assert.Contains("1 service not shown here", refused);

        _system.Services["Spooler"].Config = _system.Services["Spooler"].Config with { Dependencies = ["RpcSs", "Spooler2"] };
        string info = await Call(new ServiceInfoTool(Access(local: narrow), () => _settings), ("name", "Spooler"));
        Assert.Contains("depends on: 2 services not shown here", info);
        Assert.Contains("needed by: 1 service not shown here", info);
        Assert.DoesNotContain("MyAppWorker", info);
        Assert.Contains("needed by: ⟪MyAppWorker⟫ (running)", await Call(new ServiceInfoTool(Access(), () => _settings), ("name", "Spooler")));
    }

    /// <summary>
    /// The question puts each value on one line under its title row (2026-10-20, a review: a newline or a run of spaces hid a program line's
    /// tail), and a model's change whose program line is too long to show is refused.
    /// </summary>
    [Fact]
    public async Task Change_TheQuestion_ShowsEachValueOnOneLine_AndRefusesOneTooLongToShow()
    {
        _system.Add("MyAppOdd", "My\nApp   Odd", state: ServiceRunState.Stopped, image: "worker.exe" + new string(' ', 300) + "& net user x /add");
        await Change("MyAppOdd", "start");
        string question = Assert.Single(_asked);
        Assert.Equal("Start MyAppOdd on " + ComputerText.ThisPc + "?", question[..question.IndexOf('\n', StringComparison.Ordinal)]);
        Assert.Contains("My App Odd, runs as ", question);
        Assert.Contains("It runs worker.exe & net user x /add", question);
        Assert.Single(question, c => c == '\n');

        _asked.Clear();
        _system.Add("MyAppLong", "Long", state: ServiceRunState.Stopped, image: new string('a', ComputerText.MaxShownInQuestion + 1));
        Assert.Equal(ServiceText.TooLongToAsk("MyAppLong", ComputerText.MaxShownInQuestion + 1), await Change("MyAppLong", "start"));
        Assert.Empty(_asked);
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
        // Every filter and the limit restated (2026-10-23, the admin review's E4).
        Assert.Contains("\n2 more services; narrow with filter or state, or next: service_list(offset 5, state running, limit 5, computer app01)", text);
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
        Assert.Contains("⟪MyAppWeb⟫  ⟪My App Web⟫  stopped exit service error 0", list);   // the key name fenced since 2026-10-23 (B11)
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

    /// <summary>
    /// A turn's <c>service_control</c> holds its actions from the turn's catalog (2026-10-20, a review: each request's read of the description
    /// and schema read both files again): reading them reads no catalog.
    /// </summary>
    [Fact]
    public void ControlTool_ForATurn_ReadsNoCatalog()
    {
        int loads = 0;
        var catalog = new ComputerCatalog<ServiceComputerConfig>([new Computer<ServiceComputerConfig>("local", new ServiceComputerConfig { Access = "readwrite", Control = ["*"], ControlActions = ["restart"] })], [], ServiceConfigFile.Info);
        var access = new ServiceAccess(_system, () => { loads++; return catalog; }, () => ["local"], _time);
        var tools = NeonSidekick.App.ChatScreen.ServiceToolsFor([new ServiceControlTool(access, () => _settings, Confirm)], _settings, pane: true, access.Catalog());
        var turn = Assert.IsType<ServiceControlTool>(Assert.Single(tools));
        loads = 0;
        for (int i = 0; i < 3; i++)
        {
            Assert.Contains("runs: restart.", turn.Description);
            Assert.Contains("\"enum\": [\"restart\"]", turn.JsonSchema.ToString());
        }

        Assert.Equal(0, loads);
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
