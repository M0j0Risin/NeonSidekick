using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;

namespace NeonSidekick.Plans;

/// <summary>Who checks a Ralph pass's work once its own checks stand (<c>Ralph loop reviewer</c>, 2026-10-18).</summary>
public enum RalphReviewer
{
    /// <summary>No review: the verify command and the evidence alone.</summary>
    Off,

    /// <summary>A one-shot request to the chat's own model, with no conversation.</summary>
    Model,

    /// <summary>The first advisor offered.</summary>
    Advisor,

    /// <summary>An advisor when one is offered, else the model (the default).</summary>
    Any,
}

/// <summary>
/// The reviewer setting (2026-10-18, the Ralph loop: an independent look at each step that passed its checks): <c>off</c>, <c>model</c>,
/// <c>advisor</c>, <c>any</c> (the default), and their mapping to <see cref="RalphReviewer"/>, <c>ReflectionCooldownMode</c>'s shape.
/// <see cref="Resolve"/> is the one place the saved string becomes the enum.
/// </summary>
public static class RalphReviewerMode
{
    /// <summary>The compiled default (the user's call), pinned by <c>AppSettingsTests</c>.</summary>
    public const string Default = "any";

    /// <summary>The choices in menu order.</summary>
    public static readonly string[] Names = ["off", "model", "advisor", "any"];

    /// <summary>Trims and ignores case; false for anything that is not one of <see cref="Names"/>.</summary>
    public static bool TryParse(string? text, out RalphReviewer reviewer)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "off": reviewer = RalphReviewer.Off; return true;
            case "model": reviewer = RalphReviewer.Model; return true;
            case "advisor": reviewer = RalphReviewer.Advisor; return true;
            case "any": reviewer = RalphReviewer.Any; return true;
            default: reviewer = RalphReviewer.Any; return false;
        }
    }

    /// <summary>The saved word for <paramref name="reviewer"/>.</summary>
    public static string Name(RalphReviewer reviewer) => Names[(int)reviewer];

    /// <summary>The menu hint next to a choice. Pinned.</summary>
    public static string Describe(string name) => name switch
    {
        "off" => "no review: the step stands on its checks alone",
        "model" => "the chat's model judges each step that passed its checks",
        "advisor" => "the first advisor judges each step that passed its checks",
        "any" => "an advisor when one is offered, else the chat's model",
        _ => "",
    };

    /// <summary>The choice in force for <paramref name="effective"/>; an unknown saved value warns and uses <see cref="Default"/>.</summary>
    public static RalphReviewer Resolve(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        if (TryParse(effective.RalphLoopReviewer, out var reviewer))
        {
            return reviewer;
        }

        DiagnosticLog.Warn("Plan", $"{nameof(AppSettingsData.RalphLoopReviewer)}='{effective.RalphLoopReviewer}' is not one of {string.Join(", ", Names)}. Using {Default}.");
        TryParse(Default, out var fallback);
        return fallback;
    }
}
