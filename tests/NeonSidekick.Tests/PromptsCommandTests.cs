using NeonSidekick.App;
using NeonSidekick.Prompts;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary><c>/prompts</c>' words (2026-10-13, the user's ask): the parse, which forms are panes and fills, the paste exemption, headless.</summary>
public sealed class PromptsCommandTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 18, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick-promptscmd-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private SavedPrompts Prompts() => new(_dir, new ManualTimeProvider { UtcNow = Now });

    [Theory]
    [InlineData("", PromptsVerb.Pane)]
    [InlineData("  ", PromptsVerb.Pane)]
    [InlineData("3", PromptsVerb.Fill)]
    [InlineData("save Summarize this.", PromptsVerb.Save)]
    [InlineData("SAVE x", PromptsVerb.Save)]
    [InlineData("remove 2", PromptsVerb.Remove)]
    [InlineData("list", PromptsVerb.List)]
    [InlineData("list --clear", PromptsVerb.ClearList)]
    [InlineData("LIST --CLEAR", PromptsVerb.ClearList)]
    [InlineData("save", PromptsVerb.Unknown)]
    [InlineData("remove", PromptsVerb.Unknown)]
    [InlineData("list everything", PromptsVerb.Unknown)]
    [InlineData("hello", PromptsVerb.Unknown)]
    [InlineData("-1", PromptsVerb.Unknown)]
    public void Parse_TheForms(string args, PromptsVerb verb)
    {
        var line = PromptsCommand.Parse(args);
        Assert.Equal(verb, line.Verb);
        Assert.Equal(verb == PromptsVerb.Unknown, line.Error is not null);
    }

    [Fact]
    public void Parse_SaveTakesTheRestWhole_LineBreaksToo_AndANumberItsText()
    {
        Assert.Equal("Review this\nfor bugs", PromptsCommand.Parse("save Review this\nfor bugs").Text);
        Assert.Equal("first line\nsecond", PromptsCommand.Parse("save\nfirst line\nsecond").Text);   // save on a line of its own
        Assert.Equal("2", PromptsCommand.Parse("remove 2").Text);
        var fill = PromptsCommand.Parse("12");
        Assert.Equal(12, fill.Number);
        Assert.Equal("12", fill.Text);
        Assert.Equal(int.MaxValue, PromptsCommand.Parse("99999999999").Number);
    }

    [Fact]
    public void IsPane_AndFills()
    {
        Assert.True(PromptsCommand.IsPane(""));
        Assert.True(PromptsCommand.IsPane("list --clear"));
        Assert.False(PromptsCommand.IsPane("list"));
        Assert.False(PromptsCommand.IsPane("2"));
        Assert.False(PromptsCommand.IsPane("save x"));
        Assert.True(PromptsCommand.Fills("2"));
        Assert.False(PromptsCommand.Fills(""));
        Assert.False(PromptsCommand.Fills("save 2"));
    }

    [Fact]
    public void SavesPaste_OnlyForPromptsSave_OnALineThatIsNoCommandByTheRule()
    {
        // A line holding a paste token (a private-use character) or a line break is never a command (SubmittedLine.CommandText null);
        // /prompts save is read as itself all the same (2026-10-13, the user's pick).
        char token = (char)0xE000;
        Assert.True(PromptsCommand.SavesPaste(Line("/prompts save " + token, "/prompts save Line one\nLine two")));
        Assert.True(PromptsCommand.SavesPaste(Line("/PROMPTS  Save\nLine one", "/PROMPTS  Save\nLine one")));
        Assert.False(PromptsCommand.SavesPaste(Line("/prompts " + token, "/prompts Line one\nLine two")));
        Assert.False(PromptsCommand.SavesPaste(Line("/prompts list\nx", "/prompts list\nx")));
        Assert.False(PromptsCommand.SavesPaste(Line("/help " + token, "/help Line one\nLine two")));
        Assert.False(PromptsCommand.SavesPaste(Line("hello " + token, "hello Line one\nLine two")));
        Assert.False(PromptsCommand.SavesPaste(Line("/prompts save x", "/prompts save x")));   // a command by the rule already
        Assert.False(PromptsCommand.SavesPaste(null));

        static SubmittedLine Line(string draft, string text) => new(draft, text, [], text);
    }

    [Fact]
    public void Complete_TheVerbs_ThenListsSwitch()
    {
        Assert.Equal(["save", "list", "remove"], PromptsCommand.Complete("").Select(i => i.Text));
        Assert.Equal(["list --clear"], PromptsCommand.Complete("list ").Select(i => i.Text));
        Assert.Empty(PromptsCommand.Complete("save some"));
    }

    [Fact]
    public void Headless_SavesListsPrintsRemovesAndClears()
    {
        var prompts = Prompts();
        Assert.Equal([SavedPromptText.NoneSaved], PromptsCommand.Headless(prompts, ""));
        Assert.StartsWith("Saved prompt 1", PromptsCommand.Headless(prompts, "save Summarize this.").Single());
        Assert.StartsWith("Saved prompt 2", PromptsCommand.Headless(prompts, "save Review the diff").Single());
        Assert.DoesNotContain("Error", PromptsCommand.Headless(prompts, "save Summarize this.").Single());   // saved already: a notice

        Assert.Equal([SavedPromptText.Caption(2), "1. Summarize this.", "2. Review the diff"], PromptsCommand.Headless(prompts, "list"));
        Assert.Equal(PromptsCommand.Headless(prompts, "list"), PromptsCommand.Headless(prompts, ""));
        Assert.Equal([SavedPromptText.HeadlessPrompt(2, prompts.List()[1])], PromptsCommand.Headless(prompts, "2"));
        Assert.Contains("Review the diff", PromptsCommand.Headless(prompts, "2").Single());
        Assert.StartsWith("Error: ", PromptsCommand.Headless(prompts, "9").Single());
        Assert.StartsWith("Error: ", PromptsCommand.Headless(prompts, "save").Single());

        Assert.Equal([SavedPromptText.Removed(prompts.List()[0])], PromptsCommand.Headless(prompts, "remove 1"));
        Assert.StartsWith("Error: ", PromptsCommand.Headless(prompts, "remove 5").Single());
        Assert.Equal([SavedPromptText.Cleared(1)], PromptsCommand.Headless(prompts, "list --clear"));   // at once, no question
        Assert.Empty(prompts.List());
    }

    /// <summary>The Web UI's ⇪ reads whether the answer was a refusal and the prompt's number (2026-10-16), never the sentence's prefix.</summary>
    [Fact]
    public void SaveAnswer_SaysWhetherItRefused_AndThePromptsNumber()
    {
        var prompts = Prompts();

        Assert.Equal((false, 1), (Saved("Summarize.", out int one), one));
        Assert.Equal((false, 1), (Saved("Summarize.", out int again), again));   // already saved: no refusal
        Assert.Equal((false, 2), (Saved("Review.", out int two), two));
        Assert.Equal((true, 0), (Saved("   ", out int none), none));
        Assert.Equal((true, 0), (Saved(new string('x', SavedPrompts.MaxLength + 1), out int big), big));

        bool Saved(string text, out int number)
        {
            PromptsCommand.SaveAnswer(prompts, text, out bool refused, out number);
            return refused;
        }
    }
}
