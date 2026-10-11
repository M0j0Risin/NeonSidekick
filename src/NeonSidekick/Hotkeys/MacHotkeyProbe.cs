using System.Runtime.Versioning;
using NeonSidekick.Audio;
using NeonSidekick.Viewer;

namespace NeonSidekick.Hotkeys;

/// <summary>
/// A Mac's <c>/keycheck</c> probe (2026-10-11, the user's pick of the passive check after that day's spike): at each run's
/// <see cref="Begin"/> a fresh snapshot of macOS's own shortcuts (<c>CopySymbolicHotKeys</c>) and of the terminal's Option key
/// (Terminal.app's default profile, iTerm2's session profile by <c>ITERM_PROFILE</c>), then per chord <see cref="MacHotkeyRules.Decide"/>
/// with Carbon's exclusive register as the other-app check. The register runs on the main thread when the AppKit host runs it
/// (HIToolbox is the main thread's), else on the caller's (measured fine off the main thread in a process with no AppKit). Should the
/// main thread not answer in time, the chord is not checked (<see cref="MacHotkeyRules.MainThreadBusy"/>) and neither is the rest of
/// the run's: never a register off the main thread while the AppKit loop runs, nor one racing the posted one still queued there
/// (2026-10-11, code review).
/// It sees only what the caveat says: most apps' hotkeys (registered the shared way) and keyboard tools never show.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacHotkeyProbe(string? terminalProgram, string? itermProfile) : IHotkeyProbe
{
    private IReadOnlyList<SymbolicHotKey> _symbolic = [];
    private OptionKey _option = OptionKey.Unknown;
    private int _next;
    private bool _stalled;

    public bool Mac => true;

    public IReadOnlyList<string> Begin()
    {
        var notes = new List<string>();
        _stalled = false;
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
        if (_stalled)
        {
            return MacHotkeyRules.MainThreadBusy;
        }

        int status = RegisterOnMain(code, modifiers, (uint)Interlocked.Increment(ref _next));
        _stalled = status == MacHotkeyRules.MainThreadBusy;
        return status;
    }

    /// <summary>
    /// Carbon's exclusive register and release of one chord on the main thread while the AppKit loop runs, else on the caller's;
    /// <see cref="MacHotkeyRules.MainThreadBusy"/> when the main thread did not answer in time. The smoke's <c>keys:carbon</c> takes it too.
    /// </summary>
    internal static int RegisterOnMain(ushort code, uint modifiers, uint id)
    {
        if (!AppKitHost.IsRunning)
        {
            return MacHotkeyNative.ProbeExclusive(code, modifiers, id);
        }

        return AppKitHost.Invoke(() => MacHotkeyNative.ProbeExclusive(code, modifiers, id), out int status) ? status : MacHotkeyRules.MainThreadBusy;
    }
}
