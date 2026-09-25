using NeonSidekick.UI;
using Spectre.Console;
using Spectre.Console.Testing;

namespace NeonSidekick.Tests;

public class MentionCompleterTests
{
    [Fact]
    public void Wording_IsPinned()
    {
        Assert.Equal(8, MentionCompleter.MaxRows);
    }

    [Theory]
    [InlineData("@t", 2, 0, 2, "t")]                       // at the start
    [InlineData("see @test/th", 12, 4, 12, "test/th")]     // after a space
    [InlineData("see\n@te", 7, 4, 7, "te")]                // after a line break
    [InlineData("@test/thing.txt more", 6, 0, 15, "test/")] // the cursor inside the word: the query is what is before it
    [InlineData("@", 1, 0, 1, "")]                         // a bare @: an empty query
    [InlineData("@t ", 2, 0, 2, "t")]                      // the cursor at the word's end, a space after
    public void TryFind_FindsTheAtWordUnderTheCursor(string text, int cursor, int start, int end, string query)
    {
        Assert.True(MentionCompleter.TryFind(text, cursor, out int s, out int e, out string q));
        Assert.Equal(start, s);
        Assert.Equal(end, e);
        Assert.Equal(query, q);
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("hello", 5)]
    [InlineData("@t", 0)]           // the cursor before the @
    [InlineData("@t ", 3)]          // the cursor after the space that ended the word
    [InlineData("a@b.com", 7)]      // the @ inside a word
    [InlineData("mail a@b", 8)]
    public void TryFind_IsFalseWithoutAnAtWordUnderTheCursor(string text, int cursor)
    {
        Assert.False(MentionCompleter.TryFind(text, cursor, out _, out _, out string q));
        Assert.Equal("", q);
    }

    [Theory]
    [InlineData("#h", 2, 0, 2, "h")]
    [InlineData("see #wea", 8, 4, 8, "wea")]
    [InlineData("#", 1, 0, 1, "")]
    public void TryFind_WithATrigger_FindsTheHashWord_AndNeverTheAtWord(string text, int cursor, int start, int end, string query)
    {
        // The same word rule under any trigger (2026-09-17): # for the skill list.
        Assert.True(MentionCompleter.TryFind(text, cursor, '#', out int s, out int e, out string q));
        Assert.Equal((start, end, query), (s, e, q));
        Assert.False(MentionCompleter.TryFind(text, cursor, out _, out _, out _));
        Assert.False(MentionCompleter.TryFind(text.Replace('#', '@'), cursor, '#', out _, out _, out _));
    }

    [Theory]
    [InlineData("issue#1", 7)]     // the # inside a word
    [InlineData("#h", 0)]          // the cursor before the #
    [InlineData("# heading", 2)]   // the cursor after the space that ended the word
    public void TryFind_WithATrigger_IsFalseWithoutAHashWordUnderTheCursor(string text, int cursor)
    {
        Assert.False(MentionCompleter.TryFind(text, cursor, '#', out _, out _, out string q));
        Assert.Equal("", q);
    }

    [Theory]
    [InlineData("$r", 2, 0, 2, "r", true)]
    [InlineData("use $read", 9, 4, 9, "read", true)]
    [InlineData("$", 1, 0, 1, "", true)]
    [InlineData("cost$5", 6, 0, 0, "", false)]   // the $ inside a word
    public void TryFind_WithTheDollarTrigger_IsTheSameWordRule(string text, int cursor, int start, int end, string query, bool found)
    {
        // The $-mention list (2026-09-19) reads the trigger the # list reads: nothing in MentionCompleter changed.
        Assert.Equal(found, MentionCompleter.TryFind(text, cursor, '$', out int s, out int e, out string q));
        if (found)
        {
            Assert.Equal((start, end, query), (s, e, q));
        }

        Assert.False(MentionCompleter.TryFind(text, cursor, '#', out _, out _, out _));
    }

    [Fact]
    public void MentionList_PrefixIsEmptyUnlessSet()
    {
        var list = new MentionList(0, 2, "h", ["haiku"], false, 0, 0);
        Assert.Equal("", list.Prefix);
        Assert.Equal("#", (list with { Prefix = "#" }).Prefix);
        Assert.Equal("$", (list with { Prefix = "$" }).Prefix);
    }

    [Fact]
    public void TryFind_APasteTokenIsAWordBoundary()
    {
        string text = "\uE000@te";
        Assert.True(MentionCompleter.TryFind(text, 4, out int start, out _, out string query));
        Assert.Equal(1, start);
        Assert.Equal("te", query);
        Assert.False(MentionCompleter.TryFind("@te\uE000x", 5, out _, out _, out _));
    }

