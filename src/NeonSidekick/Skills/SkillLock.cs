using System.Globalization;

namespace NeonSidekick.Skills;

/// <summary>
/// A skill's lock (2026-10-07, the user's ask: "lock a skill so that it cannot be changed without unlocking it"): the sidecar
/// <see cref="FileName"/> in the skill's folder, set and cleared on the skill's page in <c>/skills</c>. While it is there
/// <see cref="SkillEditor"/> refuses every change (<see cref="SkillEditOutcome.Locked"/>) — the model's <c>skill_editor</c>, a
/// reflection's, a revert, the pane's rename, move and delete — <c>/skills add</c> refuses an update and <c>/skills purge</c>
/// leaves the skill out; the user's own editor (the page's edit row) still opens the file. In the folder, not in <c>skills.db</c>
/// (the user's pick), so it travels with a move or a rename, even one by hand; <see cref="SkillProvenance"/>'s shape, and like that
/// sidecar <see cref="SkillCatalog.Resources"/> leaves it out of the files <c>load_skill</c> lists and a file action never writes it.
/// The app's refusal, not the file system's: a shell command can still reach the folder.
/// </summary>
public static class SkillLock
{
    /// <summary>The sidecar's name.</summary>
    public const string FileName = ".neon-lock";

    /// <summary>Whether the skill in <paramref name="skillDirectory"/> is locked. Never throws.</summary>
    public static bool IsLocked(string skillDirectory)
    {
        ArgumentNullException.ThrowIfNull(skillDirectory);
        try
        {
            return File.Exists(Path.Combine(skillDirectory, FileName));
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Locks (<paramref name="locked"/>) or unlocks the skill in <paramref name="skillDirectory"/>: the sidecar written with the
    /// moment, for a reader, or deleted. Null when done, else the file layer's message.
    /// </summary>
    public static string? Set(string skillDirectory, bool locked, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(skillDirectory);
        string path = Path.Combine(skillDirectory, FileName);
        try
        {
            if (locked)
            {
                File.WriteAllText(path, "locked " + at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) + " in NeonSidekick's /skills; unlock it there\n");
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return ex.Message;
        }
    }
}
