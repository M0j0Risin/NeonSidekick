using NeonSidekick.Settings;

namespace NeonSidekick.Skills;

/// <summary>
/// The three folders a <see cref="SkillCatalog"/> scans, in precedence order (<see cref="SkillScope"/>).
/// Built per scan from the live settings (<see cref="For"/>), so a profile switch moves the first
/// one without a rebuild; the external one is fixed at composition (<see cref="DefaultExternalDirectory"/>,
/// or a test's temp folder).
/// <para><see cref="GlobalOn"/> (2026-10-13, the profile's <c>Global skills</c> switch): while false the global root is
/// neither read nor written — <see cref="Reads"/> leaves it out, <see cref="WritableScopes"/> too — though <see cref="Of"/> still
/// names its folder, which the skill records need to know which rows are not this profile's to touch.</para>
/// </summary>
public sealed record SkillRoots(string Profile, string Global, string External, bool GlobalOn = true)
{
    /// <summary>The folder name under the home and under a profile: <c>skills</c>.</summary>
    public const string DirectoryName = "skills";

    /// <summary>The cross-client convention under the user's home: <c>.agents\skills</c>.</summary>
    public const string ExternalDirectoryName = ".agents";

    private static readonly IReadOnlyList<SkillScope> ProfileOnly = [SkillScope.Profile];
    private static readonly IReadOnlyList<SkillScope> Both = [SkillScope.Profile, SkillScope.Global];

    /// <summary>
    /// The roots for the loaded profile: <c>&lt;profile&gt;\skills</c>, <c>&lt;home&gt;\skills</c>, <paramref name="external"/>, and the
    /// profile's <c>Global skills</c> switch, the folder and the switch read under one lock (<see cref="AppSettings.SkillsView"/>).
    /// </summary>
    public static SkillRoots For(AppSettings settings, string external)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(external);
        var (profile, global) = settings.SkillsView;
        return new SkillRoots(profile, settings.GlobalSkillsDirectory, Path.GetFullPath(external), global);
    }

    /// <summary>
    /// <c>%USERPROFILE%\.agents\skills</c>, the folder other agent clients share skills through;
    /// beside the binary when the process has no profile folder (the <see cref="AppSettings.ResolveStorageDirectory"/> rule).
    /// </summary>
    public static string DefaultExternalDirectory()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string home = string.IsNullOrWhiteSpace(profile) ? AppContext.BaseDirectory : profile;
        return Path.Combine(home, ExternalDirectoryName, DirectoryName);
    }

    /// <summary>The root for <paramref name="scope"/>.</summary>
    public string Of(SkillScope scope) => scope switch
    {
        SkillScope.Profile => Profile,
        SkillScope.Global => Global,
        _ => External,
    };

    /// <summary>
    /// Whether this profile may see <paramref name="scope"/>'s skills (2026-10-13): the profile's always, the global ones while
    /// <see cref="GlobalOn"/>, the external ones always here — that root is gated by the scan's own <c>external</c> flag.
    /// </summary>
    public bool Reads(SkillScope scope) => scope != SkillScope.Global || GlobalOn;

    /// <summary>The scopes this profile may write, in order (2026-10-13): the profile's, then the global while <see cref="GlobalOn"/>.</summary>
    public IReadOnlyList<SkillScope> WritableScopes => GlobalOn ? Both : ProfileOnly;

    /// <summary>Whether <paramref name="scope"/> is the global root while this profile has it switched off (2026-10-13).</summary>
    public bool Hides(SkillScope scope) => !Reads(scope);

    /// <summary>
    /// The roots <c>load_skill</c> reads from, for the working directory's refusal of a skill's file (2026-10-17,
    /// <see cref="Files.WorkingDirectory.SkillFolders"/>): none while <paramref name="agentSkills"/> is off, the global root only while
    /// <see cref="GlobalOn"/>, the external one only with <paramref name="externalSkills"/> — the sentence never names a tool not offered.
    /// </summary>
    public IReadOnlyList<string> ReadFolders(bool agentSkills, bool externalSkills)
    {
        if (!agentSkills)
        {
            return [];
        }

        var folders = new List<string>(3) { Profile };
        if (GlobalOn)
        {
            folders.Add(Global);
        }

        if (externalSkills)
        {
            folders.Add(External);
        }

        return folders;
    }
}
