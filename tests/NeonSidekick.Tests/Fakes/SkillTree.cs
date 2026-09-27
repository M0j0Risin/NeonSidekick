using System.Net;
using System.Text;
using NeonSidekick.Skills;

namespace NeonSidekick.Tests.Fakes;

/// <summary>
/// A GitHub repository as the API and raw.githubusercontent.com serve it, mapped onto a
/// <see cref="StubHttpMessageHandler"/> for the <c>/skills add</c> tests: the commit a ref names (the
/// SHA alone, and only under <see cref="SkillHub.ShaMediaType"/>, as the flow must ask), the commit's
/// tree, and each file's bytes at the commit (404 for anything else).
/// </summary>
public static class SkillTree
{
    /// <summary>Maps <paramref name="repo"/> (<c>owner/repo</c>) with <paramref name="files"/>; <paramref name="sizes"/> overrides a file's declared size.</summary>
    public static void Map(StubHttpMessageHandler http, string repo, IEnumerable<(string Path, string Content)> files, IEnumerable<string>? links = null,
        string commit = SkillZip.Commit, bool truncated = false, IReadOnlyDictionary<string, long>? sizes = null)
    {
        var contents = files.ToDictionary(f => f.Path, f => Encoding.UTF8.GetBytes(f.Content), StringComparer.Ordinal);
        http.Map(SkillSource.ApiBase + "repos/" + repo + "/commits/", (request, _) => Task.FromResult(
            request.Headers.Accept.ToString() == SkillHub.ShaMediaType
                ? StubHttpMessageHandler.Json(HttpStatusCode.OK, commit, SkillHub.ShaMediaType)
                : StubHttpMessageHandler.Json(HttpStatusCode.UnsupportedMediaType, "{}")));
        http.Map(SkillSource.ApiBase + "repos/" + repo + "/git/trees/" + commit + "?recursive=1", HttpStatusCode.OK, TreeJson(contents, links ?? [], truncated, sizes));
        string raw = SkillSource.RawBase + repo + "/" + commit + "/";
        http.Map(raw, (request, _) =>
        {
            string path = Uri.UnescapeDataString(request.RequestUri!.AbsoluteUri[raw.Length..]);
            return Task.FromResult(contents.TryGetValue(path, out var bytes)
                ? StubHttpMessageHandler.Bytes(HttpStatusCode.OK, bytes, "text/plain")
                : StubHttpMessageHandler.Json(HttpStatusCode.NotFound, "404: Not Found", "text/plain"));
        });
    }

    private static string TreeJson(Dictionary<string, byte[]> contents, IEnumerable<string> links, bool truncated, IReadOnlyDictionary<string, long>? sizes)
    {
        var folders = contents.Keys.Concat(links).SelectMany(p =>
        {
            string[] parts = p.Split('/');
            return Enumerable.Range(1, parts.Length - 1).Select(i => string.Join('/', parts[..i]));
        }).Distinct();
        var entries = folders.Select(f => $"{{\"path\":\"{f}\",\"mode\":\"040000\",\"type\":\"tree\",\"sha\":\"x\"}}")
            .Concat(contents.Select(c => $"{{\"path\":\"{c.Key}\",\"mode\":\"100644\",\"type\":\"blob\",\"sha\":\"x\",\"size\":{(sizes is not null && sizes.TryGetValue(c.Key, out long size) ? size : c.Value.Length)}}}"))
            .Concat(links.Select(l => $"{{\"path\":\"{l}\",\"mode\":\"120000\",\"type\":\"blob\",\"sha\":\"x\",\"size\":16}}"))
            .Append("{\"path\":\"vendor/lib\",\"mode\":\"160000\",\"type\":\"commit\",\"sha\":\"x\"}");
        return "{\"sha\":\"t\",\"url\":\"u\",\"tree\":[" + string.Join(",", entries) + "],\"truncated\":" + (truncated ? "true" : "false") + "}";
    }
}
