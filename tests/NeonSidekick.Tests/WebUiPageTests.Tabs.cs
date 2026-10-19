using System.Text.Json;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// Later on 2026-10-15 in the browser: each tab only while its controls are on (the nav, a page whose tab went off, the page that says none is
/// on, the Chat badge with Chat off), the ✕ that clears the draft, and the floating buttons (expand or collapse every fold, to the top and the
/// bottom, the keyboard's inset).
/// </summary>
public sealed partial class WebUiPageTests
{
    private void Publish(string name, string json) => PublishWhenStreaming(new WebEvent(name, () => json));

    private static string Json(WebState state) => JsonSerializer.Serialize(state, WebUiJsonContext.Default.WebState);

    private static IReadOnlyList<WebEntry> Notices(int count) => [.. Enumerable.Range(1, count).Select(i => new WebEntry(i, "notice", "line " + i, null, null, false))];

    private static bool Hidden(System.Text.Json.JsonElement value) => value.GetBoolean();

    private Task<bool> HiddenAsync(HeadlessPage page, string id) =>
        page.EvalAsync("document.getElementById('" + id + "').classList.contains('hidden')").ContinueWith(t => Hidden(t.Result), TaskScheduler.Default);

    // ── The tabs ────────────────────────────────────────────────────────────

    [WindowsFact]
    public async Task TheNav_ShowsTheTabsThatAreOn_AndTheChatBadge_IsBareWithChatOff()
    {
        _backend.Snap = new WebSnapshot([], [], State() with { HomeTab = false }, WebUiText.Words);
        if (await SignedInAsync("/") is not { } page)
        {
            return;
        }

        await page.WaitAsync("document.querySelector('header.top nav').classList.contains('ready')", "the nav shown once the tabs are known");
        Assert.True((await page.EvalAsync("document.querySelector('nav a[href=\"/home\"]').classList.contains('hidden')")).GetBoolean());
        Assert.False((await page.EvalAsync("document.querySelector('nav a[href=\"/shopping\"]').classList.contains('hidden')")).GetBoolean());

        // The Shopping tab with Chat off: no Chat link, and a question waiting at the PC draws no count, no (n) in the title.
        _backend.PulseNow = new WebPulse(true, 2, null, "", "tag", ChatTab: false, HomeTab: false, ShoppingTab: true);
        await page.GoAsync(Root + "shopping");
        await page.WaitAsync("document.getElementById('chat-link').classList.contains('hidden')", "the Chat link hidden");
        Assert.Equal("", await page.TextAsync("document.getElementById('chat-link').dataset.badge || ''"));
        Assert.False(await page.TextAsync("document.title") is var title && title.StartsWith('('), "no (n) in the title");
        Assert.False((await page.EvalAsync("document.getElementById('chat-link').classList.contains('busy')")).GetBoolean());
    }

