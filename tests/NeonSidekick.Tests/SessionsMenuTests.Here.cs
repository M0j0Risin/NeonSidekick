using NeonSidekick.App;
using NeonSidekick.Sessions;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary><c>/sessions here</c> and the "this folder" mark (2026-11-21, feature idea #4).</summary>
public partial class SessionsMenuTests
{
    private SessionsMenu NoPaneMenuIn(string? root)
    {
        var pane = new ScreenPane(_console, null, _time);
        var keys = new KeySource(_console.Input, TimeSpan.FromMilliseconds(1));
        return new SessionsMenu(_store, () => _current, new TranscriptRenderer(_console), new MenuPane(pane, keys), new InputLine(pane, keys), _time, _purged.Add, Export, () => root);
    }

    private long SeedIn(string title, string? folder)
    {
        long id = _store.Begin(title, "llama", folder)!.Value;
        _store.AppendTurn(id, "q", "r", 0, [], [], 0, 1, 1, false, cwd: folder);
        _time.Advance(TimeSpan.FromMinutes(1));
        return id;
    }

    [Fact]
    public async Task Here_WithoutThePane_ListsOnlyThisFoldersSessions_UnderItsTitle()
    {
        string here = Path.Combine(_dir, "repo");
        SeedIn("from here", here);
        SeedIn("from elsewhere", Path.Combine(_dir, "other"));
        SeedIn("from before", null);
        _console.Profile.Width = 400;   // the temp folder's long path on one line

        Assert.Null(await NoPaneMenuIn(here.ToUpperInvariant() + Path.DirectorySeparatorChar).ShowAsync(CancellationToken.None, here: true));

        string output = _console.Output;
        Assert.Contains(SessionsMenu.HereTitleFor(here.ToUpperInvariant() + Path.DirectorySeparatorChar), output);   // the folder named (#119)
        Assert.Contains("from here", output);
        Assert.DoesNotContain("from elsewhere", output);
        Assert.DoesNotContain("from before", output);
    }

    [Fact]
    public async Task Here_WithNoneOfThisFolder_SaysSo_NotThatNoSessionsAreStored()
    {
        SeedIn("from before", null);

        await NoPaneMenuIn(Path.Combine(_dir, "repo")).ShowAsync(CancellationToken.None, here: true);

        Assert.Contains(SessionText.NoneHere, _console.Output);
        Assert.DoesNotContain(SessionsMenu.EmptyNotice, _console.Output);
    }

    [Fact]
    public void ARowOfThisFolder_IsMarked_AndTheFooterNamesTheFolder()
    {
        var zone = TimeZoneInfo.Utc;
        string folder = Path.Combine(_dir, "repo");
        var session = _store.Summary(SeedIn("t", folder))!;

        Assert.EndsWith(Theme.DimMarkup("· " + SessionText.HereMark), SessionsMenu.RowMarkup(session, false, zone, here: true));
        Assert.DoesNotContain(SessionText.HereMark, SessionsMenu.RowMarkup(session, false, zone));
        var footer = SessionsMenu.Footer(session, false, zone);
        Assert.True(footer.KeepLines);
        Assert.Equal("t\n" + SessionText.FolderFooter(folder), footer.Text);
        Assert.False(SessionsMenu.Footer(session with { Cwd = null }, false, zone).KeepLines);
        Assert.Equal("Sessions · this folder", SessionsMenu.HereTitle[(SessionsMenu.HereTitle.IndexOf("Sessions", StringComparison.Ordinal))..]);
    }

    /// <summary>The here view's rows carry no mark (2026-11-22, the review's #110): every row is this folder's; the whole list marks them.</summary>
    [Fact]
    public async Task Here_InThePane_MarksNoRow_TheWholeListDoes()
    {
        string here = Path.Combine(_dir, "repo");
        SeedIn("from here", here);
        SeedIn("from elsewhere", Path.Combine(_dir, "other"));

        _console.Profile.Height = 40;
        var pane = new ScreenPane(_console, new ScreenGeometry(() => null), _time) { Hint = () => "idle" };
        var keys = new KeySource(_console.Input, TimeSpan.FromMilliseconds(1));
        var menu = new SessionsMenu(_store, () => _current, new TranscriptRenderer(pane), new MenuPane(pane, keys), new InputLine(pane, keys), _time, _purged.Add, Export, () => here);
        pane.Show();
        Push(new ConsoleKeyInfo('\x1b', ConsoleKey.Escape, false, false, false));
        Assert.Null(await menu.ShowAsync(CancellationToken.None, here: true));
        string heres = _console.Output;
        Assert.Contains("from here", heres);
        Assert.DoesNotContain("· " + SessionText.HereMark, heres.Replace(SessionsMenu.HereTitle, "", StringComparison.Ordinal));

        Push(new ConsoleKeyInfo('\x1b', ConsoleKey.Escape, false, false, false));
        Assert.Null(await menu.ShowAsync(CancellationToken.None));
        Assert.Contains("· " + SessionText.HereMark, _console.Output[heres.Length..]);   // the whole list still marks them
        Assert.Equal(SessionsMenu.Title + " · " + here, SessionsMenu.HereTitleFor(here));
        Assert.Equal(SessionsMenu.HereTitle, SessionsMenu.HereTitleFor(null));
    }
}
