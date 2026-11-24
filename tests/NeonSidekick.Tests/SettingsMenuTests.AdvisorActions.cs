using NeonSidekick.Advisors;
using NeonSidekick.App;
using NeonSidekick.Llm;
using NeonSidekick.Settings;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// An advisor's Change the server and Switch to profile (2026-11-22, the user's asks from UI testing): the advisor profile's own
/// servers picked and written to its <c>profile.json</c> alone, and a switch to its profile after a yes that ends the visit.
/// </summary>
public partial class SettingsMenuTests
{
    private const string Box1 = "http://box1:8000/v1";
    private const string Box2 = "http://box2:8000/v1";

    private static LlmServer ListedServer(string url, params string[] ids) => new(new Uri(url), url, new ProbeResult(true, ids, "", null, null));

    private AppSettingsData AdvisorFile(string profile) => Profiles.ReadProfileFile(Profiles.ProfileFile(_dir, profile));

    /// <summary>The list's first advisor (under the two add rows), then its <paramref name="action"/> row.</summary>
    private void FirstAdvisorAction(int action)
    {
        Push(Keys.Down, Keys.Down, Keys.Enter);
        Down(action);
        Push(Keys.Enter);
    }

    /// <summary>The <paramref name="action"/> row of the advisor under the cursor: after an action the list keeps it there.</summary>
    private void SameAdvisorAction(int action)
    {
        Push(Keys.Enter);
        Down(action);
        Push(Keys.Enter);
    }