    /// <summary>
    /// The tab menu (2026-10-16, the user's pick): Chat a link, Home, Shopping and YouTube behind ☰; it opens to the tabs that are on (YouTube only
    /// once the PC says so), Esc and a press outside close it, a pick goes there, and on that tab the button reads its glyph and name.
    /// </summary>
    [WindowsFact]
    public async Task TheTabMenu_ListsTheTabsOn_EscAndAPressOutsideClose_APickGoesThere()
    {
        _backend.Home = new Home();
        if (await SignedInAsync("/") is not { } page)
        {
            return;
        }

        await page.WaitAsync("document.querySelector('header.top nav').classList.contains('ready')", "the nav shown once the tabs are known");
        Assert.Equal("☰", await page.TextAsync("document.querySelector('.tab-menu-button').textContent"));
        Assert.False((await page.EvalAsync("document.querySelector('nav a[href=\"/\"]').closest('.tab-menu') !== null")).GetBoolean());   // Chat stays a link
        Assert.True(await HiddenListAsync(page));

        await page.EvalAsync("document.querySelector('.tab-menu-button').click()");
        Assert.False(await HiddenListAsync(page));
        Assert.Equal("true", await page.TextAsync("document.querySelector('.tab-menu-button').getAttribute('aria-expanded')"));
        Assert.Equal("[\"/home\",\"/shopping\"]", await page.TextAsync("JSON.stringify([...document.querySelectorAll('.tab-menu-list a:not(.hidden)')].map(a => a.getAttribute('href')))"));

        await page.EvalAsync("document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))");
        Assert.True(await HiddenListAsync(page));
        Assert.True((await page.EvalAsync("document.activeElement === document.querySelector('.tab-menu-button')")).GetBoolean());

        await page.EvalAsync("document.querySelector('.tab-menu-button').click()");
        await page.EvalAsync("document.getElementById('log').dispatchEvent(new PointerEvent('pointerdown', { bubbles: true }))");
        Assert.True(await HiddenListAsync(page));

        await page.EvalAsync("document.querySelector('.tab-menu-button').click()");
        await page.EvalAsync("document.querySelector('.tab-menu-list a[href=\"/home\"]').click()");
        await page.WaitAsync("location.pathname === '/home' && document.querySelector('.tab-menu-button').textContent === '🏠 Home ▾'", "Home, the button naming it");
        Assert.True((await page.EvalAsync("document.querySelector('.tab-menu-button').classList.contains('here')")).GetBoolean());

        // The YouTube tab joins the menu once the PC says it is on (the Shopping tab's pulse).
        _backend.PulseNow = new WebPulse(false, 0, null, "", "tag", YouTubeTab: true);
        await page.GoAsync(Root + "shopping");
        await page.WaitAsync("!document.querySelector('nav a[href=\"/youtube\"]').classList.contains('hidden')", "YouTube in the menu");
        Assert.Equal("🛒 Shopping ▾", await page.TextAsync("document.querySelector('.tab-menu-button').textContent"));
        Assert.Equal(3, (await page.EvalAsync("document.querySelectorAll('.tab-menu-list a:not(.hidden)').length")).GetInt32());
    }

    /// <summary>With Home, Shopping and YouTube all off the menu goes, and Chat alone is left (2026-10-16).</summary>
    [WindowsFact]
    public async Task TheTabMenu_WithItsThreeTabsOff_Goes_ChatAlone()
    {
        _backend.Snap = new WebSnapshot([], [], State() with { HomeTab = false, ShoppingTab = false }, WebUiText.Words);
        if (await SignedInAsync("/") is not { } page)
        {
            return;
        }

        await page.WaitAsync("document.querySelector('header.top .tab-menu').classList.contains('hidden')", "the menu gone");
        Assert.False((await page.EvalAsync("document.querySelector('nav a[href=\"/\"]').classList.contains('hidden')")).GetBoolean());
    }

    /// <summary>At a phone's width the profile's name keeps its room beside Chat and the menu (2026-10-16): 40% of the header at least.</summary>
    [WindowsFact]
    public async Task TheHeader_AtAPhonesWidth_LeavesTheProfileItsRoom()
    {
        _backend.Home = new Home();
        if (await SignedInAsync("/home", width: 360) is not { } page)
        {
            return;
        }

        await page.WaitAsync("document.querySelector('header.top nav').classList.contains('ready') && document.querySelector('.tab-menu-button').textContent === '🏠 Home ▾'", "the nav, Home named");
        double share = (await page.EvalAsync("document.querySelector('header.top .who').getBoundingClientRect().width / document.querySelector('header.top').getBoundingClientRect().width")).GetDouble();
        Assert.True(share >= 0.4, $"the profile's name keeps {share:P0} of the header");
    }

    private static async Task<bool> HiddenListAsync(HeadlessPage page) =>
        (await page.EvalAsync("document.querySelector('.tab-menu-list').classList.contains('hidden')")).GetBoolean();

    [WindowsFact]
    public async Task APageWhoseTabIsTurnedOff_GoesOn_ToATabThatIsOn()
    {
        if (await SignedInAsync("/shopping") is not { } page)
        {
            return;
        }

        await page.WaitAsync("document.body.dataset.tab === 'shopping'", "the Shopping tab");
        _backend.TabsOn = new WebTabs(Chat: true, Home: false, Shopping: false);
        _backend.PulseNow = new WebPulse(false, 0, null, "", "tag", ChatTab: true, HomeTab: false, ShoppingTab: false);
        await page.WaitAsync("document.body.dataset.tab === 'chat' && location.pathname === '/'", "the page gone to the chat");

        // The Home tab learns from its own read's 404 (its pulse rides on that read): on to Shopping, the only tab on.
        _backend.Home = new Home();
        _backend.TabsOn = new WebTabs(Chat: false, Home: true, Shopping: true);
        await page.GoAsync(Root + "home");
        await page.WaitAsync("document.getElementById('tiles').children.length > 0", "the Home tab's tiles");
        _backend.TabsOn = new WebTabs(Chat: false, Home: false, Shopping: true);
        await page.WaitAsync("document.body.dataset.tab === 'shopping'", "Home gone, on to Shopping");

        // The chat page with Chat turned off: its stream ends, the pulse says so, and it goes on.
        _backend.TabsOn = WebTabs.All;
        await page.GoAsync(Root);
        await page.WaitAsync("document.body.dataset.tab === 'chat' && document.querySelector('.entry.user') !== null", "the chat again, its stream open");
        _backend.PulseNow = new WebPulse(false, 0, null, "", "tag", ChatTab: false, HomeTab: true, ShoppingTab: false);
        _backend.TabsOn = new WebTabs(Chat: false, Home: true, Shopping: false);
        Publish("state", Json(State()));   // the next batch: the stream ends instead of writing it
        await page.WaitAsync("document.body.dataset.tab === 'home'", "the chat gone to Home");
    }

