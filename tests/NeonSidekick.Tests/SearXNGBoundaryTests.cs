using System.Text.RegularExpressions;

namespace NeonSidekick.Tests;

/// <summary>
/// The metasearch library shares the app's project (2026-11-06, the user's call), so the compiler no longer keeps the two apart. This does:
/// the library never names the app, and the app reaches the library from one adapter only.
/// </summary>
public class SearXNGBoundaryTests
{
    private static readonly string[] AllowedUsers = [Path.Combine("Web", "BuiltInMetaSearch.cs")];

    // A using or a qualified name, not the bare words: AboutText's component row says "SearXNG.Net" as plain text.
    private static readonly Regex LibraryReference = new(@"\busing\s+(static\s+)?(\w+\s*=\s*)?(global::)?SearXNG\.Net\b|\bSearXNG\.Net\.[A-Z]", RegexOptions.CultureInvariant);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NeonSidekick.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir.FullName;
    }

    private static IEnumerable<string> SourceFiles(string folder) =>
        Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal));

    [Fact]
    public void TheLibrary_NeverNamesTheApp()
    {
        string library = Path.Combine(RepoRoot(), "src", "NeonSidekick", "SearXNG");
        var files = SourceFiles(library).ToList();

        Assert.Contains(files, f => f.EndsWith("MetaSearch.cs", StringComparison.Ordinal));
        Assert.All(files, f => Assert.DoesNotContain("NeonSidekick", File.ReadAllText(f), StringComparison.Ordinal));
    }

    [Fact]
    public void TheApp_ReachesTheLibrary_OnlyThroughTheAdapter()
    {
        string app = Path.Combine(RepoRoot(), "src", "NeonSidekick");
        string library = Path.Combine(app, "SearXNG") + Path.DirectorySeparatorChar;

        foreach (string file in SourceFiles(app).Where(f => !f.StartsWith(library, StringComparison.Ordinal)))
        {
            if (!AllowedUsers.Any(a => file.EndsWith(Path.DirectorySeparatorChar + a, StringComparison.Ordinal)))
            {
                Assert.False(LibraryReference.IsMatch(File.ReadAllText(file)), file + " reaches SearXNG.Net directly");
            }
        }
    }

    [Theory]
    [InlineData("using SearXNG.Net;", true)]
    [InlineData("using SearXNG.Net.Engines;", true)]
    [InlineData("using global::SearXNG.Net;", true)]
    [InlineData("using Search = SearXNG.Net.MetaSearch;", true)]
    [InlineData("var x = new SearXNG.Net.MetaSearch();", true)]
    [InlineData("new(\"SearXNG.Net\", \"3e95f6c\", \"AGPL-3.0-or-later\", \"the metasearch\")", false)]
    public void TheCheck_FindsCodeButNotTheNameInText(string line, bool found) =>
        Assert.Equal(found, LibraryReference.IsMatch(line));
}
