namespace NeonSidekick.Comfy;

/// <summary>
/// Whether the ComfyUI picture strip and the picture viewer follow each other (2026-09-28, the user's ask: "the selection
/// of the image in the viewer update the selected image in the ComfyUI picture strip", and their setting, <c>ComfyUI picture
/// strip sync</c>, with its three names and its default): <see cref="ViewerOnly"/> — the viewer's own keys (← / →, Home /
/// End, the picture after a double-Del) move the strip's highlight, a picture the strip does not hold is ignored, the slide
/// show and an arriving picture never do; <see cref="BothWays"/> — that, and ← / → on the strip move an open viewer on the
/// strip's folder without bringing it forward; <see cref="Disabled"/> — neither. Pure.
/// </summary>
public static class StripSync
{
    public const string ViewerOnly = "viewer-only";
    public const string BothWays = "both-ways";
    public const string Disabled = "disabled";

    /// <summary>The names in the picker's order.</summary>
    public static readonly string[] Names = [ViewerOnly, BothWays, Disabled];

    public const string Default = ViewerOnly;

    /// <summary>The name in force: the setting in lower case, anything unknown read as <see cref="Default"/>.</summary>
    public static string Resolve(string? name)
    {
        string resolved = name?.Trim().ToLowerInvariant() ?? "";
        return Names.Contains(resolved, StringComparer.Ordinal) ? resolved : Default;
    }

    /// <summary>Whether the viewer's keys move the strip's highlight.</summary>
    public static bool ViewerSyncs(string? name) => Resolve(name) != Disabled;

    /// <summary>Whether the strip's arrows move an open viewer.</summary>
    public static bool StripSyncs(string? name) => Resolve(name) == BothWays;

    /// <summary>A one-line description for the picker. Pinned.</summary>
    public static string Describe(string name) => Resolve(name) switch
    {
        BothWays => "the viewer's keys move the strip, and the strip's arrows move the viewer",
        Disabled => "the strip and the viewer each keep their own picture",
        _ => "the viewer's keys move the strip's highlight",
    };
}
