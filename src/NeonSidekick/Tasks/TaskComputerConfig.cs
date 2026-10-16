using System.Text.Json;
using System.Text.Json.Serialization;
using NeonSidekick.Computers;
using NeonSidekick.Sql;

namespace NeonSidekick.Tasks;

/// <summary>
/// One computer of <c>tasks.json</c> (2026-10-15, the admin-tools plan): the shared keys (<see cref="ComputerConfig"/>) and the tasks' own —
/// <see cref="Folders"/>, the visible folders (globs, <c>\</c> the separator; absent, all), and <see cref="Privileged"/>, which lets a run or
/// an end reach a task whose principal is privileged (<see cref="TaskPrincipal"/>). <c>control</c> holds task-path globs; a pattern starting
/// with <c>\Microsoft\</c> is the only way to change one of Windows' own. <c>controlActions</c> words: run, end, enable, disable.
/// </summary>
public sealed class TaskComputerConfig : ComputerConfig
{
    /// <summary>The folders the model and <c>/tasks</c> may see here (<c>\Backup\*</c>); absent, all.</summary>
    public List<string>? Folders { get; set; }

    /// <summary>Whether a run or an end may reach a privileged task here; absent or false, never.</summary>
    public bool? Privileged { get; set; }

    [JsonIgnore]
    public bool AllowsPrivileged => Privileged == true;
}

/// <summary>The source-generated context for <c>tasks.json</c> (2026-10-15).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ComputersFile<TaskComputerConfig>))]
[JsonSerializable(typeof(TaskComputerConfig))]
public sealed partial class TaskJsonContext : JsonSerializerContext;

/// <summary>The action words: what <c>task_control</c> and <c>controlActions</c> say.</summary>
public static class TaskActions
{
    public const string Run = "run";
    public const string End = "end";
    public const string Enable = "enable";
    public const string Disable = "disable";

    public static readonly IReadOnlyList<string> All = [Run, End, Enable, Disable];

    /// <summary>Whether <paramref name="action"/> starts or stops a run (and so is held by the privileged guard).</summary>
    public static bool Runs(string action) => action is Run or End;
}

/// <summary><c>tasks.json</c>, global and per profile (2026-10-15): the shared loader with the tasks' words.</summary>
public static class TaskConfigFile
{
    public const string FileName = "tasks.json";

    public static readonly ComputerFamilyInfo Info = new(
        TaskText.Category,
        FileName,
        "Task computers offered",
        TaskText.MenuPath,
        "Task set password",
        "NeonSidekick/tasks/",
        TaskActions.All,
        "folders",
        "tasks",
        '\\',
        TaskText.Glyph);

    /// <summary>What a fresh file holds: no computers, a commented example of each key. Pinned.</summary>
    public static string EmptyText =>
        "{\n" +
        "  // One entry per computer. Its name is what the model passes as \"computer\", and what /tasks computer:<name> reads.\n" +
        "  // \"local\" is this PC, built in and read-only by default: its entry takes every key below but host, auth, user and password.\n" +
        "  // A computer is reached only once it is ticked in Task computers offered (" + TaskText.MenuPath + ").\n" +
        "  // Remove the leading // from an example to use it, and put it inside \"computers\" below.\n" +
        "  //\n" +
        "  // This PC, the model allowed to run and end the tasks under \\Backup\\ (not privileged ones):\n" +
        "  // \"local\": { \"access\": \"readwrite\", \"control\": [\"\\\\Backup\\\\*\"], \"controlActions\": [\"run\", \"end\"] },\n" +
        "  //\n" +
        "  // A server read as you, two folders visible, its SYSTEM tasks runnable:\n" +
        "  // \"app01\": { \"host\": \"app01.corp.example\", \"description\": \"app server\", \"folders\": [\"\\\\\", \"\\\\Ops\\\\*\"],\n" +
        "  //   \"access\": \"readwrite\", \"control\": [\"\\\\Ops\\\\*\"], \"privileged\": true, \"detail\": \"full\" },\n" +
        "  //\n" +
        "  // As another account, the password in " + SqlText.CredentialStore + " as NeonSidekick/tasks/<name> — set it with Task set password\n" +
        "  // on " + TaskText.MenuPath + ", or:\n" +
        "  // " + SqlText.CredentialCommand("NeonSidekick/tasks/app02", "CORP\\svc-ops") + "\n" +
        "  // \"app02\": { \"host\": \"app02\", \"auth\": \"runas\", \"user\": \"CORP\\\\svc-ops\", \"passwordStore\": \"credman\" },\n" +
        "  //\n" +
        "  // access: read (the default) or readwrite. folders: the folders visible (\\\\ the root; absent: all). control: the task\n" +
        "  // paths that may be changed (absent: nothing; Windows' own under \\\\Microsoft\\\\ only by a pattern starting there).\n" +
        "  // controlActions: run, end, enable, disable (absent: all four; []: none). privileged: true lets run and end reach a task\n" +
        "  // that runs as SYSTEM, a service account, a group or with highest privileges. detail: redacted (the default) or full.\n" +
        "  // auth: windows (as you) or runas. A runas password typed here in plain text is encrypted (\"dpapi:…\") at the next read.\n" +
        "  \"computers\": {}\n" +
        "}\n";

    public static readonly ComputerFamily<TaskComputerConfig> Family = new(Info, TaskJsonContext.Default.ComputersFileTaskComputerConfig, TaskJsonContext.Default.TaskComputerConfig, EmptyText);
}
