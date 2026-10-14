namespace NeonSidekick.UI;

/// <summary>
/// One of the rows under the pane's lower rule (2026-10-12, the user's ask: "freely arrange the order" of the hint bar,
/// the toolbar, the performance bar and the home bar; 2026-10-14 the hint bar split into the Hints bar and the Status bar):
/// <see cref="ScreenPane.BarOrder"/> draws them in the order it answers, <see cref="App.PaneBars"/> reads it from
/// <see cref="Settings.AppSettingsData.BarOrder"/>.
/// </summary>
public enum PaneBar
{
    /// <summary>The Hints bar (2026-10-14): the keys' hints, a row only while there is one (<see cref="HintsBarMode"/>).</summary>
    Hints,

    /// <summary>The Status bar (the busy row while a turn runs; the hint row until 2026-10-14): always drawn.</summary>
    Status,

    /// <summary>The toolbar (<c>Toolbar</c>).</summary>
    Toolbar,

    /// <summary>The performance bar (<c>Performance bar</c>).</summary>
    Perf,

    /// <summary>The home bar (<c>Home bar</c>).</summary>
    Home,
}
