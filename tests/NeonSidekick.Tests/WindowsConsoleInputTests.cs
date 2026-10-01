using NeonSidekick.UI;
using static NeonSidekick.UI.ConsoleInputNative;

namespace NeonSidekick.Tests;

/// <summary>The console mode bits alone: the reader itself needs a real console handle.</summary>
public class WindowsConsoleInputTests
{
    private const uint InsertMode = 0x0020;
    private const uint ShellMode = EnableProcessedInput | EnableQuickEditMode | EnableVirtualTerminalInput | EnableLineInput | EnableEchoInput | InsertMode;

    [Fact]
    public void Mode_DropsProcessedInput_InBothStates_SoCtrlCIsAKey()
    {
        uint released = WindowsConsoleInput.Mode(ShellMode, captured: false);
        uint captured = WindowsConsoleInput.Mode(ShellMode, captured: true);

        Assert.Equal(0u, released & EnableProcessedInput);
        Assert.Equal(0u, captured & EnableProcessedInput);
        Assert.Equal(0u, released & EnableVirtualTerminalInput);
        Assert.NotEqual(0u, released & EnableExtendedFlags);
        Assert.NotEqual(0u, released & EnableQuickEditMode);   // as the shell had it
        Assert.NotEqual(0u, captured & EnableMouseInput);
        Assert.Equal(0u, captured & EnableQuickEditMode);
        Assert.NotEqual(0u, captured & InsertMode);             // the rest untouched
    }

    [Fact]
    public void Mode_DropsLineAndEchoInput_InBothStates_SoCtrlSIsAKeyNotThePause()
    {
        uint released = WindowsConsoleInput.Mode(ShellMode, captured: false);
        uint captured = WindowsConsoleInput.Mode(ShellMode, captured: true);

        Assert.Equal(0u, released & (EnableLineInput | EnableEchoInput));
        Assert.Equal(0u, captured & (EnableLineInput | EnableEchoInput));
    }

    [Fact]
    public void Released_KeepsProcessedInput_TheShellsCtrlCAgain()
    {
        uint released = WindowsConsoleInput.Released(ShellMode);

        Assert.NotEqual(0u, released & EnableProcessedInput);
        Assert.NotEqual(0u, released & EnableLineInput);
        Assert.NotEqual(0u, released & EnableEchoInput);
        Assert.Equal(0u, released & EnableVirtualTerminalInput);
        Assert.NotEqual(0u, released & EnableExtendedFlags);
        Assert.Equal(0u, released & EnableMouseInput);
    }
}
