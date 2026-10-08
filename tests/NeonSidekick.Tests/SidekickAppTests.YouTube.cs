using NeonSidekick.YouTube;

namespace NeonSidekick.Tests;

/// <summary>The saved videos outside the screen (2026-10-08): headless <c>/youtube saved --clear</c> asks nothing.</summary>
public partial class SidekickAppTests
{
    [Fact]
    public async Task Headless_YouTubeSavedClear_EmptiesTheList_WithoutAsking()
    {
        var library = new YouTubeLibrary(_settings.ProfileDirectory);
        library.Add("aqz-KE-bpKQ", "Big Buck Bunny", "Blender");
        library.Add("jNQXAC9IVRw", "Me at the zoo", "jawed");

        string output = await Headless("/youtube saved --clear\n/youtube saved --clear\n");

        Assert.Contains(YouTubeText.Cleared(2) + Environment.NewLine, output);
        Assert.Contains(YouTubeText.NoneSaved + Environment.NewLine, output);   // the second finds nothing
        Assert.DoesNotContain("[error]", output);
        Assert.Empty(new YouTubeLibrary(_settings.ProfileDirectory).List());
    }
}
