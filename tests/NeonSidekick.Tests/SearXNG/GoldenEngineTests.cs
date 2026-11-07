// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Net;
using System.Text.Json;
using SearXNG.Net.Engines;
using SearXNG.Net.Locales;

namespace SearXNG.Net.Tests;

/// <summary>The same comparison for Brave, Startpage and Wikipedia.</summary>
public partial class GoldenTests
{
    private static EngineQuery QueryFrom(JsonElement item) =>
        new(
            Str(item, "query")!,
            item.TryGetProperty("page", out var page) ? page.GetInt32() : 1,
            item.TryGetProperty("safesearch", out var safe) ? (SafeSearch)safe.GetInt32() : SafeSearch.Off,
            Str(item, "locale")!);

    [Fact]
    public void BraveRequests()
    {
        var mismatches = new List<string>();
        foreach (var item in Section("brave_requests"))
        {
            var query = QueryFrom(item);
            var label = query.Query + " / " + query.Locale;
            Compare(mismatches, label + " url", Str(item, "url"), BraveEngine.BuildUrl(query).AbsoluteUri);
            var cookies = string.Join("; ", item.GetProperty("cookies").EnumerateObject().Select(c => c.Name + "=" + c.Value.GetString()));
            Compare(mismatches, label + " cookies", cookies, BraveEngine.BuildCookie(query));
        }

        AssertNoMismatches(mismatches);
    }

    [Fact]
    public void BraveParsing()
    {
        var mismatches = new List<string>();
        CompareResults(mismatches, "fixture", Golden.GetProperty("brave_fixture"), BraveEngine.Parse(Fixtures.Read("brave-results.html")));
        foreach (var snippet in Entries(Golden, "brave_snippets"))
        {
            // Compared in the next test.
            if (snippet.Name != BackticksCase)
            {
                CompareResults(mismatches, snippet.Name, snippet.Value.GetProperty("output"), BraveEngine.Parse(Str(snippet.Value, "input")!));
            }
        }

        AssertNoMismatches(mismatches);
    }

    private const string BackticksCase = "backticks";

    /// <summary>A deliberate difference: SearXNG's rewrite into JSON can't take a double quote in a backtick string. This reader can.</summary>
    [Fact]
    public void BraveBackticks_SearXngFails_ThePortReadsThem()
    {
        var page = Golden.GetProperty("brave_snippets").GetProperty(BackticksCase);

        Assert.StartsWith("JSONDecodeError", Str(page, "error"), StringComparison.Ordinal);
        var result = Assert.Single(BraveEngine.Parse(Str(page, "input")!));
        Assert.Equal("tick", result.Title);
        Assert.Equal("a \"quoted\" word", result.Content);
    }

    [Fact]
    public void BraveBadPagesFailInBoth()
    {
        foreach (var page in Entries(Golden, "brave_bad_pages"))
        {
            Assert.NotNull(Str(page.Value, "error"));
            var e = Assert.Throws<EngineException>(() => BraveEngine.Parse(Str(page.Value, "input")!));
            Assert.Equal(EngineFailureKind.UnexpectedResponse, e.Kind);
        }
    }

    [Fact]
    public void StartpageRequests()
    {
        // The code SearXNG read from its stubbed home page, with the entity decoded as lxml does.
        var code = StartpageEngine.ParseCode("<html><form id=\"search\" action=\"/sp/search\"><input type=\"hidden\" name=\"sc\" value=\"CODE&amp;123\"></form></html>");
        var mismatches = new List<string>();
        foreach (var item in Section("startpage_requests"))
        {
            var query = QueryFrom(item);
            var label = query.Query + " / " + query.Locale;
            Compare(mismatches, label + " method", "POST", Str(item, "method"));
            Compare(mismatches, label + " url", "https://www.startpage.com/sp/search", Str(item, "url"));
            Compare(mismatches, label + " preferences", Str(item, "preferences"), StartpageEngine.BuildPreferences(query));
            var want = string.Join("&", item.GetProperty("form").EnumerateArray().Select(p => p[0].GetString() + "=" + p[1].ToString()));
            var form = string.Join("&", StartpageEngine.BuildForm(query, code!).Select(p => p.Key + "=" + p.Value));
            Compare(mismatches, label + " form", want, form);
        }

        AssertNoMismatches(mismatches);
    }

