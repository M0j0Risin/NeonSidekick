using System.Net;
using NeonSidekick.ActiveDirectory;
using NeonSidekick.App;
using NeonSidekick.Computers;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Network;
using NeonSidekick.Plans;
using NeonSidekick.Services;
using NeonSidekick.Settings;
using NeonSidekick.Tasks;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// The admin families on the screen (2026-10-15, the admin-tools plan): a model's service change asked on the pane — yes runs it, no is its
/// answer —, a task's run the same way, what is offered without a pane and in plan mode, the bots' and scripts' redacted twins, and the user's
/// own <c>/services</c>, <c>/tasks</c>, <c>/ad</c> and <c>/net</c>.
/// </summary>
public partial class ChatScreenTests
{
    private FakePinger? _pinger;
    private FakeServiceSystem? _serviceSystem;
    private FakeTaskSystem? _taskSystem;
    private FakeDirectorySystem? _directorySystem;

    private const string ConfirmRowsOnScreen = "\n▸ No\n  Yes\n";

    /// <summary>The services fixture: four services on this PC, <c>local</c> readwrite over MyApp*, the switch and the writes on.</summary>
    private FakeServiceSystem ServicesFixture()
    {
        _serviceSystem = new FakeServiceSystem();
        _serviceSystem.Add("Spooler", "Print Spooler");
        _serviceSystem.Add("MyAppWorker", "My App Worker", image: @"C:\apps\worker.exe --password=hunter2");
        _serviceSystem.Add("MyAppWeb", "My App Web", state: ServiceRunState.Stopped, start: ServiceStartType.Manual);
        _serviceSystem.Add("RpcSs", "Remote Procedure Call (RPC)");
        string path = ServiceConfigFile.Family.ProfilePath(_settings.ProfileDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ \"computers\": { \"local\": { \"access\": \"readwrite\", \"control\": [\"MyApp*\"] } } }");
        _settings.Update(d => { d.TtsOutput = false; d.ServiceTools = true; d.ServiceWrites = true; d.ToolsDisabled = []; });
        return _serviceSystem;
    }

    /// <summary>
    /// A reply that is <paramref name="calls"/> (each a tool and its arguments), then <paramref name="reply"/>; each of <paramref name="answers"/>
    /// pushed at one showing of the yes/no pane, in turn.
    /// </summary>
    private void AdminTurnFixture(ConsoleKeyInfo[][] answers, string reply, params (string Tool, Dictionary<string, object?> Arguments)[] calls)
    {
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null);
        for (int i = 0; i < calls.Length; i++)
        {
            _chat.Enqueue(FakeChatClient.Call("c" + i, calls[i].Tool, calls[i].Arguments));
        }

