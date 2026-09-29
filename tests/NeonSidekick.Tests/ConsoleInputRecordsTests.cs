using NeonSidekick.UI;
using static NeonSidekick.UI.ConsoleInputNative;

namespace NeonSidekick.Tests;

public class ConsoleInputRecordsTests
{
    private static KeyEventRecord Key(ushort vk, char ch, bool down = true, uint state = 0, ushort repeat = 1) =>
        new() { bKeyDown = down ? 1 : 0, wRepeatCount = repeat, wVirtualKeyCode = vk, UnicodeChar = ch, dwControlKeyState = state };

    private static MouseEventRecord Mouse(int x, int y, uint buttons, uint flags = 0) =>
        new() { X = (short)x, Y = (short)y, dwButtonState = buttons, dwEventFlags = flags };

    [Fact]
    public void APlainKey_IsDelivered()
    {
        Assert.True(ConsoleInputRecords.TryTranslateKey(Key((ushort)ConsoleKey.A, 'a'), out var key, out int repeat));
        Assert.Equal('a', key.KeyChar);
        Assert.Equal(ConsoleKey.A, key.Key);
        Assert.Equal((ConsoleModifiers)0, key.Modifiers);
        Assert.Equal(1, repeat);
    }

    [Fact]
    public void Modifiers_ComeFromTheControlKeyState()
    {
        Assert.True(ConsoleInputRecords.TryTranslateKey(Key((ushort)ConsoleKey.A, 'A', state: ShiftPressed), out var shifted, out _));
        Assert.Equal(ConsoleModifiers.Shift, shifted.Modifiers);

        Assert.True(ConsoleInputRecords.TryTranslateKey(Key((ushort)ConsoleKey.C, '\x03', state: LeftCtrlPressed), out var ctrl, out _));
        Assert.Equal(ConsoleModifiers.Control, ctrl.Modifiers);
        Assert.Equal(ConsoleKey.C, ctrl.Key);

        Assert.True(ConsoleInputRecords.TryTranslateKey(Key((ushort)ConsoleKey.Tab, '\t', state: ShiftPressed), out var shiftTab, out _));
        Assert.Equal(ConsoleKey.Tab, shiftTab.Key);
        Assert.Equal(ConsoleModifiers.Shift, shiftTab.Modifiers);

        // AltGr is Ctrl+Alt with the character.
        Assert.True(ConsoleInputRecords.TryTranslateKey(Key((ushort)ConsoleKey.Q, '@', state: RightAltPressed | LeftCtrlPressed), out var altGr, out _));
        Assert.Equal('@', altGr.KeyChar);
        Assert.Equal(ConsoleModifiers.Alt | ConsoleModifiers.Control, altGr.Modifiers);
    }

    [Fact]
    public void AKeyWithoutACharacter_IsDelivered_WhenItIsNotAModifier()
    {
        Assert.True(ConsoleInputRecords.TryTranslateKey(Key((ushort)ConsoleKey.F4, '\0'), out var f4, out _));
        Assert.Equal(ConsoleKey.F4, f4.Key);
        Assert.Equal('\0', f4.KeyChar);

        Assert.True(ConsoleInputRecords.TryTranslateKey(Key((ushort)ConsoleKey.Escape, '\x1b'), out var esc, out _));
        Assert.True(Keys.IsCancel(esc));

        // A dead key: no character yet, the key itself; the input line drops it.
        Assert.True(ConsoleInputRecords.TryTranslateKey(Key(0xDD, '\0'), out var dead, out _));
        Assert.Equal('\0', dead.KeyChar);
    }

    [Theory]
    [InlineData(VkShift)]
    [InlineData(VkControl)]
    [InlineData(VkMenu)]
    [InlineData(VkCapital)]
    [InlineData(VkNumLock)]
    [InlineData(VkScroll)]
    public void AModifierAlone_IsNothing(ushort vk)
    {
        Assert.False(ConsoleInputRecords.TryTranslateKey(Key(vk, '\0', state: vk == VkShift ? ShiftPressed : 0), out _, out _));
    }

    [Fact]
    public void AKeyUp_IsNothing_EvenWithACharacter()
    {
        Assert.False(ConsoleInputRecords.TryTranslateKey(Key((ushort)ConsoleKey.A, 'a', down: false), out _, out _));
        Assert.False(ConsoleInputRecords.TryTranslateKey(Key(VkMenu, '\0', down: false), out _, out _));
    }

