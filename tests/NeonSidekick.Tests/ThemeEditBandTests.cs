using NeonSidekick.App;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using Spectre.Console;
using Spectre.Console.Rendering;
using Spectre.Console.Testing;

namespace NeonSidekick.Tests;

/// <summary>
/// The tidier <c>/theme edit</c> pane (2026-11-24, the user's pick "Notes in the band"): the band under the list holds the cursor's role note in
/// full, then that role's contrast; the problems name their roles; the preview asks for the whole mock screen.
/// </summary>
public sealed class ThemeEditBandTests
{
    private static ContrastProblem P(string pair, double ratio, double target = 4.5, string? slab = null, params ThemeColorSlot[] roles) =>
        new(pair, ratio, target, roles.Length > 0 ? roles : null, slab);

    private static string Note(ThemeColorSlot role) => ThemeKeys.Colors[(int)role] + ": " + ThemeText.RoleNotes[(int)role];

    [Fact]
    public void NoProblems_GiveTheNote_ThenContrastOk()
    {
        var lines = SettingsMenu.BandLines(ThemeColorSlot.Bg, [], 200, 8);

        Assert.Equal([Note(ThemeColorSlot.Bg), "✓  " + ThemeText.ChecksOk], lines);
    }

    [Fact]
    public void AFailingRole_ListsItsPairs_WorstFirst_FlooredAsTheWarningIs()
    {
        var problems = new[]
        {
            P("dimmer on bg", 2.4, 2.5, null, ThemeColorSlot.Dimmer, ThemeColorSlot.Bg),
            P("ink on bg", 4.46, 4.5, null, ThemeColorSlot.Ink, ThemeColorSlot.Bg),
            P("panelBg on bg", 1.0, 1.2, null, ThemeColorSlot.PanelBg, ThemeColorSlot.Bg),
        };

        var lines = SettingsMenu.BandLines(ThemeColorSlot.Bg, problems, 200, 8);

        Assert.Equal([Note(ThemeColorSlot.Bg), "⚠  panelBg on bg 1.0:1 (wants 1.2)", "⚠  dimmer on bg 2.4:1 (wants 2.5)", "⚠  ink on bg 4.4:1 (wants 4.5)"], lines);
    }

    [Fact]
    public void CodeSlabPairs_FoldToOneLinePerSlab_AtTheWorst()
    {
        var problems = new[]
        {
            P("codeKeyword on the good diff", 2.5, 3, "good", ThemeColorSlot.Bg, ThemeColorSlot.Good),
            P("codeString on the good diff", 1.5, 3, "good", ThemeColorSlot.Bg, ThemeColorSlot.Good),
            P("codeKeyword on the bad diff", 2.9, 3, "bad", ThemeColorSlot.Bg, ThemeColorSlot.Bad),
        };

        var lines = SettingsMenu.BandLines(ThemeColorSlot.Bg, problems, 200, 8);

        Assert.Equal(["⚠  2 code colors on the good diff, worst 1.5:1 (wants 3)", "⚠  1 code color on the bad diff, worst 2.9:1 (wants 3)"], lines.Skip(1));
    }

    [Fact]
    public void TooManyLines_EndOnAndNMore_WithNRight()
    {
        var problems = Enumerable.Range(0, 10).Select(i => P("pair" + i, 1 + i * 0.1, 4.5, null, ThemeColorSlot.Ink)).ToArray();

        var lines = SettingsMenu.BandLines(ThemeColorSlot.Ink, problems, 200, 5);

        Assert.Equal(5, lines.Count);
        Assert.Equal(ThemeText.ChecksMore(7), lines[^1]);   // the note, three pairs, then the seven left out
        Assert.Equal("⚠  and 7 more", lines[^1]);
    }

    [Fact]
    public void APassingRole_WhileAnotherFails_SaysSo_AndRolelessPairsShowOnEveryRow()
    {
        var problems = new[]
        {
            P("dimmer on bg", 2.0, 2.5, null, ThemeColorSlot.Dimmer, ThemeColorSlot.Bg),
            P("markdownHeading1 on its own ground", 1.0),
        };

        var lines = SettingsMenu.BandLines(ThemeColorSlot.Primary, problems, 200, 8);
        var failing = SettingsMenu.BandLines(ThemeColorSlot.Dimmer, problems, 200, 8);

        Assert.Equal("✓  primary meets its bars · ⚠  2 other pairs don't", lines[1]);
        Assert.Equal("⚠  markdownHeading1 on its own ground 1.0:1 (wants 4.5)", lines[2]);
        Assert.Equal("⚠  markdownHeading1 on its own ground 1.0:1 (wants 4.5)", failing[^1]);   // after the role's own
    }

    [Fact]
    public void EveryGlyph_HasTwoSpaces_AndLinesKeepToTheRowsAndWidth()
    {
        var problems = Enumerable.Range(0, 6).Select(i => P("a long pair name number " + i + " on the panel background", 1 + i * 0.1, 4.5, null, ThemeColorSlot.Warm)).ToArray();

        foreach (int width in new[] { 20, 30, 45 })
        {
            var lines = SettingsMenu.BandLines(ThemeColorSlot.Warm, problems, width, 8);
            Assert.InRange(lines.Count, 1, 8);
            Assert.All(lines, line => Assert.True(TextCells.Width(line) <= width, line));
            Assert.All(lines.Where(l => l.Contains('⚠') || l.Contains('✓')), line => Assert.Matches("[⚠✓]  [^ ]", line));
        }
    }

