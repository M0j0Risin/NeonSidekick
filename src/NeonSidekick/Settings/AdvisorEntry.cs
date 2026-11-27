using System.Text.Json.Serialization;

namespace NeonSidekick.Settings;

/// <summary>
/// One profile advisor (2026-10-10, the user's ask): another profile of this home, named by <see cref="Profile"/>, and the
/// free-text <see cref="Role"/> it advises in ("database architect", "UX designer"). An entry of
/// <see cref="AppSettingsData.AdvisorList"/>; <c>neon_advisor</c> and <c>/advisor</c> ask it on its own profile's server,
/// under its own persona and this role. One entry per profile. <see cref="Description"/> (2026-11-22, the advisor templates) is a line on
/// when to ask it ("schema design, query plans and indexes, migrations"), read after the role in <c>neon_advisor</c>'s description so the
/// model knows which advisor a question is for; "" for none (an older <c>profile.json</c> has none). <see cref="Off"/> (2026-11-27, the
/// user's ask): out of service, its role, description and threads kept — <c>neon_advisor</c> neither lists nor calls it and <c>/advisor</c>
/// refuses it; written only when true, so an older file reads as on.
/// </summary>
public sealed record AdvisorEntry(string Profile, string Role = "", string Description = "",
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Off = false);
