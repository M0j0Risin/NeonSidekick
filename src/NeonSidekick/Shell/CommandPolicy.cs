using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;

namespace NeonSidekick.Shell;

/// <summary>What stands between the model's <c>run_command</c> and the shell (2026-09-21, the user's three words).</summary>
public enum CommandPolicyMode
{
    /// <summary>No shell tool is offered at all: the group's switch.</summary>
    Off,

    /// <summary>Every command whose prefixes are not all on the allow list is put to the user first; with no screen to ask on it is refused.</summary>
    Ask,

    /// <summary>Everything runs, nothing is asked. The user's word for it.</summary>
    Yolo,
}

/// <summary>
/// The setting <c>Shell command policy</c> (2026-09-21): the three words the operator picks from
/// (<c>off</c>, <c>ask</c>, <c>yolo</c>) and their mapping to <see cref="CommandPolicyMode"/>, the
/// <see cref="Web.NetworkMode"/> shape. It is the Shell group's switch: <c>off</c> offers no shell
/// tool and drops the rule sentence, as <c>Git native tools</c> off does for git. <see cref="Resolve"/> is
/// the one place the saved string becomes the enum: a hand-edited value that is none of them falls
/// back to <see cref="Default"/> with a warning.
/// </summary>
public static class CommandPolicy
{
    /// <summary>
    /// Off. The compiled default, pinned by <c>AppSettingsTests</c>: a fresh profile offers no shell tool at all (2026-09-29,
    /// the user's call; <c>ask</c> until then — a fresh profile never ran a command behind the user's back, and now it runs none).
    /// </summary>
    public const string Default = "off";

    /// <summary>The modes in menu order.</summary>
    public static readonly string[] Names = { "off", "ask", "yolo" };

    /// <summary>Trims and ignores case; false (and <see cref="Default"/>'s <see cref="CommandPolicyMode.Off"/>, so a display of a bad value agrees with <see cref="Resolve"/>) for anything that is not one of <see cref="Names"/>.</summary>
    public static bool TryParse(string? text, out CommandPolicyMode mode)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "off": mode = CommandPolicyMode.Off; return true;
            case "ask": mode = CommandPolicyMode.Ask; return true;
            case "yolo": mode = CommandPolicyMode.Yolo; return true;
            default: mode = CommandPolicyMode.Off; return false;
        }
    }

    /// <summary>The saved word for <paramref name="mode"/>.</summary>
    public static string Name(CommandPolicyMode mode) => mode switch
    {
        CommandPolicyMode.Off => "off",
        CommandPolicyMode.Yolo => "yolo",
        _ => "ask",
    };

    /// <summary>The menu hint next to a mode. Pinned.</summary>
    public static string Describe(string name) => name switch
    {
        "off" => "no shell or script tool is offered",
        "ask" => "you approve each command not on the allow list",
        "yolo" => "every command runs, nothing is asked",
        _ => "",
    };

    /// <summary>The mode in force for <paramref name="effective"/>; an unknown saved value warns and uses <see cref="Default"/>.</summary>
    public static CommandPolicyMode Resolve(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        if (TryParse(effective.ShellCommandPolicy, out var mode))
        {
            return mode;
        }

        DiagnosticLog.Warn(ShellKinds.Category,
            $"{nameof(AppSettingsData.ShellCommandPolicy)}='{effective.ShellCommandPolicy}' is not one of {string.Join(", ", Names)}. Using {Default}.");
        TryParse(Default, out mode);
        return mode;
    }
}
