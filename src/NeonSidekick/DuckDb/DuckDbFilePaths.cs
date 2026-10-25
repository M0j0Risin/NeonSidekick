using System.Text;
using NeonSidekick.Files;

namespace NeonSidekick.DuckDb;

/// <summary>
/// A statement made ready to run (<see cref="DuckDbFilePaths.Rewrite"/>): the text DuckDB gets, each data file named by its checked full
/// path, and the full paths with the spelling the model wrote, so an error names a file as the model did.
/// </summary>
public sealed record DuckDbPrepared(string Sql, IReadOnlyDictionary<string, string> Spelled)
{
    /// <summary>A statement that names no file: run as written.</summary>
    public static DuckDbPrepared AsWritten(string sql) => new(sql, new Dictionary<string, string>());

    /// <summary><paramref name="text"/> (one of DuckDB's messages) with each full path put back as the model wrote it, both slash forms.</summary>
    public string Unspell(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (var (full, spelled) in Spelled.OrderByDescending(p => p.Key.Length))
        {
            text = text.Replace(full, spelled, StringComparison.OrdinalIgnoreCase).Replace(full.Replace('\\', '/'), spelled, StringComparison.OrdinalIgnoreCase);
        }

        return text;
    }
}

/// <summary>
/// The app's path layer for the data files a DuckDB statement reads (2026-10-25, the DuckDB plan's Phase 1; no SQLite twin), in front of the
/// engine's own folder rule (outside access off, the working directory the only folder). Every file reference the gates hand out
/// (<see cref="DuckDbFileReference"/>) is <b>checked</b> through <see cref="WorkingDirectory.Resolve(string, bool, out string)"/> as
/// <c>read_file</c> reads — whatever it refuses is refused here, nothing more — then <b>rewritten</b> in the text to its full path as a plain
/// <c>'…'</c> literal (DuckDB resolves a relative path from the process's folder, not the working directory). A pattern (<c>*</c>,
/// <c>?</c>) is taken only inside a reader's argument and within one folder: the app expands it through
/// <see cref="WorkingDirectory.Find(string, string, CancellationToken, int, int)"/> (hidden, system and link entries skipped, sorted) into a
/// list, at most <see cref="DuckDbText.MaxGlobFiles"/>; <c>**</c>, <c>[…]</c> and a pattern in a table's place are refused. With sandbox
/// files off every file is refused. Pure but for the file system's reads.
/// </summary>
public static class DuckDbFilePaths
{
    /// <summary>
    /// <paramref name="sql"/> with every one of <paramref name="files"/> checked and rewritten (null and <paramref name="prepared"/> set), or the
    /// <c>Error:</c> sentence. <paramref name="sandbox"/> is the working directory while <c>DuckDB sandbox files</c> is on, else null.
    /// </summary>
    public static string? Rewrite(string sql, IReadOnlyList<DuckDbFileReference> files, WorkingDirectory? sandbox, CancellationToken cancellationToken, out DuckDbPrepared prepared)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(files);
        prepared = DuckDbPrepared.AsWritten(sql);
        var spelled = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var edits = new List<(int Start, int End, string Text)>();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Replacement(file, sandbox, spelled, cancellationToken, out string? text) is { } refused)
            {
                return refused;
            }

            if (text is not null)
            {
                edits.Add((file.Start, file.End, text));
            }
        }

        if (edits.Count == 0)
        {
            return null;
        }

        var sb = new StringBuilder(sql);
        foreach (var (start, end, text) in edits.OrderByDescending(e => e.Start))
        {
            sb.Remove(start, end - start).Insert(start, text);
        }

        prepared = new DuckDbPrepared(sb.ToString(), spelled);
        return null;
    }

    /// <summary>A path as a DuckDB string literal: <c>'…'</c>, a quote doubled.</summary>
    public static string Literal(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return "'" + path.Replace("'", "''", StringComparison.Ordinal) + "'";
    }

    /// <summary>Whether a path holds a pattern DuckDB would expand: <c>*</c>, <c>?</c> or <c>[</c>.</summary>
    public static bool IsPattern(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path.IndexOfAny(['*', '?', '[']) >= 0;
    }

    /// <summary>One reference's new text (null leaves it as written), or the refusal.</summary>
    private static string? Replacement(DuckDbFileReference file, WorkingDirectory? sandbox, Dictionary<string, string> spelled, CancellationToken cancellationToken, out string? text)
    {
        text = null;
        string path = file.Path.Trim();
        if (sandbox is null)
        {
            return file.Need switch
            {
                DuckDbFileNeed.Required => DuckDbText.FilesNeedSandbox(path),
                DuckDbFileNeed.FileLike => DuckDbText.TableLooksLikeFile(file.Path, sandbox: false),
                _ => null,
            };
        }

        if (IsPattern(path))
        {
            if (file.Need == DuckDbFileNeed.IfFile)
            {
                return null;
            }

            return file.InReader ? Expand(file, path, sandbox, spelled, cancellationToken, out text) : DuckDbText.GlobRefused(path);
        }

        string full = "";
        var outcome = path.Length == 0 ? FileOutcome.Missing : sandbox.Resolve(path, forWrite: false, out full);
        if (outcome != FileOutcome.Ok || !File.Exists(full))
        {
            return file.Need switch
            {
                DuckDbFileNeed.IfFile => null,
                DuckDbFileNeed.FileLike => DuckDbText.TableLooksLikeFile(file.Path, sandbox: true),
                _ when outcome != FileOutcome.Ok => DuckDbText.FileRefused(path, FileText.Error(outcome, path, "read")),
                _ when Directory.Exists(full) => DuckDbText.FileRefused(path, FileText.Error(FileOutcome.IsDirectory, path, "read")),
                _ => DuckDbText.NoSuchFile(path),
            };
        }

        if (TooLong(full))
        {
            return "Error: " + DuckDbText.PathTooLong(path);
        }

        spelled[full] = path;
        text = Literal(full);
        return null;
    }

    /// <summary>A pattern in a reader's argument as the list of the files it matches in its one folder, or the refusal.</summary>
    private static string? Expand(DuckDbFileReference file, string pattern, WorkingDirectory sandbox, Dictionary<string, string> spelled, CancellationToken cancellationToken, out string? text)
    {
        text = null;
        int cut = pattern.LastIndexOfAny(['/', '\\']);
        string folder = cut < 0 ? "" : pattern[..cut];
        string name = pattern[(cut + 1)..];
        if (pattern.Contains("**", StringComparison.Ordinal) || pattern.Contains('[') || IsPattern(folder) || name.Length == 0)
        {
            return DuckDbText.GlobRefused(pattern);
        }

        var found = sandbox.Find(name, folder, cancellationToken, limit: DuckDbText.MaxGlobFiles + 1, maxDepth: 1);
        if (found.Outcome != FileOutcome.Ok)
        {
            return DuckDbText.FileRefused(pattern, FileText.Error(found.Outcome, folder.Length == 0 ? "." : folder, "read", found.Detail));
        }

        if (found.Paths.Count > DuckDbText.MaxGlobFiles || found.Truncated)
        {
            return DuckDbText.TooManyFiles(pattern);
        }

        var literals = new List<string>(found.Paths.Count);
        foreach (string relative in found.Paths)
        {
            // Each match read as read_file would read it, once more: the walk skips links already, and this keeps the one rule.
            if (sandbox.Resolve(relative, forWrite: false, out string full) != FileOutcome.Ok || !File.Exists(full))
            {
                continue;
            }

            if (TooLong(full))
            {
                return "Error: " + DuckDbText.PathTooLong(relative);
            }

            spelled[full] = relative;
            literals.Add(Literal(full));
        }

        if (literals.Count == 0)
        {
            return DuckDbText.NoMatch(pattern);
        }

        string joined = string.Join(", ", literals);
        text = file.InList ? joined : "[" + joined + "]";
        return null;
    }

    /// <summary>Whether DuckDB cannot open <paramref name="full"/>: a path of <see cref="DuckDbText.MaxPath"/> characters or more on Windows.</summary>
    public static bool TooLong(string full) => OperatingSystem.IsWindows() && full.Length >= DuckDbText.MaxPath;
}
