namespace NeonSidekick.Companion;

/// <summary>
/// A character a host can show — what the welcome splash (<see cref="UI.ScreenPane.ShowCompanion"/>) drives, so a new character is a
/// new implementation rather than a change to the host. The robot (<see cref="RobotAnimator"/>) is the first.
///
/// <para>The host tells it where it sits (<see cref="SetOrigin"/>, <see cref="HeadOnly"/>), what the user does
/// (<see cref="OnPointer"/>, <see cref="OnCaret"/>, <see cref="OnFocus"/>), the theme (<see cref="Theme"/>), and when to
/// greet or leave (<see cref="Wave"/>, <see cref="Dismiss"/>); once a frame it calls <see cref="Update"/> and, when that
/// says pixels changed, draws <see cref="Frame"/>. Its contract:</para>
/// <list type="bullet">
/// <item>Not thread-safe: the host feeds it from one thread.</item>
/// <item><see cref="Frame"/> is always the same buffer, <see cref="CompanionCanvas.Width"/> by
/// <see cref="CompanionCanvas.Height"/> pixels, so a view can hold on to it.</item>
/// <item><see cref="Update"/> returns true only when a pixel changed, so a host can skip drawing while it's still.</item>
/// <item><see cref="Theme"/> returns what was last set.</item>
/// </list>
/// </summary>
public interface ICompanion
{
    /// <summary>The character's canvas size and its head-only crop, for fitting it to a space (<see cref="CompanionFit"/>).</summary>
    CompanionCanvas Canvas { get; }

    /// <summary>The character as last painted by <see cref="Update"/>.</summary>
    PixelBuffer Frame { get; }

    /// <summary>Whether only the head is being shown (<see cref="CompanionFit.IsHeadOnly"/>): the character stops anything that would reach below the crop.</summary>
    bool HeadOnly { get; set; }

    /// <summary>Where the canvas's top-left cell is on the screen; pointer and caret positions are taken relative to it.</summary>
    void SetOrigin(int column, int row);

    /// <summary>The theme's colors by role, which the character maps onto its own parts; null for the character's own colors.</summary>
    ThemeRoles? Theme { get; set; }

    /// <summary>The pointer moved to screen cell (<paramref name="column"/>, <paramref name="row"/>).</summary>
    void OnPointer(int column, int row);

    /// <summary>
    /// Someone is typing and the text cursor is at screen cell (<paramref name="column"/>, <paramref name="row"/>), on a line
    /// from screen column <paramref name="lineStart"/> to <paramref name="lineEnd"/> (exclusive).
    /// </summary>
    void OnCaret(int column, int row, int lineStart, int lineEnd);

    /// <summary>The window gained or lost focus.</summary>
    void OnFocus(bool focused);

    /// <summary>A greeting, toward <paramref name="side"/> (−1 left, +1 right, 0 the character's choice); false when it can't now.</summary>
    bool Wave(int side = 0);

    /// <summary>Says goodbye; the host takes the character away once it's <see cref="IsDone"/>.</summary>
    void Dismiss();

    /// <summary>Whether the character has finished leaving after <see cref="Dismiss"/>.</summary>
    bool IsDone { get; }

    /// <summary>Advances to now and repaints <see cref="Frame"/>; true when any pixel changed.</summary>
    bool Update();

    /// <summary>The character's part of a debug line: its pose, motion and state, for a host's diagnostics.</summary>
    string DebugLine { get; }
}

/// <summary>
/// A character's canvas, in pixels: <paramref name="Width"/> by <paramref name="Height"/> (a pixel a cell across, two a cell
/// down), and the top <paramref name="HeadPixelRows"/> rows that show just its head when the space is too short for the rest.
/// </summary>
public readonly record struct CompanionCanvas(int Width, int Height, int HeadPixelRows);
