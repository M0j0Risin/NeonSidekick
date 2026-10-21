using NeonSidekick.Plans;

namespace NeonSidekick.Tests;

/// <summary>
/// A plan's steps as the Ralph loop reads them (2026-10-18, <see cref="PlanSteps"/>): under <c>## Steps</c> or, with no such heading, every
/// top-level task line; fenced code passed over; numbered and bulleted items; one level of children; <c>Done when:</c> lines; the first unticked
/// leaf; and the box and the children written back with each line's own ending.
/// </summary>
public sealed class PlanStepsTests
{
    private const string Headed =
        "# Guide\n\n## Goal\n- [ ] not a step: under Goal\n\n## Steps\n### Phase 1\n- [x] Write the outline\n  Done when: `docs/guide.md` has the headings\n" +
        "- [ ] Write the body\n  - Done when: every heading has a paragraph (see ref-2)\n\n```\n- [ ] in a fence\n```\n" +
        "1. [ ] Link it from `README.md`\n   **Done when:** the README links docs/guide.md\n\n## Risks\n- [ ] not a step either\n";

    [Fact]
    public void Parse_ReadsTheStepsUnderTheHeading_AndTheirDoneWhenLines()
    {
        var steps = PlanSteps.Parse(Headed);

        Assert.Equal(["Write the outline", "Write the body", "Link it from `README.md`"], steps.Select(s => s.Text));
        Assert.Equal([true, false, false], steps.Select(s => s.Ticked));
        Assert.Equal([1, 2, 3], steps.Select(s => s.Index));
        Assert.Equal("`docs/guide.md` has the headings", steps[0].DoneWhen);
        Assert.Equal("every heading has a paragraph (see ref-2)", steps[1].DoneWhen);
        Assert.Equal("the README links docs/guide.md", steps[2].DoneWhen);
        Assert.Equal(["docs/guide.md"], steps[0].NamedPaths);
        Assert.Equal(["ref-2"], steps[1].NamedRefs);
        Assert.Equal(["README.md", "docs/guide.md"], steps[2].NamedPaths);
        Assert.Equal(7, steps[0].Line);
        Assert.All(steps, s => Assert.Empty(s.Children));
        Assert.Equal("Write the body", PlanSteps.FirstUnticked(steps)!.Text);
        Assert.Equal((1, 3), PlanSteps.Counts(steps));
    }

    [Fact]
    public void Parse_WithoutAStepsHeading_ReadsEveryTopLevelTaskLine()
    {
        var steps = PlanSteps.Parse("# Plan\n- [ ] One\n- [x] Two\n  Done when: two is there\n\nSome text.\n* [ ] Three\n");

        Assert.Equal(["One", "Two", "Three"], steps.Select(s => s.Text));
        Assert.Equal("two is there", steps[1].DoneWhen);
        Assert.Null(steps[0].DoneWhen);
    }

    [Fact]
    public void Parse_NestedTaskLines_AreChildren_AndTheFirstUntickedChildComesBeforeItsParent()
    {
        const string text = "## Steps\n- [ ] Parent\n  Done when: all of it\n  - [x] First part\n    Done when: part one\n  - [ ] Second part\n      - [ ] deeper, still a child\n- [ ] After\n";

        var steps = PlanSteps.Parse(text);

        Assert.Equal(["Parent", "After"], steps.Select(s => s.Text));
        Assert.Equal(["First part", "Second part", "deeper, still a child"], steps[0].Children.Select(c => c.Text));
        Assert.Equal("part one", steps[0].Children[0].DoneWhen);
        Assert.Equal("all of it", steps[0].DoneWhen);
        Assert.Equal([0, 4], steps.Select(s => s.Index));            // a parent is no step; leaves count 1..4
        Assert.Equal([1, 2, 3], steps[0].Children.Select(c => c.Index));
        Assert.Equal("Second part", PlanSteps.FirstUnticked(steps)!.Text);
        Assert.Equal((1, 4), PlanSteps.Counts(steps));
        Assert.Equal("Parent", PlanSteps.ParentOf(steps, steps[0].Children[1])!.Text);
        Assert.Null(PlanSteps.ParentOf(steps, steps[1]));
    }

    [Fact]
    public void Parse_AllTicked_HasNoFirstUnticked_AndADoneWhenLineIsNeverABox()
    {
        const string text = "## Steps\n- [x] One\n  - [ ] Done when: this reads as a child task, not a check\n";
        var steps = PlanSteps.Parse(text);
        Assert.Single(steps[0].Children);

        var ticked = PlanSteps.Parse("## Steps\n- [x] One\n  Done when: it is\n- [X] Two\n");
        Assert.Null(PlanSteps.FirstUnticked(ticked));
        Assert.Equal((2, 2), PlanDocument.Progress("## Steps\n- [x] One\n  Done when: it is\n- [X] Two\n"));
    }

