using System.Net;
using NeonSidekick.Skills;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.Web;

namespace NeonSidekick.Tests;

public class SkillInstallFlowTests : IDisposable
{
    private const string Codeload = "https://codeload.github.com/";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly StubHttpMessageHandler _http = new();
    private readonly FakeHost _host;

    public SkillInstallFlowTests()
    {
        _host = new FakeHost(new SkillRoots(Path.Combine(_dir, "profile", "skills"), Path.Combine(_dir, "skills"), Path.Combine(_dir, ".agents", "skills")));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private SkillInstallFlow Flow() =>
        new(new SkillHub(new WebFetcher(new HttpClient(_http), new FakeHeadlessBrowser(), (_, _) => Task.FromResult(new[] { IPAddress.Parse("140.82.112.9") }))));

    private void Search(string json) => _http.Map(SkillHub.SearchUrl, HttpStatusCode.OK, json);

    private void Archive(string repo, byte[] zip) =>
        _http.Map(Codeload + repo + "/zip/", (_, _) => Task.FromResult(StubHttpMessageHandler.Bytes(HttpStatusCode.OK, zip, "application/zip")));

    private static string Hits(params (string Id, long Installs)[] hits) =>
        "{\"query\":\"q\",\"searchType\":\"fuzzy\",\"skills\":[" + string.Join(",", hits.Select(h =>
        {
            string[] parts = h.Id.Split('/');
            return $"{{\"id\":\"{h.Id}\",\"source\":\"{parts[0]}/{parts[1]}\",\"skillId\":\"{parts[2]}\",\"name\":\"{parts[2]}\",\"installs\":{h.Installs}}}";
        })) + "],\"count\":" + hits.Length + "}";

    private Task<bool> Run(string args) => Flow().RunAsync(args, _host, CancellationToken.None);

    private const string Api = "https://api.github.com/repos/";
    private const string Raw = "https://raw.githubusercontent.com/";

    private List<string> Urls() => _http.Requests.Select(r => r.Uri.AbsoluteUri).ToList();

    [Fact]
    public async Task OneHit_ListsTheRepository_FetchesOnlyThatSkill_AndInstalls()
    {
        Search(Hits(("anthropics/skills/pdf", 201532)));
        SkillTree.Map(_http, "anthropics/skills", SkillZip.RepoFiles("pdf", "docx"));
        _host.Answer = SkillScope.Global;

        Assert.True(await Run("pdf document"));

        // Later on 2026-09-26: the commit, its listing, the named skill's SKILL.md, then — after the yes — its other file. No zip, nothing of docx.
        string at = Raw + "anthropics/skills/" + SkillZip.Commit + "/skills/pdf/";
        Assert.Equal(
            [SkillHub.SearchUrl + "?q=pdf%20document&limit=25", Api + "anthropics/skills/commits/HEAD", Api + "anthropics/skills/git/trees/" + SkillZip.Commit + "?recursive=1", at + "SKILL.md", at + "scripts/run.py"],
            Urls());
        Assert.Equal(
            [SkillInstallText.SearchingLabel("pdf document"), SkillInstallText.DownloadingLabel("anthropics/skills"), SkillInstallText.ReadingSkillsLabel("anthropics/skills"), SkillInstallText.FetchingSkillLabel("pdf")],
            _host.Spins);
        Assert.StartsWith("**pdf** — Does a thing.", _host.Previews.Single(), StringComparison.Ordinal);
        Assert.Equal(SkillInstallOption.New, _host.Asked.Single().Option);
        string folder = Path.Combine(Path.GetFullPath(_host.Roots.Global), "pdf");
        Assert.Equal(SkillInstallText.InstalledNotice("pdf", SkillScope.Global, folder), _host.Notices.Single());
        Assert.Equal("print('hi')\n", File.ReadAllText(Path.Combine(folder, "scripts", "run.py")));
        var sidecar = SkillProvenance.Read(folder)!;
        Assert.Equal("anthropics/skills/pdf", sidecar.SkillsShId);
        Assert.Equal((SkillZip.Commit, "skills/pdf", "https://github.com/anthropics/skills/tree/" + SkillZip.Commit + "/skills/pdf"), (sidecar.Commit, sidecar.Path, sidecar.Url));
        Assert.Equal(1, _host.Rescans);
        Assert.Empty(_host.Errors);
    }

    [Fact]
    public async Task AListedRepository_IsPickedFromItsSkillMds_AndACancelFetchesNothingMore()
    {
        SkillTree.Map(_http, "anthropics/skills", SkillZip.RepoFiles("pdf", "docx"), links: ["skills/pdf/link"]);
        _host.Picks.Enqueue(1);
        _host.Answer = null;

        Assert.False(await Run("anthropics/skills"));

        Assert.Equal(["docx  skills/docx", "pdf   skills/pdf"], _host.PickRows.Single());
        Assert.Contains("link (a symbolic link, left out)", _host.Previews.Single(), StringComparison.Ordinal);
        Assert.Equal(SkillInstallText.KeptNotice("pdf"), _host.Notices.Single());
        Assert.DoesNotContain(Urls(), u => u.EndsWith(".py", StringComparison.Ordinal) || u.StartsWith(SkillSource.CodeloadBase, StringComparison.Ordinal));
        Assert.Equal(2, Urls().Count(u => u.EndsWith("/SKILL.md", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task TheListing_FallsBackToTheZip_WhenTheApiSaysNo_OrCutsItShort()
    {
        _http.Map(Api + "limited/r/", HttpStatusCode.Forbidden, "{\"message\":\"API rate limit exceeded\"}");
        Archive("limited/r", SkillZip.Repo("pdf"));
        _host.Answer = SkillScope.Profile;
        Assert.True(await Run("limited/r"));
        Assert.Equal(SkillProvenance.GitHubKind, SkillProvenance.Read(Path.Combine(_host.Roots.Profile, "pdf"))!.Source);
        Assert.Equal("https://codeload.github.com/limited/r/zip/HEAD", SkillProvenance.Read(Path.Combine(_host.Roots.Profile, "pdf"))!.Url);

        SkillTree.Map(_http, "big/r", SkillZip.RepoFiles("docx"), truncated: true);
        Archive("big/r", SkillZip.Repo("docx"));
        Assert.True(await Run("big/r"));
        Assert.Contains(SkillSource.CodeloadBase + "big/r/zip/HEAD", Urls());
        Assert.DoesNotContain(Urls(), u => u.StartsWith(Raw + "big/", StringComparison.Ordinal));
        Assert.Empty(_host.Errors);
    }

    [Fact]
    public async Task AListedFile_LongerThanTheListingSaid_WritesNothing()
    {
        SkillTree.Map(_http, "o/lying", SkillZip.RepoFiles("pdf"), sizes: new Dictionary<string, long> { ["skills/pdf/scripts/run.py"] = 3 });
        _host.Answer = SkillScope.Profile;

        Assert.False(await Run("o/lying"));
        Assert.Equal(SkillInstallText.FileTooBigRefusal("scripts/run.py", 3, SkillArchive.MaxSkillFileBytes), _host.Errors[^1]);
        Assert.False(Directory.Exists(Path.Combine(_host.Roots.Profile, "pdf")));
    }

    [Fact]
    public async Task ARawFailure_IsAnError_AndWritesNothing()
    {
        // A raw host that answers the SKILL.md but not the script: mapped ahead of the tree's own route, so it wins.
        _http.Map(Raw + "o/r/" + SkillZip.Commit + "/skills/pdf/scripts/", HttpStatusCode.TooManyRequests, "slow down", "text/plain");
        SkillTree.Map(_http, "o/r", SkillZip.RepoFiles("pdf"));
        _host.Answer = SkillScope.Profile;

        Assert.False(await Run("o/r"));

        Assert.StartsWith(SkillInstallText.FailedError("pdf", ""), _host.Errors.Single(), StringComparison.Ordinal);
        Assert.Contains("429", _host.Errors.Single(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(_host.Roots.Profile, "pdf")));
        Assert.DoesNotContain(Urls(), u => u.StartsWith(SkillSource.CodeloadBase, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AListingOfTooManySkills_IsAnError_UnlessOneIsNamed()
    {
        var names = Enumerable.Range(0, SkillArchive.MaxListedSkills + 1).Select(i => "s" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        SkillTree.Map(_http, "o/many", SkillZip.RepoFiles(names));
        _host.Answer = SkillScope.Profile;

        Assert.False(await Run("o/many"));
        Assert.Equal(SkillInstallText.TooManySkillsError(names.Length, SkillArchive.MaxListedSkills), _host.Errors.Single());
        Assert.DoesNotContain(Urls(), u => u.StartsWith(Raw, StringComparison.Ordinal));

        Assert.True(await Run("o/many/s7"));
        Assert.Single(Urls(), u => u.EndsWith("/SKILL.md", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SeveralHits_ArePicked_AndACancelledPickInstallsNothing()
    {
        Search(Hits(("anthropics/skills/pdf", 5), ("openai/skills/pdf", 1)));
        Archive("openai/skills", SkillZip.Repo("pdf"));
        _host.Picks.Enqueue(1);
        _host.Answer = SkillScope.Profile;

        Assert.True(await Run("pdf"));
        Assert.Equal(SkillInstallText.PickHitTitle, _host.PickTitles.Single());
        Assert.Equal(["pdf  anthropics/skills  5 installs", "pdf  openai/skills      1 install"], _host.PickRows[0]);
        Assert.True(File.Exists(Path.Combine(_host.Roots.Profile, "pdf", SkillCatalog.FileName)));

        _host.Picks.Enqueue(null);
        Assert.False(await Run("pdf"));
        Assert.Equal(SkillInstallText.CancelledNotice, _host.Notices[^1]);
    }

    [Fact]
    public async Task AReposSeveralSkills_ArePicked_AndANamedOneIsMatched()
    {
        Archive("anthropics/skills", SkillZip.Repo("pdf", "docx"));
        _host.Picks.Enqueue(0);
        _host.Answer = null;

        Assert.False(await Run("anthropics/skills"));
        Assert.Equal(SkillInstallText.PickSkillTitle, _host.PickTitles.Single());
        Assert.Equal(SkillInstallText.KeptNotice("docx"), _host.Notices.Single());
        Assert.False(Directory.Exists(_host.Roots.Profile));

        Assert.False(await Run("anthropics/skills/xlsx"));
        Assert.Equal(SkillInstallText.NoSuchSkillError("anthropics/skills/xlsx", ["docx", "pdf"]), _host.Errors.Single());
    }

    [Fact]
    public async Task Headless_ListsHits_AndWritesNothingWithoutYes()
    {
        _host.Headless = true;
        Search(Hits(("anthropics/skills/pdf", 5), ("openai/skills/pdf", 1)));
        Assert.False(await Run("pdf"));
        Assert.Equal([SkillInstallText.HeadlessPickHint, "  anthropics/skills/pdf  5 installs", "  openai/skills/pdf      1 install"], _host.Notices);

        _host.Notices.Clear();
        Archive("anthropics/skills", SkillZip.Repo("pdf", "docx"));
        Assert.False(await Run("anthropics/skills"));
        Assert.Equal([SkillInstallText.HeadlessPickHint, "  anthropics/skills/docx", "  anthropics/skills/pdf"], _host.Notices);

        _host.Notices.Clear();
        Assert.False(await Run("anthropics/skills/pdf"));
        Assert.Equal(SkillInstallText.HeadlessNeedsYes("pdf"), _host.Notices.Single());
        Assert.Single(_host.Previews);
        Assert.False(Directory.Exists(_host.Roots.Profile));

        Assert.True(await Run("anthropics/skills/pdf --yes --global"));
        Assert.True(File.Exists(Path.Combine(_host.Roots.Global, "pdf", SkillCatalog.FileName)));
        Assert.Empty(_host.Asked);
        Assert.Empty(_host.PickTitles);

        // Again from the same source: an update where it lives, whatever the flag says.
        Assert.True(await Run("anthropics/skills/pdf --yes --profile"));
        Assert.Equal(SkillInstallText.UpdatedNotice("pdf", SkillScope.Global, Path.Combine(Path.GetFullPath(_host.Roots.Global), "pdf")), _host.Notices[^1]);
    }

    [Fact]
    public async Task Refusals_AreErrors()
    {
        Assert.False(await Run("--yes"));
        Assert.Equal(SkillInstallText.UsageError, _host.Errors[^1]);

        Search("{\"skills\":[]}");
        Assert.False(await Run("nothing"));
        Assert.Equal(SkillInstallText.NoResultsError("nothing"), _host.Errors[^1]);

        _http.Map(Codeload + "o/empty/", (_, _) => Task.FromResult(StubHttpMessageHandler.Bytes(HttpStatusCode.OK, SkillZip.Build([("README.md", "x")]), "application/zip")));
        Assert.False(await Run("o/empty"));
        Assert.Equal(SkillInstallText.NoSkillsError("o/empty"), _host.Errors[^1]);

        _http.Map(Codeload + "o/html/", HttpStatusCode.OK, "<html></html>", "text/html");
        Assert.False(await Run("o/html"));
        Assert.Equal(SkillInstallText.NotZipError, _host.Errors[^1]);

        _http.Map(Api + "o/missing/", HttpStatusCode.NotFound, "{\"message\":\"Not Found\"}");
        _http.Map(Codeload + "o/missing/", HttpStatusCode.NotFound, "Not Found", "text/plain");
        Assert.False(await Run("o/missing"));
        Assert.Equal("o/missing is not on GitHub: the repository was deleted or made private", _host.Errors[^1]);
        Assert.False(await Run("https://github.com/o/missing/tree/gone/skills"));
        Assert.Equal("o/missing@gone is not on GitHub: the repository or the ref 'gone' is gone, or the repository is private", _host.Errors[^1]);

        Archive("o/bad", SkillZip.Build([("s/SKILL.md", SkillZip.SkillMd("s")), ("s/nul.txt", "x")]));
        Assert.False(await Run("o/bad"));
        Assert.Equal(SkillInstallText.CannotInstallError("s", SkillInstallText.UnsafePathRefusal("nul.txt")), _host.Errors[^1]);
        Assert.Empty(_host.Asked);
    }

    [Fact]
    public async Task ASearchHitWhoseRepositoryIsGone_SaysTheListingIsStale()
    {
        // 2026-09-27, the user's report: skills.sh still listed mhagrelius/dotfiles after the repository was deleted.
        Search(Hits(("o/missing/pdf", 3)));
        _http.Map(Api + "o/missing/", HttpStatusCode.NotFound, "{\"message\":\"Not Found\"}");
        _http.Map(Codeload + "o/missing/", HttpStatusCode.NotFound, "Not Found", "text/plain");

        Assert.False(await Run("pdf"));

        Assert.Equal("o/missing is not on GitHub: the repository was deleted or made private" + SkillInstallText.StaleSearchHitNote, _host.Errors.Single());
    }

    [Fact]
    public async Task ATakenName_IsRefused_BeforeTheQuestion_AndSkillsOff_IsAWarning()
    {
        string mine = Path.Combine(_host.Roots.Profile, "pdf");
        Directory.CreateDirectory(mine);
        File.WriteAllText(Path.Combine(mine, SkillCatalog.FileName), SkillZip.SkillMd("pdf"));
        Archive("anthropics/skills", SkillZip.Repo("pdf", "docx"));

        Assert.False(await Run("anthropics/skills/pdf"));
        Assert.Equal(SkillInstallText.TakenError("pdf", SkillScope.Profile), _host.Errors.Single());
        Assert.Empty(_host.Asked);

        _host.SkillsEnabled = false;
        _host.Answer = SkillScope.Global;
        Assert.True(await Run("anthropics/skills/docx"));
        Assert.Equal(SkillInstallText.SkillsOffWarning, _host.Warnings.Single());
    }

    /// <summary>A locked skill is never updated (2026-10-07, SkillLock): the update would swap its whole folder.</summary>
    [Fact]
    public async Task AnUpdateOfALockedSkill_IsRefused_BeforeTheQuestion()
    {
        Archive("anthropics/skills", SkillZip.Repo("pdf", "docx"));
        _host.Answer = SkillScope.Profile;
        Assert.True(await Run("anthropics/skills/pdf"));
        string folder = Path.Combine(_host.Roots.Profile, "pdf");
        Assert.Null(SkillLock.Set(folder, true, DateTimeOffset.UnixEpoch));
        int asked = _host.Asked.Count;

        Assert.False(await Run("anthropics/skills/pdf"));

        Assert.Equal(SkillInstallText.LockedError("pdf"), _host.Errors[^1]);
        Assert.Equal(asked, _host.Asked.Count);
        Assert.True(SkillLock.IsLocked(folder));
        Assert.Equal("Skill 'pdf' is locked: unlock it on /skills (its page's unlock row) to update it", SkillInstallText.LockedError("pdf"));
    }

    [Fact]
    public async Task ABadSearchAnswer_AndTheNetworkMode_AreErrors()
    {
        Search("{nope");
        Assert.False(await Run("pdf"));
        Assert.StartsWith("skills.sh's answer could not be read: ", _host.Errors[^1], StringComparison.Ordinal);

        _host.FetchOptions = new FetchOptions(FetchEngine.Default, "", NetworkReach.LocalAreaNetwork);
        Assert.False(await Run("o/r"));
        Assert.Equal(WebText.InternetRefused("codeload.github.com"), _host.Errors[^1]);
    }

    [Fact]
    public async Task AZip_IsInstalledWithItsUrlAsTheSource()
    {
        byte[] zip = SkillZip.Build([("SKILL.md", SkillZip.SkillMd("solo"))], root: "solo/", comment: null);
        _http.Map("https://example.com/solo.zip", (_, _) => Task.FromResult(StubHttpMessageHandler.Bytes(HttpStatusCode.OK, zip, "application/zip")));
        _host.Answer = SkillScope.Profile;

        Assert.True(await Run("https://example.com/solo.zip"));

        var sidecar = SkillProvenance.Read(Path.Combine(_host.Roots.Profile, "solo"))!;
        Assert.Equal((SkillProvenance.ZipKind, "https://example.com/solo.zip", "", (string?)null), (sidecar.Source, sidecar.Repo, sidecar.Ref, sidecar.Commit));
    }

    private sealed class FakeHost(SkillRoots roots) : ISkillInstallHost
    {
        public bool Headless { get; set; }
        public SkillRoots Roots { get; } = roots;
        public bool External => false;
        public bool SkillsEnabled { get; set; } = true;
        public FetchOptions FetchOptions { get; set; } = new(FetchEngine.Default, "", NetworkReach.Internet);
        public TimeProvider Time { get; } = new ManualTimeProvider();

        public List<string> Notices { get; } = [];
        public List<string> Warnings { get; } = [];
        public List<string> Errors { get; } = [];
        public List<string> Previews { get; } = [];
        public List<string> Spins { get; } = [];
        public List<string> PickTitles { get; } = [];
        public List<IReadOnlyList<string>> PickRows { get; } = [];
        public Queue<int?> Picks { get; } = new();
        public List<SkillInstallCheck> Asked { get; } = [];
        public SkillScope? Answer { get; set; }
        public int Rescans { get; private set; }

        public void Notice(string text) => Notices.Add(text);
        public void Warning(string text) => Warnings.Add(text);
        public void Error(string text) => Errors.Add(text);
        public void Preview(string markdown) => Previews.Add(markdown);

        public Task<T> SpinAsync<T>(string label, Func<Task<T>> work)
        {
            Spins.Add(label);
            return work();
        }

        public Task<int?> PickAsync(string title, IReadOnlyList<string> rows, CancellationToken cancellationToken)
        {
            PickTitles.Add(title);
            PickRows.Add(rows);
            return Task.FromResult(Picks.Dequeue());
        }

        public Task<SkillScope?> ConfirmAsync(SkillCandidate candidate, SkillSource source, SkillInstallCheck check, SkillScope? preselect, CancellationToken cancellationToken)
        {
            Asked.Add(check);
            return Task.FromResult(Answer);
        }

        public void Rescan() => Rescans++;
    }
}
