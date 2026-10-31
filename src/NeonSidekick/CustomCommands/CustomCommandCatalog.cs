using NeonSidekick.App;
using NeonSidekick.Diagnostics;
using NeonSidekick.Files;

namespace NeonSidekick.CustomCommands;

/// <summary>
/// The custom commands (2026-10-30, the custom commands plan): every <c>*.md</c> in the two <c>commands</c> folders and their subfolders up to
/// <see cref="CustomCommandName.MaxDepth"/> deep — the loaded profile's first, then the home's — the <see cref="Comfy.ComfyWorkflowCatalog"/>
/// shape. The roots are asked afresh at each scan (a profile switch needs no rebuild); a file is read again only when its write time or length
/// changed; a lock keeps Kestrel's threads and the screen's apart.
/// <list type="bullet">
/// <item>A root may be a link and is followed (one set shared with Claude Code's <c>~/.claude/commands</c>); a subfolder or file below it that
/// is a link is a Skipped row, never followed (which keeps a loop out too). Two roots that are one folder are read once, as the profile's.</item>
/// <item>A name a built-in has (the parser's own answer, so a future built-in wins the same way) is Shadowed; so is a global file the profile's
/// of the same name hides. A profile file that is a problem hides nothing: the global one runs, and the Skipped row says so.</item>
/// <item>A scan never throws: a file it can't read is an uncached Skipped row (an editor's save in progress, tried again next scan), a folder
/// it can't list a warning and one Skipped row, the rest read on. Dot-files and dot-folders are passed over without a word.</item>
/// </list>
/// </summary>
public sealed class CustomCommandCatalog
{
    internal const string Category = "Commands";

    /// <summary>How long <see cref="Recent"/> reuses a scan: the completion list asks on every key.</summary>
    public static readonly TimeSpan ReuseWindow = TimeSpan.FromSeconds(1);

    private readonly Func<CustomCommandRoots> _roots;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly Dictionary<string, (DateTime Stamp, long Length, Loaded Result)> _cache = new(StringComparer.Ordinal);
    private readonly HashSet<string> _announced = new(StringComparer.Ordinal);
    private CustomCommandScan _last = CustomCommandScan.Empty;
    private DateTimeOffset _lastAt = DateTimeOffset.MinValue;

    private sealed record Loaded(CustomCommandFileText? Text, string? Problem);

    /// <param name="roots">The two folders, asked afresh at each scan.</param>
    public CustomCommandCatalog(Func<CustomCommandRoots> roots, TimeProvider? time = null)
    {
        _roots = roots ?? throw new ArgumentNullException(nameof(roots));
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The folders as they stand now.</summary>
    public CustomCommandRoots Roots => _roots();

    /// <summary>The last scan when it is under <see cref="ReuseWindow"/> old, else a new one: the completion list's and the page's.</summary>
    public CustomCommandScan Recent()
    {
        lock (_gate)
        {
            if (_time.GetUtcNow() - _lastAt < ReuseWindow)
            {
                return _last;
            }
        }

        return Scan();
    }

    /// <summary>
    /// Whether <paramref name="word"/> is a command: the recent scan's answer, and a fresh scan's on a miss (2026-10-30, the screen test's
    /// find: <c>/commands save sum</c> then <c>/sum</c> inside <see cref="ReuseWindow"/> read as an unknown command). The parser's question;
    /// a miss is rare (a typo, an unknown word), so the rescan costs little.
    /// </summary>
    public bool Knows(string word) => word.StartsWith('/') && (Recent().Knows(word) || Scan().Knows(word));

    /// <summary>The commands, the shadowed files and the skipped ones, as of now.</summary>
    public CustomCommandScan Scan()
    {
        var roots = _roots();
        lock (_gate)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var problems = new List<CustomCommandProblem>();
            var profile = ReadRoot(roots.Profile, CustomCommandScope.Profile, seen, problems);
            List<CustomCommand> global;
            if (roots.Same)
            {
                global = [];
                problems.Add(new CustomCommandProblem(roots.Global, CustomCommandScope.Global, CustomCommandText.SameFolderNote));
            }
            else
            {
                global = ReadRoot(roots.Global, CustomCommandScope.Global, seen, problems);
            }

            foreach (string gone in _cache.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                _cache.Remove(gone);
            }

            var commands = new List<CustomCommand>();
            var shadowed = new List<CustomCommandShadow>();
            var byName = new Dictionary<string, CustomCommand>(StringComparer.OrdinalIgnoreCase);
            foreach (var command in profile.Concat(global))
            {
                if (BuiltInWord(command.Name) is { } builtIn)
                {
                    shadowed.Add(new CustomCommandShadow(command, builtIn, null));
                }
                else if (byName.TryGetValue(command.Name, out var winner))
                {
                    shadowed.Add(new CustomCommandShadow(command, null, winner));
                }
                else
                {
                    byName[command.Name] = command;
                    commands.Add(command);
                }
            }

            // A profile file that is a problem lets the global command of its name run: its row says so.
            for (int i = 0; i < problems.Count; i++)
            {
                var problem = problems[i];
                if (problem.Scope == CustomCommandScope.Profile && problem.Name is { } name && byName.TryGetValue(name, out var runs) && runs.Scope == CustomCommandScope.Global)
                {
                    problems[i] = problem with { Problem = problem.Problem + CustomCommandText.GlobalRunsInsteadNote(runs.Word) };
                }
            }

            _last = new CustomCommandScan(
                commands.OrderBy(c => c.Name, StringComparer.Ordinal).ToList(),
                shadowed,
                problems);
            _lastAt = _time.GetUtcNow();
            return _last;
        }
    }

