#:project ../src/NeonSidekick/NeonSidekick.csproj
#:property PublishAot=false

// GapBench: runs /plan gaps and /code-audit headless against planted material (tools/gapbench/) and scores what the model found
// (2026-11-17, phase 2 of .notes/plan_gaps_and_code_audit_improvements.md, the small version: almost no real review history yet, so a
// baseline on planted gaps and bugs answers whether reviews miss things, raise false ones or lose content often enough to improve).
//
//     dotnet run tools/GapBench.cs -- --url http://127.0.0.1:30000/v1                   every item, 3 runs each
//     dotnet run tools/GapBench.cs -- --url … --runs 1 --only version-flag,audit        a slice
//     dotnet run tools/GapBench.cs -- --check                                           the material is consistent; no model
//     dotnet run tools/GapBench.cs -- --score publish/gapbench/<folder>                 re-scores saved runs (after a keyword is tuned)
//     dotnet run tools/GapBench.cs -- --self-test                                       the scorer on built-in samples; no model
//
// Each run gets a fresh work folder (repo/ copied, and the plan under .neon/plans/), a scratch NEONSIDEKICK_HOME whose profile copies the
// daily profile's model settings (--like), and the published exe headless with a script on stdin. The model never sees the answers:
// no *.expect.json and no README is copied. Runs go in rounds (run 1 of every item, then run 2…), so a bench cut short overnight leaves
// every item with the same count; a finished run writes done.json, and the same --out resumes.
//
// Scoring reads stdout (the present_plan calls whole: the --log copy is cut), run.log (passes, retries, follow-ups, Usage, the shadow
// lines) and the files the run left. The stop notices and log lines are matched against the app's own text functions, rendered through
// #:project with sentinel numbers, so a reworded notice is still recognized; one that matches none is flagged "unknown".
//
// A .NET 10 file-based app: not in the solution, not run by build.ps1. Exit codes: 0 finished (timed-out or failed runs included),
// 1 the preflight failed, the server went away, Ctrl+C, or --check/--self-test found something, 2 bad arguments.

using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NeonSidekick.App;
using NeonSidekick.Audits;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Plans;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
Console.OutputEncoding = new UTF8Encoding(false);

if (Options.Parse(args) is not { } options)
{
    Console.Error.WriteLine(Options.Usage);
    return 2;
}

return options.Mode switch
{
    Mode.Check => Checker.Run(),
    Mode.SelfTest => SelfTest.Run(),
    Mode.Score => Report.ScoreFolder(options.ScoreFolder!),
    _ => await Bench.RunAsync(options),
};

enum Mode
{
    Run,
    Check,
    Score,
    SelfTest,
}

sealed class Options
{
    public const string Usage =
        "Usage: dotnet run tools/GapBench.cs -- --url <http://host:port/v1> [--model <id>] [--runs 3] [--only a,b] [--exe <path>] [--out <folder>] [--fresh]\n" +
        "                                     [--timeout <minutes, 120>] [--like <profile.json>|none] [--reasoning <level>] [--temperature <t>]\n" +
        "       dotnet run tools/GapBench.cs -- --check | --self-test | --score <out folder>";

    public Mode Mode { get; private set; } = Mode.Run;
    public string? Url { get; private set; }
    public string? Model { get; private set; }
    public int Runs { get; private set; } = 3;
    public HashSet<string>? Only { get; private set; }
    public string? Exe { get; private set; }
    public string? Out { get; private set; }
    public bool Fresh { get; private set; }
    public int TimeoutMinutes { get; private set; } = 120;
    public string? Like { get; private set; }
    public string? Reasoning { get; private set; }
    public double? Temperature { get; private set; }
    public string? ScoreFolder { get; private set; }

    public static Options? Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (a)
            {
                case "--check":
                    o.Mode = Mode.Check;
                    break;
                case "--self-test":
                    o.Mode = Mode.SelfTest;
                    break;
                case "--score":
                    o.Mode = Mode.Score;
                    o.ScoreFolder = Next();
                    if (o.ScoreFolder is null)
                    {
                        return null;
                    }

                    break;
                case "--url":
                    o.Url = Next();
                    break;
                case "--model":
                    o.Model = Next();
                    break;
                case "--runs":
                    if (!int.TryParse(Next(), NumberStyles.None, CultureInfo.InvariantCulture, out int runs) || runs < 1)
                    {
                        return null;
                    }

                    o.Runs = runs;
                    break;
                case "--only":
                    o.Only = [.. (Next() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
                    if (o.Only.Count == 0)
                    {
                        return null;
                    }

                    break;
                case "--exe":
                    o.Exe = Next();
                    break;
                case "--out":
                    o.Out = Next();
                    break;
                case "--fresh":
                    o.Fresh = true;
                    break;
                case "--timeout":
                    if (!int.TryParse(Next(), NumberStyles.None, CultureInfo.InvariantCulture, out int minutes) || minutes < 1)
                    {
                        return null;
                    }

                    o.TimeoutMinutes = minutes;
                    break;
                case "--like":
                    o.Like = Next();
                    break;
                case "--reasoning":
                    o.Reasoning = Next();
                    break;
                case "--temperature":
                    if (!double.TryParse(Next(), NumberStyles.Float, CultureInfo.InvariantCulture, out double t) || t < 0)
                    {
                        return null;
                    }

                    o.Temperature = t;
                    break;
                default:
                    return null;
            }
        }

        if (o.Mode == Mode.Run && string.IsNullOrWhiteSpace(o.Url))
        {
            return null;
        }

        return o.Url is "" || o.Model is "" || o.Exe is "" || o.Out is "" || o.Like is "" || o.Reasoning is "" ? null : o;
    }
}

/// <summary>The repository's folders, found from this file's own path so the bench runs from any folder.</summary>
static class Repo
{
    public static string Root { get; } = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Source())!, ".."));

    public static string MaterialRoot => Path.Combine(Root, "tools", "gapbench");

    public static string DefaultExe => Path.Combine(Root, "publish", "output", OperatingSystem.IsWindows() ? "NeonSidekick.exe" : "NeonSidekick");

    public static string DefaultLike => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".neonsidekick", "profiles", "neon", "profile.json");

    private static string Source([CallerFilePath] string path = "") => path;
}

// ── The material ─────────────────────────────────────────────────────────────

/// <summary>Keyword groups: every group must have one of its words in the text, case aside.</summary>
sealed record Keywords(IReadOnlyList<string[]> Groups)
{
    public bool In(string text) => Groups.Count > 0 && Groups.All(g => g.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase)));

    /// <summary>The first line of <paramref name="body"/> that holds every group.</summary>
    public string? LineIn(string body) => body.Split('\n').Select(l => l.Trim()).FirstOrDefault(In);

    /// <summary>Whether any group has a word in the text (the start-body check: no part of a decision may be there already).</summary>
    public bool AnyIn(string text) => Groups.Any(g => g.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase)));

    public override string ToString() => string.Join(" + ", Groups.Select(g => "(" + string.Join(" | ", g) + ")"));
}

sealed record PlantedGap(string Id, string Severity, string What, Keywords Match, Keywords? Fixed, IReadOnlyList<string> Gone);

sealed record PlanItem(string Name, string PlanPath, string ExpectPath, IReadOnlyList<PlantedGap> Gaps, bool Control, string? Chat, Keywords? Decision, Keywords? Reargue, IReadOnlyList<string> Absent);

sealed record Spot(string Id, string File, int Line, string Contains, string? Severity, string? Dimension, string What, Keywords? Nit = null);

sealed record AuditItem(string ExpectPath, IReadOnlyList<Spot> Bugs, IReadOnlyList<Spot> Decoys);

sealed class Material
{
    public const string AuditName = "audit";

    public required string Root { get; init; }
    public List<PlanItem> Plans { get; } = [];
    public AuditItem? Audit { get; set; }
    public List<string> Problems { get; } = [];

    public string RepoDir => Path.Combine(Root, "repo");

    public IEnumerable<string> ItemNames => Plans.Select(p => p.Name).Concat(Audit is null ? [] : [AuditName]);

    public PlanItem? Plan(string name) => Plans.FirstOrDefault(p => p.Name == name);

    public static Material Load(string root)
    {
        var m = new Material { Root = root };
        string plans = Path.Combine(root, "plans");
        var mds = Directory.Exists(plans) ? Directory.GetFiles(plans, "*.md").Select(Path.GetFileNameWithoutExtension).OfType<string>().ToHashSet() : [];
        var expects = Directory.Exists(plans)
            ? Directory.GetFiles(plans, "*.expect.json").Select(f => Path.GetFileName(f)[..^".expect.json".Length]).ToHashSet()
            : [];
        foreach (string name in mds.Except(expects).Order(StringComparer.Ordinal))
        {
            m.Problems.Add($"plans/{name}.md has no plans/{name}.expect.json");
        }

        foreach (string name in expects.Except(mds).Order(StringComparer.Ordinal))
        {
            m.Problems.Add($"plans/{name}.expect.json has no plans/{name}.md");
        }

        foreach (string name in mds.Intersect(expects).Order(StringComparer.Ordinal))
        {
            string expect = Path.Combine(plans, name + ".expect.json");
            try
            {
                m.Plans.Add(ReadPlan(name, Path.Combine(plans, name + ".md"), expect));
            }
            catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException or KeyNotFoundException)
            {
                m.Problems.Add($"plans/{name}.expect.json: {e.Message}");
            }
        }

        string repoExpect = Path.Combine(root, "repo.expect.json");
        if (File.Exists(repoExpect))
        {
            try
            {
                m.Audit = ReadAudit(repoExpect);
            }
            catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException or KeyNotFoundException)
            {
                m.Problems.Add($"repo.expect.json: {e.Message}");
            }
        }

        if (!Directory.Exists(m.RepoDir))
        {
            m.Problems.Add("repo/ is missing");
        }

        return m;
    }

    private static PlanItem ReadPlan(string name, string planPath, string expectPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(expectPath));
        var root = doc.RootElement;
        var gaps = new List<PlantedGap>();
        if (root.TryGetProperty("gaps", out var list))
        {
            foreach (var g in list.EnumerateArray())
            {
                gaps.Add(new PlantedGap(
                    Str(g, "id"), Str(g, "severity"), Str(g, "what"), Groups(g.GetProperty("match")),
                    g.TryGetProperty("fixed", out var f) ? Groups(f) : null,
                    g.TryGetProperty("gone", out var gone) ? Strings(gone) : []));
            }
        }

        return new PlanItem(
            name, planPath, expectPath, gaps,
            root.TryGetProperty("control", out var c) && c.GetBoolean(),
            root.TryGetProperty("chat", out var chat) ? chat.GetString() : null,
            root.TryGetProperty("decision", out var d) ? Groups(d) : null,
            root.TryGetProperty("reargue", out var r) ? Groups(r) : null,
            root.TryGetProperty("absent", out var a) ? Strings(a) : []);
    }

    private static AuditItem ReadAudit(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        static Spot ReadSpot(JsonElement e) => new(
            Str(e, "id"), Str(e, "file"), e.GetProperty("line").GetInt32(), Str(e, "contains"),
            e.TryGetProperty("severity", out var s) ? s.GetString() : null,
            e.TryGetProperty("dimension", out var d) ? d.GetString() : null,
            Str(e, "what"),
            e.TryGetProperty("nit", out var nit) ? Groups(nit) : null);
        var root = doc.RootElement;
        return new AuditItem(
            path,
            [.. root.GetProperty("bugs").EnumerateArray().Select(ReadSpot)],
            root.TryGetProperty("decoys", out var decoys) ? [.. decoys.EnumerateArray().Select(ReadSpot)] : []);
    }

    private static string Str(JsonElement e, string name) =>
        e.GetProperty(name).GetString() is { Length: > 0 } s ? s : throw new FormatException($"\"{name}\" is empty");

    private static List<string> Strings(JsonElement e) => [.. e.EnumerateArray().Select(x => x.GetString() ?? "")];

    private static Keywords Groups(JsonElement e)
    {
        var groups = e.EnumerateArray().Select(g => g.EnumerateArray().Select(k => k.GetString() ?? "").Where(k => k.Length > 0).ToArray()).ToList();
        if (groups.Count == 0 || groups.Any(g => g.Length == 0))
        {
            throw new FormatException("a keyword list must be a list of non-empty lists of words");
        }

        return new Keywords(groups);
    }

    /// <summary>
    /// SHA-256 over the material's files (paths with <c>/</c>, then the text with LF line endings), so a run records what it was scored against and <c>--score</c>
    /// can say when the material changed since.
    /// </summary>
    public static string Hash(string root)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                     .Where(f => !f.Contains("__pycache__", StringComparison.Ordinal) && !f.Contains(".pytest_cache", StringComparison.Ordinal))
                     .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
                     .Order(StringComparer.Ordinal))
        {
            sha.AppendData(Encoding.UTF8.GetBytes(file + "\0"));
            // Line endings aside: a checkout with core.autocrlf rewrites them, and the material is the same.
            sha.AppendData(Encoding.UTF8.GetBytes(File.ReadAllText(Path.Combine(root, file)).Replace("\r\n", "\n", StringComparison.Ordinal)));
        }

        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }
}

