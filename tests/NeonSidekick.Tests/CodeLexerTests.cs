using NeonSidekick.UI.Markdown;

namespace NeonSidekick.Tests;

public class CodeLexerTests
{
    /// <summary>The token that starts where <paramref name="text"/> first occurs (from <paramref name="from"/>): its kind and its whole text.</summary>
    private static (CodeTokenKind Kind, string Text) At(string code, CodeLanguage language, string text, int from = 0)
    {
        int index = code.IndexOf(text, from, StringComparison.Ordinal);
        Assert.True(index >= 0, $"'{text}' is not in the fixture");
        var token = CodeLexer.Lex(code, language).Single(t => t.Start <= index && index < t.End);
        return (token.Kind, code[token.Start..token.End]);
    }

    /// <summary>Trimmed: a plain run merges with the whitespace either side of it.</summary>
    private static void Is(CodeTokenKind kind, string code, CodeLanguage language, string text)
    {
        var (actualKind, actualText) = At(code, language, text);
        Assert.Equal((kind, text.Trim()), (actualKind, actualText.Trim()));
    }

    [Fact]
    public void CSharp_KeywordsTypesStringsNumbersComments()
    {
        const string code = "public static Task<int> Run(string name) { var x = 0x1F; return Foo(@\"a\"\"b\", $\"{name}\"); } // done";
        var cs = CodeLanguages.CSharp;

        Is(CodeTokenKind.Keyword, code, cs, "public");
        Is(CodeTokenKind.Keyword, code, cs, "string");
        Is(CodeTokenKind.Type, code, cs, "Task");
        Is(CodeTokenKind.Function, code, cs, "Run");
        Is(CodeTokenKind.Number, code, cs, "0x1F");
        Is(CodeTokenKind.Function, code, cs, "Foo");
        Is(CodeTokenKind.String, code, cs, "@\"a\"\"b\"");
        Is(CodeTokenKind.String, code, cs, "$\"{name}\"");
        Is(CodeTokenKind.Comment, code, cs, "// done");
        Is(CodeTokenKind.Plain, code, cs, "name");
    }

    [Fact]
    public void CSharp_DirectiveOpensALine_AndAllCapsIsNotAType()
    {
        const string code = "#region Setup\nconst int MAX = 1;";

        Is(CodeTokenKind.Keyword, code, CodeLanguages.CSharp, "#region");
        Is(CodeTokenKind.Plain, code, CodeLanguages.CSharp, "MAX");
    }

    [Fact]
    public void BlockComment_SpansLines()
    {
        const string code = "a /* one\ntwo */ b";

        Is(CodeTokenKind.Comment, code, CodeLanguages.C, "/* one\ntwo */");
        Is(CodeTokenKind.Plain, code, CodeLanguages.C, "b");
    }

    [Fact]
    public void Escapes_DoNotCloseTheString()
    {
        const string code = "s = \"a\\\"b\" + 'c'";

        Is(CodeTokenKind.String, code, CodeLanguages.JavaScript, "\"a\\\"b\"");
        Is(CodeTokenKind.String, code, CodeLanguages.JavaScript, "'c'");
    }

    [Fact]
    public void UnterminatedString_StopsAtTheLineEnd_UnterminatedCommentRunsToTheEnd()
    {
        // The fence is still streaming: neither is an error, and a single-line string never eats the next line.
        const string code = "x = \"open\ny = 1 /* still";
        var js = CodeLanguages.JavaScript;

        Is(CodeTokenKind.String, code, js, "\"open");
        Is(CodeTokenKind.Number, code, js, "1");
        Is(CodeTokenKind.Comment, code, js, "/* still");
    }

