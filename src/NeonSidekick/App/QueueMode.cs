using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;

namespace NeonSidekick.App;

/// <summary>How messages meet the <see cref="MessageQueue"/> (<see cref="QueueMode"/>).</summary>
public enum Queueing
{
    /// <summary>A message sent while a reply runs is queued and sent when the reply ends (what <c>Queue messages</c> on did).</summary>
    Automatic,

    /// <summary>Every typed message is queued, at the idle line too, and goes only on the <c>/queue</c> pane's send, one per press.</summary>
    Manual,

    /// <summary>No queue: a line typed under a reply waits for the idle line unlisted and is sent as the reply ends (what <c>Queue messages</c> off did).</summary>
    Disabled,
}

/// <summary>
/// The setting <c>Queue mode</c> (2026-10-09, the user's call: the on/off <c>Queue messages</c> became three words, its key
/// <c>QueueMessages</c> retired with no migration, so an old profile reads <see cref="Default"/>): <c>automatic</c>,
/// <c>manual</c> and <c>off</c>, and their mapping to <see cref="Queueing"/> — the <see cref="QueueCancelMode"/> shape.
/// <see cref="Resolve"/> is the one place the saved string becomes the enum: a hand-edited value that is none of them falls
/// back to <see cref="Default"/> with a warning.
/// </summary>
public static class QueueMode
{
    /// <summary>Automatic, what the switch's on did. The compiled default, pinned by <c>AppSettingsTests</c>.</summary>
    public const string Default = "automatic";

    /// <summary>The modes in menu order.</summary>
    public static readonly string[] Names = { "automatic", "manual", "off" };

    /// <summary>The words retired on 2026-10-13 (the settings verbiage pass, the user's call), still read: old → new (<see cref="RetiredWords"/>).</summary>
    internal static readonly IReadOnlyDictionary<string, string> Retired = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["disabled"] = "off",
    };

    private const string Category = "Screen";

    /// <summary>Trims and ignores case; false (and <see cref="Queueing.Automatic"/>) for anything that is not one of <see cref="Names"/>.</summary>
    public static bool TryParse(string? text, out Queueing mode)
    {
        switch (RetiredWords.Current(text, Retired))
        {
            case "automatic": mode = Queueing.Automatic; return true;
            case "manual": mode = Queueing.Manual; return true;
            case "off": mode = Queueing.Disabled; return true;
            default: mode = Queueing.Automatic; return false;
        }
    }

    /// <summary>The saved word for <paramref name="mode"/>.</summary>
    public static string Name(Queueing mode) => mode switch
    {
        Queueing.Manual => "manual",
        Queueing.Disabled => "off",
        _ => "automatic",
    };

    /// <summary>The menu hint next to a mode. Pinned.</summary>
    public static string Describe(string name) => name switch
    {
        "automatic" => "messages sent during a reply are queued and sent when it ends",
        "manual" => "messages are always queued and sent one at a time with ➤ send on /queue",
        "off" => "no queue: a line sent during a reply goes when it ends, unlisted",
        _ => "",
    };

    /// <summary>The mode in force for <paramref name="effective"/>; an unknown saved value warns and uses <see cref="Default"/>.</summary>
    public static Queueing Resolve(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        if (TryParse(effective.QueueMode, out var mode))
        {
            return mode;
        }

        DiagnosticLog.Warn(Category,
            $"{nameof(AppSettingsData.QueueMode)}='{effective.QueueMode}' is not one of {string.Join(", ", Names)}. Using {Default}.");
        TryParse(Default, out mode);
        return mode;
    }
}
