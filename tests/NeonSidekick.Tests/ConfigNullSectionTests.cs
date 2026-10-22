using NeonSidekick.ActiveDirectory;
using NeonSidekick.EventLog;
using NeonSidekick.Mcp;
using NeonSidekick.Services;
using NeonSidekick.Sql;
using NeonSidekick.Tasks;
using NeonSidekick.Unc;

namespace NeonSidekick.Tests;

/// <summary>
/// 2026-10-22, review K1: a connections file whose section is JSON <c>null</c> (<c>{ "shares": null }</c>) reads as null past the
/// non-nullable property, and every loader but SQLite's then threw a NullReferenceException from a method that "never throws" — at the
/// startup encryption pass, and at every turn's tool build. Each family's file now loads as empty.
/// </summary>
public sealed class ConfigNullSectionTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));

    public ConfigNullSectionTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* best effort */ }
    }

    private string File(string json)
    {
        string path = Path.Combine(_folder, Guid.NewGuid().ToString("N") + ".json");
        System.IO.File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void ANullSection_LoadsEmpty_InEveryFamily()
    {
        Assert.Empty(UncConfigFile.Load(File("{ \"shares\": null }")).Shares);
        Assert.Empty(SqlConfigFile.Load(File("{ \"connections\": null }")).Connections);
        Assert.Empty(EventLogConfigFile.Load(File("{ \"computers\": null }")).Computers);
        Assert.Empty(ServiceConfigFile.Family.Load(File("{ \"computers\": null }")).Computers);
        Assert.Empty(TaskConfigFile.Family.Load(File("{ \"computers\": null }")).Computers);
        Assert.Empty(DirectoryConfigFile.Load(File("{ \"domains\": null }")).Domains);
        Assert.Empty(McpConfigFile.Load(File("{ \"mcpServers\": null }")).Servers);
    }
}
