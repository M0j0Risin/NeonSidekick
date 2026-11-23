using NeonSidekick.Advisors;
using NeonSidekick.App;
using NeonSidekick.Llm;
using NeonSidekick.Settings;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// An advisor's Change the model and Change the reasoning (2026-11-22, the user's ask: "similar to how we added Change the server"):
/// one value written to the advisor's <c>profile.json</c> alone, the pane kept open; and the model each advisor answers on named on its
/// line where the session knows it (<see cref="SettingsMenu.AdvisorLinkedModel"/>).
/// </summary>
public partial class SettingsMenuTests
{
    private static Task<(ProbeResult? Listed, string? Problem)> Listed(params string[] ids) =>
        Task.FromResult<(ProbeResult?, string?)>((new ProbeResult(true, ids, ""), null));

    private static string OneSpaced(string text) => System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");

    private void OneAdvisor(AppSettingsData data, string profile = "dba")
    {
        Profiles.Create(_dir, profile, data);
        _settings.Update(d => d.AdvisorList = [new AdvisorEntry(profile, "architect")]);
    }

    // ── Change the model ────────────────────────────────────────────────────

    [Fact]
    public async Task OnThePane_ChangeTheModel_ListsTheAdvisorsModels_AndWritesTheModelAlone_WithThePaneOpen()
    {
        OneAdvisor(new AppSettingsData { LlmUrl = Box1, LlmModel = "m-b", LlmReasoning = "low" });
        _settings.Update(d => { d.LlmUrl = "http://mine:1/v1"; d.LlmModel = "mine"; });
        var (menu, pane) = PaneMenu();
        var seen = new List<string?>();
        menu.AdvisorModelLister = (data, _) =>
        {
            seen.Add(data.LlmUrl);
            return Listed("m-c", "m-a", "m-b");
        };
        OpenAdvisorList();
        FirstAdvisorAction(SettingsMenu.ChangeAdvisorModelChoice);
        Push(Keys.Down, Keys.Enter);            // m-c, under m-b (the cursor's; the list A to Z)
        Push(Keys.Escape, Keys.Escape);

        var changes = await menu.ShowAsync(CancellationToken.None);

        var saved = AdvisorFile("dba");
        Assert.Equal((Box1, "m-c", "low"), (saved.LlmUrl, saved.LlmModel, saved.LlmReasoning));
        Assert.Equal(("http://mine:1/v1", "mine"), (_settings.Current.LlmUrl, _settings.Current.LlmModel));   // the loaded profile untouched
        Assert.Equal(Profiles.DefaultName, _settings.ProfileName);
        Assert.False(changes.HasFlag(SettingsChanges.Profile));
        Assert.Equal([Box1], seen);   // the advisor's own saved settings
        Assert.Contains(SettingsMenu.AdvisorServerNotice(new AdvisorEntry("dba", "architect"), AdvisorServer.Of(saved)), Output);
        Assert.Contains(SettingsMenu.AdvisorModelSearchNotice("dba"), Output);
        Assert.Contains("dba › Change the model", Output);
        AssertPaneNeverClosedAfter(SettingsMenu.AdvisorModelSearchNotice("dba"));
        Assert.False(pane.OverlayOpen);
        Assert.Equal("(asking dba's server for its models…)", SettingsMenu.AdvisorModelSearchNotice("dba"));
        Assert.Equal("Change the model", SettingsMenu.AdvisorEntryRows[SettingsMenu.ChangeAdvisorModelChoice]);
        Assert.Equal("Change the reasoning", SettingsMenu.AdvisorEntryRows[SettingsMenu.ChangeAdvisorReasoningChoice]);
        pane.Dispose();
    }

