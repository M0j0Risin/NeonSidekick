using NeonSidekick.Settings;
using NeonSidekick.Viewer;
using NeonSidekick.Web;

namespace NeonSidekick.Tests;

/// <summary>
/// The Browser window's navigation bar (2026-10-13): its layout, state and pointer (<see cref="BrowserToolbar"/>), what the address
/// box's text means (<see cref="BrowserAddress"/>), and its keys (<see cref="BrowserKeys"/>). The window that draws it is the smoke's.
/// </summary>
public sealed class BrowserToolbarTests
{
    private static readonly BrowserBarState OnHttps = new(CanBack: true, CanForward: false, Loading: false, "https://example.com/", Typed: true);

    // ── Layout ──────────────────────────────────────────────────────────────

    [Fact]
    public void Layout_At96Dpi_PlacesTheButtonsFromTheLeft_OpenAtTheRight_AndTheFieldBetween()
    {
        var layout = BrowserToolbar.Layout(800, 96);

        Assert.Equal(40, layout.Height);
        Assert.Equal(new BarRect(4, 4, 36, 36), layout.Back);
        Assert.Equal(new BarRect(38, 4, 70, 36), layout.Forward);
        Assert.Equal(new BarRect(72, 4, 104, 36), layout.Reload);
        Assert.Equal(new BarRect(106, 4, 138, 36), layout.Home);
        Assert.Equal(new BarRect(140, 4, 172, 36), layout.History);   // later on 2026-10-13
        Assert.Equal(new BarRect(174, 4, 206, 36), layout.Links);     // later still: the saved links
        Assert.Equal(new BarRect(216, 6, 720, 34), layout.Field);
        Assert.Equal(new BarRect(217, 7, 245, 33), layout.Padlock);
        Assert.Equal(new BarRect(245, 7, 715, 33), layout.Address);
        Assert.Equal(new BarRect(722, 4, 754, 36), layout.Go);
        Assert.Equal(new BarRect(764, 4, 796, 36), layout.Open);   // the margin at the right
    }

    [Fact]
    public void Layout_At144Dpi_ScalesEverything()
    {
        var layout = BrowserToolbar.Layout(1200, 144);

        Assert.Equal(60, layout.Height);
        Assert.Equal(new BarRect(6, 6, 54, 54), layout.Back);
        Assert.Equal(new BarRect(210, 6, 258, 54), layout.History);
        Assert.Equal(new BarRect(261, 6, 309, 54), layout.Links);
        Assert.Equal(324, layout.Field.Left);
        Assert.Equal(48, layout.Go.Width);
        Assert.Equal(1194, layout.Open.Right);
        Assert.Equal(42, layout.Field.Height);
    }

    [Fact]
    public void Layout_ANarrowWindow_KeepsTheAddressBoxsLeastWidth()
    {
        var layout = BrowserToolbar.Layout(200, 96);

        Assert.True(layout.Address.Width >= BrowserToolbar.MinAddressWidth, layout.Address.ToString());
        Assert.True(layout.Open.Right > 200);   // Go and Open run off rather than the box shrinking to nothing
    }

    [Fact]
    public void HitTest_FindsEachButton_AndNothingInAGapTheFieldOrBelow()
    {
        var layout = BrowserToolbar.Layout(800, 96);

        foreach (var item in BrowserBarLayout.Buttons)
        {
            var r = layout[item];
            Assert.Equal(item, layout.HitTest(r.Left + r.Width / 2, r.Top + r.Height / 2));
            Assert.Equal(item, layout.HitTest(r.Left, r.Top));
            Assert.NotEqual(item, layout.HitTest(r.Right, r.Top));   // right edge exclusive
        }

        Assert.Null(layout.HitTest(37, 20));    // between back and forward
        Assert.Null(layout.HitTest(173, 20));   // between history and the saved links
        Assert.Null(layout.HitTest(211, 20));   // between the saved links and the field
        Assert.Null(layout.HitTest(300, 20));   // the field: the box's
        Assert.Null(layout.HitTest(20, 2));     // above the buttons
        Assert.Null(layout.HitTest(20, 100));   // the page
    }

    [Fact]
    public void EditFor_CentresTheLineInTheBox_NeverTallerThanIt()
    {
        var layout = BrowserToolbar.Layout(800, 96);

        Assert.Equal(layout.Address with { Top = 12, Bottom = 28 }, layout.EditFor(16));
        Assert.Equal(layout.Address, layout.EditFor(100));
    }

