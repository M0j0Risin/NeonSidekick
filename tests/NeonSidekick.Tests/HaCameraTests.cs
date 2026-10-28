using System.Net;
using System.Net.Http.Headers;
using NeonSidekick.Files;
using NeonSidekick.HomeAssistant;
using NeonSidekick.Images;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// Home Assistant's camera stills (2026-10-27, the ring-integration plan's step 1): <c>camera_proxy</c> fetched over a stub server —
/// the path and the token, a camera's own failures, the type and the size refused before the body is read, the capped read, the
/// timeout — and <see cref="HaCamera"/>'s strip, fit and save into <c>home-cameras</c> through the write guard.
/// </summary>
public sealed class HaCameraTests : IDisposable
{
    private const string Server = "http://ha.lan:8123";
    private const string Token = "test-token";
    private const string Proxy = Server + "/api/camera_proxy/";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly StubHttpMessageHandler _stub = new();

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private HaClient Client() => new(new Uri(Server), Token, new HttpClient(_stub));

    private static Task<HaPictureReply> Fetch(HaClient client, string id = "camera.porch", double seconds = 5) =>
        client.CameraAsync(id, TimeSpan.FromSeconds(seconds), CancellationToken.None);

    /// <summary>An answer whose body is <paramref name="body"/>, with no <c>Content-Length</c> unless one is set: a stream that can't seek.</summary>
    private static HttpResponseMessage Streamed(HttpStatusCode status, Stream body, string mediaType, long? length = null)
    {
        var content = new StreamContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        content.Headers.ContentLength = length;
        return new HttpResponseMessage(status) { Content = content };
    }

    /// <summary>
    /// <paramref name="length"/> zero bytes that can't seek (so no length is known), counting what was read; a negative length never
    /// ends, and <paramref name="stall"/> waits for the read's cancellation instead of answering.
    /// </summary>
    private sealed class Counted(long length, bool stall = false) : Stream
    {
        private readonly bool _endless = length < 0;
        private long _left = length;

        public long Taken { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (stall)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            int n = (int)(_endless ? buffer.Length : Math.Min(buffer.Length, _left));
            buffer.Span[..n].Clear();
            _left -= n;
            Taken += n;
            return n;
        }
    }

    // ── the fetch ───────────────────────────────────────────────────────────

