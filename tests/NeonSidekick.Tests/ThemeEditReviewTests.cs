using System.Text;
using System.Text.Json.Nodes;
using NeonSidekick.App;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.Tests;

/// <summary>
/// The admin-themes-render review's <c>/theme</c> fixes (2026-11-17, §4), the pure parts: the save's splice in the file's own bytes (#25, #26,
/// #156, #163), a built-in's copy from its own text (#98), the sub-command list (#217), the contrast band's cap and floor (#146, #106) and the
/// checks on the resolved styles (#100).
/// </summary>
public sealed class ThemeEditReviewTests
{
    private const string Commented =
        "﻿{\r\n  // my theme\r\n  \"name\": \"mine\",\r\n  \"description\": \"a <b> & ü\",\r\n  \"colors\": {\r\n    \"Primary\": \"#FF00FF\", // pink\r\n    \"bg\": \"#000000\",\r\n  },\r\n}\r\n";

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public void SetColors_ChangesOnlyThatValuesBytes_InTheFilesSpelling_KeepingItsMarkCommentsAndEndings()
    {
        byte[] spliced = ThemeFileSplice.SetColors(Utf8(Commented), [("primary", "#123456")])!;

        Assert.Equal(Commented.Replace("\"#FF00FF\"", "\"#123456\"", StringComparison.Ordinal), Encoding.UTF8.GetString(spliced));
        Assert.Equal(Utf8(Commented).Length, spliced.Length);
    }

    [Fact]
    public void SetColors_ARoleTheObjectLacks_GoesInAfterItsLastEntry_OnTheFilesIndentAndLineEnding()
    {
        string text = Encoding.UTF8.GetString(ThemeFileSplice.SetColors(Utf8(Commented), [("warn", "#ABCDEF"), ("bg", "#010203")])!);

        Assert.Contains("\"bg\": \"#010203\",\r\n    \"warn\": \"#ABCDEF\",\r\n  },", text, StringComparison.Ordinal);
        Assert.Contains("// my theme", text, StringComparison.Ordinal);
        var data = ThemeFile.Parse(text.TrimStart('﻿'), out string? problem);
        Assert.Null(problem);
        Assert.Equal("#ABCDEF", data!.Colors!["warn"]);

        // An empty object gets the entries on lines of their own; a file without "colors", or not an object, is the caller's to fall back on.
        string empty = Encoding.UTF8.GetString(ThemeFileSplice.SetColors(Utf8("{\n  \"colors\": {}\n}\n"), [("ink", "#FFFFFF")])!);
        Assert.Equal("{\n  \"colors\": {\n    \"ink\": \"#FFFFFF\"\n  }\n}\n", empty);
        Assert.Null(ThemeFileSplice.SetColors(Utf8("{ \"name\": \"x\" }"), [("ink", "#FFFFFF")]));
        Assert.Null(ThemeFileSplice.SetColors(Utf8("[1]"), [("ink", "#FFFFFF")]));
        Assert.Null(ThemeFileSplice.SetColors(Utf8("{ \"colors\": "), [("ink", "#FFFFFF")]));
    }

    [Fact]
    public void SetColors_ADuplicatedRole_ChangesTheLastOne_AsTheLoaderKeepsIt()
    {
        string file = "{ \"colors\": { \"ink\": \"#111111\", \"Ink\": \"#222222\" } }";
        string text = Encoding.UTF8.GetString(ThemeFileSplice.SetColors(Utf8(file), [("ink", "#333333")])!);

        Assert.Equal("{ \"colors\": { \"ink\": \"#111111\", \"Ink\": \"#333333\" } }", text);
    }

