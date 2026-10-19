using NeonSidekick.Plans;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>The Ralph loop's words (2026-10-18, <see cref="RalphText"/>): the strip, the lines, the directive and its budget, the message, the pane's check line, the stops and the summary.</summary>
public sealed class RalphTextTests
{
    /// <summary>
    /// Nothing the app ships names this repository's own build script as a check (2026-10-18, the plan's rule: the examples are generic,
    /// <c>dotnet build</c>, <c>npm test</c>, <c>cargo test</c>): the loop's and plan mode's strings, the help, and the docs but BUILD.md, which is
    /// about building this app.
    /// </summary>
    [Fact]
    public void NoShippedText_NamesThisReposBuildScript()
    {
        static IEnumerable<string> Strings(Type type) => type
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.FieldType == typeof(string))
            .Select(f => (string?)f.GetValue(null) ?? "");

        string shipped = string.Join("\n", Strings(typeof(RalphText)).Concat(Strings(typeof(PlanText)))
            .Concat(NeonSidekick.Help.HelpCommands.Commands.SelectMany(c => c.Forms).Select(f => f.Syntax + " " + f.Meaning))
            .Concat(Enum.GetValues<NeonSidekick.App.SettingsField>().Select(NeonSidekick.Help.HelpSettings.Describe))
            .Append(RalphText.Directive(".neon/plans/g.md", 1, 2, "One", "it builds", "dotnet build", reviewed: true, canAsk: false, null, null)));
        Assert.DoesNotContain("build.ps1", shipped, StringComparison.OrdinalIgnoreCase);

        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "NeonSidekick.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        foreach (string doc in Directory.GetFiles(Path.Combine(root.FullName, "docs"), "*.md").Append(Path.Combine(root.FullName, "README.md")).Where(d => Path.GetFileName(d) != "BUILD.md"))
        {
            Assert.False(File.ReadAllText(doc).Contains("build.ps1", StringComparison.OrdinalIgnoreCase), Path.GetFileName(doc) + " names build.ps1");
        }
    }

    [Fact]
    public void TheStrip_TheTranscriptLine_AndTheSessionTitle_ArePinned()
    {
        Assert.Equal("🔁 step 4/9 · pass 6", RalphText.StripText(4, 9, 6));
        Assert.Equal("🔁 final check · pass 7", RalphText.StripText(9, 9, 7, final: true));
        Assert.Equal("🔁 Pass 6 · step 4 of 9: Write the body", RalphText.TranscriptLine(6, 4, 9, "Write the body"));
        Assert.Equal("Ralph: Guide · step 4/9 · pass 6", RalphText.SessionTitle("Guide", 4, 9, 6));
        Assert.True(TextCells.Width(RalphText.Hint) <= InfoPane.HintCells);
        Assert.True(TextCells.Width(RalphText.CheckpointKeys) <= InfoPane.HintCells);
        Assert.Equal("Approve & Ralph loop", RalphText.ApproveRalphRow);
    }

    [Fact]
    public void TheDirective_SaysTheRules_TheCheck_AndWhereAQuestionGoes()
    {
        string directive = RalphText.Directive(".neon/plans/g.md", 2, 5, "Write the body", "every heading has text", "dotnet build", reviewed: true, canAsk: false, notes: "- pass 1: partial", learned: "- docs live in docs/");

        Assert.StartsWith("RALPH LOOP PASS: you are carrying out one step of the approved plan in .neon/plans/g.md, in a fresh conversation. This pass is step 2 of 5: \"Write the body\" (done when: every heading has text).\n", directive);
        Assert.Contains("- First check whether the work is already done: search the code before building anything.", directive);
        Assert.Contains("- Do not tick or untick boxes in the plan file", directive);
        Assert.Contains("- Do not commit unless the step says to.", directive);
        Assert.Contains("call finish_step with outcome split and 2 to 6 substeps", directive);
        Assert.Contains("- Do not ask the user anything and take no photo or screen capture: if you cannot go on without an answer, call finish_step with outcome blocked", directive);
        Assert.Contains("- After finish_step the app runs `dotnet build` and has your work reviewed; the step stands only if that passes.", directive);
        Assert.Contains("Notes for this pass:\n- pass 1: partial\nLearned in earlier passes:\n- docs live in docs/", directive);
        Assert.DoesNotContain("Do not ask the user", RalphText.Directive("p", 1, 1, "x", null, null, reviewed: false, canAsk: true, null, null));
        Assert.Contains("- After finish_step the app looks at what changed.", RalphText.Directive("p", 1, 1, "x", null, null, reviewed: false, canAsk: true, null, null));
        Assert.DoesNotContain("split", RalphText.Directive("p", 1, 1, "x", null, null, reviewed: false, canAsk: true, null, null, finalFix: true).Replace("cannot be split", "", StringComparison.Ordinal));
    }

    [Fact]
    public void TheDirective_KeepsToItsBudget_NotesShrinkingFirst()
    {
        string notes = new('n', 9000);
        string learned = "- " + new string('l', 1500);

        string directive = RalphText.Directive("p", 1, 1, "x", null, "make", reviewed: false, canAsk: true, notes, learned);

        Assert.True(directive.Length <= RalphText.DirectiveBudget);
        Assert.Contains(learned, directive);   // the learned facts kept whole
        Assert.Contains("…", directive);       // the notes cut
    }

    [Fact]
    public void ThePassMessage_CarriesThePlanInline_OrItsHeadings()
    {
        string small = RalphText.PassMessage(".neon/plans/g.md", "# G\n## Steps\n- [ ] a", 1, 1, "a", "it is", windowChars: 0);
        Assert.Equal("Carry out step 1 of 1 of the plan in .neon/plans/g.md: \"a\" (done when: it is). End with finish_step.\n\nThe plan:\n\n# G\n## Steps\n- [ ] a", small);

        string body = "# G\n## Steps\n" + string.Join('\n', Enumerable.Range(0, 400).Select(i => "- [ ] step " + i + new string('x', 60)));
        string large = RalphText.PassMessage(".neon/plans/g.md", body, 3, 400, "step 2", null, windowChars: 0);
        Assert.Contains("The plan is too long to send whole; its headings:\n  # G\n  ## Steps\n\nread_file .neon/plans/g.md for the rest.", large);
        Assert.DoesNotContain("step 300", large);

        // A small window: a quarter of it is the room.
        Assert.Contains("read_file", RalphText.PassMessage("p", new string('x', 1000), 1, 1, "a", null, windowChars: 2000));
    }

    [Fact]
    public void ThePanesCheckLine_IsPinned()
    {
        Assert.Equal("verify `dotnet build` · final `dotnet test` · reviewer any", RalphText.PaneCheckLine(new PlanChecks("dotnet build", "dotnet test"), RalphReviewer.Any, 0, false));
        Assert.Equal("unchecked · reviewer off · budget 2.0M · shell policy ask: an unattended loop waits on each command", RalphText.PaneCheckLine(null, RalphReviewer.Off, 2_000_000, true));
        // The fit (later on 2026-10-18): the problems after the rest, one part each; none or empty, the line as before.
        Assert.Equal("unchecked · reviewer any · 1 step: one pass does the whole plan · the plan is 170,298 characters; a pass sends it whole only up to 24,000",
            RalphText.PaneCheckLine(null, RalphReviewer.Any, 0, false, [RalphText.FitOneStep, RalphText.FitTooLong(170_298, 24_000)]));
        Assert.Equal("verify `make` · reviewer any", RalphText.PaneCheckLine(new PlanChecks("make", null), RalphReviewer.Any, 0, false, []));
    }

    [Fact]
    public void TheFitWords_ArePinned()
    {
        Assert.Equal("1 step: one pass does the whole plan", RalphText.FitOneStep);
        Assert.Equal("1 step has no Done when line", RalphText.FitNoDoneWhen(1));
        Assert.Equal("3 steps have no Done when line", RalphText.FitNoDoneWhen(3));
        Assert.Equal("1,200 steps have no Done when line", RalphText.FitNoDoneWhen(1200));
        Assert.Equal("the plan is 170,298 characters; a pass sends it whole only up to 24,000", RalphText.FitTooLong(170_298, 24_000));
        Assert.Equal("no check: steps stand unchecked", RalphText.FitNoCheck);

        string[] problems = [RalphText.FitOneStep, RalphText.FitNoCheck];
        Assert.Equal("This plan may not suit a Ralph loop: 1 step: one pass does the whole plan; no check: steps stand unchecked. Start anyway?", RalphText.FitQuestion(problems));
        Assert.Equal("It may not suit a Ralph loop: 1 step: one pass does the whole plan; no check: steps stand unchecked.", RalphText.FitSentence(problems));
        Assert.Equal("🔁 This plan may not suit a Ralph loop: 1 step: one pass does the whole plan; no check: steps stand unchecked. Starting it anyway.", RalphText.FitNotice(problems));
        Assert.Equal("🔁 Not started: revise the plan (/plan gaps tightens it), or run /plan approve --ralph and say yes to start it as it is.", RalphText.FitDeclinedNotice);

        // /plan ralph <name>'s one confirm: the problems after the check line; none, as before.
        Assert.Equal("Run p.md as a Ralph loop? unchecked · reviewer any", RalphText.ConfirmQuestion("p.md", "unchecked · reviewer any"));
        Assert.Equal("Run p.md as a Ralph loop? unchecked · reviewer any", RalphText.ConfirmQuestion("p.md", "unchecked · reviewer any", []));
        Assert.Equal("Run p.md as a Ralph loop? unchecked · reviewer any. It may not suit a Ralph loop: 1 step: one pass does the whole plan.", RalphText.ConfirmQuestion("p.md", "unchecked · reviewer any", [RalphText.FitOneStep]));
        Assert.Equal("Approve p.md and run it as a Ralph loop? reviewer any. It may not suit a Ralph loop: 1 step has no Done when line.", RalphText.ConfirmDraftQuestion("p.md", "reviewer any", [RalphText.FitNoDoneWhen(1)]));
    }

    [Fact]
    public void TheInlineLimit_IsThePassMessagesRoom()
    {
        Assert.Equal(RalphText.InlinePlanChars, RalphText.InlineLimit(0));
        Assert.Equal(RalphText.InlinePlanChars, RalphText.InlineLimit(1_000_000));
        Assert.Equal(500, RalphText.InlineLimit(2000));

        // The pass message sends a body whole exactly up to the limit.
        string body = "# G\n" + new string('x', 496);
        Assert.Contains("The plan:\n\n", RalphText.PassMessage("p", body, 1, 1, "a", null, windowChars: 2000));
        Assert.Contains("read_file", RalphText.PassMessage("p", body + "x", 1, 1, "a", null, windowChars: 2000));
    }

    [Fact]
    public void TheStops_AndTheSummary_ArePinned()
    {
        Assert.Equal("🔁 Ralph loop done: every step of .neon/plans/g.md stands.", RalphText.StopNotice(RalphStop.Done, ".neon/plans/g.md", 3, 3));
        Assert.Equal("🔁 Ralph loop stopped: two passes in a row made no progress (1 of 3 steps done). /plan ralph g goes on.", RalphText.StopNotice(RalphStop.Stalled, ".neon/plans/g.md", 1, 3));
        Assert.Equal("🔁 Ralph loop paused for your message (1 of 3 steps done). /plan ralph goes on.", RalphText.StopNotice(RalphStop.Paused, ".neon/plans/g.md", 1, 3));
        var lines = RalphText.SummaryLines(
            [new RalphPassRecord(1, 1, "a", "done", true, TimeSpan.FromSeconds(40), 1200), new RalphPassRecord(2, 2, "b", "check failed", false, TimeSpan.FromSeconds(20), 800, 300)],
            RalphStop.Stalled);
        Assert.Equal("🔁 Ralph loop, 2 passes, 1m 00s, 2.3k tokens:", lines[0]);
        Assert.Equal("  pass 1: step 1, done · 40.0s · 1.2k tokens", lines[1]);
        Assert.Equal("  pass 2: step 2, check failed, stalled · 20.0s · 1.1k tokens", lines[2]);
        Assert.Empty(RalphText.SummaryLines([], RalphStop.Done));
    }
}
