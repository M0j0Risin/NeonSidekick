using Spectre.Console;
using Spectre.Console.Rendering;

namespace NeonSidekick.UI;

/// <summary>
/// A section's heading drawn as a rule to the edge of the room it is given (2026-10-03, the user's call: the
/// <c>/tools</c> Offered tab's, the <c>/sys</c> Tools and Prompt tabs' and the <c>/mcp</c> Tools tab's bare violet
/// headings looked poor): <c>── Files · 12 of 14 ─────…</c>, a note between two runs of the rule when there is one
/// (<c>── GitLib · 11 ── off: GitLib tools is off ───…</c>). The glyphs in <see cref="Theme.PaneRule"/>, the pane's own
/// rule; the label in <see cref="Theme.SectionHeading"/>, never dimmed (the 2026-09-20 rule); the count and the note dim.
/// <see cref="Markup(string, string?, string?)"/> builds the part before the fill — what a <see cref="MenuPane"/> row
/// carries — and the renderable draws it, cut with an ellipsis (<see cref="FittedMarkup"/>) when it is wider than the
/// room, with no fill then, else followed by a space and the rule to the last cell.
/// </summary>
internal sealed class SectionRule : IRenderable
{
    /// <summary>The run of rule before the label and before a note.</summary>
    public const string Lead = "──";

    /// <summary>Between the label and its count. Pinned.</summary>
    public const string CountSeparator = " · ";

    private readonly FittedMarkup _head;

    /// <param name="markup">What <see cref="Markup(string, string?, string?)"/> built.</param>
    public SectionRule(string markup)
    {
        ArgumentNullException.ThrowIfNull(markup);
        _head = new FittedMarkup(markup);
    }

    /// <summary>
    /// The heading's markup without the fill: <see cref="Lead"/> and a space, <paramref name="label"/> in the section
    /// style, <see cref="CountSeparator"/> and <paramref name="count"/> dim when there is one, then a space,
    /// <see cref="Lead"/>, a space and <paramref name="note"/> dim when there is one. Everything escaped. Pure.
    /// </summary>
    public static string Markup(string label, string? count = null, string? note = null)
    {
        ArgumentNullException.ThrowIfNull(label);
        string rule = Theme.PaneRule.ToMarkup();
        string head = $"[{rule}]{Lead}[/] [{Theme.SectionHeading.ToMarkup()}]{Spectre.Console.Markup.Escape(label)}[/]";
        if (!string.IsNullOrEmpty(count))
        {
            head += Theme.DimMarkup(CountSeparator + count);
        }

        if (!string.IsNullOrEmpty(note))
        {
            head += $" [{rule}]{Lead}[/] " + Theme.DimMarkup(note);
        }

        return head;
    }

    public Measurement Measure(RenderOptions options, int maxWidth) => new(Math.Min(1, maxWidth), maxWidth);

    public IEnumerable<Segment> Render(RenderOptions options, int maxWidth)
    {
        var segments = ((IRenderable)_head).Render(options, maxWidth).ToList();
        int fill = maxWidth - Segment.CellCount(segments) - 1;
        if (fill > 0)
        {
            segments.Add(new Segment(" "));
            segments.Add(new Segment(new string(ScreenPane.RuleGlyph, fill), Theme.PaneRule));
        }

        return segments;
    }
}
