using NeonSidekick.Viewer;

namespace NeonSidekick.Tests;

/// <summary>Video files headless (2026-11-12, the video-files plan): <c>/video</c> answers that it needs the screen, whatever its words.</summary>
public partial class SidekickAppTests
{
    [Fact]
    public async Task Headless_Video_NeedsTheScreen()
    {
        string output = await Headless("/video clip.mp4\n/video status\n");

        Assert.Equal(2, output.Split("[error] " + VideoText.NeedsScreen).Length - 1);
    }
}