    /// <summary>Esc, and a pick of the model saved (a hand-edited <c> m-b </c> is the listed <c>m-b</c>), write and say nothing.</summary>
    [Fact]
    public async Task OnThePane_ChangeTheModel_EscAndTheSavedModel_WriteNothing()
    {
        OneAdvisor(new AppSettingsData { LlmUrl = Box1, LlmModel = " m-b ", LlmReasoning = "low" });
        string file = Profiles.ProfileFile(_dir, "dba");
        string before = File.ReadAllText(file);
        var (menu, pane) = PaneMenu();
        menu.AdvisorModelLister = (_, _) => Listed("m-a", "m-b");
        OpenAdvisorList();
        FirstAdvisorAction(SettingsMenu.ChangeAdvisorModelChoice);
        Push(Keys.Escape);                      // the model page: Esc
        SameAdvisorAction(SettingsMenu.ChangeAdvisorModelChoice);
        Push(Keys.Enter);                       // m-b, the cursor's: the one saved
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(before, File.ReadAllText(file));
        Assert.DoesNotContain(SettingsMenu.FieldName(SettingsField.AdvisorList) + ": dba — architect · http", Output);
        pane.Dispose();
    }

    /// <summary>Nothing to list: the reason said and no page opened; an empty list says its detail, no lister the no-server reason.</summary>
    [Theory]
    [InlineData("problem")]
    [InlineData("empty")]
    [InlineData("none")]
    public async Task OnThePane_ChangeTheModel_WithNothingToList_SaysWhy_AndOpensNoPage(string kind)
    {
        OneAdvisor(new AppSettingsData { LlmUrl = "docker:vllm", LlmModel = "" });
        var (menu, pane) = PaneMenu();
        menu.AdvisorModelLister = kind switch
        {
            "problem" => (_, _) => Task.FromResult<(ProbeResult?, string?)>((null, AdvisorText.ModelListDockerOne)),
            "empty" => (_, _) => Task.FromResult<(ProbeResult?, string?)>((new ProbeResult(false, [], "0 embedded models"), null)),
            _ => null,
        };
        OpenAdvisorList();
        FirstAdvisorAction(SettingsMenu.ChangeAdvisorModelChoice);
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        string why = kind switch { "problem" => AdvisorText.ModelListDockerOne, "empty" => "0 embedded models", _ => LlmSession.BotLinkNoServer };
        Assert.Contains(SettingsMenu.AdvisorNoModelsNotice("dba", why), Output);
        Assert.DoesNotContain("dba › Change the model", Output);
        Assert.DoesNotContain(SettingsMenu.AdvisorModelSearchNotice("dba"), Output);   // a Docker advisor answers at once: nothing asked
        Assert.Equal("(no models for dba: x)", SettingsMenu.AdvisorNoModelsNotice("dba", "x"));
        pane.Dispose();
    }

