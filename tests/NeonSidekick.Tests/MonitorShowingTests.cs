using NeonSidekick.Monitors;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// Whether a monitor shows this PC (later on 2026-10-23, the user's report: the XG27UQDMS on HDMI-1 still read "extended" with nothing
/// to say it showed another machine): the rule's order, the cable's kinds, the 0x00 reply some LG monitors give, the guess, the saved
/// picks in <see cref="MonitorWords"/>, the app's own record, and the words the list and the questions say. Pure: no system.
/// </summary>
public sealed class MonitorShowingTests
{
    private static MonitorInfo Monitor(string connector = "DisplayPort", bool active = true, bool shared = false) =>
        new(2, FakeMonitorSystem.Xg, "ausaa34-uid4355", "XG27UQDMS", connector, active, false, null, null, shared);

    [Fact]
    public void Judge_TheRulesInOrder()
    {
        var m = Monitor();

        // Off the desktop or duplicated: can't tell, whatever else is known.
        Assert.Equal(MonitorShowingKind.Unknown, MonitorShowing.Judge(Monitor(active: false), 0x11, 0x0F, 0x0F).Kind);
        Assert.Equal(MonitorShowingKind.Unknown, MonitorShowing.Judge(Monitor(shared: true), 0x11, 0x0F, 0x0F).Kind);

        // A pick and a read decide, over the mark and the cable.
        Assert.Equal((MonitorShowingKind.ThisPc, MonitorShowingBasis.Picked), Of(MonitorShowing.Judge(m, 0x11, 0x0F, 0x11)));
        Assert.Equal((MonitorShowingKind.Elsewhere, MonitorShowingBasis.Picked), Of(MonitorShowing.Judge(m, 0x0F, null, 0x11)));

        // The app's mark stands with the read unread, even with a pick (a failed read never lost a mark).
        Assert.Equal((MonitorShowingKind.Elsewhere, MonitorShowingBasis.Switched), Of(MonitorShowing.Judge(m, null, 0x0F, null)));
        Assert.Equal((MonitorShowingKind.Elsewhere, MonitorShowingBasis.Switched), Of(MonitorShowing.Judge(m, null, 0x0F, 0x0F)));
        Assert.Equal((MonitorShowingKind.Elsewhere, MonitorShowingBasis.Switched), Of(MonitorShowing.Judge(m, 0x0F, 0x11, null)));

        // No read, no mark: can't tell (a pick with no read too).
        Assert.Equal(MonitorShowingKind.Unknown, MonitorShowing.Judge(m, null, null, null).Kind);
        Assert.Equal(MonitorShowingKind.Unknown, MonitorShowing.Judge(m, null, null, 0x0F).Kind);

        // Else the cable.
        Assert.Equal((MonitorShowingKind.Elsewhere, MonitorShowingBasis.Cable), Of(MonitorShowing.Judge(m, 0x11, null, null)));
        Assert.Equal((MonitorShowingKind.ThisPc, MonitorShowingBasis.Cable), Of(MonitorShowing.Judge(m, 0x0F, null, null)));
    }

    [Fact]
    public void Judge_A0x00Read_IsUnread_PickedOrNot()
    {
        // Some LG monitors answer a VCP read they don't support with no error and zeros (ddcutil issue #100); no input has 0x00.
        var m = Monitor();
        Assert.Equal(MonitorShowingKind.Unknown, MonitorShowing.Judge(m, 0x00, null, null).Kind);
        Assert.Equal(MonitorShowingKind.Unknown, MonitorShowing.Judge(m, 0x00, null, 0x0F).Kind);
        Assert.Equal(MonitorShowingKind.Elsewhere, MonitorShowing.Judge(m, 0x00, 0x0F, 0x0F).Kind);   // the mark still stands
        Assert.Null(MonitorShowing.Usable(0x00));
        Assert.Equal((byte)0x1B, MonitorShowing.Usable(0x1B));
    }

