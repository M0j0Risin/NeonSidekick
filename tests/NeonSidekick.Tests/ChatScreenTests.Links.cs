using System.Net;
using Microsoft.Extensions.AI;
using NeonSidekick.Files;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using NeonSidekick.Web;
using Spectre.Console;

namespace NeonSidekick.Tests;

/// <summary>
/// Clickable links and paths on the screen (2026-11-01, plan memoized-dancing-horizon item #2): the link chord on a web link opens it
/// through the web seam (never where the terminal opens links itself), on a path opens <c>/browse</c> with the file at the cursor, at
/// the idle line and under a reply alike; a plain click opens nothing; the address under the pointer shows on the status bar.
/// </summary>
public partial class ChatScreenTests
{
    private const string LinkUrl = "https://a.b/c";

    /// <summary>The first cell of the transcript whose link is <paramref name="target"/> (the region's rows over the input row at 100), or null.</summary>
    private (int X, int Y)? FindLink(string target)
    {
        var pane = _running!.PaneForTests;
        for (int y = 99; y >= 50; y--)
        {
            for (int x = 0; x < _console.Profile.Width; x++)
            {
                if (pane.TryHitLink(x, y, out string found) && found == target)
                {
                    return (x, y);
                }
            }
        }

        return null;
    }

    /// <summary>A working folder of its own holding <c>src/Foo.cs</c> and <c>My Docs/a b.txt</c>, in force; its full path.</summary>
    private string LinkFolder()
    {
        string folder = Path.Combine(_dir, "linked");
        Directory.CreateDirectory(Path.Combine(folder, "src"));
        Directory.CreateDirectory(Path.Combine(folder, "My Docs"));
        File.WriteAllText(Path.Combine(folder, "src", "Foo.cs"), "x");
        File.WriteAllText(Path.Combine(folder, "My Docs", "a b.txt"), "x");
        _settings.Update(d => d.WorkingDirectory = folder);
        return folder;
    }

    [Fact]
    public async Task ALinkChordClicked_AtTheIdleLine_OpensInTheBrowser_APlainClickDoesNot_AndNothingIsPrinted()
    {
        _settings.Update(d => d.TtsOutput = false);
        PaneOf40Rows();
        _console.Profile.Capabilities.Links = false;   // a terminal that opens no link itself (TestConsole says it does by default)
        _chat.EnqueueText("See " + LinkUrl + " now.");
        (int X, int Y)? at = null;
        StepsWhenIdle(
            Line("a link please"),
            input =>
            {
                at = FindLink(LinkUrl);
                input.PushClick(at!.Value.X, at.Value.Y);
            },
            input => input.PushClick(at!.Value.X, at.Value.Y, opener: true),
            Line("/exit"));

        string output = await RunAsync();

        Assert.NotNull(at);
        Assert.Equal([LinkUrl], _openedUrls);
        Assert.DoesNotContain(WebText.OpenedOne(LinkUrl), output);
    }

    [WindowsFact]
    public async Task ALinkChordClicked_WhereTheTerminalOpensLinks_OpensNothing()
    {
        _settings.Update(d => d.TtsOutput = false);
        PaneOf40Rows();
        _console.Profile.Capabilities.Links = true;   // Windows Terminal or VS Code: the terminal has opened it already
        _chat.EnqueueText("See " + LinkUrl + " now.");
        (int X, int Y)? at = null;
        StepsWhenIdle(
            Line("a link please"),
            input =>
            {
                at = FindLink(LinkUrl);
                input.PushClick(at!.Value.X, at.Value.Y, opener: true);
            },
            Line("/exit"));

        await RunAsync();

        Assert.NotNull(at);
        Assert.Empty(_openedUrls);
    }

    [Fact]
    public async Task AFailedOpen_IsAnErrorLine_WithoutItsLead()
    {
        _settings.Update(d => d.TtsOutput = false);
        PaneOf40Rows();
        _console.Profile.Capabilities.Links = false;
        _web = new WebAccess(new HttpClient(_http), _browser, _time, (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }), _ => throw new InvalidOperationException("no browser here"));
        _chat.EnqueueText("See " + LinkUrl + " now.");
        StepsWhenIdle(
            Line("a link please"),
            input =>
            {
                var at = FindLink(LinkUrl)!.Value;
                input.PushClick(at.X, at.Y, opener: true);
            },
            Line("/exit"));

        string output = await RunAsync();

