using NeonSidekick.Diagnostics;
using NeonSidekick.Files;
using NeonSidekick.Settings;

namespace NeonSidekick.Tests;

public class MentionFolderModeTests
{
    [Fact]
    public void Names_ArePinned_InMenuOrder()
    {
        Assert.Equal(new[] { "apply", "remain" }, MentionFolderMode.Names);
        Assert.Equal("remain", MentionFolderMode.Default);
        Assert.Equal(MentionFolderMode.Default, new AppSettingsData().FileMentionFolderMode);
    }

    [Theory]
    [InlineData("apply", MentionFolderAction.Apply)]
    [InlineData("remain", MentionFolderAction.Remain)]
    [InlineData("  Folder-Remain ", MentionFolderAction.Remain)]
    public void TryParse_TrimsAndIgnoresCase(string text, MentionFolderAction expected)
    {
        Assert.True(MentionFolderMode.TryParse(text, out var action));
        Assert.Equal(expected, action);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("open")]
    [InlineData("folder")]
    public void TryParse_RejectsAnythingElse(string? text)
    {
        Assert.False(MentionFolderMode.TryParse(text, out var action));
        Assert.Equal(MentionFolderAction.Apply, action);
    }

    [Fact]
    public void Name_RoundTripsEveryMode_AndEveryModeHasAHint()
    {
        foreach (var name in MentionFolderMode.Names)
        {
            Assert.True(MentionFolderMode.TryParse(name, out var action));
            Assert.Equal(name, MentionFolderMode.Name(action));
            Assert.NotEqual("", MentionFolderMode.Describe(name));
        }

        // The user's wording (2026-09-16).
        Assert.Equal("insert @folder/ and close the list", MentionFolderMode.Describe("apply"));
        Assert.Equal("insert @folder/ and keep listing inside it", MentionFolderMode.Describe("remain"));
        Assert.Equal("", MentionFolderMode.Describe("open"));
    }

    [Fact]
    public void Resolve_MapsTheSavedMode()
    {
        Assert.Equal(MentionFolderAction.Remain, MentionFolderMode.Resolve(new AppSettingsData { FileMentionFolderMode = "remain" }));
        Assert.Equal(MentionFolderAction.Apply, MentionFolderMode.Resolve(new AppSettingsData { FileMentionFolderMode = "apply" }));
        Assert.Equal(MentionFolderAction.Remain, MentionFolderMode.Resolve(new AppSettingsData()));
    }

    [Fact]
    public void Resolve_UnknownSavedValue_WarnsAndUsesTheDefault()
    {
        var warnings = new List<DiagnosticEvent>();
        Action<DiagnosticEvent> capture = e => { if (e.Category == "Files" && e.Level == DiagnosticLevel.Warning) warnings.Add(e); };
        DiagnosticLog.Emitted += capture;
        try
        {
            Assert.Equal(MentionFolderAction.Remain, MentionFolderMode.Resolve(new AppSettingsData { FileMentionFolderMode = "descend" }));
        }
        finally
        {
            DiagnosticLog.Emitted -= capture;
        }

        var warning = Assert.Single(warnings);
        Assert.Contains("FileMentionFolderMode='descend' is not one of apply, remain. Using remain.", warning.Message);
    }
}