    // ── State ───────────────────────────────────────────────────────────────

    [Fact]
    public void State_NoHistory_TurnsBackAndForwardOff_AndBlankTurnsGoOpenAndThePadlockOff()
    {
        var state = BrowserBarState.Initial;

        Assert.False(state.Enabled(BrowserToolItem.Back));
        Assert.False(state.Enabled(BrowserToolItem.Forward));
        Assert.True(state.Enabled(BrowserToolItem.Reload));
        Assert.True(state.Enabled(BrowserToolItem.Home));
        Assert.False(state.Enabled(BrowserToolItem.Go));
        Assert.False(state.Enabled(BrowserToolItem.Open));
        Assert.Equal(BrowserLock.None, (state with { Source = BrowserPage.Blank }).Lock);
        Assert.Equal("", state.Glyph(BrowserToolItem.Padlock));
        Assert.False((state with { Source = BrowserPage.Blank }).Enabled(BrowserToolItem.Open));
    }

    [Fact]
    public void State_History_TurnsBackAndForwardOn()
    {
        var state = BrowserBarState.Initial with { CanBack = true, CanForward = true };

        Assert.True(state.Enabled(BrowserToolItem.Back));
        Assert.True(state.Enabled(BrowserToolItem.Forward));
    }

    [Fact]
    public void State_Loading_TurnsReloadIntoStop()
    {
        Assert.Equal(BrowserToolbar.ReloadGlyph, OnHttps.Glyph(BrowserToolItem.Reload));
        Assert.Equal("Reload", OnHttps.Name(BrowserToolItem.Reload));
        var loading = OnHttps with { Loading = true };
        Assert.Equal(BrowserToolbar.StopGlyph, loading.Glyph(BrowserToolItem.Reload));
        Assert.Equal("Stop", loading.Name(BrowserToolItem.Reload));
        Assert.True(loading.Enabled(BrowserToolItem.Reload));
    }

    [Fact]
    public void State_ThePadlock_IsALockOnHttps_AndAWarningOnHttp()
    {
        Assert.Equal(BrowserLock.Secure, OnHttps.Lock);
        Assert.Equal(BrowserToolbar.LockGlyph, OnHttps.Glyph(BrowserToolItem.Padlock));
        Assert.Equal("Secure", OnHttps.Name(BrowserToolItem.Padlock));

        var http = OnHttps with { Source = "http://example.com/" };
        Assert.Equal(BrowserLock.NotSecure, http.Lock);
        Assert.Equal(BrowserToolbar.NotSecureGlyph, http.Glyph(BrowserToolItem.Padlock));
        Assert.Equal("Not secure", http.Name(BrowserToolItem.Padlock));

        Assert.True(OnHttps.Enabled(BrowserToolItem.Go));
        Assert.True(OnHttps.Enabled(BrowserToolItem.Open));
        Assert.False((OnHttps with { Typed = false }).Enabled(BrowserToolItem.Go));
    }

    [Fact]
    public void Glyphs_AreSegoeMdl2Assets_AsEdgesOwn()
    {
        Assert.Equal(["\uE72B", "\uE72A", "\uE72C", "\uE711", "\uE80F", "\uE81C", "\uE734", "\uE735", "\uE72E", "\uE7BA", "\uE751", "\uE8A7"],
            [BrowserToolbar.BackGlyph, BrowserToolbar.ForwardGlyph, BrowserToolbar.ReloadGlyph, BrowserToolbar.StopGlyph, BrowserToolbar.HomeGlyph,
             BrowserToolbar.HistoryGlyph, BrowserToolbar.LinksGlyph, BrowserToolbar.SavedGlyph, BrowserToolbar.LockGlyph, BrowserToolbar.NotSecureGlyph, BrowserToolbar.GoGlyph, BrowserToolbar.OpenGlyph]);
        Assert.Equal("Segoe MDL2 Assets", BrowserToolbar.GlyphFace);
        Assert.Equal(["Back", "Forward", "Reload", "Home", "History", "Saved links", "Go", "Open in your browser"], BrowserBarLayout.Buttons.Select(OnHttps.Name));
        Assert.True(BrowserBarState.Initial.Enabled(BrowserToolItem.History));   // never off: an empty list opens its one dim row
        Assert.True(BrowserBarState.Initial.Enabled(BrowserToolItem.Links));     // the same, and Save this page off there

        // The star: hollow, filled while the page is saved (later on 2026-10-13).
        Assert.Equal(BrowserToolbar.LinksGlyph, OnHttps.Glyph(BrowserToolItem.Links));
        Assert.Equal(BrowserToolbar.SavedGlyph, (OnHttps with { Saved = true }).Glyph(BrowserToolItem.Links));
        Assert.NotEqual(OnHttps, OnHttps with { Saved = true });   // a change the bar draws again
    }

