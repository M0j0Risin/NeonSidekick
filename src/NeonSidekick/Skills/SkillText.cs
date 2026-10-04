using System.Globalization;
using System.Text;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Sessions;

namespace NeonSidekick.Skills;

/// <summary>
/// Every sentence the two skill tools answer with, pinned. A result is what the model reads and,
/// flattened to its first line, the dim 🎓 line the transcript shows (🛠️ until later on 2026-09-21). Every error starts with
/// <c>Error:</c>; a model's mistake is a sentence, never a warning.
/// </summary>
public static class SkillText
{
    // ── load_skill ──────────────────────────────────────────────────────────

    /// <summary>
    /// A loaded skill the specification's way: the body wrapped in a tag that names the skill, the
    /// folder the body's relative paths resolve against, and the bundled files listed (not read —
    /// <c>load_skill</c> with <c>file</c> reads one). The <c>filesFollow</c> form of 2026-09-30 went with <c>/botchat</c>'s
    /// preloaded skills on 2026-10-04.
    /// </summary>
    public static string Content(string name, string body, string directory, IReadOnlyList<string> resources, bool more, bool truncated)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(resources);
        var sb = new StringBuilder();
        sb.Append("<skill_content name=\"").Append(name).Append("\">\n");
        sb.Append(body);
        if (truncated)
        {
            sb.Append("\n\n").Append(BodyCutNote);
        }

        sb.Append("\n\nSkill directory: ").Append(directory);
        sb.Append('\n').Append(RelativePathsNote(resources.Count > 0));
        if (resources.Count > 0)
        {
            sb.Append("\n<skill_resources>");
            foreach (var file in resources)
            {
                sb.Append("\n  <file>").Append(file).Append("</file>");
            }

            if (more)
            {
                sb.Append("\n  ").Append(MoreResourcesNote);
            }

            sb.Append("\n</skill_resources>");
        }

