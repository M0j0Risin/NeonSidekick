using System.Text.Json;
using System.Text.Json.Serialization;
using NeonSidekick.Computers;
using NeonSidekick.Sql;

namespace NeonSidekick.Services;

/// <summary>
/// One computer of <c>services.json</c> (2026-10-15, the admin-tools plan): the shared keys (<see cref="ComputerConfig"/>) and the services'
/// own — <see cref="Services"/>, the visible list (case ignored, <c>*</c> any run; absent, all), and <see cref="Protected"/>, globs added to the
/// app's built-in list (<see cref="ServiceGuard.Protected"/>), never subtracted from it: a match is never stopped, restarted or changed in start
/// type, by the model or by the pane; starting one is allowed. <c>controlActions</c> words: start, stop, restart, start_type — restart its own
/// word, so a restart never needs <c>stop</c>.
/// </summary>
public sealed class ServiceComputerConfig : ComputerConfig
{
    /// <summary>The services the model and <c>/services</c> may see here; absent, all.</summary>
    public List<string>? Services { get; set; }

    /// <summary>Services never stopped, restarted or changed in start type here, over the app's own list.</summary>
    public List<string>? Protected { get; set; }
}

/// <summary>The source-generated context for <c>services.json</c> (2026-10-15, <c>EventLogJsonContext</c>'s twin).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ComputersFile<ServiceComputerConfig>))]
[JsonSerializable(typeof(ServiceComputerConfig))]
public sealed partial class ServiceJsonContext : JsonSerializerContext;

/// <summary><c>services.json</c>, global and per profile (2026-10-15): the shared loader (<see cref="ComputerFamily{TConfig}"/>) with the services' words.</summary>
public static class ServiceConfigFile
{
    public const string FileName = "services.json";

    /// <summary>The <c>controlActions</c> words, in the order the wizard and the descriptions list them. Pinned.</summary>
    public static readonly IReadOnlyList<string> ActionWords = [ServiceActions.Start, ServiceActions.Stop, ServiceActions.Restart, ServiceActions.StartType];

    public static readonly ComputerFamilyInfo Info = new(
        ServiceText.Category,
        FileName,
        "Service computers offered",
        ServiceText.MenuPath,
        "Service set password",
        "NeonSidekick/services/",
        ActionWords,
        "services",
        "services",
        NameGlob.NoSeparator,
        ServiceText.Glyph);

    /// <summary>What a fresh file holds: no computers, a commented example of each key. Pinned.</summary>
    public static string EmptyText =>
        "{\n" +
        "  // One entry per computer. Its name is what the model passes as \"computer\", and what /services computer:<name> reads.\n" +
        "  // \"local\" is this PC, built in and read-only by default: its entry takes every key below but host, auth, user and password.\n" +
        "  // A computer is reached only once it is ticked in Service computers offered (" + ServiceText.MenuPath + ").\n" +
        "  // Remove the leading // from an example to use it, and put it inside \"computers\" below.\n" +
        "  //\n" +
        "  // This PC, the model allowed to start and restart the print spooler and two app services, never to stop them:\n" +
        "  // \"local\": { \"access\": \"readwrite\", \"control\": [\"Spooler\", \"MyApp*\"], \"controlActions\": [\"start\", \"restart\"] },\n" +
        "  //\n" +
        "  // A server read as you, three services visible, SQL Server never stopped from here:\n" +
        "  // \"app01\": { \"host\": \"app01.corp.example\", \"description\": \"app server\", \"services\": [\"MSSQL*\", \"W3SVC\", \"MyApp*\"],\n" +
        "  //   \"access\": \"readwrite\", \"control\": [\"MyApp*\"], \"protected\": [\"MSSQL*\"], \"detail\": \"full\" },\n" +
        "  //\n" +
        "  // As another account, the password in " + SqlText.CredentialStore + " as NeonSidekick/services/<name> — set it with\n" +
        "  // Service set password on " + ServiceText.MenuPath + ", or:\n" +
        "  // " + SqlText.CredentialCommand("NeonSidekick/services/app02", "CORP\\svc-ops") + "\n" +
        "  // \"app02\": { \"host\": \"app02\", \"auth\": \"runas\", \"user\": \"CORP\\\\svc-ops\", \"passwordStore\": \"credman\" },\n" +
        "  //\n" +
        "  // access: read (the default) or readwrite. services: the services visible (absent: all). control: what may be changed\n" +
        "  // (absent: nothing). controlActions: start, stop, restart, start_type (absent: all four; []: none) — restart is its own word.\n" +
        "  // protected: never stopped, restarted or changed in start type, over the app's own list. detail: redacted (the default:\n" +
        "  // secret-like values in paths and commands hidden from the model) or full. auth: windows (as you) or runas.\n" +
        "  // A runas password typed here in plain text is encrypted (\"dpapi:…\") at the next read.\n" +
        "  \"computers\": {}\n" +
        "}\n";

    public static readonly ComputerFamily<ServiceComputerConfig> Family = new(Info, ServiceJsonContext.Default.ComputersFileServiceComputerConfig, ServiceJsonContext.Default.ServiceComputerConfig, EmptyText);
}

/// <summary>The action words: what <c>service_control</c> and <c>controlActions</c> say.</summary>
public static class ServiceActions
{
    public const string Start = "start";
    public const string Stop = "stop";
    public const string Restart = "restart";
    public const string StartType = "start_type";

    /// <summary>The actions <c>service_control</c> takes.</summary>
    public static readonly IReadOnlyList<string> Control = [Start, Stop, Restart];

    /// <summary>Whether <paramref name="action"/> takes the service down (and so is refused for a protected one).</summary>
    public static bool TakesDown(string action) => action is Stop or Restart or StartType;
}
