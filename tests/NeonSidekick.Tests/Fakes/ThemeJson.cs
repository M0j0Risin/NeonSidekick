using NeonSidekick.UI;

namespace NeonSidekick.Tests.Fakes;

/// <summary>
/// Theme files for the tests (2026-10-05): a theme file stands alone since that day, every colour role its own, so a test's file
/// starts from synthwave's fifteen colours and changes the ones it is about.
/// </summary>
internal static class ThemeJson
{
    /// <summary>The <c>"colors"</c> object: synthwave's, with <paramref name="changes"/> written over their roles (a value as given, a bad one too).</summary>
    public static string Colors(params (string Role, string Value)[] changes)
    {
        var synthwave = ShippedThemes.Synthwave;
        var pairs = ThemeKeys.Colors.Select((role, i) =>
        {
            string value = changes.LastOrDefault(c => c.Role == role).Value ?? Theme.ToHex(synthwave.ColorOf((ThemeColorSlot)i));
            return $"\"{role}\": \"{value}\"";
        });
        return "{ " + string.Join(", ", pairs) + " }";
    }

    /// <summary>A complete file: <paramref name="members"/> (raw JSON members, comma-separated, or null) and <see cref="Colors"/>.</summary>
    public static string File(string? members = null, params (string Role, string Value)[] changes) =>
        "{ " + (members is null ? "" : members + ", ") + "\"colors\": " + Colors(changes) + " }";
}