    /// <summary>The search notice stands only where a server is asked: a URL advisor's, not a Claude Code one's.</summary>
    [Fact]
    public async Task OnThePane_ChangeTheModel_SaysItAsksOnlyWhereAServerIsAsked()
    {
        Profiles.Create(_dir, "dba", new AppSettingsData { LlmUrl = "claude-code", LlmModel = "" });
        Profiles.Create(_dir, "ux", new AppSettingsData { LlmUrl = Box1, LlmModel = "m-a" });
        _settings.Update(d => d.AdvisorList = [new AdvisorEntry("dba", "architect"), new AdvisorEntry("ux", "designer")]);
        var (menu, pane) = PaneMenu();
        menu.AdvisorModelLister = (_, _) => Listed("m-a");
        OpenAdvisorList();
        FirstAdvisorAction(SettingsMenu.ChangeAdvisorModelChoice);
        Push(Keys.Escape);
        Push(Keys.Down);                        // ux
        SameAdvisorAction(SettingsMenu.ChangeAdvisorModelChoice);
        Push(Keys.Escape, Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.DoesNotContain(SettingsMenu.AdvisorModelSearchNotice("dba"), Output);
        Assert.Contains(SettingsMenu.AdvisorModelSearchNotice("ux"), Output);
        pane.Dispose();
    }

    /// <summary>The profile deleted, broken or moved to another server while the list stood: nothing written, and said.</summary>
    [Theory]
    [InlineData("delete")]
    [InlineData("break")]
    [InlineData("move")]
    public async Task OnThePane_ChangeTheModel_AProfileChangedUnderThePick_WritesNothing(string how)
    {
        OneAdvisor(new AppSettingsData { LlmUrl = Box1, LlmModel = "m-a" });
        string file = Profiles.ProfileFile(_dir, "dba");
        var (menu, pane) = PaneMenu();
        menu.AdvisorModelLister = (_, _) =>
        {
            switch (how)
            {
                case "delete":
                    Directory.Delete(Profiles.Directory(_dir, "dba"), recursive: true);
                    break;
                case "break":
                    File.WriteAllText(file, "{ not json");
                    break;
                default:
                    Profiles.WriteProfileFile(file, new AppSettingsData { LlmUrl = Box2, LlmModel = "x" });
                    break;
            }

            return Listed("m-a", "m-b");
        };
        OpenAdvisorList();
        FirstAdvisorAction(SettingsMenu.ChangeAdvisorModelChoice);
        Push(Keys.Down, Keys.Enter);            // m-b
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        switch (how)
        {
            case "delete":
                Assert.False(File.Exists(file));
                Assert.Contains(SettingsMenu.MissingAdvisorChangeNotice("dba"), Output);
                break;
            case "break":
                Assert.Equal("{ not json", File.ReadAllText(file));
                Assert.Contains("Could not read dba's profile.json: ", Output);
                break;
            default:
                Assert.Equal((Box2, "x"), (AdvisorFile("dba").LlmUrl, AdvisorFile("dba").LlmModel));
                Assert.Contains(SettingsMenu.AdvisorChangedMeanwhileNotice("dba"), Output);
                Assert.Equal("(dba's server changed while you picked; open Change the model again)", SettingsMenu.AdvisorChangedMeanwhileNotice("dba"));
                break;
        }

        Assert.DoesNotContain("Could not save", Output);
        pane.Dispose();
    }

    [Fact]
    public async Task OffThePane_ChangeTheModel_IsTypedPicks()
    {
        OneAdvisor(new AppSettingsData { LlmUrl = Box1, LlmModel = "m-a" });
        _menu.AdvisorModelLister = (_, _) => Listed("m-a", "m-b");
        Push(Keys.Down, Keys.Down, Keys.Enter);
        Down(SettingsMenu.ChangeAdvisorModelChoice);
        Push(Keys.Enter);
        Push(Keys.Down, Keys.Enter);            // m-b
        Push(Keys.Escape, Keys.Escape);

        await _menu.ShowAsync(CancellationToken.None, midTurn: false, open: SettingsField.AdvisorList);

        Assert.Equal("m-b", AdvisorFile("dba").LlmModel);
    }

    /// <summary>
    /// An embedded pick the advisor won't answer on (another model runs under parent-server, the linked model): the notice says so,
    /// after Change the model and after Change the server's embedded row; a linked model that is the pick adds nothing.
    /// </summary>
    [Fact]
    public async Task OnThePane_AnEmbeddedPickItWontAnswerOn_SaysSo_InTheNotice()
    {
        string embedded = NeonSidekick.EmbeddedLlm.EmbeddedEndpoint.BaseUrl.ToString();
        Profiles.Create(_dir, "dba", new AppSettingsData { LlmUrl = embedded, LlmModel = "gemma-a" });
        Profiles.Create(_dir, "ux", new AppSettingsData { LlmUrl = embedded, LlmModel = "gemma-a" });
        Profiles.Create(_dir, "qa", new AppSettingsData { LlmUrl = embedded, LlmModel = "gemma-a" });
        _settings.Update(d => d.AdvisorList = [new AdvisorEntry("dba", "architect"), new AdvisorEntry("qa", "tester"), new AdvisorEntry("ux", "designer")]);
        var (menu, pane) = PaneMenu();
        menu.AdvisorModelLister = (_, _) => Listed("gemma-a", "gemma-b");
        menu.AdvisorLinkedModel = data => data.LlmModel == "gemma-b" ? "models/gemma-running" : data.LlmModel;
        var row = new LlmServer(NeonSidekick.EmbeddedLlm.EmbeddedEndpoint.BaseUrl, "gemma", new ProbeResult(true, ["gemma-b"], "", null, null));
        menu.AdvisorServerProbe = (_, _, _) => Task.FromResult<IReadOnlyList<LlmServer>>([row]);
        OpenAdvisorList();
        FirstAdvisorAction(SettingsMenu.ChangeAdvisorModelChoice);
        Push(Keys.Down, Keys.Enter);            // dba: gemma-b, which the running model stands in for
        Push(Keys.Down);                        // qa
        SameAdvisorAction(SettingsMenu.ChangeAdvisorServerChoice);
        Push(Keys.Down, Keys.Enter);            // the embedded row (gemma-b), under the borrow row
        Push(Keys.Enter);                       // reasoning
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        string note = SettingsMenu.AdvisorSharedModelNote("gemma-running");
        string flat = OneSpaced(Output);   // the notice is longer than a pane line: the status line wraps it
        Assert.Contains(OneSpaced(SettingsMenu.AdvisorServerNotice(new AdvisorEntry("dba", "architect"), new AdvisorServerFacts(AdvisorServer.Embedded, "gemma-running")) + note), flat);
        Assert.Contains(OneSpaced(SettingsMenu.AdvisorServerNotice(new AdvisorEntry("qa", "tester"), new AdvisorServerFacts(AdvisorServer.Embedded, "gemma-running")) + note), flat);
        Assert.Equal(" (it answers on gemma-running while that runs: Advisor embedded mode parent-server)", note);
        Assert.Equal(("gemma-b", "gemma-b"), (AdvisorFile("dba").LlmModel, AdvisorFile("qa").LlmModel));
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_AnEmbeddedPickItAnswersOn_AddsNoNote()
    {
        string embedded = NeonSidekick.EmbeddedLlm.EmbeddedEndpoint.BaseUrl.ToString();
        OneAdvisor(new AppSettingsData { LlmUrl = embedded, LlmModel = "gemma-a" });
        var (menu, pane) = PaneMenu();
        menu.AdvisorModelLister = (_, _) => Listed("gemma-a", "gemma-b");
        menu.AdvisorLinkedModel = data => data.LlmModel;
        OpenAdvisorList();
        FirstAdvisorAction(SettingsMenu.ChangeAdvisorModelChoice);
        Push(Keys.Down, Keys.Enter);
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains(SettingsMenu.AdvisorServerNotice(new AdvisorEntry("dba", "architect"), new AdvisorServerFacts(AdvisorServer.Embedded, "gemma-b")), Output);
        Assert.DoesNotContain("while that runs", Output);
        pane.Dispose();
    }

    // ── Change the reasoning ────────────────────────────────────────────────

    [Fact]
    public async Task OnThePane_ChangeTheReasoning_WritesTheLevelAlone_WithThePaneOpen()
    {
        OneAdvisor(new AppSettingsData { LlmUrl = Box1, LlmModel = "m-a", LlmReasoning = "low" });
        _settings.Update(d => d.LlmReasoning = "high");
        var (menu, pane) = PaneMenu();
        OpenAdvisorList();
        FirstAdvisorAction(SettingsMenu.ChangeAdvisorReasoningChoice);
        Push(Keys.Down, Keys.Enter);            // medium, under low (the cursor's)
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        var saved = AdvisorFile("dba");
        Assert.Equal((Box1, "m-a", "medium"), (saved.LlmUrl, saved.LlmModel, saved.LlmReasoning));
        Assert.Equal("high", _settings.Current.LlmReasoning);
        Assert.Contains(SettingsMenu.AdvisorReasoningNotice("dba", "medium"), Output);
        Assert.Contains("dba › Change the reasoning", Output);
        Assert.Equal("Advisor list: dba's reasoning: medium", SettingsMenu.AdvisorReasoningNotice("dba", "medium"));
        Assert.False(pane.OverlayOpen);
        pane.Dispose();
    }

    /// <summary>
    /// The cursor opens on the level in force: a hand-edited <c> Medium </c> is medium (Enter there writes nothing), a word that is none
    /// of the levels is <c>none</c>, and a pick of <c>none</c> over it writes a clean level.
    /// </summary>
    [Theory]
    [InlineData(" Medium ", " Medium ", false)]
    [InlineData("fast", "none", true)]
    [InlineData("", "none", true)]
    public async Task OnThePane_ChangeTheReasoning_OpensOnTheLevelInForce(string saved, string after, bool written)
    {
        OneAdvisor(new AppSettingsData { LlmUrl = Box1, LlmModel = "m-a", LlmReasoning = saved });
        var (menu, pane) = PaneMenu();
        OpenAdvisorList();
        FirstAdvisorAction(SettingsMenu.ChangeAdvisorReasoningChoice);
        Push(Keys.Enter);                       // the cursor's level
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(after, AdvisorFile("dba").LlmReasoning);
        Assert.Equal(written, Output.Contains(SettingsMenu.AdvisorReasoningNotice("dba", after.Trim().ToLowerInvariant()), StringComparison.Ordinal));
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_ChangeTheReasoning_EscWritesNothing_AndUnderAReplyIsAllowed()
    {
        OneAdvisor(new AppSettingsData { LlmUrl = Box1, LlmModel = "m-a", LlmReasoning = "low" });
        var (menu, pane) = PaneMenu();
        OpenAdvisorList();
        FirstAdvisorAction(SettingsMenu.ChangeAdvisorReasoningChoice);
        Push(Keys.Escape);                      // nothing written
        SameAdvisorAction(SettingsMenu.ChangeAdvisorReasoningChoice);
        Push(Keys.Down, Keys.Down, Keys.Enter); // high
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None, midTurn: true);

        Assert.Equal("high", AdvisorFile("dba").LlmReasoning);
        Assert.Contains(SettingsMenu.AdvisorReasoningNotice("dba", "high"), Output);
        Assert.DoesNotContain(SettingsMenu.NotWhileReplyRunsNotice, Output);
        pane.Dispose();
    }

    // ── Refusals the three changes share ────────────────────────────────────

    /// <summary>A missing profile, and an entry naming the loaded profile (a <c>/profile new</c> seed keeps the list): nothing asked or written.</summary>
    [Theory]
    [InlineData(SettingsMenu.ChangeAdvisorServerChoice)]
    [InlineData(SettingsMenu.ChangeAdvisorModelChoice)]
    [InlineData(SettingsMenu.ChangeAdvisorReasoningChoice)]
    public async Task OnThePane_TheChanges_RefuseAMissingProfile_AndTheLoadedOne(int action)
    {
        string loaded = _settings.ProfileName;
        string file = Profiles.ProfileFile(_dir, loaded);
        if (!File.Exists(file))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            Profiles.WriteProfileFile(file, new AppSettingsData());
        }

        string before = File.ReadAllText(file);
        _settings.Update(d => d.AdvisorList = [new AdvisorEntry("gone", "critic"), new AdvisorEntry(loaded.ToUpperInvariant(), "me")]);
        int asked = 0;
        var (menu, pane) = PaneMenu();
        menu.AdvisorServerProbe = (_, _, _) =>
        {
            asked++;
            return Task.FromResult<IReadOnlyList<LlmServer>>([]);
        };
        menu.AdvisorModelLister = (_, _) =>
        {
            asked++;
            return Listed("m-a");
        };
        OpenAdvisorList();
        FirstAdvisorAction(action);             // gone
        Push(Keys.Down);                        // the loaded one
        SameAdvisorAction(action);
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(0, asked);
        Assert.Contains(SettingsMenu.MissingAdvisorChangeNotice("gone"), Output);
        Assert.Contains(SettingsMenu.AdvisorIsLoadedNotice(loaded.ToUpperInvariant()), Output);
        Assert.Equal(before, File.ReadAllText(file));
        Assert.Equal("(me is the profile in use: change it with /server, /model or /reasoning)", SettingsMenu.AdvisorIsLoadedNotice("me"));
        pane.Dispose();
    }

    [Fact]
    public void TheSaveError_NamesTheProfile_NotTheServer() =>
        Assert.Equal("Could not save dba's profile: denied", SettingsMenu.AdvisorSaveFailedError("dba", "denied"));

    // ── The model each answers on, on its line ──────────────────────────────

    /// <summary>
    /// The linked model names a running container's model on the row, in the band and after a change; a cached profile picks a new
    /// one up with no write (the container started), as the facts are computed at each read.
    /// </summary>
    [Fact]
    public async Task OnThePane_TheLinkedModel_NamesWhatEachAnswersOn_WithNoWrite()
    {
        Profiles.Create(_dir, "doug", new AppSettingsData { LlmUrl = "docker:vllm", LlmModel = "" });
        Profiles.Create(_dir, "einstein", new AppSettingsData { LlmUrl = "claude-code", LlmModel = "opus" });
        _settings.Update(d => d.AdvisorList = [new AdvisorEntry("doug", "architect"), new AdvisorEntry("einstein", "designer")]);
        var (menu, pane) = PaneMenu();

        string running = "";
        menu.AdvisorLinkedModel = data => NeonSidekick.Docker.DockerEndpoint.IsDocker(data.LlmUrl) ? running : data.LlmModel;
        Assert.Contains("doug     — architect · docker     · " + AdvisorServer.DefaultModel, menu.FieldFooter(SettingsField.AdvisorList, _settings.Current).Text);
        running = "Qwen/Qwen3.8-27B";
        Assert.Contains("doug     — architect · docker     · Qwen3.8-27B", menu.FieldFooter(SettingsField.AdvisorList, _settings.Current).Text);
        Assert.Contains("einstein — designer  · claudecode · opus", menu.FieldFooter(SettingsField.AdvisorList, _settings.Current).Text);

        OpenAdvisorList();
        Push(Keys.Escape, Keys.Escape);
        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains("doug      architect · docker     · Qwen3.8-27B", Output);
        Assert.Contains("einstein  designer  · claudecode · opus", Output);
        pane.Dispose();
    }

    /// <summary>The rows pad in cells: a role with a two-cell emoji keeps the kinds in one column.</summary>
    [Fact]
    public void TheRows_PadTheRoleInCells()
    {
        var facts = new AdvisorServerFacts("http", "m");
        string emoji = Spectre.Console.Markup.Remove(SettingsMenu.AdvisorRow(new AdvisorEntry("ux", "🎨 art"), true, 4, facts, 6));
        string plain = Spectre.Console.Markup.Remove(SettingsMenu.AdvisorRow(new AdvisorEntry("qa", "tests"), true, 4, facts, 6));
        Assert.Equal(TextCells.Width(plain[..plain.IndexOf(" · ", StringComparison.Ordinal)]), TextCells.Width(emoji[..emoji.IndexOf(" · ", StringComparison.Ordinal)]));
        Assert.Equal("qa  tests  · docker     · m ○", Spectre.Console.Markup.Remove(SettingsMenu.AdvisorRow(new AdvisorEntry("qa", "tests"), true, 4, facts with { Kind = "docker" }, 6, kindWidth: 10)));
    }

    // ── /advisor list: the list alone, and the reasoning disc (2026-11-23) ────────────

    /// <summary>
    /// <c>only</c> (2026-11-23, <c>/advisor list</c>, the user's pick): the list opened alone closes on one Esc, its hint saying so; each row ends
    /// its model on the reasoning disc, and the band under an advisor names the level in words on a row of its own.
    /// </summary>
    [Fact]
    public async Task OnThePane_TheListOpenedAlone_ClosesOnOneEsc_AndShowsEachAdvisorsReasoning()
    {
        OneAdvisor(new AppSettingsData { LlmUrl = Box1, LlmModel = "m-a", LlmReasoning = "high" });
        var (menu, pane) = PaneMenu();
        Push(Keys.Down, Keys.Down);             // the advisor: its band
        Push(Keys.Escape);                      // one Esc: the pane closes

        var changes = await menu.ShowAsync(CancellationToken.None, midTurn: false, open: SettingsField.AdvisorList, only: true);

        Assert.Equal(SettingsChanges.None, changes);
        Assert.False(pane.OverlayOpen);
        Assert.Contains(SettingsMenu.AdvisorListCloseKeys, Output);
        Assert.DoesNotContain(SettingsMenu.AdvisorListKeys, Output);
        Assert.Contains("· http · m-a ◕", Output);   // the row's disc, the status bar's
        Assert.Contains(SettingsMenu.AdvisorReasoningBand("high"), Output);
        Assert.Equal("Reasoning: ◕ high (extensive thinking)", SettingsMenu.AdvisorReasoningBand("high"));
        Assert.Equal("Reasoning: ○ none (thinking off)", SettingsMenu.AdvisorReasoningBand("none"));
        Assert.Equal("Enter = add, or change the advisor · Esc = close", SettingsMenu.AdvisorListCloseKeys);
        pane.Dispose();
    }

    /// <summary>An action inside the lone list still writes, the row's disc follows the new level, and one Esc then closes.</summary>
    [Fact]
    public async Task OnThePane_TheListOpenedAlone_ChangeTheReasoningWrites_TheDiscFollows_ThenOneEscCloses()
    {
        OneAdvisor(new AppSettingsData { LlmUrl = Box1, LlmModel = "m-a", LlmReasoning = "high" });
        var (menu, pane) = PaneMenu();
        FirstAdvisorAction(SettingsMenu.ChangeAdvisorReasoningChoice);
        Push(Keys.Down, Keys.Enter);            // xhigh, under high
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None, midTurn: false, open: SettingsField.AdvisorList, only: true);

        Assert.Equal("xhigh", AdvisorFile("dba").LlmReasoning);
        Assert.Contains("· m-a ●", Output[Output.LastIndexOf(SettingsMenu.AdvisorReasoningNotice("dba", "xhigh"), StringComparison.Ordinal)..]);
        Assert.False(pane.OverlayOpen);
        pane.Dispose();
    }

