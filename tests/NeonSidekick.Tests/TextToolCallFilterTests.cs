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

    /// <summary>
    /// 2026-10-11, the user's /botchat log: a Windows path lost every backslash (<c>\U</c> read as <c>U</c>, <c>\n</c> of <c>\neon</c>
    /// as a line break) and the tool refused it. Single or doubled, a path comes through; a prompt's <c>\n</c> is still a line break.
    /// </summary>
    [Theory]
    [InlineData(@"image=""C:\Users\cnels\.neonsidekick\profiles\neon\a.png""", @"C:\Users\cnels\.neonsidekick\profiles\neon\a.png")]
    [InlineData(@"image=""C:\\Users\\cnels\\.neonsidekick\\profiles\\neon\\a.png""", @"C:\Users\cnels\.neonsidekick\profiles\neon\a.png")]
    [InlineData(@"image: 'D:/pictures\tiles\new.png'", @"D:/pictures\tiles\new.png")]
    [InlineData(@"image=""\\\\server\\share\\neon.png""", @"\\server\share\neon.png")]
    [InlineData(@"image=""a \w b \d""", @"a \w b \d")]
    [InlineData(@"image=""line\nnext\tcell""", "line\nnext\tcell")]
    public void ParseArguments_KeepsAPathsBackslashes(string text, string expected)
    {
        Assert.Equal(expected, TextToolCallFilter.ParseArguments(text)!["image"]);
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

    /// <summary>
    /// A line the line form held and found no call on (2026-09-30, code review: <c>load_skill: &lt;tool_call&gt;…</c> let the markup
    /// through as it was): scanned again for the other forms, whole or split anywhere, at the stream's end too.
    /// </summary>
    [Theory]
    [InlineData("generate_image: <tool_call><function=generate_image><parameter=prompt>a dog</parameter></function></tool_call>\nscore_9, a wave")]
    [InlineData("generate_image: generate_image(prompt=\"a dog\")\nscore_9, a wave")]
    [InlineData("generate_image: generate_image(prompt=\"a dog\") score_9, a wave")]
    public void TheLineForm_AHeldLineThatIsNoCall_IsScannedForTheOtherForms(string reply)
    {
        var (text, filter) = RunLines(reply);
        Assert.Equal("generate_image: score_9, a wave", text);
        Assert.Equal("a dog", NeonSidekick.Llm.Tools.ToolArguments.ReadString(TextToolCallFilter.ParseArguments(Assert.Single(filter.Calls).Arguments)!, "prompt"));

        for (int i = 1; i < reply.Length; i++)
        {
            var (split, splitFilter) = RunLines(reply[..i], reply[i..]);
            Assert.Equal(text, split);
            Assert.Equal(filter.Calls, splitFilter.Calls);
        }
    }

    /// <summary>
    /// LastCallEnd (2026-09-30, code review: the picture writer's lead-in before a call was its prompt): where the words after the
    /// last call caught or dropped start in the text let through, whole or split anywhere; 0 with none.
    /// </summary>
    [Theory]
    [InlineData("Let me look.\ngenerate_image(prompt=\"a\") Then this.", "Then this.")]
    [InlineData("Here is my sketch!\n" + UsersLine + "\nDo you like it?", "Do you like it?")]
    [InlineData("One. generate_image(prompt=\"a\") Two. <tool_call><function=generate_image><parameter=prompt>b</parameter></function></tool_call> Three.", "Three.")]
    [InlineData("Lead-in. generate_image(prompt=\"a", "")]
    [InlineData("No call at all.", "No call at all.")]
    public void LastCallEnd_IsWhereTheWordsAfterTheLastCallStart(string reply, string after)
    {
        var (text, filter) = RunLines(reply);
        Assert.Equal(after, text[filter.LastCallEnd..]);

        for (int i = 1; i < reply.Length; i++)
        {
            var (split, splitFilter) = RunLines(reply[..i], reply[i..]);
            Assert.Equal(after, split[splitFilter.LastCallEnd..]);
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

    // ── The tagged form (2026-09-30, the user's report from /botchat) ──

    private const string UsersMarkup = "<tool_call> <function=generate_image> <parameter=prompt> score_9, score_8_up, score_7_up, source_anime, 1boy, outdoor cafe, golden  hour sunlight, realistic, depth of field, blurry background </parameter> <parameter=seed> 5566778899 </parameter>       <parameter=aspect_ratio> 1216×832 </parameter> <parameter=sampler> dpmpp_2m_sde </parameter> <parameter=cfg> 7          </parameter> <parameter=denoise> 0.8 </parameter> </function> </tool_call>";

    [Fact]
    public void TheTaggedForm_TheUsersMarkup_IsCaught_WholeOrSplitAnywhere()
    {
        var (whole, filter) = Run(UsersMarkup);
        Assert.Equal("", whole);
        Assert.Equal(Tool, Assert.Single(filter.Calls).Name);
        Assert.Equal("score_9, score_8_up, score_7_up, source_anime, 1boy, outdoor cafe, golden  hour sunlight, realistic, depth of field, blurry background", Argument(filter, "prompt").GetString());
        Assert.Equal(5566778899L, Argument(filter, "seed").GetInt64());
        Assert.Equal("1216×832", Argument(filter, "aspect_ratio").GetString());
        Assert.Equal("dpmpp_2m_sde", Argument(filter, "sampler").GetString());
        Assert.Equal(7, Argument(filter, "cfg").GetInt32());
        Assert.Equal(0.8, Argument(filter, "denoise").GetDouble());
        Assert.False(filter.SawBroken);

        for (int i = 1; i < UsersMarkup.Length; i++)
        {
            var (text, split) = Run(UsersMarkup[..i], UsersMarkup[i..]);
            Assert.Equal("", text);
            Assert.Equal(filter.Calls, split.Calls);
        }

        var (lines, lineFilter) = RunLines(UsersMarkup.Select(c => c.ToString()).ToArray());   // a character at a time, the line form on
        Assert.Equal("", lines);
        Assert.Equal(filter.Calls, lineFilter.Calls);
    }

    [Fact]
    public void TheTaggedForm_TheTextAroundItKept_ANameMidWordToo()
    {
        const string reply = "Look!<tool_call>\n<function=generate_image>\n<parameter=prompt>\na dog\n</parameter>\n</function>\n</tool_call>\n\nNice.";
        var (whole, filter) = Run(reply);
        Assert.Equal("Look!Nice.", whole);
        Assert.Equal("a dog", Argument(filter, "prompt").GetString());

        for (int i = 1; i < reply.Length; i++)
        {
            var (text, split) = Run(reply[..i], reply[i..]);
            Assert.Equal(whole, text);
            Assert.Equal(filter.Calls, split.Calls);
        }
    }

    [Theory]
    [InlineData("<tool_call>{\"name\": \"generate_image\", \"arguments\": {\"prompt\": \"a dog\", \"seed\": 7}}</tool_call>", "Here. Done.")]
    [InlineData("<tool_call>{\"name\": \"generate_image\", \"arguments\": \"{\\\"prompt\\\": \\\"a dog\\\", \\\"seed\\\": 7}\"}</tool_call>", "Here. Done.")]
    [InlineData("<function=generate_image><parameter=prompt>a dog</parameter><parameter=seed>7</parameter></function>", "Here. Done.")]
    [InlineData("<tool_call><function=generate_image><parameter=prompt>a dog</parameter><parameter=seed>7</parameter></function>", "Here. Done.")]   // no outer close
    public void TheTaggedForm_TheJsonBody_TheBareFunction_AndAMissingOuterClose(string markup, string expected)
    {
        string reply = "Here. " + markup + " Done.";
        var (text, filter) = Run(reply);
        Assert.Equal(expected, text);
        Assert.Equal("a dog", Argument(filter, "prompt").GetString());
        Assert.Equal(7, Argument(filter, "seed").GetInt32());
        Assert.False(filter.SawBroken);

        for (int i = 1; i < reply.Length; i++)
        {
            var (split, splitFilter) = Run(reply[..i], reply[i..]);
            Assert.Equal(expected, split);
            Assert.Equal(filter.Calls, splitFilter.Calls);
        }

        // The stream ending right after it is a call too.
        var (ended, endedFilter) = Run(markup);
        Assert.Equal("", ended);
        Assert.Single(endedFilter.Calls);
    }

    [Fact]
    public void TheTaggedForm_CutOffMidParameter_IsDropped_AndSaidSo()
    {
        var (text, filter) = Run("Sure: <tool_call> <function=generate_image> <parameter=prompt> a do");
        Assert.Equal("Sure: ", text);
        Assert.Empty(filter.Calls);
        Assert.True(filter.SawBroken);

        var (garbled, garbledFilter) = Run("<tool_call>not a call</tool_call>after");
        Assert.Equal("after", garbled);
        Assert.Empty(garbledFilter.Calls);
        Assert.True(garbledFilter.SawBroken);
    }

    [Theory]
    [InlineData("a < b and c > d")]
    [InlineData("<b>bold</b> text")]
    [InlineData("<tools are fun>")]
    [InlineData("<function=>empty</function>")]
    [InlineData("ends with <")]
    [InlineData("ends with <tool_ca")]
    [InlineData("ends with <function=gen")]
    public void TheTaggedForm_AnythingElse_PassesThrough(string reply)
    {
        var (text, filter) = Run(reply);
        Assert.Equal(reply, text);
        Assert.Empty(filter.Calls);

        for (int i = 1; i < reply.Length; i++)
        {
            Assert.Equal(reply, Run(reply[..i], reply[i..]).Text);
        }
    }

    [Fact]
    public void TaggedCall_ReadsBothBodies_OrIsNull()
    {
        Assert.Equal(("draw", "{\"prompt\":\"x\",\"n\":2}"), TextToolCallFilter.TaggedCall(" <function=draw> <parameter=prompt> x </parameter><parameter=n>2</parameter> "));
        Assert.Equal(("draw", "{}"), TextToolCallFilter.TaggedCall("<function=draw></function>"));
        Assert.Equal(("draw", "{}"), TextToolCallFilter.TaggedCall("{\"name\":\"draw\"}"));
        Assert.Null(TextToolCallFilter.TaggedCall("<function=draw><parameter=prompt>x"));
        Assert.Null(TextToolCallFilter.TaggedCall("{\"name\":\"draw\",\"arguments\":[1]}"));
        Assert.Null(TextToolCallFilter.TaggedCall("{\"arguments\":{}}"));
        Assert.Null(TextToolCallFilter.TaggedCall("hello"));
    }

    // ── The brace form (2026-10-11, the user's report from /botchat on Gemma 4 E2B/E4B) ──

    private const string UsersPath = @"C:\Users\cnels\.neonsidekick\profiles\neon\images\comfy_images\juggernautxl_ragnarok_img2img-253494935477047.png";

    private const string UsersPrompt = "Gnostic Sophia, female divine revelation, absolute extreme chiaroscuro, 85mm lens, f/1.8, ultimate cinematic photo style";

    private const string UsersBraces = "generate_image{image: \"" + UsersPath + "\", prompt: \"" + UsersPrompt + "\"}";

    [Fact]
    public void TheBraceForm_TheUsersReply_IsCaught_WholeOrSplitAnywhere_ItsPathWhole()
    {
        const string reply = "Behold! " + UsersBraces + " What do you think?";
        var (whole, filter) = Run(reply);
        Assert.Equal("Behold! What do you think?", whole);
        var arguments = TextToolCallFilter.ParseArguments(Assert.Single(filter.Calls).Arguments)!;
        Assert.Equal(UsersPath, arguments["image"]);
        Assert.Equal(UsersPrompt, arguments["prompt"]);
        Assert.False(filter.SawBroken);

        for (int i = 1; i < reply.Length; i++)
        {
            var (text, split) = Run(reply[..i], reply[i..]);
            Assert.Equal(whole, text);
            Assert.Equal(filter.Calls, split.Calls);

            var (lines, lineFilter) = RunLines(reply[..i], reply[i..]);
            Assert.Equal(whole, lines);
            Assert.Equal(filter.Calls, lineFilter.Calls);
        }
    }

    [Fact]
    public void TheBraceForm_QuotedKeys_ABraceInAValue_AndTheDoubledPath()
    {
        var (text, filter) = Run("Here. generate_image {\"prompt\": \"a {curly} dog }\", \"image\": \"C:\\\\pics\\\\neon.png\", \"seed\": 7} Done.");
        Assert.Equal("Here. Done.", text);
        var arguments = TextToolCallFilter.ParseArguments(Assert.Single(filter.Calls).Arguments)!;
        Assert.Equal("a {curly} dog }", arguments["prompt"]);
        Assert.Equal(@"C:\pics\neon.png", arguments["image"]);
        Assert.Equal(7L, arguments["seed"]);
    }

    /// <summary>A brace call at a line's start is not the line form's: the words after it come out with the push, not at the line's end.</summary>
    [Fact]
    public void TheBraceForm_AtALinesStart_IsNotHeldForTheLineForm()
    {
        var filter = new TextToolCallFilter([(Tool, (IReadOnlyList<string>)Parameters)]);
        Assert.Equal("Look!\nNice, right", filter.Push("Look!\ngenerate_image{prompt: \"a dog\"} Nice, right"));
        Assert.Equal("?", filter.Push("?") + filter.Flush());
        Assert.Equal([(Tool, "prompt: \"a dog\"")], filter.Calls);
    }

    // ── Aliases (later on 2026-10-11, the user's report: a workflow's name where generate_image goes) ──

    private const string Workflow = "juggernautxl_ragnarok_img2img";

    private static readonly string[] DrawParameters = ["prompt", "negative", "workflow", "image", "seed"];

    private const string UsersAliasLine = Workflow + " image=\"" + UsersPath + "\"  prompt=\"Gnostic Sophia, cosmic revelation moment, glowing hands reaching out from the celestial throne, hyper-detailed photograph\" negative=\"static, seated, distant, muted\"";

    private static (string Text, TextToolCallFilter Filter) RunAliased(params string[] deltas)
    {
        var filter = new TextToolCallFilter([(Tool, (IReadOnlyList<string>)DrawParameters)], [(Workflow, Tool, "workflow"), ("pony_txt2img", Tool, "workflow")]);
        var text = new StringBuilder();
        foreach (string delta in deltas)
        {
            text.Append(filter.Push(delta));
        }

        text.Append(filter.Flush());
        return (text.ToString(), filter);
    }

    [Fact]
    public void AnAlias_TheUsersLine_IsTheToolsCall_WithTheAliasAsItsWorkflow_WholeOrSplitAnywhere()
    {
        const string reply = "Since you're so into the intensity, let's give it a narrative quality.\n\n" + UsersAliasLine;
        var (whole, filter) = RunAliased(reply);
        Assert.Equal("Since you're so into the intensity, let's give it a narrative quality.\n\n", whole);
        Assert.Equal(Tool, Assert.Single(filter.Calls).Name);
        Assert.Equal(Workflow, Argument(filter, "workflow").GetString());
        Assert.Equal(UsersPath, Argument(filter, "image").GetString());
        Assert.Equal("Gnostic Sophia, cosmic revelation moment, glowing hands reaching out from the celestial throne, hyper-detailed photograph", Argument(filter, "prompt").GetString());
        Assert.Equal("static, seated, distant, muted", Argument(filter, "negative").GetString());

        for (int i = 1; i < reply.Length; i++)
        {
            var (text, split) = RunAliased(reply[..i], reply[i..]);
            Assert.Equal(whole, text);
            Assert.Equal(filter.Calls, split.Calls);
        }
    }

    [Theory]
    [InlineData("Look! pony_txt2img(prompt=\"a dog\", seed=7) Nice.", "pony_txt2img")]
    [InlineData("Look! pony_txt2img{prompt: \"a dog\", seed: 7} Nice.", "pony_txt2img")]
    [InlineData("Look! pony_txt2img(prompt=\"a dog\", seed=7, workflow=\"" + Workflow + "\") Nice.", Workflow)]   // its own workflow wins
    public void AnAlias_TheParenthesisedAndBraceForms_TooUnderAnAlias(string reply, string workflow)
    {
        var (text, filter) = RunAliased(reply);
        Assert.Equal("Look! Nice.", text);
        Assert.Equal(Tool, Assert.Single(filter.Calls).Name);
        Assert.Equal(workflow, Argument(filter, "workflow").GetString());
        Assert.Equal("a dog", Argument(filter, "prompt").GetString());
        Assert.Equal(7, Argument(filter, "seed").GetInt32());
    }

    [Theory]
    [InlineData(Workflow + " is my favourite workflow.")]          // the name, then no parameter
    [InlineData("I'd use " + Workflow + " image=\"a.png\" here.")]   // mid-line
    [InlineData("my flow(prompt=\"x\") stays")]                      // an alias with a space is left out
    [InlineData("other(prompt=\"x\") stays")]                        // an alias for a tool not offered is left out
    public void AnAlias_AnythingElse_PassesThrough(string reply)
    {
        var filter = new TextToolCallFilter([(Tool, (IReadOnlyList<string>)DrawParameters)], [(Workflow, Tool, "workflow"), ("my flow", Tool, "workflow"), ("other", "draw_picture", "workflow")]);
        string text = filter.Push(reply) + filter.Flush();
        Assert.Equal(reply, text);
        Assert.Empty(filter.Calls);
    }

    [Fact]
    public void WithArgument_AddsTheKeyFirst_KeepsTheCallsOwn_AndLeavesWhatDoesNotParse()
    {
        Assert.Equal("{\"workflow\":\"w\",\"prompt\":\"a\",\"seed\":7,\"cfg\":4.5,\"fast\":true}", TextToolCallFilter.WithArgument("prompt=\"a\", seed=7, cfg=4.5, fast=true", "workflow", "w"));
        Assert.Equal("{\"workflow\":\"mine\",\"n\":[1,2]}", TextToolCallFilter.WithArgument("{\"workflow\":\"mine\",\"n\":[1,2]}", "workflow", "w"));
        Assert.Equal("\"a dog\"", TextToolCallFilter.WithArgument("\"a dog\"", "workflow", "w"));
    }

    // ── The bare object form (later still on 2026-10-11, the user's report: arguments alone, no tool named) ──

    private const string UsersObjectLead = "To push that theme of temptation, I'm taking our last image and giving it a makeover.\n\n";

    private const string UsersObjectAfter = "That new iteration really pushes the 'forbidden fruit' angle, don't you think?";

    private const string UsersObject = "{\n  \"workflow\": \"juggernautxl_ragnarok_img2img\",\n  \"image\": \"C:\\\\Users\\\\cnels\\\\.neonsidekick\\\\profiles\\\\neon\\\\images\\\\comfy_images\\\\juggernautxl_ragnarok_img2img-132769972628284.png\",\n  \"prompt\": \"Gnostic Sophia, divine temptation, soft intimate lighting, 85mm, f/1.8\",\n  \"negative\": \"worst quality, low quality, blurry\"\n}\n\n";

    [Fact]
    public void TheObjectForm_TheUsersReply_IsTheOneToolsCall_WholeOrSplitAnywhere()
    {
        const string reply = UsersObjectLead + UsersObject + UsersObjectAfter;
        var (whole, filter) = RunAliased(reply);
        Assert.Equal(UsersObjectLead + UsersObjectAfter, whole);
        Assert.Equal(Tool, Assert.Single(filter.Calls).Name);
        Assert.Equal(Workflow, Argument(filter, "workflow").GetString());
        Assert.Equal(@"C:\Users\cnels\.neonsidekick\profiles\neon\images\comfy_images\juggernautxl_ragnarok_img2img-132769972628284.png", Argument(filter, "image").GetString());
        Assert.Equal("Gnostic Sophia, divine temptation, soft intimate lighting, 85mm, f/1.8", Argument(filter, "prompt").GetString());
        Assert.Equal("worst quality, low quality, blurry", Argument(filter, "negative").GetString());
        Assert.Equal(UsersObjectAfter, whole[filter.LastCallEnd..]);

        for (int i = 1; i < reply.Length; i++)
        {
            var (text, split) = RunAliased(reply[..i], reply[i..]);
            Assert.Equal(whole, text);
            Assert.Equal(filter.Calls, split.Calls);
        }
    }

    [Theory]
    [InlineData("  { \"prompt\": \"a dog\", \"image\": \"C:\\Users\\cnels\\neon.png\" }\nNice.", @"C:\Users\cnels\neon.png")]   // single backslashes: no JSON, read as pairs
    [InlineData("{\"name\": \"generate_image\", \"arguments\": {\"prompt\": \"a dog\", \"image\": \"b.png\"}}\nNice.", "b.png")]       // the Hermes object
    [InlineData("{\"name\": \"pony_txt2img\", \"arguments\": {\"prompt\": \"a dog\", \"image\": \"b.png\"}}\nNice.", "b.png")]        // naming an alias
    public void TheObjectForm_PairsAndTheHermesObject_TooAreCalls(string reply, string image)
    {
        var (text, filter) = RunAliased(reply);
        Assert.Equal("Nice.", text);
        Assert.Equal(Tool, Assert.Single(filter.Calls).Name);
        Assert.Equal("a dog", Argument(filter, "prompt").GetString());
        Assert.Equal(image, Argument(filter, "image").GetString());
    }

    [Theory]
    [InlineData("{\"prompt\": \"a dog\"}\nNice.")]                                    // one key
    [InlineData("{\"prompt\": \"a dog\", \"colour\": \"red\"}\nNice.")]               // a key the tool has not
    [InlineData("{\"name\": \"draw_picture\", \"arguments\": {\"prompt\": \"a\"}}\nNice.")]   // a tool not caught here
    [InlineData("{ not json, at all }\nNice.")]
    [InlineData("I like {\"prompt\": \"a\", \"seed\": 7} mid-line.")]                 // not at a line's start
    [InlineData("Wait:\n{\"prompt\": \"a dog\", \"seed\": 7")]                        // never closed: text, not dropped
    public void TheObjectForm_AnythingElse_PassesThrough_WholeOrSplitAnywhere(string reply)
    {
        var (text, filter) = RunAliased(reply);
        Assert.Equal(reply, text);
        Assert.Empty(filter.Calls);
        Assert.False(filter.SawBroken);

        for (int i = 1; i < reply.Length; i++)
        {
            Assert.Equal(reply, RunAliased(reply[..i], reply[i..]).Text);
        }
    }

    [Fact]
    public void TheObjectForm_KeysTwoToolsShare_AreNoCall_AndTheNamesAloneNeverCatchIt()
    {
        const string reply = "{\"prompt\": \"a dog\", \"seed\": 7}\nNice.";
        var shared = new TextToolCallFilter([(Tool, (IReadOnlyList<string>)DrawParameters), ("draw_picture", (IReadOnlyList<string>)["prompt", "seed"])]);
        Assert.Equal(reply, shared.Push(reply) + shared.Flush());
        Assert.Empty(shared.Calls);

        var (text, filter) = Run(reply);
        Assert.Equal(reply, text);
        Assert.Empty(filter.Calls);
    }

    /// <summary>An object that is no call is scanned again: a call after it on its line is still caught.</summary>
    [Fact]
    public void TheObjectForm_AnObjectThatIsNoCall_IsScannedForTheOtherForms()
    {
        var (text, filter) = RunAliased("{\"mood\": \"dark\"} generate_image(prompt=\"a\", seed=7)\nNice.");
        Assert.Equal("{\"mood\": \"dark\"} Nice.", text);
        Assert.Equal([(Tool, "prompt=\"a\", seed=7")], filter.Calls);
    }

    /// <summary>Code review, 2026-10-11: a <c>{</c> that never closed held back the rest of the reply until 8 KB or the stream's end.</summary>
    [Theory]
    [InlineData("{sighs\nThen he walked off.\nAnd that was that.\n", "{sighs\nThen he walked off.\n")]                 // a word, no key
    [InlineData("{ this is an aside and it runs on", "{ this is")]                                                   // decided mid-line
    [InlineData("{\n  \"mood\": \"dark\nThen he walked off.\nAnd that was that.\n", "{\n  \"mood\": \"dark\nThen")]  // a string open at a break
    [InlineData("{\n  \"mood\": \"dark\",\nThen he walked off.\nAnd that was that.\n", "{\n  \"mood\": \"dark\",\nThen")]  // a later line no call has
    public void TheObjectForm_AnObjectThatCannotBeACall_IsLetGo_WithoutWaitingForTheEnd(string reply, string soon)
    {
        var filter = new TextToolCallFilter([(Tool, (IReadOnlyList<string>)DrawParameters)]);
        string pushed = filter.Push(reply);
        Assert.StartsWith(soon, pushed, StringComparison.Ordinal);
        Assert.Equal(reply, pushed + filter.Flush());
        Assert.Empty(filter.Calls);

        for (int i = 1; i < reply.Length; i++)
        {
            Assert.Equal(reply, RunAliased(reply[..i], reply[i..]).Text);
        }
    }

    /// <summary>Code review, 2026-10-11: <c>"C:\pics\"</c>'s last backslash escaped the quote; in a path it is the path's, and the quote closes.</summary>
    [Theory]
    [InlineData("{\"prompt\": \"a dog\", \"image\": \"C:\\pics\\\"}\nNice.")]
    [InlineData("{\n  \"image\": \"C:\\pics\\\",\n  \"prompt\": \"a dog\"\n}\nNice.")]
    public void TheObjectForm_APathEndingInABackslash_ClosesItsString_AndIsTheCall(string reply)
    {
        var (whole, filter) = RunAliased(reply);
        Assert.Equal("Nice.", whole);
        Assert.Equal(Tool, Assert.Single(filter.Calls).Name);
        Assert.Equal("a dog", Argument(filter, "prompt").GetString());
        Assert.Equal(@"C:\pics\", Argument(filter, "image").GetString());

        for (int i = 1; i < reply.Length; i++)
        {
            var (text, split) = RunAliased(reply[..i], reply[i..]);
            Assert.Equal(whole, text);
            Assert.Equal(filter.Calls, split.Calls);
        }
    }

    /// <summary>Later on 2026-10-11 (after the code review): a path's last backslash escaped its quote in these forms too, and the call ran on.</summary>
    [Theory]
    [InlineData("Here: generate_image(prompt=\"a dog\", image=\"C:\\pics\\\")\nNice.")]
    [InlineData("Here: generate_image{prompt: \"a dog\", image: \"C:\\pics\\\"}\nNice.")]
    [InlineData("Here: generate_image(image='C:\\pics\\', prompt='a dog')\nNice.")]
    public void TheParenAndBraceForms_APathEndingInABackslash_ClosesItsString_AndIsTheCall(string reply)
    {
        var (whole, filter) = Run(reply);
        Assert.Equal("Here: Nice.", whole);
        var arguments = TextToolCallFilter.ParseArguments(Assert.Single(filter.Calls).Arguments)!;
        Assert.Equal("a dog", arguments["prompt"]);
        Assert.Equal(@"C:\pics\", arguments["image"]);

        for (int i = 1; i < reply.Length; i++)
        {
            var (text, split) = Run(reply[..i], reply[i..]);
            Assert.Equal(whole, text);
            Assert.Equal(filter.Calls, split.Calls);
        }
    }

    [Theory]
    [InlineData("my_generate_image{prompt: \"x\"} stays")]
    [InlineData("a set like {generate_image} stays")]
    [InlineData("generate_image } stays")]
    public void TheBraceForm_AnythingElse_PassesThrough(string reply)
    {
        var (text, filter) = Run(reply);
        Assert.Equal(reply, text);
        Assert.Empty(filter.Calls);
    }
}
