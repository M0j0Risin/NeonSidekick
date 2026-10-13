using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;

namespace NeonSidekick.App;

/// <summary>What the model may do with long-term memory (<see cref="MemoryMode"/>).</summary>
public enum MemoryAccess
{
    /// <summary><c>save_memory</c> and <c>recall_memory</c> both offered: the old Memory switch on.</summary>
    ReadWrite,

    /// <summary><c>recall_memory</c> alone: the model reads what is remembered and is never offered <c>save_memory</c>.</summary>
    ReadOnly,

    /// <summary>Neither tool, no memory section, no opening recall: the old Memory switch off.</summary>
    Disabled,
}

/// <summary>
/// The setting <c>Memory mode</c> (2026-10-04, the user's ask and words: the General tab's Memory switch, a bool until then, became
/// <c>read-write</c> / <c>read-only</c> / <c>disabled</c> (<c>off</c> since 2026-10-13), read-write the default; no migration, the earlier renames' call — a saved
/// <c>"Memory": false</c> is skipped and the default stands). Read-only is about the model alone: <c>/remember</c>, a row removed on
/// <c>/memory</c> and <c>/memory forget</c> stay the user's hand; disabled refuses <c>/remember</c> as off did. The
/// <see cref="BotChatMemoryMode"/> shape: <see cref="Resolve"/> is the one place the saved string becomes the enum, a hand-edited
/// value falling back to <see cref="Default"/> with a warning once per value.
/// </summary>
public static class MemoryMode
{
    public const string ReadWrite = "read-write";
    public const string ReadOnly = "read-only";
    public const string Off = "off";   // "disabled" until 2026-10-13 (the settings verbiage pass), still read (Retired)

    /// <summary>Read and write: the compiled default (the user's call), what the old switch's on was. Pinned.</summary>
    public const string Default = ReadWrite;

    /// <summary>The modes in menu order.</summary>
    public static readonly string[] Names = { ReadWrite, ReadOnly, Off };

    /// <summary>The words retired on 2026-10-13 (the settings verbiage pass, the user's call), still read: old → new (<see cref="RetiredWords"/>).</summary>
    internal static readonly IReadOnlyDictionary<string, string> Retired = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["disabled"] = Off,
    };

    private const string Category = "Screen";

    /// <summary>
    /// Trims and ignores case; false (and <see cref="MemoryAccess.ReadWrite"/>) for anything that is not one of <see cref="Names"/>.
    /// <c>off</c> is the value since 2026-10-13; <c>on</c> is <c>/memory</c>'s alias for read-write (<see cref="ChatScreen.MemoryOnWord"/>), read there.
    /// </summary>
    public static bool TryParse(string? text, out MemoryAccess mode)
    {
        switch (RetiredWords.Current(text, Retired))
        {
            case ReadWrite: mode = MemoryAccess.ReadWrite; return true;
            case ReadOnly: mode = MemoryAccess.ReadOnly; return true;
            case Off: mode = MemoryAccess.Disabled; return true;
            default: mode = MemoryAccess.ReadWrite; return false;
        }
    }

    /// <summary>The saved name of <paramref name="mode"/>.</summary>
    public static string Name(MemoryAccess mode) => mode switch
    {
        MemoryAccess.ReadOnly => ReadOnly,
        MemoryAccess.Disabled => Off,
        _ => ReadWrite,
    };

    /// <summary>The menu hint next to a mode. Pinned.</summary>
    public static string Describe(string name) => name switch
    {
        ReadWrite => "the model reads and saves memories",
        ReadOnly => "the model reads memories, never saves one",
        Off => "no memory: nothing read, nothing saved",
        _ => "",
    };

    /// <summary>The unknown value last warned of, so <see cref="Resolve"/>'s warning is written once per value (it is read every turn).</summary>
    private static string? s_warned;

    /// <summary>The mode in force for <paramref name="effective"/>; an unknown saved value uses <see cref="Default"/> and warns once while it stays the same.</summary>
    public static MemoryAccess Resolve(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        if (TryParse(effective.MemoryMode, out var mode))
        {
            return mode;
        }

        string value = effective.MemoryMode ?? "";
        if (string.Equals(Interlocked.Exchange(ref s_warned, value), value, StringComparison.Ordinal))
        {
            return MemoryAccess.ReadWrite;
        }

        DiagnosticLog.Warn(Category,
            $"{nameof(AppSettingsData.MemoryMode)}='{effective.MemoryMode}' is not one of {string.Join(", ", Names)}. Using {Default}.");
        return MemoryAccess.ReadWrite;
    }

    /// <summary>Memory on at all (read-write or read-only): the section, the list and <c>recall_memory</c>. What the old switch's on meant.</summary>
    public static bool Enabled(AppSettingsData effective) => Resolve(effective) != MemoryAccess.Disabled;

    /// <summary>The model may save (read-write): <c>save_memory</c> offered and the save sentences in the prompt.</summary>
    public static bool Saves(AppSettingsData effective) => Resolve(effective) == MemoryAccess.ReadWrite;
}
