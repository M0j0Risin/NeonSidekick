using NeonSidekick.App;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.Tests;

/// <summary>A headless <c>--private</c> run (2026-11-26, the review's fixes): its scope owns the switches, and lets them go at the end.</summary>
public partial class SidekickAppTests
{
    /// <summary>The run is private while it runs and leaves the switches off after; the crash hide is latched when the log hid its words.</summary>
    [Fact]
    public async Task APrivateRun_LeavesTheSwitchesOff_AndLatchesTheCrashHide()
    {
        try
        {
            await Headless("", options: SidekickOptions.None with { Private = true });

            Assert.False(PrivateMode.Active);
            Assert.False(PrivateMode.HideLog);
            Assert.True(PrivateMode.HidAtExit);   // Private log text hide, the default
            Assert.True(PrivateMode.HideCrash);
        }
        finally
        {
            PrivateMode.Active = false;
            PrivateMode.HideLog = false;
            PrivateMode.HidAtExit = false;
        }
    }
}