        sb.Append("\n</skill_content>");
        return sb.ToString();
    }

    public static string BodyCutNote =>
        "(cut at " + SkillCatalog.MaxBodyChars.ToString("N0", CultureInfo.InvariantCulture) + " characters)";

    public static string RelativePathsNote(bool withFiles) =>
        "Relative paths in this skill are relative to the skill directory" + (withFiles ? "; read a bundled file with " + LoadSkillTool.ToolName + " and its " + LoadSkillTool.FileArgument + " argument." : ".");


    public static string MoreResourcesNote =>
        "(and more: the first " + SkillCatalog.MaxResources.ToString(CultureInfo.InvariantCulture) + " files are listed)";

    /// <summary>A bundled file: the text in a tag that names the skill and the path.</summary>
    public static string File(string name, string relative, string text, bool truncated)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(relative);
        ArgumentNullException.ThrowIfNull(text);
        return "<skill_file skill=\"" + name + "\" path=\"" + relative + "\">\n" + text
            + (truncated ? "\n\n(cut at " + SkillCatalog.MaxResourceChars.ToString("N0", CultureInfo.InvariantCulture) + " characters)" : "")
            + "\n</skill_file>";
    }

    /// <summary><c>Error: there is no skill named 'x'; the skills are: a, b</c>, or <c>; no skill is installed</c>.</summary>
    public static string Unknown(string name, IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        return $"Error: there is no skill named '{name.Trim()}'; " + (names.Count == 0 ? "no skill is installed" : "the skills are: " + string.Join(", ", names));
    }

    public static string NoName => "Error: name is empty; pass the name of a skill from the list";

    public static string BodyMissing(string name) => $"Error: the SKILL.md of '{name}' is gone; it was there when the skills were listed";

    public static string BodyUnparseable(string name, string problem) => $"Error: the SKILL.md of '{name}' could not be read: {problem}";

    public static string FileMissing(string name, string relative) => $"Error: skill '{name}' has no file '{relative}'";

    public static string FileOutside(string name, string relative) => $"Error: '{relative}' is outside the folder of skill '{name}'; every path must stay inside it";

    public static string FileIsDirectory(string name, string relative) => $"Error: '{relative}' in skill '{name}' is a folder, not a file";

    public static string FileNotText(string name, string relative) => $"Error: '{relative}' in skill '{name}' is not a text file";

    public static string FileTooBig(string name, string relative) => $"Error: '{relative}' in skill '{name}' is too large to handle as text";

    public static string CouldNot(string verb, string name, string detail) => $"Error: could not {verb} skill '{name}': {detail}";

    /// <summary>The sentence for a <see cref="SkillCatalog.ReadResult"/> that is not Ok; <paramref name="relative"/> null for the body.</summary>
    public static string ReadError(Skill skill, string? relative, SkillCatalog.ReadResult result)
    {
        ArgumentNullException.ThrowIfNull(skill);
        ArgumentNullException.ThrowIfNull(result);
        return result.Outcome switch
        {
            SkillCatalog.ReadOutcome.Missing => relative is null ? BodyMissing(skill.Name) : FileMissing(skill.Name, relative),
            SkillCatalog.ReadOutcome.Outside => FileOutside(skill.Name, relative ?? ""),
            SkillCatalog.ReadOutcome.IsDirectory => FileIsDirectory(skill.Name, relative ?? ""),
            SkillCatalog.ReadOutcome.NotText => FileNotText(skill.Name, relative ?? ""),
            SkillCatalog.ReadOutcome.TooBig => FileTooBig(skill.Name, relative ?? ""),
            SkillCatalog.ReadOutcome.Unparseable => BodyUnparseable(skill.Name, result.Detail),
            _ => CouldNot(relative is null ? "read" : "read the file '" + relative + "' of", skill.Name, result.Detail),
        };
    }

    // ── skill_editor ────────────────────────────────────────────────────────

    /// <param name="files">Whether the tool offers the file actions (2026-09-27): a reflection with <c>Reflection edit supporting files</c> off is told create or update alone.</param>
    public static string BadAction(string raw, bool files = false) => files
        ? $"Error: '{raw.Trim()}' is not an action; use {SkillEditorTool.CreateAction}, {SkillEditorTool.UpdateAction}, {SkillEditorTool.WriteFileAction} or {SkillEditorTool.EditFileAction}"
        : $"Error: '{raw.Trim()}' is not an action; use {SkillEditorTool.CreateAction} or {SkillEditorTool.UpdateAction}";

    /// <summary>A file action with no <c>path</c> (2026-09-27).</summary>
    public static string NoPath => $"Error: {SkillEditorTool.PathArgument} is empty; name the file, relative to the skill folder (data/mapping.json)";

    /// <summary>A file action that named the SKILL.md, the app's sidecar or a folder the skill never keeps (2026-09-27).</summary>
    public static string ProtectedFile(string name, string relative) =>
        string.Equals(Path.GetFileName(relative.TrimEnd('/', '\\')), SkillCatalog.FileName, StringComparison.OrdinalIgnoreCase)
            ? $"Error: '{relative}' is the skill itself; change skill '{name}' with action {SkillEditorTool.UpdateAction} (description, instructions)"
            : $"Error: '{relative}' in skill '{name}' is the app's, not the skill's; pick another path";

    /// <summary>A file action's outcome (2026-09-27): <c>skill 'x' (profile): </c> and the file tools' own sentence — <c>FileText.Wrote</c> / <c>FileText.Edited</c>, or their refusal.</summary>
    public static string InSkill(SkillEditResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Detail.StartsWith("Error: ", StringComparison.Ordinal)
            ? "Error: skill '" + result.Name + "' (" + SkillScopes.Name(result.Scope) + "): " + result.Detail["Error: ".Length..]
            : "skill '" + result.Name + "' (" + SkillScopes.Name(result.Scope) + "): " + result.Detail;
    }

    public static string BadScope(string raw) => $"Error: '{raw.Trim()}' is not a scope; use {SkillScopes.ProfileName} (this profile only) or {SkillScopes.GlobalName} (every profile)";

    public static string BadName(string name) => $"Error: '{name.Trim()}' is not a valid skill name; use {SkillFrontmatter.NameRule}";

    public static string Exists(string name, SkillScope scope) =>
        $"Error: skill '{name}' already exists in the {SkillScopes.Name(scope)} skills; call again with action {SkillEditorTool.UpdateAction} to change it";

    /// <summary>The name is free in every root the catalog reads: a create is the right next call.</summary>
    public static string Missing(string name, SkillScope scope) =>
        $"Error: there is no skill '{name}' in the {SkillScopes.Name(scope)} skills; call again with action {SkillEditorTool.CreateAction} to write it";

    /// <summary>A create for a name that is a skill in another writable root (2026-09-16): a copy would shadow it — update it there instead.</summary>
    public static string ExistsElsewhere(string name, SkillScope scope) =>
        $"Error: skill '{name}' already exists in the {SkillScopes.Name(scope)} skills; a second copy would hide it — call again with action {SkillEditorTool.UpdateAction} to change it (the scope you pass is corrected to where it lives)";

    /// <summary>The name is a skill in the external folder, which the app never writes (2026-09-16).</summary>
    public static string ExternalReadOnly(string name) =>
        $"Error: skill '{name}' exists in the external skills ({SkillRoots.ExternalDirectoryName}\\{SkillRoots.DirectoryName}), which this app never writes; edit it by hand or pick another name";

    /// <summary>A reflection's write refused for an installed skill under <c>Reflection downloaded skills</c> = <c>read-only</c> (2026-10-02). Pinned.</summary>
    public static string InstalledReadOnly(string name, string origin) =>
        $"Error: skill '{name}' was installed from {(string.IsNullOrWhiteSpace(origin) ? "a skill source" : origin)}, and a reflection leaves an installed skill as it is; to keep what these turns taught, create a companion skill (a new name) instead";

    /// <summary>A reflection's rewrite refused for a skill it did not load first (2026-10-02). Pinned.</summary>
    public static string LoadBeforeRewrite(string name) =>
        $"Error: load skill '{name}' with load_skill before rewriting it, then keep what still holds";

    /// <summary>A reflection's rewrite refused for a skill changed since its load served it (2026-10-02). Pinned.</summary>
    public static string ChangedSinceLoad(string name) =>
        $"Error: skill '{name}' changed since you loaded it; load it again and build on the new text";

    public static string EmptyDescription =>"Error: description is empty; say what the skill does and when to use it";

    public static string DescriptionTooLong(int length) =>
        $"Error: the description is {length.ToString("N0", CultureInfo.InvariantCulture)} characters; the limit is {SkillFrontmatter.MaxDescriptionLength.ToString("N0", CultureInfo.InvariantCulture)}";

    public static string EmptyInstructions => "Error: instructions is empty; write the steps the skill follows";

    public static string InstructionsTooLong(int length) =>
        $"Error: the instructions are {length.ToString("N0", CultureInfo.InvariantCulture)} characters; the limit is {SkillEditor.MaxInstructionChars.ToString("N0", CultureInfo.InvariantCulture)}";

    public static string NothingToChange => "Error: nothing to change; give a new description, new instructions or both";

    public static string Unparseable(string name, SkillScope scope, string problem) =>
        $"Error: the SKILL.md of '{name}' ({SkillScopes.Name(scope)}) could not be read: {problem}; give both a description and instructions to rewrite it";

    /// <summary>
    /// What the skill records know of a skill, for the reflection's catalog and the <c>Skills › name</c> page (2026-09-19 over the
    /// session store; over <c>skills.db</c> since 2026-10-02): <c>loaded 12 times across 6 sessions, 3 followed by errors; last loaded
    /// 2026-09-18 14:05; written by a reflection 2× (updated 2026-09-18 14:05); edited by hand 2026-09-20 09:30; installed from
    /// owner/repo, changed by a reflection since</c> — each part only with a fact behind it, the parts joined by <c>; </c>;
    /// <see cref="NeverLoaded"/> when there is none. "Followed by errors" counts the turns with an error after the load. Pinned.
    /// </summary>
    public static string UsageLine(SkillFacts facts, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(zone);
        var parts = new List<string>(5);
        if (facts.Uses is { } uses)
        {
            parts.Add("loaded " + Count(uses.Loads, "time") + (uses.Sessions > 0 ? " across " + Count(uses.Sessions, "session") : "")
                + (uses.FollowedByErrors > 0 ? ", " + uses.FollowedByErrors.ToString(CultureInfo.InvariantCulture) + " followed by errors" : ""));
            parts.Add("last loaded " + SessionText.Moment(uses.LastAt, zone));
        }

        if (facts.ReflectionWrites > 0 && facts.LastReflectionWrite is { } mark)
        {
            string action = mark.Kind == SkillEventKinds.Created ? "created" : "updated";
            parts.Add("written by a reflection " + facts.ReflectionWrites.ToString(CultureInfo.InvariantCulture) + "× (" + action + " " + SessionText.Moment(mark.At, zone) + ")");
        }

        if (facts.HandEditedAt is { } edited)
        {
            parts.Add("edited by hand " + SessionText.Moment(edited, zone));
        }

        if (facts.InstalledFrom is { } origin)
        {
            parts.Add("installed from " + origin + (facts.ReflectionWritesSinceInstall > 0 ? ", changed by a reflection since" : ""));
        }

        return parts.Count == 0 ? NeverLoaded : string.Join("; ", parts);
    }

    /// <summary>The usage line of a skill nothing loaded, wrote, edited or installed. Pinned.</summary>
    public const string NeverLoaded = "never loaded";

    private static string Count(int value, string unit) => value.ToString(CultureInfo.InvariantCulture) + " " + unit + (value == 1 ? "" : "s");

    /// <summary>Characters of a change summary kept at most (2026-09-19): one transcript line's worth, the model told to write one or two sentences.</summary>
    public const int MaxSummaryChars = 300;

    /// <summary>
    /// The model's <c>summary</c> argument as the transcript shows it (2026-09-19): every run of
    /// whitespace (line breaks included) one space, trimmed, cut at <see cref="MaxSummaryChars"/>
    /// with the ellipsis as the last character; empty for null or blank.
    /// </summary>
    public static string CleanSummary(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return "";
        }

        var sb = new StringBuilder(summary.Length);
        bool space = false;
        foreach (char c in summary.AsSpan().Trim())
        {
            if (char.IsWhiteSpace(c))
            {
                space = true;
                continue;
            }

            if (space)
            {
                sb.Append(' ');
                space = false;
            }

            sb.Append(c);
        }

        if (sb.Length > MaxSummaryChars)
        {
            sb.Length = MaxSummaryChars;
            sb[^1] = '…';
        }

        return sb.ToString();
    }

    /// <summary><c>created skill 'x' (profile, 1,234 bytes)</c> / <c>updated skill 'x' (global, 1,234 bytes)</c>.</summary>
    public static string Edited(SkillEditResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        string scope = SkillScopes.Name(result.Scope);
        string bytes = result.Bytes.ToString("N0", CultureInfo.InvariantCulture) + " bytes";
        return result.Outcome switch
        {
            SkillEditOutcome.Created => $"created skill '{result.Name}' ({scope}, {bytes}); it is in the list from the next reply on",
            SkillEditOutcome.Updated => $"updated skill '{result.Name}' ({scope}, {bytes})",
            SkillEditOutcome.BadName => BadName(result.Name),
            SkillEditOutcome.Exists => Exists(result.Name, result.Scope),
            SkillEditOutcome.Missing => Missing(result.Name, result.Scope),
            SkillEditOutcome.ExistsElsewhere => ExistsElsewhere(result.Name, result.Scope),
            SkillEditOutcome.ExternalReadOnly => ExternalReadOnly(result.Name),
            SkillEditOutcome.EmptyDescription => EmptyDescription,
            SkillEditOutcome.DescriptionTooLong => DescriptionTooLong(result.Length),
            SkillEditOutcome.EmptyInstructions => EmptyInstructions,
            SkillEditOutcome.InstructionsTooLong => InstructionsTooLong(result.Length),
            SkillEditOutcome.NothingToChange => NothingToChange,
            SkillEditOutcome.Unparseable => Unparseable(result.Name, result.Scope, result.Detail),
            SkillEditOutcome.FileWritten or SkillEditOutcome.FileEdited or SkillEditOutcome.FileRefused => InSkill(result),
            SkillEditOutcome.ProtectedFile => ProtectedFile(result.Name, result.Path),
            _ => CouldNot("write", result.Name, result.Detail),
        };
    }
}
