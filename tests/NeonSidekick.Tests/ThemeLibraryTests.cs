using System.Text.Json;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// The built-in themes (2026-10-05, the user's ask: themes entirely JSON): every <c>assets/themes/&lt;category&gt;/*.json</c> embedded and
/// built by <see cref="ThemeLibrary"/>. These hold the shipped files to what the app needs of them, so a theme added, changed or removed
/// in the folder is checked by the next test run; the readability bar is <c>ThemeFileTests.BuiltIns_AreReadable_StyleByStyle</c>.
/// </summary>
public sealed class ThemeLibraryTests
{
    /// <summary>The repository's <c>assets/themes</c>.</summary>
    internal static string AssetsDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NeonSidekick.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir.FullName, "assets", "themes");
    }

    private static List<string> ShippedFiles() =>
        [.. Directory.EnumerateFiles(AssetsDirectory(), "*.json", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase)];

    [Fact]
    public void EveryFile_SitsInOneCategoryFolder_AndIsABuiltIn_LoadingWithNoProblem()
    {
        var files = ShippedFiles();
        string root = AssetsDirectory();

        Assert.All(files, f => Assert.True(Path.GetRelativePath(root, f).Split(Path.DirectorySeparatorChar).Length == 2, $"{Path.GetRelativePath(root, f)}: not in one category folder"));
        Assert.Empty(ThemeLibrary.Embedded.Problems.Select(p => $"{p.Shown}: {p.Problem}"));
        Assert.Equal(files.Count, ThemeLibrary.All.Count);
        foreach (string file in files)
        {
            var data = ThemeFile.Read(file, out string? problem);
            Assert.True(data is not null, problem);
            string name = ThemeFile.NameOf(data, file);
            Assert.True(ThemeLibrary.TryGet(name, out _), name);
            Assert.Equal(Path.GetFileName(Path.GetDirectoryName(file)), ThemeLibrary.CategoryOf(name));
        }
    }

    /// <summary>
    /// A shipped file stands alone and whole (2026-10-05, the user's call): every colour role, a gradient, no <c>base</c>, and its name
    /// its file's, so the folder reads as the list does.
    /// </summary>
    [Fact]
    public void EveryFile_SetsEveryColour_AndAGradient_AndNoBase_UnderItsOwnName()
    {
        var failures = new List<string>();
        foreach (string file in ShippedFiles())
        {
            using var json = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var root = json.RootElement;
            string shown = Path.GetFileName(file);
            if (root.TryGetProperty("base", out _))
            {
                failures.Add($"{shown}: has a base");
            }

            if (!root.TryGetProperty("gradient", out _))
            {
                failures.Add($"{shown}: no gradient");
            }

            var roles = root.TryGetProperty("colors", out var colors) ? colors.EnumerateObject().Select(p => p.Name).ToHashSet() : [];
            failures.AddRange(ThemeKeys.Colors.Where(role => !roles.Contains(role)).Select(role => $"{shown}: no {role}"));
            if (!root.TryGetProperty("name", out var name) || name.GetString() != Path.GetFileNameWithoutExtension(file))
            {
                failures.Add($"{shown}: its name is not its file's");
            }
        }

        Assert.Empty(failures);
    }

    [Fact]
    public void TheBuiltIns_AreAToZ_AndBuiltIn_TheDefaultCollider()
    {
        Assert.Equal(ThemeLibrary.All.Select(t => t.Name).Order(StringComparer.Ordinal), ThemeLibrary.All.Select(t => t.Name));
        Assert.All(ThemeLibrary.All, t => Assert.True(t.IsBuiltIn, t.Name));
        Assert.Equal("collider", ThemeLibrary.Default.Name);
        Assert.Equal("machines", ThemeLibrary.CategoryOf("collider"));
        Assert.NotSame(ThemePalette.Emergency, ThemeLibrary.Default);
        Assert.True(ThemeLibrary.TryGet(" NOIR ", out var noir) && noir.Name == "noir");
        Assert.False(ThemeLibrary.TryGet("matrix", out var none));
        Assert.Same(ThemeLibrary.Default, none);
        Assert.Throws<ArgumentException>(() => ThemeLibrary.Get("matrix"));
        Assert.Null(ThemeLibrary.CategoryOf("matrix"));
    }

    /// <summary>
    /// <c>Theme Atlas.html</c> shows the built-ins as they are (2026-10-05): its THEMES line names the same themes, each in its category with
    /// its colours and gradient. A theme added, changed or removed under <c>assets/themes</c> fails this until <c>tools/ThemeAtlas.cs</c> runs.
    /// </summary>
    [Fact]
    public void TheAtlas_ShowsEveryBuiltIn_AsItIs()
    {
        string page = File.ReadAllText(Path.Combine(AssetsDirectory(), "Theme Atlas.html"));
        string line = page.Split('\n').Single(l => l.StartsWith("const THEMES = ", StringComparison.Ordinal)).TrimEnd('\r');
        using var json = JsonDocument.Parse(line["const THEMES = ".Length..^1]);
        var entries = json.RootElement.EnumerateArray().ToDictionary(e => e.GetProperty("n").GetString()!);
        const string Rerun = " (run dotnet run tools/ThemeAtlas.cs)";

        Assert.True(entries.Keys.Order(StringComparer.Ordinal).SequenceEqual(ThemeLibrary.All.Select(t => t.Name)), "the atlas names other themes" + Rerun);
        foreach (var theme in ThemeLibrary.All)
        {
            var entry = entries[theme.Name];
            Assert.True(ThemeLibrary.CategoryOf(theme.Name) == entry.GetProperty("cat").GetString(), theme.Name + ": another category" + Rerun);
            Assert.True(theme.Description == entry.GetProperty("d").GetString(), theme.Name + ": another description" + Rerun);
            var colors = entry.GetProperty("c");
            Assert.True(ThemeKeys.Colors.Select((role, i) => colors.GetProperty(role).GetString() == Theme.ToHex(theme.ColorOf((ThemeColorSlot)i))).All(same => same),
                theme.Name + ": other colours" + Rerun);
            Assert.True(entry.GetProperty("gr").EnumerateArray().Select(s => s.GetString()).SequenceEqual(theme.GradientStops.Select(Theme.ToHex)),
                theme.Name + ": another gradient" + Rerun);
        }
    }

    // ── The loader, fed by hand ─────────────────────────────────────────────

    private static KeyValuePair<string, string> Resource(string path, string json) => new(ThemeLibrary.ResourcePrefix + path, json);

    [Fact]
    public void Load_TakesOneCategoryDeep_ABackslashAsASlash_AndSaysWhatItLeftOut()
    {
        var loaded = ThemeLibrary.Load(
        [
            Resource("night\\collider.json", ThemeJson.File("\"name\": \"collider\"")),
            Resource("loose.json", ThemeJson.File()),
            Resource("a/b/deep.json", ThemeJson.File()),
            Resource("night/partial.json", """{ "colors": { "primary": "#123456" } }"""),
            Resource("night/broken.json", "{"),
        ]);

        Assert.Equal(["collider"], loaded.Themes.Select(t => t.Name));
        Assert.Equal("night", loaded.Categories["collider"]);
        Assert.Equal("collider", loaded.Default.Name);
        Assert.Null(loaded.Themes[0].SourcePath);
        Assert.Equal(
            ["a/b/deep.json", "loose.json", "night/broken.json", "night/partial.json"],
            loaded.Problems.Select(p => p.Shown).Order(StringComparer.Ordinal));
        Assert.Equal(ThemeText.NotInCategory, loaded.Problems.Single(p => p.Shown == "loose.json").Problem);
        Assert.StartsWith("skipped: it does not set secondary", loaded.Problems.Single(p => p.Shown == "night/partial.json").Problem);
    }

    [Fact]
    public void Load_TwoFilesOfOneName_TheFirstByPathKeepsIt()
    {
        var loaded = ThemeLibrary.Load(
        [
            Resource("b/mine.json", ThemeJson.File(null, ("primary", "#222222"))),
            Resource("a/mine.json", ThemeJson.File(null, ("primary", "#111111"))),
        ]);

        Assert.Equal("#111111", Theme.ToHex(Assert.Single(loaded.Themes).Primary));
        Assert.Equal("a", loaded.Categories["mine"]);
        Assert.Equal(ThemeText.Duplicate("mine", "a/mine.json"), Assert.Single(loaded.Problems).Problem);
    }

    [Fact]
    public void Load_NoDefault_TheEmergencyStandsIn()
    {
        var loaded = ThemeLibrary.Load([Resource("x/other.json", ThemeJson.File())]);

        Assert.Same(ThemePalette.Emergency, loaded.Default);
        Assert.Equal(ThemeName.Default, ThemePalette.Emergency.Name);
        Assert.Equal(["other"], loaded.Themes.Select(t => t.Name));   // never listed itself
    }
}