    [WindowsFact]
    public async Task WithEveryTabOff_ThePageNamesTheSettings_AndOpensATabOnceOneIsOn()
    {
        _backend.TabsOn = new WebTabs(false, false, false);
        _backend.PulseNow = new WebPulse(false, 0, null, "", "tag", false, false, false);
        if (await SignedInAsync("/") is not { } page)
        {
            return;
        }

        await page.WaitAsync("document.getElementById('notabs') !== null", "the page that says none is on");
        string text = await page.TextAsync("document.getElementById('notabs').textContent");
        Assert.Contains("Web UI chat controls", text, StringComparison.Ordinal);
        Assert.Contains("/settings › Client › Web UI", text, StringComparison.Ordinal);

        _backend.TabsOn = new WebTabs(false, false, true);
        _backend.PulseNow = new WebPulse(false, 0, null, "", "tag", false, false, true);
        await page.WaitAsync("document.body.dataset.tab === 'shopping'", "the Shopping tab once it is on", 15);
    }

    // ── The ✕ in the message box ────────────────────────────────────────────

    [WindowsFact]
    public async Task TheDraftsCross_ShowsWithText_ClearsIt_AndItsUndoNeverOverwritesNewText()
    {
        _backend.Snap = new WebSnapshot([], [], State(), WebUiText.Words);
        if (await SignedInAsync("/") is not { } page)
        {
            return;
        }

        await page.WaitAsync("document.getElementById('clear-draft').getAttribute('aria-label') === " + JsonSerializer.Serialize(WebUiText.ClearDraft), "the words");
        Assert.True(await HiddenAsync(page, "clear-draft"));
        await page.EvalAsync("{ const d = document.getElementById('draft'); d.value = 'a long message'; d.dispatchEvent(new Event('input')); }");
        Assert.False(await HiddenAsync(page, "clear-draft"));

        await page.EvalAsync("document.getElementById('clear-draft').click()");
        Assert.Equal("", await page.TextAsync("document.getElementById('draft').value"));
        Assert.Equal("", await page.TextAsync("sessionStorage.getItem('neon.draft')"));
        Assert.True(await HiddenAsync(page, "clear-draft"));
        Assert.StartsWith(WebUiText.DraftCleared, await page.TextAsync("document.getElementById('message').textContent"), StringComparison.Ordinal);
        await page.EvalAsync("document.querySelector('#message button').click()");
        Assert.Equal("a long message", await page.TextAsync("document.getElementById('draft').value"));

        // Cleared again, then something typed: Undo leaves the new text alone.
        await page.EvalAsync("document.getElementById('clear-draft').click()");
        await page.EvalAsync("{ const d = document.getElementById('draft'); d.value = 'new'; d.dispatchEvent(new Event('input')); }");
        await page.EvalAsync("document.querySelector('#message button').click()");
        Assert.Equal("new", await page.TextAsync("document.getElementById('draft').value"));

        // A draft the app put on the box shows the ✕; a send empties it.
        await page.EvalAsync("document.getElementById('clear-draft').click()");
        Publish("draft", JsonSerializer.Serialize(new WebTextBack("from /prompts"), WebUiJsonContext.Default.WebTextBack));
        await page.WaitAsync("!document.getElementById('clear-draft').classList.contains('hidden')", "the ✕ after a draft event");
        await page.EvalAsync("document.getElementById('send').click()");
        await page.WaitAsync("document.getElementById('clear-draft').classList.contains('hidden')", "the ✕ gone after a send");
    }