// ── The app's own text, as patterns ─────────────────────────────────────────

/// <summary>
/// Patterns built from the app's own text functions, called with sentinel values (2026-11-17): a number sentinel and the words after it
/// (<c>701 passes</c>) become <c>[\d,]+( [a-z]+)*</c>, so <c>1 pass</c> and <c>12 gaps</c> both match; a string sentinel (a path, a name)
/// becomes <c>.+?</c>; a percentage becomes digits. Everything else is the app's literal text, so a reworded notice is still recognized
/// and no wording is copied here.
/// </summary>
static partial class Template
{
    public const int N1 = 701, N2 = 702, N3 = 703, N4 = 704, N5 = 705;
    public const string S1 = "QQSENTA", S2 = "QQSENTB";

    [GeneratedRegex(@"(?<num>\b70[1-9](?: [a-z]+)*)|(?<str>QQSENT[A-Z])|(?<pct>\b\d+%)", RegexOptions.CultureInvariant)]
    private static partial Regex Token();

    /// <summary>The pattern of <paramref name="rendered"/>; cut just before the sentinel <paramref name="cutBefore"/>; <paramref name="capture"/> numbered into group <c>n</c>.</summary>
    public static Regex Build(string rendered, int? cutBefore = null, int? capture = null)
    {
        var sb = new StringBuilder();
        int at = 0;
        foreach (Match m in Token().Matches(rendered))
        {
            if (m.Groups["num"].Success && cutBefore == int.Parse(m.Value.AsSpan(0, 3), CultureInfo.InvariantCulture))
            {
                sb.Append(Regex.Escape(rendered[at..m.Index]));
                return new Regex(sb.ToString(), RegexOptions.CultureInvariant);
            }

            sb.Append(Regex.Escape(rendered[at..m.Index]));
            at = m.Index + m.Length;
            if (m.Groups["num"].Success)
            {
                sb.Append(capture == int.Parse(m.Value.AsSpan(0, 3), CultureInfo.InvariantCulture) ? @"(?<n>[\d,]+)(?: [a-z]+)*" : @"[\d,]+(?: [a-z]+)*");
            }
            else if (m.Groups["str"].Success)
            {
                sb.Append(".+?");
            }
            else
            {
                sb.Append(@"[\d,]+%");
            }
        }

        sb.Append(Regex.Escape(rendered[at..]));
        return new Regex(sb.ToString(), RegexOptions.CultureInvariant);
    }

    /// <summary>
    /// The literal <paramref name="with"/> adds to <paramref name="without"/>, digits aside: the shadow line's "would converge" part, found by
    /// rendering the line both ways rather than spelled here.
    /// </summary>
    public static string Inserted(string with, string without)
    {
        string a = Regex.Replace(with, @"\d", "#"), b = Regex.Replace(without, @"\d", "#");
        int p = 0;
        while (p < a.Length && p < b.Length && a[p] == b[p])
        {
            p++;
        }

        int s = 0;
        while (s < a.Length - p && s < b.Length - p && a[^(s + 1)] == b[^(s + 1)])
        {
            s++;
        }

        return b.Length - p - s == 0 ? with[p..^s] : throw new InvalidOperationException("the two renderings differ by more than one insertion");
    }
}

/// <summary>A stop notice's pattern: the name the results use, the app's reason, and the pattern.</summary>
sealed record StopPattern(string Name, PlanGapStop? Reason, Regex Pattern);

static partial class Patterns
{
    public static readonly IReadOnlyList<StopPattern> PlanStops = BuildPlanStops();
    public static readonly IReadOnlyList<StopPattern> AuditStops = BuildAuditStops();

    // The --log lines (PlanText/AuditText), cut where a line's variable tail starts.
    public static readonly Regex PlanPass = Template.Build(PlanText.PassLogLine(Template.N1, Template.N2, [], false), cutBefore: Template.N2);
    public static readonly Regex PlanClean = Template.Build(PlanText.CleanPassLogLine(Template.N1, Template.N2, Template.N3, []), cutBefore: Template.N2);
    public static readonly Regex PlanRetry = Template.Build(PlanText.RetryLogLine(Template.N1, null));
    public static readonly Regex[] PlanFollowUp = [Template.Build(PlanText.FollowUpLogLine(Template.N1, false)), Template.Build(PlanText.FollowUpLogLine(Template.N1, true))];
    public static readonly Regex AuditPass = Template.Build(AuditText.PassLogLine(Template.N1, Template.N2, 0, false, false, null), cutBefore: Template.N2);
    public static readonly Regex AuditRetry = Template.Build(AuditText.RetryLogLine(Template.N1, null));
    public static readonly Regex[] AuditFollowUp = [Template.Build(AuditText.FollowUpLogLine(Template.N1, false)), Template.Build(AuditText.FollowUpLogLine(Template.N1, true))];

    /// <summary>The shadow line's pass number, cut before what the pass did.</summary>
    public static readonly Regex ShadowPass = Template.Build(
        AuditText.ShadowLogLine(Template.N1, new AuditShadowPass([Template.N4, 0, 0, 0], Template.N2, true, false), Template.N3, null, false, 0),
        cutBefore: Template.N4, capture: Template.N1);

    /// <summary>What a shadow line adds once the shadow streak reaches the clean passes needed.</summary>
    public static readonly string WouldConverge = Template.Inserted(
        AuditText.ShadowLogLine(1, new AuditShadowPass([0, 0, 0, 0], 2, true, false), 2, null, true, 1),
        AuditText.ShadowLogLine(1, new AuditShadowPass([0, 0, 0, 0], 1, true, false), 2, null, true, 1));

    /// <summary>
    /// One request's report (<c>Assistant.UsageLogLine</c>, internal, pinned by its tests): its total. The app's own text can't be rendered
    /// from here, so this is the one log pattern spelled out.
    /// </summary>
    [GeneratedRegex(@"\bUsage: \d+ in, \d+ out[^,]*, (?<total>\d+) total;", RegexOptions.CultureInvariant)]
    public static partial Regex Usage();

    /// <summary>The connect line (<c>LlmSession.ConnectedLogLine</c>, internal, pinned): the model's id.</summary>
    [GeneratedRegex(@"\bConnected: LLM: \S+ model=(?<model>\S+)", RegexOptions.CultureInvariant)]
    public static partial Regex Connected();

    private static List<StopPattern> BuildPlanStops()
    {
        var list = new List<StopPattern>
        {
            new("converged", PlanGapStop.Converged, Template.Build(PlanText.ConvergedNotice(Template.S1, Template.N1, Template.N2, 1))),
            new("converged", PlanGapStop.Converged, Template.Build(PlanText.ConvergedNotice(Template.S1, Template.N1, Template.N2, Template.N3))),
            new("not sent", PlanGapStop.Stopped, new Regex(Regex.Escape(PlanText.GapPassNotSentNotice), RegexOptions.CultureInvariant)),
        };
        foreach (var reason in Enum.GetValues<PlanGapStop>().Where(r => r != PlanGapStop.Converged))
        {
            list.Add(new(Name(reason), reason, Template.Build(PlanText.GapStopNotice(reason, Template.N1, Template.N2))));
        }

        return list;
    }

    private static List<StopPattern> BuildAuditStops()
    {
        var list = new List<StopPattern>
        {
            new("converged", PlanGapStop.Converged, Template.Build(AuditText.ConvergedNotice(Template.S1, Template.N1, Template.N2, Template.N3, Template.N3, pane: false))),
            new("converged, part read", PlanGapStop.Converged, Template.Build(AuditText.ConvergedNotice(Template.S1, Template.N1, Template.N2, Template.N3, Template.N4, pane: false))),
            new("converged, part read", PlanGapStop.Converged, Template.Build(AuditText.ConvergedNotice(Template.S1, Template.N1, Template.N2, Template.N3, Template.N4, pane: false, unread: Template.N5))),
        };
        foreach (var reason in Enum.GetValues<PlanGapStop>())
        {
            list.Add(new(Name(reason), reason, Template.Build(AuditText.StopNotice(reason, Template.S1, Template.N1))));
        }

        return list;
    }

    private static string Name(PlanGapStop reason) => reason switch
    {
        PlanGapStop.Converged => "converged",
        PlanGapStop.Limit => "limit",
        PlanGapStop.Unconfirmed => "unconfirmed",
        PlanGapStop.Stalled => "stalled",
        PlanGapStop.NoRevision => "no revision",
        PlanGapStop.UserSpoke => "stopped",
        _ => "stopped",
    };

    /// <summary>The run's last stop notice in <paramref name="stdout"/>: the longest pattern that matches a line wins over a shorter one.</summary>
    public static StopPattern? LastStop(string stdout, IReadOnlyList<StopPattern> patterns)
    {
        StopPattern? last = null;
        foreach (string line in stdout.Split('\n'))
        {
            var best = patterns.Where(p => p.Pattern.IsMatch(line)).MaxBy(p => p.Pattern.ToString().Length);
            if (best is not null)
            {
                last = best;
            }
        }

        return last;
    }
}

// ── Reading a run ───────────────────────────────────────────────────────────

/// <summary>One tool call read from stdout: its arguments (null when they would not parse), the raw text, and its result.</summary>
sealed class ToolCallRecord
{
    public required int Ordinal { get; init; }
    public JsonElement? Arguments { get; init; }
    public required string Raw { get; init; }
    public string? Result { get; set; }

    public bool Parsed => Arguments is not null;
    public bool Refused => Result is { } r && Assistant.IsToolError(r);
    public bool Accepted => Result is not null && !Refused;

    public IReadOnlyList<string> List(string name)
    {
        if (Arguments is not { } args || !args.TryGetProperty(name, out var value))
        {
            return [];
        }

        return value.ValueKind switch
        {
            JsonValueKind.Array => [.. value.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.GetRawText()).Where(s => s.Length > 0)],
            JsonValueKind.String when value.GetString() is { Length: > 0 } s => [s],
            _ => [],
        };
    }
}