    /// <summary>The input line comes back once after <paramref name="from"/>: the pane never closed under the picks (ConfirmAsync's test's check).</summary>
    private void AssertPaneNeverClosedAfter(string from)
    {
        string after = Output[Output.IndexOf(from, StringComparison.Ordinal)..];
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(after, "\n› \n"));
    }

    // ── Switch to profile ───────────────────────────────────────────────────

    [Fact]
    public async Task OnThePane_SwitchToProfile_NoKeeps_AndYesSwitches_ClosingThePane()
    {
        Profiles.Create(_dir, "architect", new AppSettingsData());
        _settings.Update(d => d.AdvisorList = [new AdvisorEntry("architect", "architect")]);
        var (menu, pane) = PaneMenu();
        OpenAdvisorList();
        FirstAdvisorAction(SettingsMenu.SwitchToAdvisorChoice);
        Push(Keys.Enter);                       // No (the cursor opens on it): back on the list, on architect
        SameAdvisorAction(SettingsMenu.SwitchToAdvisorChoice);
        Push(Keys.Char('y'), Keys.Enter);       // yes: the visit ends

        var changes = await menu.ShowAsync(CancellationToken.None);

        Assert.True(changes.HasFlag(SettingsChanges.Profile), changes.ToString());
        Assert.Equal("architect", _settings.ProfileName);
        Assert.Contains(SettingsMenu.SwitchToAdvisorQuestion("architect"), Output);
        Assert.Contains(ChatScreen.KeptNotice, Output);
        Assert.False(pane.OverlayOpen);
        Assert.Equal("Switch to profile architect? The conversation is cleared.", SettingsMenu.SwitchToAdvisorQuestion("architect"));
        Assert.Equal("Switch to profile", SettingsMenu.AdvisorEntryRows[SettingsMenu.SwitchToAdvisorChoice]);
        Assert.Equal("Change the server", SettingsMenu.AdvisorEntryRows[SettingsMenu.ChangeAdvisorServerChoice]);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_SwitchToProfile_MidTurn_IsRefused_WithNoQuestion()
    {
        Profiles.Create(_dir, "architect", new AppSettingsData());
        _settings.Update(d => d.AdvisorList = [new AdvisorEntry("architect", "architect")]);
        var (menu, pane) = PaneMenu();
        OpenAdvisorList();
        FirstAdvisorAction(SettingsMenu.SwitchToAdvisorChoice);
        Push(Keys.Escape, Keys.Escape);

        var changes = await menu.ShowAsync(CancellationToken.None, midTurn: true);

        Assert.False(changes.HasFlag(SettingsChanges.Profile));
        Assert.Equal(Profiles.DefaultName, _settings.ProfileName);
        Assert.Contains(SettingsMenu.NotWhileReplyRunsNotice, Output);
        Assert.DoesNotContain(SettingsMenu.SwitchToAdvisorQuestion("architect"), Output);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_AMissingAdvisor_RefusesTheSwitchAndTheServer_WithTheirNotices()
    {
        int probes = 0;
        _settings.Update(d => d.AdvisorList = [new AdvisorEntry("gone", "critic")]);
        var (menu, pane) = PaneMenu();
        menu.AdvisorServerProbe = (_, _, _) =>
        {
            probes++;
            return Task.FromResult<IReadOnlyList<LlmServer>>([]);
        };
        OpenAdvisorList();
        FirstAdvisorAction(SettingsMenu.SwitchToAdvisorChoice);
        SameAdvisorAction(SettingsMenu.ChangeAdvisorServerChoice);
        Push(Keys.Escape, Keys.Escape);

        var changes = await menu.ShowAsync(CancellationToken.None);

        Assert.False(changes.HasFlag(SettingsChanges.Profile));
        Assert.Equal(Profiles.DefaultName, _settings.ProfileName);
        Assert.Contains(SettingsMenu.MissingAdvisorProfileNotice("gone"), Output);
        Assert.Contains(SettingsMenu.MissingAdvisorChangeNotice("gone"), Output);
        Assert.Equal(0, probes);
        Assert.Equal("(gone's profile no longer exists, so there is nothing to switch to)", SettingsMenu.MissingAdvisorProfileNotice("gone"));
        Assert.Equal("(gone's profile no longer exists, so there is nothing to change)", SettingsMenu.MissingAdvisorChangeNotice("gone"));
        pane.Dispose();
    }

    /// <summary>The profile deleted while the question stood (another instance, say): no switch, its notice, no exception.</summary>
    [Fact]
    public async Task OnThePane_SwitchToProfile_AProfileDeletedUnderTheQuestion_SwitchesNothing()
    {
        Profiles.Create(_dir, "architect", new AppSettingsData());
        _settings.Update(d => d.AdvisorList = [new AdvisorEntry("architect", "architect")]);
        var (menu, pane, input) = ClickablePaneMenu(cursorTop: 100);
        input.Push(Keys.Down, Keys.Down, Keys.Enter);   // the list opened on its row: architect
        input.Push([.. Enumerable.Repeat(Keys.Down, SettingsMenu.SwitchToAdvisorChoice), Keys.Enter]);
        bool deleted = false;
        input.OnWait = () =>
        {
            if (!deleted)
            {
                // The queue ran out at the question: the profile goes, then the yes.
                deleted = true;
                Directory.Delete(Profiles.Directory(_dir, "architect"), recursive: true);
                input.Push(Keys.Char('y'), Keys.Enter, Keys.Escape, Keys.Escape);
            }
        };

        var changes = await menu.ShowAsync(CancellationToken.None, midTurn: false, open: SettingsField.AdvisorList);

        Assert.True(deleted);
        Assert.False(changes.HasFlag(SettingsChanges.Profile));
        Assert.Equal(Profiles.DefaultName, _settings.ProfileName);
        Assert.Contains(SettingsMenu.SwitchToAdvisorQuestion("architect"), Output);
        Assert.Contains(SettingsMenu.MissingAdvisorProfileNotice("architect"), Output);
        pane.Dispose();
    }

    [Fact]
    public async Task OffThePane_SwitchToProfile_IsTypedPicks()
    {
        Profiles.Create(_dir, "architect", new AppSettingsData());
        _settings.Update(d => d.AdvisorList = [new AdvisorEntry("architect", "architect")]);
        Push(Keys.Down, Keys.Down, Keys.Enter);
        Down(SettingsMenu.SwitchToAdvisorChoice);
        Push(Keys.Enter);
        Push(Keys.Down, Keys.Enter);            // Yes

        var changes = await _menu.ShowAsync(CancellationToken.None, midTurn: false, open: SettingsField.AdvisorList);

        Assert.True(changes.HasFlag(SettingsChanges.Profile), changes.ToString());
        Assert.Equal("architect", _settings.ProfileName);
    }

    /// <summary>The other two ways into the list (pass 14): <c>/settings advisor list</c>'s search and <c>/settings changed</c> end on the switch too.</summary>
    [Theory]
    [InlineData("advisor list")]
    [InlineData(SettingsMenu.ChangedWord)]
    public async Task OnThePane_SwitchToProfile_FromTheSearchOrChanged_EndsTheVisit(string words)
    {
        Profiles.Create(_dir, "architect", new AppSettingsData());
        var fresh = new AppSettingsData();
        _settings.Update(d =>
        {
            foreach (var p in Settings.SettingsJsonContext.Default.AppSettingsData.Properties)
            {
                if (p.Get is not null && p.Set is not null)
                {
                    p.Set(d, p.Get(fresh));
                }
            }

            d.AdvisorList = [new AdvisorEntry("architect", "architect")];   // the one changed row
        });
        var (menu, pane) = PaneMenu();
        Push(Keys.Enter);                       // the row found: Advisor list
        FirstAdvisorAction(SettingsMenu.SwitchToAdvisorChoice);
        Push(Keys.Char('y'), Keys.Enter);

        var changes = await menu.ShowAsync(CancellationToken.None, midTurn: false, words: words);

        Assert.True(changes.HasFlag(SettingsChanges.Profile), changes.ToString());
        Assert.Equal("architect", _settings.ProfileName);
        Assert.False(pane.OverlayOpen);
        pane.Dispose();
    }

    // ── Change the server ───────────────────────────────────────────────────

    [Fact]
    public async Task OnThePane_ChangeTheServer_ListsTheAdvisorsServers_AndWritesItsProfileAlone_WithThePaneOpen()
    {
        Profiles.Create(_dir, "dba", new AppSettingsData { LlmUrl = "", LlmModel = "old", LlmReasoning = "low" });
        _settings.Update(d =>
        {
            d.AdvisorList = [new AdvisorEntry("dba", "architect")];
            d.LlmUrl = "http://mine:1/v1";
            d.LlmModel = "mine";
        });
        var (menu, pane) = PaneMenu();
        var seen = new List<(string? Model, Uri? InUse)>();
        menu.AdvisorServerProbe = (data, inUse, _) =>
        {
            seen.Add((data.LlmModel, inUse));
            return Task.FromResult<IReadOnlyList<LlmServer>>([ListedServer(Box1, "m-a", "m-b"), ListedServer(Box2, "x")]);
        };
        OpenAdvisorList();
        FirstAdvisorAction(SettingsMenu.ChangeAdvisorServerChoice);
        Push(Keys.Down, Keys.Enter);            // box1, under the borrow row (the cursor's, the URL blank)
        Push(Keys.Down, Keys.Enter);            // m-b, under m-a
        Push(Keys.Down, Keys.Enter);            // medium, under low
        Push(Keys.Enter);                       // dba again (the cursor stays on it)
        Down(SettingsMenu.ChangeAdvisorServerChoice);
        Push(Keys.Enter, Keys.Escape);          // its server page again, on box1: Esc writes nothing
        Push(Keys.Escape, Keys.Escape);

        var changes = await menu.ShowAsync(CancellationToken.None);

        var saved = AdvisorFile("dba");
        Assert.Equal((Box1, "m-b", "medium"), (saved.LlmUrl, saved.LlmModel, saved.LlmReasoning));
        Assert.Equal(("http://mine:1/v1", "mine"), (_settings.Current.LlmUrl, _settings.Current.LlmModel));   // the loaded profile untouched
        Assert.Equal(Profiles.DefaultName, _settings.ProfileName);
        Assert.False(changes.HasFlag(SettingsChanges.Profile));
        Assert.Equal([("old", (Uri?)null), ("m-b", new Uri(Box1))], seen);   // the advisor's own saved settings, each time
        string notice = SettingsMenu.AdvisorServerNotice(new AdvisorEntry("dba", "architect"), AdvisorServer.Of(saved));
        Assert.Contains(notice, Output);
        Assert.Contains(SettingsMenu.AdvisorServerSearchNotice("dba"), Output);
        Assert.Contains("dba › Change the server › model", Output);
        Assert.Contains("dba › Change the server › reasoning", Output);
        Assert.Contains(SettingsMenu.AdvisorBorrowServerRow, Output);
        Assert.DoesNotContain(SettingsMenu.AdvisorNoServerNotice("dba"), Output);
        AssertPaneNeverClosedAfter(SettingsMenu.AdvisorServerSearchNotice("dba"));
        Assert.False(pane.OverlayOpen);
        Assert.Equal("(looking for dba's servers…)", SettingsMenu.AdvisorServerSearchNotice("dba"));
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_ChangeTheServer_EscOnTheModelWritesNothing_AndEscOnReasoningKeepsIt()
    {
        Profiles.Create(_dir, "dba", new AppSettingsData { LlmUrl = Box2, LlmModel = "x", LlmReasoning = "high" });
        _settings.Update(d => d.AdvisorList = [new AdvisorEntry("dba", "architect")]);
        var (menu, pane) = PaneMenu();
        menu.AdvisorServerProbe = (_, _, _) => Task.FromResult<IReadOnlyList<LlmServer>>([ListedServer(Box1, "m-a", "m-b"), ListedServer(Box2, "x")]);
        OpenAdvisorList();
        FirstAdvisorAction(SettingsMenu.ChangeAdvisorServerChoice);
        Push(Keys.Up, Keys.Enter, Keys.Escape); // box1 (over box2, the cursor's): the model page, Esc
        Push(Keys.Enter);
        Down(SettingsMenu.ChangeAdvisorServerChoice);
        Push(Keys.Enter, Keys.Up, Keys.Enter);  // box1 again
        Push(Keys.Enter, Keys.Escape);          // m-a, then Esc on reasoning
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        var saved = AdvisorFile("dba");
        Assert.Equal((Box1, "m-a", "high"), (saved.LlmUrl, saved.LlmModel, saved.LlmReasoning));
        Assert.Contains(SettingsMenu.AdvisorServerNotice(new AdvisorEntry("dba", "architect"), AdvisorServer.Of(saved)), Output);
        pane.Dispose();
    }

    /// <summary>A server with no list (a Docker container) saves a blank model; an embedded row is its own model, with no model page.</summary>
    [Fact]
    public async Task OnThePane_ChangeTheServer_ADockerRowSavesABlankModel_AndAnEmbeddedRowItsId()
    {
        Profiles.Create(_dir, "dba", new AppSettingsData { LlmUrl = Box1, LlmModel = "m-a", LlmReasoning = "low" });
        Profiles.Create(_dir, "ux", new AppSettingsData { LlmUrl = Box1, LlmModel = "m-a", LlmReasoning = "low" });
        _settings.Update(d => d.AdvisorList = [new AdvisorEntry("dba", "architect"), new AdvisorEntry("ux", "designer")]);
        var (menu, pane) = PaneMenu();
        var docker = new LlmServer(new Uri("docker:vllm"), "vllm", new ProbeResult(true, [], "", null, null));
        var embedded = new LlmServer(NeonSidekick.EmbeddedLlm.EmbeddedEndpoint.BaseUrl, "gemma", new ProbeResult(true, ["gemma-4-e2b"], "", null, null));
        menu.AdvisorServerProbe = (_, _, _) => Task.FromResult<IReadOnlyList<LlmServer>>([ListedServer(Box1, "m-a"), docker, embedded]);
        OpenAdvisorList();
        FirstAdvisorAction(SettingsMenu.ChangeAdvisorServerChoice);
        Push(Keys.Down, Keys.Enter);            // docker, under box1 (the cursor's)
        Push(Keys.Enter);                       // reasoning: low, as it was
        Push(Keys.Down, Keys.Enter);            // ux
        Down(SettingsMenu.ChangeAdvisorServerChoice);
        Push(Keys.Enter, Keys.Down, Keys.Down, Keys.Enter);   // the embedded row
        Push(Keys.Enter);                       // reasoning
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        var dba = AdvisorFile("dba");
        Assert.Equal(("docker:vllm", ""), (dba.LlmUrl, dba.LlmModel));
        var ux = AdvisorFile("ux");
        Assert.Equal((NeonSidekick.EmbeddedLlm.EmbeddedEndpoint.BaseUrl.ToString(), "gemma-4-e2b"), (ux.LlmUrl, ux.LlmModel));
        Assert.DoesNotContain("ux › Change the server › model", Output);
        pane.Dispose();
    }

    /// <summary>
    /// An advisor saved with a running container's loopback address (2026-11-23, hand check 4): the scan drops that row for the container's own,
    /// so the cursor lands on the Docker row by its port, and picking it keeps the saved model (the container lists none).
    /// </summary>
    [Fact]
    public async Task OnThePane_ChangeTheServer_ASavedContainerPort_LandsOnItsDockerRow()
    {
        Profiles.Create(_dir, "qa", new AppSettingsData { LlmUrl = "http://localhost:30000/v1", LlmModel = "qwen", LlmReasoning = "low" });
        _settings.Update(d => d.AdvisorList = [new AdvisorEntry("qa", "tester")]);
        var (menu, pane) = PaneMenu();
        var docker = new LlmServer(new Uri("docker:sglang"), "sglang", new ProbeResult(true, [], "", null, null)) { HostPorts = [30000] };
        menu.AdvisorServerProbe = (_, _, _) => Task.FromResult<IReadOnlyList<LlmServer>>([ListedServer(Box1, "m-a"), docker]);
        OpenAdvisorList();
        FirstAdvisorAction(SettingsMenu.ChangeAdvisorServerChoice);
        Push(Keys.Enter);                       // the cursor's row: the container
        Push(Keys.Enter);                       // reasoning: low, as it was
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        var qa = AdvisorFile("qa");
        Assert.Equal(("docker:sglang", "qwen"), (qa.LlmUrl, qa.LlmModel));
        pane.Dispose();
    }

    /// <summary>A saved loopback address no row stands for (2026-11-23) opens on the first server row, never the borrow row.</summary>
    [Fact]
    public async Task OnThePane_ChangeTheServer_ASavedAddressNoRowStandsFor_OpensOnTheFirstServer()
    {
        Profiles.Create(_dir, "qa", new AppSettingsData { LlmUrl = "http://localhost:9999/v1", LlmModel = "gone", LlmReasoning = "low" });
        _settings.Update(d => d.AdvisorList = [new AdvisorEntry("qa", "tester")]);
        var (menu, pane) = PaneMenu();
        menu.AdvisorServerProbe = (_, _, _) => Task.FromResult<IReadOnlyList<LlmServer>>([ListedServer(Box1, "m-a")]);
        OpenAdvisorList();
        FirstAdvisorAction(SettingsMenu.ChangeAdvisorServerChoice);
        Push(Keys.Enter);                       // the cursor's row: box1, not the borrow row
        Push(Keys.Enter);                       // its model: m-a
        Push(Keys.Enter);                       // reasoning
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        var qa = AdvisorFile("qa");
        Assert.Equal((Box1, "m-a"), (qa.LlmUrl, qa.LlmModel));
        pane.Dispose();
    }

    /// <summary>The /server twin (2026-11-23): a session on a container's loopback address opens the picker on the container's row.</summary>
    [Fact]
    public async Task ServerPicker_ASessionOnAContainersPort_OpensOnItsDockerRow()
    {
        var (menu, pane) = PaneMenu();
        var docker = new LlmServer(new Uri("docker:sglang"), "sglang", new ProbeResult(true, [], "", null, null)) { HostPorts = [30000] };
        Push(Keys.Enter);

        var picked = await menu.PickServerAsync([ListedServer(Box1, "m-a"), docker], new Uri("http://localhost:30000/v1"), SettingsMenu.ServerTitle, CancellationToken.None);

        Assert.Same(docker, picked);
        pane.Dispose();
    }

    /// <summary>The port match (2026-11-23) is loopback only, and only a row that carries the port.</summary>
    [Fact]
    public void DockerRowOnPort_MatchesALoopbackPort_ItsRowCarries()
    {
        var plain = new LlmServer(new Uri("http://box:30000/v1"), "SGLang", new ProbeResult(true, [], "", null, null));
        var docker = new LlmServer(new Uri("docker:sglang"), "sglang", new ProbeResult(true, [], "", null, null)) { HostPorts = [30000] };

        Assert.Equal(1, SettingsMenu.DockerRowOnPort([plain, docker], new Uri("http://localhost:30000/v1")));
        Assert.Equal(1, SettingsMenu.DockerRowOnPort([plain, docker], new Uri("http://127.0.0.1:30000/v1")));
        Assert.Null(SettingsMenu.DockerRowOnPort([plain, docker], new Uri("http://box:30000/v1")));
        Assert.Null(SettingsMenu.DockerRowOnPort([plain, docker], new Uri("http://localhost:8000/v1")));
        Assert.Null(SettingsMenu.DockerRowOnPort([plain, docker], null));
    }

    /// <summary>A new URL's model page lists that server's ids alone: the advisor's model on another server is never offered as listed.</summary>
    [Fact]
    public async Task OnThePane_ChangeTheServer_ANewUrl_ListsNoStaleModel()
    {
        Profiles.Create(_dir, "dba", new AppSettingsData { LlmUrl = Box2, LlmModel = "stale-model", LlmReasoning = "low" });
        _settings.Update(d => d.AdvisorList = [new AdvisorEntry("dba", "architect")]);
        var (menu, pane) = PaneMenu();
        menu.AdvisorServerProbe = (_, _, _) => Task.FromResult<IReadOnlyList<LlmServer>>([ListedServer(Box1, "m-a"), ListedServer(Box2, "stale-model")]);
        OpenAdvisorList();
        FirstAdvisorAction(SettingsMenu.ChangeAdvisorServerChoice);
        Push(Keys.Up, Keys.Enter);              // box1
        Push(Keys.Escape);                      // the model page: Esc
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        // The model page alone: from its crumb to the list's next draw (whose band names the advisor's saved model).
        string crumb = "dba › Change the server › model";
        int from = Output.LastIndexOf(crumb, StringComparison.Ordinal) + crumb.Length;
        string page = Output[from..Output.IndexOf(SettingsMenu.FieldName(SettingsField.AdvisorList), from, StringComparison.Ordinal)];
        Assert.Contains("m-a", page);
        Assert.DoesNotContain("stale-model", page);
        var saved = AdvisorFile("dba");
        Assert.Equal((Box2, "stale-model"), (saved.LlmUrl, saved.LlmModel));   // nothing written
        pane.Dispose();
    }

    /// <summary>
    /// A URL saved by hand with no <c>/v1</c> (2026-11-23, review #14): the cursor lands on the server listed for it, and its saved model on the
    /// model page, never on the borrow row.
    /// </summary>
    [Fact]
    public async Task OnThePane_ChangeTheServer_AHandTypedUrl_LandsOnItsServer_AndItsModel()
    {
        Profiles.Create(_dir, "dba", new AppSettingsData { LlmUrl = "http://box1:8000", LlmModel = "m-b", LlmReasoning = "high" });
        _settings.Update(d => d.AdvisorList = [new AdvisorEntry("dba", "architect")]);
        var (menu, pane) = PaneMenu();
        menu.AdvisorServerProbe = (_, _, _) => Task.FromResult<IReadOnlyList<LlmServer>>([ListedServer(Box2, "x"), ListedServer(Box1, "m-a", "m-b")]);
        OpenAdvisorList();
        FirstAdvisorAction(SettingsMenu.ChangeAdvisorServerChoice);
        Push(Keys.Enter);                       // the server on the cursor
        Push(Keys.Enter);                       // the model on the cursor
        Push(Keys.Enter);                       // the reasoning on the cursor
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        var saved = AdvisorFile("dba");
        Assert.Equal((Box1, "m-b", "high"), (saved.LlmUrl, saved.LlmModel, saved.LlmReasoning));
        pane.Dispose();
    }

    /// <summary>
    /// The borrow row: first, offered when no server answers, and a pick saves a blank URL and a blank model (2026-11-23, review #61, the
    /// user's pick: the session's server answers with its own model, as <c>/server</c> clears the model on a new URL), the reasoning kept.
    /// </summary>
    [Fact]
    public async Task OnThePane_ChangeTheServer_TheBorrowRow_SavesABlankUrlAndModel_AndStandsWithNoServer()
    {
        Profiles.Create(_dir, "dba", new AppSettingsData { LlmUrl = Box1, LlmModel = "m-a", LlmReasoning = "high" });
        _settings.Update(d => d.AdvisorList = [new AdvisorEntry("dba", "architect")]);
        var (menu, pane) = PaneMenu();
        menu.AdvisorServerProbe = (_, _, _) => Task.FromResult<IReadOnlyList<LlmServer>>([]);
        OpenAdvisorList();
        FirstAdvisorAction(SettingsMenu.ChangeAdvisorServerChoice);
        Push(Keys.Enter);                       // the borrow row, alone
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        var saved = AdvisorFile("dba");
        Assert.Equal(("", "", "high"), (saved.LlmUrl, saved.LlmModel, saved.LlmReasoning));
        Assert.Contains(SettingsMenu.AdvisorNoServerNotice("dba"), Output);
        Assert.Contains(SettingsMenu.AdvisorBorrowServerRow, Output);
        Assert.Contains(SettingsMenu.AdvisorBorrowServerBand, Output);
        Assert.Contains(SettingsMenu.AdvisorServerNotice(new AdvisorEntry("dba", "architect"), AdvisorServer.Of(saved)), Output);
        Assert.DoesNotContain("dba › Change the server › reasoning", Output);
        Assert.Equal("(no server answered for dba; turn one on in its own settings)", SettingsMenu.AdvisorNoServerNotice("dba"));
        Assert.Equal("Your session's server (none saved)", SettingsMenu.AdvisorBorrowServerRow);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_ChangeTheServer_ACorruptProfile_IsReported_AndLeftAsItWas()
    {
        Profiles.Create(_dir, "dba", new AppSettingsData());
        string file = Profiles.ProfileFile(_dir, "dba");
        File.WriteAllText(file, "{ not json");
        _settings.Update(d => d.AdvisorList = [new AdvisorEntry("dba", "architect")]);
        int probes = 0;
        var (menu, pane) = PaneMenu();
        menu.AdvisorServerProbe = (_, _, _) =>
        {
            probes++;
            return Task.FromResult<IReadOnlyList<LlmServer>>([]);
        };
        OpenAdvisorList();
        FirstAdvisorAction(SettingsMenu.ChangeAdvisorServerChoice);
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal("{ not json", File.ReadAllText(file));
        Assert.Equal(0, probes);
        Assert.Contains("Could not read dba's profile.json: ", Output);
        pane.Dispose();
    }

    /// <summary>The profile corrupted or deleted while the picks stood (the probe does it): nothing written, and said.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OnThePane_ChangeTheServer_AProfileChangedUnderThePicks_WritesNothing(bool delete)
    {
        Profiles.Create(_dir, "dba", new AppSettingsData { LlmUrl = "", LlmModel = "old" });
        string file = Profiles.ProfileFile(_dir, "dba");
        _settings.Update(d => d.AdvisorList = [new AdvisorEntry("dba", "architect")]);
        var (menu, pane) = PaneMenu();
        menu.AdvisorServerProbe = (_, _, _) =>
        {
            if (delete)
            {
                Directory.Delete(Profiles.Directory(_dir, "dba"), recursive: true);
            }
            else
            {
                File.WriteAllText(file, "{ not json");
            }

            return Task.FromResult<IReadOnlyList<LlmServer>>([]);
        };
        OpenAdvisorList();
        FirstAdvisorAction(SettingsMenu.ChangeAdvisorServerChoice);
        Push(Keys.Enter);                       // the borrow row
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        if (delete)
        {
            Assert.False(File.Exists(file));
            Assert.Contains(SettingsMenu.MissingAdvisorChangeNotice("dba"), Output);
        }
        else
        {
            Assert.Equal("{ not json", File.ReadAllText(file));
            Assert.Contains("Could not read dba's profile.json: ", Output);
        }

        Assert.DoesNotContain("Could not save", Output);
        pane.Dispose();
    }

    [Fact]
    public async Task OffThePane_ChangeTheServer_IsTypedPicks()
    {
        Profiles.Create(_dir, "dba", new AppSettingsData { LlmUrl = "", LlmModel = "old", LlmReasoning = "low" });
        _settings.Update(d => d.AdvisorList = [new AdvisorEntry("dba", "architect")]);
        _menu.AdvisorServerProbe = (_, _, _) => Task.FromResult<IReadOnlyList<LlmServer>>([ListedServer(Box1, "m-a", "m-b")]);
        Push(Keys.Down, Keys.Down, Keys.Enter);
        Down(SettingsMenu.ChangeAdvisorServerChoice);
        Push(Keys.Enter);
        Push(Keys.Down, Keys.Enter);            // box1
        Push(Keys.Enter);                       // m-a
        Push(Keys.Enter);                       // low
        Push(Keys.Escape, Keys.Escape);

        await _menu.ShowAsync(CancellationToken.None, midTurn: false, open: SettingsField.AdvisorList);

        var saved = AdvisorFile("dba");
        Assert.Equal((Box1, "m-a", "low"), (saved.LlmUrl, saved.LlmModel, saved.LlmReasoning));
    }

    /// <summary>/server's own list keeps no borrow row: the lead is the advisor's page alone.</summary>
    [Fact]
    public async Task OnThePane_ServersOwnPicker_HasNoBorrowRow()
    {
        var (menu, pane) = PaneMenu();
        Push(Keys.Escape);

        await menu.PickServerAsync([ListedServer(Box1, "m-a")], null, SettingsMenu.ServerTitle, CancellationToken.None);

        Assert.DoesNotContain(SettingsMenu.AdvisorBorrowServerRow, Output);
        Assert.False(pane.OverlayOpen);
        pane.Dispose();
    }
    /// <summary>An advisor's page (2026-11-23, the user's ask): rules after the persona and after the reasoning, each page row mapped back to its choice.</summary>
    [Fact]
    public void AdvisorEntryPage_RulesAtThreeAndSeven_RowsMapBackToTheirChoices()
    {
        var rows = SettingsMenu.AdvisorEntryPageRows();

        Assert.Equal(SettingsMenu.AdvisorEntryRows.Count + 2, rows.Count);
        Assert.Equal(new HashSet<int> { 3, 7 }, SettingsMenu.AdvisorEntryRuleRows());
        Assert.Equal(UI.SectionRule.Bare, rows[3]);
        Assert.Equal(UI.SectionRule.Bare, rows[7]);
        Assert.Equal(SettingsMenu.ChangeAdvisorServerChoice, SettingsMenu.AdvisorChoiceOfRow(4));
        Assert.Equal(SettingsMenu.ChangeAdvisorReasoningChoice, SettingsMenu.AdvisorChoiceOfRow(6));
        Assert.Equal(SettingsMenu.SwitchToAdvisorChoice, SettingsMenu.AdvisorChoiceOfRow(8));
        Assert.Equal(SettingsMenu.RemoveAdvisorChoice, SettingsMenu.AdvisorChoiceOfRow(9));
        Assert.Equal(2, SettingsMenu.AdvisorChoiceOfRow(2));
        Assert.Equal(-1, SettingsMenu.AdvisorChoiceOfRow(3));
        Assert.Equal(-1, SettingsMenu.AdvisorChoiceOfRow(-1));
        for (int choice = 0; choice < SettingsMenu.AdvisorEntryRows.Count; choice++)
        {
            int row = Enumerable.Range(0, rows.Count).Single(r => SettingsMenu.AdvisorChoiceOfRow(r) == choice);
            Assert.Equal(Spectre.Console.Markup.Escape(SettingsMenu.AdvisorEntryRows[choice]), rows[row]);
        }
    }
}
