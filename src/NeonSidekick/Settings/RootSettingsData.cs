namespace NeonSidekick.Settings;

/// <summary>
/// The root <c>settings.json</c> since profiles: nothing but which profile is loaded. Everything
/// the user can change lives in that profile's <c>profile.json</c> (<see cref="AppSettingsData"/>).
///
/// <para><see cref="SchemaVersion"/> is 2 because the file changed meaning, not merely shape: a
/// version-1 root file holds the full settings and has no <see cref="Profile"/> property, which
/// is how <see cref="AppSettings"/> recognises one to migrate. Unknown properties are ignored on
/// read, so an old file deserialises as a pointer to <see cref="Profiles.DefaultName"/>.</para>
/// </summary>
public sealed class RootSettingsData
{
    public int SchemaVersion { get; set; } = 2;

    /// <summary>The loaded profile's directory name under <c>profiles</c>.</summary>
    public string Profile { get; set; } = Profiles.DefaultName;

    /// <summary>
    /// Whether the screen has offered the desktop shortcut (2026-10-09, the user's ask: a first start asks once whether to put
    /// <c>/shortcut</c>'s shortcut on the desktop, with or without <c>--log</c>). False until the offer is answered — any answer,
    /// ESC too, the user's pick — so a new home is asked at its first start and a home from before this key at its next one.
    /// Set back to false by hand to be asked again. Kept across the pointer's rewrites (a profile switch).
    /// </summary>
    public bool ShortcutOffered { get; set; }
}
