using Microsoft.Extensions.AI;
using NeonSidekick.Computers;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Services;
using NeonSidekick.Settings;
using NeonSidekick.Tasks;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>Phase 6 of the admin review's fixes (2026-10-23): the tasks' list, changes and words, the two panes' readings, the commands' quoting.</summary>
public sealed partial class AdminFixesTests
{
    private static FakeTaskSystem TaskSystem()
    {
        var system = new FakeTaskSystem();
        system.Add(@"\Backup\Nightly", FakeTaskSystem.Xml(@"C:\b.exe"));
        system.Add(@"\Backup\Weekly", FakeTaskSystem.Xml(@"C:\w.exe"));
        system.Add(@"\Microsoft\Windows\Defrag\ScheduledDefrag", FakeTaskSystem.Xml("defrag.exe", user: "S-1-5-18"));
        system.Add(@"\Microsoft\Windows\Other\Thing", FakeTaskSystem.Xml("thing.exe"));
        return system;
    }

    private TaskAccess TaskDoor(FakeTaskSystem system, TaskComputerConfig? local = null) =>
        new(system, () => new ComputerCatalog<TaskComputerConfig>([new Computer<TaskComputerConfig>("local", local ?? new TaskComputerConfig { Access = "readwrite", Control = [@"\Backup\*"] })], [], TaskConfigFile.Info), () => ["local"], _time);

    private static async Task<string> CallTool(AIFunction tool, params (string Name, object? Value)[] arguments)
    {
        var args = new AIFunctionArguments();
        foreach (var (name, value) in arguments)
        {
            args[name] = value;
        }

        return (string)(await tool.InvokeAsync(args))!;
    }

    /// <summary>B4, I10: \Microsoft\ is not walked unless shown, and a folder under it shows its tasks.</summary>
    [Fact]
    public async Task B4_I10_WindowsOwnFolder_IsSkipped_UnlessListed()
    {
        var system = TaskSystem();
        var tool = new TaskListTool(TaskDoor(system), () => new AppSettingsData { TaskTools = true });
        await CallTool(tool);
        Assert.True(system.ListedSkippingMicrosoft);
        string defrag = await CallTool(tool, ("folder", @"\Microsoft\Windows\Defrag"));
        Assert.False(system.ListedSkippingMicrosoft);
        Assert.Contains("ScheduledDefrag", defrag);
        Assert.Contains("1 of 1 matching", defrag);
    }