    [Fact]
    public void WithBox_TicksOneStep_AndKeepsEveryOtherByte()
    {
        string text = "## Steps\r\n- [ ] One\r\n  Done when: x\r\n- [ ] Two\n- [ ] Three";
        var steps = PlanSteps.Parse(text);

        string? ticked = PlanSteps.WithBox(text, steps[1], ticked: true);

        Assert.Equal("## Steps\r\n- [ ] One\r\n  Done when: x\r\n- [x] Two\n- [ ] Three", ticked);
        Assert.Equal(text, PlanSteps.WithBox(ticked!, PlanSteps.Parse(ticked!)[1], ticked: false));
        Assert.Equal("## Steps\r\n- [ ] One\r\n  Done when: x\r\n- [ ] Two\n- [x] Three", PlanSteps.WithBox(text, steps[2], ticked: true));
    }

    [Fact]
    public void WithBox_FindsAStepThatMoved_AndGivesUpOnOneThatWent()
    {
        var step = PlanSteps.Parse("## Steps\n- [ ] One\n- [ ] Two\n")[1];

        Assert.Equal("Intro\n\n## Steps\n- [ ] One\n- [x] Two\n", PlanSteps.WithBox("Intro\n\n## Steps\n- [ ] One\n- [ ] Two\n", step, ticked: true));
        Assert.Null(PlanSteps.WithBox("## Steps\n- [ ] One\n- [ ] Renamed\n", step, ticked: true));
    }

    [Fact]
    public void StepsOfTheSameWords_AreToldApartByTheirOrder()
    {
        const string text = "## Steps\n- [x] Run the tests\n- [ ] Write it\n- [ ] Run the tests\n";
        var steps = PlanSteps.Parse(text);

        Assert.Equal([0, 0, 1], steps.Select(s => s.Occurrence));
        Assert.Equal([2, 1, 2], steps.Select(s => s.Twins));
        Assert.Equal([true, false, false], PlanSteps.Flat(steps).Select(s => s.Ticked));
        Assert.Equal("## Steps\n- [x] Run the tests\n- [ ] Write it\n- [x] Run the tests\n", PlanSteps.WithBox(text, steps[2], ticked: true));

        // Moved under the pass (a note above): the second is still the second, while there are two.
        string moved = "Intro\n\n" + text;
        Assert.Equal("Intro\n\n## Steps\n- [x] Run the tests\n- [ ] Write it\n- [x] Run the tests\n", PlanSteps.WithBox(moved, steps[2], ticked: true));
        Assert.Null(PlanSteps.WithBox("Intro\n\n## Steps\n- [ ] Run the tests\n" + text[9..], steps[2], ticked: true));   // a third: cannot be told
    }

    [Fact]
    public void Align_PairsEachLineWithTheOneItWas_RewordedInPlace_OrNew()
    {
        // 2026-10-19, the second review: lines matched by their words let a reworded, added or deleted line escape the Ralph loop's put-back.
        var before = PlanSteps.Flat(PlanSteps.Parse("## Steps\n- [ ] One\n- [ ] Two\n- [ ] Three\n- [ ] Four\n"));

        Assert.Equal([0, 1, 2, 3], PlanSteps.Align(before, PlanSteps.Flat(PlanSteps.Parse("Intro\n\n## Steps\n- [ ] One\n- [x] Two\n- [ ] Three\n- [ ] Four\n"))));
        Assert.Equal([0, 1, 2, 3], PlanSteps.Align(before, PlanSteps.Flat(PlanSteps.Parse("## Steps\n- [ ] One\n- [x] Two, early\n- [ ] Three\n- [ ] Four\n"))));
        Assert.Equal([0, -1, 1, 2, 3], PlanSteps.Align(before, PlanSteps.Flat(PlanSteps.Parse("## Steps\n- [ ] One\n- [x] New\n- [ ] Two\n- [ ] Three\n- [ ] Four\n"))));
        Assert.Equal([0, 2, 3], PlanSteps.Align(before, PlanSteps.Flat(PlanSteps.Parse("## Steps\n- [ ] One\n- [ ] Three\n- [ ] Four\n"))));
        // Two reworded and one added between the same kept lines: no telling which is which, all new.
        Assert.Equal([0, -1, -1, -1, 3], PlanSteps.Align(before, PlanSteps.Flat(PlanSteps.Parse("## Steps\n- [ ] One\n- [ ] 2\n- [ ] 3\n- [ ] 3b\n- [ ] Four\n"))));
    }