    /// <summary>
    /// The files a built-in hides that this run has not named yet (by path and write time, so a changed file is named again), each logged once;
    /// the screen shows a notice for them. From the last scan.
    /// </summary>
    public IReadOnlyList<CustomCommandShadow> TakeNewlyShadowedByBuiltIn()
    {
        lock (_gate)
        {
            var fresh = new List<CustomCommandShadow>();
            foreach (var shadow in _last.Shadowed.Where(s => s.BuiltIn is not null))
            {
                string key = shadow.Command.FilePath + "|" + StampOf(shadow.Command.FilePath);
                if (_announced.Add(key))
                {
                    DiagnosticLog.Warn(Category, CustomCommandText.ShadowedByBuiltInLogLine(shadow.Command.FilePath, shadow.BuiltIn!));
                    fresh.Add(shadow);
                }
            }

            return fresh;
        }
    }

    /// <summary>The built-in word <paramref name="name"/> would be, or null when it is none (<see cref="SlashCommands.Parse(string)"/>'s answer).</summary>
    public static string? BuiltInWord(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        string word = "/" + name;
        return SlashCommands.Parse(word).Command is SlashCommand.Unknown or SlashCommand.None ? null : word;
    }

    private List<CustomCommand> ReadRoot(string root, CustomCommandScope scope, HashSet<string> seen, List<CustomCommandProblem> problems)
    {
        var commands = new List<CustomCommand>();
        if (string.IsNullOrWhiteSpace(root))
        {
            return commands;
        }

        var files = new List<(string Relative, string Full)>();
        Walk(root, root, "", 0, scope, files, problems);
        var duplicates = CustomCommandName.CaseDuplicates(files.Select(f => f.Relative));
        foreach (var (relative, full) in files)
        {
            seen.Add(full);
            string raw = CustomCommandName.RawName(relative);
            if (CustomCommandName.FromRelativePath(relative) is not { } name)
            {
                problems.Add(new CustomCommandProblem(full, scope, CustomCommandText.BadNameProblem(relative)));
                continue;
            }

            if (duplicates.Contains(relative))
            {
                problems.Add(new CustomCommandProblem(full, scope, CustomCommandText.CaseDuplicateProblem("/" + raw), name));
                continue;
            }

            var loaded = Load(full);
            if (loaded.Problem is { } problem)
            {
                problems.Add(new CustomCommandProblem(full, scope, problem, name));
                continue;
            }

            var text = loaded.Text!;
            if (!text.UserInvocable)
            {
                problems.Add(new CustomCommandProblem(full, scope, CustomCommandText.NotUserInvocableProblem, name));
                continue;
            }

            if (text.Body.Trim().Length == 0)
            {
                problems.Add(new CustomCommandProblem(full, scope, CustomCommandText.EmptyBodyProblem, name));
                continue;
            }

            var warnings = new List<string>();
            if (text.FrontmatterWarning is { } frontmatter)
            {
                warnings.Add(frontmatter);
            }

            if (text.HasShellLines)
            {
                warnings.Add(CustomCommandText.ShellLinesWarning);
            }

            if (text.Body.Trim() == CustomCommandText.TemplateBody(name))
            {
                warnings.Add(CustomCommandText.StillTemplateWarning);
            }

            commands.Add(new CustomCommand(
                name,
                scope,
                full,
                text.Description ?? CustomCommandFile.FallbackDescription(text.Body),
                text.ArgumentHint,
                text.Arguments,
                warnings.Count == 0 ? null : string.Join("; ", warnings),
                text.Body)
            {
                HasShellLines = text.HasShellLines,
            });
        }

        return commands;
    }

