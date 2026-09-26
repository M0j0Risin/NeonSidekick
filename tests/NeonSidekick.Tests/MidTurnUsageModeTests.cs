using NeonSidekick.App;
using NeonSidekick.Settings;

namespace NeonSidekick.Tests;

public class MidTurnUsageModeTests
{
    [Theory]
    [InlineData("estimate", MidTurnUsage.Estimate)]
    [InlineData(" Last-Known ", MidTurnUsage.LastKnown)]
    [InlineData("live", MidTurnUsage.Estimate)]   // a hand-edited word: the default
    public void Resolve_ReadsTheSavedWord(string saved, MidTurnUsage mode)
    {
        Assert.Equal(mode, MidTurnUsageMode.Resolve(new AppSettingsData { LlmMidTurnUsage = saved }));
        Assert.Equal("estimate", new AppSettingsData().LlmMidTurnUsage);
        Assert.All(MidTurnUsageMode.Names, name => Assert.NotEmpty(MidTurnUsageMode.Describe(name)));
    }
}