    [Fact]
    public void AStepParsedFromTheFileWhole_IsWrittenBackOnItsOwnLine()
    {
        // 2026-10-19, the review: a step parsed from the body had the body's line, not the file's, and a twin could not be ticked at all.
        var at = new DateTimeOffset(2026, 10, 19, 9, 0, 0, TimeSpan.Zero);
        string file = PlanDocument.Render(new PlanHeader(PlanStatus.Approved, 1, at, at, "x", Verify: "dotnet build"), "Guide", "## Steps\n- [ ] Add tests\n- [ ] Add tests\n").Replace("\n", "\r\n");
        var steps = PlanSteps.Parse(file);

        string? ticked = PlanSteps.WithBox(file, steps[1], ticked: true);

        Assert.NotNull(ticked);
        Assert.Equal([false, true], PlanSteps.Parse(ticked!).Select(s => s.Ticked));
        Assert.Equal(file.Length, ticked!.Length);
        Assert.Equal(PlanDocument.TryParse(file), PlanDocument.TryParse(ticked));
    }

    [Fact]
    public void WithChildren_WritesIndentedSubstepsWithTheirChecks_AfterTheStepsOwnLines()
    {
        string text = "## Steps\r\n- [ ] Big step\r\n  Done when: it all works\r\n  A note.\r\n- [ ] Next\r\n";
        var steps = PlanSteps.Parse(text);

        string? split = PlanSteps.WithChildren(text, steps[0], [new PlanSubstep("First half", "half works"), new PlanSubstep("Second\nhalf", "all works")]);

        Assert.Equal("## Steps\r\n- [ ] Big step\r\n  Done when: it all works\r\n  A note.\r\n  - [ ] First half\r\n    Done when: half works\r\n  - [ ] Second half\r\n    Done when: all works\r\n- [ ] Next\r\n", split);
        var again = PlanSteps.Parse(split!);
        Assert.Equal(["First half", "Second half"], again[0].Children.Select(c => c.Text));
        Assert.Equal(["half works", "all works"], again[0].Children.Select(c => c.DoneWhen));
        Assert.Equal("First half", PlanSteps.FirstUnticked(again)!.Text);
        Assert.Null(PlanSteps.WithChildren(split!, again[0], [new PlanSubstep("x", "y"), new PlanSubstep("z", "w")]));   // already split
    }

    [Fact]
    public void WithTaskLines_RewritesTheBoxAndTheWords_InPlace_KeepingEachLinesEnding()
    {
        // 2026-10-19, the third review: the judge puts back the words a pass changed, not only its boxes.
        string text = "## Steps\r\n- [x] One (done)\r\n  1. [ ] Two, reworded\n- [ ] Three";
        var lines = PlanSteps.Flat(PlanSteps.Parse(text));

        string written = PlanSteps.WithTaskLines(text, [(lines[0], false, "One"), (lines[1], true, "Two\nwhole"), (lines[2] with { Text = "Not it" }, true, "x")]);

        Assert.Equal("## Steps\r\n- [ ] One\r\n  1. [x] Two whole\n- [ ] Three", written);
    }

    [Fact]
    public void WithChildren_OnTheLastLineWithNoEnding_AddsOne()
    {
        string? split = PlanSteps.WithChildren("## Steps\n- [ ] Only", PlanSteps.Parse("## Steps\n- [ ] Only")[0], [new PlanSubstep("a", "b"), new PlanSubstep("c", "d")]);

        Assert.Equal("## Steps\n- [ ] Only\n  - [ ] a\n    Done when: b\n  - [ ] c\n    Done when: d\n", split);
    }

    [Theory]
    [InlineData("Edit `src/App/Program.cs` and tests/AppTests.cs", "src/App/Program.cs|tests/AppTests.cs")]
    [InlineData("Run `npm test`", "")]
    [InlineData("See https://example.com/a.html, e.g. or i.e.", "")]
    [InlineData("Add `.gitignore` and `build.yml`", ".gitignore|build.yml")]
    public void PathsIn_FindsBacktickedAndSlashedFiles(string text, string expected)
    {
        Assert.Equal(expected.Length == 0 ? [] : expected.Split('|'), PlanSteps.PathsIn(text));
    }

