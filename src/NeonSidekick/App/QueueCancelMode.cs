using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;

namespace NeonSidekick.App;

/// <summary>What a cancelled reply does to the <see cref="MessageQueue"/> (<see cref="QueueCancelMode"/>).</summary>
public enum QueueCancel
{
    /// <summary>Nothing is sent by itself; the next message the user sends runs first, and its normal end resumes the drain.</summary>
    Hold,

    /// <summary>The next queued message is sent at once, as after a normal end.</summary>
    Drain,

    /// <summary>Every queued message is dropped, with a notice.</summary>
    Empty,
}

/// <summary>
/// The setting <c>Queue cancel mode</c> (2026-09-18): the three words the operator picks from
/// (<c>hold</c>, <c>drain</c>, <c>empty</c>) and their mapping to <see cref="QueueCancel"/>, the
/// <see cref="Web.NetworkMode"/> shape. <see cref="Resolve"/> is the one place the saved string
/// becomes the enum: a hand-edited value that is none of them falls back to <see cref="Default"/>
/// with a warning.
/// </summary>
public static class QueueCancelMode
{
    /// <summary>Empty. The compiled default (hold until 2026-09-20, the user's call), pinned by <c>AppSettingsTests</c>.</summary>
    public const string Default = "empty";

    /// <summary>The modes in menu order.</summary>
    public static readonly string[] Names = { "hold", "drain", "empty" };

    private const string Category = "Screen";

    /// <summary>Trims and ignores case; false (and <see cref="QueueCancel.Hold"/>) for anything that is not one of <see cref="Names"/>.</summary>
    public static bool TryParse(string? text, out QueueCancel mode)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "hold": mode = QueueCancel.Hold; return true;
            case "drain": mode = QueueCancel.Drain; return true;
            case "empty": mode = QueueCancel.Empty; return true;
            default: mode = QueueCancel.Hold; return false;
        }
    }

    /// <summary>The saved word for <paramref name="mode"/>.</summary>
    public static string Name(QueueCancel mode) => mode switch
    {
        QueueCancel.Drain => "drain",
        QueueCancel.Empty => "empty",
        _ => "hold",
    };

    /// <summary>The menu hint next to a mode. Pinned.</summary>
    public static string Describe(string name) => name switch
    {
        "hold" => "a canceled reply holds the queue; your next message runs first, then it resumes",
        "drain" => "a canceled reply sends the next queued message at once",
        "empty" => "a canceled reply drops every queued message",
        _ => "",
    };

    /// <summary>The mode in force for <paramref name="effective"/>; an unknown saved value warns and uses <see cref="Default"/>.</summary>
    public static QueueCancel Resolve(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        if (TryParse(effective.QueueCancelMode, out var mode))
        {
            return mode;
        }

        DiagnosticLog.Warn(Category,
            $"{nameof(AppSettingsData.QueueCancelMode)}='{effective.QueueCancelMode}' is not one of {string.Join(", ", Names)}. Using {Default}.");
        TryParse(Default, out mode);
        return mode;
    }
}