static class StdoutReader
{
    /// <summary>
    /// The calls of <paramref name="tool"/> in headless stdout, each paired with its <c>-> result</c> line in order. A call's arguments are
    /// the model's raw JSON, which may span lines, so one whole JSON value is read from the <c>{</c> on; one that won't parse is kept raw.
    /// </summary>
    public static List<ToolCallRecord> Calls(string stdout, string tool)
    {
        string call = "[tool] " + tool + " ";
        string result = "[tool] " + tool + " -> ";
        var calls = new List<ToolCallRecord>();
        var waiting = new Queue<ToolCallRecord>();
        int pos = 0;
        while (pos < stdout.Length)
        {
            int end = stdout.IndexOf('\n', pos);
            if (end < 0)
            {
                end = stdout.Length;
            }

            if (string.CompareOrdinal(stdout, pos, result, 0, result.Length) == 0)
            {
                if (waiting.TryDequeue(out var c))
                {
                    c.Result = stdout[(pos + result.Length)..end].TrimEnd('\r');
                }
            }
            else if (string.CompareOrdinal(stdout, pos, call, 0, call.Length) == 0)
            {
                int start = pos + call.Length;
                var (json, length) = ReadJson(stdout, start);
                var record = new ToolCallRecord
                {
                    Ordinal = calls.Count + 1,
                    Arguments = json,
                    Raw = json is null ? RawUntilNextTool(stdout, start) : stdout.Substring(start, length),
                };
                calls.Add(record);
                waiting.Enqueue(record);
                if (json is not null)
                {
                    int after = stdout.IndexOf('\n', start + length);
                    end = after < 0 ? stdout.Length : after;
                }
            }

            pos = end + 1;
        }

        return calls;
    }

    private static (JsonElement? Json, int Length) ReadJson(string text, int start)
    {
        if (start >= text.Length || text[start] != '{')
        {
            return (null, 0);
        }

        byte[] bytes = Encoding.UTF8.GetBytes(text, start, Math.Min(text.Length - start, 8 * 1024 * 1024));
        try
        {
            var reader = new Utf8JsonReader(bytes);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return (null, 0);
            }

            reader.Skip();
            int consumed = (int)reader.BytesConsumed;
            using var doc = JsonDocument.Parse(bytes.AsMemory(0, consumed));
            return (doc.RootElement.Clone(), Encoding.UTF8.GetCharCount(bytes, 0, consumed));
        }
        catch (JsonException)
        {
            return (null, 0);
        }
    }

    private static string RawUntilNextTool(string text, int start)
    {
        int next = text.IndexOf("\n[tool] ", start, StringComparison.Ordinal);
        string raw = next < 0 ? text[start..] : text[start..next];
        return raw.Length > 2000 ? raw[..2000] + "…" : raw;
    }
}

/// <summary>What a run's --log says.</summary>
sealed class LogFacts
{
    public int PlanPasses { get; private set; }
    public int PlanCleanPasses { get; private set; }
    public int Retries { get; private set; }
    public int FollowUps { get; private set; }
    public int AuditPasses { get; private set; }
    public long Tokens { get; private set; }
    public int Requests { get; private set; }
    public string? ModelId { get; private set; }
    public int? ShadowWouldConvergeAt { get; private set; }
    public string? LastShadowLine { get; private set; }

    public static LogFacts Read(string log)
    {
        var f = new LogFacts();
        foreach (string raw in log.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (f.ModelId is null && Patterns.Connected().Match(line) is { Success: true } connected)
            {
                f.ModelId = connected.Groups["model"].Value;
            }

            if (Patterns.Usage().Match(line) is { Success: true } usage)
            {
                f.Tokens += long.Parse(usage.Groups["total"].Value, CultureInfo.InvariantCulture);
                f.Requests++;
            }

            if (line.Contains(AuditText.ShadowLogPrefix, StringComparison.Ordinal))
            {
                f.LastShadowLine = line;
                if (f.ShadowWouldConvergeAt is null && line.Contains(Patterns.WouldConverge, StringComparison.Ordinal)
                    && Patterns.ShadowPass.Match(line) is { Success: true } shadow)
                {
                    f.ShadowWouldConvergeAt = int.Parse(shadow.Groups["n"].Value.Replace(",", "", StringComparison.Ordinal), CultureInfo.InvariantCulture);
                }

                continue;
            }

            if (Patterns.PlanPass.IsMatch(line))
            {
                f.PlanPasses++;
            }
            else if (Patterns.PlanClean.IsMatch(line))
            {
                f.PlanCleanPasses++;
            }
            else if (Patterns.PlanRetry.IsMatch(line) || Patterns.AuditRetry.IsMatch(line))
            {
                f.Retries++;
            }
            else if (Patterns.PlanFollowUp.Any(p => p.IsMatch(line)) || Patterns.AuditFollowUp.Any(p => p.IsMatch(line)))
            {
                f.FollowUps++;
            }
            else if (Patterns.AuditPass.IsMatch(line))
            {
                f.AuditPasses++;
            }
        }

        return f;
    }
}

// ── Scoring ─────────────────────────────────────────────────────────────────

sealed record GapOutcome(PlantedGap Gap, string Status, string? Line, int? Call, bool FixedInBody);

sealed record BugOutcome(Spot Bug, string Status, AuditFinding? Finding, string? Nit = null);

/// <summary>One scored run: the facts from done.json, what the logs say, and the item's own scores.</summary>
sealed class RunScore
{
    public required string Item { get; init; }
    public required int Run { get; init; }
    public required string Folder { get; init; }
    public required JsonObject Facts { get; init; }
    public string Stop { get; set; } = "unknown";
    public PlanGapStop? StopReason { get; set; }
    public LogFacts Log { get; set; } = new();
    public double Minutes { get; set; }
    public List<string> Flags { get; } = [];

    // A plan run.
    public List<ToolCallRecord> Calls { get; } = [];
    public List<GapOutcome> Planted { get; } = [];
    public List<(int Call, string Text)> OtherGaps { get; } = [];
    public bool? DecisionFolded { get; set; }
    public List<string> Reargued { get; } = [];
    public PlanLossReport? Loss { get; set; }

    // An audit run.
    public List<BugOutcome> Bugs { get; } = [];
    public List<Spot> DecoysFlagged { get; } = [];
    public List<AuditFinding> OtherFindings { get; } = [];
    public int CoverageRead { get; set; }
    public int CoverageScope { get; set; }
    public List<string> BugsAfterShadow { get; } = [];

    public bool IsAudit => Item == Material.AuditName;

    public int Passes => IsAudit ? Log.AuditPasses : Log.PlanPasses + Log.PlanCleanPasses;

    public bool Converged => StopReason == PlanGapStop.Converged;

    public bool LostContent => Loss is { } l && (l.Steps.Count > 0 || l.Unticked.Count > 0 || l.Sections.Count > 0 || l.ShrinkPercent is not null);

    public bool ConvergedWithHighOpen => Converged && (IsAudit
        ? Bugs.Any(b => IsHigh(b.Bug.Severity) && b.Status is not ("caught" or "file only"))
        : Planted.Any(g => IsHigh(g.Gap.Severity) && g.Status != "caught" && !g.FixedInBody));

    public bool ShadowStoppedEarly => BugsAfterShadow.Count > 0;

    private static bool IsHigh(string? severity) => severity is "high" or "critical";
}

