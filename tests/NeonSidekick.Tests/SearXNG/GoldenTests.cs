// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text.Json;
using System.Text.RegularExpressions;
using NeonSidekick.Tests.Fakes;
using SearXNG.Net.Engines;
using SearXNG.Net.Html;
using SearXNG.Net.Net;
using SearXNG.Net.Results;

namespace SearXNG.Net.Tests;

/// <summary>
/// Compares the port with what SearXNG's own code did on the same inputs. <c>Fixtures/searxng/golden/searxng.json</c> is recorded by
/// <c>tools/searxng/generate_goldens.py</c>. Each test collects every mismatch, so one run shows them all.
/// </summary>
public partial class GoldenTests
{
    private static readonly JsonElement Golden = JsonDocument.Parse(Fixtures.Read("golden/searxng.json")).RootElement;

    /// <summary>An array of the recording. An empty one fails, or a loop over it would pass by checking nothing.</summary>
    private static IEnumerable<JsonElement> Section(string name)
    {
        var items = Golden.GetProperty(name).EnumerateArray().ToList();
        Assert.NotEmpty(items);
        return items;
    }

    /// <summary>An object of the recording, held to the same rule.</summary>
    private static List<JsonProperty> Entries(JsonElement parent, string name)
    {
        var items = parent.GetProperty(name).EnumerateObject().ToList();
        Assert.NotEmpty(items);
        return items;
    }