    [Fact]
    public async Task AStill_IsFetchedAtItsEscapedPath_WithTheToken()
    {
        byte[] jpeg = StripFixtures.Jpeg();
        _stub.Map(Proxy, (_, _) => Task.FromResult(StubHttpMessageHandler.Bytes(HttpStatusCode.OK, jpeg, "image/jpeg")));
        using var client = Client();

        var reply = await Fetch(client);

        Assert.True(reply.Ok);
        Assert.Equal(jpeg, reply.Bytes);
        Assert.Equal("image/jpeg", reply.MediaType);
        var request = _stub.Requests.Single();
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(Proxy + "camera.porch", request.Uri.AbsoluteUri);
        Assert.Equal("Bearer " + Token, request.Authorization);
        Assert.Equal("/api/camera_proxy/camera.a%20b%2F..%3Fx", HaClient.CameraPath("camera.a b/..?x"));
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task ACamerasOwnFailures_HaveTheirOwnWords(HttpStatusCode status)
    {
        _stub.Map(Proxy, status, "500: Internal Server Error", "text/plain");
        using var client = Client();

        var reply = await Fetch(client);

        Assert.False(reply.Ok);
        Assert.Equal((int)status, reply.Status);
        Assert.Equal(status switch
        {
            HttpStatusCode.ServiceUnavailable => HaText.CameraOff,
            HttpStatusCode.InternalServerError => HaText.CameraNoPicture,
            _ => HaText.NoSuchCamera,
        }, reply.Error);
    }

    [Fact]
    public async Task AnotherFailure_ReadsAsEveryCallsDoes_FromNoMoreThanTheHeadOfItsBody()
    {
        var body = new Counted(1_000_000);
        var stub = new StubHttpMessageHandler()
            .Map(Proxy + "camera.locked", HttpStatusCode.Unauthorized, "401: Unauthorized", "text/plain")
            .Map(Proxy, (_, _) => Task.FromResult(Streamed(HttpStatusCode.BadGateway, body, "text/plain")));
        using var client = new HaClient(new Uri(Server), Token, new HttpClient(stub));

        Assert.Equal(HaText.Unauthorized, (await Fetch(client, "camera.locked")).Error);
        var reply = await Fetch(client);

        Assert.StartsWith("Error: Home Assistant answered 502 to /api/camera_proxy/camera.porch", reply.Error);
        Assert.Equal(502, reply.Status);
        Assert.True(body.Taken <= HaClient.CameraErrorBytes, $"read {body.Taken} bytes of a failure");
    }

    [Fact]
    public async Task AnAnswerThatIsNoPicture_IsRefusedUnread()
    {
        var page = new Counted(10_000);
        _stub.Map(Proxy, (_, _) => Task.FromResult(Streamed(HttpStatusCode.OK, page, "text/html")));
        using var client = Client();

        var reply = await Fetch(client);

        Assert.False(reply.Ok);
        Assert.Contains("text/html", reply.Error);
        Assert.Equal(HaText.NotAPicture("text/html"), reply.Error);
        Assert.Equal(0, page.Taken);
    }

    [Fact]
    public async Task ALengthOverTheCap_IsRefusedUnread()
    {
        var picture = new Counted(HaCamera.MaxBytes + 1L);
        _stub.Map(Proxy, (_, _) => Task.FromResult(Streamed(HttpStatusCode.OK, picture, "image/jpeg", HaCamera.MaxBytes + 1L)));
        using var client = Client();

        var reply = await Fetch(client);

        Assert.Equal(HaText.PictureTooLarge, reply.Error);
        Assert.Equal(0, picture.Taken);
    }

    [Fact]
    public async Task AStillWithNoLength_StopsJustPastTheCap()
    {
        var endless = new Counted(-1);
        _stub.Map(Proxy, (_, _) => Task.FromResult(Streamed(HttpStatusCode.OK, endless, "image/jpeg")));
        using var client = Client();

        var reply = await Fetch(client);

        Assert.Equal(HaText.PictureTooLarge, reply.Error);
        Assert.Equal(HaCamera.MaxBytes + 1L, endless.Taken);
    }

    [Fact]
    public async Task AStillOfExactlyTheCap_IsRead()
    {
        _stub.Map(Proxy, (_, _) => Task.FromResult(Streamed(HttpStatusCode.OK, new Counted(HaCamera.MaxBytes), "image/jpeg")));
        using var client = Client();

        var reply = await Fetch(client);

        Assert.True(reply.Ok, reply.Error);
        Assert.Equal(HaCamera.MaxBytes, reply.Bytes.Length);
    }

    [Fact]
    public async Task AStillThatStalls_EndsAtTheTimeout_AndTheCallersCancelThrows()
    {
        _stub.Map(Proxy, (_, _) => Task.FromResult(Streamed(HttpStatusCode.OK, new Counted(1000, stall: true), "image/jpeg")));
        using var client = Client();

        var slow = await Fetch(client, seconds: 0.05);
        Assert.StartsWith("Error: Home Assistant did not answer within ", slow.Error);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.CameraAsync("camera.porch", TimeSpan.FromSeconds(30), cts.Token));
    }

    [Fact]
    public async Task AClientDisposedUnderTheFetch_IsUnreachable_AndAClosedPortToo()
    {
        var http = new HttpClient(_stub, disposeHandler: false);
        using var client = new HaClient(new Uri(Server), Token, http);
        http.Dispose();

        Assert.StartsWith("Error: cannot reach Home Assistant at " + Server + ": ", (await Fetch(client)).Error);
        using var closed = Client();
        Assert.StartsWith("Error: cannot reach Home Assistant at " + Server + ": ", (await Fetch(closed)).Error);
    }

    [Fact]
    public async Task AClientReplacedMidRequest_SaysSo_NotThatHomeAssistantWasSlow()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _stub.Map(Proxy, async (_, _) =>
        {
            started.TrySetResult();
            await release.Task;
            throw new TaskCanceledException("the disposed client's own cancel");
        });
        var client = Client();
        var fetch = Fetch(client);
        await started.Task;

        client.Dispose();   // HaSession's swap when the URL or token changes (2026-10-27, the review)
        release.SetResult();

