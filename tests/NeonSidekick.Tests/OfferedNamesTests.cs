using NeonSidekick.App;
using NeonSidekick.Sql;

namespace NeonSidekick.Tests;

/// <summary><see cref="OfferedNames"/> (2026-10-04): which saved names a checklist calls gone, and what it keeps.</summary>
public sealed class OfferedNamesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ns-offered-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Stale_IsWhatIsNeitherListedNorPresent_TrimmedOnceInSavedOrder_AnyCase()
    {
        Assert.Equal(["old", "Other"], OfferedNames.Stale([" old ", "AW", "", "  ", "broken", "Other", "OLD"], ["aw"], ["Broken"], StringComparer.OrdinalIgnoreCase));
        Assert.Equal(["AW"], OfferedNames.Stale(["AW"], ["aw"], [], StringComparer.Ordinal));
        Assert.Empty(OfferedNames.Stale(null, ["aw"], [], StringComparer.Ordinal));
        Assert.Equal(["x"], OfferedNames.Gone(["x", "aw"], ["aw"], StringComparer.Ordinal));
    }

    [Fact]
    public void StaleConnections_DropsNothing_WhenAWholeFileWasUnreadable_AndKeepsAProblemsName()
    {
        SqlConfigProblem[] broken = [new("p (b)", "no server", Name: "b")];
        Assert.Equal(["gone"], OfferedNames.StaleConnections(["gone", "b", "aw"], ["aw"], broken));
        Assert.Empty(OfferedNames.StaleConnections(["gone", "aw"], ["aw"], [.. broken, new("p", "the file cannot be read", WholeFile: true)]));
    }

    [Fact]
    public void Without_TrimsDropsBlanksAndDuplicates_KeepingTheOrder() =>
        Assert.Equal(["b", "a"], OfferedNames.Without([" b", "gone", "", "a", "B"], ["GONE"], StringComparer.OrdinalIgnoreCase));

    /// <summary>The loads say which problem lost a whole file and which entry a problem names.</summary>
    [Fact]
    public void TheLoads_MarkAWholeFile_AndNameABadEntry()
    {
        Directory.CreateDirectory(_dir);
        string bad = Path.Combine(_dir, "bad.json");
        File.WriteAllText(bad, "{ not json");
        var whole = Assert.Single(SqlConfigFile.Load(bad).Problems);
        Assert.True(whole.WholeFile);
        Assert.Null(whole.Name);

        string entry = Path.Combine(_dir, "sql.json");
        File.WriteAllText(entry, """{ "connections": { " broken ": { "auth": "windows" } } }""");
        var named = Assert.Single(SqlConfigFile.Load(entry).Problems);
        Assert.False(named.WholeFile);
        Assert.Equal("broken", named.Name);
    }
}
