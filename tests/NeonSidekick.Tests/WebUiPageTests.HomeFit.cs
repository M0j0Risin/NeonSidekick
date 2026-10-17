using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// The rooms on a phone (2026-10-17, the user's screenshot: one open room's long names pushed every box and its switch past the card and the
/// page scrolled sideways): at 390 px nothing is wider than the screen, every switch sits inside the card, a row shows its short name (its
/// whole name its title, cut with an ellipsis when still too long) and its state under the name; a wide screen keeps the state on the
/// name's line.
/// </summary>
public sealed partial class WebUiPageTests
{
    private static IReadOnlyList<WebHomeRoom> LongRooms() =>
    [
        new WebHomeRoom("den", "area:den", "Den", true, "1 of 2 on", "on", null, "#ffcc00", true,
            [
                new WebHomeDevice("light.den_long", "💡", "Den Table Lamp Back With A Long Name", "on · 56%", null, "switch", true, "Toggle", false, null, "on", "#ffcc00",
                    Short: "Table Lamp Back With A Long Name That Runs On And On"),
                new WebHomeDevice("light.den_aura", "💡", "Den Aura", "on · 56%", null, "switch", true, "Toggle", false, null, "on", "#ffcc00", Short: "Aura"),
            ],
            [new WebHomeScene("scene.den_relax", "Den Relax", "Relax")]),
        new WebHomeRoom("bed", "area:bed", "Bedroom", false, "0 of 1 on", "off", null, null, false, [], []),
    ];

    [WindowsFact]
    public async Task Home_ThePhonesRooms_FitTheScreen_WithShortNames_AndTheStateUnderTheName()
    {
        _backend.Home = new Home { Rooms = LongRooms() };
        if (await SignedInAsync("/home", width: 390) is not { } page)
        {
            return;
        }

        await page.WaitAsync("document.querySelectorAll('.room-box .room-row-name').length === 2", "the open room's rows");
        await page.WaitAsync("document.documentElement.scrollWidth <= window.innerWidth", "no sideways scroll");
        Assert.True((await page.EvalAsync(
            "(() => { const card = document.getElementById('rooms').getBoundingClientRect(); return [...document.querySelectorAll('.room-switch')].every(s => s.getBoundingClientRect().right <= card.right + 0.5); })()")).GetBoolean());

        const string First = "document.querySelector('.room-box .room-row-name')";
        Assert.Equal("Table Lamp Back With A Long Name That Runs On And On", await page.TextAsync(First + ".textContent"));
        Assert.Equal("Den Table Lamp Back With A Long Name", await page.TextAsync(First + ".title"));
        Assert.True((await page.EvalAsync(First + ".scrollWidth > " + First + ".clientWidth")).GetBoolean());   // cut, with its ellipsis
        Assert.True((await page.EvalAsync(
            "(() => { const n = " + First + ".getBoundingClientRect(); const s = document.querySelector('.room-box .room-row-state').getBoundingClientRect(); return s.top >= n.bottom - 0.5; })()")).GetBoolean());

        // A wide screen: the state back on the name's line.
        await page.ViewportAsync(1000, mobile: false);
        await page.WaitAsync(
            "(() => { const n = " + First + ".getBoundingClientRect(); const s = document.querySelector('.room-box .room-row-state').getBoundingClientRect(); return s.top < n.bottom; })()",
            "the state on the name's line");
    }
}
