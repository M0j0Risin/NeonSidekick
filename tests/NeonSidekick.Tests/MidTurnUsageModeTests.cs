using NeonSidekick.App;
using NeonSidekick.Settings;

namespace NeonSidekick.Tests;

public class MidTurnUsageModeTests
{
    [Theory]
    [InlineData("estimate", MidTurnUsage.Estimate)]
    [InlineData(" Last-Known ", MidTurnUsage.LastKnown)]
    [InlineData("live", MidTurnUsage.LastKnown)]   // a hand-edited word: the default (last-known since later on 2026-09-25)
    public void Resolve_ReadsTheSavedWord(string saved, MidTurnUsage mode)
    {
        Assert.Equal(mode, MidTurnUsageMode.Resolve(new AppSettingsData { LlmMidTurnUsage = saved }));
        Assert.Equal("last-known", new AppSettingsData().LlmMidTurnUsage);
        Assert.All(MidTurnUsageMode.Names, name => Assert.NotEmpty(MidTurnUsageMode.Describe(name)));
    }
}
