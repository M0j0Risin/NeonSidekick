using NeonSidekick.Web;

namespace NeonSidekick.Tests;

public class ResultReferersTests
{
    private static readonly Uri Ddg = DuckDuckGoSearch.Origin;

    [Fact]
    public void For_IsTheResultsPage_AHitWasListedOn_ByItsUrl()
    {
        var referers = new ResultReferers();
        referers.Remember([new SearchResult("A", "https://a.example/page", ""), new SearchResult("Bad", "not a url", ""), new SearchResult("Bare", "b.example/x", "")], Ddg);

        Assert.Equal(Ddg, referers.For(new Uri("https://a.example/page")));
        Assert.Equal(Ddg, referers.For(new Uri("https://b.example/x")));
        Assert.Null(referers.For(new Uri("https://a.example/other")));
    }

    [Fact]
    public void TheOldestHits_GoFirst_PastTheCapacity()
    {
        var referers = new ResultReferers();
        referers.Remember(Enumerable.Range(0, ResultReferers.Capacity + 5).Select(i => new SearchResult("", $"https://e.example/{i}", "")), Ddg);

        Assert.Null(referers.For(new Uri("https://e.example/4")));
        Assert.Equal(Ddg, referers.For(new Uri("https://e.example/5")));
        Assert.Equal(Ddg, referers.For(new Uri($"https://e.example/{ResultReferers.Capacity + 4}")));
    }

    [Fact]
    public void AHitListedAgain_MovesToTheBack()
    {
        // Code review, 2026-10-11: a hit listed again kept its first place, and the latest search's hit went first.
        var referers = new ResultReferers();
        var engine = new Uri("https://search.example/");
        referers.Remember([new SearchResult("X", "https://x.example/", "")], Ddg);
        referers.Remember(Enumerable.Range(0, ResultReferers.Capacity - 1).Select(i => new SearchResult("", $"https://e.example/{i}", "")), Ddg);
        referers.Remember([new SearchResult("X", "https://x.example/", "")], engine);
        referers.Remember([new SearchResult("", "https://e.example/new", "")], Ddg);

        Assert.Equal(engine, referers.For(new Uri("https://x.example/")));
        Assert.Null(referers.For(new Uri("https://e.example/0")));
        Assert.Equal(Ddg, referers.For(new Uri("https://e.example/new")));
    }
}