    [Fact]
    public void Python_TripleQuotedString_SpansLines_PrefixesAndDecorators()
    {
        const string code = "@app.route\ndef f(self):\n    \"\"\"Doc\n    more\"\"\"\n    return f\"{x}\" # note";
        var py = CodeLanguages.Python;

        Is(CodeTokenKind.Attribute, code, py, "@app.route");
        Is(CodeTokenKind.Keyword, code, py, "def");
        Assert.Equal((CodeTokenKind.Function, "f"), At(code, py, "f(self"));
        Is(CodeTokenKind.Keyword, code, py, "self");
        Is(CodeTokenKind.String, code, py, "\"\"\"Doc\n    more\"\"\"");
        Is(CodeTokenKind.String, code, py, "f\"{x}\"");
        Is(CodeTokenKind.Comment, code, py, "# note");
    }

    [Fact]
    public void Shell_VariablesFlagsBuiltinsAndBoundedComments()
    {
        const string code = "ls -la --color $HOME ${#arr} $1 # list";
        var sh = CodeLanguages.Shell;

        Is(CodeTokenKind.Type, code, sh, "ls");
        Is(CodeTokenKind.Attribute, code, sh, "-la");
        Is(CodeTokenKind.Attribute, code, sh, "--color");
        Is(CodeTokenKind.Variable, code, sh, "$HOME");
        Is(CodeTokenKind.Variable, code, sh, "${#arr}");
        Is(CodeTokenKind.Variable, code, sh, "$1");
        Is(CodeTokenKind.Comment, code, sh, "# list");
    }

    [Fact]
    public void PowerShell_CmdletsVariablesParametersAndCaseInsensitiveKeywords()
    {
        const string code = "IF ($env:PATH -eq 'a''b') { Get-ChildItem -Path $x } <# block\ncomment #>";
        var ps = CodeLanguages.PowerShell;

        Is(CodeTokenKind.Keyword, code, ps, "IF");
        Is(CodeTokenKind.Variable, code, ps, "$env:PATH");
        Is(CodeTokenKind.Attribute, code, ps, "-eq");
        Is(CodeTokenKind.String, code, ps, "'a''b'");
        Is(CodeTokenKind.Function, code, ps, "Get-ChildItem");
        Is(CodeTokenKind.Attribute, code, ps, "-Path");
        Is(CodeTokenKind.Comment, code, ps, "<# block\ncomment #>");
    }

    /// <summary>PowerShell's type accelerators (2026-11-20, the memory-remember review's #45): Type in any case; a dashed cmdlet and a $variable named like one stay theirs.</summary>
    [Fact]
    public void PowerShell_TypeAccelerators_AreTypes()
    {
        const string code = "[string]$string = [INT]5 | Select-Object";
        var ps = CodeLanguages.PowerShell;

        Is(CodeTokenKind.Type, code, ps, "string");
        Is(CodeTokenKind.Type, code, ps, "INT");
        Is(CodeTokenKind.Variable, code, ps, "$string");
        Is(CodeTokenKind.Function, code, ps, "Select-Object");
    }

    /// <summary>LexLines (2026-11-20): one list of runs per line, a token across a break split at it, a blank line empty, the runs the text exactly.</summary>
    [Fact]
    public void LexLines_GivesOneListPerLine_SplittingATokenAtABreak()
    {
        string[] lines = ["<# a", "b #>", "", "$x = 1"];
        var runs = CodeLexer.LexLines(lines, CodeLanguages.PowerShell);

        Assert.Equal(lines.Length, runs.Count);
        Assert.Equal(lines, runs.Select(line => string.Concat(line.Select(run => run.Text))));
        Assert.Equal([("<# a", CodeTokenKind.Comment)], runs[0]);
        Assert.Equal([("b #>", CodeTokenKind.Comment)], runs[1]);
        Assert.Empty(runs[2]);
        Assert.Equal(CodeTokenKind.Variable, runs[3][0].Kind);
        Assert.Empty(CodeLexer.LexLines([], CodeLanguages.PowerShell));
    }

    [Fact]
    public void Json_KeysAreAttributes_ValuesAreStrings()
    {
        const string code = "{ \"name\" : \"neon\", \"on\": true, \"n\": -1.5e3 }";
        var json = CodeLanguages.Json;

        Is(CodeTokenKind.Attribute, code, json, "\"name\"");
        Is(CodeTokenKind.String, code, json, "\"neon\"");
        Is(CodeTokenKind.Keyword, code, json, "true");
        Is(CodeTokenKind.Number, code, json, "1.5e3");
    }

