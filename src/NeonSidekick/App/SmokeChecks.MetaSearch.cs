using System.Globalization;
using System.Net;
using System.Text;
using NeonSidekick.Settings;
using NeonSidekick.Web;

namespace NeonSidekick.App;

public static partial class SmokeChecks
{
    private static readonly string[] SmokeEngineUrls =
        ["https://bing.example/", "https://brave.example/", "https://duckduckgo.example/", "https://startpage.example/", "https://en.wikipedia.org/wiki/Rust"];

    /// <summary><c>web:metasearch</c>: the real adapter and all five engines on the published binary, over a stub with no network.</summary>
    public static SmokeCheck ProbeMetaSearch()
    {
        const string name = "web:metasearch";
        try
        {
            using var http = new HttpClient(new SmokeEngineHandler());
            var outcome = new BuiltInMetaSearch(http, TimeProvider.System).For(new AppSettingsData())
                .SearchAsync("Rust", 8, new FetchOptions(FetchEngine.HttpClient, "", NetworkReach.Internet), CancellationToken.None).GetAwaiter().GetResult();
            var missing = SmokeEngineUrls.Except(outcome.Results.Select(r => r.Url), StringComparer.Ordinal).ToList();
            bool ok = outcome.Ok && missing.Count == 0;
            return new SmokeCheck(name, ok, ok ? outcome.Results.Count.ToString(CultureInfo.InvariantCulture) + " results" : outcome.Error + " missing: " + string.Join(", ", missing));
        }
        catch (Exception ex)
        {
            return new SmokeCheck(name, false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private sealed class SmokeEngineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.RequestUri!.Host switch
            {
                "www.bing.com" => "<html><body><ol id=\"b_results\"><li class=\"b_algo\"><h2><a href=\"https://bing.example/\">From Bing</a></h2><p>A snippet</p></li></ol></body></html>",
                "en.wikipedia.org" => "{\"title\":\"Rust\",\"content_urls\":{\"desktop\":{\"page\":\"https://en.wikipedia.org/wiki/Rust\"}},\"description\":\"Iron oxide\"}",
                "search.brave.com" => "<script>data: [null,(function(a){return {data:{body:{response:{web:{results:[{url:\"https://brave.example/\",title:a,description:\"<b>d</b>\"}]}}}}}}(\"From Brave\"))];</script>",
                "www.startpage.com" => request.Method == HttpMethod.Get
                    ? "<form id=\"search\"><input name=\"sc\" value=\"CODE\"></form>"
                    : "<script>React.createElement(UIStartpage.AppSerpWeb, {\"render\":{\"presenter\":{\"regions\":{\"mainline\":[{\"display_type\":\"web-google\",\"results\":[{\"clickUrl\":\"https://startpage.example/\",\"title\":\"From Startpage\",\"description\":\"d\"}]}]}}}})</script>",
                "links.duckduckgo.com" => "{\"results\":[{\"u\":\"https://duckduckgo.example/\",\"t\":\"From <b>DDG</b>\",\"a\":\"a\"}]}",
                _ => "<html><head><link id=\"deep_preload_link\" href=\"https://links.duckduckgo.com/d.js?q=Rust\"></head></html>",
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8), RequestMessage = request });
        }
    }
}
