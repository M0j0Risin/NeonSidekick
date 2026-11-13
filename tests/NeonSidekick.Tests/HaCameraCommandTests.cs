using NeonSidekick.App;
using NeonSidekick.HomeAssistant;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// <c>/ha camera [&lt;name&gt;]</c>'s pure parts (2026-11-13, the user's ask: a camera's still the way a 📹 chip's double-click opens it, for any
/// camera): the parse, the chips' aliases as names, and the argument list.
/// </summary>
public class HaCameraCommandTests
{
    private static HaSnapshot Snapshot() => HaSnapshot.Parse("""
        [{"entity_id":"camera.porch","state":"idle","attributes":{"friendly_name":"Porch Camera"}},
         {"entity_id":"camera.garage","state":"unavailable","attributes":{"friendly_name":"Garage Camera"}},
         {"entity_id":"camera.yard","state":"idle","attributes":{"friendly_name":"Yard"}},
         {"entity_id":"light.den","state":"on","attributes":{"friendly_name":"Den"}}]
        """, null, ManualTimeProvider.DefaultUtcNow)!;

    [Fact]
    public void TheWord_IsPinned() => Assert.Equal("camera", HaCameraCommand.Word);

    [Theory]
    [InlineData("camera", null)]
    [InlineData("CAMERA", null)]
    [InlineData("camera   ", null)]
    [InlineData("  CAMERA  Front Door ", "Front Door")]
    [InlineData("Camera camera.porch", "camera.porch")]
    public void TryParse_TakesTheWord_InAnyCase_AndABareOneHasNoTarget(string args, string? target)
    {
        Assert.True(HaCameraCommand.TryParse(args, out string? parsed));
        Assert.Equal(target, parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("cameras")]
    [InlineData("stills view")]
    [InlineData("on camera")]
    public void TryParse_LeavesEveryOtherHaForm(string args) => Assert.False(HaCameraCommand.TryParse(args, out _));

    [Fact]
    public void AChipsAlias_NamesItsCamera_AnyCase_ButNotAShared_AnEmpty_OrAnotherDomainsOne()
    {
        var aliases = new Dictionary<string, string>
        {
            ["entity:camera.porch"] = "Door",
            ["entity:light.den"] = "Lamp",
            ["entity:camera.garage"] = "Back",
            ["entity:camera.yard"] = "Back",
            ["entity:camera.side"] = "",   // the emoji alone: no name
            ["area:den"] = "Den",
        };

        Assert.Equal("camera.porch", HomeBarItems.AliasedCamera(" door ", aliases));
        Assert.Null(HomeBarItems.AliasedCamera("Lamp", aliases));
        Assert.Null(HomeBarItems.AliasedCamera("Back", aliases));   // two cameras share it: Home Assistant's names decide
        Assert.Null(HomeBarItems.AliasedCamera("", aliases));
        Assert.Null(HomeBarItems.AliasedCamera(null, aliases));
        Assert.Null(HomeBarItems.AliasedCamera("Door", null));
        Assert.Null(HomeBarItems.AliasedCamera("Door", new Dictionary<string, string>()));

        Assert.Equal([("Door", "camera.porch"), ("Back", "camera.garage"), ("Back", "camera.yard")], HomeBarItems.CameraAliases(aliases));
        Assert.Empty(HomeBarItems.CameraAliases(null));
    }

    [Fact]
    public void Complete_OffersTheWord_ThenTheAliases_ThenTheCameras()
    {
        var snapshot = Snapshot();

        Assert.Contains("camera", HaCommand.Complete(snapshot, "").Select(i => i.Text));
        Assert.Equal(["camera"], HaCommand.Complete(null, "ca").Select(i => i.Text));
        Assert.Empty(HaCommand.Complete(null, "camera "));   // no snapshot yet: nothing to offer

        IReadOnlyList<(string, string)> aliases = [("Door", "camera.porch"), ("Gone", "camera.removed"), ("Yard", "camera.porch")];
        var items = HaCommand.Complete(snapshot, "camera ", cameraAliases: aliases);
        Assert.Equal(["camera Door", "camera Yard", "camera Porch Camera", "camera Garage Camera"], items.Select(i => i.Text));
        Assert.Equal("Porch Camera", items[0].Note);
        Assert.Equal("Porch Camera", items[1].Note);   // an alias equal to another camera's name: once, as the camera Enter opens
        Assert.Equal(HaText.CameraItemNote(snapshot.Find("camera.garage")!), items[3].Note);
        Assert.Contains("unavailable", items[3].Note, StringComparison.Ordinal);
        Assert.Equal(["camera Porch Camera"], HaCommand.Complete(snapshot, "camera po").Select(i => i.Text));
    }

    /// <summary>
    /// The review of 2026-11-13: an alias two cameras share isn't offered (Enter would take neither), an alias on an unavailable camera says
    /// so, a name the resolve would split as two completes to its id, and the cameras' rooms follow them.
    /// </summary>
    [Fact]
    public void Complete_LeavesASharedAliasOut_MarksAnUnavailableOne_OffersASplitNameByItsId_ThenTheRooms()
    {
        var snapshot = HaSnapshot.Parse("""
            [{"entity_id":"camera.porch","state":"idle","attributes":{"friendly_name":"Porch Camera"}},
             {"entity_id":"camera.garage","state":"unavailable","attributes":{"friendly_name":"Garage Camera"}},
             {"entity_id":"camera.side","state":"idle","attributes":{"friendly_name":"Front and Side Yard"}}]
            """, """[{"id":"porch","name":"Porch","entities":["camera.porch"]}]""", ManualTimeProvider.DefaultUtcNow)!;
        IReadOnlyList<(string, string)> aliases = [("Back", "camera.porch"), ("back", "camera.garage"), ("Shed", "camera.garage")];

        var items = HaCommand.Complete(snapshot, "camera ", cameraAliases: aliases);

        Assert.Equal(["camera Shed", "camera Porch Camera", "camera Garage Camera", "camera camera.side", "camera Porch"], items.Select(i => i.Text));
        Assert.Equal(HaText.CameraAliasNote(snapshot.Find("camera.garage")!), items[0].Note);
        Assert.Contains("unavailable", items[0].Note, StringComparison.Ordinal);
        Assert.StartsWith("Front and Side Yard", items[3].Note, StringComparison.Ordinal);
        Assert.Equal("room", items[4].Note);
    }

    [Fact]
    public void SplitVerb_IsTheLowercasedVerb_AndTheTrimmedWords()
    {
        Assert.Equal(("camera", "Front Door"), HaCommand.SplitVerb("  CAMERA   Front Door "));
        Assert.Equal(("camera", ""), HaCommand.SplitVerb("camera"));
        Assert.Equal(("", ""), HaCommand.SplitVerb("   "));
    }

    [Fact]
    public void ThePagesUsage_LeavesOutTheFormsThePageRefuses()
    {
        Assert.DoesNotContain("/ha camera", HaText.PageUsage, StringComparison.Ordinal);
        Assert.DoesNotContain("/ha stills", HaText.PageUsage, StringComparison.Ordinal);
        Assert.StartsWith(HaText.PageUsage, HaText.Usage, StringComparison.Ordinal);
    }

    [Fact]
    public void CameraId_IsTheCameraEntitysId_OnlyForACamera()
    {
        Assert.Equal("camera.porch", HomeBarItems.CameraId("entity:camera.porch"));
        Assert.Null(HomeBarItems.CameraId("entity:light.den"));
        Assert.Null(HomeBarItems.CameraId("area:porch"));
        Assert.Null(HomeBarItems.CameraId(null));
    }
}
