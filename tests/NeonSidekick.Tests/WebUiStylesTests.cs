using System.Text;
using System.Text.RegularExpressions;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// The Web UI's one stylesheet (2026-10-16, after be3799c7's Usage card added a global <c>.meter</c> that squeezed the Home tab's PC meters
/// into 8 px pills, and a bare <c>.sheet-bar</c> meant for Chat's prompts sheet that reached Home's device sheet): the pages share
/// <c>app.css</c>, so a class one page styles bare is never styled bare again by another rule. Read from the served (embedded) copy.
/// </summary>
public sealed partial class WebUiStylesTests
{
    [Fact]
    public void NoBareClassSelector_BeginsTwoTopLevelRules()
    {
        string css = Encoding.UTF8.GetString(WebUiAssets.Find("app.css")!.Bytes);
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var clashes = new List<string>();
        foreach (var (selector, line) in TopLevelSelectors(css))
        {
            foreach (string part in selector.Split(','))
            {
                string name = part.Trim();
                if (!BareClass().IsMatch(name))
                {
                    continue;
                }

                if (seen.TryGetValue(name, out int first))
                {
                    clashes.Add(name + " at lines " + first + " and " + line);
                }
                else
                {
                    seen[name] = line;
                }
            }
        }

        Assert.True(clashes.Count == 0, "app.css styles a bare class in two top-level rules: " + string.Join("; ", clashes));
    }

    [Fact]
    public void TheScan_SkipsMediaBlocks_AndComments_AndKeepsEachRulesLine()
    {
        const string css = ".a { x: 1; }\n/* .b { } */\n@media (max-width: 1px) { .a { x: 2; } }\n.c .a { }\n.a, .d { }\n";
        var selectors = TopLevelSelectors(css).ToList();
        Assert.Equal([(".a", 1), (".c .a", 4), (".a, .d", 5)], selectors);
    }

    /// <summary>
    /// An armed button's words on the error color (2026-11-17, raised after the admin-themes-render review): white on app.css's bright red
    /// read 2.8:1. Every rule that paints <c>--error</c> as its ground and sets a text color takes <c>--on-error</c>, which a readable
    /// theme's <c>/theme.css</c> sends, and the fallback reads at 4.5:1 on app.css's own red. A rule with no text color (the 🧠 glyph) is left be.
    /// </summary>
    [Fact]
    public void TextOnTheErrorColor_TakesOnError_AndItsFallbackReads()
    {
        string css = Encoding.UTF8.GetString(WebUiAssets.Find("app.css")!.Bytes);
        var wrong = new List<string>();
        int checkedRules = 0;
        foreach (Match rule in InnermostRule().Matches(css))
        {
            string body = rule.Groups[2].Value;
            if (!ErrorGround().IsMatch(body) || !TextColor().IsMatch(body))
            {
                continue;
            }

            checkedRules++;
            Match color = TextColor().Match(body);
            if (!color.Groups[1].Value.StartsWith("var(--on-error", StringComparison.Ordinal))
            {
                wrong.Add(rule.Groups[1].Value.Trim() + " → " + color.Groups[1].Value);
            }
        }

        Assert.True(checkedRules >= 5, "found " + checkedRules);
        Assert.True(wrong.Count == 0, "text on --error without --on-error: " + string.Join("; ", wrong));

        string red = Regex.Match(css, @"--error:\s*(#[0-9a-fA-F]{6})").Groups[1].Value;
        Assert.True(UI.ThemeFile.TryParseColor(red, out var ground));
        foreach (Match fallback in Regex.Matches(css, @"var\(--on-error,\s*(#[0-9a-fA-F]{6})\)"))
        {
            Assert.True(UI.ThemeFile.TryParseColor(fallback.Groups[1].Value, out var ink));
            Assert.True(UI.ColorContrast.Contrast(ink, ground) >= 4.5, fallback.Groups[1].Value + " on " + red);
        }
    }

