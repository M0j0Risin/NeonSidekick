using NeonSidekick.Settings;

namespace NeonSidekick.Tests;

public class SettingsDiffTests
{
    [Fact]
    public void Changes_NamesEachChangedProperty_OldArrowNew_InTheFilesOrder()
    {
        var before = new AppSettingsData();
        var after = new AppSettingsData { TtsSpeed = 1.3, TtsOutput = true, WorkingDirectory = @"D:\x" };

        var lines = SettingsDiff.Changes(before, after);

        Assert.Equal([@"WorkingDirectory:  → D:\x", "TtsOutput: false → true", "TtsSpeed: 1.2 → 1.3"], lines);
    }

    [Fact]
    public void Changes_IsEmptyForEqualSnapshots()
    {
        var a = new AppSettingsData { TtsSpeed = 1.5 };
        var b = new AppSettingsData { TtsSpeed = 1.5 };
        Assert.Empty(SettingsDiff.Changes(a, b));
    }

    [Fact]
    public void Changes_RendersAList_AndNeverShowsTheApiKey()
    {
        var before = new AppSettingsData();
        var after = new AppSettingsData { LlmApiKey = "sk-secret", ToolsDisabled = ["read_file", "web_fetch"] };

        var lines = SettingsDiff.Changes(before, after);

        Assert.Contains("LlmApiKey: " + SettingsDiff.Redacted, lines);
        Assert.Contains("ToolsDisabled: [gitlib_delete, unzip, zip, unc_delete] → [read_file, web_fetch]", lines);   // gitlib_delete off by default since 2026-09-20 (gitlib_discard too until 2026-09-23), zip and unzip since 2026-09-21; delete was too until later that day
        Assert.DoesNotContain(lines, l => l.Contains("sk-secret", StringComparison.Ordinal));
        Assert.Contains(nameof(AppSettingsData.LlmApiKey), SettingsDiff.Secrets);
    }

    [Fact]
    public void NotDefault_ListsTheValuesOffTheCompiledDefaults()
    {
        Assert.Empty(SettingsDiff.NotDefault(new AppSettingsData()));

        var lines = SettingsDiff.NotDefault(new AppSettingsData { TtsSpeed = 1.0, LlmApiKey = "sk-secret", LlmUrl = "http://h:1/v1" });

        Assert.Equal(["LlmApiKey=" + SettingsDiff.Redacted, "LlmUrl=http://h:1/v1", "TtsSpeed=1"], lines);
        Assert.Equal("Not default: LlmUrl=http://h:1/v1, TtsSpeed=1", AppSettings.NotDefaultLogLine(["LlmUrl=http://h:1/v1", "TtsSpeed=1"]));
        Assert.Equal(AppSettings.AllDefaultLogLine, AppSettings.NotDefaultLogLine([]));
    }
}
