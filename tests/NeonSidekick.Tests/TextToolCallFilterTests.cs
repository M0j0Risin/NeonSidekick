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
}
