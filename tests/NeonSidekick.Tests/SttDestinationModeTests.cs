using NeonSidekick.App;
using NeonSidekick.Settings;

namespace NeonSidekick.Tests;

public class SttDestinationModeTests
{
    [Theory]
    [InlineData("chat", SttTarget.Chat)]
    [InlineData(" Draft ", SttTarget.Draft)]
    [InlineData("clipboard", SttTarget.Chat)]   // a hand-edited word: the default
    public void Resolve_ReadsTheSavedWord(string saved, SttTarget target)
    {
        Assert.Equal(target, SttDestinationMode.Resolve(new AppSettingsData { SttDestination = saved }));
        Assert.Equal("chat", new AppSettingsData().SttDestination);   // what voice input did before the setting (2026-10-02)
        Assert.All(SttDestinationMode.Names, name => Assert.NotEmpty(SttDestinationMode.Describe(name)));
    }

    [Theory]
    [InlineData("", "hello", "hello")]
    [InlineData("fix this", "hello", "fix this hello")]
    [InlineData("fix this ", "hello", "fix this hello")]
    [InlineData("first line\n", "hello", "first line\nhello")]
    [InlineData("tab\t", "hello", "tab\thello")]
    public void AppendToDraft_ASpaceBetween_OnlyWhenNeeded(string draft, string spoken, string expected) =>
        Assert.Equal(expected, SttDestinationMode.AppendToDraft(draft, spoken));

    [Theory]
    [InlineData(ConsoleKey.F4, true)]
    [InlineData(ConsoleKey.F8, true)]
    [InlineData(ConsoleKey.Insert, true)]
    [InlineData(ConsoleKey.Home, false)]
    [InlineData(ConsoleKey.End, false)]
    [InlineData(ConsoleKey.PageUp, false)]
    [InlineData(ConsoleKey.PageDown, false)]
    public void PushToTalkOverDraft_NotTheKeysTheEditorMovesWith(ConsoleKey key, bool over) =>
        Assert.Equal(over, SttDestinationMode.PushToTalkOverDraft(key));
}
