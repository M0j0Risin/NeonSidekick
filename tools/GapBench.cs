#:project ../src/NeonSidekick/NeonSidekick.csproj
#:property PublishAot=false

// GapBench: runs /plan gaps and /code-audit headless against planted material (tools/gapbench/) and scores what the model found
// (2026-11-17, phase 2 of .notes/plan_gaps_and_code_audit_improvements.md, the small version: almost no real review history yet, so a
// baseline on planted gaps and bugs answers whether reviews miss things, raise false ones or lose content often enough to improve).
//
//     dotnet run tools/GapBench.cs -- --url http://127.0.0.1:30000/v1                   every item, 3 runs each
//     dotnet run tools/GapBench.cs -- --url … --runs 1 --only version-flag,audit        a slice
//     dotnet run tools/GapBench.cs -- --url … --only audit --set CodeAuditLowKeepsStreak=true    a setting under test
//     dotnet run tools/GapBench.cs -- --check                                           the material is consistent; no model
//     dotnet run tools/GapBench.cs -- --score publish/gapbench/<folder>                 re-scores saved runs (after a keyword is tuned)
//     dotnet run tools/GapBench.cs -- --self-test                                       the scorer on built-in samples; no model
//
// Each run gets a fresh work folder (repo/ copied, and the plan under .neon/plans/), a scratch NEONSIDEKICK_HOME whose profile copies the
// daily profile's model settings (--like), and the published exe headless with a script on stdin. The model never sees the answers:
// no *.expect.json and no README is copied. Runs go in rounds (run 1 of every item, then run 2…), so a bench cut short overnight leaves
// every item with the same count; a finished run writes done.json, and the same --out resumes.
// --set Name=value (2026-11-17, phase 3) writes one of the app's settings into each run's profile last; the name must be an AppSettingsData
// property, and a resume must name the same pairs (bench.json records them), so one folder never mixes runs with and without a setting.
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
        "                                     [--set <Setting>=<value>]...   (a profile.json key; the value as JSON, else a string)\n" +
        "                                     [--from <bench folder>,...]    (a fresh review of each final plan whose review left a gap missed)\n" +
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

    /// <summary>
    /// The <c>--from</c> bench folders (2026-11-19, the fresh-look probe): each finished plan run in them whose review left a gap missed gets a new
    /// review of its final plan (<see cref="Probe"/>).
    /// </summary>
    public List<string>? From { get; private set; }

    /// <summary>The <c>--set</c> pairs in the order given (2026-11-17): written into each run's profile after everything else.</summary>
    public List<(string Name, JsonNode? Value)> Sets { get; } = [];

    /// <summary>
    /// One <c>--set Name=value</c> (2026-11-17): null with why when the name isn't a public property of the app's <c>AppSettingsData</c> (the app
    /// ignores an unknown key in profile.json without a word, so a misspelling would quietly measure the default), or is <c>FileTools</c>, which
    /// a run can't do without. The value reads as JSON (<c>true</c>, <c>3</c>, <c>{…}</c>), else as a string.
    /// </summary>
    public static (string Name, JsonNode? Value)? ParseSet(string text, out string? error)
    {
        error = null;
        int eq = text.IndexOf('=', StringComparison.Ordinal);
        string name = eq > 0 ? text[..eq].Trim() : "";
        if (name.Length == 0)
        {
            error = $"--set {text}: expected Name=value";
            return null;
        }

        if (typeof(NeonSidekick.Settings.AppSettingsData).GetProperty(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance) is not { CanWrite: true })
        {
            error = $"--set {name}: not one of the app's settings (a property of AppSettingsData, spelled as profile.json spells it)";
            return null;
        }

        if (name == "FileTools")
        {
            error = "--set FileTools: a run can't read its work folder without the file tools";
            return null;
        }

        string raw = text[(eq + 1)..];
        JsonNode? value;
        try
        {
            value = JsonNode.Parse(raw);
        }
        catch (JsonException)
        {
            value = JsonValue.Create(raw);
        }

        return (name, value);
    }

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
                case "--from":
                    o.From = [.. (Next() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
                    if (o.From.Count == 0)
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
                case "--set":
                    if (ParseSet(Next() ?? "", out string? setError) is not { } set)
                    {
                        Console.Error.WriteLine(setError);
                        return null;
                    }

                    o.Sets.RemoveAll(s => s.Name == set.Name);
                    o.Sets.Add(set);
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

/// <summary>
/// A planted gap. <paramref name="Hits"/> and <paramref name="Misses"/> (2026-11-18, the keyword check) are lines a reviewer wrote that must or must not
/// match it, and <paramref name="InPlan"/> lets its match fit a line of the starting plan (C2's <c>bulk_insert</c>, the method the plan calls); only
/// <c>--check</c> reads them.
/// <para>
/// A <paramref name="Trigger"/> (2026-11-18, follow-on gaps) makes it a follow-on gap: one the starting plan doesn't have, that a revision
/// creates by taking the trigger's path (withdraw-books' <c>withdrawn_on</c> column), so only a pass after that revision can catch it. It's
/// matched a line at a time; <paramref name="TriggerHits"/> and <paramref name="TriggerMisses"/> are body lines it must and mustn't match.
/// </para>
/// </summary>
sealed record PlantedGap(string Id, string Severity, string What, Keywords Match, Keywords? Fixed, IReadOnlyList<string> Gone,
    IReadOnlyList<string>? Hits = null, IReadOnlyList<string>? Misses = null, bool InPlan = false,
    Keywords? Trigger = null, IReadOnlyList<string>? TriggerHits = null, IReadOnlyList<string>? TriggerMisses = null)
{
    public bool FollowOn => Trigger is not null;
}

sealed record PlanItem(string Name, string PlanPath, string ExpectPath, IReadOnlyList<PlantedGap> Gaps, bool Control, string? Chat, Keywords? Decision, Keywords? Reargue, IReadOnlyList<string> Absent);

sealed record Spot(string Id, string File, int Line, string Contains, string? Severity, string? Dimension, string What, Keywords? Nit = null);

sealed record AuditItem(string ExpectPath, IReadOnlyList<Spot> Bugs, IReadOnlyList<Spot> Decoys);

/// <summary>A probe's source (2026-11-19): a run (its folder, relative to the repo root), its item, and the gaps its review left missed.</summary>
sealed record ProbeSource(string Source, string Item, IReadOnlyList<string> Gaps);

/// <summary>
/// The fresh-look probe (2026-11-19, <c>--from</c>): a new review started on the final plan of a run whose review left a gap missed, to see
/// whether a look without that review's memory names it. The follow-on bench's later passes, in the same conversation, caught 1 of 8 F3s
/// left open and none of 3 F5s; Phase 3's fresh confirming passes are worth building only if a fresh look does better.
/// </summary>
static class Probe
{
    /// <summary>How <see cref="Report"/> keys a probe run's item for the hash check: it is judged by <see cref="Material.RepoHash"/>.</summary>
    public const string ItemPrefix = "probe:";

    /// <summary>
    /// The sources, folders in ordinal order and runs by item then number: each plan run whose review left a gap missed (a planted or a
    /// follow-on one; "minor only" doesn't count, as the plan names it). Audit runs, runs of an item gone from the material, and runs whose
    /// final plan is gone are named in <paramref name="skipped"/>.
    /// </summary>
    public static List<ProbeSource> Pick(IEnumerable<string> folders, Material material, List<string> skipped)
    {
        var picked = new List<ProbeSource>();
        foreach (string folder in folders.Select(Path.GetFullPath).Order(StringComparer.Ordinal))
        {
            var scores = Directory.GetDirectories(folder).Where(d => File.Exists(Path.Combine(d, "done.json"))).Select(d => Scorer.Score(d, material));
            foreach (var score in scores.OrderBy(s => s.Item, StringComparer.Ordinal).ThenBy(s => s.Run))
            {
                string source = Normal(score.Folder);
                if (score.IsAudit || material.Plan(score.Item) is null)
                {
                    skipped.Add($"{source}: {(score.IsAudit ? "an audit run" : "its item is no longer in the material")}");
                    continue;
                }

                if (!File.Exists(FinalPlan(source, score.Item)))
                {
                    skipped.Add($"{source}: its final plan is gone");
                    continue;
                }

                var gaps = MissedGaps(score);
                if (gaps.Count > 0)
                {
                    picked.Add(new ProbeSource(source, score.Item, gaps));
                }
            }
        }

        return picked;
    }

    /// <summary>The gaps a run's review left missed, planted then follow-on; "minor only", foreseen, caught and fixed ones aren't.</summary>
    public static List<string> MissedGaps(RunScore score) =>
        [.. score.Planted.Where(g => g.Status == "missed").Select(g => g.Gap.Id)
            .Concat(score.FollowOn.Where(f => f.Status == FollowOnStatus.Missed).Select(f => f.Gap.Id))];

    /// <summary>A folder as <c>bench.json</c> and <c>done.json</c> keep it: relative to the repo root, with forward slashes.</summary>
    public static string Normal(string folder) => Path.GetRelativePath(Repo.Root, Path.GetFullPath(folder)).Replace('\\', '/');

    public static string FinalPlan(string source, string item) => Path.Combine(Repo.Root, source, "work", ".neon", "plans", item + ".md");

    /// <summary>
    /// The plan as the probe gives it: the source's final plan without its front matter's <c>review:</c> line, which <c>/plan open</c> shows as
    /// "reviewed" and could tell the model the plan is done. The body is untouched.
    /// </summary>
    public static string StripReview(string text)
    {
        string nl = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Split(nl).ToList();
        int end = lines.Count > 0 && lines[0].Trim() == "---" ? lines.FindIndex(1, l => l.Trim() == "---") : -1;
        for (int i = end - 1; i >= 1; i--)
        {
            if (lines[i].StartsWith("review:", StringComparison.Ordinal))
            {
                lines.RemoveAt(i);
            }
        }

        return string.Join(nl, lines);
    }

    /// <summary>
    /// The item a probe run is scored against: only its source's missed gaps, each as an ordinary gap (no trigger), so it scores caught, minor
    /// only, fixed silently or missed, with the pass that caught it.
    /// </summary>
    public static PlanItem Derive(PlanItem plan, IReadOnlyCollection<string> ids) =>
        plan with { Gaps = [.. plan.Gaps.Where(g => ids.Contains(g.Id)).Select(g => g with { Trigger = null, TriggerHits = null, TriggerMisses = null })] };

    public static JsonArray ToJson(IEnumerable<ProbeSource> sources) =>
        new([.. sources.Select(s => (JsonNode?)new JsonObject
        {
            ["source"] = s.Source, ["item"] = s.Item, ["gaps"] = new JsonArray([.. s.Gaps.Select(g => (JsonNode?)g)]),
        })]);

    /// <summary>The sources a probe bench recorded, so a resume runs the same list in the same order whatever the keywords say now.</summary>
    public static List<ProbeSource>? Recorded(string outDir)
    {
        string path = Path.Combine(outDir, "bench.json");
        return File.Exists(path) && JsonNode.Parse(File.ReadAllText(path))?["sources"] is JsonArray sources
            ? [.. sources.Select(n => new ProbeSource((string)n!["source"]!, (string)n["item"]!, [.. n["gaps"]!.AsArray().Select(g => (string)g!)]))]
            : null;
    }

    /// <summary>
    /// Null when the folder's recorded <c>--from</c> list (none for an ordinary bench) is the one given now, or a folder never run; else the
    /// recorded list, for the refusal.
    /// </summary>
    public static string? FromDiffers(string outDir, IReadOnlyList<string>? from)
    {
        string path = Path.Combine(outDir, "bench.json");
        if (!File.Exists(path))
        {
            return null;
        }

        var recorded = JsonNode.Parse(File.ReadAllText(path))?["from"] is JsonArray a ? a.Select(n => (string)n!).ToList() : [];
        var now = (from ?? []).Select(Normal).ToList();
        return recorded.SequenceEqual(now, StringComparer.Ordinal) ? null : recorded.Count == 0 ? "(none)" : string.Join(",", recorded);
    }
}

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
                    g.TryGetProperty("gone", out var gone) ? Strings(gone) : [],
                    g.TryGetProperty("hits", out var hits) ? Strings(hits) : [],
                    g.TryGetProperty("misses", out var misses) ? Strings(misses) : [],
                    g.TryGetProperty("inPlan", out var inPlan) && inPlan.GetBoolean(),
                    g.TryGetProperty("trigger", out var trigger) ? Groups(trigger) : null,
                    g.TryGetProperty("triggerHits", out var triggerHits) ? Strings(triggerHits) : null,
                    g.TryGetProperty("triggerMisses", out var triggerMisses) ? Strings(triggerMisses) : null));
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
    /// can say when the material changed since. Every file: a run from before <see cref="ItemHash"/> (2026-11-18) has only this one to compare.
    /// </summary>
    public static string Hash(string root) => HashFiles(root, _ => true);

    /// <summary><c>repo/</c> alone (2026-11-19): what a probe run saw besides its source's plan, which it keeps as its own <c>start.md</c>.</summary>
    public static string RepoHash(string root) => HashFiles(root, f => f.StartsWith("repo/", StringComparison.Ordinal));

    /// <summary>
    /// The hash of what one item's model saw (2026-11-18, late gaps): <c>repo/</c>, plus <c>plans/&lt;name&gt;.md</c> for a plan (the audit sees the repo
    /// alone), so adding or changing another plan doesn't flag this item's runs. The <c>*.expect.json</c> files are left out: they are the scorer's key,
    /// never shown to the model, and re-scoring after a keyword is tuned is what <c>--score</c> is for. A plan whose file is gone hashes <c>""</c>, so its
    /// runs read as changed.
    /// </summary>
    public static string ItemHash(string root, string item)
    {
        string plan = "plans/" + item + ".md";
        bool audit = item == AuditName;
        if (!audit && !File.Exists(Path.Combine(root, plan)))
        {
            return "";
        }

        return HashFiles(root, f => f.StartsWith("repo/", StringComparison.Ordinal) || (!audit && f == plan));
    }

    /// <summary>
    /// The items whose runs were scored against material that has changed since (2026-11-18), once each in ordinal order: a run that recorded an
    /// <c>itemHash</c> is judged by its own item's, one from before that by the whole <see cref="Hash"/>.
    /// </summary>
    public static IReadOnlyList<string> ChangedItems(IEnumerable<(string Item, string? MaterialHash, string? ItemHash)> runs, string currentHash, Func<string, string> currentItemHash) =>
        [.. runs.Where(r => r.ItemHash is { } own ? own != currentItemHash(r.Item) : r.MaterialHash != currentHash)
            .Select(r => r.Item).Distinct().Order(StringComparer.Ordinal)];

    private static string HashFiles(string root, Func<string, bool> include)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                     .Where(f => !f.Contains("__pycache__", StringComparison.Ordinal) && !f.Contains(".pytest_cache", StringComparison.Ordinal))
                     .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
                     .Where(include)
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

    /// <summary>A second <c>present_plan</c> in a gap pass, refused (2026-11-17, one revision a pass): read from the call's result in stdout.</summary>
    public static readonly Regex SecondSave = Template.Build(PlanText.PassAlreadySavedResult(Template.S1, Template.N1));

    // 2026-11-18 (phase 3 item 2): a gap revision sent back for a structural problem, and a saved one whose result still names one.
    public static readonly Regex ShapeGap = Template.Build(PlanText.ShapeGapResult([Template.S1]));
    public static readonly Regex MeasuredLeft = Template.Build(PlanText.MeasuredSentence([Template.S1]));
    public static readonly Regex[] PlanFollowUp = [Template.Build(PlanText.FollowUpLogLine(Template.N1, false)), Template.Build(PlanText.FollowUpLogLine(Template.N1, true))];
    public static readonly Regex AuditPass = Template.Build(AuditText.PassLogLine(Template.N1, Template.N2, 0, false, false, null), cutBefore: Template.N2);

    /// <summary>
    /// A clean audit pass's low changes (2026-11-17, Code audit low keeps streak): the pass line's count captured, its words the app's own in
    /// both numbers, so "1 change to the findings" never reads as one.
    /// </summary>
    public static readonly Regex AuditLight = CountedTail(
        AuditText.PassLogLine(Template.N1, 0, 0, true, false, null, light: Template.N2), AuditText.PassLogLine(Template.N1, 0, 0, true, false, null, light: 1), Template.N2);

    /// <summary>The plan pass notice's first line (stdout), its pass captured: a pass with gaps, and a clean one (2026-11-17, a call's pass).</summary>
    public static readonly Regex[] PlanPassNotices =
    [
        Template.Build(PlanText.PassNoticeLines(Template.N1, Template.N2, Template.N3, ["QQSENTA"])[0], cutBefore: Template.N3, capture: Template.N1),
        Template.Build(PlanText.CleanPassNoticeLines(Template.N1, Template.N2, Template.N3, Template.N4, [])[0], cutBefore: Template.N3, capture: Template.N1),
    ];

    /// <summary>
    /// A confirming pass's focus line (2026-11-18, Phase 3 item 3, focus selection): the plan's and the audit's, each with its pass (<c>n</c>), its
    /// focus (<c>label</c>) and how it was picked (<c>how</c>), split out of the app's own rendering.
    /// </summary>
    public static readonly Regex[] FocusLines = [FocusLine(PlanText.FocusLogLine(Template.N1, Template.S1, Template.S2)), FocusLine(AuditText.FocusLogLine(Template.N1, Template.S1, Template.S2))];

    private static Regex FocusLine(string rendered)
    {
        string n = Template.N1.ToString(CultureInfo.InvariantCulture);
        int pass = rendered.IndexOf(n, StringComparison.Ordinal), label = rendered.IndexOf(Template.S1, StringComparison.Ordinal), how = rendered.IndexOf(Template.S2, StringComparison.Ordinal);
        return new Regex(Regex.Escape(rendered[..pass]) + @"(?<n>\d+)" + Regex.Escape(rendered[(pass + n.Length)..label]) + "(?<label>.+?)"
            + Regex.Escape(rendered[(label + Template.S1.Length)..how]) + "(?<how>.+)$", RegexOptions.CultureInvariant);
    }

    /// <summary>
    /// A plan review summary's pass line in stdout with a confirming focus (2026-11-18): headless prints it as <c>[notice] pass 4: 1 gap, 3 minor fixes
    /// (order) · 1m 17s</c>. Anchored on the notice at the line's start and the first <c>(label)</c> or <c>(label, advisor)</c> after it, so a reply that
    /// writes "pass 3: … (order)" isn't read; the label one of <see cref="PlanText.ConfirmFocusLabels"/>. For runs from before the focus lines.
    /// </summary>
    public static readonly Regex SummaryFocus = new(@"^\[notice\] pass (?<n>\d+): [^(\n]*?\((?<label>" + string.Join("|", PlanText.ConfirmFocusLabels.Select(Regex.Escape)) + @")(?:, advisor)?\)",
        RegexOptions.CultureInvariant);

    /// <summary>The plan shadow line's pass number (2026-11-17, phase 3), cut before what the pass did, as the audit's.</summary>
    public static readonly Regex PlanShadowPass = Template.Build(
        PlanText.ShadowLogLine(Template.N1, new GapShadowPass([Template.N4, 0, 0, 0], Template.N2, true, false), Template.N3, null, false, 0),
        cutBefore: Template.N4, capture: Template.N1);

    /// <summary>What a plan shadow line adds once its streak reaches the clean passes needed.</summary>
    public static readonly string PlanWouldConverge = Template.Inserted(
        PlanText.ShadowLogLine(1, new GapShadowPass([0, 0, 0, 0], 2, true, false), 2, null, true, 1),
        PlanText.ShadowLogLine(1, new GapShadowPass([0, 0, 0, 0], 1, true, false), 2, null, true, 1));

    /// <summary>
    /// <paramref name="many"/> (rendered with <paramref name="sentinel"/>) up to the sentinel, then the count captured as <c>n</c> and the words
    /// after it as either rendering has them (<paramref name="one"/>: the count 1).
    /// </summary>
    private static Regex CountedTail(string many, string one, int sentinel)
    {
        string s = sentinel.ToString(CultureInfo.InvariantCulture);
        int at = many.IndexOf(s, StringComparison.Ordinal);
        string prefix = Template.Build(many, cutBefore: sentinel).ToString();
        string manyWords = many[(at + s.Length)..];
        string oneWords = one[(at + 1)..];
        return new Regex(prefix + @"(?<n>[\d,]+)(?:" + Regex.Escape(manyWords) + "|" + Regex.Escape(oneWords) + ")(?=,|$)", RegexOptions.CultureInvariant);
    }
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
            // A streak low changes kept (2026-11-17, Code audit low keeps streak): its own words, never "nothing new" alone.
            new("converged", PlanGapStop.Converged, Template.Build(AuditText.ConvergedNotice(Template.S1, Template.N1, Template.N2, Template.N3, Template.N3, pane: false, light: true))),
            new("converged, part read", PlanGapStop.Converged, Template.Build(AuditText.ConvergedNotice(Template.S1, Template.N1, Template.N2, Template.N3, Template.N4, pane: false, light: true))),
            new("converged, part read", PlanGapStop.Converged, Template.Build(AuditText.ConvergedNotice(Template.S1, Template.N1, Template.N2, Template.N3, Template.N4, pane: false, unread: Template.N5, light: true))),
            new(Name(PlanGapStop.Unconfirmed), PlanGapStop.Unconfirmed, Template.Build(AuditText.StopNotice(PlanGapStop.Unconfirmed, Template.S1, Template.N1, lastLight: true))),
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

    /// <summary>
    /// One argument as the app reads it (2026-11-18): null when it was left out, else as <c>ToolArguments.ReadString</c> reads it (JSON null
    /// gives "", a string its value, anything else its raw text), trimmed as <c>present_plan</c> trims its checks and title unless
    /// <paramref name="trim"/> is false. <see cref="List"/> can't tell a check left out (kept) from an empty one (none).
    /// </summary>
    public string? Text(string name, bool trim = true)
    {
        if (Arguments is not { ValueKind: JsonValueKind.Object } args || !args.TryGetProperty(name, out var value))
        {
            return null;
        }

        string text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Null => "",
            _ => value.GetRawText(),
        };
        return trim ? text.Trim() : text;
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

    /// <summary>The audit's low changes that kept the streak (2026-11-17, Code audit low keeps streak), over every pass, and the passes that had some.</summary>
    public int AuditLight { get; private set; }
    public int AuditLightPasses { get; private set; }

    /// <summary>The confirming passes' focus lines (2026-11-18, Phase 3 item 3), in order: the pass, its focus, and how it was picked.</summary>
    public List<FocusRecord> Focuses { get; } = [];

    /// <summary>The plan shadow (2026-11-17, phase 3): its lines, the first pass it would have converged at, and its last line.</summary>
    public int PlanShadowLines { get; private set; }
    public int? PlanShadowWouldConvergeAt { get; private set; }
    public string? LastPlanShadowLine { get; private set; }

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

            // A focus line (2026-11-18) is never a pass either.
            if (Patterns.FocusLines.Select(p => p.Match(line)).FirstOrDefault(m => m.Success) is { } focusLine)
            {
                f.Focuses.Add(new FocusRecord(int.Parse(focusLine.Groups["n"].Value, CultureInfo.InvariantCulture), focusLine.Groups["label"].Value, focusLine.Groups["how"].Value));
                continue;
            }

            // Before the pass patterns, as the audit's: a shadow line is never a pass.
            if (line.Contains(PlanText.ShadowLogPrefix, StringComparison.Ordinal))
            {
                f.PlanShadowLines++;
                f.LastPlanShadowLine = line;
                if (f.PlanShadowWouldConvergeAt is null && line.Contains(Patterns.PlanWouldConverge, StringComparison.Ordinal)
                    && Patterns.PlanShadowPass.Match(line) is { Success: true } planShadow)
                {
                    f.PlanShadowWouldConvergeAt = int.Parse(planShadow.Groups["n"].Value.Replace(",", "", StringComparison.Ordinal), CultureInfo.InvariantCulture);
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
                if (Patterns.AuditLight.Match(line) is { Success: true } light)
                {
                    f.AuditLight += int.Parse(light.Groups["n"].Value.Replace(",", "", StringComparison.Ordinal), CultureInfo.InvariantCulture);
                    f.AuditLightPasses++;
                }
            }
        }

        return f;
    }
}

