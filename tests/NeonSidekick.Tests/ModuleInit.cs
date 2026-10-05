using System.Runtime.CompilerServices;

namespace NeonSidekick.Tests;

/// <summary>
/// The test process runs with the app's cultures (2026-09-23): <c>InvariantGlobalization</c> is off in both
/// projects since SqlClient refuses it, and <c>Program.cs</c> pins every culture to invariant first thing —
/// so does this, before any test runs, or a machine's own culture would leak into formats the app never sees.
/// <para>Synthwave is put in force too (2026-10-05): the app starts on <see cref="UI.ThemeName.Default"/>, collider since that day,
/// but the suite's renders pin synthwave's colours, and <see cref="Fakes.ThemeScope"/> puts it back after every theme test.</para>
/// </summary>
internal static class ModuleInit
{
    [ModuleInitializer]
    internal static void PinCultures()
    {
        App.CulturePin.Apply();
        UI.Theme.Use(Fakes.ShippedThemes.Synthwave);
    }
}