static class Scorer
{
    public static RunScore Score(string folder, Material material)
    {
        var facts = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "done.json")))!.AsObject();
        var score = new RunScore
        {
            Item = (string)facts["item"]!,
            Run = (int)facts["run"]!,
            Folder = folder,
            Facts = facts,
            Minutes = (double?)facts["minutes"] ?? 0,
        };
        string stdout = ReadText(Path.Combine(folder, "stdout.txt"));
        score.Log = LogFacts.Read(ReadText(Path.Combine(folder, "run.log")));

        if (ReadText(Path.Combine(folder, "stderr.txt")).Trim().Length > 0)
        {
            score.Flags.Add("stderr.txt isn't empty");
        }

        if (File.Exists(Path.Combine(folder, "home", "crash.log")))
        {
            score.Flags.Add("home/crash.log exists");
        }

        var stop = Patterns.LastStop(stdout, score.IsAudit ? Patterns.AuditStops : Patterns.PlanStops);
        score.Stop = stop?.Name ?? "unknown";
        score.StopReason = stop?.Reason;
        if ((bool?)facts["timedOut"] == true)
        {
            score.Stop = "timed out";
            score.StopReason = null;
        }
        else if (stop is null)
        {
            score.Flags.Add("no stop notice recognized (unknown)");
        }

        if (facts["exitCode"] is { } code && (int)code != 0)
        {
            score.Flags.Add($"exit code {(int)code}");
        }

        if (score.IsAudit)
        {
            if (material.Audit is { } audit)
            {
                ScoreAudit(score, audit, stdout);
            }
        }
        else if (material.Plan(score.Item) is { } plan)
        {
            ScorePlan(score, plan, stdout);
        }
        else
        {
            score.Flags.Add("the item is no longer in the material");
        }

        return score;
    }

    public static void ScorePlan(RunScore score, PlanItem plan, string stdout)
    {
        score.Calls.AddRange(StdoutReader.Calls(stdout, PresentPlanTool.ToolName));
        foreach (var c in score.Calls.Where(c => !c.Parsed))
        {
            score.Flags.Add($"present_plan call {c.Ordinal} unparsed");
        }

        int saved = score.Calls.Count(c => c.Accepted);
        if (score.Log.PlanPasses + score.Log.PlanCleanPasses > saved)
        {
            score.Flags.Add($"parse mismatch: the log counts {score.Log.PlanPasses + score.Log.PlanCleanPasses} passes, stdout shows {saved} saved present_plan calls");
        }

        string startBody = PlanDocument.Body(ReadText(Path.Combine(score.Folder, "start.md")));
        string finalPath = Path.Combine(score.Folder, "work", ".neon", "plans", plan.Name + ".md");
        string finalBody = File.Exists(finalPath) ? PlanDocument.Body(File.ReadAllText(finalPath)) : "";
        if (!File.Exists(finalPath))
        {
            score.Flags.Add("the plan file is gone");
        }

        var gapLines = score.Calls.SelectMany(c => c.List(PresentPlanTool.GapsArgument).Select(t => (c.Ordinal, Text: t))).ToList();
        var minorLines = score.Calls.SelectMany(c => c.List(PresentPlanTool.MinorArgument).Select(t => (c.Ordinal, Text: t))).ToList();
        score.Planted.AddRange(Outcomes(plan, gapLines, minorLines, finalBody));
        foreach (var line in gapLines.Where(l => !plan.Gaps.Any(g => g.Match.In(l.Text))))
        {
            score.OtherGaps.Add(line);
        }

        if (plan.Decision is { } decision)
        {
            score.DecisionFolded = decision.In(finalBody);
        }

        if (plan.Reargue is { } reargue)
        {
            score.Reargued.AddRange(gapLines.Concat(minorLines).Where(l => reargue.In(l.Text)).Select(l => l.Text));
        }

        score.Loss = PlanLoss.Compare(startBody, finalBody);
    }

    public static IEnumerable<GapOutcome> Outcomes(PlanItem plan, List<(int Ordinal, string Text)> gapLines, List<(int Ordinal, string Text)> minorLines, string finalBody)
    {
        foreach (var gap in plan.Gaps)
        {
            bool fixedInBody = (gap.Fixed?.LineIn(finalBody) is not null)
                || (gap.Gone.Count > 0 && gap.Gone.All(g => !finalBody.Contains(g, StringComparison.OrdinalIgnoreCase)));
            if (gapLines.FirstOrDefault(l => gap.Match.In(l.Text)) is { Text: not null } caught)
            {
                yield return new GapOutcome(gap, "caught", caught.Text, caught.Ordinal, fixedInBody);
            }
            else if (minorLines.FirstOrDefault(l => gap.Match.In(l.Text)) is { Text: not null } minor)
            {
                yield return new GapOutcome(gap, "minor only", minor.Text, minor.Ordinal, fixedInBody);
            }
            else if (fixedInBody)
            {
                yield return new GapOutcome(gap, "fixed silently", gap.Fixed?.LineIn(finalBody) ?? "(" + string.Join(", ", gap.Gone) + " gone)", null, true);
            }
            else
            {
                yield return new GapOutcome(gap, "missed", null, null, false);
            }
        }
    }

    public static void ScoreAudit(RunScore score, AuditItem audit, string stdout)
    {
        var calls = StdoutReader.Calls(stdout, PresentAuditTool.ToolName);
        foreach (var c in calls.Where(c => !c.Parsed))
        {
            score.Flags.Add($"present_audit call {c.Ordinal} unparsed");
        }

        string audits = Path.Combine(score.Folder, "work", ".neon", "audits");
        var reports = Directory.Exists(audits) ? Directory.GetFiles(audits, "*.md").OrderBy(File.GetLastWriteTimeUtc).ToList() : [];
        if (reports.Count == 0)
        {
            score.Flags.Add("no report in work/.neon/audits");
            foreach (var bug in audit.Bugs)
            {
                score.Bugs.Add(new BugOutcome(bug, "missed", null));
            }

            return;
        }

        if (reports.Count > 1)
        {
            score.Flags.Add($"{reports.Count} reports: scored the newest");
        }

        var report = AuditReport.Parse(File.ReadAllText(reports[^1]));
        score.CoverageRead = report.CoverageRead;
        score.CoverageScope = report.CoverageScope;
        if ((report.Findings.Count > 0 || report.Retracted.Count > 0) && !calls.Any(c => c.Accepted))
        {
            score.Flags.Add("parse mismatch: the report has findings, stdout shows no accepted present_audit call");
        }

        ScoreFindings(score, audit, report.Findings, report.Retracted, report.Nits);

        if (score.Log.ShadowWouldConvergeAt is int at)
        {
            score.BugsAfterShadow.AddRange(score.Bugs.Where(b => b.Finding is { } f && b.Status == "caught" && f.Pass > at).Select(b => b.Bug.Id));
        }

        if (score.StopReason is { } reason && score.Log.LastShadowLine is { } shadow && !shadow.TrimEnd().EndsWith(AuditText.StopWord(reason), StringComparison.Ordinal))
        {
            score.Flags.Add($"stop mismatch: the notice says {score.Stop}, the last shadow line doesn't end in \"{AuditText.StopWord(reason)}\"");
        }
    }

    /// <summary>
    /// Each planted bug's outcome, best first: caught (a finding within ±3 lines), file only (a finding with no line in its file), nit only
    /// (named only among the report's nits, by its <c>nit</c> keywords: the audit saw it and filed it as too small), retracted, missed.
    /// </summary>
    public static void ScoreFindings(RunScore score, AuditItem audit, IReadOnlyList<AuditFinding> live, IReadOnlyList<AuditFinding> retracted, IReadOnlyList<string> nits)
    {
        var used = new HashSet<AuditFinding>(ReferenceEqualityComparer.Instance);
        foreach (var bug in audit.Bugs)
        {
            var near = live.Where(f => Near(f, bug)).OrderBy(f => f.Pass).ToList();
            var fileOnly = live.Where(f => f.Line is null && SameFile(f.File, bug.File)).OrderBy(f => f.Pass).ToList();
            if (near.Count > 0)
            {
                used.UnionWith(near);
                score.Bugs.Add(new BugOutcome(bug, "caught", near[0]));
            }
            else if (fileOnly.Count > 0)
            {
                used.UnionWith(fileOnly);
                score.Bugs.Add(new BugOutcome(bug, "file only", fileOnly[0]));
            }
            else if (bug.Nit is { } keywords && nits.FirstOrDefault(keywords.In) is { } nit)
            {
                score.Bugs.Add(new BugOutcome(bug, "nit only", null, nit));
            }
            else if (retracted.FirstOrDefault(f => Near(f, bug)) is { } gone)
            {
                score.Bugs.Add(new BugOutcome(bug, "retracted", gone));
            }
            else
            {
                score.Bugs.Add(new BugOutcome(bug, "missed", null));
            }
        }

        foreach (var decoy in audit.Decoys)
        {
            var near = live.Where(f => Near(f, decoy)).ToList();
            if (near.Count > 0)
            {
                used.UnionWith(near);
                score.DecoysFlagged.Add(decoy);
            }
        }

        score.OtherFindings.AddRange(live.Where(f => !used.Contains(f)));
    }

    /// <summary>The same file and a line within ±3 (the report may write either separator, or a leading <c>./</c>).</summary>
    public static bool Near(AuditFinding f, Spot spot) => f.Line is int line && Math.Abs(line - spot.Line) <= 3 && SameFile(f.File, spot.File);

    public static bool SameFile(string a, string b) => string.Equals(NormalPath(a), NormalPath(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    public static string NormalPath(string path)
    {
        string p = path.Trim().Replace('\\', '/');
        while (p.StartsWith("./", StringComparison.Ordinal))
        {
            p = p[2..];
        }

        return p;
    }

    /// <summary>Severity agreement: 0 the same, 1 one level off, 2 or more further.</summary>
    public static int SeverityOff(AuditSeverity reported, string? planted) => planted is null ? 0 : Math.Abs((int)reported - (planted.ToLowerInvariant() switch
    {
        "critical" => (int)AuditSeverity.Critical,
        "high" => (int)AuditSeverity.High,
        "medium" => (int)AuditSeverity.Medium,
        _ => (int)AuditSeverity.Low,
    }));

    public static string ReadText(string path) => File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : "";
}

// ── Running ─────────────────────────────────────────────────────────────────

static class Bench
{
    private static readonly string[] CopiedSettings =
    [
        "LlmReasoning", "LlmReasoningEstimate", "LlmMaxToolIterations", "LlmContextLength", "LlmAutoCompactPercent", "LlmCompactType",
        "LlmCompactKeepRecent", "LlmToolCompactType", "LlmSampling", "LlmRequestTimeoutSeconds", "LlmTurnTimeoutSeconds",
    ];

    private static readonly string[] SentinelHosts = ["docker.localhost", "embedded.localhost", "claude-code.localhost"];

    public static async Task<int> RunAsync(Options o)
    {
        var material = Material.Load(Repo.MaterialRoot);
        if (material.Problems.Count > 0)
        {
            Console.Error.WriteLine("The material has problems (dotnet run tools/GapBench.cs -- --check):");
            material.Problems.ForEach(p => Console.Error.WriteLine("  " + p));
            return 1;
        }

        if (!Uri.TryCreate(o.Url, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
        {
            Console.Error.WriteLine($"--url {o.Url} isn't an http address.");
            return 2;
        }

        if (SentinelHosts.Any(h => url.Host.EndsWith(h, StringComparison.OrdinalIgnoreCase)))
        {
            Console.Error.WriteLine($"--url {o.Url} is the app's own address for a server it starts; each run would start, stop and reload it.");
            Console.Error.WriteLine("Pass the server's real address instead, such as a Docker server's published port: http://127.0.0.1:30000/v1");
            return 2;
        }

        var items = material.ItemNames.ToList();
        if (o.Only is { } only)
        {
            if (only.FirstOrDefault(n => !items.Contains(n)) is { } unknown)
            {
                Console.Error.WriteLine($"--only {unknown}: no such item. Items: {string.Join(", ", items)}");
                return 2;
            }

            items = [.. items.Where(only.Contains)];
        }

        string exe = Path.GetFullPath(o.Exe ?? Repo.DefaultExe);
        if (!File.Exists(exe))
        {
            Console.Error.WriteLine($"The exe isn't there: {exe}. Publish first (.\\build.ps1 -Publish), or pass --exe.");
            return 1;
        }

        string baseUrl = o.Url!.TrimEnd('/');
        if (await ServerAnswersAsync(baseUrl) is { } down)
        {
            Console.Error.WriteLine($"The server doesn't answer: GET {baseUrl}/models: {down}");
            return 1;
        }

        string version = await ExeVersionAsync(exe);
        if (version != SidekickApp.VersionLine)
        {
            Console.WriteLine($"Warning: the exe says \"{version}\", this source is \"{SidekickApp.VersionLine}\": the exe may be stale against the text this tool matches.");
        }

        if (BuildSettings(o) is not { } settings)
        {
            return 2;
        }

        string outDir = Path.GetFullPath(o.Out ?? Path.Combine(Repo.Root, "publish", "gapbench", DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)));
        Directory.CreateDirectory(outDir);
        if (o.Fresh)
        {
            foreach (string dir in Directory.GetDirectories(outDir).Where(d => Regex.IsMatch(Path.GetFileName(d), @"^[a-z0-9-]+-\d+$")))
            {
                Directory.Delete(dir, recursive: true);
            }

            File.Delete(Path.Combine(outDir, "results.md"));
            File.Delete(Path.Combine(outDir, "results.json"));
        }

        WriteBenchFile(outDir, o.Runs);
        string hash = Material.Hash(Repo.MaterialRoot);
        Console.WriteLine($"GapBench: {items.Count} items × {o.Runs} runs → {outDir}");
        Console.WriteLine($"  {version} · {baseUrl} · settings {settings.ToJsonString()}");

        using var interrupt = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            interrupt.Cancel();
        };

        int exit = 0;
        bool first = true;
        for (int n = 1; n <= o.Runs && exit == 0; n++)
        {
            foreach (string item in items)
            {
                string folder = Path.Combine(outDir, $"{item}-{n}");
                if (File.Exists(Path.Combine(folder, "done.json")))
                {
                    continue;
                }

                if (!first && await ServerAnswersAsync(baseUrl) is { } gone)
                {
                    Console.Error.WriteLine($"The server stopped answering ({gone}): the bench stops. Run the same command again to resume.");
                    exit = 1;
                    break;
                }

                first = false;
                var facts = await RunOneAsync(o, material, item, n, folder, exe, baseUrl, settings, version, hash, interrupt.Token);
                if (facts is null)
                {
                    Console.Error.WriteLine("Interrupted: the run in progress has no done.json, so a resume runs it again.");
                    exit = 1;
                    break;
                }

                var score = Scorer.Score(folder, material);
                Console.WriteLine($"  {item} {n}/{o.Runs}: {score.Stop}, {score.Passes} passes, {score.Minutes.ToString("0.0", CultureInfo.InvariantCulture)} min"
                    + (score.Flags.Count > 0 ? " (" + string.Join("; ", score.Flags) + ")" : ""));
            }
        }

        Report.Write(outDir, material);
        Console.WriteLine($"Results: {Path.Combine(outDir, "results.md")}");
        return exit;
    }

    private static async Task<JsonObject?> RunOneAsync(Options o, Material m, string item, int n, string folder, string exe, string url, JsonObject settings, string version, string hash, CancellationToken interrupt)
    {
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }

        string work = Path.Combine(folder, "work");
        CopyRepo(m.RepoDir, work);
        var script = new List<string>();
        if (m.Plan(item) is { } plan)
        {
            string plans = Path.Combine(work, ".neon", "plans");
            Directory.CreateDirectory(plans);
            File.Copy(plan.PlanPath, Path.Combine(plans, plan.Name + ".md"));
            File.Copy(plan.PlanPath, Path.Combine(folder, "start.md"));
            script.Add($"/plan open {plan.Name} --chat");
            if (plan.Chat is { } chat)
            {
                script.Add(chat);
            }

            script.Add("/plan gaps");
        }
        else
        {
            script.Add("/code-audit");
        }

        script.Add("/exit");
        string home = Path.Combine(folder, "home");
        string profile = Path.Combine(home, "profiles", "neon", "profile.json");
        Directory.CreateDirectory(Path.GetDirectoryName(profile)!);
        File.WriteAllText(profile, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        var start = new ProcessStartInfo(exe)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = folder,
        };
        var arguments = new List<string> { "--headless", "--url", url };
        if (o.Model is { } model)
        {
            arguments.AddRange(["--model", model]);
        }

        arguments.AddRange(["--cwd", work, "--log", Path.Combine(folder, "run.log")]);
        arguments.ForEach(start.ArgumentList.Add);

        foreach (string key in start.Environment.Keys.Where(k => k.StartsWith("NEONSIDEKICK_", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            start.Environment.Remove(key);
        }

        start.Environment["NEONSIDEKICK_HOME"] = home;
        var started = DateTimeOffset.Now;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("the exe didn't start");
        // Shared for reading, so a run in progress can be watched (Get-Content -Wait, tail -f).
        await using var stdoutFile = new FileStream(Path.Combine(folder, "stdout.txt"), FileMode.Create, FileAccess.Write, FileShare.Read);
        await using var stderrFile = new FileStream(Path.Combine(folder, "stderr.txt"), FileMode.Create, FileAccess.Write, FileShare.Read);
        var pumps = Task.WhenAll(process.StandardOutput.BaseStream.CopyToAsync(stdoutFile), process.StandardError.BaseStream.CopyToAsync(stderrFile));
        await using (var stdinWriter = process.StandardInput)
        {
            foreach (string line in script)
            {
                await stdinWriter.WriteLineAsync(line);
            }
        }
        bool timedOut = false;
        using (var limit = CancellationTokenSource.CreateLinkedTokenSource(interrupt))
        {
            limit.CancelAfter(TimeSpan.FromMinutes(o.TimeoutMinutes));
            try
            {
                await process.WaitForExitAsync(limit.Token);
            }
            catch (OperationCanceledException)
            {
                timedOut = !interrupt.IsCancellationRequested;
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }

        await pumps;
        if (interrupt.IsCancellationRequested)
        {
            return null;
        }

        var facts = new JsonObject
        {
            ["item"] = item,
            ["run"] = n,
            ["started"] = started.ToString("o", CultureInfo.InvariantCulture),
            ["minutes"] = Math.Round((DateTimeOffset.Now - started).TotalMinutes, 1),
            ["exitCode"] = timedOut ? null : (JsonNode)process.ExitCode,
            ["timedOut"] = timedOut,
            ["version"] = version,
            ["url"] = url,
            ["model"] = o.Model,
            ["settings"] = settings.DeepClone(),
            ["materialHash"] = hash,
        };
        File.WriteAllText(Path.Combine(folder, "done.json"), facts.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return facts;
    }

    /// <summary>The scratch profile: the model settings copied from <c>--like</c>, the overrides, and the bench's own plan, audit and tool settings.</summary>
    private static JsonObject? BuildSettings(Options o)
    {
        var settings = new JsonObject();
        string like = o.Like ?? Repo.DefaultLike;
        if (!string.Equals(like, "none", StringComparison.OrdinalIgnoreCase))
        {
            if (!File.Exists(like))
            {
                if (o.Like is not null)
                {
                    Console.Error.WriteLine($"--like {like}: no such file.");
                    return null;
                }

                Console.WriteLine($"Warning: {like} isn't there; the app's default model settings are used.");
            }
            else if (JsonNode.Parse(File.ReadAllText(like)) is JsonObject daily)
            {
                foreach (string key in CopiedSettings)
                {
                    if (daily[key] is { } value)
                    {
                        settings[key] = value.DeepClone();
                    }
                }
            }
        }

        if (o.Reasoning is { } reasoning)
        {
            settings["LlmReasoning"] = reasoning;
        }

        if (o.Temperature is { } t)
        {
            settings["LlmSampling"] = new JsonObject { ["*"] = new JsonObject { ["Temperature"] = t } };
        }

        settings["FileTools"] = true;
        settings["WebTools"] = false;
        settings["Advisors"] = false;
        settings["CodeAuditWebResearch"] = false;
        settings["PlanGapPasses"] = 10;
        return settings;
    }

    private static void WriteBenchFile(string outDir, int runs)
    {
        string path = Path.Combine(outDir, "bench.json");
        int planned = runs;
        if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path))?["runs"] is { } before)
        {
            planned = Math.Max(planned, (int)before);
        }

        File.WriteAllText(path, new JsonObject { ["runs"] = planned }.ToJsonString());
    }

    private static void CopyRepo(string from, string to)
    {
        foreach (string file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(from, file);
            if (relative.Split(Path.DirectorySeparatorChar).Any(part => part is "__pycache__" or ".pytest_cache") || relative.EndsWith(".pyc", StringComparison.Ordinal))
            {
                continue;
            }

            string target = Path.Combine(to, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    /// <summary>Null when <c>GET {url}/models</c> answers within 10 s with a success; else why not.</summary>
    public static async Task<string?> ServerAnswersAsync(string baseUrl)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        try
        {
            using var response = await http.GetAsync(baseUrl + "/models");
            return response.IsSuccessStatusCode ? null : $"HTTP {(int)response.StatusCode}";
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return e is TaskCanceledException ? "no answer in 10 s" : e.Message;
        }
    }

    private static async Task<string> ExeVersionAsync(string exe)
    {
        string home = Path.Combine(Path.GetTempPath(), "gapbench-version-" + Guid.NewGuid().ToString("N"));
        try
        {
            var start = new ProcessStartInfo(exe, "--version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            start.Environment["NEONSIDEKICK_HOME"] = home;
            using var process = Process.Start(start)!;
            string text = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            return text.Trim();
        }
        finally
        {
            if (Directory.Exists(home))
            {
                Directory.Delete(home, recursive: true);
            }
        }
    }
}

// ── The results ─────────────────────────────────────────────────────────────

static class Report
{
    public static int ScoreFolder(string folder)
    {
        string full = Path.GetFullPath(folder);
        if (!Directory.Exists(full))
        {
            Console.Error.WriteLine($"No such folder: {full}");
            return 2;
        }

        var material = Material.Load(Repo.MaterialRoot);
        material.Problems.ForEach(p => Console.WriteLine("Warning: " + p));
        Write(full, material);
        Console.WriteLine($"Results: {Path.Combine(full, "results.md")}");
        return 0;
    }

    public static void Write(string outDir, Material material)
    {
        var runs = Directory.GetDirectories(outDir)
            .Where(d => File.Exists(Path.Combine(d, "done.json")))
            .Select(d => Scorer.Score(d, material))
            .OrderBy(r => r.Item, StringComparer.Ordinal).ThenBy(r => r.Run)
            .ToList();
        int planned = File.Exists(Path.Combine(outDir, "bench.json")) && JsonNode.Parse(File.ReadAllText(Path.Combine(outDir, "bench.json")))?["runs"] is { } p
            ? (int)p
            : runs.Select(r => r.Run).DefaultIfEmpty(0).Max();
        string currentHash = Material.Hash(Repo.MaterialRoot);

        var md = new StringBuilder();
        md.AppendLine("# GapBench results").AppendLine();
        md.AppendLine($"{outDir} · scored {DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}").AppendLine();
        AppendMeta(md, runs, currentHash);
        AppendHeadline(md, runs, material);
        var json = new JsonObject
        {
            ["folder"] = outDir,
            ["plannedRuns"] = planned,
            ["materialHash"] = currentHash,
            ["headline"] = Headline(runs, material),
            ["runs"] = new JsonArray([.. runs.Select(RunJson)]),
        };

        foreach (var group in runs.GroupBy(r => r.Item))
        {
            AppendItem(md, group.Key, [.. group], planned, material);
        }

        File.WriteAllText(Path.Combine(outDir, "results.md"), md.ToString());
        File.WriteAllText(Path.Combine(outDir, "results.json"), json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void AppendMeta(StringBuilder md, List<RunScore> runs, string currentHash)
    {
        string Values(Func<RunScore, string?> pick) => string.Join(" / ", runs.Select(pick).Select(v => v ?? "—").Distinct());
        md.AppendLine($"- Exe: {Values(r => (string?)r.Facts["version"])}");
        md.AppendLine($"- Model: {Values(r => r.Log.ModelId)} at {Values(r => (string?)r.Facts["url"])}");
        md.AppendLine($"- Settings: {Values(r => r.Facts["settings"]?.ToJsonString())}");
        string hashes = Values(r => ((string?)r.Facts["materialHash"])?[..12]);
        md.AppendLine($"- Material: {hashes}" + (runs.Any(r => (string?)r.Facts["materialHash"] != currentHash)
            ? $" — **the material has changed since (now {currentHash[..12]})**: a moved bug line or a changed plan mis-scores these runs"
            : ""));
        md.AppendLine();
    }

    private static JsonObject Headline(List<RunScore> runs, Material material)
    {
        var plans = runs.Where(r => !r.IsAudit).ToList();
        var audits = runs.Where(r => r.IsAudit).ToList();
        return new JsonObject
        {
            ["plannedGaps"] = plans.Sum(r => r.Planted.Count),
            ["gapsCaught"] = plans.Sum(r => r.Planted.Count(g => g.Status == "caught")),
            ["gapsMinorOnly"] = plans.Sum(r => r.Planted.Count(g => g.Status == "minor only")),
            ["gapsFixedSilently"] = plans.Sum(r => r.Planted.Count(g => g.Status == "fixed silently")),
            ["plantedBugs"] = audits.Sum(r => r.Bugs.Count),
            ["bugsCaught"] = audits.Sum(r => r.Bugs.Count(b => b.Status == "caught")),
            ["bugsFileOnly"] = audits.Sum(r => r.Bugs.Count(b => b.Status == "file only")),
            ["bugsNitOnly"] = audits.Sum(r => r.Bugs.Count(b => b.Status == "nit only")),
            ["controlRuns"] = plans.Count(r => material.Plan(r.Item)?.Control == true),
            ["controlGaps"] = plans.Where(r => material.Plan(r.Item)?.Control == true).Sum(r => r.OtherGaps.Count),
            ["planRuns"] = plans.Count,
            ["runsLostContent"] = plans.Count(r => r.LostContent),
            ["convergedWithHighOpen"] = runs.Count(r => r.ConvergedWithHighOpen),
            ["auditRuns"] = audits.Count,
            ["shadowStoppedEarly"] = audits.Count(r => r.ShadowStoppedEarly),
            ["flaggedRuns"] = runs.Count(r => r.Flags.Count > 0),
        };
    }

    private static void AppendHeadline(StringBuilder md, List<RunScore> runs, Material material)
    {
        var h = Headline(runs, material);
        int I(string k) => (int)h[k]!;
        md.AppendLine("## Headline").AppendLine();
        md.AppendLine($"- Planted gaps caught: **{I("gapsCaught")} of {I("plannedGaps")}** ({I("gapsMinorOnly")} only as minor fixes, {I("gapsFixedSilently")} fixed without being reported)");
        md.AppendLine($"- Planted bugs caught: **{I("bugsCaught")} of {I("plantedBugs")}** ({I("bugsFileOnly")} more named only by file, {I("bugsNitOnly")} only as nits)");
        md.AppendLine($"- Gaps reported on the control plan: **{I("controlGaps")}** over {I("controlRuns")} runs");
        md.AppendLine($"- Runs that lost content: **{I("runsLostContent")} of {I("planRuns")}**");
        md.AppendLine($"- Runs that converged with a high gap open or a high bug missed: **{I("convergedWithHighOpen")}**");
        md.AppendLine($"- Audit runs where the shadow rule would have stopped before a planted bug was found: **{I("shadowStoppedEarly")} of {I("auditRuns")}**");
        if (I("flaggedRuns") > 0)
        {
            md.AppendLine($"- Runs flagged (read them): **{I("flaggedRuns")}**");
        }

        md.AppendLine();
    }

    private static void AppendItem(StringBuilder md, string item, List<RunScore> runs, int planned, Material material)
    {
        md.AppendLine($"## {item} (n = {runs.Count} of {planned})").AppendLine();
        md.AppendLine("| | median | range |").AppendLine("|---|---|---|");
        Row(md, "passes", runs.Select(r => (double)r.Passes));
        Row(md, "retries", runs.Select(r => (double)r.Log.Retries));
        Row(md, "follow-ups", runs.Select(r => (double)r.Log.FollowUps));
        Row(md, "tokens (k)", runs.Select(r => Math.Round(r.Log.Tokens / 1000.0, 1)));
        Row(md, "minutes", runs.Select(r => r.Minutes));
        if (item == Material.AuditName)
        {
            Row(md, "bugs caught", runs.Select(r => (double)r.Bugs.Count(b => b.Status == "caught")));
            Row(md, "decoys flagged", runs.Select(r => (double)r.DecoysFlagged.Count));
            Row(md, "other findings", runs.Select(r => (double)r.OtherFindings.Count));
            Row(md, "files read %", runs.Select(r => r.CoverageScope > 0 ? Math.Round(100.0 * r.CoverageRead / r.CoverageScope) : 0));
        }
        else
        {
            Row(md, "planted gaps caught", runs.Select(r => (double)r.Planted.Count(g => g.Status == "caught")));
            Row(md, material.Plan(item)?.Control == true ? "gaps reported (all false)" : "other gaps reported", runs.Select(r => (double)r.OtherGaps.Count));
        }

        md.AppendLine();
        md.AppendLine("Stops: " + string.Join(", ", runs.GroupBy(r => r.Stop).Select(g => $"{g.Key} ×{g.Count()}"))).AppendLine();

        if (item == Material.AuditName && material.Audit is { } audit)
        {
            md.AppendLine("| bug | severity | caught | file only | nit only | retracted | severity agrees | first pass |").AppendLine("|---|---|---|---|---|---|---|---|");
            foreach (var bug in audit.Bugs)
            {
                var outcomes = runs.Select(r => r.Bugs.FirstOrDefault(b => b.Bug.Id == bug.Id)).OfType<BugOutcome>().ToList();
                var caught = outcomes.Where(o => o.Status == "caught").ToList();
                md.AppendLine($"| {bug.Id} {bug.What} | {bug.Severity} | {caught.Count} | {outcomes.Count(o => o.Status == "file only")} | {outcomes.Count(o => o.Status == "nit only")} | {outcomes.Count(o => o.Status == "retracted")} | "
                    + $"{caught.Count(o => Scorer.SeverityOff(o.Finding!.Severity, bug.Severity) == 0)} same, {caught.Count(o => Scorer.SeverityOff(o.Finding!.Severity, bug.Severity) == 1)} one off | "
                    + string.Join(", ", caught.Select(o => o.Finding!.Pass.ToString(CultureInfo.InvariantCulture))) + " |");
            }

            md.AppendLine();
        }
        else if (material.Plan(item) is { } plan && plan.Gaps.Count > 0)
        {
            md.AppendLine("| gap | severity | caught | minor only | fixed silently | missed |").AppendLine("|---|---|---|---|---|---|");
            foreach (var gap in plan.Gaps)
            {
                var outcomes = runs.Select(r => r.Planted.FirstOrDefault(g => g.Gap.Id == gap.Id)).OfType<GapOutcome>().ToList();
                md.AppendLine($"| {gap.Id} {gap.What} | {gap.Severity} | {outcomes.Count(o => o.Status == "caught")} | {outcomes.Count(o => o.Status == "minor only")} | "
                    + $"{outcomes.Count(o => o.Status == "fixed silently")} | {outcomes.Count(o => o.Status == "missed")} |");
            }

            md.AppendLine();
        }

        if (material.Plan(item) is { Decision: not null })
        {
            md.AppendLine($"Decision folded into the plan: {runs.Count(r => r.DecisionFolded == true)} of {runs.Count}; runs with a gap re-arguing it: {runs.Count(r => r.Reargued.Count > 0)}").AppendLine();
        }

        foreach (var run in runs)
        {
            AppendRun(md, run);
        }
    }

    private static void AppendRun(StringBuilder md, RunScore r)
    {
        md.AppendLine($"### {r.Item} run {r.Run}").AppendLine();
        md.AppendLine($"- `{r.Folder}`");
        md.AppendLine($"- {r.Stop}; {r.Passes} passes, {r.Log.Retries} retries, {r.Log.FollowUps} follow-ups; {Math.Round(r.Log.Tokens / 1000.0, 1).ToString(CultureInfo.InvariantCulture)}k tokens over {r.Log.Requests} requests; {r.Minutes.ToString("0.0", CultureInfo.InvariantCulture)} min");
        foreach (string flag in r.Flags)
        {
            md.AppendLine($"- ⚠ {flag}");
        }

        foreach (var g in r.Planted)
        {
            md.AppendLine($"- {g.Gap.Id} ({g.Gap.Severity}): **{g.Status}**" + (g.Line is { } line ? $"{(g.Call is { } c ? $" in call {c}" : "")}: «{Flat(line)}»" : "")
                + (g.Status != "fixed silently" && g.FixedInBody ? " · fixed in the final plan" : ""));
        }

        if (r.DecisionFolded is { } folded)
        {
            md.AppendLine($"- Decision folded in: {(folded ? "yes" : "**no**")}");
        }

        foreach (string reargued in r.Reargued)
        {
            md.AppendLine($"- Re-argues the decision: «{Flat(reargued)}»");
        }

        if (r.Loss is { } loss && r.LostContent)
        {
            md.AppendLine("- Lost content (read: a good revision may rightly drop a step):"
                + (loss.Steps.Count > 0 ? $" steps: {string.Join(" | ", loss.Steps.Select(Flat))};" : "")
                + (loss.Unticked.Count > 0 ? $" unticked: {string.Join(" | ", loss.Unticked.Select(Flat))};" : "")
                + (loss.Sections.Count > 0 ? $" sections: {string.Join(", ", loss.Sections)};" : "")
                + (loss.ShrinkPercent is { } shrink ? $" shrank {shrink}%" : ""));
        }

        foreach (var c in r.Calls.Where(c => !c.Parsed))
        {
            md.AppendLine($"- Unparsed call {c.Ordinal}: `{Flat(c.Raw)}`");
        }

        if (r.OtherGaps.Count > 0)
        {
            md.AppendLine("- Other gaps reported:");
            foreach (var (call, text) in r.OtherGaps)
            {
                md.AppendLine($"  - call {call}: {Flat(text)}");
            }
        }

        foreach (var b in r.Bugs)
        {
            md.AppendLine($"- {b.Bug.Id} ({b.Bug.Severity}): **{b.Status}**" + (b.Finding is { } f
                ? $" as {f.Id} ({AuditFinding.Word(f.Severity)}) {f.Location}, pass {f.Pass}: «{Flat(f.Title)}»"
                : b.Nit is { } nit ? $": «{Flat(nit)}»" : ""));
        }

        foreach (var d in r.DecoysFlagged)
        {
            md.AppendLine($"- Decoy {d.Id} flagged ({d.File}:{d.Line})");
        }

        if (r.IsAudit)
        {
            md.AppendLine($"- Coverage: {r.CoverageRead} of {r.CoverageScope} files read; shadow rule would converge at pass {r.Log.ShadowWouldConvergeAt?.ToString(CultureInfo.InvariantCulture) ?? "—"}"
                + (r.BugsAfterShadow.Count > 0 ? $"; planted bugs found after it: **{string.Join(", ", r.BugsAfterShadow)}**" : ""));
        }

        if (r.OtherFindings.Count > 0)
        {
            md.AppendLine("- Other findings (not counted as false; a confirmed one can join repo.expect.json):");
            foreach (var f in r.OtherFindings)
            {
                md.AppendLine($"  - {f.Id} ({AuditFinding.Word(f.Severity)}, {f.Dimension}) {f.Location}, pass {f.Pass}: {Flat(f.Title)}");
            }
        }

        md.AppendLine();
    }

    private static JsonObject RunJson(RunScore r) => new()
    {
        ["item"] = r.Item,
        ["run"] = r.Run,
        ["folder"] = r.Folder,
        ["stop"] = r.Stop,
        ["passes"] = r.Passes,
        ["retries"] = r.Log.Retries,
        ["followUps"] = r.Log.FollowUps,
        ["tokens"] = r.Log.Tokens,
        ["requests"] = r.Log.Requests,
        ["minutes"] = r.Minutes,
        ["model"] = r.Log.ModelId,
        ["flags"] = new JsonArray([.. r.Flags.Select(f => (JsonNode?)f)]),
        ["planted"] = new JsonArray([.. r.Planted.Select(g => (JsonNode?)new JsonObject { ["id"] = g.Gap.Id, ["status"] = g.Status, ["line"] = g.Line, ["call"] = g.Call, ["fixedInBody"] = g.FixedInBody })]),
        ["otherGaps"] = new JsonArray([.. r.OtherGaps.Select(g => (JsonNode?)g.Text)]),
        ["decisionFolded"] = r.DecisionFolded,
        ["reargued"] = new JsonArray([.. r.Reargued.Select(t => (JsonNode?)t)]),
        ["lostContent"] = r.LostContent,
        ["convergedWithHighOpen"] = r.ConvergedWithHighOpen,
        ["bugs"] = new JsonArray([.. r.Bugs.Select(b => (JsonNode?)new JsonObject
        {
            ["id"] = b.Bug.Id,
            ["status"] = b.Status,
            ["finding"] = b.Finding?.Id,
            ["severity"] = b.Finding is { } f ? AuditFinding.Word(f.Severity) : null,
            ["pass"] = b.Finding?.Pass,
        })]),
        ["decoysFlagged"] = new JsonArray([.. r.DecoysFlagged.Select(d => (JsonNode?)d.Id)]),
        ["otherFindings"] = new JsonArray([.. r.OtherFindings.Select(f => (JsonNode?)$"{f.Location}: {f.Title}")]),
        ["coverageRead"] = r.CoverageRead,
        ["coverageScope"] = r.CoverageScope,
        ["shadowWouldConvergeAt"] = r.Log.ShadowWouldConvergeAt,
        ["bugsAfterShadow"] = new JsonArray([.. r.BugsAfterShadow.Select(b => (JsonNode?)b)]),
    };

    private static void Row(StringBuilder md, string name, IEnumerable<double> values)
    {
        var v = values.Order().ToList();
        if (v.Count == 0)
        {
            return;
        }

        double median = v.Count % 2 == 1 ? v[v.Count / 2] : (v[(v.Count / 2) - 1] + v[v.Count / 2]) / 2;
        md.AppendLine($"| {name} | {Num(median)} | {Num(v[0])}–{Num(v[^1])} |");
    }

    private static string Num(double d) => d.ToString("0.#", CultureInfo.InvariantCulture);

    private static string Flat(string s)
    {
        string flat = string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Replace("|", "\\|", StringComparison.Ordinal);
        return flat.Length > 400 ? flat[..400] + "…" : flat;
    }
}

// ── --check ─────────────────────────────────────────────────────────────────

static class Checker
{
    private static readonly string[] Severities = ["critical", "high", "medium", "low"];

    public static int Run()
    {
        var m = Material.Load(Repo.MaterialRoot);
        var problems = new List<string>(m.Problems);
        foreach (var plan in m.Plans)
        {
            CheckPlan(m, plan, problems);
        }

        if (m.Audit is { } audit)
        {
            CheckAudit(m, audit, problems);
        }
        else
        {
            problems.Add("repo.expect.json is missing");
        }

        string tests = RunRepoTests(m.RepoDir, problems);
        Console.WriteLine($"Material: {m.Plans.Count} plans ({string.Join(", ", m.Plans.Select(p => p.Name))}), "
            + $"{m.Audit?.Bugs.Count ?? 0} bugs, {m.Audit?.Decoys.Count ?? 0} decoys. {tests}");
        if (problems.Count == 0)
        {
            Console.WriteLine("--check: OK");
            return 0;
        }

        Console.WriteLine($"--check: {problems.Count} problem(s):");
        problems.ForEach(p => Console.WriteLine("  " + p));
        return 1;
    }

    private static void CheckPlan(Material m, PlanItem plan, List<string> problems)
    {
        string where = $"plans/{plan.Name}";
        string text = File.ReadAllText(plan.PlanPath);
        string body = PlanDocument.Body(text);
        var fit = PlanFit.Read(body, null, reviewed: true, RalphText.InlinePlanChars);
        problems.AddRange(fit.Structural.Select(s => $"{where}.md: the app's shape check: {s}"));
        if (fit.TooLong is { } tooLong)
        {
            problems.Add($"{where}.md: {tooLong}");
        }

        if (fit.Steps.Count == 0)
        {
            problems.Add($"{where}.md: no steps");
        }

        foreach (var gap in plan.Gaps)
        {
            if (!Severities.Contains(gap.Severity))
            {
                problems.Add($"{where}.expect.json: {gap.Id}'s severity \"{gap.Severity}\" isn't one of {string.Join(", ", Severities)}");
            }

            if (gap.Fixed?.LineIn(body) is { } line)
            {
                problems.Add($"{where}.expect.json: {gap.Id}'s fixed match is already in the starting plan: «{line}»");
            }

            foreach (string gone in gap.Gone.Where(g => !body.Contains(g, StringComparison.OrdinalIgnoreCase)))
            {
                problems.Add($"{where}.expect.json: {gap.Id}'s gone \"{gone}\" isn't in the starting plan, so it reads as fixed from the start");
            }
        }

        if (plan.Control && plan.Gaps.Count > 0)
        {
            problems.Add($"{where}.expect.json: a control plan has no planted gaps");
        }

        if (plan.Decision is { } decision && decision.AnyIn(body))
        {
            problems.Add($"{where}.expect.json: part of the decision ({decision}) is already in the starting plan");
        }

        if ((plan.Decision is null) != (plan.Chat is null))
        {
            problems.Add($"{where}.expect.json: chat and decision go together");
        }

        if (plan.Chat is { } chat && (chat.Any(c => c > 0x7e || c < 0x20) || chat.Length == 0))
        {
            problems.Add($"{where}.expect.json: the chat line must be plain ASCII on one line (headless sets no input encoding)");
        }

        foreach (string absent in plan.Absent)
        {
            foreach (string file in Directory.GetFiles(m.RepoDir, "*", SearchOption.AllDirectories))
            {
                if (File.ReadAllText(file).Contains(absent, StringComparison.Ordinal))
                {
                    problems.Add($"{where}.expect.json: \"{absent}\" must not be in repo/, but {Path.GetRelativePath(m.RepoDir, file)} has it");
                }
            }
        }
    }

    private static void CheckAudit(Material m, AuditItem audit, List<string> problems)
    {
        var spots = audit.Bugs.Concat(audit.Decoys).ToList();
        foreach (var spot in spots)
        {
            string file = Path.Combine(m.RepoDir, spot.File.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(file))
            {
                problems.Add($"repo.expect.json: {spot.Id}: {spot.File} isn't in repo/");
                continue;
            }

            string[] lines = File.ReadAllLines(file);
            if (spot.Line < 1 || spot.Line > lines.Length || !lines[spot.Line - 1].Contains(spot.Contains, StringComparison.Ordinal))
            {
                int actual = Array.FindIndex(lines, l => l.Contains(spot.Contains, StringComparison.Ordinal)) + 1;
                problems.Add($"repo.expect.json: {spot.Id}: {spot.File}:{spot.Line} doesn't hold «{spot.Contains}»" + (actual > 0 ? $" (line {actual} does)" : ""));
            }
        }

        foreach (var bug in audit.Bugs)
        {
            if (bug.Severity is null || !Severities.Contains(bug.Severity))
            {
                problems.Add($"repo.expect.json: {bug.Id}'s severity \"{bug.Severity}\" isn't one of {string.Join(", ", Severities)}");
            }

            if (bug.Dimension is null || !AuditFocus.Dimensions.Contains(bug.Dimension))
            {
                problems.Add($"repo.expect.json: {bug.Id}'s dimension \"{bug.Dimension}\" isn't one of {string.Join(", ", AuditFocus.Dimensions)}");
            }
        }

        // A finding matches a spot within ±3 lines, so two spots in one file closer than 7 would share findings.
        for (int i = 0; i < spots.Count; i++)
        {
            for (int j = i + 1; j < spots.Count; j++)
            {
                if (Scorer.SameFile(spots[i].File, spots[j].File) && Math.Abs(spots[i].Line - spots[j].Line) < 7)
                {
                    problems.Add($"repo.expect.json: {spots[i].Id} and {spots[j].Id} are {Math.Abs(spots[i].Line - spots[j].Line)} lines apart in {spots[i].File}: keep them 7 or more apart");
                }
            }
        }
    }

    /// <summary>repo/'s own tests with Python's unittest, writing no bytecode; a missing Python is reported, not a failure.</summary>
    private static string RunRepoTests(string repo, List<string> problems)
    {
        string[] pythons = OperatingSystem.IsWindows() ? ["python", "py"] : ["python3", "python"];
        foreach (string python in pythons)
        {
            var start = new ProcessStartInfo(python) { WorkingDirectory = repo, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (string a in (string[])["-B", "-m", "unittest", "discover", "-s", "tests", "-t", "."])
            {
                start.ArgumentList.Add(a);
            }

            start.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
            Process? process;
            try
            {
                process = Process.Start(start);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                continue;
            }

            if (process is null)
            {
                continue;
            }

            using (process)
            {
                var output = process.StandardOutput.ReadToEndAsync();
                string error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                _ = output.Result;
                if (process.ExitCode == 9009 || error.Contains("was not found", StringComparison.Ordinal) && error.Contains("Microsoft Store", StringComparison.Ordinal))
                {
                    continue;   // the Windows Store's python stub
                }

                if (process.ExitCode != 0)
                {
                    problems.Add($"repo/'s tests failed ({python} -m unittest):\n{error.Trim()}");
                    return "repo/'s tests: FAILED.";
                }

                string summary = error.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("Ran ", StringComparison.Ordinal)) ?? "ran";
                return $"repo/'s tests: {summary}, OK.";
            }
        }

        return "repo/'s tests: not run (no Python found).";
    }
}

// ── --self-test ─────────────────────────────────────────────────────────────

static class SelfTest
{
    private static int _failed;

    public static int Run()
    {
        _failed = 0;
        Notices();
        LogLines();
        PlanRun();
        AuditRun();
        Console.WriteLine(_failed == 0 ? "--self-test: OK" : $"--self-test: {_failed} check(s) failed");
        return _failed == 0 ? 0 : 1;
    }

    private static void Expect(bool ok, string what)
    {
        if (!ok)
        {
            _failed++;
            Console.WriteLine("  FAIL " + what);
        }
    }

    private static void Notices()
    {
        foreach (var reason in Enum.GetValues<PlanGapStop>().Where(r => r != PlanGapStop.Converged))
        {
            foreach (var (passes, closed) in new[] { (1, 1), (4, 12), (1, 0) })
            {
                string line = "[notice] " + PlanText.GapStopNotice(reason, passes, closed);
                var stop = Patterns.LastStop(line, Patterns.PlanStops);
                Expect(stop?.Reason == reason || (stop?.Name == "stopped" && reason is PlanGapStop.UserSpoke or PlanGapStop.Stopped),
                    $"plan notice {reason} ({passes}, {closed}) read as {stop?.Name ?? "nothing"}");
            }

            foreach (int passes in new[] { 1, 7, 1234 })
            {
                var audit = Patterns.LastStop(AuditText.StopNotice(reason, "audit-20261117", passes), Patterns.AuditStops);
                Expect(audit?.Reason == reason, $"audit notice {reason} ({passes}) read as {audit?.Name ?? "nothing"}");
            }
        }

        foreach (int clean in new[] { 1, 2, 3 })
        {
            var stop = Patterns.LastStop("[notice] " + PlanText.ConvergedNotice(".neon/plans/x y.md", 1, 1, clean) + " /plan approve …", Patterns.PlanStops);
            Expect(stop?.Reason == PlanGapStop.Converged, $"plan converged (clean {clean}) read as {stop?.Name ?? "nothing"}");
        }

        var full = Patterns.LastStop(AuditText.ConvergedNotice("audit-x", 5, 2, 9, 9, pane: false), Patterns.AuditStops);
        Expect(full?.Name == "converged", $"audit converged read as {full?.Name ?? "nothing"}");
        var part = Patterns.LastStop(AuditText.ConvergedNotice("audit-x", 1, 1, 1500, 2000, pane: false, unread: 3), Patterns.AuditStops);
        Expect(part?.Name == "converged, part read", $"audit converged on part of the scope read as {part?.Name ?? "nothing"}");
        Expect(Patterns.LastStop("You: hello\nNeon: fine", Patterns.PlanStops) is null, "a run with no notice reads as nothing");
    }

    private static void LogLines()
    {
        string log = string.Join('\n',
            "2026-11-17 23:19:39.864 [Info] App: Connected: LLM: http://127.0.0.1:30000/v1 model=org/Model-27B (configured)",
            "2026-11-17 23:20:01.341 [Debug] Llm: Usage: 16161 in, 152 out (83 reasoning), 16313 total; first token after 1.74s, streamed 0.87s.",
            "2026-11-17 23:20:03.958 [Debug] Llm: Usage: 100 in, 20 out, 120 total; first token after 0.29s, streamed 2.26s.",
            "x [Info] App: " + PlanText.PassLogLine(1, 2, ["a gap", "another"], false, ["minor"]),
            "x [Info] App: " + PlanText.RetryLogLine(2, "the connection was dropped"),
            "x [Info] App: " + PlanText.FollowUpLogLine(2, true),
            "x [Info] App: " + PlanText.CleanPassLogLine(2, 1, 2, ["steps"]),
            "x [Info] App: " + PlanText.CleanPassLogLine(3, 2, 2, ["risks"], ["a word"]));
        var f = LogFacts.Read(log);
        Expect(f.ModelId == "org/Model-27B", $"model id read as {f.ModelId}");
        Expect(f.Tokens == 16433 && f.Requests == 2, $"tokens read as {f.Tokens} over {f.Requests}");
        Expect(f.PlanPasses == 1 && f.PlanCleanPasses == 2, $"plan passes read as {f.PlanPasses} + {f.PlanCleanPasses} clean");
        Expect(f.Retries == 1 && f.FollowUps == 1, $"retries {f.Retries}, follow-ups {f.FollowUps}");

        string shadow = string.Join('\n',
            "x " + AuditText.PassLogLine(1, 3, 0, false, false, null),
            "x " + AuditText.ShadowLogLine(1, new AuditShadowPass([0, 1, 2, 0], 0, true, true), 2, null, false, 0),
            "x " + AuditText.PassLogLine(2, 0, 1, true, false, null),
            "x " + AuditText.ShadowLogLine(2, new AuditShadowPass([0, 0, 0, 1], 1, true, false), 2, null, true, 1),
            "x " + AuditText.ShadowLogLine(3, new AuditShadowPass([0, 0, 0, 0], 2, true, false), 2, null, true, 2),
            "x " + AuditText.ShadowLogLine(4, new AuditShadowPass([0, 0, 0, 0], 3, true, false), 2, PlanGapStop.Converged, true, 2));
        var a = LogFacts.Read(shadow);
        Expect(a.AuditPasses == 2, $"audit passes read as {a.AuditPasses}");
        Expect(a.ShadowWouldConvergeAt == 3, $"shadow would converge read as {a.ShadowWouldConvergeAt}");
        Expect(a.LastShadowLine?.EndsWith(AuditText.StopWord(PlanGapStop.Converged), StringComparison.Ordinal) == true, "the last shadow line's real word");
    }

    private static void PlanRun()
    {
        string folder = Scratch();
        try
        {
            var plan = new PlanItem("selftest", "", "", [
                new PlantedGap("G1", "high", "", new Keywords([["give_back", "return"], ["reserv"]]), null, []),
                new PlantedGap("G2", "high", "", new Keywords([["migrat"]]), null, []),
                new PlantedGap("G3", "medium", "", new Keywords([["unit test"]]), new Keywords([["test_reserve"]]), []),
                new PlantedGap("G4", "high", "", new Keywords([["bulk_insert"]]), null, ["bulk_insert"]),
                new PlantedGap("G5", "high", "", new Keywords([["never reported"]]), null, []),
            ], false, "Decided: 0.25 a day.", new Keywords([["0.25"]]), new Keywords([["rate"], ["open question"]]), []);
            string start = "# T\n\n## Steps\n\n- [ ] Call store.bulk_insert(rows).\n  Done when: it works.\n- [ ] Keep me.\n  Done when: kept.\n\n## Risks\n\n- Open question (your call): the rate.\n";
            string final = "# T\n\n## Steps\n\n- [ ] Write the rows one at a time, then add test_reserve.\n  Done when: it passes.\n\n## Risks\n\n- The rate is 0.25 a day.\n";
            File.WriteAllText(Path.Combine(folder, "start.md"), start);
            Directory.CreateDirectory(Path.Combine(folder, "work", ".neon", "plans"));
            File.WriteAllText(Path.Combine(folder, "work", ".neon", "plans", "selftest.md"), "---\nstatus: draft\nrevision: 3\n---\n" + final);
            string stdout = string.Join('\n',
                "You: /plan open selftest --chat",
                "[tool] present_plan {\"title\": \"T\",",
                "  \"markdown\": \"# T\\n...\",",
                "  \"gaps\": [\"Nothing says what a return does to a reserved book\", \"bulk_insert doesn't exist\", \"An unrelated gap\"],",
                "  \"minor\": [\"Named the migration step\"]}",
                "[tool] present_plan -> Plan saved as revision 2.",
                "[tool] present_plan {\"title\": \"T\", \"gaps\": [\"The rate is an open question\"], \"markdown\": \"x\"}",
                "[tool] present_plan -> " + PlanText.CheckedRequiredResult,
                "[tool] present_plan {\"title\": \"T\", \"gaps\": [",
                "[tool] present_plan -> Error: the arguments were not valid JSON.",
                "[tool] present_plan {\"title\": \"T\", \"gaps\": [], \"checked\": [\"steps\"], \"markdown\": \"x\"}",
                "[tool] present_plan -> Plan saved as revision 3.",
                "[notice] " + PlanText.ConvergedNotice(".neon/plans/selftest.md", 2, 3, 1));
            string log = "x " + PlanText.PassLogLine(1, 2, ["a"], false) + "\nx " + PlanText.CleanPassLogLine(2, 1, 1, ["steps"]);
            File.WriteAllText(Path.Combine(folder, "run.log"), log);
            var score = new RunScore { Item = "selftest", Run = 1, Folder = folder, Facts = new JsonObject(), Log = LogFacts.Read(log) };
            var stop = Patterns.LastStop(stdout, Patterns.PlanStops);
            score.Stop = stop?.Name ?? "unknown";
            score.StopReason = stop?.Reason;
            Scorer.ScorePlan(score, plan, stdout);

            Expect(score.Calls.Count == 4, $"present_plan calls read: {score.Calls.Count}");
            Expect(score.Calls.Count(c => c.Refused) == 2 && score.Calls.Count(c => c.Accepted) == 2, "two refused calls, two saved");
            Expect(score.Calls.Count(c => !c.Parsed) == 1 && score.Flags.Any(f => f.Contains("unparsed", StringComparison.Ordinal)), "the broken call is kept as unparsed");
            Expect(!score.Flags.Any(f => f.StartsWith("parse mismatch", StringComparison.Ordinal)), "no parse mismatch: " + string.Join("; ", score.Flags));
            string Status(string id) => score.Planted.First(g => g.Gap.Id == id).Status;
            Expect(Status("G1") == "caught", "G1 caught, the call's JSON spanning lines: " + Status("G1"));
            Expect(Status("G2") == "minor only", "G2 only in minor: " + Status("G2"));
            Expect(Status("G3") == "fixed silently", "G3 fixed silently: " + Status("G3"));
            Expect(Status("G4") == "caught" && score.Planted.First(g => g.Gap.Id == "G4").FixedInBody, "G4 caught and gone from the body");
            Expect(Status("G5") == "missed", "G5 missed: " + Status("G5"));
            Expect(score.Converged && score.ConvergedWithHighOpen, "converged with a high gap open (G5)");
            Expect(score.OtherGaps.Count == 2, $"other gaps: {score.OtherGaps.Count}");
            Expect(score.DecisionFolded == true && score.Reargued.Count == 1, $"decision folded {score.DecisionFolded}, re-argued {score.Reargued.Count}");
            Expect(score.LostContent && score.Loss!.Steps.Count > 0, "a lost step is listed");

            File.WriteAllText(Path.Combine(folder, "run.log"), log + "\nx " + PlanText.CleanPassLogLine(3, 2, 2, ["risks"]));
            var mismatch = new RunScore { Item = "selftest", Run = 1, Folder = folder, Facts = new JsonObject(), Log = LogFacts.Read(File.ReadAllText(Path.Combine(folder, "run.log"))) };
            Scorer.ScorePlan(mismatch, plan, stdout);
            Expect(mismatch.Flags.Any(f => f.StartsWith("parse mismatch", StringComparison.Ordinal)), "three logged passes against two saved calls is a parse mismatch");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static void AuditRun()
    {
        var audit = new AuditItem("", [
            new Spot("B1", "shelf/store.py", 98, "", "high", "security", ""),
            new Spot("B2", "shelf/loans.py", 60, "", "medium", "correctness", ""),
            new Spot("B3", "shelf/export.py", 12, "", "high", "security", ""),
            new Spot("B4", "shelf/cli.py", 31, "", "low", "performance", ""),
            new Spot("B5", "tests/test_loans.py", 47, "", "low", "tests", ""),
            new Spot("B6", "shelf/listing.py", 36, "", "low", "performance", "", new Keywords([["cmd_list"], ["n+1", "per book"]])),
        ], [new Spot("D1", "shelf/store.py", 81, "", null, null, "")]);
        var report = new AuditReport();
        AuditFinding F(string id, AuditSeverity severity, string file, int? line, int pass, string title = "t") =>
            new(id, severity, "security", file, line, title, "detail", null, null, pass, false, false, null, []);
        report.Findings.Add(F("A1", AuditSeverity.High, "shelf\\store.py", 99, 1));
        report.Findings.Add(F("A2", AuditSeverity.Medium, "./shelf/export.py", 10, 2));
        report.Findings.Add(F("A3", AuditSeverity.Low, "shelf/cli.py", null, 1));
        report.Findings.Add(F("A4", AuditSeverity.Medium, "shelf/store.py", 82, 1));
        report.Findings.Add(F("A5", AuditSeverity.Low, "shelf/other.py", 5, 4, "a bug nobody planted"));
        report.Findings.Add(F("A7", AuditSeverity.Low, "tests/test_loans.py", 48, 4));
        report.Retracted.Add(F("A6", AuditSeverity.Medium, "shelf/loans.py", 61, 2) with { Retracted = true, RetractReason = "not a bug" });
        report.Nits.Add("shelf/listing.py cmd_list runs one query per book (N+1).");
        var parsed = AuditReport.Parse(report.Render());
        Expect(parsed.Findings.Count == 6 && parsed.Retracted.Count == 1, $"the report round-trips: {parsed.Findings.Count} findings, {parsed.Retracted.Count} retracted");

        var score = new RunScore { Item = Material.AuditName, Run = 1, Folder = "", Facts = new JsonObject() };
        score.Log = LogFacts.Read("x " + AuditText.ShadowLogLine(3, new AuditShadowPass([0, 0, 0, 0], 2, true, false), 2, null, true, 2));
        Scorer.ScoreFindings(score, audit, parsed.Findings, parsed.Retracted, parsed.Nits);
        string Status(string id) => score.Bugs.First(b => b.Bug.Id == id).Status;
        Expect(Status("B1") == "caught", "a \\ path within ±3 is caught: " + Status("B1"));
        Expect(Status("B3") == "caught", "a ./ path within ±3 is caught: " + Status("B3"));
        Expect(Status("B4") == "file only", "a finding with no line is file only: " + Status("B4"));
        Expect(Status("B2") == "retracted", "a retracted finding: " + Status("B2"));
        Expect(Status("B6") == "nit only", "a bug named only among the nits: " + Status("B6"));
        Expect(score.DecoysFlagged.Count == 1, "the decoy is flagged");
        Expect(score.OtherFindings.Count == 1 && score.OtherFindings[0].Id == "A5", "one other finding");
        Expect(Scorer.SeverityOff(AuditSeverity.Medium, "high") == 1, "severity one level off");
        if (score.Log.ShadowWouldConvergeAt is int at)
        {
            score.BugsAfterShadow.AddRange(score.Bugs.Where(b => b.Finding is { } f && b.Status == "caught" && f.Pass > at).Select(b => b.Bug.Id));
        }

        Expect(score.BugsAfterShadow.SequenceEqual(["B5"]), "the bug found after the shadow rule would converge: " + string.Join(",", score.BugsAfterShadow));
    }

    private static string Scratch()
    {
        string folder = Path.Combine(Path.GetTempPath(), "gapbench-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }
}
