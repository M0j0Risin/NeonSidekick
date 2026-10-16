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
}
