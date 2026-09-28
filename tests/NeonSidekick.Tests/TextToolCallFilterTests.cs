using System.Text;
using System.Text.Json;
using NeonSidekick.Llm;

namespace NeonSidekick.Tests;

/// <summary>
/// <see cref="TextToolCallFilter"/> (2026-09-25): a tool call a model writes out as text is kept out of the reply and
/// caught for the loop to run; anything else passes through untouched, however the stream is split.
/// </summary>
public class TextToolCallFilterTests
{
    private const string Tool = "generate_image";

    private static (string Text, TextToolCallFilter Filter) Run(params string[] deltas)
    {
        var filter = new TextToolCallFilter([Tool]);
        var text = new StringBuilder();
        foreach (string delta in deltas)
        {
            text.Append(filter.Push(delta));
        }

        text.Append(filter.Flush());
        return (text.ToString(), filter);
    }

    [Fact]
    public void AWrittenCall_IsDropped_AndCaught_TheTextAroundItKept()
    {
        var (text, filter) = Run("Look at this! generate_image(prompt=\"a dog surfing\", seed=7)\n\nIsn't it nice?");

        Assert.Equal("Look at this! Isn't it nice?", text);
        Assert.Equal([(Tool, "prompt=\"a dog surfing\", seed=7")], filter.Calls);
        Assert.False(filter.SawBroken);
    }

    [Fact]
    public void SplitAtEveryPosition_GivesTheSameResult()
    {
        const string reply = "Here you go `generate_image (prompt=\"a (tiny) dog\\\" said\", negative='no )')` done";
        var (whole, wholeFilter) = Run(reply);
        Assert.Equal("Here you go done", whole);
        Assert.Equal([(Tool, "prompt=\"a (tiny) dog\\\" said\", negative='no )'")], wholeFilter.Calls);

        for (int i = 1; i < reply.Length; i++)
        {
            var (text, filter) = Run(reply[..i], reply[i..]);
            Assert.Equal(whole, text);
            Assert.Equal(wholeFilter.Calls, filter.Calls);
        }
    }

    [Theory]
    [InlineData("my_generate_image(prompt=\"x\") stays")]      // not at a word start
    [InlineData("draw_picture(prompt=\"x\") stays")]           // not an offered tool
    [InlineData("generate_image is the tool I use")]           // the name, no call
    [InlineData("ends with a tick `")]
    [InlineData("ends inside the name generate_im")]
    public void AnythingElse_PassesThrough(string reply)
    {
        var (text, filter) = Run(reply);

        Assert.Equal(reply, text);
        Assert.Empty(filter.Calls);
    }

    [Fact]
    public void ACallStillOpenAtTheEnd_IsDropped_AndSaidSo()
    {
        var (text, filter) = Run("Sure: generate_image(prompt=\"a dog");

        Assert.Equal("Sure: ", text);
        Assert.Empty(filter.Calls);
        Assert.True(filter.SawBroken);
    }

    /// <summary>2026-09-28, code review: a quote mid-word opened a string that never closed, and the rest of the reply was lost.</summary>
    [Theory]
    [InlineData("Look! generate_image(prompt=a dog's birthday party) Isn't it nice?", "prompt=a dog's birthday party")]
    [InlineData("Look! generate_image(prompt=a 12\" vinyl, seed=7) Isn't it nice?", "prompt=a 12\" vinyl, seed=7")]
    public void AQuoteMidWord_OpensNoString_WholeOrSplitAnywhere(string reply, string arguments)
    {
        for (int i = 0; i < reply.Length; i++)
        {
            var (text, filter) = i == 0 ? Run(reply) : Run(reply[..i], reply[i..]);
            Assert.Equal("Look! Isn't it nice?", text);
            Assert.Equal([(Tool, arguments)], filter.Calls);
            Assert.False(filter.SawBroken);
        }
    }

    [Fact]
    public void ParseArguments_ReadsKeyValuePairs()
    {
        var arguments = TextToolCallFilter.ParseArguments("prompt=\"a \\\"big\\\" dog\", negative: 'cats', seed=7, cfg=4.5, verbatim=true, workflow=pony, steps=None")!;

        Assert.Equal("a \"big\" dog", arguments["prompt"]);
        Assert.Equal("cats", arguments["negative"]);
        Assert.Equal(7L, arguments["seed"]);
        Assert.Equal(4.5, arguments["cfg"]);
        Assert.Equal(true, arguments["verbatim"]);
        Assert.Equal("pony", arguments["workflow"]);
        Assert.False(arguments.ContainsKey("steps"));
    }

    [Fact]
    public void ParseArguments_ReadsAJsonObject_AndNothingAsNoArguments()
    {
        var json = TextToolCallFilter.ParseArguments("{\"prompt\": \"a dog\", \"seed\": 7}")!;
        Assert.Equal("a dog", ((JsonElement)json["prompt"]!).GetString());
        Assert.Equal(7, ((JsonElement)json["seed"]!).GetInt32());

        Assert.Empty(TextToolCallFilter.ParseArguments("   ")!);
    }

    [Theory]
    [InlineData("\"a dog\"")]              // positional
    [InlineData("prompt=\"a dog")]         // unclosed string
    [InlineData("prompt \"a dog\"")]       // no = or :
    [InlineData("prompt=")]
    [InlineData("{not json")]
    [InlineData("[1, 2]")]
    public void ParseArguments_IsNull_ForWhatDoesNotParse(string text)
    {
        Assert.Null(TextToolCallFilter.ParseArguments(text));
    }

