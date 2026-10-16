using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;

namespace NeonSidekick.Sessions;

/// <summary>Which session names the rule above the input row shows (<c>Session show name</c>, 2026-09-18).</summary>
public enum SessionNameDisplay
{
    /// <summary>Whatever title the store holds: the first line, then the model's slug in its place.</summary>
    AllNames,

    /// <summary>A model-written or typed title alone; the automatic first line never shows.</summary>
    ModelWritten,

    /// <summary>The rule stays bare.</summary>
    None,
}

/// <summary>
/// The show-name setting: the three words the operator picks from (<c>all</c>,
/// <c>model-written</c>, <c>none</c>) and their mapping to <see cref="SessionNameDisplay"/>, the
/// <see cref="SessionNamingMode"/> shape. <see cref="Resolve"/> is the one place the saved string
/// becomes the enum: a hand-edited value that is none of them falls back to <see cref="Default"/> with a warning.
/// </summary>
public static class SessionShowName
{
    /// <summary>Every name. The compiled default (the user's call, 2026-09-18), pinned by <c>AppSettingsTests</c>.</summary>
    public const string Default = "all";

    /// <summary>The choices in menu order.</summary>
    public static readonly string[] Names = { "all", "model-written", "none" };

    /// <summary>The words retired on 2026-10-13 (the settings verbiage pass, the user's call), still read: old → new (<see cref="RetiredWords"/>).</summary>
    internal static readonly IReadOnlyDictionary<string, string> Retired = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["all-names"] = "all",
    };

    private const string Category = "Sessions";

    /// <summary>Trims and ignores case; false for anything that is not one of <see cref="Names"/>.</summary>
    public static bool TryParse(string? text, out SessionNameDisplay display)
    {
        switch (RetiredWords.Current(text, Retired))
        {
            case "all": display = SessionNameDisplay.AllNames; return true;
            case "model-written": display = SessionNameDisplay.ModelWritten; return true;
            case "none": display = SessionNameDisplay.None; return true;
            default: display = SessionNameDisplay.AllNames; return false;
        }
    }

    /// <summary>The saved word for <paramref name="display"/>.</summary>
    public static string Name(SessionNameDisplay display) => display switch
    {
        SessionNameDisplay.ModelWritten => "model-written",
        SessionNameDisplay.None => "none",
        _ => "all",
    };

    /// <summary>The menu hint next to a choice. Pinned.</summary>
    public static string Describe(string name) => name switch
    {
        "all" => "every session name shows on the line above the input row",
        "model-written" => "only a model-written or typed name shows; the first line never does",
        "none" => "the rule stays bare",
        _ => "",
    };

    /// <summary>The choice in force for <paramref name="effective"/>; an unknown saved value warns and uses <see cref="Default"/>.</summary>
    public static SessionNameDisplay Resolve(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        if (TryParse(effective.SessionShowName, out var display))
        {
            return display;
        }

        DiagnosticLog.Warn(Category,
            $"{nameof(AppSettingsData.SessionShowName)}='{effective.SessionShowName}' is not one of {string.Join(", ", Names)}. Using {Default}.");
        TryParse(Default, out display);
        return display;
    }
}
