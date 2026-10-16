using System.Text;
using NeonSidekick.ActiveDirectory;
using NeonSidekick.Files;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// One directory answer drawn three ways (2026-10-15, the AD gaps plan, §6 and §1b): the card's key/value labels padded per block and its tables in
/// columns under a header (wide characters counted right, the last column unpadded, a too-wide row giving way in its flexible column only, a
/// stranger's control characters cleaned), every line within the width with its row's target, the copy unbounded; the CSV's header the card's
/// columns, RFC 4180 quoting, the formula guard, dates only in date columns, two tables as one sheet; the transcript's note naming a save.
/// </summary>
public sealed class DirectoryRenderTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly DateTimeOffset When = new(2026, 10, 14, 10, 0, 0, TimeSpan.Zero);

    private static DirectoryTable List(params DirectoryColumn[] columns) => new(columns);

    private static DirectoryRow Row(string account, string name, DateTimeOffset? last = null, bool opens = true, string? dn = null) =>
        new("model " + account, [account, name, DirectoryCell.At(last)], dn ?? "CN=" + name + ",DC=x", name, opens);

    private static DirectoryAnswer Stale()
    {
        var answer = new DirectoryAnswer { Head = "users not signed in for 90 days in lab: 3", Zone = Utc, Now = When };
        var main = List(new(DirectoryText.ColAccount), new(DirectoryText.ColName, Flexible: true), new(DirectoryText.ColLastSignIn, Date: true));
        main.Rows.Add(Row("alice", "Alice Smith", When));
        main.Rows.Add(Row("bo", "山田太郎 🐙", When.AddDays(-3)));
        answer.Tables.Add(main);
        var never = new DirectoryTable(main.Columns) { Title = "never signed in (1)", TitleInModel = true, Section = "never signed in" };
        never.Rows.Add(Row("carol", "Carol King"));
        answer.Tables.Add(never);
        return answer;
    }

    // ── The card ───────────────────────────────────────────────────────────

    [Fact]
    public void Card_LabelsAlignedPerBlock()
    {
        var answer = new DirectoryAnswer { Head = "User ⟪alice⟫ in lab: enabled" };
        answer.Fields.Add(new DirectoryField("name", "⟪Alice Smith⟫"));
        answer.Fields.Add(new DirectoryField("last sign-in", "never"));
        var lines = DirectoryRender.ForCard(answer, 80).Select(l => l.Text).ToList();
        Assert.Equal(["User alice in lab: enabled", "  name:         Alice Smith", "  last sign-in: never"], lines);
    }

    [Fact]
    public void Card_ColumnsAligned_WideCharactersCounted_TheLastColumnUnpadded()
    {
        var lines = DirectoryRender.ForCard(Stale(), 120);
        var header = lines.First(l => l.Style == DirectoryCardStyle.Header).Text;   // the main table's
        var alice = lines.First(l => l.Text.Contains("alice", StringComparison.Ordinal)).Text;
        var bo = lines.First(l => l.Text.Contains("bo ", StringComparison.Ordinal)).Text;
        int column = TextCells.Width(header[..header.IndexOf(DirectoryText.ColLastSignIn, StringComparison.Ordinal)]);
        Assert.Equal(column, TextCells.Width(alice[..alice.IndexOf("2026-10-14", StringComparison.Ordinal)]));
        Assert.Equal(column, TextCells.Width(bo[..bo.IndexOf("2026-10-11", StringComparison.Ordinal)]));   // 山田太郎 and the emoji two cells each
        Assert.EndsWith("2026-10-14 10:00", alice);   // the last column unpadded, the date in the card's short form
        Assert.Contains(lines, l => l.Text == "never signed in (1)" && l.Style == DirectoryCardStyle.Title);   // at the left, as the model's "never signed in (1):"
        Assert.Contains(lines, l => l.Text.StartsWith("  carol", StringComparison.Ordinal) && l.Text.EndsWith(DirectoryRender.NoValue, StringComparison.Ordinal));   // no value: the dash
    }

    [Fact]
    public void Card_ATooWideRow_ShrinksItsFlexibleColumnOnly_NeverAnAccountOrADate()
    {
        var answer = Stale();
        answer.Tables[0].Rows.Add(Row("dave_the_long_account", new string('n', 80), When));
        var lines = DirectoryRender.ForCard(answer, 60);
        Assert.All(lines, l => Assert.True(TextCells.Width(l.Text) <= 60, l.Text));
        var dave = lines.Single(l => l.Text.Contains("dave_the_long_account", StringComparison.Ordinal)).Text;
        Assert.EndsWith("2026-10-14 10:00", dave);   // the date whole
        Assert.Contains("…", dave);                  // the name cut
    }

    [Fact]
    public void Card_ALongValue_ContinuesUnderTheValueColumn_AndEveryLineFits()
    {
        var answer = new DirectoryAnswer { Head = "Computer ⟪WS01⟫ in lab: enabled" };
        answer.Fields.Add(new DirectoryField("os", "⟪Windows⟫"));
        answer.Fields.Add(new DirectoryField("description", "⟪" + string.Join(' ', Enumerable.Repeat("words", 30)) + "⟫"));
        var lines = DirectoryRender.ForCard(answer, 40).Select(l => l.Text).ToList();
        Assert.All(lines, l => Assert.True(TextCells.Width(l) <= 40, l));
        int value = "  description: ".Length;
        Assert.StartsWith("  description: words", lines[2]);
        Assert.StartsWith(new string(' ', value) + "words", lines[3]);
    }

    [Fact]
    public void Copy_IsTheCardUnbounded_NothingCut()
    {
        var answer = Stale();
        answer.Tables[0].Rows.Add(Row("dave", new string('n', 300), When));
        Assert.Contains(DirectoryRender.CardText(answer), l => l.Contains(new string('n', 300), StringComparison.Ordinal));
    }

    [Fact]
    public void Card_AStrangersEscapeTabBreakAndOverride_DrawAsOneCleanRow()
    {
        var answer = new DirectoryAnswer { Head = "h" };
        var table = List(new(DirectoryText.ColAccount), new(DirectoryText.ColName, Flexible: true));
        table.Rows.Add(new DirectoryRow("m", ["x", "evil\u001b[2Jname\tand\nmore\u202Eend"], "CN=x", "x", true));
        answer.Tables.Add(table);
        var row = DirectoryRender.ForCard(answer, 200).Last().Text;
        Assert.DoesNotContain('\u001b', row);
        Assert.DoesNotContain('\n', row);
        Assert.DoesNotContain('\t', row);
        Assert.DoesNotContain('\u202E', row);
        Assert.Contains("name and more", row);
        Assert.Equal("a b c", DirectoryRender.Clean("a\tb\nc\u2066\u202A"));
    }

    /// <summary>A wrapped row's lines share its target; a contact's row has none; two rows reading alike keep their own targets, found by index.</summary>
    [Fact]
    public void Card_Targets_ByLine()
    {
        var answer = new DirectoryAnswer { Head = "Group ⟪Staff⟫ in lab: 3 members, direct" };
        var table = List(new(DirectoryText.ColName, Flexible: true), new(DirectoryText.ColKind), new(DirectoryText.ColAccount));
        table.Rows.Add(new DirectoryRow("a", ["John Smith", "user", "jsmith"], "CN=John Smith,OU=A,DC=x", "John Smith", true));
        table.Rows.Add(new DirectoryRow("b", ["John Smith", "user", "jsmith"], "CN=John Smith,OU=B,DC=x", "John Smith", true));
        table.Rows.Add(new DirectoryRow("c", ["Vendor Rep", "contact", null], "CN=Vendor Rep,DC=x", "Vendor Rep", Opens: false));
        answer.Tables.Add(table);
        answer.Fields.Add(new DirectoryField("managed by", "⟪" + new string('m', 120) + "⟫", new DirectoryRowTarget("CN=Owner,DC=x", "Owner")));
        var lines = DirectoryRender.ForCard(answer, 40);
        var rows = lines.Where(l => l.Text.Contains("John Smith", StringComparison.Ordinal)).ToList();
        Assert.Equal(["CN=John Smith,OU=A,DC=x", "CN=John Smith,OU=B,DC=x"], rows.Select(r => r.Target!.Dn));
        Assert.Null(lines.Single(l => l.Text.Contains("Vendor", StringComparison.Ordinal)).Target);
        var owner = lines.Where(l => l.Target?.Dn == "CN=Owner,DC=x").ToList();
        Assert.True(owner.Count > 1);   // the wrapped value's lines all open the owner
        var view = new DirectoryCardView(answer);
        using var console = new Spectre.Console.Testing.TestConsole();
        var segments = view.Render(Spectre.Console.Rendering.RenderOptions.Create(console, console.Profile.Capabilities), 40).ToList();
        Assert.Equal(lines.Count, Spectre.Console.Rendering.Segment.SplitLines(segments).Count);   // one segment line per card line: index n is line n
        Assert.Equal("CN=John Smith,OU=B,DC=x", view.TargetAt(lines.ToList().IndexOf(rows[1]))!.Dn);
        Assert.Null(view.TargetAt(0));
    }

    /// <summary>Every table's model lines and cells come from the same rows: the same count, order and account in both.</summary>
    [Fact]
    public void EveryTable_ItsModelLinesAndCells_TheSameRows()
    {
        var answer = Stale();
        string model = DirectoryRender.ForModel(answer);
        var card = DirectoryRender.CardText(answer);
        foreach (var table in answer.Tables)
        {
            var modelLines = table.ShownRows.Select(r => r.ModelLine).ToList();
            var cardRows = card.Where(l => table.ShownRows.Any(r => l.TrimStart().StartsWith(r.Cells[0]!.Text!, StringComparison.Ordinal))).ToList();
            Assert.Equal(modelLines.Count, cardRows.Count);
            for (int i = 0; i < modelLines.Count; i++)
            {
                Assert.Contains("\n  " + modelLines[i], model);
                Assert.StartsWith("  " + table.Rows[i].Cells[0]!.Text, cardRows[i]);
                Assert.Contains(table.Rows[i].Cells[0]!.Text!, modelLines[i]);
            }
        }
    }

    // ── The CSV ────────────────────────────────────────────────────────────

    [Fact]
    public void Csv_QuotesAndGuardsFormulas_AndAnEmptyCellWhereTheCardShowsADash()
    {
        Assert.Equal("'=HYPERLINK(1)", DirectoryCsv.Cell("=HYPERLINK(1)"));
        Assert.Equal("'+1", DirectoryCsv.Cell("+1"));
        Assert.Equal("'-1", DirectoryCsv.Cell("-1"));
        Assert.Equal("'@x", DirectoryCsv.Cell("@x"));
        Assert.Equal("\"a, \"\"b\"\"\"", DirectoryCsv.Cell("a, \"b\""));
        Assert.Equal("\"one\ntwo\"", DirectoryCsv.Cell("one\ntwo"));
        var answer = new DirectoryAnswer { Head = "h", Zone = Utc, Now = When };
        var table = List(new(DirectoryText.ColAccount), new(DirectoryText.ColName));
        table.Rows.Add(new DirectoryRow("m", [null, "Vendor Rep"], "CN=Vendor Rep,DC=x"));
        answer.Tables.Add(table);
        string csv = DirectoryCsv.Text(answer)!;
        Assert.Equal("account,name,dn\r\n,Vendor Rep,\"CN=Vendor Rep,DC=x\"\r\n", csv);
        Assert.Contains(DirectoryRender.NoValue, DirectoryRender.CardText(answer).Last());
    }

    [Fact]
    public void Csv_TwoTablesAsOneSheet_DatesOnlyDates_AndTheHeaderIsTheCardsColumns()
    {
        var answer = Stale();
        var lines = DirectoryCsv.Text(answer)!.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("account,name,last sign-in (UTC+00:00),section,dn", lines[0]);
        Assert.Equal("alice,Alice Smith,2026-10-14 10:00,,\"CN=Alice Smith,DC=x\"", lines[1]);
        Assert.Equal("carol,Carol King,,never signed in,\"CN=Carol King,DC=x\"", lines[3]);   // a date column holds a date or nothing
        var one = new DirectoryAnswer { Head = "h", Zone = Utc };
        one.Tables.Add(answer.Tables[0]);
        Assert.Equal("account,name,last sign-in (UTC+00:00),dn", DirectoryCsv.Text(one)!.Split("\r\n")[0]);   // no section column with one table
        var header = DirectoryRender.ForCard(one, 200).Single(l => l.Style == DirectoryCardStyle.Header).Text.Split("  ", StringSplitOptions.RemoveEmptyEntries).Select(c => c.Trim());
        Assert.Equal(one.Tables[0].Columns.Select(c => c.Name), header);
        var withLink = new DirectoryAnswer { Head = "h" };
        var links = new DirectoryTable([new(DirectoryText.ColName)], list: false);
        links.Rows.Add(new DirectoryRow("x", ["Ops"], "CN=Ops,DC=x"));
        withLink.Fields.Add(new DirectoryField("member of (1)", "⟪Ops⟫", Links: links));
        Assert.Null(DirectoryCsv.Text(withLink));   // a card's link lists are never saved
    }

    [Fact]
    public void Csv_TheZoneIsNamed_AndAFolderGetsANameFromTheQuestion()
    {
        Assert.Equal("UTC-05:00", DirectoryCsv.ZoneLabel(TimeZoneInfo.CreateCustomTimeZone("x", TimeSpan.FromHours(-5), "x", "x"), When));
        Assert.Equal("ad-group-staff.csv", DirectoryCsv.NameFor("group Staff"));
        Assert.Equal("ad-ou-neon-engineering.csv", DirectoryCsv.NameFor("ou Neon/Engineering"));
        Assert.Equal("ad-find-stale_users.csv", DirectoryCsv.NameFor("find stale_users"));
        Assert.True(DirectoryCsv.NameFor(new string('x', 200)).Length <= DirectoryCsv.MaxNameChars + 4);
    }

    // ── The transcript's note ──────────────────────────────────────────────

    [Fact]
    public void Note_NamesTheSave_WithinTwoHundred_TheNamesEndVisible()
    {
        Assert.Equal("(" + DirectoryText.Glyph + " Group ⟪Ops⟫ in lab: 4 members, direct)", DirectoryText.Note("Group ⟪Ops⟫ in lab: 4 members, direct\n  ⟪a⟫"));
        string saved = DirectoryText.Saved(3212, "staff-users.csv", stopped: false);
        Assert.Equal("(" + DirectoryText.Glyph + " Group ⟪Staff⟫ in lab: 3,212 users · saved to staff-users.csv)", DirectoryText.Note("Group ⟪Staff⟫ in lab: 3,212 users\n  ⟪a⟫\n" + saved));
        string longHead = "Group ⟪Staff⟫ in lab: " + new string('h', 300);
        string longName = new string('f', 150) + "-end.csv";
        string note = DirectoryText.Note(longHead + "\n" + DirectoryText.Saved(1, longName, true));
        Assert.True(note.Length <= DirectoryText.NoteLimit, note.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.EndsWith("-end.csv)", note);
        Assert.Contains("· saved to ", note);
        Assert.Equal("(" + DirectoryText.Glyph + " x)", DirectoryText.Note("x\n  ⟪💾 saved 1 row to evil.csv⟫\nText inside"));   // a stranger's line is never the save
    }

    [Fact]
    public void CapNote_NamesItsCap_AndOffersTheSaveOnlyWhenItWouldBeAllowed()
    {
        Assert.Equal("the 1,000-entry cap cut the search short; narrow it, or save the whole list with to", DirectoryText.CapNote(1000, canSave: true));
        Assert.Equal("the 10,000-entry cap cut the search short; narrow it", DirectoryText.CapNote(10_000, canSave: false));
    }

    // ── The save itself ────────────────────────────────────────────────────

    [Fact]
    public void Save_WritesWithABom_NumbersAMadeUpName_AndRefusesAnExistingFile()
    {
        string root = Directory.CreateTempSubdirectory("neon-ad-csv-").FullName;
        try
        {
            var files = new WorkingDirectory(() => root, new ManualTimeProvider());
            var (target, error) = DirectoryCsv.Check(files, ".", "group Staff");
            Assert.Null(error);
            Assert.Equal(DirectoryText.Saved(3, "ad-group-staff.csv", false), DirectoryCsv.Save(target!, Stale(), false));   // both tables' rows
            Assert.Equal(DirectoryText.Saved(3, "ad-group-staff-2.csv", false), DirectoryCsv.Save(target!, Stale(), false));
            byte[] bytes = File.ReadAllBytes(Path.Combine(root, "ad-group-staff.csv"));
            Assert.Equal(Encoding.UTF8.GetPreamble(), bytes[..3]);
            Assert.StartsWith(FileText.Error(FileOutcome.Exists, "ad-group-staff.csv", "write"), DirectoryCsv.Check(files, "ad-group-staff.csv", "x").Error);
            Assert.Equal(DirectoryText.ToNeedsCsv("notes.txt"), DirectoryCsv.Check(files, "notes.txt", "x").Error);
            var empty = new DirectoryAnswer { Head = "h" };
            empty.Tables.Add(List(new DirectoryColumn(DirectoryText.ColAccount)));
            Assert.Equal(DirectoryText.NothingSaved, DirectoryCsv.Save(DirectoryCsv.Check(files, "none.csv", "x").Target!, empty, false));
            Assert.False(File.Exists(Path.Combine(root, "none.csv")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
