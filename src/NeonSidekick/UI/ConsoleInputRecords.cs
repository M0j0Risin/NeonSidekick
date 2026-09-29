using static NeonSidekick.UI.ConsoleInputNative;

namespace NeonSidekick.UI;

/// <summary>
/// What a raw console input record means, as pure functions so the reader thread in
/// <see cref="WindowsConsoleInput"/> stays a loop and the rules are pinned by tests.
///
/// <para><see cref="TryTranslateKey"/> is the filter <c>Console.ReadKey</c> applies on Windows
/// (dotnet/runtime <c>ConsolePal.Windows.ReadKey</c>), ported so that swapping the reader changes
/// nothing about which keys the app sees: key-ups are dropped except the Alt release that ends an
/// Alt+NumPad sequence and carries its character; a modifier pressed alone is dropped; while Alt
/// is down the NumPad and navigation keys of a sequence are dropped; a repeat count is a count.
/// Everything else — a dead key (no character), <c>VK_PACKET</c> from the terminal's own paste or
/// an IME (a character, no key), each half of a surrogate pair in its own record — is delivered as
/// it is, exactly as before.</para>
/// </summary>
public static class ConsoleInputRecords
{
    /// <summary>
    /// The key a record delivers, if any, and how many times: false for a record the app should
    /// never see. Pinned.
    /// </summary>
    public static bool TryTranslateKey(in KeyEventRecord record, out ConsoleKeyInfo key, out int repeat)
    {
        key = default;
        repeat = 0;
        ushort keyCode = record.wVirtualKeyCode;

        // Key-ups are noise, except the Alt release that reveals an Alt+NumPad character.
        if (record.bKeyDown == 0 && keyCode != VkMenu)
        {
            return false;
        }

        char ch = (char)record.UnicodeChar;
        if (ch == '\0' && IsModifier(keyCode))
        {
            return false;
        }

        // While Alt is down the NumPad digits (and the navigation keys they share) are the
        // sequence itself, whether NumLock is on or not.
        if (IsAltDown(record.dwControlKeyState) && IsAltSequenceKey(keyCode))
        {
            return false;
        }

        uint state = record.dwControlKeyState;
        key = new ConsoleKeyInfo(
            ch,
            (ConsoleKey)keyCode,
            shift: (state & ShiftPressed) != 0,
            alt: IsAltDown(state),
            control: (state & (LeftCtrlPressed | RightCtrlPressed)) != 0);
        repeat = Math.Max(1, (int)record.wRepeatCount);
        return true;
    }

    /// <summary>
    /// The mouse event a record is, if any: an <see cref="InputEvent.Click"/> for a button bit
    /// going from up to down (<paramref name="buttons"/> is the previous record's button state and
    /// is updated here; a double-click record is a click too), an <see cref="InputEvent.Release"/> for the left button
    /// going from down to up (2026-09-28, the drop of a picture dragged onto the chat line; it arrives with no event
    /// flag, and the right button's release is still nothing — as is the left's in a record that also presses the
    /// right, which is the right's click), an <see cref="InputEvent.Drag"/> for a move with the left button
    /// held (a move with nothing or only the right button held is nothing), an <see cref="InputEvent.Wheel"/>
    /// for a vertical wheel record with a delta (the signed high word of the button state, +120 per
    /// notch away from the user; a smaller delta still counts one notch). <paramref name="wheel"/> is
    /// true for any wheel record — the vertical one too, so a reader with nobody holding the wheel
    /// can hand the mouse back instead; the horizontal wheel and a zero delta are never an event.
    /// The button state is left alone by a wheel record. Pinned.
    /// </summary>
    public static bool TryTranslateMouse(in MouseEventRecord record, ref uint buttons, out InputEvent mouse, out bool wheel)
    {
        mouse = null!;
        uint flags = record.dwEventFlags;
        wheel = (flags & (MouseWheeled | MouseHWheeled)) != 0;
        if (wheel)
        {
            if ((flags & MouseWheeled) != 0 && WheelNotches(record.dwButtonState) is int notches and not 0)
            {
                mouse = new InputEvent.Wheel(record.X, record.Y, notches);
                return true;
            }

            return false;
        }

        uint previous = buttons;
        buttons = record.dwButtonState;
        if ((flags & MouseMoved) != 0)
        {
            if ((buttons & FromLeft1stButtonPressed) != 0)
            {
                mouse = new InputEvent.Drag(record.X, record.Y);
                return true;
            }

            // A move that finds the left button up after it was down is its release, whatever flag it came with.
            if ((previous & FromLeft1stButtonPressed) != 0)
            {
                mouse = new InputEvent.Release(record.X, record.Y);
                return true;
            }

            return false;
        }

        uint pressed = buttons & ~previous;
        uint released = previous & ~buttons;
        if ((released & FromLeft1stButtonPressed) != 0 && (pressed & RightmostButtonPressed) == 0)
        {
            mouse = new InputEvent.Release(record.X, record.Y);
            return true;
        }

        if ((pressed & FromLeft1stButtonPressed) != 0)
        {
            mouse = new InputEvent.Click(record.X, record.Y, MouseButton.Left);
            return true;
        }

        if ((pressed & RightmostButtonPressed) != 0)
        {
            mouse = new InputEvent.Click(record.X, record.Y, MouseButton.Right);
            return true;
        }

        return false;
    }

    /// <summary>One wheel record's notches from its button state: the signed high word over <c>WHEEL_DELTA</c> (120), a fraction of a notch rounded up to one, 0 for no delta.</summary>
    public static int WheelNotches(uint buttonState)
    {
        int delta = (short)(buttonState >> 16);
        return delta == 0 ? 0 : Math.Sign(delta) * Math.Max(1, Math.Abs(delta) / 120);
    }

    private static bool IsAltDown(uint state) => (state & (LeftAltPressed | RightAltPressed)) != 0;

    private static bool IsModifier(ushort keyCode) =>
        keyCode is >= VkShift and <= VkMenu || keyCode == VkCapital || keyCode == VkNumLock || keyCode == VkScroll;

    /// <summary>NumPad 0–9, Clear, Insert, and PageUp … DownArrow (0x21–0x28): the keys an Alt+NumPad sequence is typed on.</summary>
    private static bool IsAltSequenceKey(ushort keyCode) =>
        keyCode is >= VkNumpad0 and <= VkNumpad9 || keyCode == VkClear || keyCode == (ushort)ConsoleKey.Insert
        || keyCode is >= (ushort)ConsoleKey.PageUp and <= (ushort)ConsoleKey.DownArrow;
}