    [Theory]
    [InlineData("DisplayPort", 0x0F, true)]
    [InlineData("DisplayPort", 0x10, true)]
    [InlineData("DisplayPort", 0x11, false)]
    [InlineData("DisplayPort", 0x12, false)]
    [InlineData("DisplayPort", 0x1B, null)]   // a vendor code (USB-C is often 0x1B): no guess
    [InlineData("USB-C", 0x0F, true)]         // a USB-C connection carries DisplayPort
    [InlineData("USB-C", 0x11, false)]
    [InlineData("USB-C", 0x1B, null)]
    [InlineData("HDMI", 0x11, true)]
    [InlineData("HDMI", 0x0F, false)]
    [InlineData("HDMI", 0x03, null)]          // DVI on an HDMI cable: adapters are common, no guess
    [InlineData("DVI", 0x11, null)]
    [InlineData("Other", 0x0F, null)]
    [InlineData("Internal", 0x11, null)]
    public void CableMatches_TheTwoKinds(string connector, int code, bool? matches) =>
        Assert.Equal(matches, MonitorShowing.CableMatches(connector, (byte)code));

    [Fact]
    public void Counts_IsShowingOrCantTell()
    {
        var m = Monitor();
        Assert.True(MonitorShowing.Judge(m, 0x0F, null, null).Counts);
        Assert.True(MonitorShowing.Judge(m, null, null, null).Counts);   // can't tell: today's assumption
        Assert.False(MonitorShowing.Judge(m, 0x11, null, null).Counts);
    }

    [Fact]
    public void CableGuess_TheCurrentOfTheCablesKind_ElseTheOnlyOne()
    {
        var xg = Monitor();
        byte[] xgInputs = [0x0F, 0x11, 0x12];
        byte[] lgInputs = [0x11, 0x12, 0x0F, 0x10];
        Assert.Equal((byte)0x0F, MonitorShowing.CableGuess(xg, 0x11, xgInputs));   // its one DisplayPort input
        Assert.Equal((byte)0x10, MonitorShowing.CableGuess(xg, 0x10, lgInputs));   // the current one, of the cable's kind
        Assert.Null(MonitorShowing.CableGuess(xg, 0x11, lgInputs));                // two DisplayPort inputs: no guess
        Assert.Null(MonitorShowing.CableGuess(xg, 0x1B, lgInputs));                // a vendor current: no guess either
        Assert.Null(MonitorShowing.CableGuess(Monitor("Other"), 0x0F, xgInputs));
        Assert.Equal((byte)0x11, MonitorShowing.CableGuess(Monitor("HDMI"), 0x00, [0x0F, 0x11]));   // a 0x00 read is none: the only HDMI input
    }

    [Fact]
    public void Words_ThePicks_SetReplaceClear_AndAHandEditedFile()
    {
        var m = Monitor();
        var saved = MonitorWords.SetPc(null, "ausaa34-uid4355", 0x0F);
        Assert.Equal(["ausaa34-uid4355/0f"], saved);   // the entry shape, pinned
        saved = MonitorWords.SetPc(["gsm7768-uid4353/0f", .. saved!], "ausaa34-uid4355", 0x11);
        Assert.Equal(["gsm7768-uid4353/0f", "ausaa34-uid4355/11"], saved);
        Assert.Equal(["gsm7768-uid4353/0f"], MonitorWords.SetPc(saved, "ausaa34-uid4355", null));
        Assert.Null(MonitorWords.SetPc(["ausaa34-uid4355/11"], "ausaa34-uid4355", null));

        // A hand edit: a bad entry skipped, the first of two read, the next save back to one.
        string[] edited = ["garbage", "ausaa34-uid4355/zz", "ausaa34-uid4355/12", "ausaa34-uid4355/0f"];
        var words = new MonitorWords(null, null, [m], edited);
        Assert.Equal((byte)0x12, words.PcInput(m));
        Assert.Equal(["garbage", "ausaa34-uid4355/11"], MonitorWords.SetPc(edited, "ausaa34-uid4355", 0x11));
    }

    [Fact]
    public void Words_AMovedCable_KeepsItsPick_AndRowsMarkIt()
    {
        // The cable moved (another UID): the one saved key of the same model, as the names.
        var moved = Monitor() with { Key = "ausaa34-uid9999" };
        var words = new MonitorWords(null, null, [moved], ["ausaa34-uid4355/11"]);
        Assert.Equal("ausaa34-uid4355", words.SavedKey(moved));
        Assert.Equal((byte)0x11, words.PcInput(moved));
        var rows = words.Rows(moved, [0x0F, 0x11, 0x12], 0x0F, null);
        Assert.Equal([0x11], rows.Where(r => r.ThisPc).Select(r => (int)r.Code));
    }

