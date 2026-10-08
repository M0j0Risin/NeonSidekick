namespace NeonSidekick.Audio;

/// <summary>The macOS microphone's sentences (2026-10-07, sound on a Mac). Pinned by <c>MicrophoneAccessTests</c>.</summary>
public static class MicrophoneText
{
    /// <summary>The settings page that holds the permission, as System Settings names it on macOS 13 and later.</summary>
    public const string SettingsPath = "System Settings › Privacy & Security › Microphone";

    /// <summary>The terminal named in the sentences: set once by <c>Program</c> from <c>TERM_PROGRAM</c> (<see cref="TerminalName"/>).</summary>
    public static string Terminal { get; set; } = TerminalName(null);

    /// <summary>The Mac counterpart of Windows' "no wave-in device": Core Audio names no default input.</summary>
    public const string NoInputDevice = "no input device";

    public const string Restricted =
        "Microphone access is restricted on this Mac (a configuration profile or Screen Time); voice input cannot record.";

    public const string Silent =
        "The microphone sends only silence. On a MacBook with its lid closed its own microphone is off: open the lid or pick another input in System Settings › Sound › Input. Otherwise check that the input is not muted.";

    public static string Denied(string terminal) =>
        $"Microphone access is off for {terminal}, so voice input hears nothing. Turn {terminal} on in {SettingsPath}, then try again.";

    /// <summary>The app macOS asks about, from <c>TERM_PROGRAM</c>: Terminal and iTerm2 by name, anything else as "your terminal app".</summary>
    public static string TerminalName(string? termProgram) => termProgram switch
    {
        "Apple_Terminal" => "Terminal",
        "iTerm.app" => "iTerm2",
        "vscode" => "Visual Studio Code",
        "WezTerm" => "WezTerm",
        "ghostty" => "Ghostty",
        _ => "your terminal app",
    };
}