    // ── The floating buttons ────────────────────────────────────────────────

    [WindowsFact]
    public async Task ExpandAll_OpensEveryFold_ClosesThem_AndAsAModeOpensTheNewOnes()
    {
        var diff = new WebDiff("Added 3 lines", [new WebDiffRow("+", 1, "a"), new WebDiffRow("+", 2, "b"), new WebDiffRow("+", 3, "c")], null, 3, "▸ 3 lines");
        _backend.Snap = new WebSnapshot(
        [
            new WebEntry(1, "assistant", "hi", null, "<p>hi</p>", false),
        ], [], State(), WebUiText.Words);
        if (await SignedInAsync("/") is not { } page)
        {
            return;
        }

        // Plain replies: nothing to fold, no ↕.
        await page.WaitAsync("document.querySelector('.entry.assistant') !== null", "the reply");
        await Task.Delay(200);
        Assert.True(await HiddenAsync(page, "expand-all"));

        Publish("append", JsonSerializer.Serialize(new WebEntry(2, "thinking", "deep", null, null, false) { Summary = "💭 thought", Ended = true }, WebUiJsonContext.Default.WebEntry));
        Publish("append", JsonSerializer.Serialize(new WebEntry(3, "run", "🛠️ 2 tool calls", null, null, false) { Keep = 1, Ended = true }, WebUiJsonContext.Default.WebEntry));
        Publish("append", JsonSerializer.Serialize(new WebEntry(4, "tool", "🛠️ a", null, null, false) { Run = 3 }, WebUiJsonContext.Default.WebEntry));
        Publish("append", JsonSerializer.Serialize(new WebEntry(5, "tool", "🛠️ b", null, null, false) { Run = 3 }, WebUiJsonContext.Default.WebEntry));
        Publish("append", JsonSerializer.Serialize(new WebEntry(6, "tool", "🛠️ edited x", null, null, false) { Diff = diff }, WebUiJsonContext.Default.WebEntry));
        await page.WaitAsync("!document.getElementById('expand-all').classList.contains('hidden')", "↕ once there is a fold");

        await page.EvalAsync("document.getElementById('expand-all').click()");
        Assert.True((await page.EvalAsync("[...document.querySelectorAll('details.think-fold, details.diff-fold')].every(d => d.open)")).GetBoolean());
        Assert.Equal(0, (await page.EvalAsync("document.querySelectorAll('.run .unit.hidden').length")).GetInt32());
        Assert.Equal("true", await page.TextAsync("document.getElementById('expand-all').getAttribute('aria-pressed')"));
        Assert.Equal(WebUiText.CollapseAll, await page.TextAsync("document.getElementById('expand-all').getAttribute('aria-label')"));

        // The mode: a run that arrives and ends starts open.
        Publish("append", JsonSerializer.Serialize(new WebEntry(7, "run", "🛠️ 2 more", null, null, false) { Keep = 1 }, WebUiJsonContext.Default.WebEntry));
        Publish("append", JsonSerializer.Serialize(new WebEntry(8, "tool", "🛠️ c", null, null, false) { Run = 7 }, WebUiJsonContext.Default.WebEntry));
        Publish("append", JsonSerializer.Serialize(new WebEntry(9, "tool", "🛠️ d", null, null, false) { Run = 7 }, WebUiJsonContext.Default.WebEntry));
        Publish("end", JsonSerializer.Serialize(new WebEnd(7, null), WebUiJsonContext.Default.WebEnd));
        await page.WaitAsync("document.querySelectorAll('.run').length === 2 && document.querySelectorAll('.run .unit').length === 4", "the second run");
        Assert.Equal(0, (await page.EvalAsync("document.querySelectorAll('.run .unit.hidden').length")).GetInt32());

        await page.EvalAsync("document.getElementById('expand-all').click()");
        Assert.True((await page.EvalAsync("[...document.querySelectorAll('details.think-fold, details.diff-fold')].every(d => !d.open)")).GetBoolean());
        Assert.True((await page.EvalAsync("document.querySelectorAll('.run .unit.hidden').length > 0")).GetBoolean());
        Assert.Equal("0", await page.TextAsync("sessionStorage.getItem('neon.expandAll')"));
    }

