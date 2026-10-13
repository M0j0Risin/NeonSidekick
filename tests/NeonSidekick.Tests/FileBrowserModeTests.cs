using NeonSidekick.Diagnostics;
using NeonSidekick.Files;
using NeonSidekick.Settings;

namespace NeonSidekick.Tests;

public class FileBrowserModeTests
{
    [Fact]
    public void Names_ArePinned_InMenuOrder()
    {
        Assert.Equal(new[] { "hide-hidden", "show-hidden" }, FileBrowserMode.Names);
        Assert.Equal("hide-hidden", FileBrowserMode.Default);
        Assert.Equal(FileBrowserMode.Default, new AppSettingsData().FileBrowserMode);
    }

    [Theory]
    [InlineData("hide-hidden", FileBrowserVisibility.Default)]
    [InlineData("show-hidden", FileBrowserVisibility.ShowHidden)]
    [InlineData("  Show-Hidden ", FileBrowserVisibility.ShowHidden)]
    public void TryParse_TrimsAndIgnoresCase(string text, FileBrowserVisibility expected)
    {
        Assert.True(FileBrowserMode.TryParse(text, out var visibility));
        Assert.Equal(expected, visibility);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("hidden")]
    [InlineData("all")]
    public void TryParse_RejectsAnythingElse(string? text)
    {
        Assert.False(FileBrowserMode.TryParse(text, out var visibility));
        Assert.Equal(FileBrowserVisibility.Default, visibility);
    }

    [Fact]
    public void Name_RoundTripsEveryMode_AndEveryModeHasAHint()
    {
        foreach (var name in FileBrowserMode.Names)
        {
            Assert.True(FileBrowserMode.TryParse(name, out var visibility));
            Assert.Equal(name, FileBrowserMode.Name(visibility));
            Assert.NotEqual("", FileBrowserMode.Describe(name));
        }

        Assert.Equal("hide hidden, system and dot entries", FileBrowserMode.Describe("hide-hidden"));   // since 2026-09-23, when /tree came to follow the mode
        Assert.Equal("list hidden, system and dot entries too", FileBrowserMode.Describe("show-hidden"));
        Assert.Equal("", FileBrowserMode.Describe("hidden"));
    }

    [Fact]
    public void Resolve_MapsTheSavedMode()
    {
        Assert.Equal(FileBrowserVisibility.ShowHidden, FileBrowserMode.Resolve(new AppSettingsData { FileBrowserMode = "show-hidden" }));
        Assert.Equal(FileBrowserVisibility.Default, FileBrowserMode.Resolve(new AppSettingsData { FileBrowserMode = "hide-hidden" }));
        Assert.Equal(FileBrowserVisibility.Default, FileBrowserMode.Resolve(new AppSettingsData()));
    }

    [Fact]
    public void Resolve_UnknownSavedValue_WarnsAndUsesTheDefault()
    {
        var warnings = new List<DiagnosticEvent>();
        Action<DiagnosticEvent> capture = e => { if (e.Category == "Files" && e.Level == DiagnosticLevel.Warning) warnings.Add(e); };
        DiagnosticLog.Emitted += capture;
        try
        {
            Assert.Equal(FileBrowserVisibility.Default, FileBrowserMode.Resolve(new AppSettingsData { FileBrowserMode = "all" }));
        }
        finally
        {
            DiagnosticLog.Emitted -= capture;
        }

        var warning = Assert.Single(warnings);
        Assert.Contains("FileBrowserMode='all' is not one of hide-hidden, show-hidden. Using hide-hidden.", warning.Message);
    }

    // ── The disks behind the tree ───────────────────────────────────────────

    [Fact]
    public void IsShown_LeavesOutHiddenSystemAndDotFolders()
    {
        Assert.True(FileSystemFolders.IsShown("Users", FileAttributes.Directory));
        Assert.False(FileSystemFolders.IsShown("$RECYCLE.BIN", FileAttributes.Directory | FileAttributes.Hidden | FileAttributes.System));
        Assert.False(FileSystemFolders.IsShown("AppData", FileAttributes.Directory | FileAttributes.Hidden));
        Assert.False(FileSystemFolders.IsShown("System Volume Information", FileAttributes.Directory | FileAttributes.System));
        Assert.False(FileSystemFolders.IsShown(".git", FileAttributes.Directory));
    }

    [Fact]
    public void Children_ListsSubfoldersSorted_HiddenOnesOnlyUnderShowHidden_AndExistsAnswersForBoth()
    {
        string root = Path.Combine(Path.GetTempPath(), "neon-folders-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "beta"));
            Directory.CreateDirectory(Path.Combine(root, "Alpha"));
            Directory.CreateDirectory(Path.Combine(root, ".dot"));
            string hidden = Path.Combine(root, "hidden");
            Directory.CreateDirectory(hidden);
            File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);
            File.WriteAllText(Path.Combine(root, "a-file.txt"), "not a folder");

            var shown = new FileSystemFolders(FileBrowserVisibility.Default);
            var all = new FileSystemFolders(FileBrowserVisibility.ShowHidden);

            Assert.Equal([Path.Combine(root, "Alpha"), Path.Combine(root, "beta")], shown.Children(root));
            Assert.Equal([Path.Combine(root, ".dot"), Path.Combine(root, "Alpha"), Path.Combine(root, "beta"), hidden], all.Children(root));
            Assert.True(shown.Exists(hidden));
            Assert.True(shown.Exists(Path.Combine(root, ".dot")));
            Assert.False(shown.Exists(Path.Combine(root, "a-file.txt")));
            Assert.False(shown.Exists(Path.Combine(root, "nowhere")));
            Assert.ThrowsAny<IOException>(() => shown.Children(Path.Combine(root, "nowhere")));

            // The roots hold the temp folder's own: a ready drive on Windows, / elsewhere.
            var roots = shown.Roots();
            Assert.NotEmpty(roots);
            Assert.Contains(roots, r => root.StartsWith(r, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(["/"], roots);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
