using NeonSidekick.App;
using NeonSidekick.HomeAssistant;
using NeonSidekick.Llm;
using NeonSidekick.Timers;
using NeonSidekick.UI;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// The completion survey's rows on the screen's argument lists (2026-10-27): each new source carried through <see cref="ChatScreen.ArgumentSources"/>
/// (the command's own words without one), <c>/view</c>'s flags and <c>/imagine</c>'s pictures as path lists, <c>/sampling</c>'s <c>clear</c> after a
/// field, <c>/timer stopwatch</c>'s names; and the Web UI page's list, which offers what the page runs and nothing else.
/// </summary>
public partial class ChatScreenTests
{
    [Fact]
    public void ArgumentItems_TheSurveysSources_AreCarried_AndTheCommandsOwnWordsStandWithoutThem()
    {
        IReadOnlyList<CompletionItem> Marked(string argText) => [new("marked " + argText, "")];
        var sources = Sources() with { HomeBar = Marked, Prompts = Marked, BrowserLinks = Marked, YouTubeSaved = Marked, Cameras = Marked, Stopwatches = ["laps", "tea"] };

        foreach (string command in (string[])["/homebar", "/prompts", "/browser", "/youtube", "/camera"])
        {
            Assert.Equal(["marked x"], Texts(ChatScreen.ArgumentItems(command, "x", sources)));
        }

        Assert.Equal(HomeBarText.Words, Texts(ChatScreen.ArgumentItems("/homebar", "", Sources())));
        Assert.Equal(["save", "list", "remove"], Texts(ChatScreen.ArgumentItems("/prompts", "", Sources())));
        Assert.Contains("snap", Texts(ChatScreen.ArgumentItems("/camera", "", Sources())));

        Assert.Equal([new CompletionItem("stopwatch laps", TimerText.StopwatchName), new CompletionItem("stopwatch tea", TimerText.StopwatchName)], ChatScreen.ArgumentItems("/timer", "stopwatch ", sources));
        Assert.Equal(["STOPWATCH tea"], Texts(ChatScreen.ArgumentItems("/timer", "STOPWATCH t", sources)));   // the head as typed
        Assert.Empty(ChatScreen.ArgumentItems("/timer", "stopwatch ", Sources()));   // none running

        string field = SamplingField.All[0].Wire;
        Assert.Equal([new CompletionItem(field + " clear", SamplingText.ClearFieldCompletionNote)], ChatScreen.ArgumentItems("/sampling", field + " ", sources));
        Assert.Equal([SamplingText.ExtraWord + " clear"], Texts(ChatScreen.ArgumentItems("/sampling", SamplingText.ExtraWord + " c", sources)));
        Assert.Empty(ChatScreen.ArgumentItems("/sampling", "bogus ", sources));
        Assert.Empty(ChatScreen.ArgumentItems("/sampling", field + " 0.5", sources));   // the value is the user's to type
    }

    [Fact]
    public void ArgumentPaths_ViewsFlags_AndImaginesPictures()
    {
        var sources = Sources();
        Assert.Equal(["--chat", "--thumbs"], ChatScreen.ArgumentPaths("/view", "-", sources)!.Paths);
        Assert.Equal(["docs/ --thumbs"], ChatScreen.ArgumentPaths("/view", "docs/ --t", sources)!.Paths);
        Assert.Null(ChatScreen.ViewFlagPaths("--chat "));   // a flag typed: the pictures again
        Assert.Null(ChatScreen.ViewFlagPaths("docs/pic.png"));   // a path being typed is the path list's
        Assert.Null(ChatScreen.ViewFlagPaths("--chat docs/ "));

        Assert.Equal(["faceswap --image pic.png"], ChatScreen.ArgumentPaths("/imagine", "faceswap --image p", sources)!.Paths);
        Assert.Equal(["a --seed 4 --image2 docs/", "a --seed 4 --image2 pic.png", "a --seed 4 --image2 shot.jpg"], ChatScreen.ArgumentPaths("/imagine", "a --seed 4 --image2 ", sources)!.Paths);
        Assert.Null(ChatScreen.ArgumentPaths("/imagine", "a cat", sources));   // the prompt is free text
        Assert.Null(ChatScreen.ArgumentPaths("/imagine", "a --seed 4", sources));
    }

    /// <summary>
    /// The page's list (2026-10-27): the saved prompts by number, <c>/browser</c>'s links only after <c>unsave</c> (a number opens a window on
    /// the PC), no <c>/ha stills</c> (the page refuses it), and the vault's folders while the vault tools are on.
    /// </summary>
    [Fact]
    public async Task ThePagesDropdown_OffersThePromptsAndUnsave_NoStills_AndTheVaultWhileOn()
    {
        string vault = Path.Combine(_settings.StorageDirectory, "vault-survey");
        Directory.CreateDirectory(Path.Combine(vault, "notes"));
        Directory.CreateDirectory(Path.Combine(vault, ".obsidian"));
        _settings.Update(d => { d.TtsOutput = false; d.WebUiCommands = true; d.ObsidianTools = true; d.ObsidianVault = vault; });
        SavePrompts("Summarize the day.");
        SavedLinks().Add("https://a.example/", "Alpha", out _);
        WebEventHub.Subscription? page = null;
        IReadOnlyList<string> Items(string text) => [.. Web.Complete(new WebCompleteRequest { Text = text, Cursor = text.Length, Seq = 1 }).Items.Select(x => x.Text)];
        IReadOnlyList<string>? prompts = null, browser = null, unsave = null, ha = null, vaultOn = null, vaultOff = null;
        var input = WebSteps(
            i =>
            {
                page = Web.Events.Subscribe();
                PushLine(i, "/new");
            },
            i =>
            {
                prompts = Items("/prompts ");
                browser = Items("/browser ");
                unsave = Items("/browser unsave ");
                ha = Items("/ha s");
                vaultOn = Items("/vault ");
                _settings.Update(d => d.ObsidianTools = false);
                PushLine(i, "/new");
            },
            i =>
            {
                vaultOff = Items("/vault ");
                PushLine(i, "/exit");
            });

        await RunAsync(input);

        Assert.Contains(prompts!, t => t.EndsWith('1'));
        Assert.DoesNotContain(browser!, t => t.EndsWith('1'));
        Assert.Contains(unsave!, t => t.EndsWith("unsave 1", StringComparison.Ordinal));
        Assert.NotEmpty(ha!);
        Assert.DoesNotContain(ha!, t => t.Contains("stills", StringComparison.Ordinal));
        Assert.Contains(vaultOn!, t => t.EndsWith("notes/", StringComparison.Ordinal));
        Assert.Empty(vaultOff!);
        page!.Dispose();
    }
}