        Assert.Equal(HaText.Unreachable(Server, HaText.ClientReplaced), (await fetch).Error);
    }

    [Fact]
    public void OneOf_NamesAtMostTheCap_ForAMediaPlayerAsForACamera()
    {
        int count = HaSnapshot.MaxCandidates + 1;
        string states = "[" + string.Join(",", Enumerable.Range(1, count).Select(i =>
            "{\"entity_id\":\"media_player.tv_" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\",\"state\":\"on\",\"attributes\":{}}")) + "]";

        var (player, error) = HaMediaTool.Player(Snapshot(states), null);

        Assert.Null(player);
        Assert.StartsWith("Error: 'media player' could be " + count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " things; ", error);
        Assert.EndsWith(" (and 1 more)", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSession_NeedsHomeAssistantSetUp_AndGivesAStillAtLeastFifteenSeconds()
    {
        using var unset = new HaSession(() => new AppSettingsData());
        Assert.Equal(HaText.NotConfigured, (await unset.CameraAsync("camera.porch", CancellationToken.None)).Error);

        Assert.Equal(TimeSpan.FromSeconds(15), HaCamera.TimeoutFor(TimeSpan.FromSeconds(10)));
        Assert.Equal(TimeSpan.FromSeconds(30), HaCamera.TimeoutFor(TimeSpan.FromSeconds(30)));

        byte[] jpeg = StripFixtures.Jpeg();
        _stub.Map(Proxy, (_, _) => Task.FromResult(StubHttpMessageHandler.Bytes(HttpStatusCode.OK, jpeg, "image/jpeg")));
        var settings = new AppSettingsData { HomeAssistantUrl = Server, HomeAssistantToken = Token };
        using var ha = new HaSession(() => settings, (url, token) => new HaClient(url, token, new HttpClient(_stub)));
        Assert.Equal(jpeg, (await ha.CameraAsync("camera.porch", CancellationToken.None)).Bytes);
    }

    // ── the picture ─────────────────────────────────────────────────────────

    [Fact]
    public void TheStill_LosesItsExifAndGps_AndIsMarkedACameraPhoto()
    {
        byte[] exif = ImageFixtures.ExifJpeg();
        Assert.NotEqual(MetadataKinds.None, MetadataStripper.Survey(exif)!.Found & MetadataKinds.Gps);

        var (image, error) = HaCamera.ToAttachment(HaPictureReply.Success(exif, "image/jpeg"), "camera.porch");

        Assert.Null(error);
        Assert.True(image!.Camera);
        Assert.Equal("image/jpeg", image.MediaType);
        Assert.Equal(MetadataKinds.None, MetadataStripper.Survey(image.Bytes)!.Found);
    }

    [Fact]
    public void ABigStill_IsFitted_AsAJpeg()
    {
        var (image, _) = HaCamera.ToAttachment(HaPictureReply.Success(ImageFixtures.Quadrants(3000, 100), "image/bmp"), "camera.porch");

        Assert.Equal(ImageFile.MaxSide, image!.Width);
        Assert.Equal("image/jpeg", image.MediaType);
    }

    [Fact]
    public void AFailedFetch_KeepsItsSentence_AndBytesThatWontDecodeAreSaidSo()
    {
        Assert.Equal(HaText.CameraNoPicture, HaCamera.ToAttachment(HaPictureReply.Failed(HaText.CameraNoPicture, 500), "camera.porch").Error);

        var (image, error) = HaCamera.ToAttachment(HaPictureReply.Success([1, 2, 3, 4], "image/jpeg"), "camera.porch");
        Assert.Null(image);
        Assert.Equal(HaText.PictureUnreadable(ImageLoadFailure.CouldNotRead), error);
    }

    [Theory]
    [InlineData("camera.front_door_live_view", "front_door_live_view")]
    [InlineData("camera.Front Door!", "frontdoor")]
    [InlineData("camera.../..\\x", "x")]
    [InlineData("camera.###", "camera")]
    [InlineData("camera.a123456789_123456789_123456789_123456789_end", "a123456789_123456789_123456789_123456789")]
    public void AFileName_NeverTrustsTheId(string id, string expected) => Assert.Equal(expected, HaCamera.ObjectId(id));

    // ── secrets out of ha_states (step 2) ───────────────────────────────────

    private static string Full(string id, string attributes) =>
        HaText.Full(new HaEntity(id, id, "idle", null, System.Text.Json.JsonDocument.Parse(attributes).RootElement.Clone(), null));

    [Fact]
    public void ACamerasToken_AndItsLinksQueries_StayOutOfItsAttributes()
    {
        string text = Full("camera.front_door_live_view", """
            {"access_token":"abc123tok","video_url":"https://download-a.ring.com/v/7701.mp4?X-Amz-Credential=cred42&X-Amz-Signature=sig999",
             "last_video_id":7701388889539373813,"attribution":"Data provided by Ring.com",
             "entity_picture":"/api/camera_proxy/camera.front_door_live_view?token=abc123tok","friendly_name":"Front Door Live view","supported_features":2}
            """);

        foreach (string secret in new[] { "abc123tok", "cred42", "sig999" })
        {
            Assert.DoesNotContain(secret, text);
        }

        Assert.DoesNotContain("\n" + HaText.AccessToken + ":", text);
        Assert.Contains("\nentity_picture: /api/camera_proxy/camera.front_door_live_view?" + Llm.Tools.ToolRedaction.Placeholder, text);
        Assert.Contains("\nvideo_url: https://download-a.ring.com/v/7701.mp4?" + Llm.Tools.ToolRedaction.Placeholder, text);
        Assert.Contains("\nfriendly_name: Front Door Live view", text);
        Assert.Contains("\nlast_video_id: 7701388889539373813", text);
        Assert.Contains("\nsupported_features: 2", text);
    }

    [Fact]
    public void AMediaPlayersArt_LosesItsQuery_AndOrdinaryAttributesStayAsTheyAre()
    {
        string text = Full("media_player.tv", """
            {"entity_picture":"/api/media_player_proxy/media_player.tv?token=t0k3n&cache=1","media_title":"News at 6","volume_level":0.25,
             "manual":"https://example.com/manual.pdf","source_list":["HDMI 1","HDMI 2"]}
            """);

        Assert.DoesNotContain("t0k3n", text);
        Assert.Contains("\nentity_picture: /api/media_player_proxy/media_player.tv?" + Llm.Tools.ToolRedaction.Placeholder, text);
        Assert.Contains("\nmedia_title: News at 6", text);
        Assert.Contains("\nvolume_level: 0.25", text);
        Assert.Contains("\nmanual: https://example.com/manual.pdf", text);
        Assert.Contains("\nsource_list: [\"HDMI 1\",\"HDMI 2\"]", text);
    }

    [Fact]
    public void ANestedObject_LosesItsSignedLink_ItsToken_ItsPassword_AndAJwt()
    {
        string text = Full("sensor.stream", """
            {"stream":{"url":"https://cdn.example/s.m3u8?Policy=pol77&Key-Pair-Id=kp88","access_token":"nested-tok","password":"pw-123",
             "id":"eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjMifQ.c2lnbmF0dXJl"},"rtsp":"rtsp://admin:hunter2@10.0.0.5/live"}
            """);

        foreach (string secret in new[] { "pol77", "kp88", "nested-tok", "pw-123", "c2lnbmF0dXJl", "hunter2" })
        {
            Assert.DoesNotContain(secret, text);
        }

        Assert.Contains("https://cdn.example/s.m3u8?" + Llm.Tools.ToolRedaction.Placeholder, text);
    }

    [Fact]
    public void ALongAttribute_IsRedactedBeforeItsCut_SoNoHalfTokenShows()
    {
        string value = new string('a', 370) + " https://x.example/p?token=SECRETVALUE";
        string text = Full("sensor.long", "{\"note\":\"" + value + "\"}");

        Assert.DoesNotContain("SECR", text);
        Assert.Equal(HaText.Redacted(value), value[..371] + "https://x.example/p?" + Llm.Tools.ToolRedaction.Placeholder);
    }

    // ── the save ────────────────────────────────────────────────────────────

    private (WorkingDirectory Images, string Root, IDisposable Guard) ImagesUnderAHome()
    {
        string home = Path.Combine(_dir, "home");
        string root = Path.Combine(home, "profiles", "neon", "images");
        var guard = WriteGuard.Configure(new WriteGuardFacts(() => home, () => null, () => [], () => null, () => false, () => null));
        return (new WorkingDirectory(() => root, new ManualTimeProvider(), WorkingDirectoryOptions.Images), root, guard);
    }

    [Fact]
    public void AStill_IsSavedStripped_UnderHomeCameras_WithAClashSuffix()
    {
        var (images, root, guard) = ImagesUnderAHome();
        using var _ = guard;
        var (image, _) = HaCamera.ToAttachment(HaPictureReply.Success(ImageFixtures.ExifJpeg(), "image/jpeg"), "camera.porch");
        var local = new DateTimeOffset(2026, 10, 27, 11, 42, 9, TimeSpan.FromHours(-5));

        var first = HaCamera.Save(images, image!, "camera.porch", local);
        var second = HaCamera.Save(images, image!, "camera.porch", local);

        string folder = Path.Combine(root, HaCamera.FolderName);
        Assert.Null(first.Error);
        Assert.Equal(Path.Combine(folder, "porch-20261027-114209.jpg"), first.Image.Path);
        Assert.Equal(first.Image.Path, first.Saved);
        Assert.Equal(Path.Combine(folder, "porch-20261027-114209-2.jpg"), second.Image.Path);
        Assert.True(first.Image.Camera);
        byte[] kept = File.ReadAllBytes(first.Image.Path);
        Assert.Equal(image!.Bytes, kept);
        Assert.Equal(MetadataKinds.None, MetadataStripper.Survey(kept)!.Found);
    }

    [Fact]
    public void ARefusedSave_IsASentence_AndTheStillIsKeptWithNoFile()
    {
        var (images, root, guard) = ImagesUnderAHome();
        using var _ = guard;
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, HaCamera.FolderName), "a file where the folder goes");
        var (image, _) = HaCamera.ToAttachment(HaPictureReply.Success(StripFixtures.Jpeg(), "image/jpeg"), "camera.porch");

        var save = HaCamera.Save(images, image!, "camera.porch", new DateTimeOffset(2026, 10, 27, 11, 42, 9, TimeSpan.Zero));

        Assert.Null(save.Saved);
        Assert.StartsWith("The picture could not be saved: Error: ", save.Error);
        Assert.Equal("", save.Image.Path);   // no file, and no name a viewer would look for (2026-10-27, the review)
        Assert.Same(image!.Bytes, save.Image.Bytes);
        Assert.True(save.Image.Camera);
    }

    [Fact]
    public void AStillWithEveryNameTaken_SaysSo_AndNeverOffersAnOverwrite()
    {
        var (images, root, guard) = ImagesUnderAHome();
        using var _ = guard;
        string folder = Path.Combine(root, HaCamera.FolderName);
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "porch-20261027-114209.jpg"), [1]);
        for (int n = 2; n <= ImageOutput.MaxNumbered; n++)
        {
            File.WriteAllBytes(Path.Combine(folder, "porch-20261027-114209-" + n.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".jpg"), [1]);
        }

        var (image, _) = HaCamera.ToAttachment(HaPictureReply.Success(StripFixtures.Jpeg(), "image/jpeg"), "camera.porch");

        var save = HaCamera.Save(images, image!, "camera.porch", new DateTimeOffset(2026, 10, 27, 11, 42, 9, TimeSpan.Zero));

        Assert.Equal(HaText.CameraNotSaved(ImageText.NoFreeName("home-cameras/porch-20261027-114209.jpg", ImageOutput.MaxNumbered)), save.Error);
        Assert.DoesNotContain("overwrite", save.Error, StringComparison.Ordinal);
        Assert.Equal(ImageOutput.MaxNumbered, Directory.GetFiles(folder).Length);
    }

    // ── the tool (step 3) ───────────────────────────────────────────────────

    /// <summary>Ring's shapes from step 0: a live view and its last recording (unavailable here), a second camera, and a light.</summary>
    private static string States(string frontDoor = "idle") => $$$"""
        [{"entity_id":"camera.front_door_live_view","state":"{{{frontDoor}}}","attributes":{"friendly_name":"Front Door Live view","access_token":"tok"}},
         {"entity_id":"camera.front_door_last_recording","state":"unavailable","attributes":{"friendly_name":"Front Door Last recording"}},
         {"entity_id":"camera.backyard_live_view","state":"idle","attributes":{"friendly_name":"Backyard Live view"}},
         {"entity_id":"light.porch","state":"on","attributes":{"friendly_name":"Porch"}}]
        """;

    private static HaSnapshot Snapshot(string states) => HaSnapshot.Parse(states, null, ManualTimeProvider.DefaultUtcNow)!;

    /// <summary>The recording-time template's answer: two sensors of the device, the newer one 2026-10-27 15:04:05 UTC.</summary>
    private const string TimeAnswer = "sensor.front_door_last_ding=2026-10-20T01:00:00+00:00;sensor.front_door_last_activity=2026-10-27T15:04:05+00:00;";

    /// <summary>
    /// A session over the stub with <paramref name="states"/> answered in turn (the last one kept), the areas template answered empty and the
    /// time template <paramref name="timeAnswer"/> (null = a 500), and the tool over a guarded images folder in the test zone.
    /// </summary>
    private (HaCameraTool Tool, HaSession Ha, string Folder, IDisposable Guard) Tool(byte[]? still, string? timeAnswer = TimeAnswer, params string[] states)
    {
        int read = 0;
        _stub.Map(Server + "/api/states", (_, _) => Task.FromResult(StubHttpMessageHandler.Json(HttpStatusCode.OK, states[Math.Min(read++, states.Length - 1)])));
        _stub.Map(Server + "/api/template", async (request, ct) =>
        {
            string body = await request.Content!.ReadAsStringAsync(ct);
            return !body.Contains("device_id(", StringComparison.Ordinal) ? StubHttpMessageHandler.Json(HttpStatusCode.OK, "[]", "text/plain")
                : timeAnswer is null ? StubHttpMessageHandler.Json(HttpStatusCode.InternalServerError, "500: Internal Server Error", "text/plain")
                : StubHttpMessageHandler.Json(HttpStatusCode.OK, timeAnswer, "text/plain");
        });
        _stub.Map(Proxy, (_, _) => Task.FromResult(still is null
            ? StubHttpMessageHandler.Json(HttpStatusCode.InternalServerError, "500: Internal Server Error", "text/plain")
            : StubHttpMessageHandler.Bytes(HttpStatusCode.OK, still, "image/jpeg")));
        var settings = new AppSettingsData { HomeAssistantUrl = Server, HomeAssistantToken = Token };
        var ha = new HaSession(() => settings, (url, token) => new HaClient(url, token, new HttpClient(_stub)), new ManualTimeProvider());
        var (images, root, guard) = ImagesUnderAHome();
        return (new HaCameraTool(ha, images, ManualTimeProvider.DefaultZone), ha, Path.Combine(root, HaCamera.FolderName), guard);
    }

    private static async Task<object?> Invoke(HaCameraTool tool, string? target)
    {
        var arguments = new Microsoft.Extensions.AI.AIFunctionArguments();
        if (target is not null)
        {
            arguments[HaCameraTool.TargetArgument] = target;
        }

        return await tool.InvokeAsync(arguments);
    }

    private int ProxyRequests() => _stub.Requests.Count(r => r.Uri.AbsolutePath.StartsWith("/api/camera_proxy/", StringComparison.Ordinal));

    [Theory]
    [InlineData("Front Door Live view", "camera.front_door_live_view")]
    [InlineData("camera.backyard_live_view", "camera.backyard_live_view")]
    [InlineData("front door camera", "camera.front_door_live_view")]   // "camera" from the id; the recording is unavailable, so the live view
    [InlineData("front door", "camera.front_door_live_view")]
    [InlineData("backyard", "camera.backyard_live_view")]
    public void ACamera_IsFoundByName_ById_OrByItsWords_TheAvailableOnePreferred(string target, string id)
    {
        var (camera, error) = HaCameraTool.Camera(Snapshot(States()), target);

        Assert.Null(error);
        Assert.Equal(id, camera!.Id);
    }

    [Fact]
    public void ALight_IsNoCamera_AndATargetLeftOut_NeedsExactlyOne()
    {
        var snapshot = Snapshot(States());

        Assert.Equal(HaText.NotACamera(snapshot.Find("light.porch")!), HaCameraTool.Camera(snapshot, "light.porch").Error);
        Assert.StartsWith("Error: ", HaCameraTool.Camera(snapshot, null).Error);
        Assert.Contains("Backyard Live view", HaCameraTool.Camera(snapshot, null).Error);
        Assert.StartsWith("Error: ", HaCameraTool.Camera(snapshot, "garage").Error);

        var one = Snapshot("""[{"entity_id":"camera.porch","state":"idle","attributes":{"friendly_name":"Porch"}},{"entity_id":"light.den","state":"on","attributes":{}}]""");
        Assert.Equal("camera.porch", HaCameraTool.Camera(one, null).Camera!.Id);
        var none = Snapshot("""[{"entity_id":"light.den","state":"on","attributes":{}}]""");
        Assert.Equal(HaText.NoneOf(HaCameraTool.Domain), HaCameraTool.Camera(none, null).Error);
    }

    [Fact]
    public async Task TheTool_SendsTheStill_SavesIt_AndSaysWhenItsRecordingWas()
    {
        var (tool, ha, folder, guard) = Tool(ImageFixtures.ExifJpeg(), TimeAnswer, States());
        using var _ = guard;
        using var __ = ha;

        var result = Assert.IsType<Llm.Tools.ToolImageResult>(await Invoke(tool, "front door"));

        var image = Assert.Single(result.Images);
        Assert.True(image.Camera);
        Assert.Equal(Path.Combine(folder, "front_door_live_view-20260911-140530.jpg"), image.Path);
        Assert.True(File.Exists(image.Path));
        Assert.Equal(MetadataKinds.None, MetadataStripper.Survey(File.ReadAllBytes(image.Path))!.Found);
        string[] lines = result.Text.Split('\n');
        Assert.Equal($"Front Door Live view (camera.front_door_live_view): a still ({image.Width}x{image.Height}), saved as {image.Path}", lines[0]);
        Assert.Equal("From the latest recording, at Tue 2026-10-27 08:04.", lines[1]);
        Assert.Equal(HaText.SnapshotNote, lines[2]);
        Assert.Equal("The picture is in the next message.", lines[3]);
        Assert.Equal(1, ProxyRequests());
        var timeRequest = Assert.Single(_stub.Requests, r => r.Body?.Contains("device_id(", StringComparison.Ordinal) == true);
        Assert.Contains("camera.front_door_live_view", timeRequest.Body);
    }

    [Fact]
    public async Task ARecordingTimeThatFails_LeavesItsLineOut_AndTheStillGoesAnyway()
    {
        var (tool, ha, _, guard) = Tool(StripFixtures.Jpeg(), timeAnswer: null, States());
        using var _ = guard;
        using var __ = ha;

        var result = Assert.IsType<Llm.Tools.ToolImageResult>(await Invoke(tool, "backyard"));

        Assert.DoesNotContain("From the latest recording", result.Text);
        Assert.Equal(HaText.SnapshotNote, result.Text.Split('\n')[1]);
    }

    [Fact]
    public async Task ACameraWithNoPicture_IsItsSentence_AndNothingIsSaved()
    {
        var (tool, ha, folder, guard) = Tool(still: null, TimeAnswer, States());
        using var _ = guard;
        using var __ = ha;

        Assert.Equal(HaText.CameraNoPicture, await Invoke(tool, "backyard"));
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public async Task AnUnavailableCamera_IsReadAgain_AndFetchedOnceItsBack()
    {
        var (tool, ha, _, guard) = Tool(StripFixtures.Jpeg(), TimeAnswer, States("unavailable"), States());
        using var _ = guard;
        using var __ = ha;

        Assert.IsType<Llm.Tools.ToolImageResult>(await Invoke(tool, "Front Door Live view"));

        Assert.Equal(2, _stub.Requests.Count(r => r.Uri.AbsolutePath == "/api/states"));
        Assert.Equal(1, ProxyRequests());
    }

    [Fact]
    public async Task ACameraStillUnavailable_IsRefused_WithNoFetch()
    {
        var (tool, ha, _, guard) = Tool(StripFixtures.Jpeg(), TimeAnswer, States("unavailable"));
        using var _ = guard;
        using var __ = ha;

        Assert.Equal(HaText.CameraUnavailable("Front Door Live view", "unavailable"), await Invoke(tool, "Front Door Live view"));
        Assert.Equal(2, _stub.Requests.Count(r => r.Uri.AbsolutePath == "/api/states"));
        Assert.Equal(0, ProxyRequests());
    }

    [Fact]
    public void TheTimeTemplate_TakesOnlyACameraId_AndTheNewestTimeWins()
    {
        Assert.Contains("device_id('camera.front_door_live_view')", HaCamera.TimeTemplate("camera.front_door_live_view"));
        Assert.Null(HaCamera.TimeTemplate("camera.x') }}{{ states"));
        Assert.Null(HaCamera.TimeTemplate("light.den"));
        Assert.Null(HaCamera.TimeTemplate("camera.Front"));

        Assert.Equal(new DateTimeOffset(2026, 10, 27, 15, 4, 5, TimeSpan.Zero), HaCamera.RecordingTime(TimeAnswer));
        Assert.Null(HaCamera.RecordingTime(""));
        Assert.Null(HaCamera.RecordingTime("sensor.a=unknown;sensor.b=unavailable;"));
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), HaCamera.RecordingTime("sensor.a=unknown;sensor.b=2026-10-01T00:00:00+00:00"));
    }

    [Fact]
    public void TheRecordingsTime_IsShownInTheZone_WithItsDay()
    {
        var utc = new DateTimeOffset(2026, 10, 27, 15, 4, 5, TimeSpan.Zero);

        Assert.Equal("From the latest recording, at Tue 2026-10-27 08:04.", HaText.CameraRecorded(utc, ManualTimeProvider.DefaultZone));
        Assert.Equal("From the latest recording, at Tue 2026-10-27 15:04.", HaText.CameraRecorded(utc, TimeZoneInfo.Utc));
    }

    [Fact]
    public void TheCamera_IsLeftOut_WithNoCodecs_OrUnderABlindModel_AndSaysWhy()
    {
        var images = new WorkingDirectory(() => _dir, new ManualTimeProvider(), WorkingDirectoryOptions.Images);
        using var ha = new HaSession(() => new AppSettingsData());
        var tools = App.ChatScreen.HomeAssistantTools(ha, null, images: images);

        Assert.Empty(App.ChatScreen.HomeToolNotes(codecs: true, blind: false));
        Assert.Same(tools, App.ChatScreen.HomeToolsFor(tools, App.ChatScreen.HomeToolNotes(codecs: true, blind: false)));

        var noCodecs = App.ChatScreen.HomeToolNotes(codecs: false, blind: false);
        Assert.Equal(App.SystemPromptSummary.NotOffered(HaText.NoPictureSupportReason), Assert.Single(noCodecs, n => n.Key == HaCameraTool.ToolName).Value);
        var blind = App.ChatScreen.HomeToolNotes(codecs: true, blind: true);
        Assert.Equal(App.SystemPromptSummary.NotOffered(App.ToolsText.BlindReason), Assert.Single(blind).Value);

        var offered = App.ChatScreen.HomeToolsFor(tools, blind);
        Assert.Equal(tools.Count - 1, offered.Count);
        Assert.DoesNotContain(HaCameraTool.ToolName, offered.Select(t => t.Name));
    }

    [LiveHomeAssistantFact]
    public async Task Live_ACameraThatHasAPicture_IsFetched_StrippedAndSaved()
    {
        // Reads only: the stills land in this test's own folder, deleted after; a camera with no picture (Ring's with no recording) is passed over.
        var live = new AppSettingsData { HomeAssistantUrl = LiveHomeAssistant.BaseUrl!.AbsoluteUri, HomeAssistantToken = LiveHomeAssistant.Token };
        using var ha = new HaSession(() => live);
        var (images, root, guard) = ImagesUnderAHome();
        using var _ = guard;
        var tool = new HaCameraTool(ha, images);
        var (snapshot, error) = await ha.SnapshotAsync(CancellationToken.None);
        Assert.Null(error);

        var answers = new List<string>();
        foreach (var camera in snapshot!.Of(HaCameraTool.Domain).Where(c => !c.Unavailable))
        {
            var result = await Invoke(tool, camera.Id);
            if (result is Llm.Tools.ToolImageResult taken)
            {
                var image = Assert.Single(taken.Images);
                Assert.True(image.Camera);
                Assert.StartsWith(Path.Combine(root, HaCamera.FolderName), image.Path);
                Assert.Equal(MetadataKinds.None, MetadataStripper.Survey(File.ReadAllBytes(image.Path))!.Found & MetadataKinds.Gps);
                Assert.Contains(HaText.SnapshotNote, taken.Text);
                return;
            }

            answers.Add(camera.Id + ": " + result);
        }

        Assert.Fail("No camera gave a picture:\n" + string.Join("\n", answers));
    }
}