    private static string? Str(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static void AssertNoMismatches(List<string> mismatches) =>
        Assert.True(mismatches.Count == 0, mismatches.Count + " mismatch(es):\n" + string.Join("\n", mismatches));

    private static void Compare(List<string> mismatches, string label, string? expected, string? actual)
    {
        if (expected != actual)
        {
            mismatches.Add($"{label}\n  searxng: {expected ?? "(null)"}\n  port:    {actual ?? "(null)"}");
        }
    }

    /// <summary><c>html.parser</c> changes between patch releases, and <c>HtmlText</c> follows this one.</summary>
    [Fact]
    public void TheRecording_IsOfThePythonTheParserFollows()
    {
        Assert.Equal("3.13.14", Str(Golden, "python_version"));
    }

    [Fact]
    public void HtmlToText()
    {
        var mismatches = new List<string>();
        foreach (var item in Section("html_to_text"))
        {
            var input = Str(item, "input")!;
            Compare(mismatches, JsonSerializer.Serialize(input, GoldenJson.Default.String), Str(item, "output"), HtmlText.FromFragment(input));
        }

        AssertNoMismatches(mismatches);
    }

    [Fact]
    public void NormalizeText()
    {
        var mismatches = new List<string>();
        foreach (var item in Section("normalize_text"))
        {
            var input = Str(item, "input")!;
            Compare(mismatches, JsonSerializer.Serialize(input, GoldenJson.Default.String), Str(item, "output"), ResultNormalizer.NormalizeText(input, item.GetProperty("max").GetInt32()));
        }

        AssertNoMismatches(mismatches);
    }

    [Fact]
    public void UrlParts()
    {
        var mismatches = new List<string>();
        foreach (var item in Section("urls"))
        {
            if (!item.TryGetProperty("parts", out var parts))
            {
                continue;
            }

            var input = Str(item, "input")!;
            var parsed = ParsedUrl.Parse(input);
            string actual = string.Join(" | ", parsed.Scheme, parsed.Netloc, parsed.Path, parsed.Params, parsed.Query, parsed.Fragment);
            Compare(mismatches, input, string.Join(" | ", parts.EnumerateArray().Select(p => p.GetString())), actual);
        }

        AssertNoMismatches(mismatches);
    }

    /// <summary>
    /// The port's one deliberate difference here: these aren't web addresses, so they are dropped. SearXNG keeps them and leaves
    /// filtering to its templates. Each is named, so any other difference still fails.
    /// </summary>
    private static readonly HashSet<string> DroppedOnPurpose = new(StringComparer.Ordinal)
    {
        "javascript:alert(1)",
        "data:text/html,hello",
        "file:///etc/passwd",
        "mailto:a@example.com",
        "example.com/no-host",
        "http:///nohost",
        "1http://example.com/x",
    };

    [Fact]
    public void NormalizedUrls()
    {
        var mismatches = new List<string>();
        foreach (var item in Section("urls"))
        {
            var input = Str(item, "input")!;
            var expected = DroppedOnPurpose.Contains(input) ? null : Str(item, "normalized") ?? "error: " + Str(item, "normalize_error");
            Compare(mismatches, input, expected, ResultNormalizer.NormalizeUrl(input)?.GetUrl());
        }

        AssertNoMismatches(mismatches);
    }

    [Fact]
    public void UrlEncodingHelpers()
    {
        var mismatches = new List<string>();
        foreach (var item in Section("quote_plus"))
        {
            Compare(mismatches, "quote_plus " + Str(item, "input"), Str(item, "output"), UrlEncoding.QuotePlus(Str(item, "input")!));
        }

        foreach (var item in Section("parse_qs_u"))
        {
            Compare(mismatches, "parse_qs " + Str(item, "input"), Str(item, "output"), UrlEncoding.FirstQueryValue(Str(item, "input")!, "u"));
        }

        AssertNoMismatches(mismatches);
    }

    [Fact]
    public void BingRequests()
    {
        var mismatches = new List<string>();
        foreach (var item in Section("bing_requests"))
        {
            var query = new EngineQuery(Str(item, "query")!, 1, (SafeSearch)item.GetProperty("safesearch").GetInt32(), Str(item, "locale")!);
            var expected = SetLangAndCountry().Replace(Str(item, "url")!, m => "&mkt=" + m.Groups[1].Value + "-" + m.Groups[2].Value.ToUpperInvariant());
            Compare(mismatches, Str(item, "query") + " / " + Str(item, "locale"), expected, BingEngine.BuildUrl(query).AbsoluteUri);
        }

        AssertNoMismatches(mismatches);
    }

    /// <summary>A deliberate difference, listed in the README: the port sends a market as mkt. The rest of the URL must still match.</summary>
    [GeneratedRegex("&setlang=([a-z]+)&cc=([a-z]+)")]
    private static partial Regex SetLangAndCountry();

    [Fact]
    public void BingRegionTable()
    {
        var mismatches = new List<string>();
        foreach (var region in Entries(Golden, "bing_regions"))
        {
            Compare(mismatches, region.Name, region.Value.GetString(), BingEngine.RegionFor(region.Name));
        }

        AssertNoMismatches(mismatches);
    }

    [Fact]
    public void BingRedirects()
    {
        var mismatches = new List<string>();
        foreach (var item in Section("bing_redirects"))
        {
            Compare(mismatches, Str(item, "input")!, Str(item, "output"), BingEngine.DecodeRedirect(Str(item, "input")!));
        }

        AssertNoMismatches(mismatches);
    }

    private static void CompareResults(List<string> mismatches, string label, JsonElement expected, IReadOnlyList<EngineResult> actual)
    {
        var want = expected.EnumerateArray().ToList();
        Compare(mismatches, label + " count", want.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), actual.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        for (int i = 0; i < Math.Min(want.Count, actual.Count); i++)
        {
            Compare(mismatches, $"{label}[{i}] url", Str(want[i], "url"), actual[i].Url);
            Compare(mismatches, $"{label}[{i}] title", Str(want[i], "title"), actual[i].Title);
            Compare(mismatches, $"{label}[{i}] content", Str(want[i], "content"), actual[i].Content);
        }
    }

    [Fact]
    public void BingParsing()
    {
        var mismatches = new List<string>();
        CompareResults(mismatches, "fixture", Golden.GetProperty("bing_fixture"), BingEngine.Parse(Fixtures.Read("bing-results.html")));
        foreach (var snippet in Entries(Golden, "bing_snippets"))
        {
            // SearXNG reads this as no results; here a page that lost its structure is a failure, as the README says.
            if (snippet.Name == "no_results")
            {
                Assert.Throws<EngineException>(() => BingEngine.Parse(Str(snippet.Value, "input")!));
            }

            // Compared in the next test.
            else if (snippet.Name != IconCase)
            {
                CompareResults(mismatches, snippet.Name, snippet.Value.GetProperty("output"), BingEngine.Parse(Str(snippet.Value, "input")!));
            }
        }

        AssertNoMismatches(mismatches);
    }

    private const string IconCase = "icon_with_following_text";

    /// <summary>A deliberate difference: lxml's remove takes the text after an icon with it, which SearXNG doesn't appear to intend.</summary>
    [Fact]
    public void BingIcon_SearXngLosesTheTextAfterIt_ThePortKeepsIt()
    {
        var icon = Golden.GetProperty("bing_snippets").GetProperty(IconCase);

        Assert.Equal("", Str(icon.GetProperty("output")[0], "content"));
        Assert.Equal("The snippet", Assert.Single(BingEngine.Parse(Str(icon, "input")!)).Content);
    }

    private const string FirstPage = "<html><head><link id=\"deep_preload_link\" rel=\"preload\" as=\"script\" href=\"https://links.duckduckgo.com/d.js?q=dotnet&amp;vqd=TOKEN&amp;dp=TOKEN\"></head></html>";

    [Fact]
    public async Task DuckDuckGoParsingAndFlow()
    {
        var mismatches = new List<string>();
        var flow = Golden.GetProperty("ddg_flow");
        var handler = new StubHandler(request => StubHandler.Text(request.RequestUri!.AbsolutePath == "/d.js" ? Fixtures.Read("ddg-results.json") : FirstPage));
        using var http = new HttpClient(handler);
        var engine = new DuckDuckGoEngine();

        var results = await engine.SearchAsync(new EngineQuery("dotnet native aot"), http, CancellationToken.None);

        CompareResults(mismatches, "fixture", Golden.GetProperty("ddg_fixture"), results);
        Compare(mismatches, "first page url", Str(flow, "first_page_url"), handler.Requests[0].RequestUri!.AbsoluteUri);
        Compare(mismatches, "api url", Str(flow, "api_url"), handler.Requests[1].RequestUri!.AbsoluteUri);
        foreach (var header in Entries(flow, "api_headers"))
        {
            // NonValidated gives the value as sent; TryGetValues would parse it and hand back .NET's reformatting.
            var sent = handler.Requests[1].Headers.NonValidated.TryGetValues(header.Name, out var values) ? values.ToString() : null;
            Compare(mismatches, "api header " + header.Name, header.Value.GetString(), sent);
        }

        handler.Requests.Clear();
        await engine.SearchAsync(new EngineQuery("dotnet native aot", PageNumber: 2), http, CancellationToken.None);
        var nextLink = Str(flow, "next_page_link")!.Replace("/d.js?", "/d.js?o=json&", StringComparison.Ordinal);
        Compare(mismatches, "next page url", nextLink, handler.Requests[0].RequestUri!.OriginalString);

        handler.Requests.Clear();
        await engine.SearchAsync(new EngineQuery(new string('a', 500)), http, CancellationToken.None);
        Compare(mismatches, "requests for a 500 character query", "0", handler.Requests.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(JsonValueKind.Null, flow.GetProperty("url_for_500_chars").ValueKind);

        AssertNoMismatches(mismatches);
    }

    [Fact]
    public void HttpErrors()
    {
        var mismatches = new List<string>();
        var page = new Uri("https://example.com/");
        foreach (var item in Section("errors"))
        {
            int status = item.GetProperty("status").GetInt32();
            var label = $"{status} {Str(item, "server")} {Str(item, "text")}";
            string? kind = null;
            string? rest = null;
            try
            {
                EngineHttp.RaiseForHttpError(status, Str(item, "server")!, Str(item, "text")!, page);
            }
            catch (EngineException e)
            {
                kind = e.Kind switch
                {
                    EngineFailureKind.Captcha => "SearxEngineCaptchaException",
                    EngineFailureKind.AccessDenied => "SearxEngineAccessDeniedException",
                    EngineFailureKind.TooManyRequests => "SearxEngineTooManyRequestsException",
                    EngineFailureKind.HttpError => "HTTPError",
                    _ => e.Kind.ToString(),
                };
                rest = e.SuspendFor?.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            Compare(mismatches, label + " kind", Str(item, "raised"), kind);
            var wantRest = item.TryGetProperty("suspended_time", out var seconds) && seconds.ValueKind == JsonValueKind.Number
                ? seconds.GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture)
                : null;
            Compare(mismatches, label + " rest", wantRest, rest);
        }

        AssertNoMismatches(mismatches);
    }

    private static void CompareMerged(List<string> mismatches, string label, JsonElement expected, IReadOnlyList<MergedResult> actual)
    {
        var want = expected.EnumerateArray().ToList();
        Compare(mismatches, label + " order", string.Join("\n    ", want.Select(w => Str(w, "url"))), string.Join("\n    ", actual.Select(a => a.Url)));
        for (int i = 0; i < Math.Min(want.Count, actual.Count); i++)
        {
            var at = $"{label}[{i}] ";
            Compare(mismatches, at + "url", Str(want[i], "url"), actual[i].Url);
            Compare(mismatches, at + "title", Str(want[i], "title"), actual[i].Title);
            Compare(mismatches, at + "content", Str(want[i], "content"), actual[i].Content);
            Compare(mismatches, at + "positions", string.Join(",", want[i].GetProperty("positions").EnumerateArray().Select(p => p.GetInt32())), string.Join(",", actual[i].Positions));
            Compare(mismatches, at + "engines", string.Join(",", want[i].GetProperty("engines").EnumerateArray().Select(e => e.GetString())), string.Join(",", actual[i].Engines.Order(StringComparer.Ordinal)));
            if (Math.Abs(want[i].GetProperty("score").GetDouble() - actual[i].Score) > 1e-9)
            {
                mismatches.Add($"{at}score\n  searxng: {want[i].GetProperty("score").GetDouble()}\n  port:    {actual[i].Score}");
            }
        }
    }

    [Fact]
    public void MergeScenarios()
    {
        var mismatches = new List<string>();
        foreach (var scenario in Entries(Golden, "merge_scenarios"))
        {
            var engines = scenario.Value.GetProperty("engines").EnumerateObject().ToDictionary(
                e => e.Name,
                e => (IEngine)new FakeEngine(e.Name) { Weight = e.Value[0].GetDouble(), Category = e.Value[1].GetString()! },
                StringComparer.Ordinal);
            var container = new ResultContainer(engines);
            foreach (var extend in scenario.Value.GetProperty("extends").EnumerateArray())
            {
                container.Extend(extend[0].GetString()!, extend[1].EnumerateArray().Select(r => new EngineResult(Str(r, "url")!, Str(r, "title")!, Str(r, "content")!)));
            }

            CompareMerged(mismatches, scenario.Name, scenario.Value.GetProperty("expected"), container.GetOrderedResults());
        }

        AssertNoMismatches(mismatches);
    }

    [Fact]
    public async Task MergingTheCapturedPages()
    {
        var handler = new StubHandler(request => StubHandler.Text(request.RequestUri!.AbsolutePath == "/d.js" ? Fixtures.Read("ddg-results.json") : FirstPage));
        using var http = new HttpClient(handler);
        var ddg = await new DuckDuckGoEngine().SearchAsync(new EngineQuery("x"), http, CancellationToken.None);
        var engines = new Dictionary<string, IEngine>(StringComparer.Ordinal)
        {
            ["bing"] = new FakeEngine("bing"),
            ["duckduckgo web"] = new FakeEngine("duckduckgo web"),
        };
        var container = new ResultContainer(engines);
        container.Extend("bing", BingEngine.Parse(Fixtures.Read("bing-results.html")));
        container.Extend("duckduckgo web", ddg);

        var mismatches = new List<string>();
        CompareMerged(mismatches, "merged", Golden.GetProperty("merged_fixtures"), container.GetOrderedResults());

        AssertNoMismatches(mismatches);
    }

    [Fact]
    public void EngineSettings()
    {
        var settings = Golden.GetProperty("settings");
        var pairs = new (string Name, IEngine Engine)[] { ("bing", new BingEngine()), ("duckduckgo", new DuckDuckGoEngine()), ("brave", new BraveEngine()), ("startpage", new StartpageEngine()), ("wikipedia", new WikipediaEngine()) };
        foreach (var (name, engine) in pairs)
        {
            var want = settings.GetProperty(name);
            Assert.Equal(want.GetProperty("categories")[0].GetString(), engine.Category);
            Assert.Equal(want.GetProperty("paging").GetBoolean(), engine.Paging);
            Assert.Equal(want.GetProperty("timeout").GetDouble(), engine.Timeout.TotalSeconds);
            Assert.Equal(want.GetProperty("weight").GetDouble(), engine.Weight);
        }

        // suspend(): min(max_ban_time_on_fail, ban_time_on_fail).
        int rest = Math.Min(settings.GetProperty("max_ban_time_on_fail").GetInt32(), settings.GetProperty("ban_time_on_fail").GetInt32());
        Assert.Equal(TimeSpan.FromSeconds(rest), new MetaSearchOptions().DefaultSuspendTime);
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
internal sealed partial class GoldenJson : System.Text.Json.Serialization.JsonSerializerContext;