    /// <summary>R3: the folder is read as a path.</summary>
    [Theory]
    [InlineData("", "\\")]
    [InlineData("\\", "\\")]
    [InlineData("Backup", @"\Backup")]
    [InlineData(@"\Backup\", @"\Backup")]
    [InlineData(@"\Backup\Nightly", @"\Backup\Nightly")]
    [InlineData(@"\a/b", null)]
    [InlineData(@"\..", null)]
    [InlineData(@"\a\\b", null)]
    [InlineData("\\a\u0001b", null)]
    public void R3_TheFolder_IsAPath(string given, string? folder) => Assert.Equal(folder, TaskAccess.Folder(given).Folder);

    /// <summary>R2: the schema's examples are one backslash each.</summary>
    [Fact]
    public void R2_TheSchemaExamples_DecodeToOneBackslash()
    {
        var tool = new TaskListTool(TaskDoor(TaskSystem()), () => new AppSettingsData { TaskTools = true });
        string folder = tool.JsonSchema.GetProperty("properties").GetProperty("folder").GetProperty("description").GetString()!;
        Assert.Contains("\"\\Backup\"", folder);
        Assert.DoesNotContain(@"\\", folder);
    }

    /// <summary>E4: the next call keeps every filter and the limit.</summary>
    [Fact]
    public void E4_TheTasksNextCall_KeepsEveryFilter()
    {
        var entries = Enumerable.Range(0, 6).Select(i => new TaskEntry(@"\Backup\T" + i, "T" + i, TaskRunState.Ready, true, null, 0, null, 0)).ToList();
        string text = TaskRender.List("app01", entries, 6, 0, 2, new TaskFilter(@"\Backup", false, "ready", "T", true), "app01");
        Assert.Contains(@"next: task_list(offset 2, folder ""\Backup"", recursive false, state ready, filter ""T"", include_microsoft true, limit 2, computer app01)", text);
    }

    /// <summary>C2: a task whose definition changed after the question is not run.</summary>
    [Fact]
    public async Task C2_ATaskChangedAfterTheQuestion_IsNotRun()
    {
        var system = TaskSystem();
        var access = TaskDoor(system);
        var (source, _) = access.Computer(null, forceRedacted: false);
        string answer = await TaskChanges.RunAsync(access, source!, @"\Backup\Nightly", TaskActions.Run, true, false, (_, _) =>
        {
            system.Tasks[@"\Backup\Nightly"] = (system.Tasks[@"\Backup\Nightly"].Entry, FakeTaskSystem.Xml(@"C:\evil.exe"));
            return Task.FromResult<bool?>(true);
        }, CancellationToken.None);
        Assert.StartsWith(@"Error: \Backup\Nightly on this PC changed after the question", answer);
        Assert.Empty(system.Changes);
    }

    /// <summary>C2's services half: a service whose program changed after the question is not started.</summary>
    [Fact]
    public async Task C2_AServiceChangedAfterTheQuestion_IsNotStarted()
    {
        var system = Services();
        var (access, source) = ServiceDoor(system);
        string answer = await ServiceChanges.RunAsync(access, source, "Helper", ServiceActions.Start, null, true, false, (_, _) =>
        {
            system.Services["Helper"].Config = system.Services["Helper"].Config with { ImagePath = @"C:\evil.exe" };
            return Task.FromResult<bool?>(true);
        }, CancellationToken.None);
        Assert.StartsWith("Error: Helper on this PC changed after the question", answer);
        Assert.Empty(system.Changes);
    }

    /// <summary>T12: an unreadable definition's run is refused as not known, never as SYSTEM.</summary>
    [Fact]
    public async Task T12_AnUnreadableDefinition_IsNotCalledSystem()
    {
        var system = TaskSystem();
        system.Tasks[@"\Backup\Nightly"] = (system.Tasks[@"\Backup\Nightly"].Entry, "<Task><not closed");
        var access = TaskDoor(system);
        var (source, _) = access.Computer(null, forceRedacted: false);
        string answer = await TaskChanges.RunAsync(access, source!, @"\Backup\Nightly", TaskActions.Run, true, false, (_, _) => Task.FromResult<bool?>(true), CancellationToken.None);
        Assert.Equal(TaskText.PrivilegedUnknown(@"\Backup\Nightly"), answer);
        Assert.DoesNotContain("SYSTEM", answer);
    }

    /// <summary>T18: Run's own refusals are sentences.</summary>
    [Theory]
    [InlineData(0x80041328, "run on demand")]
    [InlineData(0x80041320, "not signed in")]
    [InlineData(0x8004131E, "end boundary")]
    public void T18_RunsOwnRefusals_AreSentences(uint code, string words) =>
        Assert.Contains(words, TaskErrors.Sentence(unchecked((int)code), new TaskErrorContext("this PC", @"\Backup\Nightly", false, "me", true), ""));

    /// <summary>E8, P3: the history line quotes the path; /tasks and /services read quoted words.</summary>
    [Fact]
    public void E8_P3_Quoting()
    {
        string line = TaskCommand.HistoryLine(@"\Ops\Sync computer:dc02", "app01");
        Assert.EndsWith("computer:\"app01\" level:all \"\\Ops\\Sync computer:dc02\"", line);
        var parsed = NeonSidekick.EventLog.EventLogCommand.Words(line);
        Assert.Equal(@"\Ops\Sync computer:dc02", parsed[^1]);

        var (tasks, error) = TaskCommand.Parse(@"folder:""\Microsoft\Windows\Windows Defender"" microsoft");
        Assert.Null(error);
        Assert.Equal(@"\Microsoft\Windows\Windows Defender", tasks!.Folder);
        var (services, _) = ServiceCommand.Parse(@"computer:""app 01"" state:all");
        Assert.Equal("app 01", services!.Computer);
    }

    /// <summary>T15: the panes read an answer for the user: their no said plainly, a change made in part still read again.</summary>
    [Fact]
    public void T15_ThePanesReading()
    {
        Assert.Equal((ComputerText.DeclinedOnPane, false, false), ComputerText.ForPane(ComputerText.Declined));
        var (text, failed, changed) = ComputerText.ForPane(ComputerText.StoppedWaitingMade("starting"));
        Assert.DoesNotContain("Do not", text);
        Assert.False(failed);
        Assert.True(changed);
        var restart = ComputerText.ForPane(ServiceText.RestartNotStarted("App", "this PC", ServiceRunState.StopPending, RestartStop.Cancelled));
        Assert.True(restart.Failed);
        Assert.True(restart.Changed);
        Assert.DoesNotContain("Error:", restart.Text);
    }

    /// <summary>J12: a lone star is everything, for every separator.</summary>
    [Theory]
    [InlineData('\\', @"\Backup\Nightly")]
    [InlineData('/', "Microsoft-Windows-PowerShell/Operational")]
    [InlineData(NameGlob.NoSeparator, "Spooler")]
    public void J12_ALoneStar_MatchesEverything(char separator, string name) => Assert.True(NameGlob.Matches("*", name, separator));
}
