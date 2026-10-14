namespace NeonSidekick.UI;

/// <summary>How the Hints bar takes its row (<see cref="HintsBarMode"/>).</summary>
public enum HintsBarStyle
{
    /// <summary>A row while there is a hint, held until the next natural redraw or the idle timeout.</summary>
    Auto,

    /// <summary>The row always, blank while there is nothing to say.</summary>
    Always,

    /// <summary>No row: the hints ride the Status bar, as the hint row had them until 2026-10-14.</summary>
    Off,
}

/// <summary>
/// The setting <c>Hints bar</c> (2026-10-14, the user's ask: the hint row split into a Status bar for the state and a Hints bar of
/// its own for the keys, shown over it while there is something to say): <c>auto</c> (the default) gives the bar a row while a hint
/// stands and holds it, blank, until the next send, turn end, menu or resize, or <see cref="ScreenPane.HintsIdle"/> with no hint
/// and no key, so typing never bounces the input row; <c>always</c> keeps the row; <c>off</c> puts the hints back on the Status bar.
/// A display setting: an unknown word reads as <see cref="Default"/> without a warning, as the bars' checklists do. Pure.
/// </summary>
public static class HintsBarMode
{
    public const string Auto = "auto";
    public const string Always = "always";
    public const string Off = "off";

    /// <summary>The compiled default. Pinned.</summary>
    public const string Default = Auto;

    /// <summary>The words in menu order. Pinned.</summary>
    public static readonly string[] Names = [Auto, Always, Off];

    /// <summary>The style for the saved word <paramref name="text"/> (trimmed, any case); null or unknown is <see cref="Default"/>'s.</summary>
    public static HintsBarStyle Resolve(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        Always => HintsBarStyle.Always,
        Off => HintsBarStyle.Off,
        _ => HintsBarStyle.Auto,
    };

    /// <summary>The saved word for <paramref name="style"/>.</summary>
    public static string Name(HintsBarStyle style) => style switch
    {
        HintsBarStyle.Always => Always,
        HintsBarStyle.Off => Off,
        _ => Auto,
    };

    /// <summary>The menu's note beside a word.</summary>
    public static string Describe(string name) => name switch
    {
        Auto => "a row while there is a hint, held until the next send",
        Always => "always a row, blank while there is nothing to say",
        Off => "no row: the hints share the Status bar",
        _ => "",
    };

    /// <summary>
    /// The one hint the bar shows (2026-10-14, the user's pick: a priority ladder, never joined): the first non-empty of
    /// <paramref name="alert"/> (the kill switch's), <paramref name="armed"/> (exit, rewind, splash delete), <paramref name="overlay"/>
    /// (an open pane's keys) and <paramref name="keys"/> (the <c>/read</c> keys, the splash's or the picture strip's arrows); null for
    /// none. Pure.
    /// </summary>
    public static string? Pick(string? alert, string? armed, string? overlay, string? keys) =>
        !string.IsNullOrEmpty(alert) ? alert
        : !string.IsNullOrEmpty(armed) ? armed
        : !string.IsNullOrEmpty(overlay) ? overlay
        : !string.IsNullOrEmpty(keys) ? keys
        : null;
}
