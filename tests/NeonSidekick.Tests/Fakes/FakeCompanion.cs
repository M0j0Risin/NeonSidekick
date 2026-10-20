using NeonSidekick.Companion;
using Spectre.Console;

namespace NeonSidekick.Tests.Fakes;

/// <summary>
/// A character for the pane's companion overlay (2026-10-20, the welcome splash's robot): a canvas of the given size whose top
/// pixel of every cell is <see cref="Ink"/> and bottom empty, so each painted row is a run of <c>▀</c>; every call recorded;
/// <see cref="Update"/> reports a change while <see cref="Changes"/> says so, and throws once <see cref="Throws"/> is set.
/// </summary>
public sealed class FakeCompanion : ICompanion
{
    public static readonly Color Ink = new(0x11, 0x22, 0x33);

    public FakeCompanion(int width = 24, int rows = 8, int headRows = 4)
    {
        Canvas = new CompanionCanvas(width, rows * 2, headRows * 2);
        Frame = new PixelBuffer(width, rows * 2);
        for (int y = 0; y < rows * 2; y += 2)
        {
            Frame.Fill(0, y, width, 1, Ink);
        }
    }

    public CompanionCanvas Canvas { get; }

    public PixelBuffer Frame { get; }

    public bool HeadOnly { get; set; }

    public ThemeRoles? Theme { get; set; }

    public List<(int Column, int Row)> Origins { get; } = [];

    public List<(int Column, int Row)> Pointers { get; } = [];

    public List<(int Column, int Row, int Start, int End)> Carets { get; } = [];

    public List<bool> Focus { get; } = [];

    public int Updates { get; private set; }

    public bool Dismissed { get; private set; }

    public bool IsDone { get; set; }

    /// <summary>Whether <see cref="Update"/> says a pixel changed.</summary>
    public bool Changes { get; set; } = true;

    public bool Throws { get; set; }

    public string DebugLine => "fake";

    public void SetOrigin(int column, int row) => Origins.Add((column, row));

    public void OnPointer(int column, int row) => Pointers.Add((column, row));

    public void OnCaret(int column, int row, int lineStart, int lineEnd) => Carets.Add((column, row, lineStart, lineEnd));

    public void OnFocus(bool focused) => Focus.Add(focused);

    public bool Wave(int side = 0) => false;

    public void Dismiss() => Dismissed = true;

    public bool Update()
    {
        Updates++;
        if (Throws)
        {
            throw new InvalidOperationException("the fake companion threw");
        }

        return Changes;
    }
}
