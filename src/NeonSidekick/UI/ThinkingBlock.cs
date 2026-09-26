using NeonSidekick.UI.Markdown;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace NeonSidekick.UI;

/// <summary>
/// The model's thinking in the pane's live slot (2026-09-26, the user's ask): the header
/// <see cref="ThinkingFoldText.LiveHeader"/>, then the text so far — dim italic on the code block's
/// panel fill, under a tool line's indent — the whole of it every time, as a <see cref="ReplyBlock"/>
/// is. Plain text, not markdown: thinking is a draft, and half a table or fence mid-stream would jump
/// about. When the pane commits it, the header is the summary of a thinking group
/// (<see cref="Scrollback.BeginThinkingGroup"/>) and the rows its members, so it folds to
/// <see cref="ThinkingFoldText.Summary"/> with <see cref="Elapsed"/> the moment it ends.
/// </summary>
public sealed class ThinkingBlock : IRenderable
{
    /// <summary>The body's indent ahead of the panel fill: a tool line's, so the fill starts under the triangle.</summary>
    public const string Indent = "  ";

    private IRenderable? _content;

    public ThinkingBlock(string text, TimeSpan? elapsed = null)
    {
        Text = text ?? throw new ArgumentNullException(nameof(text));
        Elapsed = elapsed;
    }

    public string Text { get; }

    /// <summary>How long the thinking streamed; null while it still does.</summary>
    public TimeSpan? Elapsed { get; }

    public Measurement Measure(RenderOptions options, int maxWidth) => Content.Measure(options, maxWidth);

    public IEnumerable<Segment> Render(RenderOptions options, int maxWidth) => Content.Render(options, maxWidth);

    private IRenderable Content => _content ??= Build();

    private IRenderable Build()
    {
        var header = new Text(ThinkingFoldText.LiveHeader, Theme.DimText);
        string body = Text.Trim();
        if (body.Length == 0)
        {
            return header;
        }

        var lines = new List<IRenderable>();
        foreach (string line in body.Split('\n'))
        {
            string clean = line.TrimEnd('\r').Replace("\t", "    ", StringComparison.Ordinal);

            // A blank line must keep its row (Rows skips a child that renders nothing).
            lines.Add(new Text(clean.Length == 0 ? " " : clean, Theme.ThinkingText));
        }

        var panel = new HangingIndent(MarkdownView.CodeIndent, MarkdownView.CodeIndent, Theme.ThinkingText, new Rows(lines));
        return new Rows(header, new HangingIndent(Indent, Indent, Style.Plain, panel));
    }
}
