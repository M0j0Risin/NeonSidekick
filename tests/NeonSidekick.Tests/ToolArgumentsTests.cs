using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Llm.Tools;

namespace NeonSidekick.Tests;

public class ToolArgumentsTests
{
    private static AIFunctionArguments Args(object? value) =>
        new(new Dictionary<string, object?> { ["x"] = value });

    private static JsonElement Json(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    [Fact]
    public void ReadString_TakesEveryShape()
    {
        Assert.Equal("", ToolArguments.ReadString(new AIFunctionArguments(), "x"));
        Assert.Equal("", ToolArguments.ReadString(Args(null), "x"));
        Assert.Equal("", ToolArguments.ReadString(Args(Json("null")), "x"));
        Assert.Equal("hi", ToolArguments.ReadString(Args(Json("\"hi\"")), "x"));
        Assert.Equal("42", ToolArguments.ReadString(Args(Json("42")), "x"));
        Assert.Equal("[1,2]", ToolArguments.ReadString(Args(Json("[1,2]")), "x"));
        Assert.Equal("hi", ToolArguments.ReadString(Args("hi"), "x"));
        Assert.Equal("7", ToolArguments.ReadString(Args(7), "x"));
    }

    [Fact]
    public void TryReadInt32_MissingNullAndBlank_AreNull()
    {
        foreach (var arguments in new[] { new AIFunctionArguments(), Args(null), Args(Json("null")), Args(""), Args("  "), Args(Json("\"\"")) })
        {
            Assert.True(ToolArguments.TryReadInt32(arguments, "x", out var value, out _));
            Assert.Null(value);
        }
    }

    [Fact]
    public void TryReadInt32_ReadsNumbersStringsAndClrIntegers()
    {
        Assert.True(ToolArguments.TryReadInt32(Args(Json("-6")), "x", out var value, out _));
        Assert.Equal(-6, value);
        Assert.True(ToolArguments.TryReadInt32(Args(Json("\" +12 \"")), "x", out value, out _));
        Assert.Equal(12, value);
        Assert.True(ToolArguments.TryReadInt32(Args("3"), "x", out value, out _));
        Assert.Equal(3, value);
        Assert.True(ToolArguments.TryReadInt32(Args(4), "x", out value, out _));
        Assert.Equal(4, value);
        Assert.True(ToolArguments.TryReadInt32(Args(5L), "x", out value, out _));
        Assert.Equal(5, value);
    }

    [Fact]
    public void TryReadInt32_RejectsEverythingElse_AndReportsWhatWasSent()
    {
        Assert.False(ToolArguments.TryReadInt32(Args(Json("1.5")), "x", out _, out var raw));
        Assert.Equal("1.5", raw);
        Assert.False(ToolArguments.TryReadInt32(Args(Json("\"many\"")), "x", out _, out raw));
        Assert.Equal("many", raw);
        Assert.False(ToolArguments.TryReadInt32(Args(Json("true")), "x", out _, out raw));
        Assert.Equal("true", raw);
        Assert.False(ToolArguments.TryReadInt32(Args(Json("[1]")), "x", out _, out raw));
        Assert.Equal("[1]", raw);
        Assert.False(ToolArguments.TryReadInt32(Args("1,000"), "x", out _, out raw));
        Assert.Equal("1,000", raw);
        Assert.False(ToolArguments.TryReadInt32(Args(Json("99999999999")), "x", out _, out raw));
        Assert.Equal("99999999999", raw);
        Assert.False(ToolArguments.TryReadInt32(Args(99999999999L), "x", out _, out raw));
        Assert.Equal("99999999999", raw);
    }
    [Fact]
    public void TryReadBoolean_MissingNullAndBlank_AreNull()
    {
        foreach (var arguments in new[] { new AIFunctionArguments(), Args(null), Args(Json("null")), Args(""), Args("  "), Args(Json("\"\"")) })
        {
            Assert.True(ToolArguments.TryReadBoolean(arguments, "x", out var value, out _));
            Assert.Null(value);
        }
    }

    [Fact]
    public void TryReadBoolean_TakesJsonBooleans_Words_AndClrBools()
    {
        Assert.True(ToolArguments.TryReadBoolean(Args(Json("true")), "x", out var value, out _));
        Assert.True(value);
        Assert.True(ToolArguments.TryReadBoolean(Args(Json("false")), "x", out value, out _));
        Assert.False(value);
        Assert.True(ToolArguments.TryReadBoolean(Args(Json("\"TRUE\"")), "x", out value, out _));
        Assert.True(value);
        Assert.True(ToolArguments.TryReadBoolean(Args(" false "), "x", out value, out _));
        Assert.False(value);
        Assert.True(ToolArguments.TryReadBoolean(Args(true), "x", out value, out _));
        Assert.True(value);
    }

    [Theory]
    [InlineData("\"yes\"", "yes")]
    [InlineData("1", "1")]
    [InlineData("[true]", "[true]")]
    public void TryReadBoolean_RefusesAnythingElse_WithTheRawText(string json, string raw)
    {
        Assert.False(ToolArguments.TryReadBoolean(Args(Json(json)), "x", out var value, out var sent));
        Assert.Null(value);
        Assert.Equal(raw, sent);
        Assert.False(ToolArguments.TryReadBoolean(Args("maybe"), "x", out _, out sent));
        Assert.Equal("maybe", sent);
    }

    [Fact]
    public void TryReadStringList_TakesAnArray_ASingleString_OrAClrSequence_AndNothingForMissing()
    {
        Assert.True(ToolArguments.TryReadStringList(new AIFunctionArguments(), "x", out var values, out _));
        Assert.Empty(values);
        Assert.True(ToolArguments.TryReadStringList(Args(Json("null")), "x", out values, out _));
        Assert.Empty(values);
        Assert.True(ToolArguments.TryReadStringList(Args(Json("[]")), "x", out values, out _));
        Assert.Empty(values);
        Assert.True(ToolArguments.TryReadStringList(Args(Json("[\"a.png\", \"b.jpg\"]")), "x", out values, out _));
        Assert.Equal(["a.png", "b.jpg"], values);
        Assert.True(ToolArguments.TryReadStringList(Args(Json("\"one.png\"")), "x", out values, out _));
        Assert.Equal(["one.png"], values);
        Assert.True(ToolArguments.TryReadStringList(Args(Json("\" \"")), "x", out values, out _));
        Assert.Empty(values);
        Assert.True(ToolArguments.TryReadStringList(Args("typed.png"), "x", out values, out _));
        Assert.Equal(["typed.png"], values);
        Assert.True(ToolArguments.TryReadStringList(Args(new[] { "c", "d" }), "x", out values, out _));
        Assert.Equal(["c", "d"], values);
    }

    /// <summary>
    /// A list written as text (2026-11-15): a whole string that parses as an array of strings, JSON or Python-quoted, is that list, in a JSON
    /// string or a CLR one; a path with brackets, YAML's <c>[a, b]</c> and an array holding a number stay one string.
    /// </summary>
    [Theory]
    [InlineData("[\"a.txt\", \"b.txt\"]", "a.txt|b.txt")]
    [InlineData(" ['a.txt', 'b.txt'] ", "a.txt|b.txt")]
    [InlineData("[]", "")]
    [InlineData("[a, b]", "[a, b]")]
    [InlineData("[milk]", "[milk]")]
    [InlineData("notes [draft].md", "notes [draft].md")]
    [InlineData("[\"x\"] y", "[\"x\"] y")]
    [InlineData("[1, \"a\"]", "[1, \"a\"]")]
    public void TryReadStringList_AListWrittenAsText_IsThatList_AndNothingElseIs(string text, string expected)
    {
        string[] want = expected.Length == 0 ? [] : expected.Split('|');
        Assert.True(ToolArguments.TryReadStringList(Args(Json("\"" + JsonEncodedText.Encode(text) + "\"")), "x", out var values, out _));
        Assert.Equal(want, values);
        Assert.True(ToolArguments.TryReadStringList(Args(text), "x", out values, out _));
        Assert.Equal(want, values);
    }

    [Theory]
    [InlineData("[1, \"a\"]", "[1, \"a\"]")]
    [InlineData("{\"p\":\"a\"}", "{\"p\":\"a\"}")]
    [InlineData("7", "7")]
    public void TryReadStringList_RefusesAnythingElse_WithTheRawText(string json, string raw)
    {
        Assert.False(ToolArguments.TryReadStringList(Args(Json(json)), "x", out var values, out var sent));
        Assert.Empty(values);
        Assert.Equal(raw, sent);
        Assert.False(ToolArguments.TryReadStringList(Args(42), "x", out _, out sent));
        Assert.Equal("42", sent);
    }

    [Fact]
    public void TryReadObjectList_TakesAnArrayOfObjects_AnEncodedOne_OrAClrSequence_AndNothingForMissing()
    {
        Assert.True(ToolArguments.TryReadObjectList(new AIFunctionArguments(), "x", out var objects, out _));
        Assert.Empty(objects);
        Assert.True(ToolArguments.TryReadObjectList(Args(Json("null")), "x", out objects, out _));
        Assert.Empty(objects);
        Assert.True(ToolArguments.TryReadObjectList(Args(Json("[]")), "x", out objects, out _));
        Assert.Empty(objects);
        Assert.True(ToolArguments.TryReadObjectList(Args(Json("[{\"a\":1}, {\"b\":\"x\"}]")), "x", out objects, out var raw));
        Assert.Equal(2, objects.Count);
        Assert.Equal("x", objects[1].GetProperty("b").GetString());
        Assert.Equal("[{\"a\":1}, {\"b\":\"x\"}]", raw);
        // The list encoded as a string, and a blank string as nothing.
        Assert.True(ToolArguments.TryReadObjectList(Args(Json("\"[{\\\"a\\\":1}]\"")), "x", out objects, out _));
        Assert.Equal(1, objects.Single().GetProperty("a").GetInt32());
        Assert.True(ToolArguments.TryReadObjectList(Args("[{\"c\":true}]"), "x", out objects, out _));
        Assert.True(objects.Single().GetProperty("c").GetBoolean());
        Assert.True(ToolArguments.TryReadObjectList(Args(Json("\" \"")), "x", out objects, out _));
        Assert.Empty(objects);
        Assert.True(ToolArguments.TryReadObjectList(Args(new[] { Json("{\"d\":2}") }), "x", out objects, out _));
        Assert.Equal(2, objects.Single().GetProperty("d").GetInt32());
    }

    [Fact]
    public void TryReadObjectList_IsLenient_ALoneObject_ATrailingComma_APythonLiteral()
    {
        // A lone object is a list of one, as a value and inside a string.
        Assert.True(ToolArguments.TryReadObjectList(Args(Json("{\"p\":\"a\"}")), "x", out var objects, out var raw));
        Assert.Equal("a", objects.Single().GetProperty("p").GetString());
        Assert.Equal("{\"p\":\"a\"}", raw);
        Assert.True(ToolArguments.TryReadObjectList(Args("{\"p\": 1}"), "x", out objects, out _));
        Assert.Equal(1, objects.Single().GetProperty("p").GetInt32());
        // What a server's parser hands over as text when its own parse failed: a trailing comma, single quotes, True.
        Assert.True(ToolArguments.TryReadObjectList(Args("[{\"p\": 1,},]"), "x", out objects, out _));
        Assert.Equal(1, objects.Single().GetProperty("p").GetInt32());
        Assert.True(ToolArguments.TryReadObjectList(Args("[{'p': 'it\\'s', 'q': True, 'r': None}]"), "x", out objects, out _));
        Assert.Equal("it's", objects.Single().GetProperty("p").GetString());
        Assert.True(objects.Single().GetProperty("q").GetBoolean());
        Assert.Equal(JsonValueKind.Null, objects.Single().GetProperty("r").ValueKind);
    }

    [Fact]
    public void LooseJson_Normalize_IsPinned()
    {
        Assert.Equal("[{\"a\": \"it's\", \"b\": true, \"c\": null, \"d\": \"say \\\"hi\\\"\"}]", LooseJson.Normalize("[{'a': 'it\\'s', 'b': True, 'c': None, 'd': 'say \"hi\"'}]"));
        Assert.Equal("{\"a\": \"x\\ny\"}", LooseJson.Normalize("{'a': 'x\r\ny'}"));
        // JSON is JSON: a double-quoted string, its escapes and the words inside it untouched.
        Assert.Equal("{\"True\": \"None \\\" ok\"}", LooseJson.Normalize("{\"True\": \"None \\\" ok\"}"));
        Assert.Equal("[\"unterminated\"", LooseJson.Normalize("['unterminated"));
        Assert.Equal("", LooseJson.Normalize(""));
        Assert.Null(LooseJson.TryParse("not json"));
        using var loose = LooseJson.TryParse("{'k': 1,}");
        Assert.Equal(1, loose!.RootElement.GetProperty("k").GetInt32());
    }

    [Fact]
    public void LooseJson_FirstValue_CutsAtTheEndOfTheFirstCompleteValue()
    {
        Assert.Equal("[{\"a\": \"]}\"}]", LooseJson.FirstValue("[{\"a\": \"]}\"}]}"));     // brackets inside a string do not count
        Assert.Equal("[{'a': ']}'}]", LooseJson.FirstValue("[{'a': ']}'}]} trailing"));
        Assert.Equal("{\"a\": \"\\\"]\"}", LooseJson.FirstValue("{\"a\": \"\\\"]\"} x"));   // an escaped quote inside a string
        Assert.Equal("[1]", LooseJson.FirstValue("questions: [1]"));                          // a word ahead of the value
        Assert.Null(LooseJson.FirstValue("[1]"));                                             // whole already: nothing to cut
        Assert.Null(LooseJson.FirstValue("[1]  \n"));
        Assert.Null(LooseJson.FirstValue("[1, 2"));                                           // never closes
        Assert.Null(LooseJson.FirstValue("no brackets"));
        Assert.Null(LooseJson.FirstValue(""));
    }

    [Fact]
    public void TryReadObjectList_TakesAStringifiedList_WithSglangsStrayBraceAfterIt()
    {
        // Verbatim from neon.log, 2026-09-17 01:45 (SGLang, Qwen3): the parser's fallback hands the list as a
        // string with the outer object's closing brace sliced in — a well-formed list refused for one character.
        const string sent = "[{\"question\": \"Hi Christopher, it's around 8:45 PM on Wednesday. What can I help you with? Pick any that apply.\", \"type\": \"multi\", \"options\": [\"Check the weather (San Antonio)\", \"Gather recipes for a dish\", \"Look at files in the working directory\", \"Open or search something on the web\", \"Set a timer or count days\", \"Something else\"]}]}";

        Assert.True(ToolArguments.TryReadObjectList(Args(sent), "x", out var objects, out string raw));
        Assert.Equal(sent, raw);
        Assert.Equal("multi", objects.Single().GetProperty("type").GetString());
        Assert.Equal(6, objects.Single().GetProperty("options").GetArrayLength());

        // The same in Python quoting. A remark or a second value after the list is refused (2026-11-15, the tool-arguments review's #39): only
        // closing brackets may follow the first value, so nothing is dropped unseen.
        Assert.True(ToolArguments.TryReadObjectList(Args("[{'question': 'Q?', 'options': ['a', 'b']}]}"), "x", out objects, out _));
        Assert.Equal("Q?", objects.Single().GetProperty("question").GetString());
        Assert.False(ToolArguments.TryReadObjectList(Args("[{\"question\": \"Q?\", \"options\": [\"a\", \"b\"]}] — asked as a list"), "x", out objects, out _));
        Assert.False(ToolArguments.TryReadObjectList(Args("{\"question\": \"A?\"}, {\"question\": \"B?\"}"), "x", out objects, out _));
        Assert.Empty(objects);

        // A list that never closes is still refused: nothing is invented.
        Assert.False(ToolArguments.TryReadObjectList(Args("[{\"question\": \"Q?\", \"options\": [\"a\", \"b\""), "x", out objects, out _));
        Assert.Empty(objects);
    }

    /// <summary>
    /// A list of strings written as text (2026-11-15, the tool-arguments review's #39, #92): a second list after the first is one odd string,
    /// never the first alone; a lone backslash in a Windows path is read as a backslash; a string's escaped quotes stay quotes.
    /// </summary>
    [Fact]
    public void TryReadStringList_AnEncodedList_IsReadWhole_AndItsBackslashesAsPaths()
    {
        const string two = "[\"a\"] x [\"b\"]";
        Assert.True(ToolArguments.TryReadStringList(Args(two), "x", out var values, out _));
        Assert.Equal([two], values);

        Assert.True(ToolArguments.TryReadStringList(Args("[\"docs\\notes.txt\", \"C:\\Users\\bob\\a.txt\"]"), "x", out values, out _));
        Assert.Equal(["docs\\notes.txt", "C:\\Users\\bob\\a.txt"], values);

        Assert.True(ToolArguments.TryReadStringList(Args("[\"say \\\"hi\\\".txt\"]"), "x", out values, out _));
        Assert.Equal(["say \"hi\".txt"], values);

        // A real escape still reads as one, and a path ending in a backslash before its quote stays one unparsed string.
        Assert.True(ToolArguments.TryReadStringList(Args("[\"caf\\u00e9.txt\"]"), "x", out values, out _));
        Assert.Equal(["caf\u00e9.txt"], values);
        const string trailing = "[\"C:\\temp\\\"]";
        Assert.True(ToolArguments.TryReadStringList(Args(trailing), "x", out values, out _));
        Assert.Equal([trailing], values);

        // SGLang's closing brace after the list is still taken.
        Assert.True(ToolArguments.TryReadStringList(Args("[\"a.txt\", \"b.txt\"]}"), "x", out values, out _));
        Assert.Equal(["a.txt", "b.txt"], values);
    }

    /// <summary>A raw control character in a hand-written string is escaped, not left to fail the parse (#125); a CRLF still reads as LF.</summary>
    [Fact]
    public void LooseJson_EscapesEveryControlCharacter_InAString()
    {
        using var tabbed = LooseJson.TryParse("['a\tb', 'c\u0001d', 'e\r\nf']");
        Assert.NotNull(tabbed);
        Assert.Equal(["a\tb", "c\u0001d", "e\nf"], tabbed!.RootElement.EnumerateArray().Select(e => e.GetString()));
        Assert.Null(LooseJson.TryParse("[\"a\"] x [\"b\"]"));
        Assert.Null(LooseJson.TryParse("[1] [2]"));
        Assert.Equal("[\"a\\\\b\", \"c\\\"d\"]", LooseJson.DoubleLoneBackslashes("[\"a\\b\", \"c\\\"d\"]"));
    }

    [Theory]
    [InlineData("[1, {\"a\":1}]", "[1, {\"a\":1}]")]
    [InlineData("[\"a\"]", "[\"a\"]")]
    [InlineData("7", "7")]
    [InlineData("\"not json\"", "not json")]
    public void TryReadObjectList_RefusesAnythingElse_WithTheRawText(string json, string raw)
    {
        Assert.False(ToolArguments.TryReadObjectList(Args(Json(json)), "x", out var objects, out var sent));
        Assert.Empty(objects);
        Assert.Equal(raw, sent);
        Assert.False(ToolArguments.TryReadObjectList(Args(42), "x", out _, out sent));
        Assert.Equal("42", sent);
        Assert.False(ToolArguments.TryReadObjectList(Args(new[] { Json("1") }), "x", out objects, out _));
        Assert.Empty(objects);
    }
}
