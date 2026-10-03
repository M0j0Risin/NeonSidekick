using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Settings;
using NeonSidekick.Shell;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// The shell police's forbidden strings (2026-10-03, the user's idea): the loose match, the list's keeping, the words the model and the
/// user get, and the line for the user alone carried past the model (<see cref="ToolShownResult"/>, <see cref="TurnEvent.ToolResult.Shown"/>).
/// </summary>
public sealed class ForbiddenStringsTests
{
    [Fact]
    public void Normalize_CollapsesEveryRunOfWhitespace_AndTrims()
    {
        Assert.Equal("rm -rf build", ForbiddenStrings.Normalize("  rm \t -rf\r\n\n build  "));
        Assert.Equal("", ForbiddenStrings.Normalize(" \t\n"));
        Assert.Equal("x", ForbiddenStrings.Normalize("x"));
    }

    [Fact]
    public void Find_IgnoresCase_AndSpacing_ReturnsTheEntryAsTyped_FirstInListOrder()
    {
        Assert.Equal("rm -rf", ForbiddenStrings.Find("RM   -RF /", ["rm -rf"]));
        Assert.Equal("Remove-Item -Recurse", ForbiddenStrings.Find("remove-item\n  -recurse x", ["Remove-Item -Recurse"]));
        Assert.Equal("format", ForbiddenStrings.Find("format c: & rm -rf x", ["format", "rm -rf"]));   // the list's order, not the text's
        Assert.Equal("rm  -rf", ForbiddenStrings.Find("rm -rf x", ["rm  -rf"]));                      // a hand-edited entry's spacing still matches, named as it is
        Assert.Null(ForbiddenStrings.Find("rm -r -f x", ["rm -rf"]));
        Assert.Null(ForbiddenStrings.Find("anything", []));
        Assert.Null(ForbiddenStrings.Find("anything", ["", "  "]));                                   // a blank entry never matches
        Assert.Equal("del", ForbiddenStrings.Find("model", ["del"]));                                 // a substring, not a word: the user's pick
    }

    [Fact]
    public void TheList_KeepsCase_NoDuplicatesIgnoringCaseAndSpacing_SortedAToZ()
    {
        Assert.Equal(["Format", "rm -rf"], ForbiddenStrings.Sorted(["rm  -rf", "Format", "RM -RF", " ", "format"]));
        Assert.Equal(["format", "rm -rf"], ForbiddenStrings.Add(["rm -rf"], "  format "));
        Assert.Equal(["rm -rf"], ForbiddenStrings.Add(["rm -rf"], "RM\t-RF"));
        Assert.Equal(["rm -rf"], ForbiddenStrings.Add(["rm -rf"], "   "));
        Assert.Equal(["format"], ForbiddenStrings.Without(["rm -rf", "format"], "RM -RF"));
        Assert.True(ForbiddenStrings.Contains(["rm  -rf"], "Rm -Rf"));
        Assert.False(ForbiddenStrings.Contains(["rm -rf"], "rm -r"));
    }

    [Fact]
    public void Wording_TheModelIsNeverToldTheString_TheUserIs()
    {
        Assert.Equal("Error: forbidden by the shell police — the user does not allow this command or script. Do not try another way to do the same thing; tell the user it was refused.", ShellText.Forbidden);
        Assert.StartsWith(ShellText.ForbiddenHead, ShellText.Forbidden);
        Assert.Equal("forbidden string 'rm -rf' — not run", ShellText.ForbiddenShown("rm -rf"));
        Assert.Equal("police: forbidden ('rm -rf') — cmd \"rm -rf build\"", ShellText.ForbiddenLogLine(new CommandRequest("cmd", "rm -rf build", []), "rm -rf"));
        Assert.True(ShellText.IsPoliced(ShellText.Forbidden));
        Assert.True(ShellText.IsPoliced(ShellText.OutsidePath("~")));
        Assert.False(ShellText.IsOutside(ShellText.Forbidden));
        Assert.False(ShellText.IsPoliced("exit 0 in 0.0 s (cmd): dir\n"));
    }

    [Fact]
    public void TheRow_NameAndValue()
    {
        Assert.Equal("Shell police forbidden strings", SettingsMenu.FieldName(SettingsField.ShellPoliceForbiddenStrings));
        Assert.Equal("none", SettingsMenu.Strings(0));
        Assert.Equal("1 string", SettingsMenu.Strings(1));
        Assert.Equal("3 strings", SettingsMenu.Strings(3));
        Assert.Equal("2 strings", SettingsMenu.FieldValue(SettingsField.ShellPoliceForbiddenStrings, new AppSettingsData { ShellPoliceForbiddenStrings = ["a", "b", "A"] }, ""));
        Assert.False(SettingsMenu.IsToggle(SettingsField.ShellPoliceForbiddenStrings));
        Assert.Equal(["Shell police forbidden strings", "  Format", "  rm -rf"], ToolsMenu.ForbiddenStringLines(new AppSettingsData { ShellPoliceForbiddenStrings = ["rm -rf", "Format"] }));
    }

    [Fact]
    public void Copy_DeepCopiesTheList()
    {
        var source = new AppSettingsData { ShellPoliceForbiddenStrings = ["rm -rf"] };
        var copy = AppSettings.Copy(source);
        source.ShellPoliceForbiddenStrings.Add("format");
        Assert.Equal(["rm -rf"], copy.ShellPoliceForbiddenStrings);
    }

    /// <summary>Answers a <see cref="ToolShownResult"/>: the shape of a refused <c>run_command</c>, without a shell.</summary>
    private sealed class ShownTool : AIFunction
    {
        private static readonly JsonElement Schema = NeonSidekick.Llm.Tools.ToolSchema.Parse("""{"type":"object","properties":{}}""");

        public override string Name => "shown";

        public override string Description => "Refuses.";

        public override JsonElement JsonSchema => Schema;

        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
            => new(new ToolShownResult(ShellText.Forbidden, ShellText.ForbiddenShown("rm -rf")));
    }

    [Fact]
    public async Task Assistant_TheModelGetsTheText_TheEventCarriesTheShownLine()
    {
        var client = new FakeChatClient();
        var history = new ConversationHistory("sys");
        var assistant = new Assistant(client, history, new LlmTimeouts(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)), [new ShownTool()]);
        client.Enqueue(FakeChatClient.Call("call-1", "shown", new Dictionary<string, object?>()));
        client.EnqueueText("refused");

        var events = new List<TurnEvent>();
        await foreach (var evt in assistant.RunTurnAsync("clean it")) events.Add(evt);

        var result = Assert.Single(events.OfType<TurnEvent.ToolResult>());
        Assert.Equal(ShellText.Forbidden, result.Text);
        Assert.Equal("forbidden string 'rm -rf' — not run", result.Shown);
        var sent = Assert.Single(client.Requests[1][3].Contents.OfType<FunctionResultContent>());
        Assert.Equal(ShellText.Forbidden, sent.Result);

        // The two-part form a side loop (and execute_code's bridge) calls answers the text alone.
        var (text, _) = await Assistant.InvokeToolAsync([new ShownTool()], new FunctionCallContent("c", "shown", null), CancellationToken.None);
        Assert.Equal(ShellText.Forbidden, text);
    }
}