    [Fact]
    public void Align_AcrossAStretchOfUnequalSides_PairsTheAlikeLines()
    {
        // 2026-10-19, the fourth review: one line reworded and one added between two kept lines paired nothing, and the reworded step read as deleted.
        var before = PlanSteps.Flat(PlanSteps.Parse("## Steps\n- [ ] One\n- [ ] Update the unit tests\n- [ ] Four\n"));

        Assert.Equal([0, 1, -1, 2], PlanSteps.Align(before, PlanSteps.Flat(PlanSteps.Parse("## Steps\n- [ ] One\n- [x] Update the unit tests first\n- [ ] run setup.ps1\n- [ ] Four\n"))));
        Assert.Equal([0, -1, 1, 2], PlanSteps.Align(before, PlanSteps.Flat(PlanSteps.Parse("## Steps\n- [ ] One\n- [ ] run setup.ps1\n- [ ] Update unit tests\n- [ ] Four\n"))));
        Assert.True(PlanSteps.Likeness("Update the unit tests", "update the unit tests first") > PlanSteps.Alike);
        Assert.True(PlanSteps.Likeness("Update the unit tests", "run setup.ps1") < PlanSteps.Alike);
    }

    [Fact]
    public void WithoutTaskLines_TakesALineOut_WithItsOwnLinesUnderIt_AndNothingElse()
    {
        const string text = "## Steps\r\n- [ ] One\r\n  Done when: one.md\r\n- [ ] New\r\n  Done when: new.md\r\n  a note\r\n  - [ ] Child\r\n- [ ] Two\r\n";
        var steps = PlanSteps.Flat(PlanSteps.Parse(text));

        var (left, removed) = PlanSteps.WithoutTaskLines(text, [steps.Single(s => s.Text == "New")]);

        Assert.Equal("## Steps\r\n- [ ] One\r\n  Done when: one.md\r\n  - [ ] Child\r\n- [ ] Two\r\n", left);
        Assert.Equal([3, 4, 5], removed);
        // A line whose words are not those any more: left.
        var (same, none) = PlanSteps.WithoutTaskLines(text, [steps[0] with { Text = "Other" }]);
        Assert.Equal(text, same);
        Assert.Empty(none);
    }

    [Theory]
    [InlineData("# [ ] Step 1")]
    [InlineData("## [x] Step 1")]
    [InlineData("### [X] Step 1")]
    [InlineData("###### [ ] Step 1")]
    [InlineData("### [ ]")]
    [InlineData("### **[ ] Step 1**")]
    [InlineData("### _[x] Step 1_")]
    [InlineData("[ ] Step 1")]
    [InlineData("  [x] Step 1")]
    [InlineData("- **[ ] Step 1**")]
    [InlineData("1. __[x] Step 1__")]
    public void UnreadBoxes_CountsEveryFormTheParserSkips(string line)
    {
        Assert.Equal(1, PlanSteps.UnreadBoxes("# Plan\n\n## Steps\n\n" + line + "\n"));
        Assert.Empty(PlanSteps.Parse("## Steps\n" + line));
    }

    [Theory]
    [InlineData("- [ ] **Step 1**")]
    [InlineData("[x]: https://example.com/a")]
    [InlineData("[ ]Step 1")]
    [InlineData("### Step 1 [ ]")]
    [InlineData("☐ Step 1")]
    [InlineData("| [ ] | a cell |")]
    public void UnreadBoxes_CountsNoTaskLine_NorAnythingThatIsNoBox(string line)
    {
        Assert.Equal(0, PlanSteps.UnreadBoxes("## Steps\n" + line + "\n"));
    }

    [Fact]
    public void UnreadBoxes_PassesOverFences_AndTaskLinesElsewhere()
    {
        Assert.Equal(0, PlanSteps.UnreadBoxes("## Steps\n```\n### [ ] in a fence\n[ ] bare\n```\n- [ ] Real\n\n## Verification\n- [ ] a list task, not an unread step\n"));
        Assert.Equal(2, PlanSteps.UnreadBoxes(PlanFitTests.HeadingSteps));
    }

    [Fact]
    public void UnreadBoxes_ReadsACrlfText_AsItsLfTwin()
    {
        string lf = "## Steps\n### [ ]\n### [x] Step 2\n[ ] Step 3\n- [ ] Real\n";
        Assert.Equal(3, PlanSteps.UnreadBoxes(lf));
        Assert.Equal(3, PlanSteps.UnreadBoxes(lf.Replace("\n", "\r\n", StringComparison.Ordinal)));
    }
}
