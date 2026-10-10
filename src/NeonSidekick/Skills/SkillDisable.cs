using System.Globalization;

namespace NeonSidekick.Skills;

/// <summary>
/// A skill switched off (2026-10-09, the user's ask: "a disabled skill remains in the folder but is not offered and reflection can't
/// update it"): the sidecar <see cref="FileName"/> in the skill's folder, set and cleared on the skill's page in <c>/skills</c>, the
/// <see cref="SkillLock"/> shape (the user's pick: in the folder, so it travels with a move or a rename and a disabled global skill is
/// off for every profile). The catalog keeps a disabled skill out of <see cref="SkillCatalog.Skills"/> — no prompt entry, no
/// <c>load_skill</c>, no <c>#</c> mention, no bot — and lists it in <see cref="SkillCatalog.Disabled"/>; it still holds its name, so
/// a same-named skill in a lower root stays shadowed (the user's pick). A reflection sees it in its catalog under
/// <see cref="SkillsPrompt.DisabledTag"/>, so it writes no second skill for the topic, and cannot load it. <see cref="SkillEditor"/>
/// refuses its content changes (<see cref="SkillEditOutcome.Disabled"/>: an update, a file action, a revert, a create of its name);
/// the pane's rename, move and delete still work (the user tidying it). <c>/skills add</c> may update it, the sidecar carried into
/// the new folder; <c>/skills purge</c> leaves it out. Like the lock, <see cref="SkillCatalog.Resources"/> never lists it and a file
/// action never writes it.
/// </summary>
public static class SkillDisable
{
    /// <summary>The sidecar's name.</summary>
    public const string FileName = ".neon-disabled";

    /// <summary>Whether the skill in <paramref name="skillDirectory"/> is disabled. Never throws.</summary>
    public static bool IsDisabled(string skillDirectory)
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
    /// Disables (<paramref name="disabled"/>) or enables the skill in <paramref name="skillDirectory"/>: the sidecar written with the
    /// moment, for a reader, or deleted. Null when done, else the file layer's message.
    /// </summary>
    public static string? Set(string skillDirectory, bool disabled, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(skillDirectory);
        string path = Path.Combine(skillDirectory, FileName);
        try
        {
            if (disabled)
            {
                File.WriteAllText(path, "disabled " + at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) + " in NeonSidekick's /skills; enable it there\n");
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