    /// <summary>
    /// The un-themed page on black (2026-11-20, the user's ask: like the app's windows with <i>Themed external windows</i> off): app.css's
    /// <c>--bg</c> and <c>--text</c> are <see cref="WebTheme.PageGround"/> and <see cref="WebTheme.PageText"/> (the manifest's and the home
    /// tiles' twins, "change them together"), and the text, the dim text and each speaker read at 4.5:1 on the ground and both panels.
    /// </summary>
    [Fact]
    public void ThePagesOwnLook_IsBlack_TheConstantsMatch_AndItsInksRead()
    {
        string css = Encoding.UTF8.GetString(WebUiAssets.Find("app.css")!.Bytes);
        string Var(string name)
        {
            var found = Regex.Match(css, "--" + name + @":\s*(#[0-9a-fA-F]{6})");
            Assert.True(found.Success, "--" + name);
            return found.Groups[1].Value;
        }

        Assert.Equal("#000000", Var("bg"));
        Assert.Equal(WebTheme.PageGround, Var("bg"));
        Assert.Equal(WebTheme.PageText, Var("text"));
        Assert.True(UI.ThemeFile.TryParseColor(WebTheme.PageGround, out var groundColor));
        Assert.Equal(WebTheme.PageGroundColor, groundColor);

        string[] grounds = ["bg", "panel", "panel-2"];
        string[] inks = ["text", "dim", "speaker-0", "speaker-1", "speaker-2", "speaker-3", "speaker-4", "speaker-5"];
        foreach (string ground in grounds)
        {
            Assert.True(UI.ThemeFile.TryParseColor(Var(ground), out var back));
            foreach (string ink in inks)
            {
                Assert.True(UI.ThemeFile.TryParseColor(Var(ink), out var fore));
                Assert.True(UI.ColorContrast.Contrast(fore, back) >= 4.5, "--" + ink + " on --" + ground);
            }
        }
    }

    /// <summary>Each top-level rule's selector text with the line it begins on: comments left out, at-rule blocks (<c>@media</c>…) skipped whole.</summary>
    internal static IEnumerable<(string Selector, int Line)> TopLevelSelectors(string css)
    {
        var text = new StringBuilder();
        int depth = 0;
        int line = 1;
        int start = 1;
        bool skipping = false;
        for (int i = 0; i < css.Length; i++)
        {
            char c = css[i];
            if (c == '/' && i + 1 < css.Length && css[i + 1] == '*')
            {
                int end = css.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? css.Length : end + 2;
                line += css.AsSpan(i, end - i).Count('\n');
                i = end - 1;
                continue;
            }

            if (c == '\n')
            {
                line++;
            }

            if (c == '{')
            {
                if (depth == 0)
                {
                    string selector = text.ToString().Trim();
                    skipping = selector.StartsWith('@');
                    if (!skipping)
                    {
                        yield return (selector, start);
                    }

                    text.Clear();
                }

                depth++;
                continue;
            }

            if (c == '}')
            {
                depth = Math.Max(0, depth - 1);
                if (depth == 0)
                {
                    skipping = false;
                }

                continue;
            }

            if (depth == 0)
            {
                if (c == ';')
                {
                    text.Clear();   // an @import or @charset line
                    continue;
                }

                if (text.Length == 0 && char.IsWhiteSpace(c))
                {
                    continue;
                }

                if (text.Length == 0)
                {
                    start = line;
                }

                text.Append(c);
            }
        }
    }

    [GeneratedRegex(@"^\.[A-Za-z_][\w-]*$")]
    private static partial Regex BareClass();

    /// <summary>A rule with no block inside it: its selector, then its declarations (inside an <c>@media</c> too).</summary>
    [GeneratedRegex(@"([^{}]*)\{([^{}]*)\}")]
    private static partial Regex InnermostRule();

    [GeneratedRegex(@"(?<![\w-])background(?:-color)?:\s*var\(--error\b")]
    private static partial Regex ErrorGround();

    /// <summary>The <c>color</c> declaration (not <c>background-color</c> or <c>border-color</c>) and its value.</summary>
    [GeneratedRegex(@"(?<![\w-])color:\s*([^;}]+)")]
    private static partial Regex TextColor();
}
