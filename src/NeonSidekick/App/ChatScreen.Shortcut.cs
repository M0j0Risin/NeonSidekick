using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;
using NeonSidekick.Shortcuts;
using NeonSidekick.UI;

namespace NeonSidekick.App;

// ── /shortcut (2026-10-07) ────────────────────────────────────────────────

internal sealed partial class ChatScreen
{
    /// <summary>
    /// Writes <c>/shortcut</c>'s desktop shortcut (<see cref="WindowsShortcutWriter"/>, <see cref="MacShortcutWriter"/> since 2026-10-08,
    /// a fake in tests); null elsewhere.
    /// </summary>
    private readonly IShortcutWriter? _shortcutWriter;

    /// <summary>
    /// <c>/shortcut [profile] [--log]</c> (2026-10-07, the user's ask): a desktop shortcut that starts this exe on the loaded profile,
    /// or the one named (as the profile list spells it), and with <c>--log</c> a stamped log beside the exe
    /// (<see cref="DesktopShortcut"/> decides the name, the command line and the folder it starts in). One already there is
    /// replaced and the notice says so. A temporary profile is allowed: <c>--profile</c> opens one, only the pointer never lands
    /// there. On a Mac (2026-10-08) a <c>.command</c> file in the same place, with the same command line; elsewhere, an error.
    /// </summary>
    private void HandleShortcut(string args)
    {
        if (_shortcutWriter is not { } writer)
        {
            _transcript.Error(ShortcutText.NotHere);
            return;
        }

        if (!DesktopShortcut.TryParse(args, out var request))
        {
            _transcript.Error(ShortcutText.Usage);
            return;
        }

        string profile = _settings.ProfileName;
        if (request.Profile is { } typed)
        {
            if (Profiles.Resolve(_settings.StorageDirectory, typed) is not { } found)
            {
                _transcript.Error(ProfileMissingError(typed));
                return;
            }

            profile = found;
        }

        if (writer.Executable is not { } exe)
        {
            _transcript.Error(ShortcutText.NoOwnExecutable);
            return;
        }

        var spec = DesktopShortcut.For(exe, writer.DesktopFolder, profile, request.Log, writer.Kind);
        bool replaced = File.Exists(spec.LinkPath);
        try
        {
            writer.Write(spec);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _transcript.Error(ShortcutText.WriteFailed(spec.LinkPath, ex.Message));
            return;
        }

        DiagnosticLog.Info(ShortcutText.Category, ShortcutText.LogLine(spec));
        _transcript.Notice(ShortcutText.Made(spec.LinkPath, spec.Arguments, replaced));
    }

    /// <summary>
    /// <c>/shortcut</c>'s argument list: every profile (the loaded one marked) and <c>--log</c>; after a profile, <c>--log</c>;
    /// after <c>--log</c>, the profiles. Nothing once both are typed.
    /// </summary>
    internal static IReadOnlyList<CompletionItem> ShortcutItems(string argText, IReadOnlyList<string> profiles, string? loaded)
    {
        CompletionItem Profile(string prefix, string name) =>
            new(prefix + name, Profiles.NameEquals(name, loaded) ? ShortcutText.LoadedProfileNote : ShortcutText.ProfileNote);

        var log = new CompletionItem(DesktopShortcut.LogSwitch, ShortcutText.LogNote);
        int space = argText.IndexOf(' ', StringComparison.Ordinal);
        if (space < 0)
        {
            return MentionCompleter.Matches([.. profiles.Select(name => Profile("", name)), log], argText);
        }

        string first = argText[..space];
        if (string.Equals(first, DesktopShortcut.LogSwitch, StringComparison.OrdinalIgnoreCase))
        {
            return MentionCompleter.Matches(profiles.Select(name => Profile(first + " ", name)).ToList(), argText);
        }

        return MentionCompleter.Matches([new(first + " " + DesktopShortcut.LogSwitch, ShortcutText.LogNote)], argText);
    }
}
