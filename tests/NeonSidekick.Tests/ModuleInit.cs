using System.Runtime.CompilerServices;

namespace NeonSidekick.Tests;

/// <summary>
/// The test process runs with the app's cultures (2026-09-23): <c>InvariantGlobalization</c> is off in both
/// projects since SqlClient refuses it, and <c>Program.cs</c> pins every culture to invariant first thing —
/// so does this, before any test runs, or a machine's own culture would leak into formats the app never sees.
/// <para>On a Mac the secrets the tests encrypt use a Keychain key of their own, <c>NeonSidekick.Tests</c> (later on 2026-10-06): under
/// the app's name the suite made the app's real key in the login Keychain, trusting <c>dotnet</c>.</para>
/// <para>On a Mac ImageIO is registered as MagicScaler's codecs (2026-10-07), as <c>Program.cs</c> does, before any test touches a picture.</para>
/// <para>Synthwave is put in force too (2026-10-05): the app starts on <see cref="UI.ThemeName.Default"/>, collider since that day,
/// but the suite's renders pin synthwave's colours, and <see cref="Fakes.ThemeScope"/> puts it back after every theme test.</para>
/// </summary>
internal static class ModuleInit
{
    /// <summary>The Keychain service the tests' key lives under on a Mac, never the app's.</summary>
    public const string TestKeychainService = "NeonSidekick.Tests";

    [ModuleInitializer]
    internal static void PinCultures()
    {
        App.CulturePin.Apply();
        if (OperatingSystem.IsMacOS())
        {
            Sql.MacKeychain.KeyService = TestKeychainService;
            Images.ImageIOCodecs.Register();
        }

        UI.Theme.Use(Fakes.ShippedThemes.Synthwave);
    }
}
