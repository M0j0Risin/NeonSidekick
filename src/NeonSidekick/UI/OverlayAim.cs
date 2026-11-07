using Spectre.Console;
using Spectre.Console.Rendering;

namespace NeonSidekick.UI;

/// <summary>
/// One thing on an overlay a click acts on (2026-11-07, menu clicks): overlay row <see cref="Row"/>, columns <see cref="First"/> to
/// <see cref="Last"/> (both counted), and <see cref="Text"/>, what the host gave for it before the cursor's highlight, so moving the
/// cursor changes nothing. <see cref="Cursor"/> says the cursor is on it: it keeps its own look under the pointer, and takes no part in
/// whether two draws show the same thing.
/// </summary>
public readonly record struct OverlayTarget(int Row, int First, int Last, string Text, bool Cursor = false);

/// <summary>
/// What a pane has on show (2026-11-07, menu clicks): which page (<see cref="Page"/>: its title, tab, sub-tab and the rows above its
/// list) and its <see cref="Targets"/>. <see cref="ScreenPane"/> reads one list for both jobs: whether a click was aimed at an older
/// layout (<see cref="ScreenPane.ClickIsStale"/>) and which row the pointer's over. Immutable, handed over on each draw.
/// </summary>
public sealed record OverlayAim(string Page, IReadOnlyList<OverlayTarget> Targets)
{
    /// <summary>The target at overlay row <paramref name="row"/>, column <paramref name="x"/>; null on a heading, a gap or off the list. Pure.</summary>
    public OverlayTarget? At(int row, int x)
    {
        foreach (var target in Targets)
        {
            if (target.Row == row && x >= target.First && x <= target.Last)
            {
                return target;
            }
        }

        return null;
    }

    /// <summary>Whether <paramref name="a"/> and <paramref name="b"/> show the same page with the same text in the same places, wherever the cursor is. Pure.</summary>
    public static bool SameShown(OverlayAim? a, OverlayAim? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        if (!string.Equals(a.Page, b.Page, StringComparison.Ordinal) || a.Targets.Count != b.Targets.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Targets.Count; i++)
        {
            var (x, y) = (a.Targets[i], b.Targets[i]);
            if (x.Row != y.Row || x.First != y.First || x.Last != y.Last || !string.Equals(x.Text, y.Text, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// <paramref name="line"/> with <paramref name="background"/> behind columns <paramref name="first"/> to <paramref name="last"/>,
    /// padded with blanks to the last: the hover's slab. Its text keeps its colors, and a part with a background of its own (a marked
    /// grid cell, a find's match) keeps that. Columns are <see cref="TextCells"/>' clusters, what the terminal draws, since Spectre's
    /// count miscounts ⚙️ 🛠️ 🖥️. Pure.
    /// </summary>
    internal static SegmentLine Slab(SegmentLine line, int first, int last, Color background)
    {
        ArgumentNullException.ThrowIfNull(line);
        var painted = new SegmentLine();
        int column = 0;
        foreach (var segment in line)
        {
            if (segment.IsControlCode || segment.IsLineBreak || segment.Text.Length == 0)
            {
                painted.Add(segment);
                continue;
            }

            string text = segment.Text;
            int start = 0;
            int i = 0;
            bool inside = column >= first && column <= last;
            while (i < text.Length)
            {
                bool here = column >= first && column <= last;
                if (here != inside)
                {
                    painted.Add(Piece(segment, text[start..i], inside, background));
                    start = i;
                    inside = here;
                }

                column += TextCells.ClusterWidth(text, i, out int length);
                i += length;
            }

            painted.Add(Piece(segment, text[start..], inside, background));
        }

        if (column < first)
        {
            painted.Add(new Segment(new string(' ', first - column)));
            column = first;
        }

        if (column <= last)
        {
            painted.Add(new Segment(new string(' ', last - column + 1), new Style(background: background)));
        }

        return painted;
    }

    private static Segment Piece(Segment segment, string text, bool inside, Color background) =>
        new(text, inside && segment.Style.Background == Color.Default ? new Style(segment.Style.Foreground, background, segment.Style.Decoration) : segment.Style, segment.Link);
}