    /// <summary>2026-09-28, code review: a bare prompt's commas ended it, and the call was not run.</summary>
    [Fact]
    public void ParseArguments_ABareValue_KeepsItsCommas_UntilTheNextKey()
    {
        var alone = TextToolCallFilter.ParseArguments("prompt=score_9, masterpiece, a castle")!;
        Assert.Equal("score_9, masterpiece, a castle", alone["prompt"]);

        var then = TextToolCallFilter.ParseArguments("prompt=score_9, masterpiece, a castle, seed = 7, 'negative'=blur")!;
        Assert.Equal("score_9, masterpiece, a castle", then["prompt"]);
        Assert.Equal(7L, then["seed"]);
        Assert.Equal("blur", then["negative"]);

        var colons = TextToolCallFilter.ParseArguments("prompt: a castle, at dusk, seed: 7")!;
        Assert.Equal("a castle, at dusk", colons["prompt"]);
        Assert.Equal(7L, colons["seed"]);
    }

    // ── The line form (later on 2026-09-25, the user's report from /botchat) ──

    private static readonly string[] Parameters = ["prompt", "negative", "negative_extra", "width", "height", "seed", "image", "image2"];

    private const string UsersLine = "generate_image prompt: score_9, score_8_up, source_anime, 1girl, solo, cute, shy, blush, looking at viewer, standing, dark lighting, messy background    width: 1024 height: 1024";

    private static (string Text, TextToolCallFilter Filter) RunLines(params string[] deltas)
    {
        var filter = new TextToolCallFilter([(Tool, (IReadOnlyList<string>)Parameters)]);
        var text = new StringBuilder();
        foreach (string delta in deltas)
        {
            text.Append(filter.Push(delta));
        }

        text.Append(filter.Flush());
        return (text.ToString(), filter);
    }

    private static JsonElement Argument(TextToolCallFilter filter, string key) =>
        (JsonElement)TextToolCallFilter.ParseArguments(Assert.Single(filter.Calls).Arguments)![key]!;

    [Fact]
    public void TheLineForm_TheUsersLine_IsCaught_WholeOrSplitAnywhere()
    {
        var (whole, filter) = RunLines(UsersLine);
        Assert.Equal("", whole);
        Assert.Equal(Tool, Assert.Single(filter.Calls).Name);
        Assert.Equal("score_9, score_8_up, source_anime, 1girl, solo, cute, shy, blush, looking at viewer, standing, dark lighting, messy background", Argument(filter, "prompt").GetString());
        Assert.Equal(1024, Argument(filter, "width").GetInt32());
        Assert.Equal(1024, Argument(filter, "height").GetInt32());
        Assert.False(filter.SawBroken);

        for (int i = 1; i < UsersLine.Length; i++)
        {
            var (text, split) = RunLines(UsersLine[..i], UsersLine[i..]);
            Assert.Equal("", text);
            Assert.Equal(filter.Calls, split.Calls);
        }
    }

    [Fact]
    public void TheLineForm_OnItsOwnLine_TheLinesAroundItKept()
    {
        const string reply = "Here is my sketch!\n" + UsersLine + "\nDo you like it?";
        var (whole, filter) = RunLines(reply);
        Assert.Equal("Here is my sketch!\nDo you like it?", whole);
        Assert.Single(filter.Calls);

        for (int i = 1; i < reply.Length; i++)
        {
            var (text, split) = RunLines(reply[..i], reply[i..]);
            Assert.Equal(whole, text);
            Assert.Equal(filter.Calls, split.Calls);
        }
    }

    [Fact]
    public void TheLineForm_TakesAColonAfterTheName_ABacktick_AndQuotes()
    {
        var (text, filter) = RunLines("  `generate_image: prompt=\"a dog, surfing\" negative_extra: cats seed: 7`\n");
        Assert.Equal("", text);
        Assert.Equal("a dog, surfing", Argument(filter, "prompt").GetString());
        Assert.Equal("cats", Argument(filter, "negative_extra").GetString());   // never read as negative
        Assert.Equal(7, Argument(filter, "seed").GetInt32());
    }

    [Theory]
    [InlineData("generate_image is how I draw.")]                       // the name, then no parameter
    [InlineData("generate_image: nice")]                                // a colon, then no parameter
    [InlineData("I used generate_image prompt: x")]                     // mid-line
    [InlineData("generate_images prompt: x")]                           // another word
    [InlineData("first line\ngenerate_image is how I draw.\nlast")]
    public void TheLineForm_AnythingElse_PassesThrough(string reply)
    {
        var (text, filter) = RunLines(reply);
        Assert.Equal(reply, text);
        Assert.Empty(filter.Calls);

        for (int i = 1; i < reply.Length; i++)
        {
            Assert.Equal(reply, RunLines(reply[..i], reply[i..]).Text);
        }
    }

    [Fact]
    public void TheLineForm_NeedsTheParameters_TheNamesAloneNeverCatchIt()
    {
        var (text, filter) = Run(UsersLine);
        Assert.Equal(UsersLine, text);
        Assert.Empty(filter.Calls);
    }

    [Fact]
    public void TheParenthesisedForm_StillCaught_WithTheParametersGiven()
    {
        var (text, filter) = RunLines("generate_image(prompt=\"a dog\")\nNice?");
        Assert.Equal("Nice?", text);
        Assert.Equal([(Tool, "prompt=\"a dog\"")], filter.Calls);
    }

    [Fact]
    public void LineArguments_StartsWithAKey_OrIsNull()
    {
        Assert.Null(TextToolCallFilter.LineArguments("a dog prompt: x", Parameters));
        Assert.Null(TextToolCallFilter.LineArguments("", Parameters));
        Assert.Equal("{\"prompt\":\"a dog\",\"image2\":\"b.png\"}", TextToolCallFilter.LineArguments("PROMPT = a dog image2: b.png", Parameters));
    }
}