    /// <summary>
    /// The files <c>/profile push|pull commands</c> copies from <paramref name="root"/>: the ones a scan walks (<c>*.md</c> up to
    /// <see cref="CustomCommandName.MaxDepth"/> folders deep, dot-files and dot-folders left behind), each by its <c>/</c>-joined path under the
    /// root, a Skipped file among them as it is; and the links below the root, never followed, by the same path. A missing root is none.
    /// </summary>
    public static (IReadOnlyList<(string Relative, string Full)> Files, IReadOnlyList<string> Links) CopyableFiles(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var files = new List<(string, string)>();
        var problems = new List<CustomCommandProblem>();
        Walk(root, root, "", 0, CustomCommandScope.Profile, files, problems);
        var links = problems.Where(p => p.Problem == CustomCommandText.LinkProblem)
            .Select(p => Path.GetRelativePath(root, p.FilePath).Replace('\\', '/'))
            .ToList();
        return (files, links);
    }

    // The *.md files under folder (relative to root, scan order), links and deep folders as problems, dot-entries passed over.
    private static void Walk(string root, string folder, string relative, int depth, CustomCommandScope scope, List<(string, string)> files, List<CustomCommandProblem> problems)
    {
        List<FileSystemInfo> entries;
        try
        {
            var info = new DirectoryInfo(folder);
            if (!info.Exists)
            {
                return;
            }

            entries = info.EnumerateFileSystemInfos().OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            DiagnosticLog.Warn(Category, CustomCommandText.UnreadableFolderLogLine(folder, ex.Message));
            problems.Add(new CustomCommandProblem(folder, scope, CustomCommandText.UnreadableFolderProblem(ex.Message)));
            return;
        }

        foreach (var entry in entries)
        {
            if (entry.Name.StartsWith('.'))
            {
                continue;
            }

            string path = relative.Length == 0 ? entry.Name : relative + "/" + entry.Name;
            bool isDirectory = (entry.Attributes & FileAttributes.Directory) != 0;
            if (!isDirectory && !entry.Name.EndsWith(CustomCommandName.Extension, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0 && WorkingDirectory.RealLinkTarget(entry.FullName) is not null)
            {
                problems.Add(new CustomCommandProblem(entry.FullName, scope, CustomCommandText.LinkProblem));
                continue;
            }

            if (isDirectory)
            {
                if (depth >= CustomCommandName.MaxDepth)
                {
                    problems.Add(new CustomCommandProblem(entry.FullName, scope, CustomCommandText.TooDeepProblem));
                    continue;
                }

                Walk(root, entry.FullName, path, depth + 1, scope, files, problems);
                continue;
            }

            files.Add((path, entry.FullName));
        }
    }

    // A file read and parsed, or its problem; a read that failed is not cached, so the next scan tries again.
    private Loaded Load(string file)
    {
        DateTime stamp;
        long length;
        try
        {
            var info = new FileInfo(file);
            stamp = info.LastWriteTimeUtc;
            length = info.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Loaded(null, ex.Message);
        }

        if (_cache.TryGetValue(file, out var cached) && cached.Stamp == stamp && cached.Length == length)
        {
            return cached.Result;
        }

        Loaded result;
        if (length > CustomCommandFile.MaxBytes)
        {
            result = new Loaded(null, CustomCommandText.TooLargeProblem);
        }
        else
        {
            try
            {
                string text = WorkingDirectory.Decode(File.ReadAllBytes(file), out _);
                result = new Loaded(CustomCommandFile.Read(text), null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new Loaded(null, ex.Message);
            }
        }

        _cache[file] = (stamp, length, result);
        return result;
    }

    private static string StampOf(string file)
    {
        try
        {
            return File.GetLastWriteTimeUtc(file).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }
}