    [Fact]
    public void Apply_ReplacesTheWord_WithASpaceUnlessRemaining()
    {
        Assert.Equal(("@test/thing.txt ", 16), MentionCompleter.Apply("@t", 0, 2, "@test/thing.txt", space: true));
        Assert.Equal(("see @test/thing.txt  now", 20), MentionCompleter.Apply("see @te/thi now", 4, 11, "@test/thing.txt", space: true));
        Assert.Equal(("@test/", 6), MentionCompleter.Apply("@t", 0, 2, "@test/", space: false));
        // A folder applied (not remained) reads like a file: a mention and a space.
        Assert.Equal(("@test/ ", 7), MentionCompleter.Apply("@t", 0, 2, "@test/", space: true));
        // A command or a skill name: the word itself and a space.
        Assert.Equal(("/settings ", 10), MentionCompleter.Apply("/se", 0, 3, "/settings", space: true));
        Assert.Equal(("/skill haiku  now", 13), MentionCompleter.Apply("/skill ha now", 7, 9, "haiku", space: true));
    }

    [Theory]
    [InlineData("/", 1, 1, "/")]                       // the bare slash: every command
    [InlineData("/se", 3, 3, "/se")]
    [InlineData("/set", 3, 4, "/se")]                  // the cursor inside the word: the query is what is before it
    [InlineData("/settings", 9, 9, "/settings")]      // the full word (Matches then closes the list)
    [InlineData("/skill haiku", 6, 6, "/skill")]      // the cursor at the end of the command word, the argument after
    public void TryFindCommand_FindsTheSlashWordAtTheStart(string text, int cursor, int end, string query)
    {
        Assert.True(MentionCompleter.TryFindCommand(text, cursor, out int foundEnd, out string found));
        Assert.Equal(end, foundEnd);
        Assert.Equal(query, found);
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("/", 0)]                 // the cursor before the slash
    [InlineData("/settings x", 11)]      // past the word
    [InlineData("/settings x", 10)]
    [InlineData("see /tmp", 8)]          // not at the start
    [InlineData(" /help", 6)]            // leading whitespace
    [InlineData("hello", 5)]
    public void TryFindCommand_IsNoneElsewhere(string text, int cursor)
    {
        Assert.False(MentionCompleter.TryFindCommand(text, cursor, out _, out _));
    }

    [Theory]
    [InlineData("/tts ", 5, "/tts", 5, 5, "")]                             // nothing typed yet: every candidate
    [InlineData("/skill haiku", 12, "/skill", 7, 12, "haiku")]
    [InlineData("/skill haiku", 9, "/skill", 7, 12, "ha")]                 // the cursor inside the word
    [InlineData("/Skill  we", 10, "/Skill", 8, 10, "we")]                  // any case, any run of whitespace; the command as typed
    [InlineData("/prof w", 7, "/prof", 6, 7, "w")]                         // any /word: the app judges it (an unknown one gets nothing)
    [InlineData("/profile delete work", 18, "/profile", 9, 20, "delete wo")]   // the whole argument so far, spaces included
    [InlineData("/profile delete ", 16, "/profile", 9, 16, "delete ")]     // the cursor on whitespace: nothing after it to replace
    [InlineData("/timer stop the big", 19, "/timer", 7, 19, "stop the big")]
    [InlineData("/skill haiku m", 14, "/skill", 7, 14, "haiku m")]         // a word after a complete one: the source matches nothing
    public void TryFindArgument_FindsTheArgumentAfterTheCommand(string text, int cursor, string command, int start, int end, string query)
    {
        Assert.True(MentionCompleter.TryFindArgument(text, cursor, out string foundCommand, out int foundStart, out int foundEnd, out string found));
        Assert.Equal(command, foundCommand);
        Assert.Equal(start, foundStart);
        Assert.Equal(end, foundEnd);
        Assert.Equal(query, found);
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("/tts", 4)]              // no whitespace after the command yet
    [InlineData("/tts ", 3)]             // the cursor on the command word
    [InlineData("/tts ", 0)]
    [InlineData("/tts  x", 5)]           // the cursor inside the whitespace run before more text
    [InlineData("x /tts ", 7)]           // not at the start
    [InlineData("hello there", 11)]
    public void TryFindArgument_IsNoneElsewhere(string text, int cursor)
    {
        Assert.False(MentionCompleter.TryFindArgument(text, cursor, out _, out _, out _, out _));
    }

    [Fact]
    public void Matches_KeepsThePrefixMatchesInOrder_AndNoneOnTheFullWord()
    {
        IReadOnlyList<CompletionItem> items = [new("/settings", "edit"), new("/server", "pick"), new("/skill", "load"), new("/skills", "list"), new("/help", "show")];

        Assert.Equal(items, MentionCompleter.Matches(items, ""));
        Assert.Equal(["/settings", "/server", "/skill", "/skills"], MentionCompleter.Matches(items, "/s").Select(i => i.Text));
        Assert.Equal(["/settings", "/server"], MentionCompleter.Matches(items, "/SE").Select(i => i.Text));
        // The word typed in full: no list, so Enter sends — /skill is done even though /skills starts with it.
        Assert.Empty(MentionCompleter.Matches(items, "/skill"));
        Assert.Empty(MentionCompleter.Matches(items, "/SKILLS"));
        Assert.Empty(MentionCompleter.Matches(items, "/x"));
        Assert.Empty(MentionCompleter.Matches([], "/"));
    }