// ── Scoring ─────────────────────────────────────────────────────────────────

sealed record GapOutcome(PlantedGap Gap, string Status, string? Line, int? Call, bool FixedInBody);

/// <summary>
/// A follow-on gap's outcome (2026-11-18): its status (<see cref="FollowOnStatus"/>), the line that named it or the body line that fixed it,
/// the accepted call whose body first took its path, and the call whose line named it.
/// </summary>
sealed record FollowOnOutcome(PlantedGap Gap, string Status, string? Line, int? Introduced, int? Named);

/// <summary>A follow-on gap's statuses (2026-11-18), in the order they're judged: the first that applies wins.</summary>
static class FollowOnStatus
{
    public const string NotIntroduced = "not introduced";
    public const string Foreseen = "foreseen";
    public const string Caught = "caught";
    public const string MinorOnly = "minor only";
    public const string FixedSilently = "fixed silently";
    public const string Dropped = "dropped";
    public const string Missed = "missed";

    public static readonly string[] All = [NotIntroduced, Foreseen, Caught, MinorOnly, FixedSilently, Dropped, Missed];
}

sealed record BugOutcome(Spot Bug, string Status, AuditFinding? Finding, string? Nit = null);

/// <summary>One scored run: the facts from done.json, what the logs say, and the item's own scores.</summary>
/// <summary>A confirming pass's focus (2026-11-18, Phase 3 item 3): its pass, its label, and how it was picked (empty when read from the summary).</summary>
sealed record FocusRecord(int Pass, string Label, string How);

/// <summary>Which focuses a run's confirming passes looked at (2026-11-18, Phase 3 item 3, focus selection).</summary>
static class FocusReader
{
    /// <summary>
    /// The run's focus lines from its log; a run without them (every run before them) reads the plan review summary's labels in
    /// <paramref name="stdout"/> (<see cref="Patterns.SummaryFocus"/>), and says so. A pass line without a label adds none.
    /// </summary>
    public static (List<FocusRecord> Focuses, bool FromSummary) Read(LogFacts log, string stdout)
    {
        if (log.Focuses.Count > 0)
        {
            return (log.Focuses, false);
        }

        var found = new List<FocusRecord>();
        foreach (string raw in stdout.Split('\n'))
        {
            if (Patterns.SummaryFocus.Match(raw.TrimEnd('\r')) is { Success: true } m)
            {
                found.Add(new FocusRecord(int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture), m.Groups["label"].Value, ""));
            }
        }

        return (found, found.Count > 0);
    }

    /// <summary>Counts in the <c>label ×n</c> form the Stops line uses, the most first, ties by the labels' own order.</summary>
    public static string Counts(IEnumerable<FocusRecord> focuses) =>
        string.Join(", ", focuses.GroupBy(f => f.Label).OrderByDescending(g => g.Count()).ThenBy(g => Order(g.Key)).Select(g => $"{g.Key} ×{g.Count()}"));

    private static int Order(string label)
    {
        int i = PlanText.ConfirmFocusLabels.ToList().IndexOf(label);
        return i < 0 ? int.MaxValue : i;
    }
}

sealed class RunScore
{
    public required string Item { get; init; }

    /// <summary>A probe run's source (2026-11-19, <c>--from</c>): the run whose final plan it reviewed afresh; null for an ordinary run.</summary>
    public string? Source { get; set; }
    public required int Run { get; init; }
    public required string Folder { get; init; }
    public required JsonObject Facts { get; init; }
    public string Stop { get; set; } = "unknown";
    public PlanGapStop? StopReason { get; set; }
    public LogFacts Log { get; set; } = new();
    public double Minutes { get; set; }

    /// <summary>The confirming passes' focuses (2026-11-18, Phase 3 item 3), and whether they were read from the review summary (no focus lines).</summary>
    public List<FocusRecord> Focuses { get; set; } = [];
    public bool FocusesFromSummary { get; set; }
    public List<string> Flags { get; } = [];

    // A plan run.
    public List<ToolCallRecord> Calls { get; } = [];
    public List<GapOutcome> Planted { get; } = [];

    /// <summary>The follow-on gaps' outcomes (2026-11-18), kept out of <see cref="Planted"/> so every count of it stays starting-plan only.</summary>
    public List<FollowOnOutcome> FollowOn { get; } = [];
    public List<(int Call, string Text)> OtherGaps { get; } = [];
    public bool? DecisionFolded { get; set; }
    public List<string> Reargued { get; } = [];
    public PlanLossReport? Loss { get; set; }

    /// <summary>Each present_plan call's pass (2026-11-17), by its ordinal, from stdout's pass notices.</summary>
    public Dictionary<int, int> CallPasses { get; } = [];

    /// <summary>The planted gaps first caught after the plan shadow would have converged (2026-11-17, phase 3).</summary>
    public List<string> GapsAfterShadow { get; } = [];

    /// <summary>The <c>present_plan</c> calls refused as a pass's second save (2026-11-17, one revision a pass).</summary>
    public int SecondSavesRefused { get; set; }

    /// <summary>The gap revisions sent back for a structural problem (2026-11-18, known problems up front, <c>PlanText.ShapeGapResult</c>).</summary>
    public int ShapeRefusals { get; set; }

    /// <summary>The gap revisions saved with a structural problem still standing (2026-11-18): their result ends in <c>PlanText.MeasuredSentence</c>.</summary>
    public int MeasuredLeft { get; set; }

    /// <summary>The accepted clean reports (2026-11-18, the minor check measured): the headline's M.</summary>
    public int CleanReports { get; set; }

    /// <summary>The clean reports whose revision changed steps or a check (2026-11-18): the headline's N.</summary>
    public int CleanStepChanges => StepChanges.Count;

    /// <summary>Each clean report that changed steps or a check (2026-11-18, <see cref="PlanStepChanges"/>), by its call ordinal.</summary>
    public List<(int Call, int Added, int Removed, int Moved, int DoneWhen, bool Checks)> StepChanges { get; } = [];

