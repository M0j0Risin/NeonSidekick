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
    /// <c>load_skill</c> with <c>file</c> reads one). <paramref name="filesFollow"/> (2026-09-30, <c>/botchat</c>'s preloaded skills,
    /// whose bundled files come after the content): the note says so instead of pointing at <c>load_skill</c>.
    /// </summary>
    public static string Content(string name, string body, string directory, IReadOnlyList<string> resources, bool more, bool truncated, bool filesFollow = false)
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
        sb.Append('\n').Append(filesFollow && resources.Count > 0 ? FilesFollowNote : RelativePathsNote(resources.Count > 0));
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

    /// <summary>The note of a <see cref="Content"/> whose bundled files follow it (2026-09-30). Pinned: it is prompt text.</summary>
    public const string FilesFollowNote = "Relative paths in this skill are relative to the skill directory; its bundled files follow.";

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

    /// <summary>
    /// <c>load_skill</c>'s answer for a skill whose content the prompt already carries (2026-09-30, code review: a <c>/botchat</c>
    /// preloaded skill, <see cref="Llm.Tools.LoadSkillTool"/>'s <c>preloaded</c>): no content again, a sentence saying where it is.
    /// </summary>
    public static string AlreadyLoaded(string name) => AlreadyLoadedOpen + name.Trim() + AlreadyLoadedClose;

    private const string AlreadyLoadedOpen = "Skill '";
    private const string AlreadyLoadedClose = "' is already loaded: its instructions are in your system prompt; follow them from there.";

    /// <summary>
    /// Whether <paramref name="result"/> is an <see cref="AlreadyLoaded"/> answer, and for which skill (2026-09-30, code review):
    /// the sentence is the model's, so the transcript shows <see cref="AlreadyLoadedNote"/> instead, and the picture writer knows
    /// a call of it brought nothing new.
    /// </summary>
    public static bool IsAlreadyLoaded(string result, out string name)
    {
        ArgumentNullException.ThrowIfNull(result);
        bool matches = result.StartsWith(AlreadyLoadedOpen, StringComparison.Ordinal) && result.EndsWith(AlreadyLoadedClose, StringComparison.Ordinal)
            && result.Length > AlreadyLoadedOpen.Length + AlreadyLoadedClose.Length;
        name = matches ? result[AlreadyLoadedOpen.Length..^AlreadyLoadedClose.Length] : "";
        return matches;
    }

    /// <summary>The transcript's dim line for an <see cref="AlreadyLoaded"/> answer (2026-09-30, code review): <c>skill 'x' already loaded (preloaded)</c>.</summary>
    public static string AlreadyLoadedNote(string name) => $"skill '{name}' already loaded (preloaded)";

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

    public static string EmptyDescription => "Error: description is empty; say what the skill does and when to use it";

    public static string DescriptionTooLong(int length) =>
        $"Error: the description is {length.ToString("N0", CultureInfo.InvariantCulture)} characters; the limit is {SkillFrontmatter.MaxDescriptionLength.ToString("N0", CultureInfo.InvariantCulture)}";

    public static string EmptyInstructions => "Error: instructions is empty; write the steps the skill follows";

    public static string InstructionsTooLong(int length) =>
        $"Error: the instructions are {length.ToString("N0", CultureInfo.InvariantCulture)} characters; the limit is {SkillEditor.MaxInstructionChars.ToString("N0", CultureInfo.InvariantCulture)}";

    public static string NothingToChange => "Error: nothing to change; give a new description, new instructions or both";

    public static string Unparseable(string name, SkillScope scope, string problem) =>
        $"Error: the SKILL.md of '{name}' ({SkillScopes.Name(scope)}) could not be read: {problem}; give both a description and instructions to rewrite it";

    /// <summary>
    /// How the stored sessions used a skill, for the reflection's catalog and the <c>Skills › name</c>
    /// page (2026-09-19): <c>loaded in 12 turns across 6 sessions, 4 with errors; last loaded 2026-09-18 14:05;
    /// written by a reflection 2× (updated 2026-09-18 14:05)</c> — each part only with a fact behind
    /// it, the parts joined by <c>; </c>; <see cref="NeverLoaded"/> when neither the usage nor a
    /// reflection exists. Pinned.
    /// </summary>
    public static string UsageLine(SkillUsage? usage, ReflectionMark? mark, int writes, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var parts = new List<string>(3);
        if (usage is not null)
        {
            parts.Add("loaded in " + Count(usage.Turns, "turn") + " across " + Count(usage.Sessions, "session") + (usage.WithErrors > 0 ? ", " + usage.WithErrors.ToString(CultureInfo.InvariantCulture) + " with errors" : ""));
            parts.Add("last loaded " + SessionText.Moment(usage.LastAt, zone));
        }

        if (mark is not null && writes > 0)
        {
            parts.Add("written by a reflection " + writes.ToString(CultureInfo.InvariantCulture) + "× (" + mark.Action + " " + SessionText.Moment(mark.At, zone) + ")");
        }

        return parts.Count == 0 ? NeverLoaded : string.Join("; ", parts);
    }

    /// <summary>The usage line of a skill no stored turn loaded and no reflection wrote. Pinned.</summary>
    public const string NeverLoaded = "never loaded in a stored session";

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
