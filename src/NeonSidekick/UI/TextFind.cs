using Spectre.Console;
using Spectre.Console.Rendering;

namespace NeonSidekick.UI;

/// <summary>
/// The find over laid-out lines (2026-10-07, the user's ask, phase 4 of the UI round: type to find in an info pane): where a
/// text occurs in each line, ignoring case, and the line again with those places marked (<see cref="Theme.FindMatch"/>, the
/// one the find is on <see cref="Theme.FindCurrent"/>). The lines are the segments a pane draws, so what is found is what is
/// on the screen, wrapping and all: a word cut across two rows is not found. Pure.
/// </summary>
public static class TextFind
{
    /// <summary>One place the text occurs: the line and the character it starts at in that line's text (<see cref="LineText"/>).</summary>
    public readonly record struct Hit(int Line, int Start);

    /// <summary>A line's text as the screen shows it: its segments' text, control codes and line breaks left out.</summary>
    public static string LineText(IReadOnlyList<Segment> line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var text = new System.Text.StringBuilder();
        foreach (var segment in line)
        {
            if (!segment.IsControlCode && !segment.IsLineBreak)
            {
                text.Append(segment.Text);
            }
        }

        return text.ToString();
    }

    /// <summary>Every place <paramref name="find"/> occurs in <paramref name="lines"/>, ignoring case, line by line and left to right, none overlapping; none for an empty find.</summary>
    public static List<Hit> Matches(IReadOnlyList<IReadOnlyList<Segment>> lines, string find)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(find);
        var hits = new List<Hit>();
        if (find.Length == 0)
        {
            return hits;
        }

        for (int i = 0; i < lines.Count; i++)
        {
            string text = LineText(lines[i]);
            for (int at = text.IndexOf(find, StringComparison.OrdinalIgnoreCase); at >= 0; at = text.IndexOf(find, at + find.Length, StringComparison.OrdinalIgnoreCase))
            {
                hits.Add(new Hit(i, at));
            }
        }

        return hits;
    }

    /// <summary>
    /// <paramref name="line"/> with the stretches of <paramref name="length"/> characters from each of <paramref name="starts"/>
    /// (ascending) marked: <see cref="Theme.FindMatch"/> over the text's own style, and the one starting at
    /// <paramref name="current"/> (−1 for none on this line) <see cref="Theme.FindCurrent"/>. A segment a stretch cuts is split.
    /// </summary>
    public static IReadOnlyList<Segment> Mark(IReadOnlyList<Segment> line, IReadOnlyList<int> starts, int length, int current)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(starts);
        if (starts.Count == 0 || length <= 0)
        {
            return line;
        }

        var marked = new List<Segment>(line.Count + 2 * starts.Count);
        int position = 0;
        foreach (var segment in line)
        {
            if (segment.IsControlCode || segment.IsLineBreak)
            {
                marked.Add(segment);
                continue;
            }

            string text = segment.Text;
            int i = 0;
            while (i < text.Length)
            {
                var (inside, end, isCurrent) = Place(starts, length, current, position + i);
                int stop = Math.Min(text.Length, end - position);
                var style = !inside ? segment.Style : isCurrent ? Theme.FindCurrent : segment.Style.Combine(Theme.FindMatch);
                marked.Add(new Segment(text[i..stop], style));
                i = stop;
            }

            position += text.Length;
        }

        return marked;
    }

    /// <summary>Whether character <paramref name="at"/> is inside a stretch, where that stretch (or the gap before the next one) ends, and whether it is the current one.</summary>
    private static (bool Inside, int End, bool Current) Place(IReadOnlyList<int> starts, int length, int current, int at)
    {
        foreach (int start in starts)
        {
            if (start + length <= at)
            {
                continue;
            }

            return start <= at ? (true, start + length, start == current) : (false, start, false);
        }

        return (false, int.MaxValue, false);
    }
}