    [Fact]
    public void AltNumpad_DeliversTheCharacterOnTheAltRelease_AndNothingBefore()
    {
        // Alt down, 0, 2, 3, 3 on the NumPad, Alt up carrying 'é' (233).
        Assert.False(ConsoleInputRecords.TryTranslateKey(Key(VkMenu, '\0', state: LeftAltPressed), out _, out _));
        foreach (ushort vk in new ushort[] { VkNumpad0, 0x62, 0x63, 0x63 })
        {
            Assert.False(ConsoleInputRecords.TryTranslateKey(Key(vk, '\0', state: LeftAltPressed), out _, out _));
            Assert.False(ConsoleInputRecords.TryTranslateKey(Key(vk, '\0', down: false, state: LeftAltPressed), out _, out _));
        }

        Assert.True(ConsoleInputRecords.TryTranslateKey(Key(VkMenu, 'é', down: false), out var composed, out _));
        Assert.Equal('é', composed.KeyChar);
        Assert.Equal((ConsoleKey)VkMenu, composed.Key);

        // The navigation keys the NumPad shares are the sequence too while Alt is down.
        Assert.False(ConsoleInputRecords.TryTranslateKey(Key((ushort)ConsoleKey.Insert, '\0', state: LeftAltPressed), out _, out _));
        Assert.False(ConsoleInputRecords.TryTranslateKey(Key((ushort)ConsoleKey.DownArrow, '\0', state: LeftAltPressed), out _, out _));
        // Without Alt they are keys.
        Assert.True(ConsoleInputRecords.TryTranslateKey(Key((ushort)ConsoleKey.DownArrow, '\0'), out _, out _));
    }

    [Fact]
    public void ARepeatCount_IsACount()
    {
        Assert.True(ConsoleInputRecords.TryTranslateKey(Key((ushort)ConsoleKey.X, 'x', repeat: 3), out _, out int repeat));
        Assert.Equal(3, repeat);
        Assert.True(ConsoleInputRecords.TryTranslateKey(Key((ushort)ConsoleKey.X, 'x', repeat: 0), out _, out repeat));
        Assert.Equal(1, repeat);
    }

    [Fact]
    public void ACharacterWithoutAKey_IsDelivered()
    {
        // VK_PACKET (the terminal's own paste, an IME) and a bare 0.
        Assert.True(ConsoleInputRecords.TryTranslateKey(Key(0xE7, 'ü'), out var packet, out _));
        Assert.Equal('ü', packet.KeyChar);
        Assert.True(ConsoleInputRecords.TryTranslateKey(Key(0, 'x'), out var bare, out _));
        Assert.Equal('x', bare.KeyChar);

        // Each half of a surrogate pair is its own record and passes through.
        Assert.True(ConsoleInputRecords.TryTranslateKey(Key(0xE7, '\ud83d'), out var high, out _));
        Assert.True(ConsoleInputRecords.TryTranslateKey(Key(0xE7, '\ude00'), out var low, out _));
        Assert.Equal("😀", new string(new[] { high.KeyChar, low.KeyChar }));
    }

    [Fact]
    public void ALeftPress_IsAClick_ItsReleaseARelease()
    {
        uint buttons = 0;
        Assert.True(ConsoleInputRecords.TryTranslateMouse(Mouse(7, 12, FromLeft1stButtonPressed), ref buttons, out var click, out bool wheel));
        Assert.Equal(new InputEvent.Click(7, 12, MouseButton.Left), click);
        Assert.False(wheel);

        // The release: flags 0, buttons 0 — not a click but a release (2026-09-28, the drop of a dragged picture).
        Assert.True(ConsoleInputRecords.TryTranslateMouse(Mouse(7, 12, 0), ref buttons, out var release, out wheel));
        Assert.Equal(new InputEvent.Release(7, 12), release);
        Assert.False(wheel);
        Assert.Equal(0u, buttons);

        // Up already: another record with nothing held is nothing.
        Assert.False(ConsoleInputRecords.TryTranslateMouse(Mouse(7, 12, 0), ref buttons, out _, out _));
    }

    /// <summary>A move record that finds the left button up after it was down is the release too, at the cell it moved to.</summary>
    [Fact]
    public void AMove_ThatLetsGoOfTheLeftButton_IsARelease()
    {
        uint buttons = 0;
        Assert.True(ConsoleInputRecords.TryTranslateMouse(Mouse(1, 1, FromLeft1stButtonPressed), ref buttons, out _, out _));
        Assert.True(ConsoleInputRecords.TryTranslateMouse(Mouse(4, 9, 0, MouseMoved), ref buttons, out var release, out _));
        Assert.Equal(new InputEvent.Release(4, 9), release);
        Assert.False(ConsoleInputRecords.TryTranslateMouse(Mouse(5, 9, 0, MouseMoved), ref buttons, out _, out _));
    }

    [Fact]
    public void ARightPress_IsARightClick()
    {
        uint buttons = 0;
        Assert.True(ConsoleInputRecords.TryTranslateMouse(Mouse(3, 4, RightmostButtonPressed), ref buttons, out var click, out _));
        Assert.Equal(new InputEvent.Click(3, 4, MouseButton.Right), click);
    }

