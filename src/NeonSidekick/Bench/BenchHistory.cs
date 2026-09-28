using System.Text.Json;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.Bench;

/// <summary>
/// The profile's saved <c>/test</c> runs (2026-09-28, the user's call: kept, so runs compare across models), in
/// <see cref="FileName"/> beside <c>memory.json</c>: the last <see cref="MaxRuns"/>, oldest first. Read on each use — the
/// file is small and read only when <c>/test</c> is typed. Missing is empty; unreadable is empty with a warning, and the
/// next run's save writes over it. A save that fails is logged and reported false, never thrown: the run's results are on
/// the screen whatever happens to the file.
/// </summary>
public sealed class BenchHistory
{
    public const string FileName = "tests.json";

    /// <summary>The runs kept; the oldest go first.</summary>
    public const int MaxRuns = 50;

    private const string Category = "Test";

    private readonly object _gate = new();
    private readonly string _filePath;

    /// <param name="directory">The profile's directory; the file is <see cref="FileName"/> under it.</param>
    public BenchHistory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _filePath = Path.Combine(Path.GetFullPath(directory), FileName);
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
    public IReadOnlyDictionary<string, (BenchResult Result, DateTimeOffset At)> Latest(string model)
    {
        var latest = new Dictionary<string, (BenchResult, DateTimeOffset)>(StringComparer.OrdinalIgnoreCase);
        foreach (var run in Runs().Where(r => r.Model.Equals(model, StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var result in run.Results.Where(r => r.Verdict != BenchVerdict.Skipped))
            {
                latest[result.Test] = (result, run.At);
            }
        }

        return latest;
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