    [Fact]
    public void Yaml_KeysListMarkersAndComments()
    {
        const string code = "name: neon\nitems:\n  - id: 1\n    url: http://x#y # real\nflag: True";
        var yaml = CodeLanguages.Yaml;

        Is(CodeTokenKind.Attribute, code, yaml, "name");
        Is(CodeTokenKind.Attribute, code, yaml, "items");
        Is(CodeTokenKind.Attribute, code, yaml, "id");
        Is(CodeTokenKind.Number, code, yaml, "1");
        Is(CodeTokenKind.Plain, code, yaml, "http");
        Is(CodeTokenKind.Comment, code, yaml, "# real");
        Is(CodeTokenKind.Keyword, code, yaml, "True");
    }

    [Fact]
    public void Toml_SectionsAndKeys()
    {
        const string code = "[server.http]\nport = 8080 # comment\nname = \"x\"";
        var toml = CodeLanguages.Toml;

        Is(CodeTokenKind.Heading, code, toml, "[server.http]");
        Is(CodeTokenKind.Attribute, code, toml, "port");
        Is(CodeTokenKind.Number, code, toml, "8080");
        Is(CodeTokenKind.Comment, code, toml, "# comment");
        Is(CodeTokenKind.String, code, toml, "\"x\"");
    }

    [Fact]
    public void Sql_CaseInsensitiveKeywords_DoubledQuotes_DashComments()
    {
        const string code = "select COUNT(*) FROM users WHERE name = 'O''Brien' -- who\nCREATE TABLE t (id INT);";
        var sql = CodeLanguages.Sql;

        Is(CodeTokenKind.Keyword, code, sql, "select");
        Is(CodeTokenKind.Function, code, sql, "COUNT");
        Is(CodeTokenKind.Keyword, code, sql, "FROM");
        Is(CodeTokenKind.String, code, sql, "'O''Brien'");
        Is(CodeTokenKind.Comment, code, sql, "-- who");
        Is(CodeTokenKind.Type, code, sql, "INT");
    }

    [Fact]
    public void Rust_MacrosLifetimesAttributesAndCharLiterals()
    {
        const string code = "#[derive(Debug)]\nfn f<'a>(s: &'a str) -> Option<char> { println!(\"x\"); 'c' }";
        var rust = CodeLanguages.Rust;

        Is(CodeTokenKind.Attribute, code, rust, "#[derive(Debug)]");
        Is(CodeTokenKind.Keyword, code, rust, "fn");
        Is(CodeTokenKind.Type, code, rust, "'a");
        Is(CodeTokenKind.Type, code, rust, "str");
        Is(CodeTokenKind.Type, code, rust, "Option");
        Is(CodeTokenKind.Function, code, rust, "println!");
        Is(CodeTokenKind.String, code, rust, "'c'");
    }

    [Fact]
    public void Go_RawBacktickString_SpansLines()
    {
        const string code = "s := `one\ntwo`\nvar n int";

        Is(CodeTokenKind.String, code, CodeLanguages.Go, "`one\ntwo`");
        Is(CodeTokenKind.Type, code, CodeLanguages.Go, "int");
    }

    [Fact]
    public void Css_SelectorsPropertiesColoursAndUnits()
    {
        const string code = ".card a:hover {\n  margin-top: 10px;\n  color: #ff2e97;\n}";
        var css = CodeLanguages.Css;

        Is(CodeTokenKind.Tag, code, css, ".card");
        Assert.Equal((CodeTokenKind.Tag, "a"), At(code, css, "a:hover"));
        Is(CodeTokenKind.Keyword, code, css, ":hover");
        Is(CodeTokenKind.Attribute, code, css, "margin-top");
        Is(CodeTokenKind.Number, code, css, "10px");
        Is(CodeTokenKind.Number, code, css, "#ff2e97");
    }