    /// <summary>The pass of <paramref name="call"/>, null when unknown.</summary>
    public int? PassOf(int? call) => call is int c && CallPasses.TryGetValue(c, out int pass) ? pass : null;

    // An audit run.
    public List<BugOutcome> Bugs { get; } = [];
    public List<Spot> DecoysFlagged { get; } = [];
    public List<AuditFinding> OtherFindings { get; } = [];
    public int CoverageRead { get; set; }
    public int CoverageScope { get; set; }
    public List<string> BugsAfterShadow { get; } = [];

    /// <summary>The report's live findings, and those labeled low (2026-11-17: a drift toward low would mean the low-streak rule is gamed).</summary>
    public int FindingsTotal { get; set; }
    public int FindingsLow { get; set; }

    public bool IsAudit => Item == Material.AuditName;

    public bool IsProbe => Source is not null;

    public int Passes => IsAudit ? Log.AuditPasses : Log.PlanPasses + Log.PlanCleanPasses;

    public bool Converged => StopReason == PlanGapStop.Converged;

    public bool LostContent => Loss is { } l && (l.Steps.Count > 0 || l.Unticked.Count > 0 || l.Sections.Count > 0 || l.ShrinkPercent is not null);

    public bool ConvergedWithHighOpen => Converged && (IsAudit
        ? Bugs.Any(b => IsHigh(b.Bug.Severity) && b.Status is not ("caught" or "file only"))
        : Planted.Any(g => IsHigh(g.Gap.Severity) && g.Status != "caught" && !g.FixedInBody)
            || FollowOn.Any(f => IsHigh(f.Gap.Severity) && f.Status == FollowOnStatus.Missed));

    /// <summary>Whether a pass was a confirming one (2026-11-18): the run's focus records have its number.</summary>
    public bool IsConfirming(int? pass) => pass is int p && Focuses.Any(f => f.Pass == p);

    /// <summary>A confirming pass's focus (2026-11-18), or null for a pass that wasn't one.</summary>
    public string? FocusOf(int? pass) => pass is int p ? Focuses.FirstOrDefault(f => f.Pass == p)?.Label : null;

    public bool ShadowStoppedEarly => BugsAfterShadow.Count > 0;

    public bool PlanShadowStoppedEarly => GapsAfterShadow.Count > 0;