    [Fact]
    public void Words_A0x00Current_IsNoRow()
    {
        var m = Monitor();
        var rows = MonitorWords.None([m]).Rows(m, [0x0F, 0x11], 0x00, null);
        Assert.Equal([0x0F, 0x11], rows.Select(r => (int)r.Code));
        Assert.DoesNotContain(rows, r => r.Current);
    }

    [Fact]
    public void Words_For_ReadsTheThreeSettings()
    {
        var m = Monitor();
        var settings = new AppSettingsData
        {
            MonitorInputNames = new Dictionary<string, string> { ["ausaa34-uid4355/11"] = "Work laptop" },
            MonitorInputsHidden = ["ausaa34-uid4355/12"],
            MonitorPcInputs = ["ausaa34-uid4355/0f"],
        };
        var words = MonitorWords.For(settings, [m]);
        Assert.Equal("Work laptop", words.Given(m, 0x11));
        Assert.True(words.Hidden(m, 0x12));
        Assert.Equal((byte)0x0F, words.PcInput(m));
    }

    [Fact]
    public void Away_MarksOnlyAShowingFrom_AndASwitchToThisPcClears()
    {
        var away = new MonitorAway();
        Assert.False(away.Switched("k", 0x11, 0x0F, fromCounts: false));   // from another input: no mark "left HDMI-1"
        Assert.Null(away.LeftFrom("k"));
        Assert.False(away.Switched("k", 0x00, 0x11));                     // a 0x00 from: none
        Assert.True(away.Switched("k", 0x0F, 0x11));                      // from this PC's: marked
        Assert.Equal((byte)0x0F, away.LeftFrom("k"));
        Assert.False(away.Switched("k", 0x11, 0x12));                     // the first left is kept
        Assert.True(away.Switched("k", 0x12, 0x10, toIsPc: true));        // to this PC's input (another code): cleared
        Assert.Null(away.LeftFrom("k"));
        Assert.False(away.Switched("k", 0x0F, 0x10, toIsPc: true));       // a switch to this PC's input marks nothing
    }

    [Fact]
    public void TheWords_ForEachBasis()
    {
        var m = Monitor();
        var words = MonitorWords.None([m]);
        Assert.Equal("not this PC's (DisplayPort-1)", MonitorText.ShowingWords(MonitorShowing.Judge(m, 0x11, null, 0x0F), words, m));
        Assert.Equal("not this PC's (the app switched it from DisplayPort-1)", MonitorText.ShowingWords(MonitorShowing.Judge(m, 0x11, 0x0F, null), words, m));
        Assert.Equal("not this PC's (guessed from the DisplayPort cable)", MonitorText.ShowingWords(MonitorShowing.Judge(m, 0x11, null, null), words, m));
        Assert.Equal("this PC's input", MonitorText.ShowingWords(MonitorShowing.Judge(m, 0x0F, null, 0x0F), words, m));
        Assert.Null(MonitorText.ShowingWords(MonitorShowing.Judge(m, 0x0F, null, null), words, m));   // a cable match says nothing

        Assert.Equal("", MonitorText.ElsewhereTail([], " · "));
        Assert.Equal(" · 2 XG27UQDMS shows another input", MonitorText.ElsewhereTail(["2 XG27UQDMS"], " · "));
        Assert.Equal("; 1 LG and 2 XG27UQDMS show other inputs", MonitorText.ElsewhereTail(["1 LG", "2 XG27UQDMS"], "; "));
        Assert.Equal("Now extended; 2 XG27UQDMS shows another input.", MonitorText.LayoutNow(MonitorArrangement.Extended, ["2 XG27UQDMS"]));
        Assert.Equal(MonitorText.LayoutNow(MonitorArrangement.Extended), MonitorText.LayoutNow(MonitorArrangement.Extended, []));
    }

    private static (MonitorShowingKind, MonitorShowingBasis?) Of(MonitorShowing showing) => (showing.Kind, showing.Basis);
}