    [Fact]
    public void WordRows_PadsTheWords_DimsTheNotes_AndCutsALongNoteAtTheEdge()
    {
        var list = new MentionList(0, 2, "/s", ["/server", "/settings", "/skill"], false, 1, 0) { Notes = ["pick a server", "edit [x] and save the settings for good", "load"] };
        Assert.True(list.IsWordList);
        Assert.False(new MentionList(0, 2, "t", ["a"], false, 0, 0).IsWordList);

        var (rows, first) = MentionCompleter.WordRows(list, capacity: 8);

        Assert.Equal(0, first);
        Assert.Equal(3, rows.Count);
        // Rendered at 30 cells: the pointer, the word padded to the longest + the gap, the note — cut with an ellipsis, never wrapped.
        var console = new TestConsole { EmitAnsiSequences = false };
        console.Profile.Width = 30;
        console.Write(new Rows(rows));
        Assert.Equal(
            [
                MenuPane.NoPointer + "/server    pick a server",
                MenuPane.Pointer + "/settings  edit [x] and sav…",
                MenuPane.NoPointer + "/skill     load",
            ],
            console.Output.TrimEnd().Split('\n').Select(l => l.TrimEnd()));
        Assert.Equal(MenuPane.Pointer + "/settings  edit [x] and save the settings for good", MentionCompleter.WordRowText("/settings", "edit [x] and save the settings for good", 11, active: true));

        // The padding follows the words in view, not the whole list; the more row when the view is cut.
        (rows, first) = MentionCompleter.WordRows(list with { Cursor = 2 }, capacity: 2);
        Assert.Equal(2, first);
        Assert.Equal(2, rows.Count);
        console = new TestConsole { EmitAnsiSequences = false };
        console.Profile.Width = 30;
        console.Write(new Rows(rows));
        Assert.Equal([MenuPane.Pointer + "/skill  load", MenuPane.NoPointer + MenuPane.MoreHint], console.Output.TrimEnd().Split('\n').Select(l => l.TrimEnd()));

        Assert.Throws<ArgumentException>(() => MentionCompleter.WordRows(new MentionList(0, 2, "t", ["a"], false, 0, 0), 8));
    }

    [Fact]
    public void Move_WrapsAtBothEnds()
    {
        var list = new MentionList(0, 2, "t", ["a", "b", "c"], false, 0, 0);
        Assert.Equal(2, list.Move(-1).Cursor);
        Assert.Equal(1, list.Move(+1).Cursor);
        Assert.Equal(0, list.Move(+3).Cursor);
        Assert.Equal(0, (list with { Matches = [] }).Move(+1).Cursor);
    }

    [Fact]
    public void Rows_PointTheCursor_AndCutWithTheMoreRow()
    {
        var list = new MentionList(0, 2, "t", ["test/", "test/bling.txt", "test/thing.txt"], false, 1, 0);

        var (rows, first) = MentionCompleter.Rows(list, capacity: 8);

        Assert.Equal(0, first);
        Assert.Equal(new[]
        {
            MenuPane.NoPointer + "test/",
            MenuPane.RowMarkup("test/bling.txt", active: true),
            MenuPane.NoPointer + "test/thing.txt",
        }, rows);
        Assert.Contains($"[{Theme.MenuHighlight.ToMarkup()}]{MenuPane.Pointer}", rows[1]);

        // Two rows of room for three matches: one match row and the more row; the viewport follows the cursor.
        (rows, first) = MentionCompleter.Rows(list with { Cursor = 2 }, capacity: 2);
        Assert.Equal(2, first);
        Assert.Equal(new[] { MenuPane.RowMarkup("test/thing.txt", active: true), Theme.DimMarkup(MenuPane.NoPointer + MenuPane.MoreHint) }, rows);
    }

    [Fact]
    public void Rows_ATruncatedList_KeepsARowForTheNote_AndEscapesPaths()
    {
        var list = new MentionList(0, 2, "t", ["a[1].txt", "b.txt", "c.txt"], true, 0, 0);

        var (rows, _) = MentionCompleter.Rows(list, capacity: 3);

        // Three rows of room less the note = two, so one match row and the more row, then the note.
        Assert.Equal(new[]
        {
            MenuPane.RowMarkup(Markup.Escape("a[1].txt"), active: true),
            Theme.DimMarkup(MenuPane.NoPointer + MenuPane.MoreHint),
            Theme.DimMarkup(MenuPane.NoPointer + MentionCompleter.TruncatedRow),
        }, rows);
    }
}