    [Fact]
    public void WithColorsJson_TheFallback_WritesOntoTheFilesKey_InItsLineEndings()
    {
        string file = "{\r\n  \"description\": \"a <b>\",\r\n  \"colors\": { \"Primary\": \"#FF00FF\" }\r\n}";
        string text = SettingsMenu.WithColorsJson(file, [("primary", "#123456"), ("ink", "#EEEEEE")], "\r\n");

        var colors = JsonNode.Parse(text)!["colors"]!.AsObject();
        Assert.Equal(["Primary", "ink"], colors.Select(p => p.Key));
        Assert.Equal("#123456", (string?)colors["Primary"]);
        Assert.Contains("\"a <b>\"", text, StringComparison.Ordinal);   // the relaxed writer: no <
        Assert.DoesNotContain("\n", text.Replace("\r\n", "", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.EndsWith("\r\n", text, StringComparison.Ordinal);
        Assert.Throws<System.Text.Json.JsonException>(() => SettingsMenu.WithColorsJson("[1]", [("ink", "#EEEEEE")]));
    }

    [Fact]
    public void SetName_ReplacesTheTopLevelName_OnlyThere()
    {
        string file = "{ \"colors\": { \"name\": \"#000000\" }, \"name\": \"bauhaus\" }";

        Assert.Equal("{ \"colors\": { \"name\": \"#000000\" }, \"name\": \"bauhaus-custom\" }", Encoding.UTF8.GetString(ThemeFileSplice.SetName(Utf8(file), "bauhaus-custom")!));
        Assert.Null(ThemeFileSplice.SetName(Utf8("{ \"colors\": {} }"), "x"));
    }

    /// <summary>A built-in's copy is its own file with the new name (#98): bauhaus's heading keeps <c>"bg": "secondary"</c>, words and all.</summary>
    [Fact]
    public void ExportBytes_ABuiltIn_IsItsOwnTextRenamed_AUserThemeIsWrittenWhole()
    {
        var bauhaus = ThemeLibrary.Get("bauhaus");
        string source = ThemeLibrary.SourceText("bauhaus")!;
        string copy = Encoding.UTF8.GetString(SettingsMenu.ExportBytes(bauhaus, "bauhaus-custom"));

        Assert.Contains("\"bg\": \"secondary\"", copy, StringComparison.Ordinal);
        Assert.Equal(source.Replace("\"name\": \"bauhaus\"", "\"name\": \"bauhaus-custom\"", StringComparison.Ordinal), copy);
        Assert.Null(ThemeLibrary.SourceText("no-such-theme"));

        var user = bauhaus with { SourcePath = "x.json" };
        Assert.Equal(ThemeFile.Export(user, "y"), Encoding.UTF8.GetString(SettingsMenu.ExportBytes(user, "y")));
    }

    [Fact]
    public void Subcommand_FindsTheWord_AndTheNameRules_ReadTheOneList()
    {
        Assert.Equal(ThemeText.EditWord, ThemeText.Subcommand("EDIT mine", out string rest));
        Assert.Equal(" mine", rest);
        Assert.Equal(ThemeText.ExportWord, ThemeText.Subcommand("export", out rest));
        Assert.Equal("", rest);
        Assert.Null(ThemeText.Subcommand("editor", out rest));
        Assert.Equal("editor", rest);
        Assert.Null(ThemeText.Subcommand("deleteme", out rest));                    // /theme delete (2026-11-20)
        Assert.Equal(ThemeText.DeleteWord, ThemeText.Subcommand("DELETE  x", out rest));
        Assert.Equal("  x", rest);                                                  // the delete's split reads it as x
        Assert.Equal(ThemeText.SubcommandWords.Count, ThemeText.SubcommandNotes.Count);

        Assert.All(ThemeText.SubcommandWords, w => Assert.False(ThemeFile.IsValidNewName(w)));
        Assert.All(ThemeText.LoadableSubcommandWords, w => Assert.True(ThemeFile.IsValidName(w)));
        Assert.All(ThemeText.RefusedOnLoad, w => Assert.False(ThemeFile.IsValidName(w)));
        Assert.EndsWith("and not \"export\")", ThemeText.BadName("x"), StringComparison.Ordinal);
        Assert.EndsWith("and not \"export\" or \"edit\" or \"delete\".", ThemeText.ExportBadName("x"), StringComparison.Ordinal);
        Assert.Equal("noir-custom", ThemeText.CopyName("noir"));
    }

    [Fact]
    public void ChecksWarning_ShowsThreeThenACount_AndFloorsEachRatio()
    {
        ContrastProblem P(string pair, double ratio) => new(pair, ratio, 4.5);
        string band = ThemeText.ChecksWarning([P("a", 4.46), P("b", 1.99), P("c", 3), P("d", 2), P("e", 2)]);

        Assert.Equal("⚠ a 4.4:1, wants 4.5 · b 1.9:1, wants 4.5 · c 3.0:1, wants 4.5 · and 2 more", band);
        Assert.DoesNotContain("more", ThemeText.ChecksWarning([P("a", 1)]), StringComparison.Ordinal);
    }

    /// <summary>
    /// The checks read the styles as the terminal resolves them (#100): a code style set to the page's own color is caught on both slabs, and
    /// a style with its own ground is judged on it, named by its key.
    /// </summary>
    [Fact]
    public void Problems_JudgeTheResolvedStyles_CodeOnTheSlabs_AndAStylesOwnGround()
    {
        var synthwave = ShippedThemes.Synthwave;
        var hidden = synthwave with
        {
            Styles = new Dictionary<ThemeStyleSlot, StyleOverride>
            {
                [ThemeStyleSlot.CodeKeyword] = new(Foreground: synthwave.Bg),
                [ThemeStyleSlot.MarkdownHeading1] = new(Foreground: synthwave.Ink, Background: synthwave.Ink),
            },
        };

        var pairs = ThemeChecks.Problems(hidden).Select(p => p.Pair).ToList();

        Assert.Contains(ThemeText.SlabPair("codeKeyword", "good"), pairs);
        Assert.Contains(ThemeText.SlabPair("codeKeyword", "bad"), pairs);
        Assert.Contains(ThemeText.OwnGroundPair("markdownHeading1"), pairs);
        Assert.Empty(ThemeChecks.Problems(synthwave));
    }
}
