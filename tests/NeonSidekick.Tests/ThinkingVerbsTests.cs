using NeonSidekick.App;

namespace NeonSidekick.Tests;

public sealed class ThinkingVerbsTests
{
    /// <summary>
    /// The list is the asset's (2026-10-12, the user's ask: <c>assets/verbs/verbs.txt</c>, edited by hand), so the pins are its
    /// shape, never its words: not empty, lowercase letters only, no repeats, never the plain spinner word.
    /// </summary>
    [Fact]
    public void All_IsLowercaseDistinctAndNotThePlainWord()
    {
        Assert.NotEmpty(ThinkingVerbs.All);
        Assert.All(ThinkingVerbs.All, v => Assert.Matches("^[a-z]+$", v));
        Assert.Equal(ThinkingVerbs.All.Length, ThinkingVerbs.All.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain(ChatScreen.ThinkingLabel, ThinkingVerbs.All);
        Assert.DoesNotContain(TurnStages.WritingLabel, ThinkingVerbs.All);
    }

    /// <summary>The embedded list is the repository's file as <see cref="ThinkingVerbs.Parse"/> reads it: the build can never ship another.</summary>
    [Fact]
    public void All_IsTheAssetFile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NeonSidekick.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        string asset = File.ReadAllText(Path.Combine(dir.FullName, "assets", "verbs", "verbs.txt"));
        Assert.Equal(ThinkingVerbs.Parse(asset), ThinkingVerbs.All);
    }

    [Fact]
    public void Parse_TrimsSkipsBlanksAndRepeats_KeepingTheOrder()
    {
        Assert.Equal(["zipping", "bellowing", "giggling"], ThinkingVerbs.Parse("zipping\r\n  bellowing \n\n\t\ngiggling\nzipping\n"));
        Assert.Equal(["one"], ThinkingVerbs.Parse("one"));
        Assert.Empty(ThinkingVerbs.Parse(""));
        Assert.Empty(ThinkingVerbs.Parse("\r\n \n"));
        Assert.Throws<ArgumentNullException>(() => ThinkingVerbs.Parse(null!));
    }

    [Fact]
    public void Pick_DrawsFromTheList_AndTheSameSeedDrawsTheSameVerb()
    {
        var random = new Random(11);
        for (int i = 0; i < 200; i++)
        {
            Assert.Contains(ThinkingVerbs.Pick(random), ThinkingVerbs.All);
        }

        Assert.Equal(ThinkingVerbs.Pick(new Random(7)), ThinkingVerbs.Pick(new Random(7)));
        Assert.Throws<ArgumentNullException>(() => ThinkingVerbs.Pick(null!));
    }

    [Fact]
    public void Pick_WithAnExclusion_NeverDrawsIt_AndNullExcludesNothing()
    {
        string excluded = ThinkingVerbs.All[^1];
        var random = new Random(3);
        for (int i = 0; i < 500; i++)
        {
            Assert.NotEqual(excluded, ThinkingVerbs.Pick(random, excluded));
        }

        // The same seed, the same draw when nothing is excluded — and the first draw again when
        // the exclusion is another word.
        Assert.Equal(ThinkingVerbs.Pick(new Random(7)), ThinkingVerbs.Pick(new Random(7), null));
        string first = ThinkingVerbs.Pick(new Random(7))!;
        Assert.Equal(first, ThinkingVerbs.Pick(new Random(7), first == excluded ? ThinkingVerbs.All[0] : excluded));
        Assert.Throws<ArgumentNullException>(() => ThinkingVerbs.Pick(null!, excluded));
    }

    /// <summary>An edited file of one verb, or none, never breaks the draw (2026-10-12): the one verb whatever is excluded, else null.</summary>
    [Fact]
    public void Pick_OverOneVerbOrNone_NeverLoops()
    {
        Assert.Equal("solo", ThinkingVerbs.Pick(["solo"], new Random(1), "solo"));
        Assert.Equal("solo", ThinkingVerbs.Pick(["solo"], new Random(1)));
        Assert.Null(ThinkingVerbs.Pick([], new Random(1), "solo"));
        Assert.Null(ThinkingVerbs.Pick([], new Random(1)));
        Assert.Equal("b", ThinkingVerbs.Pick(["a", "b"], new Random(1), "a"));
    }
}
