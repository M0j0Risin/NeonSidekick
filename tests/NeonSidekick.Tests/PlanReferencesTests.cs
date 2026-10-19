using Microsoft.Extensions.AI;
using NeonSidekick.Files;
using NeonSidekick.Llm;
using NeonSidekick.Plans;
using NeonSidekick.Sessions;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// Plan reference pictures (2026-10-18, <see cref="PlanReferences"/>): the conversation's pictures, saved beside the plan once each, listed under
/// <c>## References</c>, and loaded again for a run — the step's own, else all — within the budget; never for a model that cannot see them.
/// </summary>
public sealed class PlanReferencesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();
    private readonly WorkingDirectory _files;
    private const string PlanPath = ".neon/plans/guide.md";

    private static readonly byte[] Red = FakeHeadlessBrowser.TinyPng;
    private static readonly byte[] Other = [.. FakeHeadlessBrowser.TinyPng, 0];   // other bytes, so another picture

    public PlanReferencesTests()
    {
        _files = new WorkingDirectory(() => _dir, _time);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static ChatMessage UserWith(params AIContent[] parts) => new(ChatRole.User, parts.ToList());

    private static DataContent Picture(byte[] bytes, string? path = null, string? key = null)
    {
        var part = new DataContent(bytes, "image/png");
        if (path is not null || key is not null)
        {
            part.AdditionalProperties = new AdditionalPropertiesDictionary { [ConversationHistory.PathKey] = path ?? "x.png" };
            if (key is not null)
            {
                part.AdditionalProperties[key] = path ?? "x.png";
            }
        }

        return part;
    }

    [Fact]
    public void FromHistory_TakesTheUsersPictures_AndLeavesTheRestOut()
    {
        var carrier = UserWith(Picture(Red));
        carrier.AdditionalProperties = new AdditionalPropertiesDictionary { [ConversationHistory.CarrierKey] = true };
        var messages = new List<ChatMessage>
        {
            UserWith(new TextContent("look"), Picture(Red, "pasted.png")),
            carrier,                                                                  // a view_image carrier
            new(ChatRole.Assistant, [Picture(Other)]),                                // not the user's
            UserWith(Picture(Other, "cam.jpg", ConversationHistory.CameraKey)),      // the camera, not kept
            UserWith(Picture(Other, "shot.png", ConversationHistory.ScreenKey)),     // a screenshot, not kept
            UserWith(Picture(Other, ".neon/plans/x/ref-1.png", ConversationHistory.ReferenceKey)),   // a reference the app attached
            UserWith(new DataContent(new byte[] { 1 }, PdfFile.MediaType)),         // a PDF
            UserWith(new TextContent(PictureBudget.LeftOut("old.png"))),             // pruned: read again
        };

        var found = PlanReferences.FromHistory(messages, keepCamera: false, keepScreen: false, path => path == "old.png" ? new PlanPicture(Other, "image/png", path) : null);

        Assert.Equal(["pasted.png", "old.png"], found.Select(p => p.Source));
        Assert.Equal(4, PlanReferences.FromHistory(messages, keepCamera: true, keepScreen: true, path => new PlanPicture(Other, "image/png", path)).Count);
    }

    [Fact]
    public void FromHistory_ReadsNoCameraOrScreenPictureAgain_ThatACompactionTookOut()
    {
        // 2026-10-19, the review: the stand-in lost the marks, and a photo the keep settings leave out was read from disk and saved beside the plan.
        var messages = new List<ChatMessage>
        {
            UserWith(PictureBudget.LeftOutPart(Picture(Other, "cam.jpg", ConversationHistory.CameraKey))),
            UserWith(PictureBudget.LeftOutPart(Picture(Other, "shot.png", ConversationHistory.ScreenKey))),
            UserWith(PictureBudget.LeftOutPart(Picture(Other, ".neon/plans/x/ref-1.png", ConversationHistory.ReferenceKey))),
            UserWith(PictureBudget.LeftOutPart(Picture(Red, "pasted.png"))),
        };
        Func<string, PlanPicture?> reload = path => new PlanPicture(Other, "image/png", path);

        Assert.Equal(["pasted.png"], PlanReferences.FromHistory(messages, keepCamera: false, keepScreen: false, reload).Select(p => p.Source));
        Assert.Equal(["cam.jpg", "shot.png", "pasted.png"], PlanReferences.FromHistory(messages, keepCamera: true, keepScreen: true, reload).Select(p => p.Source));
        Assert.Equal(PictureBudget.LeftOut("cam.jpg"), ((TextContent)messages[0].Contents[0]).Text);
    }

    [Fact]
    public void Sync_SavesEachPictureOnce_NumberedOnward_AndNamesFollowsIt()
    {
        var pictures = new[] { new PlanPicture(Red, "image/png", null), new PlanPicture(Red, "image/png", null), new PlanPicture(Other, "image/jpeg", null) };
        Assert.Equal(["ref-1", "ref-2"], PlanReferences.Names(_files, null, pictures));

        var saved = PlanReferences.Sync(_files, PlanPath, pictures);

        Assert.Equal([new PlanReference("ref-1", ".neon/plans/guide/ref-1.png"), new PlanReference("ref-2", ".neon/plans/guide/ref-2.jpg")], saved);
        Assert.Equal(Red, File.ReadAllBytes(Path.Combine(_dir, ".neon", "plans", "guide", "ref-1.png")));
        // Again with a third: the first two kept, the new one numbered after them.
        byte[] third = [.. Other, 1];
        var again = PlanReferences.Sync(_files, PlanPath, [.. pictures, new PlanPicture(third, "image/png", null)]);
        Assert.Equal(["ref-1", "ref-2", "ref-3"], again.Select(r => r.Name));
        Assert.Equal(["ref-1", "ref-2", "ref-3"], PlanReferences.Names(_files, PlanPath, [new PlanPicture(Red, "image/png", null), new PlanPicture(Other, "image/png", null), new PlanPicture(third, "image/png", null)]));
        Assert.Equal(["ref-4"], PlanReferences.Names(_files, PlanPath, [new PlanPicture([9, 9], "image/png", null)]));
        Assert.Equal(".neon/plans/guide", PlanReferences.FolderOf(PlanPath));
    }

    [Fact]
    public void ReconcileSection_KeepsTheModelsLines_AndAppendsWhatItLeftOut()
    {
        var saved = new[] { new PlanReference("ref-1", ".neon/plans/guide/ref-1.png"), new PlanReference("ref-2", ".neon/plans/guide/ref-2.png") };

        string listed = PlanReferences.ReconcileSection("# G\n\n## References\n- ref-1: the login screen\n- `docs/mock.png`: the mock\n\n## Risks\n- none", saved);
        Assert.Equal("# G\n\n## References\n- ref-1: the login screen\n- `docs/mock.png`: the mock\n- ref-2 (`.neon/plans/guide/ref-2.png`): a picture from the conversation\n\n## Risks\n- none", listed);

        string added = PlanReferences.ReconcileSection("# G\n\n## Steps\n- [ ] a\n", saved);
        Assert.EndsWith("## Steps\n- [ ] a\n\n## References\n- ref-1 (`.neon/plans/guide/ref-1.png`): a picture from the conversation\n- ref-2 (`.neon/plans/guide/ref-2.png`): a picture from the conversation", added);

        Assert.Equal("# G\n", PlanReferences.ReconcileSection("# G\n", []));
        Assert.Equal([("ref-1", null), (null, "docs/mock.png"), ("ref-2", null)], PlanReferences.Entries(listed).Select(e => (e.Ref, e.Path)));
    }

    [WindowsFact]   // the pictures decode through WIC
    public void ForRun_LoadsTheStepsOwnPictures_ElseAll_MarkedAsReferences()
    {
        PlanReferences.Sync(_files, PlanPath, [new PlanPicture(Red, "image/png", null), new PlanPicture(Other, "image/png", null)]);
        string body = "# G\n\n## Steps\n- [ ] Use the second (ref-2)\n- [ ] Anything\n\n## References\n- ref-1: one\n- ref-2: two\n";
        var steps = PlanSteps.Parse(body);

        var named = PlanReferences.ForRun(_files, PlanPath, body, steps[0], keep: 20, megabytes: 24, canSee: true, out string? none);
        var all = PlanReferences.ForRun(_files, PlanPath, body, steps[1], keep: 20, megabytes: 24, canSee: true, out _);
        var capped = PlanReferences.ForRun(_files, PlanPath, body, null, keep: 1, megabytes: 24, canSee: true, out _);
        var blind = PlanReferences.ForRun(_files, PlanPath, body, null, keep: 20, megabytes: 24, canSee: false, out string? unseen);

        Assert.Null(none);
        Assert.Equal([".neon/plans/guide/ref-2.png"], named.Select(i => i.Path));
        Assert.All(named, i => Assert.True(i.Reference));
        Assert.Equal([".neon/plans/guide/ref-1.png", ".neon/plans/guide/ref-2.png"], all.Select(i => i.Path));
        Assert.Single(capped);
        Assert.Empty(blind);
        Assert.Equal(PlanText.ReferencesUnseenNotice(2), unseen);
        Assert.Equal(".neon/plans/guide/ref-2.png", ConversationHistory.ReferencePath(ConversationHistory.ImagePart(named[0])));
    }

    [Fact]
    public void AStoredSession_KeepsAReferencesStandIn_NotItsBytes()
    {
        var image = new ImageAttachment(".neon/plans/guide/ref-1.png", Red, "image/png", 1, 1) { Reference = true };
        var history = new ConversationHistory("");
        history.AddUser("go", [image]);

        string json = SessionHistory.ToJson(history.Messages);

        Assert.Contains(PlanText.ReferenceNotKept(".neon/plans/guide/ref-1.png").Replace("\"", "\\u0022", StringComparison.Ordinal), json.Replace("\"", "\\u0022", StringComparison.Ordinal));
        Assert.DoesNotContain(Convert.ToBase64String(Red), json);
        Assert.Equal("(reference picture .neon/plans/guide/ref-1.png, kept beside the plan)", PlanText.ReferenceNotKept(".neon/plans/guide/ref-1.png"));
        Assert.Equal(PlanText.Glyph + " 2 reference pictures attached.", PlanText.ReferencesAttachedNotice(2));
    }

    [Fact]
    public void ANewPlansName_StepsPastAFolderLeftBehind()
    {
        Directory.CreateDirectory(Path.Combine(_dir, ".neon", "plans", "guide"));   // pictures of a plan whose file went
        var session = new PlanSession();
        session.Enter("Add a guide");

        var (saved, _) = PlanFiles.Save(session, _files, _time, "Guide", "# Guide\n- [ ] a\n", "guide");

        Assert.Equal(".neon/plans/guide-2.md", saved!.Path);
    }
}