    [WindowsFact]
    public async Task ExpandAll_ReadsTheDeferredBlocks_TwoAtATime()
    {
        for (int i = 1; i <= 5; i++)
        {
            _backend.Entries[i] = new WebEntry(i, "thinking", "Whole " + i, null, null, false) { Summary = "s", Ended = true };
        }

        _backend.Snap = new WebSnapshot(
            [.. Enumerable.Range(1, 5).Select(i => new WebEntry(i, "thinking", "", null, null, false) { Summary = "s" + i, Ended = true, Deferred = true })],
            [], State(), WebUiText.Words);
        if (await SignedInAsync("/") is not { } page)
        {
            return;
        }

        await page.WaitAsync("!document.getElementById('expand-all').classList.contains('hidden')", "↕");
        // The reads held until the test lets each go, so the most open at once shows.
        await page.EvalAsync("""
            window.__open = 0; window.__max = 0; window.__held = [];
            const real = window.fetch;
            window.fetch = (url, options) => {
              if (!String(url).startsWith('/api/entry/')) return real(url, options);
              window.__open++;
              window.__max = Math.max(window.__max, window.__open);
              return new Promise(done => window.__held.push(() => { window.__open--; done(real(url, options)); }));
            };
            """);
        await page.EvalAsync("document.getElementById('expand-all').click()");
        await page.WaitAsync("window.__held.length === 2", "two reads open");
        await page.WaitAsync(
            "(window.__held.length && window.__held.shift()(), [...document.querySelectorAll('.entry.thinking .think-body')].every(b => b.textContent.startsWith('Whole')))",
            "every block read, one let go at a time");
        Assert.Equal(2, (await page.EvalAsync("window.__max")).GetInt32());
    }

    [WindowsFact]
    public async Task TheScrollButtons_ShowAwayFromTheirEnd_AndHideUnderTheUsageCard()
    {
        _backend.Snap = new WebSnapshot(Notices(3), [], State(), WebUiText.Words);
        if (await SignedInAsync("/") is not { } page)
        {
            return;
        }

        // A short transcript: neither.
        await page.WaitAsync("document.querySelectorAll('.entry.notice').length === 3", "three lines");
        await Task.Delay(200);
        Assert.True(await HiddenAsync(page, "to-top"));
        Assert.True(await HiddenAsync(page, "to-bottom"));

        _backend.Snap = new WebSnapshot(Notices(120), [], State(), WebUiText.Words);
        await page.GoAsync(Root);
        await page.WaitAsync("document.querySelectorAll('.entry.notice').length === 120", "a long transcript");
        await page.WaitAsync("!document.getElementById('to-top').classList.contains('hidden') && document.getElementById('to-bottom').classList.contains('hidden')", "at the bottom: ⤒ alone");
        Assert.Equal(WebUiText.ToTop, await page.TextAsync("document.getElementById('to-top').getAttribute('aria-label')"));

        await page.EvalAsync("document.getElementById('to-top').click()");
        await page.WaitAsync("window.scrollY < 5", "at the top");
        await page.WaitAsync("document.getElementById('to-top').classList.contains('hidden') && !document.getElementById('to-bottom').classList.contains('hidden')", "at the top: ⤓ alone");
        await page.EvalAsync("document.getElementById('to-bottom').click()");
        await page.WaitAsync("document.getElementById('to-bottom').classList.contains('hidden')", "back at the bottom");

        await page.EvalAsync("document.getElementById('usage').classList.remove('hidden'); document.getElementById('usage').click()");
        await page.WaitAsync("!document.getElementById('usage-card').classList.contains('hidden')", "the usage card");
        await page.WaitAsync("document.getElementById('to-top').classList.contains('hidden')", "the buttons hidden under the card");
    }

    [WindowsFact]
    public async Task TheKeyboardsInset_IsWhatTheViewportLosesAtTheBottom()
    {
        if (await SignedInAsync("/") is not { } page)
        {
            return;
        }

        Assert.Equal(300, (await page.EvalAsync("Neon.keyboardInset(800, { height: 500, offsetTop: 0 })")).GetInt32());
        Assert.Equal(250, (await page.EvalAsync("Neon.keyboardInset(800, { height: 500, offsetTop: 50 })")).GetInt32());
        Assert.Equal(0, (await page.EvalAsync("Neon.keyboardInset(800, { height: 800, offsetTop: 0 })")).GetInt32());
        Assert.Equal(0, (await page.EvalAsync("Neon.keyboardInset(800, null)")).GetInt32());
    }
}