        Assert.Contains(TranscriptRenderer.ErrorGlyph + WebText.OpenFailed(LinkUrl, "no browser here")["Error: ".Length..], output);
    }

    [Fact]
    public async Task ALinkChordClicked_UnderAReply_Opens()
    {
        _settings.Update(d => d.TtsOutput = false);
        PaneOf40Rows();
        _console.Profile.Capabilities.Links = false;
        _chat.EnqueueText("See " + LinkUrl + " now.");
        _chat.EnqueueText("Second ", "answer.");
        bool clicked = false;
        _chat.BeforeUpdate = async (i, ct) =>
        {
            if (i == 1 && _chat.Requests.Count == 2 && FindLink(LinkUrl) is { } at)
            {
                _scripted!.PushClick(at.X, at.Y, opener: true);
                clicked = true;
                for (int tries = 0; tries < 500 && _scripted.IsAvailable; tries++)
                {
                    await Task.Delay(10, CancellationToken.None);
                }
            }
        };
        StepsWhenIdle(Line("one"), Line("two"), Line("/exit"));

        await RunAsync();

        Assert.True(clicked);
        Assert.Equal([LinkUrl], _openedUrls);
    }

    [Fact]
    public async Task APathChordClicked_AtTheIdleLine_BrowsesItsFolder_TheFileAtTheCursor_TheDraftBackAfter()
    {
        _settings.Update(d => d.TtsOutput = false);
        PaneOf40Rows();
        _console.Profile.Width = 120;
        string folder = LinkFolder();
        _chat.EnqueueText("Look at `My Docs/a b.txt` here.");
        _chat.EnqueueText("ok");
        StepsWhenIdle(
            Line("which file?"),
            input =>
            {
                var at = FindLink(LinkText.PathLink(Path.Combine(folder, "My Docs", "a b.txt")))!.Value;
                input.Push(Keys.Char('a'), Keys.Char('b'), Keys.Char('c'));
                input.PushClick(at.X, at.Y, opener: true);
            },
            input => input.Push(Keys.Escape),   // the browse pane closes
            input => input.Push(Keys.Enter),    // the draft came back: sent
            Line("/exit"));

        string output = await RunAsync();

        Assert.Contains("\n" + Path.Combine(folder, "My Docs", "a b.txt") + "\n", output);   // the path row: the cursor on the file
        Assert.Equal(2, _chat.Requests.Count);
        Assert.Equal("abc", _chat.Requests[1].Last(m => m.Role == ChatRole.User).Text);
    }

    [Fact]
    public async Task ATypedBrowse_OfAFile_OpensItsFolder_TheFileAtTheCursor()
    {
        _settings.Update(d => d.TtsOutput = false);
        PaneOf40Rows();
        _console.Profile.Width = 120;
        string folder = LinkFolder();
        StepsWhenIdle(
            Line("/browse " + Path.Combine(folder, "My Docs", "a b.txt")),
            input => input.Push(Keys.Enter, Keys.Escape),   // Enter opens the file at the cursor
            Line("/exit"));

        string output = await RunAsync();

        Assert.Contains("\n" + Path.Combine(folder, "My Docs", "a b.txt") + "\n", output);
        Assert.Equal([Path.Combine(folder, "My Docs", "a b.txt")], _openedFiles);
    }

    [Fact]
    public async Task APathLink_MadeBeforeACwd_ToAnotherFolder_OpensNothing_AndSaysWhy()
    {
        _settings.Update(d => d.TtsOutput = false);
        PaneOf40Rows();
        string folder = LinkFolder();
        string other = Path.Combine(_dir, "elsewhere");
        Directory.CreateDirectory(other);
        string file = Path.Combine(folder, "src", "Foo.cs");
        _chat.EnqueueText("See `src/Foo.cs`.");
        StepsWhenIdle(
            Line("which file?"),
            input =>
            {
                var at = FindLink(LinkText.PathLink(file))!.Value;
                _settings.Update(d => d.WorkingDirectory = other);
                input.PushClick(at.X, at.Y, opener: true);
            },
            Line("/exit"));

        string output = await RunAsync();

        Assert.Contains(TranscriptRenderer.ErrorGlyph + FileText.OutsideRoot(file), output);
        Assert.Empty(_openedFiles);
    }

    [Fact]
    public async Task TheAddressUnderThePointer_ShowsOnTheStatusBar_WithTheHintsRowDown()
    {
        _settings.Update(d => d.TtsOutput = false);
        PaneOf40Rows();
        _chat.EnqueueText("See " + LinkUrl + " now.");
        string? hint = null;
        StepsWhenIdle(
            Line("a link please"),
            input =>
            {
                var at = FindLink(LinkUrl)!.Value;
                var pane = _running!.PaneForTests;
                _time.Advance(ScreenPane.Tick);   // hover asked: a link is stored
                // The pointer's row is a window row (the geometry's window top is 0 here): walked up from the buffer row to the link's.
                for (int up = 0; up <= 100 && pane.LinkHintNow is null; up++)
                {
                    pane.PointerMoved(at.X, at.Y - up);
                    _time.Advance(ScreenPane.Tick);
                }

                hint = pane.LinkHintNow;
                PushLine(input, "/exit");   // a step that pushes nothing would leave the read waiting
            });

        string output = await RunAsync();

        Assert.Equal(LinkText.HoverHint(LinkUrl, null, outside: false, LinkText.HintWidth), hint);
        Assert.Contains(LinkText.Chord + " opens " + LinkUrl, output);
    }
}
