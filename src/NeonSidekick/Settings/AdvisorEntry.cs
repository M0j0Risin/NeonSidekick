namespace NeonSidekick.Settings;

/// <summary>
/// One profile advisor (2026-10-10, the user's ask): another profile of this home, named by <see cref="Profile"/>, and the
/// free-text <see cref="Role"/> it advises in ("database architect", "UX designer"). An entry of
/// <see cref="AppSettingsData.AdvisorList"/>; <c>neon_advisor</c> and <c>/advisor</c> ask it on its own profile's server,
/// under its own persona and this role. One entry per profile.
/// </summary>
public sealed record AdvisorEntry(string Profile, string Role = "");
