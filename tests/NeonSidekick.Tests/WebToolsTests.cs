using System.Net;
using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.Web;

namespace NeonSidekick.Tests;

/// <summary>The web tools over a stub client and a fake browser: the argument reading, the settings they read per call, the paging, the download into a temp sandbox.</summary>
public sealed class WebToolsTests : IDisposable
{
    private const string Article = "<html><head><title>Example Domain</title></head><body><h1>Example Domain</h1><p>This domain is for use in illustrative examples in documents.</p></body></html>";

    private readonly StubHttpMessageHandler _http = new();
    private readonly FakeHeadlessBrowser _browser = new();
    private readonly ManualTimeProvider _time = new();
    private readonly AppSettingsData _settings = new();
    private readonly WebAccess _web;
    private readonly WebSearchTool _search;
    private readonly WebFetchTool _fetch;
    private readonly OpenUrlTool _open;
    private readonly DownloadFileTool _download;
    private readonly List<string> _opened = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly Files.WorkingDirectory _files;
    private Exception? _openFails;

    public WebToolsTests()
    {
        _web = new WebAccess(new HttpClient(_http), _browser, _time, (host, _) => Task.FromResult(host == "example.com" ? new[] { IPAddress.Parse("93.184.216.34") } : throw new System.Net.Sockets.SocketException(11001)), url => { if (_openFails is not null) throw _openFails; _opened.Add(url); });
        _search = new WebSearchTool(_web, () => _settings);
        _fetch = new WebFetchTool(_web, () => _settings);
        _open = new OpenUrlTool(_web);
        _root = Path.Combine(_dir, "files");
        _files = new Files.WorkingDirectory(() => _root, _time);
        _download = new DownloadFileTool(_web, _files, () => _settings);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static AIFunctionArguments Args(params (string Name, object? Value)[] pairs) =>
        new(pairs.ToDictionary(p => p.Name, p => p.Value));

    private static JsonElement Json(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    [Fact]
    public void Names_Schemas_AndDescriptions_ArePinned()
    {
        Assert.Equal("web_search", WebSearchTool.ToolName);
        Assert.Equal("web_fetch", WebFetchTool.ToolName);
        Assert.Equal("open_url", OpenUrlTool.ToolName);
        Assert.Equal(5, OpenUrlTool.MaxUrls);
        Assert.Equal("download_file", DownloadFileTool.ToolName);
        var all = App.ChatScreen.WebTools(_web, _files, () => _settings);
        Assert.Equal(new[] { WebSearchTool.ToolName, WebFetchTool.ToolName, OpenUrlTool.ToolName, DownloadFileTool.ToolName }, all.Select(t => t.Name));
        // The download tool is a file write: it leaves the list with the file tools off (2026-09-18), the list itself otherwise.
        Assert.Same(all, App.ChatScreen.WebToolsFor(all, filesEnabled: true));
        Assert.Equal(new[] { WebSearchTool.ToolName, WebFetchTool.ToolName, OpenUrlTool.ToolName }, App.ChatScreen.WebToolsFor(all, filesEnabled: false).Select(t => t.Name));
        var three = App.ChatScreen.WebToolsFor(all, filesEnabled: false);
        Assert.Same(three, App.ChatScreen.WebToolsFor(three, filesEnabled: false));
        Assert.Equal(1, WebSearchTool.MinResults);
        Assert.Equal(20, WebSearchTool.MaxResults);
        Assert.Equal(32_000, WebFetchTool.MaxChars);

        var search = _search.JsonSchema;
        Assert.Equal(new[] { "query", "max_results" }, search.GetProperty("properties").EnumerateObject().Select(p => p.Name));
        Assert.Equal("integer", search.GetProperty("properties").GetProperty("max_results").GetProperty("type").GetString());
        Assert.Equal(new[] { "query" }, search.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        Assert.Contains("web_fetch", _search.Description);

        var fetch = _fetch.JsonSchema;
        Assert.Equal(new[] { "url", "offset" }, fetch.GetProperty("properties").EnumerateObject().Select(p => p.Name));
        Assert.Equal(new[] { "url" }, fetch.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        Assert.Contains("32000 characters at a time", _fetch.Description);
        Assert.Contains("web_search", _fetch.Description);

        var open = _open.JsonSchema;
        Assert.Equal(new[] { "url", "urls" }, open.GetProperty("properties").EnumerateObject().Select(p => p.Name));
        Assert.Equal("array", open.GetProperty("properties").GetProperty("urls").GetProperty("type").GetString());
        Assert.False(open.TryGetProperty("required", out _));
        Assert.Equal("Opens a web page in the user's own browser, on their screen. Use it when they ask to open, show or see a link rather than to have it read out; one link in url, or up to 5 in urls.", _open.Description);

        var download = _download.JsonSchema;
        Assert.Equal(new[] { "url", "path", "overwrite" }, download.GetProperty("properties").EnumerateObject().Select(p => p.Name));
        Assert.Equal("boolean", download.GetProperty("properties").GetProperty("overwrite").GetProperty("type").GetString());
        Assert.Equal(new[] { "url" }, download.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(
            "Downloads a file from the web — a picture, a PDF, an archive, a data file, a page's source — and saves it under the working directory (the user's cwd / current directory), creating any missing folders; nothing is read or opened. " +
            "path is the file to write, or a folder to put it in under the file's own name; without it the file lands at the top under its own name. " +
            "A file that already exists is left alone unless overwrite is true. " +   // its .trash clause went with File safe edits (2026-10-01)
            "Up to 50 MB. To read a page use web_fetch; to look at a saved picture use view_image.",
            _download.Description);
        Assert.Equal(DownloadFileTool.DescriptionFor(50_000_000), _download.Description);
        // The size is the Web download max (MB) setting since 2026-10-01 (a constant 50 MB until then), the description with it.
        _settings.WebDownloadMaxMegabytes = 2000;
        Assert.EndsWith("Up to 2000 MB. To read a page use web_fetch; to look at a saved picture use view_image.", _download.Description, StringComparison.Ordinal);
        Assert.Equal(50, AppSettingsData.DefaultWebDownloadMaxMegabytes);
        Assert.Equal(2_000_000_000L, DownloadFileTool.MaxBytesOf(_settings));
        Assert.Equal(1_000_000L, DownloadFileTool.MaxBytesOf(new AppSettingsData { WebDownloadMaxMegabytes = 0 }));                 // a hand-edited value clamped
        Assert.Equal(102_400_000_000L, DownloadFileTool.MaxBytesOf(new AppSettingsData { WebDownloadMaxMegabytes = int.MaxValue }));
    }

    // ── open_url ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Open_OneLink_HandsItToTheBrowser_AndSaysSo()
    {
        Assert.Equal("Opened https://example.com/docs in your browser", await _open.InvokeAsync(Args(("url", "https://example.com/docs")), CancellationToken.None));
        Assert.Equal("Opened https://example.com/ in your browser", await _open.InvokeAsync(Args(("url", Json("\"example.com\""))), CancellationToken.None));
        Assert.Equal(new[] { "https://example.com/docs", "https://example.com/" }, _opened);
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task Open_SeveralLinks_IsAHeader_AndALineEach_BadOnesInPlace()
    {
        string answer = (string)(await _open.InvokeAsync(Args(("urls", Json("[\"https://a.example/\", \"ftp://b.example/\", \"c.example/x\"]"))), CancellationToken.None))!;

        Assert.Equal("Opened 2 links in your browser:\n- https://a.example/\nError: 'ftp://b.example/' is not an http or https URL\n- https://c.example/x", answer);
        Assert.Equal(new[] { "https://a.example/", "https://c.example/x" }, _opened);
    }

    [Fact]
    public async Task Open_UrlAndUrls_Combine_AndSixAreRefused_Unopened()
    {
        Assert.Equal("Opened 2 links in your browser:\n- https://a.example/\n- https://b.example/", await _open.InvokeAsync(Args(("url", "https://a.example/"), ("urls", Json("[\"https://b.example/\"]"))), CancellationToken.None));
        Assert.Equal(WebText.TooManyLinks, await _open.InvokeAsync(Args(("urls", Json("[\"https://1.example/\",\"https://2.example/\",\"https://3.example/\",\"https://4.example/\",\"https://5.example/\",\"https://6.example/\"]"))), CancellationToken.None));
        Assert.Equal(2, _opened.Count);
    }

    [Fact]
    public async Task Open_NoLink_BadList_AndAFailingShell()
    {
        Assert.Equal(WebText.NoUrl, await _open.InvokeAsync(new AIFunctionArguments(), CancellationToken.None));
        Assert.Equal(WebText.NoUrl, await _open.InvokeAsync(Args(("url", " ")), CancellationToken.None));
        Assert.Equal("Error: '3' is not a list of links for 'urls'", await _open.InvokeAsync(Args(("urls", Json("3"))), CancellationToken.None));
        Assert.Equal("Error: 'C:\\x.html' is not an http or https URL", await _open.InvokeAsync(Args(("url", @"C:\x.html")), CancellationToken.None));

        _openFails = new System.ComponentModel.Win32Exception(1155, "No application is associated");
        Assert.Equal("Error: could not open 'https://a.example/' in your browser: No application is associated", await _open.InvokeAsync(Args(("url", "https://a.example/")), CancellationToken.None));
        Assert.Equal("Error: none of the 2 links could be opened\nError: could not open 'https://a.example/' in your browser: No application is associated\nError: could not open 'https://b.example/' in your browser: No application is associated", await _open.InvokeAsync(Args(("urls", Json("[\"https://a.example/\",\"https://b.example/\"]"))), CancellationToken.None));
        Assert.Empty(_opened);
    }


    // ── web_search ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Search_NoArgument_TakesTheSettingsCount()
    {
        _http.Map("https://html.duckduckgo.com/html/", HttpStatusCode.OK, File.ReadAllText(DuckDuckGoSearchTests.FixturePath), "text/html");
        _settings.WebSearchMaxResults = 3;

        string answer = (string)(await _search.InvokeAsync(Args(("query", Json("\"rust async traits\""))), CancellationToken.None))!;

        Assert.StartsWith("Searched \"rust async traits\" (3 results, DuckDuckGo):\n1. ", answer);
        Assert.Equal(3, answer.Split('\n').Count(l => l.Length > 1 && char.IsDigit(l[0]) && l[1] == '.'));
        Assert.Contains(" — https://doc.rust-lang.org/book/ch17-05-traits-for-async.html\n   ", answer);
    }

    [Fact]
    public async Task Search_TheArgument_Overrides_UpToTheCap()
    {
        _http.Map("https://html.duckduckgo.com/html/", HttpStatusCode.OK, File.ReadAllText(DuckDuckGoSearchTests.FixturePath), "text/html");

        Assert.StartsWith("Searched \"x\" (2 results, DuckDuckGo):", (string)(await _search.InvokeAsync(Args(("query", "x"), ("max_results", Json("2"))), CancellationToken.None))!);
        Assert.Equal(WebText.BadResultCount, await _search.InvokeAsync(Args(("query", "x"), ("max_results", Json("25"))), CancellationToken.None));
        Assert.Equal(WebText.BadResultCount, await _search.InvokeAsync(Args(("query", "x"), ("max_results", 0)), CancellationToken.None));
        Assert.Equal("Error: 'lots' is not a whole number for 'max_results'", await _search.InvokeAsync(Args(("query", "x"), ("max_results", Json("\"lots\""))), CancellationToken.None));
    }

    [Fact]
    public async Task Search_AHandEditedCount_IsClamped()
    {
        _settings.WebSearchMaxResults = 99;
        Assert.Equal(20, WebSearchTool.DefaultCount(_settings));
        _settings.WebSearchMaxResults = 0;
        Assert.Equal(1, WebSearchTool.DefaultCount(_settings));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Search_NoQuery_IsTheSentence()
    {
        Assert.Equal(WebText.NoQuery, await _search.InvokeAsync(new AIFunctionArguments(), CancellationToken.None));
        Assert.Equal(WebText.NoQuery, await _search.InvokeAsync(Args(("query", "  ")), CancellationToken.None));
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task Search_TheMethodPicksTheEngine_AndTheUrlIsReadOnlyUnderSearxng()
    {
        _http.Map("http://localhost:8080/search", HttpStatusCode.OK, "{\"results\":[{\"url\":\"https://a/\",\"title\":\"A\",\"content\":\"one\"}]}");
        _settings.WebSearxngUrl = "http://localhost:8080";

        // The URL alone no longer switches the engine (2026-09-15): duckduckgo ignores it.
        Assert.Same(_web.DuckDuckGo, _web.Engine(_settings));

        _settings.WebSearchMethod = "searxng";
        Assert.Equal("Searched \"x\" (1 result, SearXNG):\n1. A — https://a/\n   one", await _search.InvokeAsync(Args(("query", "x")), CancellationToken.None));

        // searxng with no usable URL falls back to DuckDuckGo quietly; the URL keeps its value across a switch back.
        _settings.WebSearxngUrl = "not a url";
        Assert.Same(_web.DuckDuckGo, _web.Engine(_settings));
        _settings.WebSearxngUrl = "";
        Assert.Same(_web.DuckDuckGo, _web.Engine(_settings));
        _settings.WebSearxngUrl = "http://localhost:8080";
        Assert.IsType<SearxngSearch>(_web.Engine(_settings));
        _settings.WebSearchMethod = "duckduckgo";
        Assert.Same(_web.DuckDuckGo, _web.Engine(_settings));
        Assert.Equal("http://localhost:8080", _settings.WebSearxngUrl);
        // A hand-edited method reads as the default.
        _settings.WebSearchMethod = "google";
        Assert.Same(_web.DuckDuckGo, _web.Engine(_settings));
    }

    [Fact]
    public async Task Search_AnErrorSentence_IsTheAnswer()
    {
        _http.Map("https://html.duckduckgo.com/html/", HttpStatusCode.TooManyRequests, "<p>no</p>", "text/html");
        _settings.WebBrowserMode = "httpclient";

        Assert.Equal("Error: DuckDuckGo refused the search (HTTP 429 Too Many Requests); try again in a minute", await _search.InvokeAsync(Args(("query", "x")), CancellationToken.None));
    }

    // ── web_fetch ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Fetch_APage_IsTheHeader_AndTheMarkdown()
    {
        _http.Map("https://example.com/", HttpStatusCode.OK, Article, "text/html");

        string answer = (string)(await _fetch.InvokeAsync(Args(("url", "https://example.com/")), CancellationToken.None))!;

        Assert.Equal("Example Domain — https://example.com/ (chars 1–79 of 79, http)\n\n# Example Domain\n\nThis domain is for use in illustrative examples in documents.", answer);
    }

    [Fact]
    public async Task Fetch_ABareHost_GetsHttps_AndABadUrl_TheSentence()
    {
        _http.Map("https://example.com/", HttpStatusCode.OK, Article, "text/html");

        Assert.StartsWith("Example Domain — https://example.com/ (", (string)(await _fetch.InvokeAsync(Args(("url", "example.com")), CancellationToken.None))!);
        Assert.Equal(WebText.NoUrl, await _fetch.InvokeAsync(new AIFunctionArguments(), CancellationToken.None));
        Assert.Equal("Error: 'ftp://x/' is not an http or https URL", await _fetch.InvokeAsync(Args(("url", " ftp://x/ ")), CancellationToken.None));
        Assert.Equal(@"Error: 'C:\notes.txt' is not an http or https URL", await _fetch.InvokeAsync(Args(("url", @"C:\notes.txt")), CancellationToken.None));
    }

    [Fact]
    public async Task Fetch_Pages_ThroughTheCache_WithoutASecondDownload()
    {
        string longText = string.Join(" ", Enumerable.Range(0, 9000).Select(i => "w" + i));   // ~ 50k chars
        _http.Map("https://example.com/long", HttpStatusCode.OK, "<title>Long</title><p>" + longText + "</p>", "text/html");

        string first = (string)(await _fetch.InvokeAsync(Args(("url", "https://example.com/long")), CancellationToken.None))!;
        string header = first.Split('\n')[0];
        Assert.StartsWith("Long — https://example.com/long (chars 1–32,000 of ", header);
        Assert.EndsWith("; continue with offset 32000, http)", header);
        Assert.Equal(WebFetchTool.MaxChars, first.Length - header.Length - 2);

        string second = (string)(await _fetch.InvokeAsync(Args(("url", "https://example.com/long"), ("offset", Json("32000"))), CancellationToken.None))!;
        Assert.StartsWith("Long — https://example.com/long (chars 32,001–", second.Split('\n')[0]);
        Assert.EndsWith(", http)", second.Split('\n')[0]);
        Assert.DoesNotContain("continue with offset", second);
        Assert.Equal(longText[32000..], second[(second.IndexOf("\n\n", StringComparison.Ordinal) + 2)..]);
        Assert.Single(_http.Requests);

        Assert.Equal("Error: offset 90000 is past the end of the page (" + longText.Length.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " characters)", await _fetch.InvokeAsync(Args(("url", "https://example.com/long"), ("offset", 90000)), CancellationToken.None));
        Assert.Equal(WebText.NegativeOffset, await _fetch.InvokeAsync(Args(("url", "https://example.com/long"), ("offset", -1)), CancellationToken.None));
        Assert.Equal("Error: 'x' is not a whole number for 'offset'", await _fetch.InvokeAsync(Args(("url", "https://example.com/long"), ("offset", "x")), CancellationToken.None));
        Assert.Single(_http.Requests);
    }

    [Fact]
    public async Task Fetch_Text_ComesBackVerbatim_Untitled()
    {
        _http.Map("https://example.com/a.json", HttpStatusCode.OK, "{\"a\":1}", "application/json");

        Assert.Equal("(untitled) — https://example.com/a.json (chars 1–7 of 7, http)\n\n{\"a\":1}", await _fetch.InvokeAsync(Args(("url", "https://example.com/a.json")), CancellationToken.None));
    }

    [Fact]
    public async Task Fetch_AnEmptyPage_IsTheHeaderAlone()
    {
        _http.Map("https://example.com/empty", HttpStatusCode.OK, "<title>Blank</title><script>x()</script>", "text/html");

        Assert.Equal("Blank — https://example.com/empty (no readable text, http)", await _fetch.InvokeAsync(Args(("url", "https://example.com/empty")), CancellationToken.None));
    }

    [Fact]
    public async Task Fetch_ReadsTheSettings_PerCall()
    {
        _http.Map("https://example.com/", HttpStatusCode.Forbidden, "<p>no</p>", "text/html");
        _browser.Html = Article;

        _settings.WebBrowserMode = "httpclient";
        Assert.Equal("Error: 'https://example.com/' refused the request (HTTP 403 Forbidden)", await _fetch.InvokeAsync(Args(("url", "https://example.com/")), CancellationToken.None));
        Assert.Empty(_browser.Runs);

        _settings.WebBrowserMode = "default";
        Assert.StartsWith("Example Domain — https://example.com/ (chars 1–79 of 79, headless browser)", (string)(await _fetch.InvokeAsync(Args(("url", "https://example.com/")), CancellationToken.None))!);
        Assert.Single(_browser.Runs);

        _settings.WebBrowserPath = @"D:\tools\chrome.exe";
        _settings.WebBrowserMode = "chromium";
        await _fetch.InvokeAsync(Args(("url", "https://example.com/other")), CancellationToken.None);
        Assert.Equal(@"D:\tools\chrome.exe", _browser.LocateCalls[^1]);

        _settings.WebBrowserMode = "httpclient";
        Assert.Equal(WebText.LanRefused("10.0.0.5"), await _fetch.InvokeAsync(Args(("url", "http://10.0.0.5/")), CancellationToken.None));
        _settings.WebBrowserNetworkMode = "both";
        _http.Map("http://10.0.0.5/", HttpStatusCode.OK, "<p>router</p>", "text/html");
        Assert.Equal("(untitled) — http://10.0.0.5/ (chars 1–6 of 6, http)\n\nrouter", await _fetch.InvokeAsync(Args(("url", "http://10.0.0.5/")), CancellationToken.None));
    }

    [Fact]
    public async Task Fetch_APublicHost_IsRefused_UnderLocalAreaNetwork()
    {
        // 2026-09-18: the third mode; the sentence names the host and the mode, the request is never made.
        _settings.WebBrowserMode = "httpclient";
        _settings.WebBrowserNetworkMode = "local_area_network";
        _http.Map("https://example.com/", HttpStatusCode.OK, "<p>hi</p>", "text/html");

        Assert.Equal(WebText.InternetRefused("example.com"), await _fetch.InvokeAsync(Args(("url", "https://example.com/")), CancellationToken.None));
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task Fetch_AFailedFetch_IsNotCached()
    {
        _http.Map("https://example.com/flaky", HttpStatusCode.ServiceUnavailable, "<p>later</p>", "text/html");
        _settings.WebBrowserMode = "httpclient";

        Assert.StartsWith("Error: 'https://example.com/flaky' refused the request", (string)(await _fetch.InvokeAsync(Args(("url", "https://example.com/flaky")), CancellationToken.None))!);
        Assert.Equal(0, _web.Pages.Count);
        await _fetch.InvokeAsync(Args(("url", "https://example.com/flaky")), CancellationToken.None);
        Assert.Equal(2, _http.Requests.Count);
    }

    [Fact]
    public void Options_ComeFromTheSettings()
    {
        var options = WebAccess.Options(new AppSettingsData { WebBrowserMode = "chromium", WebBrowserPath = @"C:\x.exe", WebBrowserNetworkMode = "both" });

        Assert.Equal(new FetchOptions(FetchEngine.Chromium, @"C:\x.exe", NetworkReach.Both), options);
        Assert.Equal(new FetchOptions(FetchEngine.Default, "", NetworkReach.Internet), WebAccess.Options(new AppSettingsData()));
        Assert.Equal(NetworkReach.LocalAreaNetwork, WebAccess.Options(new AppSettingsData { WebBrowserNetworkMode = "local_area_network" }).Reach);
    }

    // ── download_file ───────────────────────────────────────────────────────

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4, 5, 6, 7, 8];

    private byte[] Saved(string relative) => File.ReadAllBytes(Path.Combine(_root, relative));

    [Fact]
    public async Task Download_SavesTheBodyAsItCame_TheSentencePinned()
    {
        // 2026-09-18: any media type, the bytes whole, the sentence naming the path, the size, the type and the URL; a picture says view_image.
        _http.Map("https://example.com/cat.png", (_, _) => Task.FromResult(StubHttpMessageHandler.Bytes(HttpStatusCode.OK, Png, "image/png")));

        string result = (string)(await _download.InvokeAsync(Args(("url", "https://example.com/cat.png")), CancellationToken.None))!;

        Assert.Equal("downloaded cat.png (16 B, image/png) from https://example.com/cat.png; view_image shows it", result);
        Assert.Equal(Png, Saved("cat.png"));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public async Task Download_APath_IsTheFile_AFolder_TakesTheFilesName_WithTheHeadersNameFirst()
    {
        _http.Map("https://example.com/data.bin", (_, _) => Task.FromResult(StubHttpMessageHandler.Bytes(HttpStatusCode.OK, [1, 2, 3], "application/octet-stream")));
        _http.Map("https://example.com/export?id=7", (_, _) =>
        {
            var response = StubHttpMessageHandler.Bytes(HttpStatusCode.OK, [9, 9], "text/csv");
            response.Content.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment") { FileName = "\"..\\evil\\report (Q3).csv\"" };
            return Task.FromResult(response);
        });
        Directory.CreateDirectory(Path.Combine(_root, "in"));

        Assert.Equal(@"downloaded out\blob.bin (3 B, application/octet-stream) from https://example.com/data.bin", await _download.DownloadAsync("https://example.com/data.bin", "out/blob.bin", false, CancellationToken.None));
        Assert.Equal(@"downloaded in\data.bin (3 B, application/octet-stream) from https://example.com/data.bin", await _download.DownloadAsync("https://example.com/data.bin", "in", false, CancellationToken.None));
        Assert.Equal(@"downloaded new\data.bin (3 B, application/octet-stream) from https://example.com/data.bin", await _download.DownloadAsync("https://example.com/data.bin", "new/", false, CancellationToken.None));
        // The header's name wins over the URL's last segment, its path and the quotes stripped, the query never part of a name.
        Assert.Equal("downloaded report (Q3).csv (2 B, text/csv) from https://example.com/export?id=7", await _download.DownloadAsync("https://example.com/export?id=7", null, false, CancellationToken.None));
        Assert.Equal([9, 9], Saved("report (Q3).csv"));
    }

    [Fact]
    public async Task Download_AUrlNamingNoFile_NeedsAPath_AndTheSandboxJudgesIt()
    {
        _http.Map("https://example.com/", (_, _) => Task.FromResult(StubHttpMessageHandler.Bytes(HttpStatusCode.OK, [1], "text/html")));

        Assert.Equal("Error: 'https://example.com/' names no file; give a path to save it as", await _download.DownloadAsync("https://example.com/", null, false, CancellationToken.None));
        Assert.Equal("downloaded home.html (1 B, text/html) from https://example.com/", await _download.DownloadAsync("https://example.com/", "home.html", false, CancellationToken.None));
        Assert.StartsWith("Error: '../x.html' is outside the working directory", await _download.DownloadAsync("https://example.com/", "../x.html", false, CancellationToken.None));
        Assert.Equal(WebText.NoDownloadUrl, await _download.DownloadAsync("  ", null, false, CancellationToken.None));
        Assert.Equal(WebText.NotHttp("ftp://x/y"), await _download.DownloadAsync("ftp://x/y", null, false, CancellationToken.None));
        Assert.Equal("Error: 'maybe' is not true or false for 'overwrite'", await _download.InvokeAsync(Args(("url", "https://example.com/"), ("overwrite", "maybe")), CancellationToken.None));
    }

    [Fact]
    public async Task Download_AnExistingFile_NeedsOverwrite_ThenIsReplacedInPlace()
    {
        _http.Map("https://example.com/cat.png", (_, _) => Task.FromResult(StubHttpMessageHandler.Bytes(HttpStatusCode.OK, Png, "image/png")));
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(Path.Combine(_root, "cat.png"), [0]);

        Assert.Equal("Error: 'cat.png' already exists; call again with overwrite true to replace it", await _download.DownloadAsync("https://example.com/cat.png", null, false, CancellationToken.None));
        Assert.Equal("replaced cat.png (16 B, image/png) from https://example.com/cat.png; view_image shows it", await _download.DownloadAsync("https://example.com/cat.png", null, true, CancellationToken.None));
        Assert.Equal(Png, Saved("cat.png"));
        Assert.False(Directory.Exists(Path.Combine(_root, ".trash")));   // nothing kept (File safe edits went 2026-10-01)
    }

    [Fact]
    public async Task Download_TheCap_IsTheSetting_TheBodyStreamedToDisk_AndAnOversizeLeavesNothing()
    {
        // 2026-10-01, the user's ask: the cap is Web download max (MB); the body goes to a temporary sibling, never held whole.
        _settings.WebDownloadMaxMegabytes = 1;
        _http.Map("https://example.com/big.bin", (_, _) => Task.FromResult(TrickleStream.Response(new TrickleStream(new byte[1_500_000]), "application/octet-stream")));
        _http.Map("https://example.com/declared.bin", (_, _) => Task.FromResult(StubHttpMessageHandler.Bytes(HttpStatusCode.OK, new byte[1_600_000], "application/octet-stream")));

        // No length declared: the body outgrows the cap mid-stream, and what was written goes.
        Assert.Equal("Error: 'https://example.com/big.bin' is over the 1 MB download limit", await _download.DownloadAsync("https://example.com/big.bin", null, false, CancellationToken.None));
        Assert.Empty(Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
        // A length over it: refused before a file is begun.
        Assert.Equal("Error: 'https://example.com/declared.bin' is 1.6 MB, over the 1 MB download limit", await _download.DownloadAsync("https://example.com/declared.bin", null, false, CancellationToken.None));
        Assert.Empty(Directory.GetFiles(_root, "*", SearchOption.AllDirectories));

        // Raised, both come whole, past the old in-memory read's chunk.
        _settings.WebDownloadMaxMegabytes = 2;
        Assert.Equal("downloaded big.bin (1.5 MB, application/octet-stream) from https://example.com/big.bin", await _download.DownloadAsync("https://example.com/big.bin", null, false, CancellationToken.None));
        Assert.Equal(1_500_000, new FileInfo(Path.Combine(_root, "big.bin")).Length);
        Assert.Equal("downloaded declared.bin (1.6 MB, application/octet-stream) from https://example.com/declared.bin", await _download.DownloadAsync("https://example.com/declared.bin", null, false, CancellationToken.None));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public async Task Download_ACancelMidBody_Escapes_AndDeletesThePartialFile()
    {
        var firstRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _http.Map("https://example.com/slow.bin", (_, _) => Task.FromResult(TrickleStream.Response(new TrickleStream(new byte[100_000], hang: true, firstRead), "application/octet-stream")));
        using var cts = new CancellationTokenSource();

        var download = _download.DownloadAsync("https://example.com/slow.bin", null, false, cts.Token);
        await firstRead.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await Task.Delay(50);
        Assert.NotEmpty(Directory.GetFiles(_root, "*.tmp"));   // the partial file, beside where the file would go
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
        Assert.Empty(Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Download_AFailedFetch_IsItsSentence_AndNothingIsWritten()
    {
        _settings.WebBrowserNetworkMode = "local_area_network";
        _http.Map("https://example.com/cat.png", (_, _) => Task.FromResult(StubHttpMessageHandler.Bytes(HttpStatusCode.OK, Png, "image/png")));

        Assert.Equal(WebText.InternetRefused("example.com"), await _download.DownloadAsync("https://example.com/cat.png", null, false, CancellationToken.None));
        Assert.Empty(_http.Requests);
        Assert.False(Directory.Exists(_root));

        _settings.WebBrowserNetworkMode = "internet";
        _http.Map("https://example.com/gone.png", HttpStatusCode.NotFound, "no", "text/plain");
        Assert.Equal("Error: could not fetch 'https://example.com/gone.png': HTTP 404 Not Found", await _download.DownloadAsync("https://example.com/gone.png", null, false, CancellationToken.None));
        Assert.False(Directory.Exists(_root));
    }
}
