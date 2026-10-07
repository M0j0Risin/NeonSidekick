using NeonSidekick.App;
using NeonSidekick.Docker;
using NeonSidekick.Llm;
using NeonSidekick.Mcp;
using NeonSidekick.Sessions;
using NeonSidekick.UI;
using NeonSidekick.YouTube;

namespace NeonSidekick.Tests;

/// <summary>
/// The list panes' detail footers and their type-to-filter (2026-10-07, the user's ask: a row cut at the edge said nothing of what it
/// lost): each footer pinned for a row the pane would cut, and the matches each filter keeps.
/// </summary>
public class ListFootersTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly DateTimeOffset At = new(2026, 10, 7, 9, 12, 0, TimeSpan.Zero);

    [Fact]
    public void Sessions_TheWholeTitle_ThenWhenItRan_ItsTurns_ItsModel_AndWhetherItIsOnScreen()
    {
        var session = new SessionSummary(12, At, At.AddHours(5).AddMinutes(28), "Fixing the tab strips and the saved YouTube videos, then the version column", TitleSource.Model, "qwen3", 14);

        Assert.Equal(
            new MenuFooter("Fixing the tab strips and the saved YouTube videos, then the version column", "started 2026-10-07 09:12 · last 2026-10-07 14:40 · 14 turns · qwen3 · " + SessionsMenu.CurrentNote),
            SessionsMenu.Footer(session, current: true, Utc));
        Assert.Equal("started 2026-10-07 09:12 · last 2026-10-07 14:40 · 1 turn", SessionsMenu.Footer(session with { Model = "", Turns = 1 }, current: false, Utc).Last);
        Assert.True(SessionsMenu.Matches("TAB STRIPS", session));
        Assert.True(SessionsMenu.Matches("qwen", session));           // the model too
        Assert.False(SessionsMenu.Matches("mac port", session));
    }

    [Fact]
    public void Rewind_TheWholeMessage_ItsLinesJoined_AndItsTools()
    {
        Assert.Equal(new MenuFooter("first line second line", "message #3 · 4 tool calls"), RewindText.Footer(new RewindTurn(3, 10, "first line\n\nsecond line", 4)));
        Assert.Equal("message #1 · no tools", RewindText.Footer(new RewindTurn(1, 0, "hi", 0)).Last);
        Assert.Equal("message #2 · 1 tool call", RewindText.Footer(new RewindTurn(2, 4, "hi", 1)).Last);
    }

    [Fact]
    public void Docker_TheImageProjectPortsAndNetworks_ThenDockersStatus()
    {
        var container = new DockerContainer("abc", "mysql_dev", "mysql:8.4", "sha256:1", "running", "Up 3 hours (healthy)", 0,
            [new DockerPort("0.0.0.0", 3306, 3306, "tcp")], new Dictionary<string, string> { [DockerContainer.ProjectLabel] = "shop" }, ["shop_default"], []);

        Assert.Equal(new MenuFooter("mysql_dev · mysql:8.4 · project shop · 3306→3306/tcp · network shop_default", "Up 3 hours (healthy)"), DockerMenu.Footer(container));
        Assert.True(DockerMenu.Matches("shop", container));           // the project
        Assert.True(DockerMenu.Matches("MYSQL:8", container));        // the image
        Assert.False(DockerMenu.Matches("postgres", container));
    }

    [Fact]
    public void YouTube_TheSavedAndTheFound_WholeWithTheirFacts()
    {
        var saved = new YouTubeSaved { Id = "aqz-KE-bpKQ", Title = "Big Buck Bunny", Author = "Blender", Duration = 635, Position = 154, Added = At, LastPlayed = At.AddHours(12).AddMinutes(33) };
        Assert.Equal(new MenuFooter("Big Buck Bunny — Blender", "at 2:34 of 10:35 · saved 2026-10-07 09:12 · last played 2026-10-07 21:45 · id aqz-KE-bpKQ"), YouTubeText.SavedFooter(saved, Utc));
        Assert.True(YouTubeText.SavedMatches("blender", saved));
        Assert.True(YouTubeText.SavedMatches("aqz", saved));          // the id
        Assert.False(YouTubeText.SavedMatches("lofi", saved));

        var hit = new YouTubeHit("aqz-KE-bpKQ", "Big Buck Bunny 60fps 4K", "Blender", At.AddYears(-12), TimeSpan.FromSeconds(635), 21_543_678);
        Assert.Equal(new MenuFooter("Big Buck Bunny 60fps 4K", "Blender · 10:35 · 21M views · 2014 · id aqz-KE-bpKQ"), YouTubeText.PickFooter(hit));
    }

    [Fact]
    public void Servers_TheUrlTheDetailAndTheModels_AndTheModelsWholeId()
    {
        var vllm = new LlmServer(new Uri("http://10.0.0.5:8000/v1"), "vLLM", new ProbeResult(true, ["Qwen/Qwen3-30B-A3B-Instruct-2507-FP8", "other"], "2 chat models"));
        Assert.Equal(new MenuFooter("vLLM · http://10.0.0.5:8000/v1", "2 chat models · 2 models"), SettingsMenu.ServerFooter(vllm));
        Assert.True(SettingsMenu.ServerMatches("qwen3", vllm));       // a model it lists
        Assert.True(SettingsMenu.ServerMatches("10.0.0", vllm));      // the URL
        Assert.False(SettingsMenu.ServerMatches("ollama", vllm));

        Assert.Equal(new MenuFooter("Qwen/Qwen3-30B-A3B-Instruct-2507-FP8", SettingsMenu.ModelInUseNote), SettingsMenu.ModelFooter("Qwen/Qwen3-30B-A3B-Instruct-2507-FP8", inUse: true));
        Assert.Null(SettingsMenu.ModelFooter("x", inUse: false).Last);
    }

    [Fact]
    public void Mcp_AFailedServersWholeError_AConnectedOnesTools_AndTheEditRowsFiles()
    {
        McpServerStatus failed = new("docker", McpScope.Profile, null, true, McpState.Failed, "The process cannot access the file because it is being used by another process.", [], "stdio: docker mcp gateway run");
        McpServerStatus shadowed = new("chrome", McpScope.Global, McpScope.Profile, true, McpState.Off, null, [], "http: http://localhost:9/mcp");
        var facts = new McpFacts([failed, shadowed], [], true, true, new HashSet<string>(), @"D:\p\mcp.json", @"D:\g\mcp.json");

        Assert.Equal(new MenuFooter(McpText.StatusFailed(failed.Detail!), "profile · stdio: docker mcp gateway run"), McpRows.ServerFooter(facts, new McpRow.Server("docker")));
        Assert.Equal(new MenuFooter(McpText.StatusShadowed, "global · http: http://localhost:9/mcp"), McpRows.ServerFooter(facts, new McpRow.Server("chrome")));
        Assert.Equal(new MenuFooter(@"D:\p\mcp.json"), McpRows.ServerFooter(facts, new McpRow.EditProfile()));
        Assert.Equal(new MenuFooter(@"D:\g\mcp.json"), McpRows.ServerFooter(facts, new McpRow.EditGlobal()));
        Assert.Null(McpRows.ServerFooter(facts, new McpRow.Reload()));
        Assert.Null(McpRows.ServerFooter(facts, null));
    }

    [Fact]
    public void AHintWithAnyEsc_GainsTheFilter_BeforeIt()
    {
        Assert.Equal("Enter = choose · type = filter · ESC = keep", MenuFilter.HintBeforeEsc("Enter = choose · ESC = keep", ""));
        Assert.Equal("Enter = choose · " + MenuFilter.FilteringKeys, MenuFilter.HintBeforeEsc("Enter = choose · ESC = the first listed", "qw"));
        Assert.Equal("Enter = pick · type = filter", MenuFilter.HintBeforeEsc("Enter = pick", ""));
    }
}