    // ── Pointer ─────────────────────────────────────────────────────────────

    [Fact]
    public void Pointer_HoverMovesAndLeaves_SayingWhenToDrawAgain()
    {
        var bar = new BrowserToolbar();

        Assert.True(bar.Move(BrowserToolItem.Back));
        Assert.False(bar.Move(BrowserToolItem.Back));
        Assert.Equal(BrowserToolItem.Back, bar.Hot);
        Assert.True(bar.Move(null));
        Assert.True(bar.Move(BrowserToolItem.Home));
        Assert.True(bar.Leave());
        Assert.False(bar.Leave());
        Assert.Null(bar.Hot);
    }

    [Fact]
    public void Pointer_AClick_IsDownAndUpOnTheSameEnabledButton()
    {
        var bar = new BrowserToolbar();

        Assert.True(bar.Down(BrowserToolItem.Back, OnHttps));
        Assert.Equal(BrowserToolItem.Back, bar.Pressed);
        Assert.Equal(BrowserToolItem.Back, bar.Up(BrowserToolItem.Back, OnHttps));
        Assert.Null(bar.Pressed);

        bar.Down(BrowserToolItem.Back, OnHttps);
        Assert.Null(bar.Up(BrowserToolItem.Home, OnHttps));      // released elsewhere: no click
        bar.Down(BrowserToolItem.Back, OnHttps);
        Assert.Null(bar.Up(BrowserToolItem.Back, OnHttps with { CanBack = false }));   // turned off in between

        Assert.False(bar.Down(BrowserToolItem.Forward, OnHttps));   // off: no press
        Assert.Null(bar.Pressed);
        Assert.False(bar.Down(BrowserToolItem.Padlock, OnHttps));
        Assert.False(bar.Down(null, OnHttps));

        bar.Down(BrowserToolItem.Home, OnHttps);
        Assert.True(bar.Cancel());
        Assert.False(bar.Cancel());
        Assert.Null(bar.Up(BrowserToolItem.Home, OnHttps));
    }

    [Fact]
    public void Blend_GoesChannelByChannel()
    {
        Assert.Equal(0x000000u, BrowserToolbar.Blend(0x000000, 0xFFFFFF, 0));
        Assert.Equal(0xFFFFFFu, BrowserToolbar.Blend(0x000000, 0xFFFFFF, 100));
        Assert.Equal(0x408020u, BrowserToolbar.Blend(0x000000, 0x80FF40, 50));
        Assert.Equal(0x203040u, BrowserToolbar.Blend(0x203040, 0xFFFFFF, -5));
        Assert.Equal(0x7F7F7Fu, BrowserToolbar.Blend(0xFFFFFF, 0x000000, 50));
    }

    // ── The address box ─────────────────────────────────────────────────────

    private static Uri Search(string words) => new("https://search.test/?q=" + Uri.EscapeDataString(words));

    [Theory]
    [InlineData("https://example.com/a?b=1", "https://example.com/a?b=1")]
    [InlineData("  example.com/x  ", "https://example.com/x")]
    [InlineData("example.com:8080/x", "https://example.com:8080/x")]
    [InlineData("http://example.com/", "http://example.com/")]
    [InlineData("localhost", "http://localhost/")]
    [InlineData("localhost:3000", "http://localhost:3000/")]
    [InlineData("LOCALHOST:3000/app", "http://localhost:3000/app")]
    [InlineData("about:blank", "about:blank")]
    [InlineData("rust async traits", "https://search.test/?q=rust%20async%20traits")]
    [InlineData("neon", "https://search.test/?q=neon")]
    [InlineData("localhostess", "https://search.test/?q=localhostess")]
    [InlineData("re:zero", "https://search.test/?q=re%3Azero")]
    [InlineData("javascript:void is bad", "https://search.test/?q=javascript%3Avoid%20is%20bad")]
    public void Resolve_GoesToAnAddress_OrSearchesWords(string text, string expected)
    {
        var go = BrowserAddress.Resolve(text, Search);

        Assert.Null(go.Refusal);
        Assert.Equal(expected, go.Url!.AbsoluteUri);
    }