    [Fact]
    public void StartpageParsing()
    {
        var mismatches = new List<string>();
        CompareResults(mismatches, "fixture", Golden.GetProperty("startpage_fixture"), StartpageEngine.Parse(Fixtures.Read("startpage-results.html")));
        foreach (var snippet in Entries(Golden, "startpage_snippets"))
        {
            // SearXNG reads this as no results; here a page that lost its structure is a failure, as the README says.
            if (snippet.Name == "no_mainline")
            {
                Assert.Throws<EngineException>(() => StartpageEngine.Parse(Str(snippet.Value, "input")!));
                continue;
            }

            CompareResults(mismatches, snippet.Name, snippet.Value.GetProperty("output"), StartpageEngine.Parse(Str(snippet.Value, "input")!));
        }

        AssertNoMismatches(mismatches);
        Assert.NotNull(Str(Golden, "startpage_no_marker_error"));
        Assert.Equal(EngineFailureKind.UnexpectedResponse, Assert.Throws<EngineException>(() => StartpageEngine.Parse("<html>nothing</html>")).Kind);
    }

    [Fact]
    public void WikipediaRequests()
    {
        var mismatches = new List<string>();
        foreach (var item in Section("wikipedia_requests"))
        {
            var query = QueryFrom(item);

            // A deliberate difference, listed in the README: a slash in a title is encoded, so it can't change the API path.
            var expected = Str(item, "url")!.Replace("/summary/AC/DC", "/summary/AC%2FDC", StringComparison.Ordinal);
            Compare(mismatches, query.Query + " / " + query.Locale, expected, WikipediaEngine.BuildUrl(query).AbsoluteUri);
        }

        AssertNoMismatches(mismatches);
    }

    [Fact]
    public void WikipediaParsing()
    {
        var mismatches = new List<string>();
        CompareResults(mismatches, "fixture", Golden.GetProperty("wikipedia_fixture"), WikipediaEngine.Parse(Fixtures.Read("wikipedia-summary.json")));
        foreach (var item in Entries(Golden, "wikipedia_cases"))
        {
            var status = (HttpStatusCode)item.Value.GetProperty("status").GetInt32();
            var text = Str(item.Value, "input")!;
            CompareResults(mismatches, item.Name, item.Value.GetProperty("output"), WikipediaEngine.IsNoArticle(status, text) ? [] : WikipediaEngine.Parse(text));
        }

        AssertNoMismatches(mismatches);
    }

    [Fact]
    public void WikipediaFailuresFailInBoth()
    {
        foreach (var item in Entries(Golden, "wikipedia_fails"))
        {
            Assert.NotNull(Str(item.Value, "error"));
            var status = (HttpStatusCode)item.Value.GetProperty("status").GetInt32();
            var text = Str(item.Value, "input")!;
            Assert.False(WikipediaEngine.IsNoArticle(status, text), item.Name);
            if (status == HttpStatusCode.OK)
            {
                Assert.Throws<EngineException>(() => WikipediaEngine.Parse(text));
            }
        }
    }

    [Fact]
    public void NewerEngineLocales()
    {
        var tables = Golden.GetProperty("locale_tables");
        var mismatches = new List<string>();
        void Check(string table, Func<string, string?> port)
        {
            foreach (var entry in Entries(tables, table))
            {
                Compare(mismatches, table + " " + entry.Name, entry.Value.GetString(), port(entry.Name));
            }
        }

        Check("brave_regions", t => t == EngineQuery.AllLocales ? "all" : EngineLocale.Resolve(t, BraveLocales.Regions, "all"));
        Check("brave_interface", t => EngineLocale.Resolve(t, BraveLocales.InterfaceLanguages, "en-us"));
        Check("startpage_regions", StartpageEngine.RegionFor);
        Check("startpage_languages", StartpageEngine.LanguageFor);
        Check("wikipedia_hosts", WikipediaEngine.HostFor);
        AssertNoMismatches(mismatches);
    }
}