    [Fact]
    public void Problems_NameTheirRoles()
    {
        var dull = ThemeLibrary.Default with { Dimmer = ThemeLibrary.Default.Bg, Primary = ThemeLibrary.Default.PanelBg };

        var problems = ThemeChecks.Problems(dull);

        Assert.Equal([ThemeColorSlot.Dimmer, ThemeColorSlot.Bg], problems.Single(p => p.Pair == ThemeText.DimmerPair).Roles);
        Assert.Equal([ThemeColorSlot.Primary, ThemeColorSlot.PanelBg], problems.Single(p => p.Pair == ThemeText.PanelRolePair("primary")).Roles);
    }

    /// <summary>
    /// The last-7 review's #19: a code color on a diff slab names the role it comes from — the default's own (keyword: primary, never ink though
    /// ink may share the color), an alias's source (tag: keyword's), a theme's change by the roles of its color, a free hex by none.
    /// </summary>
    [Fact]
    public void CodeOnSlabProblems_NameTheCodeColorsOwnRole()
    {
        var p = ThemeLibrary.Default;
        Assert.Equal([ThemeColorSlot.Primary], ThemeChecks.CodeColorRoles(p with { Ink = p.Primary }, ThemeStyleSlot.CodeKeyword));
        Assert.Equal([ThemeColorSlot.Primary], ThemeChecks.CodeColorRoles(p, ThemeStyleSlot.CodeTag));
        Assert.Equal([ThemeColorSlot.Secondary], ThemeChecks.CodeColorRoles(p, ThemeStyleSlot.CodeAttribute));

        var changed = p with { Styles = new Dictionary<ThemeStyleSlot, StyleOverride> { [ThemeStyleSlot.CodeType] = new(Foreground: p.Warm), [ThemeStyleSlot.CodeKeyword] = new(Set: Spectre.Console.Decoration.Bold) } };
        Assert.Contains(ThemeColorSlot.Warm, ThemeChecks.CodeColorRoles(changed, ThemeStyleSlot.CodeType));
        Assert.Contains(ThemeColorSlot.Warm, ThemeChecks.CodeColorRoles(changed, ThemeStyleSlot.CodeAttribute));   // the alias follows its source's change
        Assert.Equal([ThemeColorSlot.Primary], ThemeChecks.CodeColorRoles(changed, ThemeStyleSlot.CodeKeyword));   // a decoration alone doesn't count
        var hex = p with { Styles = new Dictionary<ThemeStyleSlot, StyleOverride> { [ThemeStyleSlot.CodeString] = new(Foreground: new Spectre.Console.Color(1, 2, 3)) } };
        Assert.Empty(ThemeChecks.CodeColorRoles(hex, ThemeStyleSlot.CodeString));

        // The slab problem itself carries it: a keyword color dull on the good slab names primary.
        var dull = p with { Primary = Theme.Mix(p.Bg, p.Good, Theme.DiffSlabShare) };
        var problem = ThemeChecks.Problems(dull).Single(x => x.Pair == ThemeText.SlabPair(ThemeKeys.Styles[(int)ThemeStyleSlot.CodeKeyword], "good"));
        Assert.Contains(ThemeColorSlot.Primary, problem.Roles!);
    }

    /// <summary>The default map is the derived styles' color, for every code slot that is no alias (Theme's Derive).</summary>
    [Fact]
    public void CodeRoles_MatchTheDerivedStyles()
    {
        var p = ThemeLibrary.Default;
        var styles = Theme.StylesBuilt(p);
        foreach (var (_, slot) in Theme.CodeSlots.Where(c => ThemeKeys.AliasOf(c.Slot) is null))
        {
            Assert.Equal(styles(slot).Foreground, p.ColorOf(ThemeChecks.CodeRoles[slot]));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ScreenRows_IsTheWholeMockScreen_NoLineDropped(int collapse)
    {
        int rows = ThemePreview.ScreenRows(collapse);
        static string Text(IRenderable line)
        {
            using var console = new TestConsole();
            return string.Concat(line.Render(RenderOptions.Create(console, console.Profile.Capabilities), 60).Select(s => s.Text));
        }

        var exact = ThemePreview.Lines(ShippedThemes.Synthwave, 60, rows, "NEON", "1.2.3", diffCollapseCount: collapse).Select(Text).ToList();
        var roomy = ThemePreview.Lines(ShippedThemes.Synthwave, 60, rows + 5, "NEON", "1.2.3", diffCollapseCount: collapse).Select(Text).Take(rows).ToList();

        Assert.Equal(roomy, exact);
        Assert.Equal(ThemePreview.ScreenRows(0), rows);   // a fold swaps the diff's summary row for its own, no row added
    }
}
