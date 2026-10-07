using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Files;
using NeonSidekick.Llm;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using NeonSidekick.Viewer;

namespace NeonSidekick.Tests;

/// <summary>
/// The thumbnail browser on the screen (2026-10-04): <c>/view &lt;path&gt; --thumbs</c> (<c>/thumbs</c> until later that day), <c>/comfy
/// thumbs</c> and the toolbar's 🪟 open it, and the chat keeps it, the viewer
/// and the strip on one picture; the picture menu's attach, print and lines arrive from a window's thread.
/// </summary>
public partial class ChatScreenTests
{
    private Action<string, string?>? _openThumbs;   // ThumbsWindow.Open: null = none, as off Windows
    private Action<string>? _followThumbs;          // ThumbsWindow.Follow: null = none
    private Action<string>? _showInViewer;          // PictureWindow.ShowQuietly: null = none
    private Func<bool>? _closeThumbs;               // ThumbsWindow.Close: null = none

    [WindowsFact]
    public async Task ViewThumbs_OpensOnAFolder_OrOnAPicturesFolderWithItSelected_AndTheErrors()
    {
        _settings.Update(d => d.TtsOutput = false);
        string files = Path.Combine(_settings.ProfileDirectory, WorkingDirectory.DefaultFolderName);
        Directory.CreateDirectory(Path.Combine(files, "docs"));
        File.WriteAllBytes(Path.Combine(files, "docs", "square.bmp"), SmokeChecks.SolidBmp(4, 4));
        File.WriteAllText(Path.Combine(files, "notes.txt"), "text");
        var opened = new List<(string Folder, string? Select)>();
        _openThumbs = (folder, select) => opened.Add((folder, select));
        PushLine("/view docs --thumbs");
        PushLine("/view --thumbs docs/square.bmp");
        PushLine("/view --thumbs");                  // no path: the usage error (the user's call, 2026-10-04)
        PushLine("/view --chat docs --thumbs");      // both: the usage error
        PushLine("/view nope --thumbs");
        PushLine("/view notes.txt --thumbs");
        PushLine(@"/view ..\x --thumbs");
        PushLine("/exit");

        string output = await RunAsync();

        string docs = Path.Combine(files, "docs");
        Assert.Equal([(docs, null), (docs, Path.Combine(docs, "square.bmp"))], opened);
        Assert.Equal(2, Count(output, ThumbsText.Opened(docs)));
        Assert.Contains(ThumbsText.Keys, output);
        Assert.Equal(2, Count(output, "  ✗ " + ChatScreen.ViewUsageError));
        Assert.Contains("  ✗ " + FileText.Missing("nope"), output);
        Assert.Contains("  ✗ " + FileText.NotAnImage("notes.txt"), output);
        Assert.Contains("  ✗ " + FileText.OutsideRoot(@"..\x"), output);
        Assert.Empty(_chat.Requests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ComfyThumbs_OpensOnTheOutputFolder_MadeFirst_OrIsAnErrorWithoutAWindow(bool available)
    {
        _settings.Update(d => d.TtsOutput = false);
        var opened = new List<(string Folder, string? Select)>();
        if (available)
        {
            _openThumbs = (folder, select) => opened.Add((folder, select));
        }

        PushLine("/comfy thumbs");
        PushLine("/view . --thumbs");
        PushLine("/exit");

        string output = await RunAsync();

        string folder = Path.GetDirectoryName(ComfyPicture("x.png"))!;
        if (available)
        {
            Assert.Equal((folder, null), opened[0]);
            Assert.True(Directory.Exists(folder));
            Assert.Contains(ThumbsText.Opened(folder), output);
            Assert.Equal(2, opened.Count);
        }
        else
        {
            Assert.Contains("  ✗ " + ThumbsText.Unavailable, output);
        }
    }

    /// <summary><c>/comfy thumbs</c> and <c>/view &lt;folder&gt; --thumbs</c> under a reply: the window opens, its line lands in the reply, the reply runs on.</summary>
    [Fact]
    public async Task MidTurn_Thumbs_OpensTheWindow_TheReplyRunsOn()
    {
        var opened = new List<string>();
        _openThumbs = (folder, _) => opened.Add(folder);
        MidTurnFixture(i =>
        {
            if (i == 1)
            {
                PushLine("/comfy thumbs");
                PushLine("/view . --thumbs");
            }
        });

        string output = await RunAsync();

        Assert.Equal(2, opened.Count);
        Assert.Contains(ThumbsText.Opened(Path.GetDirectoryName(ComfyPicture("x.png"))!), output);
        Assert.DoesNotContain(ChatScreen.MidTurnDeferredNotice("/comfy"), output);
        Assert.DoesNotContain(ChatScreen.MidTurnDeferredNotice("/view"), output);
        Assert.DoesNotContain(ChatScreen.CancelledNotice, output);
        Assert.Single(_chat.Requests);
        Assert.Equal(MidTurnClass.Quick, ChatScreen.MidTurnPolicy(SlashCommand.View, "docs --thumbs"));
        Assert.Equal(MidTurnClass.Quick, ChatScreen.MidTurnPolicy(SlashCommand.Comfy, "thumbs"));
        Assert.Equal(MidTurnClass.Deferred, ChatScreen.MidTurnPolicy(SlashCommand.View, "--thumbs"));
    }

    /// <summary>
    /// The toolbar's 🪟 (2026-10-04, the user's ask): a double-click opens the browser on the ComfyUI output folder, the next closes
    /// it, as 🎞️ does the viewer; the typed <c>/comfy thumbs</c> only opens. 🧮 opens <c>/theme</c>'s picker, which wears it.
    /// </summary>
    [Fact]
    public async Task TheToolbarsWindow_OpensAndClosesTheComfyThumbs_AndTheAbacusOpensTheThemes()
    {
        _settings.Update(d => { d.TtsOutput = false; d.ToolbarItems = [ToolbarItems.Themes, ToolbarItems.ComfyThumbs]; });
        _console.Profile.Height = 40;
        _console.Profile.Width = 240;
        _geometry = new ScreenGeometry(() => null, () => 100);   // an empty line: row 100, the rule 101, the hint row 102, the toolbar 103
        bool open = false;
        var opened = new List<string>();
        int closed = 0;
        _openThumbs = (folder, _) => { opened.Add(folder); open = true; };
        _closeThumbs = () => { bool was = open; open = false; closed += was ? 1 : 0; return was; };
        StepsWhenIdle(
            input => { input.PushClick(3, 103); input.PushClick(3, 103); },   // 🪟: opened
            input => { input.PushClick(3, 103); input.PushClick(3, 103); },   // 🪟 again: closed
            Line("/comfy thumbs"),                                            // typed: opened
            Line("/comfy thumbs"),                                            // typed again: brought forward, never closed
            input => { input.PushClick(0, 103); input.PushClick(0, 103); },   // 🧮: the theme picker
            Key(Keys.Escape),
            Line("/exit"));

        string output = await RunAsync();

        Assert.Equal(3, opened.Count);
        Assert.Equal(1, closed);
        Assert.True(open);
        Assert.Contains(ThumbsText.Closed, output);
        Assert.Contains(SettingsMenu.ThemeTitle, output);
        Assert.Contains("\n" + ScreenPane.ToolbarRow(ChatScreen.ThemeToolGlyph + " " + ChatScreen.ComfyThumbsToolGlyph, "", 239), output);
        Assert.Empty(_chat.Requests);
    }

    /// <summary>
    /// A pick in the browser moves the viewer without the keyboard and highlights the strip's tile; the viewer's keys and the strip's
    /// arrows move the browser — with the strip off too — and nothing answers back.
    /// </summary>
    [WindowsFact]
    public async Task ThePicturesStayInStep_TheBrowserTheViewerAndTheStrip()
    {
        ComfyServer();
        PaneOf40Rows();
        var shown = new List<string>();
        var followed = new List<string>();
        _showInViewer = shown.Add;
        _followThumbs = followed.Add;
        StepsWhenIdle(
            Line("/imagine a cat --seed 5"),
            Line("/imagine a dog --seed 6"),
            input =>
            {
                // The browser's thread, as it were: the older picture picked; then the viewer's keys on the newer one.
                _running!.ThumbsPicked(ComfyPicture("pony-5.png"));
                _running!.ViewerBrowsed(ComfyPicture("pony-6.png"));
                input.Push(Keys.Right);   // the strip's arrow: the browser follows
            },
            Line("/exit"));

        string output = await RunAsync();

        Assert.Equal([ComfyPicture("pony-5.png")], shown);
        Assert.Equal([ComfyPicture("pony-6.png"), ComfyPicture("pony-5.png")], followed);
        Assert.Contains(NeonSidekick.Comfy.ComfyText.StripSelectedHint(2, 2), output);
    }

    [Fact]
    public async Task ViewerBrowsed_MovesTheBrowser_WithTheStripOff()
    {
        _settings.Update(d =>
        {
            d.TtsOutput = false;
            d.ComfyPictureStrip = false;
        });
        var followed = new List<string>();
        _followThumbs = followed.Add;
        StepsWhenIdle(input =>
        {
            _running!.ViewerBrowsed(@"D:\pics\a.png");
            PushLine(input, "/exit");
        });

        await RunAsync();

        Assert.Equal([@"D:\pics\a.png"], followed);
    }

    /// <summary>
    /// The picture menu's Attach puts the picture on the line as a paste (the draft's next word apart), Print prints it as
    /// <c>/print</c> would, and a row's line lands in the chat — all from another thread, waking the idle line.
    /// </summary>
    [WindowsFact]
    public async Task TheMenusAttachPrintAndLines_ReachTheIdleLine()
    {
        _settings.Update(d => d.TtsOutput = false);
        string files = Path.Combine(_settings.ProfileDirectory, WorkingDirectory.DefaultFolderName);
        Directory.CreateDirectory(files);
        string picture = Path.Combine(files, "square.bmp");
        File.WriteAllBytes(picture, SmokeChecks.SolidBmp(4, 4));
        _chat.EnqueueText("A square.");
        StepsWhenIdle(
            _ =>
            {
                _running!.PictureReported(PictureMenuText.Edited("wrote square-edited.bmp"), false);
                _running!.PictureReported("square.bmp: Error: nope", true);
                _running!.PrintPicture(picture);
                _running!.AttachPicture(picture);
            },
            Line("what is this?"),
            Line("/exit"));

        string output = await RunAsync();

        Assert.Contains(PictureMenuText.Edited("wrote square-edited.bmp"), output);
        Assert.Contains("  ✗ square.bmp: Error: nope", output);
        Assert.Equal(FakePrintSpooler.Laser, Assert.Single(_printSpooler.Jobs).Printer);
        var user = _chat.Requests[0].Last(m => m.Role == ChatRole.User);
        Assert.StartsWith("[Image #1] what is this?", user.Text);
        Assert.Single(user.Contents.OfType<DataContent>());
    }
}
