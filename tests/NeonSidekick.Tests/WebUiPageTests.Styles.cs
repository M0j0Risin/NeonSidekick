using NeonSidekick.Tests.Fakes;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// The transcript's roles on the page as the terminal draws them (2026-10-21, the user's call): a review's pass line in the theme's tertiary
/// style, and the user's lines per <i>User line style</i> in the theme's user style. The fake's <c>/theme.css</c> sets token values of its
/// own, so a match proves the page reads the theme's tokens, not <c>app.css</c>'s defaults.
/// </summary>
public sealed partial class WebUiPageTests
{
    private const string StyledSheet = ":root { --accent-3: #010203; --user: #040506; --text: #0a0b0c; }\n";

    private static Task<string> ComputedAsync(HeadlessPage page, string selector, string property) =>
        page.TextAsync("getComputedStyle(document.querySelector('" + selector + "'))." + property);

    [WindowsFact]
    public async Task Chat_AProgressNotice_WearsTheTertiaryStyle_AndAPlainOneStaysANotice()
    {
        _backend.Css = StyledSheet;
        _backend.Snap = new WebSnapshot(
            [new WebEntry(1, "notice", "pass 1", null, null, false) { Tone = "progress" }, new WebEntry(2, "notice", "plain", null, null, false)],
            [], State(), WebUiText.Words);
        if (await SignedInAsync("/") is not { } page)
        {
            return;
        }

        await page.WaitAsync("document.querySelectorAll('#log .entry.notice').length === 2", "both notices");
        const string Progress = "#log .entry.notice.tone-progress";
        const string Plain = "#log .entry.notice:not([class*=tone-])";
        Assert.Equal("rgb(1, 2, 3)", await ComputedAsync(page, Progress, "color"));
        Assert.Equal("700", await ComputedAsync(page, Progress, "fontWeight"));
        Assert.Equal("normal", await ComputedAsync(page, Progress, "fontStyle"));
        Assert.NotEqual("rgb(1, 2, 3)", await ComputedAsync(page, Plain, "color"));
        Assert.Equal("italic", await ComputedAsync(page, Plain, "fontStyle"));
    }

    [WindowsFact]
    public async Task Chat_TheUsersLines_FollowUserLineStyle_BoldByDefault()
    {
        _backend.Css = StyledSheet;
        _backend.Snap = new WebSnapshot([new WebEntry(1, "user", "hello", null, null, false)], [], State(), WebUiText.Words);
        if (await SignedInAsync("/") is not { } page)
        {
            return;
        }

        // No word in the state (an older app): bold, the whole line in the user style.
        const string Line = "#log .entry.user";
        await page.WaitAsync("document.querySelector('" + Line + "') !== null && document.body.dataset.userLine === 'bold'", "the user's line, bold");
        Assert.Equal("rgb(4, 5, 6)", await ComputedAsync(page, Line, "color"));
        Assert.Equal("700", await ComputedAsync(page, Line, "fontWeight"));

        // Quiet: the glyph in the user color, the words in the body ink.
        Publish("state", Json(State() with { UserLine = "quiet" }));
        await page.WaitAsync("document.body.dataset.userLine === 'quiet'", "quiet");
        Assert.Equal("rgb(10, 11, 12)", await ComputedAsync(page, Line, "color"));
        Assert.Equal("rgb(4, 5, 6)", await ComputedAsync(page, Line + " .glyph", "color"));
        Assert.Equal("400", await ComputedAsync(page, Line, "fontWeight"));

        // Slab: the line on the faint fill.
        Publish("state", Json(State() with { UserLine = "slab" }));
        await page.WaitAsync("document.body.dataset.userLine === 'slab'", "slab");
        Assert.Equal("rgb(10, 11, 12)", await ComputedAsync(page, Line, "color"));
        Assert.NotEqual("rgba(0, 0, 0, 0)", await ComputedAsync(page, Line, "backgroundColor"));
    }
}
