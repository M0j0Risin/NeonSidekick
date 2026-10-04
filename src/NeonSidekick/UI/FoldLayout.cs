using Spectre.Console.Rendering;

namespace NeonSidekick.UI;

/// <summary>
/// A renderable that folds part of itself once it is over (2026-10-04, an edit's diff past <c>Diff collapse count</c>): read by
/// <see cref="ScreenPane"/>'s flow write the way <see cref="IPictureLayout"/> is, and handed to the store as a
/// <see cref="Scrollback.FoldSpec"/>. Null: nothing in it folds.
/// </summary>
public interface IFoldLayout
{
    FoldLayout? Fold { get; }
}

/// <summary>
/// What folds in a write: from its line <paramref name="Head"/> (counted from the first line the write touches, an open line it
/// continues included) to its end, past <paramref name="Keep"/> when it measures <paramref name="Size"/>; the head row reads
/// <paramref name="Collapsed"/> folded and <paramref name="Expanded"/> unfolded, its own text until it folds.
/// </summary>
public sealed record FoldLayout(int Head, int Keep, int Size, IRenderable Collapsed, IRenderable Expanded);
