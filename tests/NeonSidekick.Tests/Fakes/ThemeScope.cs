using NeonSidekick.UI;

namespace NeonSidekick.Tests.Fakes;

/// <summary>
/// Puts synthwave back in force when disposed (2026-09-23): <see cref="Theme"/> is process-wide, and
/// every test that switches it (<c>/theme</c>, the <c>Theme</c> row, <see cref="Theme.Use"/>) must
/// leave the default behind for the tests that pin its hex. The suite runs serially.
/// </summary>
public sealed class ThemeScope : IDisposable
{
    /// <summary>Starts from synthwave, whatever an earlier test left.</summary>
    public ThemeScope() => Theme.Use(ShippedThemes.Synthwave);

    public void Dispose() => Theme.Use(ShippedThemes.Synthwave);
}
