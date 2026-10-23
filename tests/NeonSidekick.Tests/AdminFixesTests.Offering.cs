using System.Text.RegularExpressions;
using NeonSidekick.App;
using NeonSidekick.Computers;
using NeonSidekick.Llm;
using NeonSidekick.Services;
using NeonSidekick.Settings;
using NeonSidekick.Tasks;

namespace NeonSidekick.Tests;

/// <summary>Phase 12 of the admin review's fixes (2026-10-23): what a turn offers, the prompt's rules, and the lists' counts.</summary>
public sealed partial class AdminFixesTests
{
    /// <summary>I4: the start type's own sentence rides its own flag; the control sentence its.</summary>
    [Fact]
    public void I4_TheStartTypesSentence_RidesItsOwnFlag()
    {
        string startTypes = Assistant.AdminTail(AdminRules.Services | AdminRules.ServiceStartTypes, eventLog: false);
        Assert.Contains(Assistant.ServiceStartTypeRule, startTypes);
        Assert.DoesNotContain(Assistant.ServiceWriteRule, startTypes);
        Assert.Contains(Assistant.ServiceWriteRule, Assistant.AdminTail(AdminRules.Services | AdminRules.ServiceWrites, eventLog: false));
    }

    /// <summary>N3: the 4740 pointer only where it can be followed.</summary>
    [Fact]
    public void N3_TheLockoutPointer_OnlyWhereItCanBeFollowed()
    {
        Assert.DoesNotContain(Assistant.DirectoryLockoutRule, Assistant.AdminTail(AdminRules.Directory, eventLog: true));
        Assert.Contains(Assistant.DirectoryLockoutRule, Assistant.AdminTail(AdminRules.Directory | AdminRules.DirectoryLockout, eventLog: true));
    }

    /// <summary>L8: a readwrite computer with no control patterns offers no write.</summary>
    [Fact]
    public void L8_NoControlPatterns_NoWrite()
    {
        var settings = new AppSettingsData { ServiceTools = true, ServiceWrites = true };
        var none = new ComputerCatalog<ServiceComputerConfig>([new Computer<ServiceComputerConfig>("local", new ServiceComputerConfig { Access = "readwrite" })], [], ServiceConfigFile.Info);
        Assert.Equal(ToolsText.NoControlReason, ChatScreen.ServiceWriteReason(Llm.Tools.ServiceControlTool.ToolName, settings, pane: true, none));
        var some = new ComputerCatalog<ServiceComputerConfig>([new Computer<ServiceComputerConfig>("local", new ServiceComputerConfig { Access = "readwrite", Control = ["App"] })], [], ServiceConfigFile.Info);
        Assert.Null(ChatScreen.ServiceWriteReason(Llm.Tools.ServiceControlTool.ToolName, settings, pane: true, some));
    }

    /// <summary>S9: a task list's header counts the lines that fit.</summary>
    [Fact]
    public void S9_TheTaskListsHeader_CountsWhatFits()
    {
        var entries = Enumerable.Range(0, 100).Select(i => new TaskEntry(@"\Backup\" + new string('n', 280) + i.ToString(System.Globalization.CultureInfo.InvariantCulture), "T", TaskRunState.Ready, true, null, 0, null, 0)).ToList();
        string text = TaskRender.List("app01", entries, 100, 0, 100, new TaskFilter(), null);
        int lines = text.Split('\n').Count(l => l.StartsWith("  ⟪", StringComparison.Ordinal));
        var head = Regex.Match(text, @": (\d+) of 100 matching");
        Assert.True(head.Success);
        Assert.Equal(lines, int.Parse(head.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
        Assert.True(lines < 100);
    }

    /// <summary>T19: the panes' words live in their Text classes.</summary>
    [Fact]
    public void T19_ThePanesWords_InTheirTextClasses()
    {
        Assert.Equal(ServiceText.CopiedNotice("App"), ServicesMenu.CopiedNotice("App"));
        Assert.Equal(TaskText.AskOffNotice, TasksMenu.AskOffNotice);
        Assert.Equal(["automatic", "delayed", "manual", "disabled"], ServiceText.StartTypeWords);
    }
}