    /// <summary>The next visit without <c>only</c> keeps today's path: the list's hint says back, and Esc goes back to the tab.</summary>
    [Fact]
    public async Task OnThePane_AVisitAfterTheLoneList_GoesBackAgain()
    {
        OneAdvisor(new AppSettingsData { LlmUrl = Box1, LlmModel = "m-a" });
        var (menu, pane) = PaneMenu();
        Push(Keys.Escape);
        await menu.ShowAsync(CancellationToken.None, midTurn: false, open: SettingsField.AdvisorList, only: true);
        int second = Output.Length;
        Push(Keys.Escape, Keys.Escape);         // the list, then the tab

        await menu.ShowAsync(CancellationToken.None, midTurn: false, open: SettingsField.AdvisorList);

        Assert.Contains(SettingsMenu.AdvisorListKeys, Output[second..]);
        Assert.DoesNotContain(SettingsMenu.AdvisorListCloseKeys, Output[second..]);
        pane.Dispose();
    }

    /// <summary>Off the pane (typed picks) the lone list closes on one Esc too.</summary>
    [Fact]
    public async Task OffThePane_TheListOpenedAlone_ClosesOnOneEsc()
    {
        OneAdvisor(new AppSettingsData { LlmUrl = Box1, LlmModel = "m-a" });
        Push(Keys.Escape);

        var changes = await _menu.ShowAsync(CancellationToken.None, midTurn: false, open: SettingsField.AdvisorList, only: true);

        Assert.Equal(SettingsChanges.None, changes);
        Assert.Equal(Profiles.DefaultName, _settings.ProfileName);
    }

    /// <summary>A missing advisor's band has its description and no reasoning line; its row no disc.</summary>
    [Fact]
    public async Task OnThePane_AMissingAdvisorsBand_HasNoReasoningLine()
    {
        _settings.Update(d => d.AdvisorList = [new AdvisorEntry("gone", "critic")]);
        var (menu, pane) = PaneMenu();
        Push(Keys.Down, Keys.Down);
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None, midTurn: false, open: SettingsField.AdvisorList, only: true);

        Assert.Contains(SettingsMenu.NoDescriptionBand, Output);
        Assert.DoesNotContain("Reasoning: ", Output);
        pane.Dispose();
    }
}