    /// <summary>
    /// How a caught gap's leading tag (<see cref="PlanGapTags"/>) compares with the planted severity (2026-11-17): same, one off, two off, or
    /// untagged.
    /// </summary>
    public static string LabelOf(GapOutcome g)
    {
        if (g.Status != "caught" || g.Line is null)
        {
            return "";
        }

        var tag = PlanGapTags.Read(g.Line).Severity;
        if (tag == PlanGapSeverity.Untagged)
        {
            return "untagged";
        }

        int planted = g.Gap.Severity.ToLowerInvariant() switch
        {
            "critical" or "high" => (int)PlanGapSeverity.High,
            "medium" => (int)PlanGapSeverity.Medium,
            _ => (int)PlanGapSeverity.Low,
        };
        return Math.Abs((int)tag - planted) switch
        {
            0 => "same",
            1 => "one off",
            _ => "two off",
        };
    }

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
        (score.Focuses, score.FocusesFromSummary) = FocusReader.Read(score.Log, stdout);

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
            if (facts["probeGaps"] is JsonArray ids)
            {
                // A probe run (2026-11-19): only its source's missed gaps, scored as ordinary ones; other gaps still judged against them all.
                score.Source = (string?)facts["source"] ?? "?";
                ScorePlan(score, Probe.Derive(plan, [.. ids.Select(i => (string)i!)]), stdout, plan.Gaps);
            }
            else
            {
                ScorePlan(score, plan, stdout);
            }
        }
        else
        {
            score.Flags.Add("the item is no longer in the material");
        }

        return score;
    }

    /// <param name="otherGapsAgainst">The gaps a line must name none of to be an "other gap" (2026-11-19): a probe's whole material item, where
    /// <paramref name="plan"/> holds only the gaps it probes; by default <paramref name="plan"/>'s own.</param>
    public static void ScorePlan(RunScore score, PlanItem plan, string stdout, IReadOnlyList<PlantedGap>? otherGapsAgainst = null)
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

        score.SecondSavesRefused = score.Calls.Count(c => c.Refused && Patterns.SecondSave.IsMatch(c.Result!));
        score.ShapeRefusals = score.Calls.Count(c => c.Refused && Patterns.ShapeGap.IsMatch(c.Result!));
        score.MeasuredLeft = score.Calls.Count(c => c.Accepted && c.List(PresentPlanTool.GapsArgument).Count > 0 && Patterns.MeasuredLeft.IsMatch(c.Result!));
        ScoreStepChanges(score, startBody, PlanDocument.TryParse(ReadText(Path.Combine(score.Folder, "start.md"))));

        // Accepted calls only (2026-11-17, one revision a pass): a refused call's gaps were never recorded, so they aren't caught or reported.
        var gapLines = score.Calls.Where(c => c.Accepted).SelectMany(c => c.List(PresentPlanTool.GapsArgument).Select(t => (c.Ordinal, Text: t))).ToList();
        var minorLines = score.Calls.Where(c => c.Accepted).SelectMany(c => c.List(PresentPlanTool.MinorArgument).Select(t => (c.Ordinal, Text: t))).ToList();
        score.Planted.AddRange(Outcomes(plan, gapLines, minorLines, finalBody));
        var bodies = score.Calls.Where(c => c.Accepted && c.Parsed)
            .Select(c => (c.Ordinal, Body: PlanDocument.SavedBody(c.Text(PresentPlanTool.TitleArgument) ?? "", c.Text(PresentPlanTool.MarkdownArgument, trim: false) ?? "")))
            .ToList();
        score.FollowOn.AddRange(FollowOnOutcomes(plan, bodies, gapLines, minorLines, finalBody));
        foreach (var line in gapLines.Where(l => !(otherGapsAgainst ?? plan.Gaps).Any(g => g.Match.In(l.Text))))
        {
            score.OtherGaps.Add(line);
        }

        if (plan.Decision is { } decision)
        {
            score.DecisionFolded = decision.In(finalBody);
        }

        if (plan.Reargue is { } reargue)
        {
            // A line that carries the decision itself reports folding it in ("closed the open question: 25 cents a day, cap 1000"),
            // not a re-argument: the baseline's two "re-argued" runs were both that (2026-11-17).
            score.Reargued.AddRange(gapLines.Concat(minorLines)
                .Where(l => reargue.In(l.Text) && plan.Decision?.In(l.Text) != true)
                .Select(l => l.Text));
        }

        score.Loss = PlanLoss.Compare(startBody, finalBody);
        foreach (var (call, pass) in CallPasses(stdout))
        {
            score.CallPasses[call] = pass;
        }

        if (score.Log.PlanShadowWouldConvergeAt is int at)
        {
            score.GapsAfterShadow.AddRange(score.Planted.Where(g => g.Status == "caught" && score.PassOf(g.Call) > at).Select(g => g.Gap.Id));
            score.GapsAfterShadow.AddRange(score.FollowOn.Where(f => f.Status == FollowOnStatus.Caught && score.PassOf(f.Named) > at).Select(f => f.Gap.Id));
        }

        if (score.StopReason is { } reason && score.Stop != "not sent" && score.Log.LastPlanShadowLine is { } shadow && !shadow.TrimEnd().EndsWith(PlanText.StopWord(reason), StringComparison.Ordinal))
        {
            score.Flags.Add($"stop mismatch: the notice says {score.Stop}, the last plan shadow line doesn't end in \"{PlanText.StopWord(reason)}\"");
        }
    }

    /// <summary>
    /// The minor check measured (2026-11-18, phase 3 item 2): each accepted clean report's body against the body in force before it (the last
    /// accepted call's, else the start plan's), steps paired as the app pairs them (<see cref="PlanStepChanges"/>), and its <c>verify</c>/<c>final</c>
    /// against the last in force, with <c>present_plan</c>'s rules: one left out keeps the file's, an empty one means none.
    /// </summary>
    private static void ScoreStepChanges(RunScore score, string startBody, PlanHeader? start)
    {
        string body = startBody;
        string? verify = start?.Verify, final = start?.Final;
        foreach (var c in score.Calls.Where(c => c.Accepted && c.Parsed))
        {
            string saved = PlanDocument.SavedBody(c.Text(PresentPlanTool.TitleArgument) ?? "", c.Text(PresentPlanTool.MarkdownArgument, trim: false) ?? "");
            string? sentVerify = c.Text(PresentPlanTool.VerifyArgument), sentFinal = c.Text(PresentPlanTool.FinalArgument);
            if (c.List(PresentPlanTool.GapsArgument).Count == 0)
            {
                score.CleanReports++;
                var report = PlanStepChanges.Compare(body, saved);
                bool checks = CheckChanges(sentVerify, verify) || CheckChanges(sentFinal, final);
                if (report.Any || checks)
                {
                    score.StepChanges.Add((c.Ordinal, report.Added.Count, report.Removed.Count, report.Moved.Count, report.DoneWhenChanged.Count, checks));
                }
            }

            body = saved;
            verify = sentVerify is null ? verify : sentVerify.Length == 0 ? null : sentVerify;
            final = sentFinal is null ? final : sentFinal.Length == 0 ? null : sentFinal;
        }
    }

    /// <summary>Whether a check sent (null: left out) differs from the one in force (null: none): <c>PresentPlanTool.Changes</c>'s rule.</summary>
    private static bool CheckChanges(string? sent, string? held) =>
        sent is not null && !string.Equals(sent.Length == 0 ? null : sent, held, StringComparison.Ordinal);

    /// <summary>
    /// Each present_plan call's pass (2026-11-17): a pass's notice ("Gap review pass N of M: …") comes after its calls, so a call belongs to the
    /// first notice after it, and the calls after the last notice to the pass after that one (the pass the stop notice ends).
    /// </summary>
    public static IEnumerable<(int Call, int Pass)> CallPasses(string stdout)
    {
        const string call = "[tool] " + PresentPlanTool.ToolName + " ";
        const string result = "[tool] " + PresentPlanTool.ToolName + " -> ";
        var waiting = new List<int>();
        int ordinal = 0;
        int lastPass = 0;
        foreach (string raw in stdout.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.StartsWith(call, StringComparison.Ordinal) && !line.StartsWith(result, StringComparison.Ordinal))
            {
                waiting.Add(++ordinal);
            }
            else if (Patterns.PlanPassNotices.Select(p => p.Match(line)).FirstOrDefault(m => m.Success) is { } notice)
            {
                lastPass = int.Parse(notice.Groups["n"].Value.Replace(",", "", StringComparison.Ordinal), CultureInfo.InvariantCulture);
                foreach (int c in waiting)
                {
                    yield return (c, lastPass);
                }

                waiting.Clear();
            }
        }

        foreach (int c in waiting)
        {
            yield return (c, lastPass + 1);
        }
    }

    /// <summary>
    /// The follow-on gaps' outcomes (2026-11-18). The introducing call is the first accepted call with a body line on the trigger's path; then
    /// the first status that applies: <b>foreseen</b> (a gap or minor line of that call or an earlier one names it, or that body already
    /// holds its fix), <b>caught</b> (a later gap line names it), <b>minor only</b>, <b>fixed silently</b> (the final body holds the fix),
    /// <b>dropped</b> (the final body left the path) and <b>missed</b> (still on it, unfixed, never named).
    /// </summary>
    public static IEnumerable<FollowOnOutcome> FollowOnOutcomes(PlanItem plan, List<(int Ordinal, string Body)> bodies,
        List<(int Ordinal, string Text)> gapLines, List<(int Ordinal, string Text)> minorLines, string finalBody)
    {
        foreach (var gap in plan.Gaps.Where(g => g.FollowOn))
        {
            var trigger = gap.Trigger!;
            if (bodies.FirstOrDefault(b => trigger.LineIn(b.Body) is not null) is not { Body: not null } introducing)
            {
                yield return new FollowOnOutcome(gap, FollowOnStatus.NotIntroduced, null, null, null);
                continue;
            }

            int at = introducing.Ordinal;
            var named = gapLines.Concat(minorLines).Where(l => gap.Match.In(l.Text)).OrderBy(l => l.Ordinal).ToList();
            if (named.FirstOrDefault(l => l.Ordinal <= at) is { Text: not null } early)
            {
                yield return new FollowOnOutcome(gap, FollowOnStatus.Foreseen, early.Text, at, early.Ordinal);
            }
            else if (gap.Fixed?.LineIn(introducing.Body) is { } handled)
            {
                yield return new FollowOnOutcome(gap, FollowOnStatus.Foreseen, handled, at, null);
            }
            else if (gapLines.FirstOrDefault(l => l.Ordinal > at && gap.Match.In(l.Text)) is { Text: not null } caught)
            {
                yield return new FollowOnOutcome(gap, FollowOnStatus.Caught, caught.Text, at, caught.Ordinal);
            }
            else if (minorLines.FirstOrDefault(l => l.Ordinal > at && gap.Match.In(l.Text)) is { Text: not null } minor)
            {
                yield return new FollowOnOutcome(gap, FollowOnStatus.MinorOnly, minor.Text, at, minor.Ordinal);
            }
            else if (gap.Fixed?.LineIn(finalBody) is { } fix)
            {
                yield return new FollowOnOutcome(gap, FollowOnStatus.FixedSilently, fix, at, null);
            }
            else if (trigger.LineIn(finalBody) is null)
            {
                yield return new FollowOnOutcome(gap, FollowOnStatus.Dropped, null, at, null);
            }
            else
            {
                yield return new FollowOnOutcome(gap, FollowOnStatus.Missed, null, at, null);
            }
        }
    }

    public static IEnumerable<GapOutcome> Outcomes(PlanItem plan, List<(int Ordinal, string Text)> gapLines, List<(int Ordinal, string Text)> minorLines, string finalBody)
    {
        foreach (var gap in plan.Gaps.Where(g => !g.FollowOn))
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
        score.FindingsTotal = live.Count;
        score.FindingsLow = live.Count(f => f.Severity == AuditSeverity.Low);
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

        if (o.From is { } fromFolders)
        {
            if (o.Only is not null)
            {
                Console.Error.WriteLine("--from picks its own runs; leave out --only.");
                return 2;
            }

            if (fromFolders.FirstOrDefault(f => !Directory.Exists(f)) is { } missing)
            {
                Console.Error.WriteLine($"--from {missing}: no such folder.");
                return 2;
            }
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
            File.Delete(Path.Combine(outDir, "bench.json"));
        }

        if (BenchSetsDiffer(outDir, o) is { } recorded)
        {
            Console.Error.WriteLine($"{outDir} was run with --set {recorded}; resume with the same --set pairs, or pass --fresh to start over.");
            return 2;
        }

        if (Probe.FromDiffers(outDir, o.From) is { } recordedFrom)
        {
            Console.Error.WriteLine($"{outDir} was run with --from {recordedFrom}; resume with the same --from, or pass --fresh to start over.");
            return 2;
        }

        List<ProbeSource>? sources = null;
        if (o.From is { } from)
        {
            // A resume runs the recorded sources: a keyword change since could change the pick and shift every run's number.
            var skipped = new List<string>();
            sources = Probe.Recorded(outDir) ?? Probe.Pick(from, material, skipped);
            skipped.ForEach(s => Console.WriteLine("  skipped " + s));
            if (sources.Count == 0)
            {
                Console.Error.WriteLine("No run in --from left a gap missed: nothing to probe.");
                return 1;
            }
        }

        WriteBenchFile(outDir, sources is null ? o.Runs : sources.Count * o.Runs, o.Sets, o.From, sources);
        string hash = Material.Hash(Repo.MaterialRoot);

        // The runs to make (2026-11-19): every item × --runs, n outer; with --from, the sources in order --runs times over, numbered on.
        var jobs = new List<(string Item, int N, int Of, ProbeSource? Source)>();
        if (sources is null)
        {
            for (int n = 1; n <= o.Runs; n++)
            {
                jobs.AddRange(items.Select(item => (item, n, o.Runs, (ProbeSource?)null)));
            }
        }
        else
        {
            int total = sources.Count * o.Runs;
            for (int k = 1; k <= total; k++)
            {
                var source = sources[(k - 1) % sources.Count];
                jobs.Add((source.Item, k, total, source));
            }
        }

        var itemHashes = jobs.Select(j => j.Item).Distinct().ToDictionary(i => i, i => Material.ItemHash(Repo.MaterialRoot, i), StringComparer.Ordinal);
        string repoHash = Material.RepoHash(Repo.MaterialRoot);
        Console.WriteLine(sources is null
            ? $"GapBench: {items.Count} items × {o.Runs} runs → {outDir}"
            : $"GapBench: a fresh review of {sources.Count} final plans × {o.Runs} → {outDir}");
        Console.WriteLine($"  {version} · {baseUrl} · settings {settings.ToJsonString()}");

        using var interrupt = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            interrupt.Cancel();
        };

        int exit = 0;
        bool first = true;
        foreach (var (item, n, of, source) in jobs)
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
            var facts = source is null
                ? await RunOneAsync(o, material, item, n, folder, exe, baseUrl, settings, version, hash, itemHashes[item], interrupt.Token)
                : await RunOneAsync(o, material, item, n, folder, exe, baseUrl, settings, version, hash, repoHash, interrupt.Token,
                    Probe.StripReview(File.ReadAllText(Probe.FinalPlan(source.Source, item))),
                    new JsonObject { ["source"] = source.Source, ["probeGaps"] = new JsonArray([.. source.Gaps.Select(g => (JsonNode?)g)]) });
            if (facts is null)
            {
                Console.Error.WriteLine("Interrupted: the run in progress has no done.json, so a resume runs it again.");
                exit = 1;
                break;
            }

            var score = Scorer.Score(folder, material);
            Console.WriteLine($"  {item} {n}/{of}" + (source is null ? "" : $" ({source.Source}: {string.Join(", ", source.Gaps)})")
                + $": {score.Stop}, {score.Passes} passes, {score.Minutes.ToString("0.0", CultureInfo.InvariantCulture)} min"
                + (score.Flags.Count > 0 ? " (" + string.Join("; ", score.Flags) + ")" : ""));
        }

        Report.Write(outDir, material);
        Console.WriteLine($"Results: {Path.Combine(outDir, "results.md")}");
        return exit;
    }

    private static async Task<JsonObject?> RunOneAsync(Options o, Material m, string item, int n, string folder, string exe, string url, JsonObject settings, string version, string hash, string itemHash, CancellationToken interrupt,
        string? planText = null, JsonObject? extraFacts = null)
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
            // A probe (2026-11-19) starts from its source's final plan; it is its start.md too, so the run keeps what the model saw.
            if (planText is not null)
            {
                File.WriteAllText(Path.Combine(plans, plan.Name + ".md"), planText);
                File.WriteAllText(Path.Combine(folder, "start.md"), planText);
            }
            else
            {
                File.Copy(plan.PlanPath, Path.Combine(plans, plan.Name + ".md"));
                File.Copy(plan.PlanPath, Path.Combine(folder, "start.md"));
            }
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
            ["itemHash"] = itemHash,
        };
        foreach (var (key, value) in extraFacts ?? [])
        {
            facts[key] = value?.DeepClone();
        }

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
        // Last, so a --set wins over both the copied settings and the bench's own keys (2026-11-17).
        foreach (var (name, value) in o.Sets)
        {
            settings[name] = value?.DeepClone();
        }

        return settings;
    }

    /// <summary>The --set pairs as bench.json records them: an object, name to value.</summary>
    public static JsonObject SetsJson(IEnumerable<(string Name, JsonNode? Value)> sets) =>
        new(sets.Select(s => KeyValuePair.Create(s.Name, s.Value?.DeepClone())));

    /// <summary>
    /// The pairs <paramref name="outDir"/>'s bench.json recorded, spelled for the error, when they aren't <paramref name="o"/>'s (2026-11-17): a
    /// resume that forgot a --set would mix runs with and without it. Null when they match, the folder is new, or --fresh starts it over.
    /// </summary>
    public static string? BenchSetsDiffer(string outDir, Options o)
    {
        string path = Path.Combine(outDir, "bench.json");
        if (o.Fresh || !File.Exists(path))
        {
            return null;
        }

        var recorded = JsonNode.Parse(File.ReadAllText(path))?["sets"] as JsonObject ?? new JsonObject();
        var now = SetsJson(o.Sets);
        bool same = recorded.Count == now.Count && recorded.All(kv => now.ContainsKey(kv.Key) && JsonNode.DeepEquals(kv.Value, now[kv.Key]));
        return same ? null : recorded.Count == 0 ? "(none)" : string.Join(" ", recorded.Select(kv => $"{kv.Key}={kv.Value?.ToJsonString() ?? "null"}"));
    }

    private static void WriteBenchFile(string outDir, int runs, IEnumerable<(string Name, JsonNode? Value)> sets,
        IReadOnlyList<string>? from = null, IReadOnlyList<ProbeSource>? sources = null)
    {
        string path = Path.Combine(outDir, "bench.json");
        int planned = runs;
        if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path))?["runs"] is { } before)
        {
            planned = Math.Max(planned, (int)before);
        }

        var bench = new JsonObject { ["runs"] = planned };
        var setsJson = SetsJson(sets);
        if (setsJson.Count > 0)
        {
            bench["sets"] = setsJson;
        }

        // A probe bench (2026-11-19) keeps its --from list and the sources it picked, for the resume.
        if (from is not null && sources is not null)
        {
            bench["from"] = new JsonArray([.. from.Select(f => (JsonNode?)Probe.Normal(f))]);
            bench["sources"] = Probe.ToJson(sources);
        }

        File.WriteAllText(path, bench.ToJsonString());
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
        if (runs.Count == 0)
        {
            // A bench stopped before its first run finished (Ctrl+C, a server gone): the exe, model and settings come from finished
            // runs, so there is nothing to show but that (2026-11-17, the Ctrl+C hand check printed empty lines and a zero headline).
            md.AppendLine("No finished runs yet. Run the same command again to resume.").AppendLine();
        }
        else
        {
            AppendMeta(md, runs, currentHash);
            AppendHeadline(md, runs, material);
        }

        var json = new JsonObject
        {
            ["folder"] = outDir,
            ["plannedRuns"] = planned,
            ["materialHash"] = currentHash,
            ["headline"] = Headline(runs, material),
            ["runs"] = new JsonArray([.. runs.Select(r =>
            {
                var run = RunJson(r);
                if (r.IsProbe)
                {
                    run["source"] = r.Source;
                    run["probeGaps"] = new JsonArray([.. r.Planted.Select(g => (JsonNode?)g.Gap.Id)]);
                }

                return (JsonNode?)run;
            })]),
        };

        foreach (var group in runs.GroupBy(r => r.Item))
        {
            AppendItem(md, group.Key, [.. group], planned, material);
        }

        File.WriteAllText(Path.Combine(outDir, "results.md"), md.ToString());
        File.WriteAllText(Path.Combine(outDir, "results.json"), json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>The current hash a run's key is judged by (2026-11-19): a probe's (<see cref="Probe.ItemPrefix"/>) <c>repo/</c> alone, else its item's.</summary>
    internal static string HashFor(string key) => key.StartsWith(Probe.ItemPrefix, StringComparison.Ordinal)
        ? Material.RepoHash(Repo.MaterialRoot)
        : Material.ItemHash(Repo.MaterialRoot, key);

    /// <summary>The plan gap table's rows (2026-11-19): a probe bench's are the gaps its runs probed, follow-on ones too; else the planted ones.</summary>
    internal static IEnumerable<PlantedGap> TableGaps(PlanItem plan, List<RunScore> runs) => runs.Any(r => r.IsProbe)
        ? plan.Gaps.Where(g => runs.Any(r => r.Planted.Any(p => p.Gap.Id == g.Id)))
        : plan.Gaps.Where(g => !g.FollowOn);

    private static void AppendMeta(StringBuilder md, List<RunScore> runs, string currentHash)
    {
        string Values(Func<RunScore, string?> pick) => string.Join(" / ", runs.Select(pick).Select(v => v ?? "—").Distinct());
        md.AppendLine($"- Exe: {Values(r => (string?)r.Facts["version"])}");
        md.AppendLine($"- Model: {Values(r => r.Log.ModelId)} at {Values(r => (string?)r.Facts["url"])}");
        md.AppendLine($"- Settings: {Values(r => r.Facts["settings"]?.ToJsonString())}");
        string hashes = Values(r => ((string?)r.Facts["materialHash"])?[..12]);
        var itemHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        var changed = Material.ChangedItems(
            runs.Select(r => (r.IsProbe ? Probe.ItemPrefix + r.Item : r.Item, (string?)r.Facts["materialHash"], (string?)r.Facts["itemHash"])),
            currentHash,
            item => itemHashes.TryGetValue(item, out string? h) ? h : itemHashes[item] = HashFor(item));
        md.AppendLine($"- Material: {hashes}" + (changed.Count > 0
            ? $" — **changed since for {string.Join(", ", changed)}**: a moved bug line or a changed plan mis-scores those runs"
            : ""));
        md.AppendLine();
    }

    private static JsonObject Headline(List<RunScore> runs, Material material)
    {
        var plans = runs.Where(r => !r.IsAudit).ToList();
        var audits = runs.Where(r => r.IsAudit).ToList();
        var h = new JsonObject
        {
            ["plannedGaps"] = plans.Sum(r => r.Planted.Count),
            ["gapsCaught"] = plans.Sum(r => r.Planted.Count(g => g.Status == "caught")),
            ["gapsMinorOnly"] = plans.Sum(r => r.Planted.Count(g => g.Status == "minor only")),
            ["gapsFixedSilently"] = plans.Sum(r => r.Planted.Count(g => g.Status == "fixed silently")),
            // The follow-on gaps (2026-11-18), all 0 for a bench without them.
            ["followOnIntroduced"] = plans.Sum(r => r.FollowOn.Count(f => f.Status != FollowOnStatus.NotIntroduced)),
            ["followOnForeseen"] = plans.Sum(r => r.FollowOn.Count(f => f.Status == FollowOnStatus.Foreseen)),
            ["followOnCaughtLater"] = plans.Sum(r => r.FollowOn.Count(f => f.Status == FollowOnStatus.Caught)),
            ["followOnCaughtConfirming"] = plans.Sum(r => r.FollowOn.Count(f => f.Status == FollowOnStatus.Caught && r.IsConfirming(r.PassOf(f.Named)))),
            ["followOnMinorOnly"] = plans.Sum(r => r.FollowOn.Count(f => f.Status == FollowOnStatus.MinorOnly)),
            ["followOnFixedSilently"] = plans.Sum(r => r.FollowOn.Count(f => f.Status == FollowOnStatus.FixedSilently)),
            ["followOnDropped"] = plans.Sum(r => r.FollowOn.Count(f => f.Status == FollowOnStatus.Dropped)),
            ["followOnMissed"] = plans.Sum(r => r.FollowOn.Count(f => f.Status == FollowOnStatus.Missed)),
            ["plantedBugs"] = audits.Sum(r => r.Bugs.Count),
            ["bugsCaught"] = audits.Sum(r => r.Bugs.Count(b => b.Status == "caught")),
            ["bugsFileOnly"] = audits.Sum(r => r.Bugs.Count(b => b.Status == "file only")),
            ["bugsNitOnly"] = audits.Sum(r => r.Bugs.Count(b => b.Status == "nit only")),
            ["controlRuns"] = plans.Count(r => material.Plan(r.Item)?.Control == true),
            ["controlGaps"] = plans.Where(r => material.Plan(r.Item)?.Control == true).Sum(r => r.OtherGaps.Count),
            ["planRuns"] = plans.Count,
            ["runsLostContent"] = plans.Count(r => r.LostContent),
            ["cleanReports"] = plans.Sum(r => r.CleanReports),
            ["cleanStepChanges"] = plans.Sum(r => r.CleanStepChanges),
            ["cleanStepsAdded"] = plans.Sum(r => r.StepChanges.Sum(s => s.Added)),
            ["cleanStepsRemoved"] = plans.Sum(r => r.StepChanges.Sum(s => s.Removed)),
            ["cleanStepsMoved"] = plans.Sum(r => r.StepChanges.Sum(s => s.Moved)),
            ["cleanDoneWhen"] = plans.Sum(r => r.StepChanges.Sum(s => s.DoneWhen)),
            ["cleanChecks"] = plans.Sum(r => r.StepChanges.Count(s => s.Checks)),
            ["convergedWithHighOpen"] = runs.Count(r => r.ConvergedWithHighOpen),
            ["auditRuns"] = audits.Count,
            ["shadowStoppedEarly"] = audits.Count(r => r.ShadowStoppedEarly),
            ["planShadowRuns"] = plans.Count(r => r.Log.PlanShadowLines > 0),
            ["planShadowStoppedEarly"] = plans.Count(r => r.PlanShadowStoppedEarly),
            ["labelsSame"] = plans.Sum(r => r.Planted.Count(g => RunScore.LabelOf(g) == "same")),
            ["labelsOneOff"] = plans.Sum(r => r.Planted.Count(g => RunScore.LabelOf(g) == "one off")),
            ["labelsTwoOff"] = plans.Sum(r => r.Planted.Count(g => RunScore.LabelOf(g) == "two off")),
            ["labelsUntagged"] = plans.Sum(r => r.Planted.Count(g => RunScore.LabelOf(g) == "untagged")),
            ["auditLowChanges"] = audits.Sum(r => r.Log.AuditLight),
            ["findings"] = audits.Sum(r => r.FindingsTotal),
            ["findingsLow"] = audits.Sum(r => r.FindingsLow),
            ["flaggedRuns"] = runs.Count(r => r.Flags.Count > 0),
        };

        // A probe bench (2026-11-19): its gaps by when the fresh review named them. Left out otherwise, so older results don't change.
        var probes = plans.Where(r => r.IsProbe).ToList();
        if (probes.Count > 0)
        {
            int Count(Func<RunScore, GapOutcome, bool> pick) => probes.Sum(r => r.Planted.Count(g => pick(r, g)));
            h["probeGaps"] = Count((_, _) => true);
            h["probeCaughtPass1"] = Count((r, g) => g.Status == "caught" && r.PassOf(g.Call) == 1);
            h["probeCaughtLater"] = Count((r, g) => g.Status == "caught" && r.PassOf(g.Call) != 1);
            h["probeMinorOnly"] = Count((_, g) => g.Status == "minor only");
            h["probeFixedSilently"] = Count((_, g) => g.Status == "fixed silently");
            h["probeMissed"] = Count((_, g) => g.Status == "missed");
        }

        return h;
    }

    private static void AppendHeadline(StringBuilder md, List<RunScore> runs, Material material)
    {
        var h = Headline(runs, material);
        int I(string k) => (int)h[k]!;
        md.AppendLine("## Headline").AppendLine();
        // A bench of plans only or the audit only (2026-11-17, --only) leaves out the other kind's lines rather than print "0 of 0".
        bool plans = I("planRuns") > 0, audits = I("auditRuns") > 0;
        if (plans)
        {
            md.AppendLine($"- Planted gaps caught: **{I("gapsCaught")} of {I("plannedGaps")}** ({I("gapsMinorOnly")} only as minor fixes, {I("gapsFixedSilently")} fixed without being reported)");
            if (h.ContainsKey("probeGaps"))
            {
                md.AppendLine($"- Fresh look on **{I("probeGaps")} gaps a review left open**: **{I("probeCaughtPass1")} caught in pass 1**, {I("probeCaughtLater")} later, "
                    + $"{I("probeMinorOnly")} only as minor fixes, {I("probeFixedSilently")} fixed silently, {I("probeMissed")} missed");
            }

            // Follow-on gaps (2026-11-18): no "of M", since a run's paths exclude each other (withdraw-books' F1–F3 or F4).
            if (runs.Any(r => r.FollowOn.Count > 0))
            {
                md.AppendLine($"- Follow-on gaps (a revision creates them): **{I("followOnIntroduced")} introduced**; {I("followOnForeseen")} foreseen, "
                    + $"**{I("followOnCaughtLater")} caught later** ({I("followOnCaughtConfirming")} in a confirming pass), {I("followOnMinorOnly")} only as minor fixes, "
                    + $"{I("followOnFixedSilently")} fixed silently, {I("followOnDropped")} dropped, **{I("followOnMissed")} missed**");
            }
        }

        if (audits)
        {
            md.AppendLine($"- Planted bugs caught: **{I("bugsCaught")} of {I("plantedBugs")}** ({I("bugsFileOnly")} more named only by file, {I("bugsNitOnly")} only as nits)");
        }

        if (I("controlRuns") > 0)
        {
            md.AppendLine($"- Gaps reported on the control plan: **{I("controlGaps")}** over {I("controlRuns")} runs");
        }

        if (plans)
        {
            md.AppendLine($"- Runs that lost content: **{I("runsLostContent")} of {I("planRuns")}**");
            md.AppendLine($"- Clean reports that changed steps: **{I("cleanStepChanges")} of {I("cleanReports")}** ({I("cleanStepsAdded")} added, {I("cleanStepsRemoved")} removed, "
                + $"{I("cleanStepsMoved")} moved, {I("cleanDoneWhen")} Done when lines, {I("cleanChecks")} verify or final)");
        }

        md.AppendLine($"- Runs that converged with a high gap open or a high bug missed: **{I("convergedWithHighOpen")}**");
        if (audits)
        {
            md.AppendLine($"- Audit runs where the shadow rule would have stopped before a planted bug was found: **{I("shadowStoppedEarly")} of {I("auditRuns")}**");
        }

        if (I("planShadowRuns") > 0)
        {
            md.AppendLine($"- Plan runs where the shadow rule would have stopped before a planted gap was caught: **{I("planShadowStoppedEarly")} of {I("planShadowRuns")}**");
        }

        int labeled = I("labelsSame") + I("labelsOneOff") + I("labelsTwoOff") + I("labelsUntagged");
        if (labeled > 0 && I("labelsUntagged") < labeled)
        {
            md.AppendLine($"- Caught gaps' severity tags against the planted: **{I("labelsSame")} same**, {I("labelsOneOff")} one off, {I("labelsTwoOff")} two off, {I("labelsUntagged")} untagged");
        }

        if (I("auditRuns") > 0)
        {
            md.AppendLine($"- Audit findings labeled low: **{I("findingsLow")} of {I("findings")}**; low changes that kept a streak: {I("auditLowChanges")}");
        }
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
            Row(md, "findings labeled low", runs.Select(r => (double)r.FindingsLow));
            Row(md, "low changes kept a streak", runs.Select(r => (double)r.Log.AuditLight));
        }
        else
        {
            Row(md, "planted gaps caught", runs.Select(r => (double)r.Planted.Count(g => g.Status == "caught")));
            if (runs.Any(r => r.FollowOn.Count > 0))
            {
                Row(md, "follow-on gaps caught later", runs.Select(r => (double)r.FollowOn.Count(f => f.Status == FollowOnStatus.Caught)));
            }

            Row(md, material.Plan(item)?.Control == true ? "gaps reported (all false)" : "other gaps reported", runs.Select(r => (double)r.OtherGaps.Count));
            Row(md, "second saves refused", runs.Select(r => (double)r.SecondSavesRefused));
            Row(md, "shape refusals", runs.Select(r => (double)r.ShapeRefusals));
            Row(md, "saved with a measured problem", runs.Select(r => (double)r.MeasuredLeft));
            Row(md, "clean reports that changed steps", runs.Select(r => (double)r.CleanStepChanges));
        }

        md.AppendLine();
        md.AppendLine("Stops: " + string.Join(", ", runs.GroupBy(r => r.Stop).Select(g => $"{g.Key} ×{g.Count()}"))).AppendLine();
        if (runs.SelectMany(r => r.Focuses).ToList() is { Count: > 0 } focuses)
        {
            md.AppendLine("Confirming focuses: " + FocusReader.Counts(focuses) + (runs.Any(r => r.FocusesFromSummary) ? " (read from the review summary where a run has no focus lines)" : "")).AppendLine();
        }

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
            md.AppendLine("| gap | severity | caught | minor only | fixed silently | missed | tag agrees | first pass |").AppendLine("|---|---|---|---|---|---|---|---|");
            bool probe = runs.Any(r => r.IsProbe);
            foreach (var gap in TableGaps(plan, runs))
            {
                var outcomes = runs.Select(r => (Run: r, Outcome: r.Planted.FirstOrDefault(g => g.Gap.Id == gap.Id))).Where(p => p.Outcome is not null).ToList();
                var caught = outcomes.Where(p => p.Outcome!.Status == "caught").ToList();
                md.AppendLine($"| {gap.Id} {gap.What} | {gap.Severity} | {caught.Count} | {outcomes.Count(p => p.Outcome!.Status == "minor only")} | "
                    + $"{outcomes.Count(p => p.Outcome!.Status == "fixed silently")} | {outcomes.Count(p => p.Outcome!.Status == "missed")} | "
                    + $"{caught.Count(p => RunScore.LabelOf(p.Outcome!) == "same")} same, {caught.Count(p => RunScore.LabelOf(p.Outcome!) == "one off")} one off, "
                    + $"{caught.Count(p => RunScore.LabelOf(p.Outcome!) == "untagged")} untagged | "
                    + string.Join(", ", caught.Select(p => p.Run.PassOf(p.Outcome!.Call)?.ToString(CultureInfo.InvariantCulture) ?? "?")) + " |");
            }

            md.AppendLine();
            if (!probe)
            {
                AppendFollowOnTable(md, plan, runs);
            }
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

    /// <summary>
    /// The follow-on gaps' table (2026-11-18): each gap's statuses over the runs, then the passes whose gap lines caught it later, each with
    /// its focus when it was a confirming pass ("4 (verification)", "3" for a pass after a gap pass). Nothing for an item without them.
    /// </summary>
    private static void AppendFollowOnTable(StringBuilder md, PlanItem plan, List<RunScore> runs)
    {
        var gaps = plan.Gaps.Where(g => g.FollowOn).ToList();
        if (gaps.Count == 0)
        {
            return;
        }

        md.AppendLine("| follow-on gap | severity | introduced | foreseen | caught later | minor later | fixed silently | dropped | missed | caught in pass |")
            .AppendLine("|---|---|---|---|---|---|---|---|---|---|");
        foreach (var gap in gaps)
        {
            var outcomes = runs.Select(r => (Run: r, Outcome: r.FollowOn.FirstOrDefault(f => f.Gap.Id == gap.Id))).Where(p => p.Outcome is not null).ToList();
            int Count(string status) => outcomes.Count(p => p.Outcome!.Status == status);
            var caught = outcomes.Where(p => p.Outcome!.Status == FollowOnStatus.Caught).Select(p =>
            {
                int? pass = p.Run.PassOf(p.Outcome!.Named);
                return (pass?.ToString(CultureInfo.InvariantCulture) ?? "?") + (p.Run.FocusOf(pass) is { } focus ? $" ({focus})" : "");
            });
            md.AppendLine($"| {gap.Id} {gap.What} | {gap.Severity} | {outcomes.Count - Count(FollowOnStatus.NotIntroduced)} of {outcomes.Count} | {Count(FollowOnStatus.Foreseen)} | "
                + $"{Count(FollowOnStatus.Caught)} | {Count(FollowOnStatus.MinorOnly)} | {Count(FollowOnStatus.FixedSilently)} | {Count(FollowOnStatus.Dropped)} | "
                + $"{Count(FollowOnStatus.Missed)} | {string.Join(", ", caught)} |");
        }

        md.AppendLine();
    }

    private static void AppendRun(StringBuilder md, RunScore r)
    {
        md.AppendLine($"### {r.Item} run {r.Run}").AppendLine();
        md.AppendLine($"- `{r.Folder}`");
        if (r.Source is { } source)
        {
            md.AppendLine($"- A fresh review of `{source}`'s final plan, for {string.Join(", ", r.Planted.Select(g => g.Gap.Id))}");
        }

        md.AppendLine($"- {r.Stop}; {r.Passes} passes, {r.Log.Retries} retries, {r.Log.FollowUps} follow-ups; {Math.Round(r.Log.Tokens / 1000.0, 1).ToString(CultureInfo.InvariantCulture)}k tokens over {r.Log.Requests} requests; {r.Minutes.ToString("0.0", CultureInfo.InvariantCulture)} min");
        foreach (string flag in r.Flags)
        {
            md.AppendLine($"- ⚠ {flag}");
        }

        foreach (var g in r.Planted)
        {
            md.AppendLine($"- {g.Gap.Id} ({g.Gap.Severity}): **{g.Status}**" + (g.Line is { } line ? $"{(g.Call is { } c ? $" in call {c}" : "")}{(r.PassOf(g.Call) is { } gp ? $", pass {gp}" : "")}: «{Flat(line)}»" : "")
                + (g.Status != "fixed silently" && g.FixedInBody ? " · fixed in the final plan" : ""));
        }

        foreach (var f in r.FollowOn)
        {
            md.AppendLine($"- {f.Gap.Id} ({f.Gap.Severity}, follow-on): **{f.Status}**"
                + (f.Introduced is { } at ? $"; path taken in call {at}{(r.PassOf(at) is { } ap ? $", pass {ap}" : "")}" : "")
                + (f.Named is { } n ? $"; named in call {n}{(r.PassOf(n) is { } np ? $", pass {np}" : "")}{(r.FocusOf(r.PassOf(n)) is { } focus ? $" ({focus})" : "")}" : "")
                + (f.Line is { } line ? $": «{Flat(line)}»" : ""));
        }

        if (r.Focuses.Count > 0)
        {
            md.AppendLine("- Confirming focuses: " + string.Join(", ", r.Focuses.Select(f => $"pass {f.Pass} {f.Label}" + (f.How.Length > 0 ? $" ({f.How})" : "")))
                + (r.FocusesFromSummary ? " (from the review summary)" : ""));
        }

        if (!r.IsAudit && r.Log.PlanShadowLines > 0)
        {
            md.AppendLine($"- Plan shadow rule would converge at pass {r.Log.PlanShadowWouldConvergeAt?.ToString(CultureInfo.InvariantCulture) ?? "—"}"
                + (r.GapsAfterShadow.Count > 0 ? $"; planted gaps caught after it: **{string.Join(", ", r.GapsAfterShadow)}**" : ""));
        }

        foreach (var s in r.StepChanges)
        {
            md.AppendLine($"- Clean report in call {s.Call}{(r.PassOf(s.Call) is { } sp ? $", pass {sp}" : "")} changed steps: {s.Added} added, {s.Removed} removed, {s.Moved} moved, "
                + $"{s.DoneWhen} Done when lines" + (s.Checks ? ", verify or final" : ""));
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
                + (r.BugsAfterShadow.Count > 0 ? $"; planted bugs found after it: **{string.Join(", ", r.BugsAfterShadow)}**" : "")
                + $"; findings labeled low: {r.FindingsLow} of {r.FindingsTotal}"
                + (r.Log.AuditLight > 0 ? $"; low changes kept the streak: {r.Log.AuditLight} over {r.Log.AuditLightPasses} passes" : ""));
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

    internal static JsonObject RunJson(RunScore r) => new()
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
        ["planted"] = new JsonArray([.. r.Planted.Select(g => (JsonNode?)new JsonObject { ["id"] = g.Gap.Id, ["status"] = g.Status, ["line"] = g.Line, ["call"] = g.Call, ["pass"] = r.PassOf(g.Call), ["fixedInBody"] = g.FixedInBody })]),
        ["followOn"] = new JsonArray([.. r.FollowOn.Select(f => (JsonNode?)new JsonObject
        {
            ["id"] = f.Gap.Id, ["status"] = f.Status, ["line"] = f.Line, ["introducedCall"] = f.Introduced, ["introducedPass"] = r.PassOf(f.Introduced),
            ["namedCall"] = f.Named, ["namedPass"] = r.PassOf(f.Named), ["namedFocus"] = r.FocusOf(r.PassOf(f.Named)),
        })]),
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
        ["findings"] = r.FindingsTotal,
        ["findingsLow"] = r.FindingsLow,
        ["auditLowChanges"] = r.Log.AuditLight,
        ["planShadowWouldConvergeAt"] = r.Log.PlanShadowWouldConvergeAt,
        ["focuses"] = new JsonArray([.. r.Focuses.Select(f => (JsonNode?)new JsonObject { ["pass"] = f.Pass, ["label"] = f.Label, ["how"] = f.How.Length > 0 ? f.How : null })]),
        ["focusesFromSummary"] = r.FocusesFromSummary,
        ["gapsAfterShadow"] = new JsonArray([.. r.GapsAfterShadow.Select(g => (JsonNode?)g)]),
        ["secondSavesRefused"] = r.SecondSavesRefused,
        ["shapeRefusals"] = r.ShapeRefusals,
        ["measuredLeft"] = r.MeasuredLeft,
        ["cleanReports"] = r.CleanReports,
        ["cleanStepChanges"] = new JsonArray([.. r.StepChanges.Select(s => (JsonNode?)new JsonObject
        {
            ["call"] = s.Call, ["added"] = s.Added, ["removed"] = s.Removed, ["moved"] = s.Moved, ["doneWhen"] = s.DoneWhen, ["checks"] = s.Checks,
        })]),
        ["gapLabels"] = new JsonObject(r.Planted.Where(g => g.Status == "caught").Select(g => KeyValuePair.Create(g.Gap.Id, (JsonNode?)RunScore.LabelOf(g)))),
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

        problems.AddRange(KeywordProblems($"{where}.expect.json", plan.Gaps, body));

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

    /// <summary>
    /// How well the planted gaps' <c>match</c> keywords fit (2026-11-18, the keyword check, once a scratch script): each matches its own <c>what</c> and no
    /// other gap's; none fits a line of the starting plan (a reported line often quotes the plan), unless the gap says <c>inPlan</c>; every <c>hits</c>
    /// line matches its gap and no other, unless that other lists it too (a line that reports both); no <c>misses</c> line matches. Old runs' gap lines
    /// are left to a by-hand read: they live in <c>publish/</c>, which isn't tracked.
    /// </summary>
    internal static List<string> KeywordProblems(string where, IReadOnlyList<PlantedGap> gaps, string body)
    {
        var problems = new List<string>();
        static string Cut(string line) => line.Length > 100 ? line[..100] + "…" : line;
        foreach (var gap in gaps)
        {
            if (!gap.Match.In(gap.What))
            {
                problems.Add($"{where}: {gap.Id}'s match doesn't match its own what");
            }

            foreach (var other in gaps.Where(o => o != gap && gap.Match.In(o.What)))
            {
                problems.Add($"{where}: {gap.Id}'s match matches {other.Id}'s what");
            }

            if (!gap.InPlan && gap.Match.LineIn(body) is { } planLine)
            {
                problems.Add($"{where}: {gap.Id}'s match fits a line of the starting plan (inPlan: true allows it): «{Cut(planLine)}»");
            }

            foreach (string hit in gap.Hits ?? [])
            {
                if (!gap.Match.In(hit))
                {
                    problems.Add($"{where}: {gap.Id}'s match misses its hit «{Cut(hit)}»");
                }

                foreach (var other in gaps.Where(o => o != gap && o.Match.In(hit) && !(o.Hits ?? []).Contains(hit)))
                {
                    problems.Add($"{where}: {other.Id}'s match claims {gap.Id}'s hit «{Cut(hit)}» (list it as {other.Id}'s hit too if it reports both)");
                }
            }

            foreach (string miss in (gap.Misses ?? []).Where(gap.Match.In))
            {
                problems.Add($"{where}: {gap.Id}'s match matches its miss «{Cut(miss)}»");
            }

            if (gap.Trigger is { } trigger)
            {
                problems.AddRange(FollowOnProblems(where, gap, trigger, body));
            }
        }

        return problems;
    }

    /// <summary>
    /// A follow-on gap's own rules (2026-11-18): its trigger fits its own <c>what</c> and no line of the starting plan (the path isn't taken
    /// yet); it has a <c>fixed</c>, which fits no starting-plan line either (or "foreseen" would come free); each <c>triggerHits</c> line
    /// matches the trigger and no <c>triggerMisses</c> line does.
    /// </summary>
    private static IEnumerable<string> FollowOnProblems(string where, PlantedGap gap, Keywords trigger, string body)
    {
        static string Cut(string line) => line.Length > 100 ? line[..100] + "…" : line;
        if (!trigger.In(gap.What))
        {
            yield return $"{where}: {gap.Id}'s trigger doesn't match its own what";
        }

        if (trigger.LineIn(body) is { } triggered)
        {
            yield return $"{where}: {gap.Id}'s trigger fits a line of the starting plan, so the path counts as taken from the start: «{Cut(triggered)}»";
        }

        if (gap.Fixed is not { } fixedBy)
        {
            yield return $"{where}: {gap.Id} is a follow-on gap with no fixed: foreseen and fixed silently can't be told from missed";
        }
        else if (fixedBy.LineIn(body) is { } fixedLine)
        {
            yield return $"{where}: {gap.Id}'s fixed fits a line of the starting plan, so foreseen would come free: «{Cut(fixedLine)}»";
        }

        foreach (string hit in (gap.TriggerHits ?? []).Where(h => !trigger.In(h)))
        {
            yield return $"{where}: {gap.Id}'s trigger misses its trigger hit «{Cut(hit)}»";
        }

        foreach (string miss in (gap.TriggerMisses ?? []).Where(trigger.In))
        {
            yield return $"{where}: {gap.Id}'s trigger matches its trigger miss «{Cut(miss)}»";
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
        Severity();
        PlanRun();
        StepChangesRun();
        ItemHashRun();
        KeywordRun();
        FocusRun();
        FollowOnRun();
        ProbeRun();
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

    /// <summary>Phase 3 (2026-11-17): --set, the audit's low changes and its light stops, the plan shadow, a call's pass, a gap's tag.</summary>
    private static void Severity()
    {
        Expect(Options.ParseSet("CodeAuditLowKeepsStreak=true", out _) is { Value: JsonValue v } && v.GetValue<bool>(), "--set reads true as JSON");
        Expect(Options.ParseSet("LlmReasoning=high", out _) is { Value: JsonValue s } && s.GetValue<string>() == "high", "--set reads a bare word as a string");
        Expect(Options.ParseSet("CodeAuditLowKeepStreak=true", out string? misspelled) is null && misspelled is not null, "--set refuses a name the app doesn't have");
        Expect(Options.ParseSet("FileTools=false", out string? files) is null && files is not null, "--set refuses FileTools");
        Expect(Options.ParseSet("=1", out _) is null, "--set refuses a missing name");

        string bench = Scratch();
        try
        {
            Options Opts(params string[] extra) => Options.Parse(["--url", "http://127.0.0.1:1/v1", .. extra])!;
            var with = Opts("--set", "CodeAuditLowKeepsStreak=true");
            Expect(Bench.BenchSetsDiffer(bench, with) is null, "a new folder takes any --set");
            File.WriteAllText(Path.Combine(bench, "bench.json"), new JsonObject { ["runs"] = 3, ["sets"] = Bench.SetsJson(with.Sets) }.ToJsonString());
            Expect(Bench.BenchSetsDiffer(bench, Opts("--set", "CodeAuditLowKeepsStreak=true")) is null, "a resume with the same --set");
            Expect(Bench.BenchSetsDiffer(bench, Opts()) == "CodeAuditLowKeepsStreak=true", "a resume that forgot --set names the recorded pair");
            Expect(Bench.BenchSetsDiffer(bench, Opts("--set", "CodeAuditLowKeepsStreak=false")) is not null, "a resume with another value");
            Expect(Bench.BenchSetsDiffer(bench, Opts("--fresh")) is null, "--fresh starts over");
            File.WriteAllText(Path.Combine(bench, "bench.json"), "{\"runs\":3}");
            Expect(Bench.BenchSetsDiffer(bench, with) == "(none)", "a --set on a folder run without one");
        }
        finally
        {
            Directory.Delete(bench, recursive: true);
        }

        string audit = string.Join('\n',
            "x " + AuditText.PassLogLine(1, 3, 0, false, false, null),
            "x " + AuditText.PassLogLine(2, 0, 1, true, false, null, light: 1),
            "x " + AuditText.PassLogLine(3, 0, 0, true, true, null, light: 12),
            "x " + AuditText.PassLogLine(4, 0, 0, true, false, null));
        var a = LogFacts.Read(audit);
        Expect(a.AuditPasses == 4, $"audit passes with low changes read as {a.AuditPasses}");
        Expect(a.AuditLight == 13 && a.AuditLightPasses == 2, $"low changes read as {a.AuditLight} over {a.AuditLightPasses} passes");

        var converged = Patterns.LastStop(AuditText.ConvergedNotice("audit-x", 4, 2, 9, 9, pane: false, light: true), Patterns.AuditStops);
        Expect(converged?.Name == "converged", $"a converged audit with low changes read as {converged?.Name ?? "nothing"}");
        var part = Patterns.LastStop(AuditText.ConvergedNotice("audit-x", 4, 2, 5, 9, pane: false, unread: 4, light: true), Patterns.AuditStops);
        Expect(part?.Name == "converged, part read", $"a part-read convergence with low changes read as {part?.Name ?? "nothing"}");
        var unconfirmed = Patterns.LastStop(AuditText.StopNotice(PlanGapStop.Unconfirmed, "audit-x", 5, lastLight: true), Patterns.AuditStops);
        Expect(unconfirmed?.Reason == PlanGapStop.Unconfirmed, $"an unconfirmed stop after low changes read as {unconfirmed?.Name ?? "nothing"}");

        string plan = string.Join('\n',
            "x " + PlanText.PassLogLine(1, 2, ["[high] a", "b"], false),
            "x " + PlanText.ShadowLogLine(1, new GapShadowPass([1, 0, 0, 1], 0, true, true), 2, null, false, 0),
            "x " + PlanText.PassLogLine(2, 1, ["[low] c"], false),
            "x " + PlanText.ShadowLogLine(2, new GapShadowPass([0, 0, 1, 0], 1, true, false), 2, null, false, 0),
            "x " + PlanText.ShadowLogLine(3, new GapShadowPass([0, 0, 0, 0], 2, true, false), 2, null, true, 1),
            "x " + PlanText.CleanPassLogLine(3, 1, 2, ["steps"]),
            "x " + PlanText.ShadowLogLine(4, new GapShadowPass([0, 0, 0, 0], 3, true, false), 2, PlanGapStop.Converged, true, 2));
        var p = LogFacts.Read(plan);
        Expect(p.PlanPasses == 2 && p.PlanCleanPasses == 1, $"plan shadow lines aren't passes: {p.PlanPasses} + {p.PlanCleanPasses} clean");
        Expect(p.PlanShadowLines == 4 && p.PlanShadowWouldConvergeAt == 3, $"plan shadow: {p.PlanShadowLines} lines, would converge at {p.PlanShadowWouldConvergeAt}");
        Expect(p.ShadowWouldConvergeAt is null && p.LastShadowLine is null, "a plan shadow line isn't the audit's");
        Expect(p.LastPlanShadowLine?.EndsWith(PlanText.StopWord(PlanGapStop.Converged), StringComparison.Ordinal) == true, "the last plan shadow line's real word");

        string stdout = string.Join('\n',
            "[tool] present_plan {\"title\": \"T\", \"gaps\": [\"a\"]}",
            "[tool] present_plan -> Error: no.",
            "[tool] present_plan {\"title\": \"T\", \"gaps\": [\"a\"]}",
            "[tool] present_plan -> Plan saved as revision 2.",
            "[notice] " + PlanText.PassNoticeLines(1, 10, 2, ["a"])[0],
            "[tool] present_plan {\"title\": \"T\", \"gaps\": []}",
            "[tool] present_plan -> Plan saved as revision 3.",
            "[notice] " + PlanText.CleanPassNoticeLines(12, 20, 1, 2, ["steps"])[0],
            "[tool] present_plan {\"title\": \"T\", \"gaps\": []}",
            "[notice] " + PlanText.ConvergedNotice(".neon/plans/t.md", 13, 1, 2));
        var passes = Scorer.CallPasses(stdout).ToDictionary(c => c.Call, c => c.Pass);
        Expect(passes.Count == 4 && passes[1] == 1 && passes[2] == 1 && passes[3] == 12 && passes[4] == 13,
            "calls' passes: " + string.Join(", ", passes.Select(kv => $"{kv.Key}→{kv.Value}")));

        PlantedGap Gap(string severity) => new("G", severity, "", new Keywords([["x"]]), null, []);
        string Label(string severity, string line) => RunScore.LabelOf(new GapOutcome(Gap(severity), "caught", line, 1, false));
        Expect(Label("high", "[high] x") == "same" && Label("medium", "High: x") == "one off" && Label("high", "(low) x") == "two off", "tags against the planted severity");
        Expect(Label("medium", "x is missing") == "untagged" && Label("high", "High-level x") == "untagged", "an untagged gap");
        Expect(RunScore.LabelOf(new GapOutcome(Gap("high"), "minor only", "[high] x", 1, false)) == "", "a gap not caught has no label");
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
                "  \"minor\": [\"Named the migration step\", \"Closed the open question: the rate is 0.25 a day\"]}",
                "[tool] present_plan -> Plan saved as revision 2. " + PlanText.MeasuredSentence(["1 step has no indented Done when line"]),
                "[tool] present_plan {\"title\": \"T\", \"gaps\": [\"A gap only a shape-refused call sent\"], \"markdown\": \"x\"}",
                "[tool] present_plan -> " + PlanText.ShapeGapResult(["1 step has no indented Done when line"]),
                "[tool] present_plan {\"title\": \"T\", \"gaps\": [\"A gap only a refused call sent\"], \"markdown\": \"x\"}",
                "[tool] present_plan -> " + PlanText.CheckedRequiredResult,
                "[tool] present_plan {\"title\": \"T\", \"gaps\": [\"never reported, but refused\"], \"markdown\": \"x\"}",
                "[tool] present_plan -> " + PlanText.PassAlreadySavedResult(".neon/plans/selftest.md", 2),
                "[tool] present_plan {\"title\": \"T\", \"gaps\": [",
                "[tool] present_plan -> Error: the arguments were not valid JSON.",
                "[tool] present_plan {\"title\": \"T\", \"gaps\": [\"The rate is an open question\"], \"checked\": [\"steps\"], \"markdown\": \"x\"}",
                "[tool] present_plan -> Plan saved as revision 3.",
                "[notice] " + PlanText.ConvergedNotice(".neon/plans/selftest.md", 2, 3, 1));
            string log = "x " + PlanText.PassLogLine(1, 2, ["a"], false) + "\nx " + PlanText.CleanPassLogLine(2, 1, 1, ["steps"]);
            File.WriteAllText(Path.Combine(folder, "run.log"), log);
            var score = new RunScore { Item = "selftest", Run = 1, Folder = folder, Facts = new JsonObject(), Log = LogFacts.Read(log) };
            var stop = Patterns.LastStop(stdout, Patterns.PlanStops);
            score.Stop = stop?.Name ?? "unknown";
            score.StopReason = stop?.Reason;
            Scorer.ScorePlan(score, plan, stdout);

            Expect(score.Calls.Count == 6, $"present_plan calls read: {score.Calls.Count}");
            Expect(score.Calls.Count(c => c.Refused) == 4 && score.Calls.Count(c => c.Accepted) == 2, "four refused calls, two saved");
            Expect(score.SecondSavesRefused == 1, $"second saves refused: {score.SecondSavesRefused} (a CheckedRequired or shape refusal isn't one)");
            Expect(score.ShapeRefusals == 1, $"shape refusals: {score.ShapeRefusals}");
            Expect(score.MeasuredLeft == 1, $"saved with a measured problem: {score.MeasuredLeft}");
            Expect(score.CleanReports == 0 && score.CleanStepChanges == 0, $"no clean report among gap calls: {score.CleanReports}");
            Expect(score.Calls.Count(c => !c.Parsed) == 1 && score.Flags.Any(f => f.Contains("unparsed", StringComparison.Ordinal)), "the broken call is kept as unparsed");
            Expect(!score.Flags.Any(f => f.StartsWith("parse mismatch", StringComparison.Ordinal)), "no parse mismatch: " + string.Join("; ", score.Flags));
            string Status(string id) => score.Planted.First(g => g.Gap.Id == id).Status;
            Expect(Status("G1") == "caught", "G1 caught, the call's JSON spanning lines: " + Status("G1"));
            Expect(Status("G2") == "minor only", "G2 only in minor: " + Status("G2"));
            Expect(Status("G3") == "fixed silently", "G3 fixed silently: " + Status("G3"));
            Expect(Status("G4") == "caught" && score.Planted.First(g => g.Gap.Id == "G4").FixedInBody, "G4 caught and gone from the body");
            Expect(Status("G5") == "missed", "G5 missed, its words only in a refused call: " + Status("G5"));
            Expect(score.Converged && score.ConvergedWithHighOpen, "converged with a high gap open (G5)");
            Expect(score.OtherGaps.Count == 2 && !score.OtherGaps.Any(g => g.Text.Contains("refused", StringComparison.Ordinal)), $"other gaps, a refused call's left out: {score.OtherGaps.Count}");
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

    /// <summary>
    /// The minor check measured (2026-11-18, phase 3 item 2): clean reports over a real step list, scored with no flag raised. An added step counts,
    /// a reword doesn't, a <c>"verify": null</c> with none in force doesn't (before the next call puts one in force), and a new <c>verify</c> does.
    /// </summary>
    private static void StepChangesRun()
    {
        string folder = Scratch();
        try
        {
            var plan = new PlanItem("selftest", "", "", [], false, null, null, null, []);
            const string Start = "# T\n\n## Steps\n\n- [ ] Write it.\n  Done when: written.\n";
            const string Added = Start + "- [ ] Test it.\n  Done when: tested.\n";
            const string Reworded = Start + "- [ ] Test it well.\n  Done when: tested.\n";
            File.WriteAllText(Path.Combine(folder, "start.md"), "---\nstatus: draft\nrevision: 1\n---\n" + Start);
            Directory.CreateDirectory(Path.Combine(folder, "work", ".neon", "plans"));
            File.WriteAllText(Path.Combine(folder, "work", ".neon", "plans", "selftest.md"), "---\nstatus: draft\nrevision: 5\nverify: dotnet test\n---\n" + Reworded);
            string Call(string markdown, string? verify = null, bool nullVerify = false)
            {
                var args = new JsonObject { ["title"] = "T", ["markdown"] = markdown, ["gaps"] = new JsonArray(), ["checked"] = new JsonArray("the steps") };
                if (verify is not null || nullVerify)
                {
                    args["verify"] = verify;
                }

                return "[tool] present_plan " + args.ToJsonString() + "\n[tool] present_plan -> No gaps found in this pass.";
            }

            string stdout = string.Join('\n', Call(Added), Call(Reworded), Call(Reworded, nullVerify: true), Call(Reworded, "dotnet test"));
            string log = string.Join('\n', Enumerable.Range(1, 4).Select(n => "x " + PlanText.CleanPassLogLine(n, n, 9, ["steps"])));
            File.WriteAllText(Path.Combine(folder, "run.log"), log);
            var score = new RunScore { Item = "selftest", Run = 1, Folder = folder, Facts = new JsonObject(), Log = LogFacts.Read(log) };
            Scorer.ScorePlan(score, plan, stdout);

            Expect(score.Flags.Count == 0, "the step-change run raises no flag: " + string.Join("; ", score.Flags));
            Expect(score.Calls.Count == 4 && score.Calls.All(c => c.Accepted), $"four clean calls read: {score.Calls.Count}");
            Expect(score.Calls[2].Text("verify") == "" && score.Calls[0].Text("verify") is null, "a JSON null verify reads as empty, one left out as null");
            Expect(score.CleanReports == 4, $"clean reports: {score.CleanReports}");
            Expect(score.CleanStepChanges == 2, $"clean reports that changed steps: {score.CleanStepChanges} ({string.Join(", ", score.StepChanges.Select(s => s.Call))})");
            Expect(score.StepChanges.Count > 0 && score.StepChanges[0] == (1, 1, 0, 0, 0, false), $"an added step counts as added: {(score.StepChanges.Count > 0 ? score.StepChanges[0] : default)}");
            Expect(score.StepChanges.Count > 1 && score.StepChanges[1] == (4, 0, 0, 0, 0, true), $"a new verify counts as a check change: {(score.StepChanges.Count > 1 ? score.StepChanges[1] : default)}");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// The per-item material hash (2026-11-18, late gaps), on a small material root: an item's hash follows <c>repo/</c> and its own plan, not
    /// another plan or its own key; the whole hash follows all four; the warning names only the runs whose own material changed. And a planted
    /// gap's pass reaches the run JSON.
    /// </summary>
    private static void ItemHashRun()
    {
        string root = Scratch();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "repo"));
            Directory.CreateDirectory(Path.Combine(root, "plans"));
            void Put(string relative, string text) => File.WriteAllText(Path.Combine(root, relative), text);
            Put("repo/a.py", "x = 1\n");
            Put("plans/one.md", "# One\n");
            Put("plans/one.expect.json", "{\"gaps\": []}");
            Put("plans/two.md", "# Two\n");
            string one = Material.ItemHash(root, "one"), audit = Material.ItemHash(root, Material.AuditName), whole = Material.Hash(root);

            Put("plans/two.md", "# Two, changed\n");
            Expect(Material.ItemHash(root, "one") == one && Material.Hash(root) != whole, "another plan's change moves the whole hash, not this item's");
            whole = Material.Hash(root);
            Put("plans/one.expect.json", "{\"gaps\": [], \"control\": true}");
            Expect(Material.ItemHash(root, "one") == one && Material.Hash(root) != whole, "a key change moves the whole hash, not the item's");
            whole = Material.Hash(root);
            Put("plans/one.md", "# One, changed\n");
            string oneChanged = Material.ItemHash(root, "one");
            Expect(oneChanged != one && Material.ItemHash(root, Material.AuditName) == audit && Material.Hash(root) != whole, "the item's own plan moves its hash and the whole one, not the audit's");
            whole = Material.Hash(root);
            Put("repo/a.py", "x = 2\n");
            Expect(Material.ItemHash(root, "one") != oneChanged && Material.ItemHash(root, Material.AuditName) != audit && Material.Hash(root) != whole, "a repo change moves every hash");
            Expect(Material.ItemHash(root, "gone") == "", "a plan whose file is gone hashes empty");

            string now = Material.Hash(root);
            string Current(string item) => Material.ItemHash(root, item);
            var changed = Material.ChangedItems(
                [("one", "old", Current("one")), ("two", "old", "stale"), ("audit", "old", null), ("audit", now, null), ("two", "old", "stale")],
                now, Current);
            Expect(changed.SequenceEqual(["audit", "two"]), "the warning names a stale item hash and an old run's changed whole hash, once each, in order: " + string.Join(", ", changed));
            Expect(Material.ChangedItems([("one", now, null), ("one", "old", Current("one"))], now, Current).Count == 0, "a matching whole hash, or a matching item hash, flags nothing");

            var gap = new PlantedGap("G1", "high", "a gap", new Keywords([["a"]]), null, []);
            var score = new RunScore { Item = "one", Run = 1, Folder = root, Facts = new JsonObject() };
            score.CallPasses[1] = 1;
            score.CallPasses[2] = 3;
            score.Planted.Add(new GapOutcome(gap, "caught", "[high] a", 2, false));
            score.Planted.Add(new GapOutcome(gap with { Id = "G2" }, "missed", null, null, false));
            var planted = Report.RunJson(score)["planted"]!.AsArray();
            Expect((int?)planted[0]!["pass"] == 3 && (int?)planted[0]!["call"] == 2 && planted[1]!["pass"] is null, $"a planted gap's pass (not its call) in the run JSON: {planted.ToJsonString()}");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Which focuses a run's confirming passes looked at (2026-11-18, Phase 3 item 3): read from the focus lines when the log has them, even with the
    /// review summary in stdout too; else from the summary's labels (a pass line with none adds nothing, a reply's own "pass 3: … (order)" isn't read).
    /// </summary>
    private static void FocusRun()
    {
        string stdout = string.Join('\n',
            "Assistant: pass 3: looked again (order)",
            "[notice] pass 1: 1 gap · 1m 2s · 90.1k tokens",
            "[notice] pass 2: clean · 40.0s · 80.0k tokens",
            "[notice] pass 3: clean, 1 minor fix (verification), confirmed · 47.6s · 93.6k tokens",
            "[notice] pass 4: 1 gap, 3 minor fixes (order) · 1m 17s · 98.1k tokens",
            "[notice] pass 6: clean (risks, advisor), confirmed",
            "[notice] pass 7: clean (advisor)");
        string log = string.Join('\n',
            "x " + PlanText.PassLogLine(1, 1, [], false),
            "x " + PlanText.FocusLogLine(3, "assumptions", "by change (assumptions 4, order 1)"),
            "x " + PlanText.FocusLogLine(5, "order", "least used"),
            "x " + AuditText.FocusLogLine(2, "maintainability", "in turn"));

        var logged = LogFacts.Read(log);
        Expect(logged.PlanPasses == 1, $"a focus line isn't a pass: {logged.PlanPasses}");
        var (fromLog, summary) = FocusReader.Read(logged, stdout);
        Expect(!summary && fromLog.Count == 3 && fromLog[0] == new FocusRecord(3, "assumptions", "by change (assumptions 4, order 1)")
            && fromLog[1] == new FocusRecord(5, "order", "least used") && fromLog[2] == new FocusRecord(2, "maintainability", "in turn"),
            "the focus lines win over the summary: " + string.Join(" / ", fromLog));

        var (fromSummary, read) = FocusReader.Read(LogFacts.Read(""), stdout);
        Expect(read && string.Join(",", fromSummary.Select(f => $"{f.Pass}:{f.Label}")) == "3:verification,4:order,6:risks",
            "the summary's labels without focus lines: " + string.Join(",", fromSummary.Select(f => $"{f.Pass}:{f.Label}")));
        Expect(FocusReader.Counts(fromSummary.Append(new FocusRecord(9, "order", ""))) == "order ×2, verification ×1, risks ×1", "the count line: " + FocusReader.Counts(fromSummary));
        Expect(FocusReader.Read(LogFacts.Read(""), "[notice] pass 2: clean · 1s") is ({ Count: 0 }, false), "no labels, no focuses");
    }

    /// <summary>
    /// Follow-on gaps (2026-11-18): each status once on built bodies and lines, then a scored run for what only <c>ScorePlan</c> does (a refused
    /// call's body takes no path, a later catch counts for the shadow, a missed high one leaves a high gap open, the catch's pass and focus),
    /// and the check's follow-on rules, each broken once.
    /// </summary>
    private static void FollowOnRun()
    {
        PlantedGap On(string id, string path, string word, string? fix = null, string severity = "medium") =>
            new(id, severity, id, new Keywords([[word]]), fix is null ? null : new Keywords([[fix]]), [], Trigger: new Keywords([[path]]));
        var plan = new PlanItem("followon", "", "", [
            new PlantedGap("W", "high", "", new Keywords([["ordinary"]]), null, []),
            On("A", "path_a", "alpha", "fix_a"),
            On("B", "path_b", "beta"),
            On("C", "path_c", "gamma"),
            On("D", "path_d", "delta"),
            On("E", "path_e", "epsilon"),
            On("F", "path_f", "zeta", "fix_f"),
            On("G", "path_g", "omega", severity: "high"),
            On("H", "path_h", "theta"),
            On("I", "path_i", "iota"),
            new PlantedGap("J", "medium", "J", new Keywords([["kappa"]]), null, [], Trigger: new Keywords([["path_j"], ["column"]])),
        ], false, null, null, null, []);
        var bodies = new List<(int, string)>
        {
            (1, "# T\npath_a and fix_a\npath_b\npath_d\npath_e\npath_f\npath_g\npath_h\npath_j\na column"),
            (2, "# T\npath_c\npath_d\npath_f\npath_g"),
        };
        var gapLines = new List<(int, string)> { (1, "beta problem"), (1, "gamma risk ahead"), (1, "an ordinary gap"), (2, "delta found") };
        var minorLines = new List<(int, string)> { (2, "epsilon tidied") };
        var outcomes = Scorer.FollowOnOutcomes(plan, bodies, gapLines, minorLines, "# T\npath_c\npath_f with fix_f\npath_g").ToDictionary(o => o.Gap.Id);
        string Got(string id) => outcomes[id].Status;
        Expect(!outcomes.ContainsKey("W") && Scorer.Outcomes(plan, gapLines, minorLines, "").Select(o => o.Gap.Id).SequenceEqual(["W"]),
            "an ordinary gap is scored by Outcomes, a follow-on one only by FollowOnOutcomes");
        Expect(Got("A") == FollowOnStatus.Foreseen && outcomes["A"].Named is null, "A: the introducing body already holds its fix: " + Got("A"));
        Expect(Got("B") == FollowOnStatus.Foreseen && outcomes["B"].Named == 1, "B: a line of the introducing call names it: " + Got("B"));
        Expect(Got("C") == FollowOnStatus.Foreseen && outcomes["C"].Introduced == 2 && outcomes["C"].Named == 1, "C: named in call 1, its path taken in call 2: " + Got("C"));
        Expect(Got("D") == FollowOnStatus.Caught && outcomes["D"].Introduced == 1 && outcomes["D"].Named == 2, "D: caught by a later gap line: " + Got("D"));
        Expect(Got("E") == FollowOnStatus.MinorOnly, "E: named only by a later minor line: " + Got("E"));
        Expect(Got("F") == FollowOnStatus.FixedSilently && outcomes["F"].Line == "path_f with fix_f", "F: the final body holds its fix: " + Got("F"));
        Expect(Got("G") == FollowOnStatus.Missed, "G: still on the path in the final body: " + Got("G"));
        Expect(Got("H") == FollowOnStatus.Dropped, "H: the final body left the path: " + Got("H"));
        Expect(Got("I") == FollowOnStatus.NotIntroduced, "I: no body took the path: " + Got("I"));
        Expect(Got("J") == FollowOnStatus.NotIntroduced, "J: the trigger's groups on two lines take no path: " + Got("J"));

        string folder = Scratch();
        try
        {
            var scored = new PlanItem("selftest", "", "", [
                new PlantedGap("K", "high", "K", new Keywords([["migrat"]]), new Keywords([["table_info"]]), [], Trigger: new Keywords([["withdrawn_on text"]])),
                new PlantedGap("L", "high", "L", new Keywords([["refused words"]]), new Keywords([["fix_l"]]), [], Trigger: new Keywords([["refused_path"]])),
                new PlantedGap("M", "high", "M", new Keywords([["never named"]]), new Keywords([["fix_m"]]), [], Trigger: new Keywords([["withdrawn_on text"]])),
            ], false, null, null, null, []);
            string body = "# T\n\n## Steps\n\n- [ ] Add withdrawn_on TEXT to books.\n  Done when: it passes.\n";
            File.WriteAllText(Path.Combine(folder, "start.md"), "# T\n\n## Steps\n\n- [ ] Delete the book.\n  Done when: it passes.\n");
            Directory.CreateDirectory(Path.Combine(folder, "work", ".neon", "plans"));
            File.WriteAllText(Path.Combine(folder, "work", ".neon", "plans", "selftest.md"), "---\nstatus: draft\nrevision: 3\n---\n" + body);
            static string Markdown(string text) => JsonValue.Create(text).ToJsonString();
            string stdout = string.Join('\n',
                "[tool] present_plan {\"title\": \"T\", \"gaps\": [\"the delete fails\"], \"markdown\": " + Markdown(body) + "}",
                "[tool] present_plan -> Plan saved as revision 2.",
                "[tool] present_plan {\"title\": \"T\", \"gaps\": [\"refused words\"], \"markdown\": " + Markdown(body + "refused_path\n") + "}",
                "[tool] present_plan -> " + PlanText.PassAlreadySavedResult(".neon/plans/selftest.md", 2),
                "[notice] " + PlanText.PassNoticeLines(1, 10, 1, ["a"])[0],
                "[tool] present_plan {\"title\": \"T\", \"gaps\": [\"no migration for the new column\"], \"markdown\": " + Markdown(body) + "}",
                "[tool] present_plan -> Plan saved as revision 3.",
                "[notice] " + PlanText.PassNoticeLines(2, 10, 1, ["a"])[0],
                "[notice] " + PlanText.ConvergedNotice(".neon/plans/selftest.md", 2, 3, 1));
            string log = string.Join('\n',
                "x " + PlanText.PassLogLine(1, 1, ["a"], false),
                "x " + PlanText.ShadowLogLine(1, new GapShadowPass([0, 0, 0, 0], 2, true, false), 2, null, true, 1),
                "x " + PlanText.FocusLogLine(2, "order", "in turn"),
                "x " + PlanText.PassLogLine(2, 1, ["a"], false));
            var facts = LogFacts.Read(log);
            Expect(facts.PlanShadowWouldConvergeAt == 1, $"the sample's shadow would converge at pass 1: {facts.PlanShadowWouldConvergeAt}");
            var score = new RunScore { Item = "selftest", Run = 1, Folder = folder, Facts = new JsonObject(), Log = facts };
            var stop = Patterns.LastStop(stdout, Patterns.PlanStops);
            score.Stop = stop?.Name ?? "unknown";
            score.StopReason = stop?.Reason;
            (score.Focuses, score.FocusesFromSummary) = FocusReader.Read(facts, stdout);
            Scorer.ScorePlan(score, scored, stdout);
            var k = score.FollowOn.First(f => f.Gap.Id == "K");
            Expect(score.Planted.Count == 0 && score.FollowOn.Count == 3, $"follow-on gaps aren't planted ones: {score.Planted.Count} planted, {score.FollowOn.Count} follow-on");
            Expect(k.Status == FollowOnStatus.Caught && k.Introduced == 1 && score.PassOf(k.Named) == 2, $"K caught in pass 2: {k.Status}, call {k.Named}, pass {score.PassOf(k.Named)}");
            Expect(score.IsConfirming(2) && score.FocusOf(2) == "order" && !score.IsConfirming(1), "pass 2 was a confirming pass, on order");
            Expect(score.GapsAfterShadow.SequenceEqual(["K"]), "a follow-on gap caught after the shadow's pass counts for it: " + string.Join(", ", score.GapsAfterShadow));
            Expect(score.FollowOn.First(f => f.Gap.Id == "L").Status == FollowOnStatus.NotIntroduced, "L: only a refused call's body took its path");
            Expect(score.FollowOn.First(f => f.Gap.Id == "M").Status == FollowOnStatus.Missed && score.Converged && score.ConvergedWithHighOpen,
                "M: a missed high follow-on gap leaves a high gap open");
            Expect(!score.OtherGaps.Any(g => g.Text.Contains("migration", StringComparison.Ordinal)), "a line naming a follow-on gap isn't an other gap");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }

        string start = "# T\n\n## Context\n\n- The books table has isbn and title.\n";
        PlantedGap Rule(Keywords? trigger, Keywords? fix, string[]? hits = null, string[]? misses = null) =>
            new("F", "high", "a withdrawn_on column breaks lend", new Keywords([["breaks lend"]]), fix, [], Trigger: trigger, TriggerHits: hits, TriggerMisses: misses);
        var good = new Keywords([["withdrawn_on"]]);
        var fixedBy = new Keywords([["refuses"]]);
        bool Flags(PlantedGap gap, string text) => Checker.KeywordProblems("x", [gap], start).Any(p => p.Contains(text, StringComparison.Ordinal));
        Expect(Checker.KeywordProblems("x", [Rule(good, fixedBy, ["SET withdrawn_on = today"], ["the title stays"])], start).Count == 0, "a clean follow-on gap passes");
        Expect(Flags(Rule(new Keywords([["archive"]]), fixedBy), "trigger doesn't match its own what"), "a trigger off its own what");
        Expect(Flags(Rule(new Keywords([["withdrawn_on", "isbn"]]), fixedBy), "trigger fits a line of the starting plan"), "a trigger already in the starting plan");
        Expect(Flags(Rule(good, null), "no fixed"), "a follow-on gap with no fixed");
        Expect(Flags(Rule(good, new Keywords([["title"]])), "fixed fits a line of the starting plan"), "a fix already in the starting plan");
        Expect(Flags(Rule(good, fixedBy, ["an archive table"]), "misses its trigger hit"), "a trigger hit the trigger misses");
        Expect(Flags(Rule(good, fixedBy, null, ["rather than a withdrawn_on column"]), "matches its trigger miss"), "a trigger miss the trigger matches");
    }

    /// <summary>
    /// The fresh-look probe (2026-11-19): which runs it picks, the plan it gives (no <c>review:</c> line), the derived item, a probe run scored
    /// (caught in pass 1, a line naming a gap it doesn't probe kept out of other gaps), the gap table's rows, its hash key, and the resume check.
    /// </summary>
    private static void ProbeRun()
    {
        PlantedGap Gap(string id, string word, bool followOn = false) =>
            new(id, "medium", id, new Keywords([[word]]), null, [], Trigger: followOn ? new Keywords([["path_" + id]]) : null);
        var plan = new PlanItem("probe", "", "", [Gap("W", "ordinary"), Gap("F", "isbn reuse", followOn: true), Gap("G", "gamma", followOn: true)],
            false, null, null, null, []);
        RunScore Built(params (string Id, string Status)[] gaps)
        {
            var built = new RunScore { Item = "probe", Run = 1, Folder = "x", Facts = new JsonObject(), Log = LogFacts.Read("") };
            foreach (var (id, status) in gaps)
            {
                var gap = plan.Gaps.First(g => g.Id == id);
                if (gap.FollowOn)
                {
                    built.FollowOn.Add(new FollowOnOutcome(gap, status, null, 1, null));
                }
                else
                {
                    built.Planted.Add(new GapOutcome(gap, status, null, null, false));
                }
            }

            return built;
        }

        Expect(Probe.MissedGaps(Built(("W", "missed"), ("F", FollowOnStatus.Missed), ("G", FollowOnStatus.Foreseen))).SequenceEqual(["W", "F"]),
            "a missed planted gap and a missed follow-on gap pick the run; a foreseen one doesn't");
        Expect(Probe.MissedGaps(Built(("W", "minor only"), ("F", FollowOnStatus.MinorOnly), ("G", FollowOnStatus.Caught))).Count == 0, "minor only and caught don't");

        string withReview = "---\r\nstatus: draft\r\nreview: 5 passes, confirmed\r\nrevision: 4\r\n---\r\n# T\r\nreview: kept in the body\r\n";
        string stripped = Probe.StripReview(withReview);
        Expect(stripped == "---\r\nstatus: draft\r\nrevision: 4\r\n---\r\n# T\r\nreview: kept in the body\r\n", "the front matter's review: line goes, the body's stays: " + stripped);
        Expect(Probe.StripReview("# T\nreview: x\n") == "# T\nreview: x\n", "no front matter, nothing taken");

        var derived = Probe.Derive(plan, ["F"]);
        Expect(derived.Gaps.Count == 1 && derived.Gaps[0].Id == "F" && !derived.Gaps[0].FollowOn, "the derived item: F alone, as an ordinary gap");

        string folder = Scratch();
        try
        {
            string body = "# T\n\n## Steps\n\n- [ ] Keep the row.\n  Done when: it passes.\n";
            File.WriteAllText(Path.Combine(folder, "start.md"), body);
            Directory.CreateDirectory(Path.Combine(folder, "work", ".neon", "plans"));
            File.WriteAllText(Path.Combine(folder, "work", ".neon", "plans", "probe.md"), "---\nstatus: draft\nrevision: 2\n---\n" + body);
            static string Markdown(string text) => JsonValue.Create(text).ToJsonString();
            string stdout = string.Join('\n',
                "[tool] present_plan {\"title\": \"T\", \"gaps\": [\"an ordinary gap again\", \"the isbn reuse problem\", \"a new finding\"], \"markdown\": " + Markdown(body) + "}",
                "[tool] present_plan -> Plan saved as revision 2.",
                "[notice] " + PlanText.PassNoticeLines(1, 10, 3, ["a"])[0]);
            var score = new RunScore
            {
                Item = "probe", Run = 1, Folder = folder, Facts = new JsonObject(), Log = LogFacts.Read("x " + PlanText.PassLogLine(1, 3, ["a"], false)),
                Source = "publish/gapbench/x/probe-1",
            };
            Scorer.ScorePlan(score, derived, stdout, plan.Gaps);
            var f = score.Planted.Single();
            Expect(f.Gap.Id == "F" && f.Status == "caught" && score.PassOf(f.Call) == 1, $"the probed gap caught in pass 1: {f.Status}, pass {score.PassOf(f.Call)}");
            Expect(score.OtherGaps.Count == 1 && score.OtherGaps[0].Text == "a new finding",
                "a line naming W, which isn't probed, isn't another gap: " + string.Join("; ", score.OtherGaps.Select(g => g.Text)));
            Expect(score.FollowOn.Count == 0, "a probe run scores no follow-on gaps");
            Expect(Report.TableGaps(plan, [score]).Select(g => g.Id).SequenceEqual(["F"]), "a probe bench's table rows are the probed gaps");
            Expect(Report.TableGaps(plan, [Built()]).Select(g => g.Id).SequenceEqual(["W"]), "an ordinary bench's are the planted ones");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }

        Expect(Report.HashFor(Probe.ItemPrefix + "withdraw-books") == Material.RepoHash(Repo.MaterialRoot)
            && Report.HashFor("withdraw-books") == Material.ItemHash(Repo.MaterialRoot, "withdraw-books"), "a probe run's hash key is repo/ alone");
        Expect(Material.ChangedItems([(Probe.ItemPrefix + "withdraw-books", "old", Material.RepoHash(Repo.MaterialRoot))], "now", Report.HashFor).Count == 0,
            "a probe run that saw today's repo/ isn't flagged, whatever the plan file says now");

        string bench = Scratch();
        try
        {
            string a = Path.Combine(Repo.Root, "publish", "gapbench", "a"), b = Path.Combine(Repo.Root, "publish", "gapbench", "b");
            Expect(Probe.FromDiffers(bench, [a]) is null, "a folder never run takes any --from");
            var sources = new List<ProbeSource> { new("publish/gapbench/a/probe-2", "probe", ["F"]) };
            File.WriteAllText(Path.Combine(bench, "bench.json"),
                new JsonObject { ["runs"] = 1, ["from"] = new JsonArray("publish/gapbench/a"), ["sources"] = Probe.ToJson(sources) }.ToJsonString());
            Expect(Probe.FromDiffers(bench, [a]) is null, "a resume with the same --from");
            Expect(Probe.FromDiffers(bench, [a, b]) is not null && Probe.FromDiffers(bench, null) is not null, "another --from, or none, is refused");
            Expect(Probe.Recorded(bench) is [{ Source: "publish/gapbench/a/probe-2", Item: "probe", Gaps: ["F"] }], "the recorded sources read back");
            File.WriteAllText(Path.Combine(bench, "bench.json"), "{\"runs\":3}");
            Expect(Probe.FromDiffers(bench, null) is null && Probe.FromDiffers(bench, [a]) is not null, "an ordinary bench resumes as one, and takes no probe");
        }
        finally
        {
            Directory.Delete(bench, recursive: true);
        }
    }

    /// <summary>The keyword check's five rules (2026-11-18), each broken once on built gaps, and a clean set that passes.</summary>
    private static void KeywordRun()
    {
        const string body = "# Plan\n\n- [ ] Add `remove_member`.\n  Done when: it prints removed.";
        PlantedGap Gap(string id, string what, string[][] match, string[]? hits = null, string[]? misses = null, bool inPlan = false) =>
            new(id, "high", what, new Keywords(match), null, [], hits, misses, inPlan);
        var a = Gap("A", "the foreign key fails the delete", [["foreign key"]], ["a foreign key line", "both: foreign key and renewal"], ["a renewal line"]);
        var b = Gap("B", "renewals are never saved", [["renewal"]], ["a renewal is lost", "both: foreign key and renewal"]);
        Expect(Checker.KeywordProblems("x", [a, b], body).Count == 0, "a clean set, with a line that is a hit of both: " + string.Join(" / ", Checker.KeywordProblems("x", [a, b], body)));

        bool Flags(string what, IReadOnlyList<PlantedGap> gaps, string text)
        {
            var found = Checker.KeywordProblems("x", gaps, body);
            bool ok = found.Any(p => p.Contains(text, StringComparison.Ordinal));
            Expect(ok, $"{what}: wanted «{text}», got: {string.Join(" / ", found)}");
            return ok;
        }

        Flags("rule 1", [a with { What = "the delete fails" }, b], "A's match doesn't match its own what");
        Flags("rule 2", [a with { What = "the foreign key fails the renewal" }, b], "B's match matches A's what");
        Flags("rule 3", [a with { Match = new Keywords([["remove_member"]]), What = "remove_member fails", Hits = [] }, b], "A's match fits a line of the starting plan");
        Expect(Checker.KeywordProblems("x", [a with { Match = new Keywords([["remove_member"]]), What = "remove_member fails", Hits = [], InPlan = true }, b], body).Count == 0,
            "inPlan allows a match on a plan line");
        Flags("rule 4, a missed hit", [a with { Hits = ["nothing here"] }, b], "A's match misses its hit");
        Flags("rule 4, a hit of one gap claimed by another", [a, b with { Hits = ["a renewal is lost"] }, Gap("C", "the renewal cap", [["renewal cap"]], ["the renewal cap is off"])],
            "B's match claims C's hit");
        Flags("rule 5", [a with { Misses = ["a foreign key line"] }, b], "A's match matches its miss");
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