    [Fact]
    public void ADrag_IsADrag_ItsEndARelease_AndADoubleClickAClick()
    {
        uint buttons = 0;
        Assert.True(ConsoleInputRecords.TryTranslateMouse(Mouse(1, 1, FromLeft1stButtonPressed), ref buttons, out _, out _));
        // Moving with the button held: a drag to each cell, never a click.
        Assert.True(ConsoleInputRecords.TryTranslateMouse(Mouse(2, 1, FromLeft1stButtonPressed, MouseMoved), ref buttons, out var drag, out bool wheel));
        Assert.Equal(new InputEvent.Drag(2, 1), drag);
        Assert.False(wheel);
        Assert.True(ConsoleInputRecords.TryTranslateMouse(Mouse(3, 2, FromLeft1stButtonPressed, MouseMoved), ref buttons, out drag, out _));
        Assert.Equal(new InputEvent.Drag(3, 2), drag);
        // The same bit still down without a move flag: nothing; the release: a release.
        Assert.False(ConsoleInputRecords.TryTranslateMouse(Mouse(3, 1, FromLeft1stButtonPressed), ref buttons, out _, out _));
        Assert.True(ConsoleInputRecords.TryTranslateMouse(Mouse(3, 1, 0), ref buttons, out var release, out _));
        Assert.Equal(new InputEvent.Release(3, 1), release);
        // A double-click record is a press again.
        Assert.True(ConsoleInputRecords.TryTranslateMouse(Mouse(3, 1, FromLeft1stButtonPressed, ConsoleInputNative.DoubleClick), ref buttons, out var click, out _));
        Assert.Equal(new InputEvent.Click(3, 1, MouseButton.Left), click);
    }

    [Fact]
    public void AMove_WithTheRightButtonHeld_IsNothing()
    {
        uint buttons = 0;
        Assert.True(ConsoleInputRecords.TryTranslateMouse(Mouse(1, 1, RightmostButtonPressed), ref buttons, out _, out _));
        Assert.False(ConsoleInputRecords.TryTranslateMouse(Mouse(2, 1, RightmostButtonPressed, MouseMoved), ref buttons, out _, out _));
        Assert.False(ConsoleInputRecords.TryTranslateMouse(Mouse(2, 1, 0), ref buttons, out _, out _));
    }

    [Fact]
    public void AMove_IsNothing_AndAWheelIsAWheel()
    {
        uint buttons = 0;
        Assert.False(ConsoleInputRecords.TryTranslateMouse(Mouse(5, 5, 0, MouseMoved), ref buttons, out _, out bool wheel));
        Assert.False(wheel);
        buttons = FromLeft1stButtonPressed;
        // The vertical wheel is an event — a notch away from the user (+120 in the high word) is +1 — and a wheel record too.
        Assert.True(ConsoleInputRecords.TryTranslateMouse(Mouse(5, 5, 0x00780000, MouseWheeled), ref buttons, out var up, out wheel));
        Assert.Equal(new InputEvent.Wheel(5, 5, 1), up);
        Assert.True(wheel);
        Assert.Equal(FromLeft1stButtonPressed, buttons);
        Assert.True(ConsoleInputRecords.TryTranslateMouse(Mouse(6, 7, 0xFF880000, MouseWheeled), ref buttons, out var down, out wheel));
        Assert.Equal(new InputEvent.Wheel(6, 7, -1), down);
        Assert.True(wheel);
        // A wheel record with no delta, and the horizontal wheel, are a wheel but never an event.
        Assert.False(ConsoleInputRecords.TryTranslateMouse(Mouse(5, 5, 0, MouseWheeled), ref buttons, out _, out wheel));
        Assert.True(wheel);
        Assert.False(ConsoleInputRecords.TryTranslateMouse(Mouse(5, 5, 0x00780000, MouseHWheeled), ref buttons, out _, out wheel));
        Assert.True(wheel);
        // The middle button is nobody's (pressed from nothing held: with the left one down before, the record is its release).
        buttons = 0;
        Assert.False(ConsoleInputRecords.TryTranslateMouse(Mouse(5, 5, 0x0004), ref buttons, out _, out wheel));
        Assert.False(wheel);
    }

    [Theory]
    [InlineData(0x00780000u, 1)]
    [InlineData(0xFF880000u, -1)]
    [InlineData(0x00F00000u, 2)]
    [InlineData(0xFE200000u, -4)]
    [InlineData(0x00300000u, 1)]
    [InlineData(0xFFD00000u, -1)]
    [InlineData(0x00000001u, 0)]
    public void WheelNotches_IsTheHighWordOverTheDelta_AFractionRoundedUpToOne(uint buttonState, int notches)
        => Assert.Equal(notches, ConsoleInputRecords.WheelNotches(buttonState));

    [Fact]
    public void TheInputRecord_HasTheWin32Layout()
    {
        Assert.Equal(20, System.Runtime.InteropServices.Marshal.SizeOf<InputRecord>());
        Assert.Equal(16, System.Runtime.InteropServices.Marshal.SizeOf<KeyEventRecord>());
        Assert.Equal(16, System.Runtime.InteropServices.Marshal.SizeOf<MouseEventRecord>());
    }
}
