using System.Runtime.Versioning;
using NeonSidekick.Audio;
using NeonSidekick.Viewer;

namespace NeonSidekick.Hotkeys;

/// <summary>
/// A Mac's <c>/keycheck</c> probe (2026-10-11, the user's pick of the passive check after that day's spike): at each run's
/// <see cref="Begin"/> a fresh snapshot of macOS's own shortcuts (<c>CopySymbolicHotKeys</c>) and of the terminal's Option key
/// (Terminal.app's default profile, iTerm2's session profile by <c>ITERM_PROFILE</c>), then per chord <see cref="MacHotkeyRules.Decide"/>
/// with Carbon's exclusive register as the other-app check. The register runs on the main thread when the AppKit host runs it
/// (HIToolbox is the main thread's), else on the caller's (measured fine off the main thread in a process with no AppKit).
/// It sees only what the caveat says: most apps' hotkeys (registered the shared way) and keyboard tools never show.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacHotkeyProbe(string? terminalProgram, string? itermProfile) : IHotkeyProbe
{
    private IReadOnlyList<SymbolicHotKey> _symbolic = [];
    private OptionKey _option = OptionKey.Unknown;
    private int _next;

    public bool Mac => true;

    public IReadOnlyList<string> Begin()
    {
        var notes = new List<string>();
        _symbolic = MacHotkeyNative.SymbolicHotKeys(out int status);
        if (status != MacHotkeyRules.NoErr)
        {
            notes.Add(KeyCheckText.NoSystemList(status));
        }

        var (option, note) = terminalProgram switch
        {
            MacHotkeyRules.TerminalApp => TerminalApp(),
            MacHotkeyRules.Iterm => Iterm(),
            _ => (OptionKey.Unknown, KeyCheckText.UnknownOption(MicrophoneText.TerminalName(terminalProgram))),
        };
        _option = option;
        notes.Add(note);
        return notes;
    }

    public HotkeyProbeResult Probe(KeyChord chord) => MacHotkeyRules.Decide(chord, _symbolic, Register, _option);

    private static (OptionKey, string) TerminalApp()
    {
        var (profile, meta) = MacHotkeyNative.TerminalAppSettings();
        return MacHotkeyRules.TerminalAppOption(profile, meta);
    }

    private (OptionKey, string) Iterm()
    {
        var (profile, left, right) = MacHotkeyNative.ItermSettings(itermProfile);
        return MacHotkeyRules.ItermOption(profile, left, right);
    }

    private int Register(ushort code, uint modifiers)
    {
        uint id = (uint)Interlocked.Increment(ref _next);
        return AppKitHost.IsRunning && AppKitHost.Invoke(() => MacHotkeyNative.ProbeExclusive(code, modifiers, id), out int status)
            ? status
            : MacHotkeyNative.ProbeExclusive(code, modifiers, id);
    }
}
