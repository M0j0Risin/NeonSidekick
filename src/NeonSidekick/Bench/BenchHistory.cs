using System.Text.Json;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.Bench;

/// <summary>
/// The profile's saved <c>/bench</c> runs (2026-09-28, the user's call: kept, so runs compare across models), in
/// <see cref="FileName"/> beside <c>memory.json</c>: the last <see cref="MaxRuns"/>, oldest first. Read on each use — the
/// file is small and read only when <c>/bench</c> is typed. Missing is empty; unreadable is empty with a warning, and the
/// next run's save writes over it. A save that fails is logged and reported false, never thrown: the run's results are on
/// the screen whatever happens to the file.
/// </summary>
public sealed class BenchHistory
{
    public const string FileName = "tests.json";

    /// <summary>The runs kept; the oldest go first.</summary>
    public const int MaxRuns = 50;

    private const string Category = BenchText.LogCategory;

    private readonly object _gate = new();
    private readonly string _filePath;

    /// <param name="directory">The profile's directory; the file is <see cref="FileName"/> under it.</param>
    public BenchHistory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _filePath = NeonSidekick.Settings.Profiles.ConfigPath(Path.GetFullPath(directory), FileName);   // <profile>\configs since 2026-10-16
    }

    public string FilePath => _filePath;

    /// <summary>The saved runs, oldest first.</summary>
    public IReadOnlyList<BenchRun> Runs()
    {
        lock (_gate)
        {
            return Load();
        }
    }

    /// <summary>
    /// Each test's latest result against <paramref name="model"/> (ignoring case), with its run's time: what the listing
    /// shows beside the test. A skip does not replace an earlier verdict.
    /// </summary>
    public IReadOnlyDictionary<string, (BenchResult Result, DateTimeOffset At)> Latest(string model) =>
        LatestOf(Runs().Where(r => r.Model.Equals(model, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Each test's latest result against <paramref name="model"/> at <paramref name="reasoning"/> (both ignoring case; null matches
    /// the runs saved before the level was), 2026-11-27: the Bench tab's grid row and a bare <c>/bench</c> at the current level, so
    /// a row never mixes two levels' verdicts. A skip does not replace an earlier verdict.
    /// </summary>
    public IReadOnlyDictionary<string, (BenchResult Result, DateTimeOffset At)> Latest(string model, string? reasoning) =>
        LatestOf(Runs().Where(r => r.Model.Equals(model, StringComparison.OrdinalIgnoreCase) && SameLevel(r.Reasoning, reasoning)));

    /// <summary>Each test's latest non-skip result over <paramref name="runs"/> (oldest first), with its run's time.</summary>
    internal static IReadOnlyDictionary<string, (BenchResult Result, DateTimeOffset At)> LatestOf(IEnumerable<BenchRun> runs)
    {
        var latest = new Dictionary<string, (BenchResult, DateTimeOffset)>(StringComparer.OrdinalIgnoreCase);
        foreach (var run in runs)
        {
            foreach (var result in run.Results.Where(r => r.Verdict != BenchVerdict.Skipped))
            {
                latest[result.Test] = (result, run.At);
            }
        }

        return latest;
    }

    /// <summary>Whether two saved levels are the same: null only with null, a word with the same word ignoring case.</summary>
    internal static bool SameLevel(string? a, string? b) =>
        a is null ? b is null : b is not null && a.Equals(b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The run a finished <paramref name="run"/> compares with (2026-11-27, the user's call): the newest saved run of the same model at
    /// the same reasoning level that graded a test this one graded; null for none. A run saved without its level matches none, so a
    /// level change is never read as a verdict change. Read before <paramref name="run"/> is appended.
    /// </summary>
    public BenchRun? Previous(BenchRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return Previous(Runs(), run);
    }

    /// <summary><see cref="Previous(BenchRun)"/> over <paramref name="runs"/>, oldest first.</summary>
    internal static BenchRun? Previous(IReadOnlyList<BenchRun> runs, BenchRun run)
    {
        if (run.Reasoning is null)
        {
            return null;
        }

        var graded = run.Results.Where(r => r.Verdict != BenchVerdict.Skipped).Select(r => r.Test).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (int i = runs.Count - 1; i >= 0; i--)
        {
            var earlier = runs[i];
            if (!ReferenceEquals(earlier, run)
                && earlier.Model.Equals(run.Model, StringComparison.OrdinalIgnoreCase)
                && earlier.Reasoning is not null && SameLevel(earlier.Reasoning, run.Reasoning)
                && earlier.Results.Any(r => r.Verdict != BenchVerdict.Skipped && graded.Contains(r.Test)))
            {
                return earlier;
            }
        }

        return null;
    }

    /// <summary>Adds <paramref name="run"/> as the newest, dropping the oldest past <see cref="MaxRuns"/>. False (logged) when the file could not be written.</summary>
    public bool Append(BenchRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        lock (_gate)
        {
            var runs = Load();
            runs.Add(run);
            if (runs.Count > MaxRuns)
            {
                runs.RemoveRange(0, runs.Count - MaxRuns);
            }

            string tempPath = $"{_filePath}.{Guid.NewGuid():N}.tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
                File.WriteAllText(tempPath, JsonSerializer.Serialize(new BenchHistoryFile { Runs = runs }, BenchJsonContext.Default.BenchHistoryFile));
                File.Move(tempPath, _filePath, overwrite: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                try { File.Delete(tempPath); } catch { /* best effort */ }
                DiagnosticLog.Error(Category, $"Could not write {FileName}: {ex.Message}", ex);
                return false;
            }
        }
    }

    /// <summary>The file's runs; missing or unreadable is empty. Caller holds the lock.</summary>
    private List<BenchRun> Load()
    {
        if (!File.Exists(_filePath))
        {
            return new List<BenchRun>();
        }

        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(_filePath), BenchJsonContext.Default.BenchHistoryFile)?.Runs ?? new List<BenchRun>();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Warn(Category, $"Could not read {FileName}; starting with no saved test runs: {ex.Message}", ex);
            return new List<BenchRun>();
        }
    }
}
