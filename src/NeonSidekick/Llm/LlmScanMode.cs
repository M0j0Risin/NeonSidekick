using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;

namespace NeonSidekick.Llm;

/// <summary>Where discovery looks for an LLM server (<see cref="LlmEndpointProbe.DiscoverAllAsync"/>).</summary>
public enum ScanScope
{
    /// <summary>The usual ports on 127.0.0.1 alone.</summary>
    Local,

    /// <summary>The usual ports on every other machine of the local network; this machine skipped.</summary>
    Remote,

    /// <summary>This machine first, then the local network.</summary>
    Both,

    /// <summary>No scan at all: a blank URL connects nothing and says so; only a URL set by hand connects.</summary>
    Disabled,
}

/// <summary>
/// The scan-mode setting: the four words the operator picks from (<c>local</c>, <c>remote</c>,
/// <c>both</c>, <c>disabled</c>) and their mapping to <see cref="ScanScope"/>, the way
/// <see cref="CompactType"/> maps the compact words. <see cref="Resolve"/> is the one place the
/// saved string becomes the enum: a hand-edited value that is none of them falls back to
/// <see cref="Default"/> with a warning.
/// </summary>
public static class LlmScanMode
{
    /// <summary>
    /// No scan. The compiled default, pinned by <c>AppSettingsTests</c>: since 2026-09-29 (the user's call; <c>local</c>, this
    /// machine alone, until then) a fresh profile asks no port until a server is picked or a mode is.
    /// </summary>
    public const string Default = "disabled";

    /// <summary>The modes in menu order; <c>disabled</c> last (2026-09-15).</summary>
    public static readonly string[] Names = { "local", "remote", "both", "disabled" };

    private const string Category = "Llm";

    /// <summary>Trims and ignores case; false (and <see cref="Default"/>'s <see cref="ScanScope.Disabled"/>, so a display of a bad value agrees with <see cref="Resolve"/>) for anything that is not one of <see cref="Names"/>.</summary>
    public static bool TryParse(string? text, out ScanScope scope)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "local": scope = ScanScope.Local; return true;
            case "remote": scope = ScanScope.Remote; return true;
            case "both": scope = ScanScope.Both; return true;
            case "disabled": scope = ScanScope.Disabled; return true;
            default: scope = ScanScope.Disabled; return false;
        }
    }

    /// <summary>The saved word for <paramref name="scope"/>.</summary>
    public static string Name(ScanScope scope) => scope switch
    {
        ScanScope.Remote => "remote",
        ScanScope.Both => "both",
        ScanScope.Disabled => "disabled",
        _ => "local",
    };

    /// <summary>The menu hint next to a mode. Pinned.</summary>
    public static string Describe(string name) => name switch
    {
        "local" => "the usual ports on this machine (127.0.0.1)",
        "remote" => "the usual ports on every other machine on the local network",
        "both" => "this machine first, then the local network",
        "disabled" => "no scan; set LLM URL by hand",
        _ => "",
    };

    /// <summary>Whether <paramref name="scope"/> reaches beyond this machine.</summary>
    public static bool IncludesNetwork(ScanScope scope) => scope is ScanScope.Remote or ScanScope.Both;

    /// <summary>Whether <paramref name="scope"/> scans at all: false for <see cref="ScanScope.Disabled"/> alone, when a blank URL connects nothing and a bare <c>/server</c> refuses.</summary>
    public static bool Scans(ScanScope scope) => scope != ScanScope.Disabled;

    /// <summary>The scope in force for <paramref name="effective"/>; an unknown saved value warns and uses <see cref="Default"/>.</summary>
    public static ScanScope Resolve(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        if (TryParse(effective.LlmScanMode, out var scope))
        {
            return scope;
        }

        DiagnosticLog.Warn(Category,
            $"{nameof(AppSettingsData.LlmScanMode)}='{effective.LlmScanMode}' is not one of {string.Join(", ", Names)}. Using {Default}.");
        TryParse(Default, out scope);
        return scope;
    }
}
