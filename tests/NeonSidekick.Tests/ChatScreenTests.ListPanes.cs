using NeonSidekick.App;
using NeonSidekick.Camera;
using NeonSidekick.Files;
using NeonSidekick.Plans;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// The read-only lists on the info pane (2026-10-04, the user's pick): <c>/tree</c>, <c>/comfy offered</c>, <c>/docker logs</c>,
/// <c>/ha states</c>, <c>/print printers</c>, <c>/plan open</c>, <c>/camera list</c> and <c>/screen list</c> open a pane, closed with
/// ESC, and write nothing into the chat; a failure stays its error line.
/// </summary>
public partial class ChatScreenTests
{
    /// <summary>The pane on the screen: a geometry and room for it.</summary>
    private void ListPaneScreen()
    {
        _settings.Update(d => d.TtsOutput = false);
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null);
    }

    /// <summary>Runs <paramref name="command"/>, closes its pane with ESC, exits; the output.</summary>
    private async Task<string> RunListPaneAsync(string command)
    {
        PushLine(command);
        _console.Input.PushKey(Keys.Escape);
        PushLine("/exit");
        return await RunAsync();
    }

    /// <summary><paramref name="text"/> is on the screen, and on no notice line.</summary>
    private static void OnThePaneOnly(string output, string text)
    {
        Assert.Contains(text, output);
        Assert.DoesNotContain(output.Split('\n'), line => line.Contains(text, StringComparison.Ordinal) && line.Contains("  · ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Tree_AtTheIdleLine_IsOnThePane()
    {
        ListPaneScreen();
        string files = Path.Combine(_settings.ProfileDirectory, WorkingDirectory.DefaultFolderName);
        Directory.CreateDirectory(files);
        File.WriteAllText(Path.Combine(files, "pane-notes.txt"), "hello");

        string output = await RunListPaneAsync("/tree");

        Assert.Contains(TreeText.PaneLabel("/tree", ""), output);
        OnThePaneOnly(output, "pane-notes.txt");
    }

    [Fact]
    public async Task ComfyOffered_IsOnThePane()
    {
        ListPaneScreen();
        _settings.Update(d => { d.ComfyTools = true; d.ComfyWorkflowsOffered = ["pony-txt2img"]; });
        File.WriteAllText(Path.Combine(_settings.ProfileComfyDirectory, "pony-txt2img.json"), "{\"6\":{\"class_type\":\"CLIPTextEncode\",\"inputs\":{\"text\":\"{{prompt}}\"}}}");

        string output = await RunListPaneAsync("/comfy offered");

        Assert.Contains("/comfy " + ChatScreen.ComfyOfferedWord, output);
        OnThePaneOnly(output, "1 workflow offered to the model:");
        OnThePaneOnly(output, "• pony-txt2img");
    }

    [WindowsFact]
    public async Task DockerLogs_AreOnThePane_ButAFailureIsItsErrorLine()
    {
        var engine = DockerEngine();
        engine.Map(
            NeonSidekick.Docker.DockerClient.Host + "/v1.47/containers/c9e0889008d03fe42ce2ba7be159d07f2392a1c5abe9322dd7acb9bce852b7f3/logs",
            (_, _) => Task.FromResult(StubHttpMessageHandler.Bytes(System.Net.HttpStatusCode.OK, [.. SmokeChecks.DockerFrame(1, "mariadb ready for connections\n")], NeonSidekick.Docker.DockerLogStream.MultiplexedType)));
        ListPaneScreen();
        PushLine("/docker logs nope");

        string output = await RunListPaneAsync("/docker logs mariadb_dev 5");

        Assert.Contains("/docker logs mariadb_dev 5", output);
        OnThePaneOnly(output, "mariadb ready for connections");
        Assert.Contains("  ✗ ", output);   // the unknown container: its error line in the chat, no pane
    }

    [Fact]
    public async Task HaStates_AreOnThePane()
    {
        const string server = "http://ha.lan:8123";
        var stub = new StubHttpMessageHandler()
            .Map(server + "/api/states", System.Net.HttpStatusCode.OK, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ha", "states.json")))
            .Map(server + "/api/template", System.Net.HttpStatusCode.OK, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ha", "areas.json")), "text/plain")
            .Map(server + "/api/services/", System.Net.HttpStatusCode.OK, "[]");
        _haClient = (url, token) => new NeonSidekick.HomeAssistant.HaClient(url, token, new HttpClient(stub));
        ListPaneScreen();
        _settings.Update(d => { d.HomeAssistantUrl = server; d.HomeAssistantToken = "test-token"; });

        string output = await RunListPaneAsync("/ha states light");

        Assert.Contains("/ha states light", output);
        OnThePaneOnly(output, "light.den_den_corner_lamp");
    }

    [Fact]
    public async Task PrintPrinters_AreOnThePane()
    {
        ListPaneScreen();

        string output = await RunListPaneAsync("/print printers");

        Assert.Contains("/print printers", output);
        OnThePaneOnly(output, FakePrintSpooler.Laser);
    }

    [Fact]
    public async Task PlanOpen_Alone_ListsThePlansOnThePane()
    {
        ListPaneScreen();
        OldPlan("add-guide", PlanStatus.Incomplete, "# Guide\n- [x] One\n- [ ] Two");

        string output = await RunListPaneAsync("/plan open");

        Assert.Contains("/plan " + PlanText.OpenWord, output);
        OnThePaneOnly(output, "add-guide");
    }

    [Fact]
    public async Task CameraList_IsOnThePane()
    {
        _cameraSystem = new FakeCameraSystem();
        ListPaneScreen();

        string output = await RunListPaneAsync("/camera list");

        Assert.Contains("/camera list", output);
        OnThePaneOnly(output, "1. Fake Cam  ← chosen");
        OnThePaneOnly(output, CameraText.ListHeader);
    }

    [Fact]
    public async Task ScreenList_IsOnThePane()
    {
        _screenSystem = new FakeScreenSystem();
        ListPaneScreen();

        string output = await RunListPaneAsync("/screen list");

        Assert.Contains("\n" + Titled("/screen list") + "\n", output);   // the label once: one tab, no chip (2026-10-04)
        Assert.DoesNotContain("/screen list   /screen list", output);
        Assert.Contains(InfoPane.SingleTabHintText, output);
        OnThePaneOnly(output, "window:200  \"notes.txt - Notepad\"");
    }
}
