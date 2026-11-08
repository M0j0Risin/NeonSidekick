using Spectre.Console;
using Spectre.Console.Rendering;

namespace NeonSidekick.UI;

/// <summary>
/// A section's heading drawn as a rule to the edge of the room it is given (2026-10-03, the user's call: the
/// <c>/tools</c> Offered tab's, the <c>/usage</c> Prompt and Tools tabs' and the <c>/mcp</c> Tools tab's bare violet
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

    // The fill's style as the heading was built (2026-10-29): a heading in a transcript write may be rendered again on a resize,
    // and must not take a theme picked since.
    private readonly Style _rule = Theme.PaneRule;

    // The gradient rules' stops as the heading was built (2026-11-01, Theme.RuleStops; null = flat), for the same reason.
    private readonly Color[]? _stops = Theme.RuleStops;

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
        int fill = maxWidth - FittedMarkup.RowCells(segments) - 1;
        if (_stops is { } stops)
        {
            // Gradient rules (2026-11-01): the lead's two glyphs and the fill shade along the theme's quiet gradient across the width the
            // heading is drawn at; the label, the count and a note's inner lead keep their styles.
            ShadeLead(segments, stops, maxWidth);
        }

        if (fill > 0)
        {
            int column = FittedMarkup.RowCells(segments) + 1;
            segments.Add(new Segment(" "));
            if (_stops is { } fillStops)
            {
                foreach (var (text, color) in Theme.RuleRuns(fill, fillStops, column, maxWidth, ScreenPane.RuleGlyph))
                {
                    segments.Add(new Segment(text, new Style(foreground: color)));
                }
            }
            else
            {
                segments.Add(new Segment(new string(ScreenPane.RuleGlyph, fill), _rule));
            }
        }

        return segments;
    }

    /// <summary>The first segment's leading rule glyphs, up to <see cref="Lead"/>'s cells, in <paramref name="stops"/>' colors when it is in the rule's style.</summary>
    private void ShadeLead(List<Segment> segments, Color[] stops, int maxWidth)
    {
        if (segments.Count == 0 || !segments[0].Style.Equals(_rule))
        {
            return;
        }

        string text = segments[0].Text;
        int glyphs = 0;
        while (glyphs < text.Length && glyphs < Lead.Length && text[glyphs] == ScreenPane.RuleGlyph)
        {
            glyphs++;
        }

        if (glyphs == 0)
        {
            return;
        }

        var pieces = Theme.RuleRuns(glyphs, stops, 0, maxWidth, ScreenPane.RuleGlyph)
            .Select(p => new Segment(p.Text, new Style(foreground: p.Color)))
            .ToList();
        if (glyphs < text.Length)
        {
            pieces.Add(new Segment(text[glyphs..], segments[0].Style, segments[0].Link));
        }

        segments.RemoveAt(0);
        segments.InsertRange(0, pieces);
    }
}