        _chat.EnqueueText(reply);
        var input = Scripted();
        StepsWhenIdle(Line("fix the app"), Line("/exit"));
        var idle = input.OnWait!;
        var pending = new Queue<ConsoleKeyInfo[]>(answers);
        int asked = 0;
        input.OnWait = () =>
        {
            if (_keys is { PendingLine.IsCompleted: false })
            {
                int shown = CountOf(_console.Output, ConfirmRowsOnScreen);
                if (shown > asked && pending.Count > 0)
                {
                    asked = shown;
                    input.Push(pending.Dequeue());
                }

                return;
            }

            idle();
        };
    }

    [WindowsFact]
    public async Task ServiceControl_YesOnThePane_RestartsTheService()
    {
        var system = ServicesFixture();
        AdminTurnFixture([[Keys.Down, Keys.Enter]], "Restarted.", (ServiceControlTool.ToolName, new() { ["name"] = "MyAppWorker", ["action"] = "restart" }));

        string output = await RunAsync();

        Assert.Contains(ConfirmRowsOnScreen, output);
        Assert.Equal(["stop MyAppWorker", "start MyAppWorker"], system.Changes);
        var result = (string)Results(_chat.Requests[^1]).Single().Result!;
        Assert.Equal(ServiceText.Done("restart", "MyAppWorker", ComputerText.ThisPc, ServiceRunState.Running), result);
    }

    [WindowsFact]
    public async Task ServiceControl_NoOnThePane_IsTheModelsAnswer_AndAProtectedOneIsNeverAsked()
    {
        var system = ServicesFixture();
        AdminTurnFixture([[Keys.Enter]], "Understood.",
            (ServiceControlTool.ToolName, new() { ["name"] = "MyAppWeb", ["action"] = "start" }),
            (ServiceControlTool.ToolName, new() { ["name"] = "Spooler", ["action"] = "stop" }));

        await RunAsync();

        var results = Results(_chat.Requests[^1]).Select(r => (string)r.Result!).ToList();
        Assert.Equal(ComputerText.Declined, results[0]);
        Assert.Equal(ComputerText.NotInControl("Spooler", ComputerText.ThisPc, ServiceConfigFile.Info), results[1]);
        Assert.Empty(system.Changes);
    }

    [WindowsFact]
    public async Task TaskControl_YesOnThePane_RunsTheTask()
    {
        _taskSystem = new FakeTaskSystem();
        _taskSystem.Add(@"\Ops\Sync", FakeTaskSystem.Xml(@"C:\ops\sync.cmd"));
        string path = TaskConfigFile.Family.ProfilePath(_settings.ProfileDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ \"computers\": { \"local\": { \"access\": \"readwrite\", \"control\": [\"\\\\Ops\\\\*\"] } } }");
        _settings.Update(d => { d.TtsOutput = false; d.TaskTools = true; d.TaskWrites = true; });
        AdminTurnFixture([[Keys.Down, Keys.Enter]], "Ran it.", (TaskControlTool.ToolName, new() { ["path"] = @"\Ops\Sync", ["action"] = "run" }));

        string output = await RunAsync();

        Assert.Contains(@"C:\ops\sync.cmd", output);   // the question shows the command line whole
        Assert.Equal([@"run \Ops\Sync"], _taskSystem.Changes);
    }

    [Fact]
    public void Offered_TheChangesNeedAPane_AndAReadWriteComputer_AndPlanModeDropsThem()
    {
        var settings = new AppSettingsData { ServiceTools = true, ServiceWrites = true, TaskTools = true, TaskWrites = true };
        var system = new FakeServiceSystem();
        var readWrite = new ComputerCatalog<ServiceComputerConfig>([new Computer<ServiceComputerConfig>("local", new ServiceComputerConfig { Access = "readwrite", ControlActions = ["start"] })], [], ServiceConfigFile.Info);
        var access = new ServiceAccess(system, () => readWrite, () => null, _time);
        var tools = ChatScreen.ServiceTools(access, () => settings, (_, _) => Task.FromResult<bool?>(true));

        Assert.Equal([ServiceListTool.ToolName, ServiceInfoTool.ToolName, ServiceControlTool.ToolName], ChatScreen.ServiceToolsFor(tools, settings, pane: true, readWrite).Select(t => t.Name));   // start_type not allowed
        Assert.Equal(ToolsText.NoStartTypeAllowedReason, ChatScreen.ServiceWriteReason(ServiceStartTypeTool.ToolName, settings, true, readWrite));
        Assert.Equal(ToolsText.NoPaneReason, ChatScreen.ServiceWriteReason(ServiceControlTool.ToolName, settings, pane: false, readWrite));
        Assert.Equal(2, ChatScreen.ServiceToolsFor(tools, settings, pane: false, readWrite).Count);
        var readOnly = ServiceConfigFile.Family.LocalOnly;
        Assert.Equal(ToolsText.NoReadWriteComputerReason, ChatScreen.ServiceWriteReason(ServiceControlTool.ToolName, settings, true, readOnly));
        Assert.Equal(ToolsText.SwitchOffReason(SettingsField.ServiceWrites), ChatScreen.ServiceWriteReason(ServiceControlTool.ToolName, new AppSettingsData(), true, readWrite));
        Assert.Equal(ToolsText.NoPaneReason, ChatScreen.TaskWriteReason(settings, pane: false, TaskConfigFile.Family.LocalOnly));
        foreach (string write in (string[])[ServiceControlTool.ToolName, ServiceStartTypeTool.ToolName, TaskControlTool.ToolName])
        {
            Assert.Contains(write, PlanTools.Mutating);
        }

        foreach (string read in ChatScreen.ServiceToolNames.Concat(ChatScreen.TaskToolNames).Concat(ChatScreen.DirectoryToolNames).Concat(ChatScreen.NetworkToolNames).Except([ServiceControlTool.ToolName, ServiceStartTypeTool.ToolName, TaskControlTool.ToolName]))
        {
            Assert.Contains(read, PlanTools.ReadOnly);
        }
    }

    [Fact]
    public void Twins_TheBotsAndScriptsReadRedacted_AndNeverChange()
    {
        var access = new ServiceAccess(new FakeServiceSystem(), () => ServiceConfigFile.Family.LocalOnly, () => null, _time);
        var bots = ChatScreen.ServiceTools(access, () => new AppSettingsData(), null, forceRedacted: true);
        Assert.Equal([ServiceListTool.ToolName, ServiceInfoTool.ToolName], bots.Select(t => t.Name));
        Assert.All(bots, t => Assert.True(((ServiceTool)t).ForceRedacted));
        var main = ChatScreen.ServiceTools(access, () => new AppSettingsData(), null);
        Assert.All(main.Select(RedactedTwins.Of).OfType<ServiceListTool>(), t => Assert.True(t.ForceRedacted));
        var directory = new DirectoryAccess(new FakeDirectorySystem(), () => new DirectoryCatalog([], []), () => null, () => null, _time);
        Assert.All(ChatScreen.DirectoryTools(directory).Select(RedactedTwins.Of), t => Assert.True(((DirectoryTool)t).ForceRedacted));
        var tasks = new TaskAccess(new FakeTaskSystem(), () => TaskConfigFile.Family.LocalOnly, () => null, _time);
        Assert.DoesNotContain(ChatScreen.TaskTools(tasks, () => new AppSettingsData(), null, forceRedacted: true), t => t is TaskControlTool);
    }

    [WindowsFact]
    public async Task Services_WithoutThePane_ListsTheServices_AndAChangeIsYourOwnHand()
    {
        ServicesFixture();
        _settings.Update(d => d.ServiceWrites = false);
        PushLine("/services state:running");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains("Services on " + ComputerText.ThisPc, output);
        Assert.Contains("MyAppWorker", output);
        Assert.DoesNotContain("MyAppWeb ", output);   // stopped
        Assert.Empty(_chat.Requests);
    }

    [Fact]
    public async Task Ad_TheUsersCard_WithoutThePane()
    {
        _directorySystem = new FakeDirectorySystem();
        _directorySystem.Add("DC=lab,DC=example", ("maxPwdAge", "-36288000000000"));
        _directorySystem.Add("CN=Alice Smith,OU=Staff,DC=lab,DC=example", ("objectClass", "user"), ("sAMAccountName", "alice"), ("displayName", "Alice Smith"), ("userAccountControl", "512"));
        string path = DirectoryConfigFile.ProfilePath(_settings.ProfileDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ \"domains\": { \"lab\": { \"domain\": \"lab.example\", \"server\": \"localhost\" } } }");
        _settings.Update(d => { d.TtsOutput = false; d.DirectoryDomainsOffered = ["lab"]; });
        PushLine("/ad user alice");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains("User alice in lab.example: enabled", output);
        Assert.Empty(_chat.Requests);
    }

    [Fact]
    public async Task Net_PingIsYourOwnHand_TheModelsRefusalSaidDimly()
    {
        _pinger = new FakePinger();
        _settings.Update(d => { d.TtsOutput = false; d.NetworkToolsReach = "internet"; });
        PushLine("/net ping 192.168.1.20 1");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains("Ping 192.168.1.20 (192.168.1.20): 1 sent, 1 received, 0% loss", output);
        Assert.Contains("(the model's net_ tools would be refused here:", output);
        Assert.Equal([(IPAddress.Parse("192.168.1.20"), 128)], _pinger.Sent.Select(s => (s.Address, s.Ttl)));   // one echo, Windows' own TTL
    }
}
