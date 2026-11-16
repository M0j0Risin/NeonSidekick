using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// A session hit's snippet for the Web UI's Sessions tab (2026-11-16, plan lucid-roaming-lantern #40): the marks read and taken out, the saved
/// paths cleaned, the match found again in what is left, a window around it with … at a cut that never splits a label; a stray marker is text.
/// </summary>
public sealed class WebSnippetTests
{
    private static string Marked(string text, string word) => text.Replace(word, WebSnippet.Open + word + WebSnippet.Close, StringComparison.Ordinal);

    private static string Joined(IReadOnlyList<WebSnippetPart> parts) => string.Concat(parts.Select(p => p.Match ? "<" + p.Text + ">" : p.Text));

    [Fact]
    public void AShortText_IsWhole_WithItsMatchFlagged()
    {
        Assert.Equal("where are the <taxes>?", Joined(WebSnippet.Parts(Marked("where are the taxes?", "taxes"))));
    }

    [Fact]
    public void ALongText_IsCutAroundTheMatch_WithEllipses()
    {
        string text = string.Join(' ', Enumerable.Range(1, 40).Select(i => "w" + i));
        var parts = WebSnippet.Parts(Marked(text, "w20"));
        string joined = Joined(parts);
        Assert.StartsWith("…", joined, StringComparison.Ordinal);
        Assert.EndsWith("…", joined, StringComparison.Ordinal);
        Assert.Contains("<w20>", joined, StringComparison.Ordinal);
        Assert.Equal(12, joined.Trim('…').Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public void ASavedPicturesPath_NeverShows_AndAMatchInsideItIsGone()
    {
        string sent = @"[Image #1, saved as C:\Users\someone\secretfolder\images\pasted-1.png] what is in this picture";
        var byFolder = WebSnippet.Parts(sent.Replace("secretfolder", WebSnippet.Open + "secretfolder" + WebSnippet.Close, StringComparison.Ordinal));
        Assert.DoesNotContain(byFolder, p => p.Text.Contains("secretfolder", StringComparison.Ordinal) || p.Text.Contains(@"C:\", StringComparison.Ordinal));
        Assert.DoesNotContain(byFolder, p => p.Match);

        var beside = WebSnippet.Parts(Marked(sent, "picture"));
        Assert.Equal("[Image #1] what is in this <picture>", Joined(beside));
    }

    [Fact]
    public void AWindowNeverCutsALabel()
    {
        string text = "one two three four five six seven [PDF #2 quarterly report.pdf] eight nine ten eleven twelve thirteen fourteen fifteen sixteen match";
        string joined = Joined(WebSnippet.Parts(Marked(text, "match")));
        Assert.StartsWith("…[PDF #2 quarterly report.pdf] eight", joined, StringComparison.Ordinal);   // the window began inside it
        Assert.EndsWith("<match>", joined, StringComparison.Ordinal);
    }

    [Fact]
    public void AStrayMarker_IsShownAsText()
    {
        var parts = WebSnippet.Parts("odd " + WebSnippet.Open + " text and " + WebSnippet.Open + "match" + WebSnippet.Close);
        Assert.Equal("odd " + WebSnippet.Open + " text and <match>", Joined(parts));
    }
}