    [Fact]
    public void Markup_TagsAttributesValuesCommentsAndEntities()
    {
        const string code = "<!-- note -->\n<div class=\"x\" hidden>a &amp; b</div>\n<br/>";
        var xml = CodeLanguages.Markup;

        Is(CodeTokenKind.Comment, code, xml, "<!-- note -->");
        Is(CodeTokenKind.Tag, code, xml, "div");
        Is(CodeTokenKind.Attribute, code, xml, "class");
        Is(CodeTokenKind.String, code, xml, "\"x\"");
        Is(CodeTokenKind.Attribute, code, xml, "hidden");
        Is(CodeTokenKind.Keyword, code, xml, "&amp;");
        Assert.Equal(CodeTokenKind.Tag, At(code, xml, "div", code.IndexOf("</", StringComparison.Ordinal)).Kind);
    }

    [Fact]
    public void Markup_UnclosedTag_EndsAtTheNextTag()
    {
        const string code = "<a href=\"x\"\n<b>";

        Is(CodeTokenKind.Tag, code, CodeLanguages.Markup, "b");
    }

    [Fact]
    public void Diff_ClassifiesEachLineByItsFirstCharacter()
    {
        const string code = "--- a/x\n+++ b/x\n@@ -1 +1 @@\n-old\n+new\n same";
        var diff = CodeLanguages.Diff;

        Is(CodeTokenKind.Heading, code, diff, "--- a/x");
        Is(CodeTokenKind.Heading, code, diff, "@@ -1 +1 @@");
        Is(CodeTokenKind.Deleted, code, diff, "-old");
        Is(CodeTokenKind.Inserted, code, diff, "+new");
        Is(CodeTokenKind.Plain, code, diff, " same");
    }

    [Fact]
    public void EmptyText_HasNoTokens()
    {
        Assert.Empty(CodeLexer.Lex("", CodeLanguages.CSharp));
    }

    public static TheoryData<string, string> Fixtures() => new()
    {
        { "csharp", "public class A { int x = 1; } // c\n#if DEBUG\n/* open" },
        { "python", "def f():\n    '''doc\n    return b'x' # c" },
        { "bash", "for f in *.txt; do echo \"$f\" ${x:-y}; done # c\n'unterminated" },
        { "powershell", "$a = @{ k = 'v' }; Get-Item -Path \"x\" <# open" },
        { "json", "{\"a\": [1, 2.5, null], \"b\": \"x\\\"y\"}" },
        { "yaml", "- a: 1\n  b: 'c'\n---\n# c\nkey without colon" },
        { "toml", "[a]\nb = \"\"\"multi\nline\"\"\"\n[unclosed" },
        { "sql", "SELECT * FROM t WHERE a = 'x' /* c */;\n-- end" },
        { "c", "#include <stdio.h>\nint main(void) { return 0; }" },
        { "java", "@Override public String toString() { return \"\"\"\n text\"\"\"; }" },
        { "kotlin", "fun main() { val s = \"\"\"raw\"\"\" }" },
        { "go", "func main() { r := 'x'; s := `raw` }" },
        { "rust", "#![allow(x)] fn f<'a>() -> &'a str { r\"raw\" }" },
        { "css", "@media x { .a { color: #fff; -webkit-x: 1.5em } }" },
        { "html", "<?xml version=\"1.0\"?><![CDATA[x]]><a b='c'>t</a><unclosed x=\"" },
        { "diff", "+a\n-b\n c\n" },
        { "ts", "" },
        { "js", "\n\n" },
    };

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Tokens_CoverTheText_ExactlyOnce_AndAdjacentKindsDiffer(string language, string code)
    {
        var tokens = CodeLexer.Lex(code, CodeLanguages.Find(language)!);

        int at = 0;
        for (int t = 0; t < tokens.Count; t++)
        {
            Assert.Equal(at, tokens[t].Start);
            Assert.True(tokens[t].Length > 0);
            if (t > 0)
            {
                Assert.NotEqual(tokens[t - 1].Kind, tokens[t].Kind);
            }

            at = tokens[t].End;
        }

        Assert.Equal(code.Length, at);
    }
}