    [Theory]
    [InlineData("file:///C:/Windows")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,hi")]
    [InlineData("ms-msdt:/id")]
    [InlineData("search-ms:query=x")]
    [InlineData("ftp://example.com/")]
    [InlineData(@"C:\Windows")]
    [InlineData(@"\\server\share")]
    [InlineData("view-source:https://example.com")]
    public void Resolve_RefusesWhatReachesTheMachine(string text)
    {
        var go = BrowserAddress.Resolve(text, Search);

        Assert.Null(go.Url);
        Assert.Equal("the Browser window goes to web pages only, not " + text, go.Refusal);
    }

    [Fact]
    public void Resolve_Blank_GoesNowhere()
    {
        Assert.Equal(BrowserGo.Nowhere, BrowserAddress.Resolve("   ", Search));
        Assert.Equal(BrowserGo.Nowhere, BrowserAddress.Resolve(null, Search));
    }

    [Fact]
    public void SearchUrl_IsDuckDuckGosSite_UnlessSearxngIsPickedWithAUrl()
    {
        Assert.Equal("https://duckduckgo.com/?q=rust%20async", BrowserAddress.SearchUrl(new AppSettingsData(), " rust async ").AbsoluteUri);
        Assert.Equal("https://duckduckgo.com/?q=x", BrowserAddress.SearchUrl(new AppSettingsData { WebSearchMethod = "searxng" }, "x").AbsoluteUri);   // no URL
        Assert.Equal("https://duckduckgo.com/?q=x", BrowserAddress.SearchUrl(new AppSettingsData { WebSearxngUrl = "http://box:8080/" }, "x").AbsoluteUri);   // not picked
        Assert.Equal("http://box:8080/search?q=a%20b", BrowserAddress.SearchUrl(new AppSettingsData { WebSearchMethod = "searxng", WebSearxngUrl = "http://box:8080/" }, "a b").AbsoluteUri);
        Assert.Equal("http://box:8080/searx/search?q=x", BrowserAddress.SearchUrl(new AppSettingsData { WebSearchMethod = "searxng", WebSearxngUrl = "http://box:8080/searx/search?q=old" }, "x").AbsoluteUri);
        Assert.Equal("http://box:8080/search?q=x", SearxngSearch.PageUrl(new Uri("http://box:8080/search"), "x").AbsoluteUri);
    }

    [Fact]
    public void Shown_IsEmptyOnTheBlankPage_ElseTheAddress()
    {
        Assert.Equal("", BrowserAddress.Shown(null));
        Assert.Equal("", BrowserAddress.Shown("about:blank"));
        Assert.Equal("https://example.com/", BrowserAddress.Shown("https://example.com/"));
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("  ", null)]
    [InlineData("example.com", "https://example.com/")]
    [InlineData("localhost:3000", "http://localhost:3000/")]
    [InlineData("https://start.example/home", "https://start.example/home")]
    [InlineData("my start page", null)]
    [InlineData("file:///C:/", null)]
    [InlineData("neon", null)]
    public void Home_IsAWebAddress_ElseTheBlankPage(string setting, string? expected)
    {
        Assert.Equal(expected, BrowserAddress.Home(setting)?.AbsoluteUri);
    }

    // ── Keys ────────────────────────────────────────────────────────────────

    [Fact]
    public void Keys_TheBarsChords()
    {
        Assert.Equal(BrowserKeyAction.Forward, BrowserKeys.ActionFor(BrowserKeys.VkRight, control: false, alt: true));
        Assert.Equal(BrowserKeyAction.None, BrowserKeys.ActionFor(BrowserKeys.VkRight, control: false, alt: false));
        Assert.Equal(BrowserKeyAction.Reload, BrowserKeys.ActionFor(BrowserKeys.VkR, control: true, alt: false));
        Assert.Equal(BrowserKeyAction.None, BrowserKeys.ActionFor(BrowserKeys.VkR, control: false, alt: false));
        Assert.Equal(BrowserKeyAction.Address, BrowserKeys.ActionFor(BrowserKeys.VkL, control: true, alt: false));
        Assert.Equal(BrowserKeyAction.Address, BrowserKeys.ActionFor(BrowserKeys.VkD, control: false, alt: true));
        Assert.Equal(BrowserKeyAction.Address, BrowserKeys.ActionFor(BrowserKeys.VkF6, control: false, alt: false));
        Assert.Equal(BrowserKeyAction.None, BrowserKeys.ActionFor(BrowserKeys.VkD, control: false, alt: false));   // typing a d
        Assert.Equal(BrowserKeyAction.Stop, BrowserKeys.ActionFor(BrowserKeys.VkEscape, control: false, alt: false, loading: true));
        Assert.Equal(BrowserKeyAction.None, BrowserKeys.ActionFor(BrowserKeys.VkEscape, control: false, alt: false));   // the page's Esc
        Assert.Equal(BrowserKeyAction.History, BrowserKeys.ActionFor(BrowserKeys.VkH, control: true, alt: false));   // later on 2026-10-13
        Assert.Equal(BrowserKeyAction.None, BrowserKeys.ActionFor(BrowserKeys.VkH, control: false, alt: false));
    }

    [Fact]
    public void Keys_F11AndEsc_FullScreen()
    {
        Assert.Equal(BrowserKeyAction.ToggleFullScreen, BrowserKeys.ActionFor(BrowserKeys.VkF11, control: false, alt: false));
        Assert.Equal(BrowserKeyAction.ToggleFullScreen, BrowserKeys.ActionFor(BrowserKeys.VkF11, control: false, alt: false, fullScreen: true));
        Assert.Equal(BrowserKeyAction.None, BrowserKeys.ActionFor(BrowserKeys.VkF11, control: true, alt: false));
        Assert.Equal(BrowserKeyAction.None, BrowserKeys.ActionFor(BrowserKeys.VkF11, control: false, alt: true));
        Assert.Equal(BrowserKeyAction.LeaveFullScreen, BrowserKeys.ActionFor(BrowserKeys.VkEscape, control: false, alt: false, fullScreen: true));
        Assert.Equal(BrowserKeyAction.Stop, BrowserKeys.ActionFor(BrowserKeys.VkEscape, control: false, alt: false, loading: true, fullScreen: true));   // the load first
        Assert.Equal(BrowserKeyAction.None, BrowserKeys.ActionFor(BrowserKeys.VkEscape, control: true, alt: false, fullScreen: true));
    }

    // ── Full screen (2026-10-14) ────────────────────────────────────────────

    [Fact]
    public void FullScreen_F11_TogglesTheWindowsOwn_WithTheBar()
    {
        var on = default(BrowserFullScreen).Toggled();
        Assert.Equal(new BrowserFullScreen(Window: true, Page: false), on);
        Assert.True(on.Covering);
        Assert.True(on.BarShown);
        Assert.Equal(default, on.Toggled());
        Assert.False(default(BrowserFullScreen).Covering);
    }

    [Fact]
    public void FullScreen_ThePagesOwn_PutsTheBarAside()
    {
        var page = default(BrowserFullScreen).PageChanged(true);
        Assert.True(page.Covering);
        Assert.False(page.BarShown);
        Assert.Equal(default, page.PageChanged(false));
    }

    [Fact]
    public void FullScreen_F11DuringThePagesOwn_LeavesBoth()
    {
        Assert.Equal(default, new BrowserFullScreen(Window: true, Page: true).Toggled());
        Assert.Equal(default, new BrowserFullScreen(Window: false, Page: true).Toggled());
    }

    [Fact]
    public void FullScreen_Esc_PeelsThePagesFirst()
    {
        var both = new BrowserFullScreen(Window: true, Page: true);
        var f11 = both.Left();
        Assert.Equal(new BrowserFullScreen(Window: true, Page: false), f11);
        Assert.True(f11.BarShown);
        Assert.Equal(default, f11.Left());
        Assert.Equal(default, new BrowserFullScreen(Window: false, Page: true).Left());
    }

    [Fact]
    public void FullScreen_ThePageLeavingItsOwn_KeepsF11s()
    {
        var f11 = default(BrowserFullScreen).Toggled();
        var video = f11.PageChanged(true);
        Assert.Equal(f11, video.PageChanged(false));
    }
}
