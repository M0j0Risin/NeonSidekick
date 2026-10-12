namespace NeonSidekick.UI;

/// <summary>
/// One of the four rows under the pane's lower rule (2026-10-12, the user's ask: "freely arrange the order" of the hint bar,
/// the toolbar, the performance bar and the home bar): <see cref="ScreenPane.BarOrder"/> draws them in the order it answers,
/// <see cref="App.PaneBars"/> reads it from <see cref="Settings.AppSettingsData.BarOrder"/>.
/// </summary>
public enum PaneBar
{
    /// <summary>The hint row (the busy row while a turn runs): always drawn.</summary>
    Hint,

    /// <summary>The toolbar (<c>Show toolbar</c>).</summary>
    Toolbar,

    /// <summary>The performance bar (<c>Show performance bar</c>).</summary>
    Perf,

    /// <summary>The home bar (<c>Show home bar</c>).</summary>
    Home,
}
