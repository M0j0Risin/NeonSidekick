using System.Text;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Protocol;
using NeonSidekick.App;
using NeonSidekick.Files;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Mcp;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// An MCP result's pictures and resources (2026-11-21, feature ideas #31 and #33) and <c>mcp_read</c>: the render of each block kind, the
/// tool's list, read and paging over a stub, and the session's resource side over the in-process pipe servers.
/// </summary>
public class McpReadToolTests : IAsyncDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly AppSettings _settings;
    private readonly InProcessMcpServers _servers = new();

    public McpReadToolTests()
    {
        _settings = new AppSettings(_dir);
        _settings.Update(d => d.McpServers = true);
    }

    public async ValueTask DisposeAsync()
    {
        await _servers.DisposeAsync();
        _settings.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static readonly byte[] Png = Encoding.ASCII.GetBytes(McpPipeServer.PngBase64);

    private static ImageContentBlock Image() => new() { Data = Png, MimeType = "image/png" };

    // ── The render ───────────────────────────────────────────────────────────

    [Fact]
    public void APictureBlock_IsAPicture_LabelledWithNoPath_AndTheTextSaysItComes()
    {
        var rendered = McpTool.Render(new CallToolResult { Content = [new TextContentBlock { Text = "shot" }, Image()] }, McpRenderContext.Plain("pw__shot"));
        Assert.Equal("⟪shot⟫\n" + McpText.PictureAttached(1, 4, 3) + "\n" + UntrustedText.QuoteNote, rendered.Text);
        var picture = Assert.Single(rendered.Pictures);
        Assert.Equal("", picture.Path);
        Assert.Equal("pw__shot #1", picture.McpSource);
        Assert.Equal("(picture 1, 4x3, in the next message)", McpText.PictureAttached(1, 4, 3));
        Assert.IsType<ToolImageResult>(rendered.ToolResult());
        Assert.IsType<string>(new McpRendered("x", []).ToolResult());
    }

    [Fact]
    public void BadBytes_BadBase64_TooBig_AndPastTheCap_AreEachALine_NeverAFailedCall()
    {
        var notPicture = new ImageContentBlock { Data = Encoding.ASCII.GetBytes(Convert.ToBase64String("not a picture"u8.ToArray())), MimeType = "image/png" };
        var badBase64 = new ImageContentBlock { Data = Encoding.ASCII.GetBytes("@@@@"), MimeType = "image/png" };
        var huge = new ImageContentBlock { Data = new byte[(int)(ImageFile.MaxFileBytes / 3 * 4) + 8], MimeType = "image/png" };
        var result = new CallToolResult { Content = [notPicture, badBase64, huge, Image(), Image(), Image(), Image(), Image()] };

        var rendered = McpTool.Render(result, McpRenderContext.Plain("t"));

        string unreadable = McpText.PictureUnreadable(ImageLoadFailure.CouldNotRead);
        Assert.Equal(string.Join("\n", unreadable, unreadable, McpText.PictureUnreadable(ImageLoadFailure.TooLarge),
            McpText.PictureAttached(1, 4, 3), McpText.PictureAttached(2, 4, 3), McpText.PictureAttached(3, 4, 3), McpText.PictureAttached(4, 4, 3), McpText.PictureLeftOut), rendered.Text);
        Assert.Equal(McpText.PicturesPerCall, rendered.Pictures.Count);
        Assert.Equal(["t #1", "t #2", "t #3", "t #4"], rendered.Pictures.Select(p => p.McpSource));
        Assert.Equal("(a picture was left out: one call carries 4 at most)", McpText.PictureLeftOut);
        Assert.Equal("(a picture was left out: it is over 20 MB or 40 megapixels)", McpText.PictureUnreadable(ImageLoadFailure.TooLarge));
    }

    [Fact]
    public void AnErrorResult_KeepsItsPrefix_AndItsPicturesStillAttach()
    {
        var rendered = McpTool.Render(new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = "Error: page crashed" }, Image()] }, McpRenderContext.Plain("t"));
        Assert.StartsWith(McpText.ErrorPrefix + "⟪page crashed⟫", rendered.Text, StringComparison.Ordinal);
        Assert.Single(rendered.Pictures);
    }

    [Fact]
    public void AnEmbeddedText_IsFencedUnderTheAppsHeader_AndACutNamesMcpReadsStartOnlyWhenOffered()
    {
        var shortOne = new EmbeddedResourceBlock { Resource = new TextResourceContents { Uri = "notes://a", MimeType = "text/markdown", Text = "# Hi ⟫" } };
        Assert.Equal("Resource ⟪notes://a⟫ (text/markdown):\n⟪# Hi ››⟫\n" + UntrustedText.QuoteNote,
            McpTool.Render(new CallToolResult { Content = [shortOne] }, McpRenderContext.Plain("t")).Text);

        // A text of escaping characters: the window's source length is less than MaxReadChars, and the note's start is that length.
        string body = new string('\u202E', 10_000) + new string('x', 40_000);
        var longOne = new EmbeddedResourceBlock { Resource = new TextResourceContents { Uri = "notes://long", Text = body } };
        var (_, consumed) = UntrustedText.ScrubPrefix(body, 0, WorkingDirectory.MaxReadChars);
        Assert.True(consumed < WorkingDirectory.MaxReadChars);

        string offered = McpTool.Render(new CallToolResult { Content = [longOne] }, new McpRenderContext("t", ReadOffered: true)).Text;
        Assert.Contains(McpText.ResourceCut(consumed, body.Length, readOffered: true), offered, StringComparison.Ordinal);
        Assert.Contains("mcp_read with this uri and start " + consumed.ToString(System.Globalization.CultureInfo.InvariantCulture), offered, StringComparison.Ordinal);
        string plain = McpTool.Render(new CallToolResult { Content = [longOne] }, McpRenderContext.Plain("t")).Text;
        Assert.Contains("the rest was not kept", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("mcp_read", plain, StringComparison.Ordinal);

        // mcp_read on the same uri, from the note's start, goes on at the very next character.
        var read = McpReadTool.Read(new ReadResourceResult { Contents = [new TextResourceContents { Uri = "notes://long", Text = body }] }, "notes://long", consumed);
        Assert.Contains(UntrustedText.Quote(UntrustedText.ScrubPrefix(body, consumed, WorkingDirectory.MaxReadChars).Scrubbed), read.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ABlob_IsAPictureWhenItsTypeSaysSo_ElseNamed_ALinkIsNamed_AndAudioStaysDropped()
    {
        var picture = new EmbeddedResourceBlock { Resource = new BlobResourceContents { Uri = "x://p.png", MimeType = "image/png", Blob = Png } };
        var zip = new EmbeddedResourceBlock { Resource = new BlobResourceContents { Uri = "x://a.zip", MimeType = "application/zip", Blob = Encoding.ASCII.GetBytes("AAAAAAAA") } };
        var link = new ResourceLinkBlock { Uri = "x://doc", Name = "doc", Description = "The\ndoc." };
        var audio = new AudioContentBlock { Data = Png, MimeType = "audio/wav" };

        var plain = McpTool.Render(new CallToolResult { Content = [picture, zip, link, audio] }, McpRenderContext.Plain("t"));
        Assert.Equal(string.Join("\n",
            McpText.PictureAttached(1, 4, 3),
            "(resource ⟪x://a.zip⟫, application/zip, 6 bytes, not read: only text and pictures are)",
            "(a link to resource ⟪doc⟫ ⟪x://doc⟫: ⟪The doc.⟫)",
            "(an audio block was dropped)",
            UntrustedText.QuoteNote), plain.Text);
        Assert.Single(plain.Pictures);

        Assert.Contains("; read it with mcp_read)", McpTool.Render(new CallToolResult { Content = [link] }, new McpRenderContext("t", true)).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnMcpPicture_TakenOutByTheBudget_SaysToCallTheToolAgain_AndKeepsItsMark()
    {
        var image = McpTool.Render(new CallToolResult { Content = [Image()] }, McpRenderContext.Plain("pw__shot")).Pictures[0];
        var part = ConversationHistory.ImagePart(image);
        // The label lives under McpKey alone (2026-11-22, the review's #47): never a path a rewind or an export would take for a file.
        Assert.Null(ConversationHistory.PicturePath(part));
        Assert.Equal("pw__shot #1", ConversationHistory.McpLabel(part));
        string left = PictureBudget.LeftOutFor(part);
        Assert.Equal(McpText.PictureTakenOut("pw__shot #1"), left);
        Assert.DoesNotContain("view_image", left, StringComparison.Ordinal);
        Assert.True(PictureBudget.LeftOutPart(part).AdditionalProperties!.ContainsKey(ConversationHistory.McpKey));
    }

    [Fact]
    public void ASavedSession_LeavesAnMcpPictureOut_UnlessKept_AndAKeptOneComesBackMarked()
    {
        var image = McpTool.Render(new CallToolResult { Content = [Image()] }, McpRenderContext.Plain("pw__shot")).Pictures[0];
        var message = new ChatMessage(ChatRole.User, [ConversationHistory.ImagePart(image)]);

        string dropped = Sessions.SessionHistory.ToJson([message]);
        Assert.Contains(System.Text.Json.JsonEncodedText.Encode(McpText.PictureNotKept("pw__shot #1")).ToString(), dropped, StringComparison.Ordinal);
        Assert.DoesNotContain(McpPipeServer.PngBase64[..20], dropped, StringComparison.Ordinal);

        string kept = Sessions.SessionHistory.ToJson([message], keepMcp: true);
        var restored = Sessions.SessionHistory.FromJsonWhole(kept, out _);
        var part = Assert.IsType<DataContent>(Assert.Single(Assert.Single(restored).Contents));
        Assert.Equal("pw__shot #1", part.AdditionalProperties![ConversationHistory.McpKey]);
        Assert.Null(ConversationHistory.PicturePath(part));   // #47: restored with no path

        // A row with no label (#101) comes back named generically, never as an empty name.
        var blank = Sessions.SessionHistory.Restore(new Sessions.StoredMessage
        {
            Role = "user",
            Parts = [new Sessions.StoredPart { Kind = Sessions.StoredPart.ImageKind, Bytes = McpPipeServer.PngBase64, MediaType = "image/png", Mcp = true }],
        });
        Assert.Equal(McpText.UnnamedPicture, ConversationHistory.McpLabel(Assert.Single(blank!.Contents)));

        // Saved again with the setting off, the restored picture is left out as it should be.
        Assert.Contains(System.Text.Json.JsonEncodedText.Encode(McpText.PictureNotKept("pw__shot #1")).ToString(), Sessions.SessionHistory.ToJson(restored), StringComparison.Ordinal);
    }

    // ── mcp_read over a stub ─────────────────────────────────────────────────

    private sealed class StubResources : IMcpResources
    {
        public IReadOnlyList<string> ResourceServers { get; init; } = ["docs"];

        public ReadResourceResult Answer { get; set; } = new();

        public Exception? Fails { get; set; }

        public Task<McpResourceAnswer> ListResourcesAsync(string server, int max, CancellationToken cancellationToken) =>
            Task.FromResult(new McpResourceAnswer(new McpResourceList(
                [new Resource { Uri = "docs://a", Name = "A ⟫", MimeType = "text/plain", Size = 1200, Description = "First." }],
                [new ResourceTemplate { UriTemplate = "docs://{id}", Name = "by id" }], More: false), null, null));

        public Task<McpResourceAnswer> ReadResourceAsync(string server, string uri, CancellationToken cancellationToken) =>
            Fails is { } fails ? Task.FromException<McpResourceAnswer>(fails) : Task.FromResult(new McpResourceAnswer(null, Answer, null));
    }

    private static Task<object?> Invoke(IMcpResources resources, Dictionary<string, object?> arguments) =>
        new McpReadTool(resources, resources.ResourceServers).InvokeAsync(new AIFunctionArguments(arguments)).AsTask();

    [Fact]
    public async Task TheList_IsOneLineEach_TheServersWordsFenced()
    {
        string listed = Assert.IsType<string>(await Invoke(new StubResources(), new() { ["server"] = "docs" }));
        Assert.Equal(
            "docs: 1 resource, 1 template\n- ⟪docs://a⟫ · ⟪A ››⟫ · text/plain · 1,200 bytes: ⟪First.⟫\n" + McpText.TemplatesHeader + "\n- ⟪docs://{id}⟫ · ⟪by id⟫\n" + UntrustedText.QuoteNote,
            listed);
    }

    [Fact]
    public async Task ARead_PagesFromStart_TheNextStartWhereTheWindowStopped_AndPastTheEndIsAnError()
    {
        string body = new string('\u2066', 3_000) + new string('y', 30_000);   // page one: 18,000 escaped and 14,000 y; page two, the rest
        var stub = new StubResources { Answer = new ReadResourceResult { Contents = [new TextResourceContents { Uri = "docs://a", MimeType = "text/plain", Text = body }] } };
        string first = Assert.IsType<string>(await Invoke(stub, new() { ["server"] = "docs", ["uri"] = "docs://a" }));
        var (_, consumed) = UntrustedText.ScrubPrefix(body, 0, WorkingDirectory.MaxReadChars);
        Assert.StartsWith(McpText.ReadHeader("docs://a", "text/plain", 1, consumed, body.Length, consumed), first, StringComparison.Ordinal);
        Assert.Contains("continue with start " + consumed.ToString(System.Globalization.CultureInfo.InvariantCulture), first, StringComparison.Ordinal);
        Assert.EndsWith(UntrustedText.QuoteNote, first, StringComparison.Ordinal);

        string last = Assert.IsType<string>(await Invoke(stub, new() { ["server"] = "docs", ["uri"] = "docs://a", ["start"] = consumed }));
        Assert.DoesNotContain("continue with start", last, StringComparison.Ordinal);
        Assert.Equal(McpText.StartPastEnd(body.Length, body.Length), await Invoke(stub, new() { ["server"] = "docs", ["uri"] = "docs://a", ["start"] = body.Length }));
        Assert.Equal(McpText.BadStart(-1), await Invoke(stub, new() { ["server"] = "docs", ["uri"] = "docs://a", ["start"] = -1 }));
    }

    [Fact]
    public async Task SeveralParts_AreJoinedWithALeadLineEach_APictureBlobIsAPicture_AnotherBlobIsNamed()
    {
        var stub = new StubResources
        {
            Answer = new ReadResourceResult
            {
                Contents =
                [
                    new TextResourceContents { Uri = "docs://a", MimeType = "text/plain", Text = "one" },
                    new TextResourceContents { Uri = "docs://b", Text = "two" },
                    new BlobResourceContents { Uri = "docs://p.png", MimeType = "image/png", Blob = Png },
                    new BlobResourceContents { Uri = "docs://x.bin", MimeType = "application/octet-stream", Blob = Encoding.ASCII.GetBytes("AAAA") },
                ],
            },
        };
        var result = Assert.IsType<ToolImageResult>(await Invoke(stub, new() { ["server"] = "docs", ["uri"] = "docs://all" }));
        Assert.Contains(UntrustedText.Quote("docs://a · text/plain\none\n\ndocs://b · text\ntwo"), result.Text, StringComparison.Ordinal);
        Assert.Contains(McpText.PictureAttached(1, 4, 3), result.Text, StringComparison.Ordinal);
        Assert.Contains("(resource ⟪docs://x.bin⟫, application/octet-stream, 3 bytes, not read", result.Text, StringComparison.Ordinal);
        Assert.Equal("mcp_read #1", Assert.Single(result.Images).McpSource);
    }

    // ── The review's section 5 (2026-11-22) ─────────────────────────────────

    [Fact]
    public async Task TheUriNamingOneOfSeveralParts_PagesThatPartAlone_AndNamesTheOthers()
    {
        var stub = new StubResources
        {
            Answer = new ReadResourceResult
            {
                Contents =
                [
                    new TextResourceContents { Uri = "docs://a", MimeType = "text/plain", Text = "one" },
                    new TextResourceContents { Uri = "docs://b", Text = "two" },
                ],
            },
        };
        string read = Assert.IsType<string>(await Invoke(stub, new() { ["server"] = "docs", ["uri"] = "docs://a" }));
        Assert.StartsWith(McpText.ReadHeader("docs://a", "text/plain", 1, 3, 3, null) + "\n" + UntrustedText.Quote("one"), read, StringComparison.Ordinal);
        Assert.Contains(McpText.OtherPart("docs://b", null, 3), read, StringComparison.Ordinal);
        Assert.DoesNotContain("two", read, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALaterPage_NeverAttachesThePicturesAgain_AndPastTheEndStillSaysWhatCame()
    {
        string body = new string('y', WorkingDirectory.MaxReadChars + 10);
        var stub = new StubResources
        {
            Answer = new ReadResourceResult
            {
                Contents =
                [
                    new TextResourceContents { Uri = "docs://a", MimeType = "text/plain", Text = body },
                    new BlobResourceContents { Uri = "docs://p.png", MimeType = "image/png", Blob = Png },
                    new BlobResourceContents { Uri = "docs://x.zip", MimeType = "application/zip", Blob = Encoding.ASCII.GetBytes("AAAA") },
                ],
            },
        };
        var first = Assert.IsType<ToolImageResult>(await Invoke(stub, new() { ["server"] = "docs", ["uri"] = "docs://a" }));
        Assert.Single(first.Images);
        Assert.Contains("docs://x.zip", first.Text, StringComparison.Ordinal);

        string second = Assert.IsType<string>(await Invoke(stub, new() { ["server"] = "docs", ["uri"] = "docs://a", ["start"] = WorkingDirectory.MaxReadChars }));
        Assert.Contains(McpText.CameWithFirstPage(1, 1), second, StringComparison.Ordinal);
        Assert.DoesNotContain("docs://x.zip", second, StringComparison.Ordinal);

        string past = Assert.IsType<string>(await Invoke(stub, new() { ["server"] = "docs", ["uri"] = "docs://a", ["start"] = body.Length }));
        Assert.StartsWith(McpText.StartPastEnd(body.Length, body.Length), past, StringComparison.Ordinal);
        Assert.Contains(McpText.CameWithFirstPage(1, 1), past, StringComparison.Ordinal);
        Assert.Equal("(its 1 picture and 1 other part came with the first page, start 0)", McpText.CameWithFirstPage(1, 1));
    }

    [Fact]
    public async Task AnEmptyText_SaysItsEmpty_AStartWithoutAUri_IsRefused_AndAWayThereThatFails_IsTheServersReadFailure()
    {
        var empty = new StubResources { Answer = new ReadResourceResult { Contents = [new TextResourceContents { Uri = "docs://a", MimeType = "text/plain", Text = "" }] } };
        string read = Assert.IsType<string>(await Invoke(empty, new() { ["server"] = "docs", ["uri"] = "docs://a" }));
        Assert.StartsWith(McpText.ReadHeader("docs://a", "text/plain", 0, 0, 0, null) + "\n" + McpText.ResourceEmpty, read, StringComparison.Ordinal);
        Assert.DoesNotContain("1–0", read, StringComparison.Ordinal);

        Assert.Equal(McpText.StartNeedsUri, await Invoke(empty, new() { ["server"] = "docs", ["start"] = 5 }));

        var failing = new StubResources { Fails = new HttpRequestException("connection reset") };
        string failed = Assert.IsType<string>(await Invoke(failing, new() { ["server"] = "docs", ["uri"] = "docs://a" }));
        Assert.StartsWith(McpText.ErrorPrefix + McpText.ReadFailed("docs", UntrustedText.Fenced("connection reset")), failed, StringComparison.Ordinal);
        failing.Fails = new TimeoutException("slow");
        Assert.StartsWith(McpText.ErrorPrefix + "docs could not read it: ", Assert.IsType<string>(await Invoke(failing, new() { ["server"] = "docs", ["uri"] = "docs://a" })), StringComparison.Ordinal);
    }

    [Fact]
    public void EmbeddedTexts_ShareOneBudget_ALaterOneIsNamed_ALinksNameStaysOnOneLine()
    {
        string body = new string('x', WorkingDirectory.MaxReadChars);
        EmbeddedResourceBlock Block(int n) => new() { Resource = new TextResourceContents { Uri = "notes://" + n.ToString(System.Globalization.CultureInfo.InvariantCulture), Text = body } };
        var link = new ResourceLinkBlock { Uri = "x://doc", Name = "line\nbreak" };
        string text = McpTool.Render(new CallToolResult { Content = [Block(1), Block(2), Block(3), link] }, new McpRenderContext("t", ReadOffered: true)).Text;
        Assert.Contains(UntrustedText.Quote(body), text, StringComparison.Ordinal);
        Assert.Contains(McpText.ResourceLeftOut("notes://2", body.Length, readOffered: true), text, StringComparison.Ordinal);
        Assert.Contains(McpText.ResourceLeftOut("notes://3", body.Length, readOffered: true), text, StringComparison.Ordinal);
        Assert.True(text.Length < body.Length * 2);
        Assert.Contains("(a link to resource ⟪line break⟫ ⟪x://doc⟫", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnErrorResult_OpeningWithAnEmbeddedText_KeepsALaterTextsOwnError()
    {
        var embedded = new EmbeddedResourceBlock { Resource = new TextResourceContents { Uri = "notes://a", Text = "context" } };
        string text = McpTool.Render(new CallToolResult { IsError = true, Content = [embedded, new TextContentBlock { Text = "Error: the second" }] }, McpRenderContext.Plain("t")).Text;
        Assert.Contains("⟪Error: the second⟫", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Paging_StopsAtARepeatedCursor_AnEmptyPage_AndTenPages()
    {
        int calls = 0;
        var (items, more) = await McpSession.PagesAsync<int>(_ => { calls++; return Task.FromResult<(IList<int>?, string?)>(([1], "same")); }, 100);
        Assert.Equal((2, 2, true), (calls, items.Count, more));

        calls = 0;
        (items, more) = await McpSession.PagesAsync<int>(_ => { calls++; return Task.FromResult<(IList<int>?, string?)>(([], "c" + calls.ToString(System.Globalization.CultureInfo.InvariantCulture))); }, 100);
        Assert.Equal((1, 0, true), (calls, items.Count, more));

        calls = 0;
        (items, more) = await McpSession.PagesAsync<int>(_ => { calls++; return Task.FromResult<(IList<int>?, string?)>(([1], "c" + calls.ToString(System.Globalization.CultureInfo.InvariantCulture))); }, 100);
        Assert.Equal((McpSession.MaxListPages, true), (calls, more));

        calls = 0;
        (items, more) = await McpSession.PagesAsync<int>(cursor => { calls++; return Task.FromResult<(IList<int>?, string?)>(([1, 2], cursor is null ? "next" : null)); }, 100);
        Assert.Equal((2, 4, false), (calls, items.Count, more));
    }

    [Fact]
    public async Task ATemplateListThatFails_KeepsTheResources_AndLogsIt()
    {
        _servers.Resources("docs", ("pipe://docs/a", "a", "alpha"));
        _servers.TemplatesFail.Add("docs");
        await using var session = await ConnectedAsync("""{ "mcpServers": { "docs": { "command": "d" } } }""");
        var listed = await session.ListResourcesAsync("docs", 10, CancellationToken.None);
        Assert.Null(listed.Refusal);
        Assert.Equal("pipe://docs/a", Assert.Single(listed.Listing!.Resources).Uri);
        Assert.Empty(listed.Listing.Templates);
    }

    [Fact]
    public async Task AnUnknownServer_NamesTheServersThatOfferResources()
    {
        Assert.Equal(McpText.ErrorPrefix + "wiki offers no resources; mcp_read reads from: docs", await Invoke(new StubResources(), new() { ["server"] = "wiki" }));
        Assert.Equal(McpText.ErrorPrefix + "wiki offers no resources; no connected MCP server does", await Invoke(new StubResources { ResourceServers = [] }, new() { ["server"] = "wiki" }));
        Assert.StartsWith(McpText.ErrorPrefix + "start", Assert.IsType<string>(await Invoke(new StubResources(), new() { ["server"] = "docs", ["uri"] = "docs://a", ["start"] = -5 })), StringComparison.Ordinal);
    }

    [Fact]
    public void ItReadsOnly_SoPlanModeKeepsIt_AndAdvisorsNeverGetIt()
    {
        Assert.Contains(McpReadTool.ToolName, Plans.PlanTools.ReadOnly);
        Assert.Contains(McpReadTool.ToolName, NeonAdvisorTool.Excluded);
        Assert.True(McpAskRule.AsksRead(McpAskMode.Always));
        Assert.False(McpAskRule.AsksRead(McpAskMode.Writes));
        Assert.False(McpAskRule.AsksRead(McpAskMode.Never));
    }

    // ── The session's resource side over the pipe servers ─────────────────────

    private void ProfileFile(string json)
    {
        Directory.CreateDirectory(Profiles.ConfigsDirectory(_settings.ProfileDirectory));
        File.WriteAllText(McpConfigFile.ProfilePath(_settings.ProfileDirectory), json);
    }

    private async Task<McpSession> ConnectedAsync(string json, McpApprovers.Recording? approver = null)
    {
        ProfileFile(json);
        var session = new McpSession(_settings, _servers.Transport) { Approver = approver is null ? McpApprovers.AllowAll : approver.Approve, ReadToolOffered = () => true };
        await session.ConnectAllAsync(_settings.Current, null, CancellationToken.None);
        return session;
    }

    [Fact]
    public async Task ThroughTheSession_OnlyAServerWithResourcesIsOffered_ItListsReadsAndFillsATemplate()
    {
        _servers.Resources("docs", ("pipe://docs/a", "a", "alpha"), ("pipe://docs/p.png", "p", null));
        await using var session = await ConnectedAsync("""{ "mcpServers": { "docs": { "command": "d" }, "plain": { "command": "p" } } }""");

        Assert.Equal(["docs"], session.ResourceServers);
        var read = Assert.IsType<McpReadTool>(session.TurnTools()[^1]);
        Assert.Contains("Servers: docs.", read.Description, StringComparison.Ordinal);

        string listed = Assert.IsType<string>(await read.InvokeAsync(new AIFunctionArguments { ["server"] = "docs" }));
        Assert.Contains("⟪pipe://docs/a⟫", listed, StringComparison.Ordinal);
        Assert.Contains("⟪" + McpPipeServer.TemplateUri + "⟫", listed, StringComparison.Ordinal);

        string text = Assert.IsType<string>(await read.InvokeAsync(new AIFunctionArguments { ["server"] = "docs", ["uri"] = "pipe://docs/a" }));
        Assert.Contains("⟪alpha⟫", text, StringComparison.Ordinal);
        Assert.Contains("⟪echo: hello⟫", Assert.IsType<string>(await read.InvokeAsync(new AIFunctionArguments { ["server"] = "docs", ["uri"] = "pipe://echo/hello" })), StringComparison.Ordinal);
        Assert.IsType<ToolImageResult>(await read.InvokeAsync(new AIFunctionArguments { ["server"] = "docs", ["uri"] = "pipe://docs/p.png" }));

        string missing = Assert.IsType<string>(await read.InvokeAsync(new AIFunctionArguments { ["server"] = "docs", ["uri"] = "pipe://docs/none" }));
        Assert.StartsWith(McpText.ErrorPrefix + "docs could not read it: ⟪", missing, StringComparison.Ordinal);

        Assert.Equal(McpText.ErrorPrefix + "plain offers no resources; mcp_read reads from: docs", await read.InvokeAsync(new AIFunctionArguments { ["server"] = "plain" }));
    }

    [Fact]
    public async Task UnderAskAlways_AReadAsks_AsTheProtocolsMethod_AndADenialSendsNothing()
    {
        _servers.Resources("docs", ("pipe://docs/a", "a", "alpha"));
        var approver = new McpApprovers.Recording(McpVerdict.No);
        await using var session = await ConnectedAsync("""{ "mcpServers": { "docs": { "command": "d", "ask": "always" } } }""", approver);

        var denied = await session.ReadResourceAsync("docs", "pipe://docs/a", CancellationToken.None);
        Assert.Equal(McpText.ErrorPrefix + McpText.DeniedError(McpReadTool.ToolName), denied.Refusal!.Render());
        Assert.Equal(0, _servers.Server("docs").Reads);
        var ask = Assert.Single(approver.Asks);
        Assert.Equal((McpText.ResourcesReadMethod, McpReadTool.ToolName, McpReadTool.ToolName), (ask.ServerTool, ask.Name, ask.Definition));
        Assert.NotEqual("", ask.Fingerprint);
        Assert.Equal("pipe://docs/a", ask.Arguments["uri"].GetString());

        var allowed = await session.ReadResourceAsync("docs", "pipe://docs/a", CancellationToken.None);
        Assert.Null(allowed.Refusal);
        Assert.Equal(1, _servers.Server("docs").Reads);

        await session.ListResourcesAsync("docs", 10, CancellationToken.None);
        Assert.Equal(McpText.ResourcesListMethod, approver.Asks[^1].ServerTool);

        // One allow key for both methods (2026-11-22, the review's #116), and mcp_read counted among the tools that ask (#38).
        Assert.All(approver.Asks, a => Assert.Equal(McpReadTool.ToolName, a.AllowTool));
        Assert.Single(approver.Asks.Select(a => McpSession.McpAllowKey(a.Server, a.AllowTool ?? a.ServerTool, a.Fingerprint, a.Definition)).Distinct());
        Assert.Contains(McpReadTool.ToolName, session.AskingNames());
    }

    [Fact]
    public async Task UnderWrites_AReadNeverAsks()
    {
        _servers.Resources("docs", ("pipe://docs/a", "a", "alpha"));
        var approver = new McpApprovers.Recording();
        await using var session = await ConnectedAsync("""{ "mcpServers": { "docs": { "command": "d" } } }""", approver);
        Assert.Null((await session.ReadResourceAsync("docs", "pipe://docs/a", CancellationToken.None)).Refusal);
        Assert.Empty(approver.Asks);
        Assert.DoesNotContain(McpReadTool.ToolName, session.AskingNames());
    }

    [Fact]
    public async Task ADroppedServer_ReconnectsForTheRead_AndOneThatCameBackWithoutResourcesIsRefused()
    {
        _servers.Resources("docs", ("pipe://docs/a", "a", "alpha"));
        await using var session = await ConnectedAsync("""{ "mcpServers": { "docs": { "command": "d" } } }""");

        await _servers.Drop("docs");
        var back = await session.ReadResourceAsync("docs", "pipe://docs/a", CancellationToken.None);
        Assert.Null(back.Refusal);
        Assert.Equal("alpha", Assert.IsType<TextResourceContents>(Assert.Single(back.Read!.Contents)).Text);

        _servers.NoResources("docs");
        await _servers.Drop("docs");
        var gone = await session.ReadResourceAsync("docs", "pipe://docs/a", CancellationToken.None);
        Assert.StartsWith(McpText.ErrorPrefix + "docs offers no resources", gone.Refusal!.Render(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AToolsResult_PointsToMcpRead_OnlyForAServerItReads()
    {
        _servers.Tools("docs", (McpPipeServer.LinkToolName, "Links."));
        _servers.Resources("docs", ("pipe://docs/a", "a", "alpha"));
        _servers.Tools("plain", (McpPipeServer.LinkToolName, "Links."));
        await using var session = await ConnectedAsync("""{ "mcpServers": { "docs": { "command": "d" }, "plain": { "command": "p" } } }""");
        var tools = session.Tools;

        string docs = Assert.IsType<string>(await tools.Single(t => t.Name == "docs__link").InvokeAsync(new AIFunctionArguments()));
        string plain = Assert.IsType<string>(await tools.Single(t => t.Name == "plain__link").InvokeAsync(new AIFunctionArguments()));
        Assert.Contains("read it with mcp_read", docs, StringComparison.Ordinal);
        Assert.DoesNotContain("mcp_read", plain, StringComparison.Ordinal);
    }

    [Fact]
    public void ABlindModel_HasEveryMcpToolsPicturesKeptBack()
    {
        var read = new McpReadTool(new StubResources(), ["docs"]);
        Assert.Empty(ChatScreen.UnseenMcpPictures(sees: true, [read]));
        Assert.Equal([McpReadTool.ToolName], ChatScreen.UnseenMcpPictures(sees: false, [read, new GetCurrentTimeTool(TimeProvider.System)]));
    }

    // ── Picture labels across a conversation (2026-11-22, the review's #60, #131, #141) ──

    [Fact]
    public void OneCounter_NumbersEveryToolsPictures_AndAnUnseenOne_SaysItWentToTheUser()
    {
        var counter = new McpPictureCounter();
        var shot = McpTool.Render(new CallToolResult { Content = [Image()] }, McpRenderContext.Plain("pw__shot") with { Pictures = counter });
        var read = McpReadTool.Read(new ReadResourceResult { Contents = [new BlobResourceContents { Uri = "x://p.png", MimeType = "image/png", Blob = Png }] }, "x://p.png", 0,
            McpRenderContext.Plain(McpReadTool.ToolName) with { Pictures = counter });
        Assert.Equal("pw__shot #1", Assert.Single(shot.Pictures).McpSource);
        Assert.Equal("mcp_read #2", Assert.Single(read.Pictures).McpSource);
        Assert.Contains(McpText.PictureAttached(2, 4, 3), read.Text, StringComparison.Ordinal);

        var unseen = McpTool.Render(new CallToolResult { Content = [Image()] }, McpRenderContext.Plain("t") with { PicturesSeen = false });
        Assert.Contains(McpText.PictureShownToUser(1, 4, 3), unseen.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("in the next message", unseen.Text, StringComparison.Ordinal);
        Assert.Single(unseen.Pictures);   // still the user's

        Assert.Equal(7, McpPictureCounter.Highest(["pw__shot #2", "mcp_read #7", "no number", "x #"]));
        counter.Reset(7);
        Assert.Equal(8, counter.Next());
    }

    [Fact]
    public async Task AResumedSession_NumbersPastItsOwnPictures()
    {
        _servers.Resources("docs", ("pipe://docs/p.png", "p", null));
        await using var session = await ConnectedAsync("""{ "mcpServers": { "docs": { "command": "d" } } }""");
        var old = new DataContent(new byte[] { 1 }, "image/png") { AdditionalProperties = new AdditionalPropertiesDictionary { [ConversationHistory.McpKey] = "pw__shot #2" } };
        session.ResetPictures([new ChatMessage(ChatRole.User, [old])]);

        var read = new McpReadTool(session, session.ResourceServers);
        var result = Assert.IsType<ToolImageResult>(await read.InvokeAsync(new AIFunctionArguments { ["server"] = "docs", ["uri"] = "pipe://docs/p.png" }));
        Assert.Equal("mcp_read #3", Assert.Single(result.Images).McpSource);
    }

    [Fact]
    public void ACarrier_NamesAnMcpPicture_ByItsLabel()
    {
        var history = new ConversationHistory("system");
        history.AddToolImages([new ImageAttachment("", [1], ImageFile.Png, 1, 1) { McpSource = "pw__shot #1" }], "pw__shot");
        Assert.Equal(ConversationHistory.ImageCarrierText(["pw__shot #1"], "pw__shot"), history.Messages[^1].Contents.OfType<TextContent>().First().Text);
        Assert.Equal("pw__shot-1.png", McpText.PictureFileName("pw__shot #1", ".png"));
        Assert.Equal("a-b_c-2.jpg", McpText.PictureFileName("a/b_c #2", ".jpg"));
    }
}
