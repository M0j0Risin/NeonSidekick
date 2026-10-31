using NeonSidekick.App;
using NeonSidekick.Diagnostics;
using NeonSidekick.Files;

namespace NeonSidekick.CustomCommands;

/// <summary>
/// The custom commands (2026-10-30, the custom commands plan): every <c>*.md</c> in the two <c>commands</c> folders and their subfolders up to
/// <see cref="CustomCommandName.MaxDepth"/> deep — the loaded profile's first, then the home's — the <see cref="Comfy.ComfyWorkflowCatalog"/>
/// shape. The roots are asked afresh at each scan (a profile switch needs no rebuild); a file is read again only when its write time or length
/// changed. Two locks (2026-10-31, review P1-7): one walk and load at a time under the scan lock, which alone touches the file cache, and the
/// last scan published under a short one, so a reader (the completion list, a Kestrel thread) never waits on a slow folder.
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
    // Guards _last, _lastAt, _missed and _announced only; held for a moment, never over a walk.
    private readonly object _gate = new();
    // One scan at a time; the only lock _cache is touched under.
    private readonly object _scanGate = new();
    private readonly Dictionary<string, (DateTime Stamp, long Length, Loaded Result)> _cache = new(StringComparer.Ordinal);
    private readonly HashSet<string> _announced = new(StringComparer.Ordinal);
    // The words a scan was asked about and missed since it was published (2026-10-31, review P1-6/P3-8): a repeated miss scans no more.
    private readonly HashSet<string> _missed = new(StringComparer.OrdinalIgnoreCase);
    private CustomCommandScan _last = CustomCommandScan.Empty;
    private DateTimeOffset _lastAt = DateTimeOffset.MinValue;

    private sealed record Loaded(CustomCommandFileText? Text, string? Problem, string Stamp = "");

    /// <param name="roots">The two folders, asked afresh at each scan.</param>
    public CustomCommandCatalog(Func<CustomCommandRoots> roots, TimeProvider? time = null)
    {
        _roots = roots ?? throw new ArgumentNullException(nameof(roots));
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The folders as they stand now.</summary>
    public CustomCommandRoots Roots => _roots();

    /// <summary>
    /// The last scan when it is under <see cref="ReuseWindow"/> old and of the folders asked now (2026-10-31, review P5-3: a profile switch
    /// inside the window handed back the other profile's commands), else a new one: the completion list's and the page's. While another scan
    /// runs it hands back the last one rather than wait (review P1-7), unless that one read other folders.
    /// </summary>
    public CustomCommandScan Recent() => Recent(out _);

    private CustomCommandScan Recent(out bool scanned)
    {
        scanned = false;
        var roots = _roots();
        lock (_gate)
        {
            if (_time.GetUtcNow() - _lastAt < ReuseWindow && _last.Roots == roots)
            {
                return _last;
            }
        }

        if (!Monitor.TryEnter(_scanGate))
        {
            lock (_gate)
            {
                if (_last.Roots == roots)
                {
                    return _last;
                }
            }

            Monitor.Enter(_scanGate);
        }

        try
        {
            scanned = true;
            return ScanHeld(_roots());
        }
        finally
        {
            Monitor.Exit(_scanGate);
        }
    }

    /// <summary>
    /// Whether <paramref name="word"/> is a command: the recent scan's answer, and a fresh scan's on a miss (2026-10-30, the screen test's
    /// find: <c>/commands save sum</c> then <c>/sum</c> inside <see cref="ReuseWindow"/> read as an unknown command). Since 2026-10-31 (review
    /// P1-6, P3-8) a miss scans at most once per word per published scan, and never right after <see cref="Recent"/> just scanned, so a pasted
    /// path costs one scan, not one per key. An edit the app makes calls <see cref="Invalidate"/>, so its new name is read at once.
    /// </summary>
    public bool Knows(string word)
    {
        ArgumentNullException.ThrowIfNull(word);
        if (!word.StartsWith('/'))
        {
            return false;
        }

        var recent = Recent(out bool scanned);
        if (recent.Knows(word))
        {
            return true;
        }

        if (!scanned)
        {
            lock (_gate)
            {
                if (_missed.Contains(word))
                {
                    return false;
                }
            }

            recent = Scan();
            if (recent.Knows(word))
            {
                return true;
            }
        }

        lock (_gate)
        {
            if (ReferenceEquals(_last, recent))
            {
                _missed.Add(word);
            }
        }

        return false;
    }

    /// <summary>
    /// Marks the last scan stale (2026-10-31, review P1-6): every edit the app makes to a commands folder (the pane, <c>/commands new|save</c>,
    /// <c>/prompts</c>' m, push and pull) calls it, so the next <see cref="Recent"/> reads afresh — the Web UI's names too.
    /// </summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _lastAt = DateTimeOffset.MinValue;
            _missed.Clear();
        }
    }

    /// <summary>The commands, the shadowed files and the skipped ones, as of now; waits for a scan already running.</summary>
    public CustomCommandScan Scan()
    {
        lock (_scanGate)
        {
            return ScanHeld(_roots());
        }
    }

    // The walk and the loads, under _scanGate; the answer published under _gate.
    private CustomCommandScan ScanHeld(CustomCommandRoots roots)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var problems = new List<CustomCommandProblem>();
        var profile = ReadRoot(roots.Profile, CustomCommandScope.Profile, seen, problems);
        List<CustomCommand> global;
        bool same = roots.Same;
        if (same)
        {
            // A note, not a problem (2026-10-31, review P5-6): nothing was skipped, and the empty lists still read as empty.
            global = [];
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

        var scan = new CustomCommandScan(
            commands.OrderBy(c => c.Name, StringComparer.Ordinal).ToList(),
            shadowed,
            problems)
        {
            Roots = roots,
            Note = same ? CustomCommandText.SameFolderNote : null,
            SameFolder = same,
        };
        lock (_gate)
        {
            _last = scan;
            _lastAt = _time.GetUtcNow();
            _missed.Clear();
        }

        return scan;
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
                string key = shadow.Command.FilePath + "|" + shadow.Command.Stamp;
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

            // The template's description or hint left as they were warns too (2026-10-31, review P6-5), not only its body.
            if (text.Body.Trim() == CustomCommandText.TemplateBody(name) || CustomCommandText.IsTemplateDescription(text.Description) || text.ArgumentHint == CustomCommandText.TemplateHint)
            {
                warnings.Add(CustomCommandText.StillTemplateWarning);
            }

            // Cleaned at the source (2026-10-31, review P2-1, P3-4): a control character in the file reaches no surface as a code.
            string? description = text.Description ?? CustomCommandFile.FallbackDescription(text.Body);
            commands.Add(new CustomCommand(
                name,
                scope,
                full,
                description is null ? null : UI.DiffView.Clean(description),
                text.ArgumentHint is null ? null : UI.DiffView.Clean(text.ArgumentHint),
                text.Arguments,
                warnings.Count == 0 ? null : UI.DiffView.Clean(string.Join("; ", warnings)),
                text.Body)
            {
                HasShellLines = text.HasShellLines,
                Stamp = loaded.Stamp,
            });
        }

        return commands;
    }

    /// <summary>
    /// The files <c>/profile push|pull commands</c> copies from <paramref name="root"/>: the ones a scan walks (<c>*.md</c> up to
    /// <see cref="CustomCommandName.MaxDepth"/> folders deep, dot-files and dot-folders left behind), each by its <c>/</c>-joined path under the
    /// root, a Skipped file among them as it is; the links below the root, never followed, by the same path; and since 2026-10-31 (review
    /// P5-4) the folders that couldn't be listed, so a copy reports them instead of "nothing to copy". A missing root is none.
    /// </summary>
    public static (IReadOnlyList<(string Relative, string Full)> Files, IReadOnlyList<string> Links, IReadOnlyList<CustomCommandProblem> Unreadable) CopyableFiles(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var files = new List<(string, string)>();
        var problems = new List<CustomCommandProblem>();
        Walk(root, root, "", 0, CustomCommandScope.Profile, files, problems);
        var links = problems.Where(p => p.Problem == CustomCommandText.LinkProblem)
            .Select(p => Path.GetRelativePath(root, p.FilePath).Replace('\\', '/'))
            .ToList();
        var unreadable = problems.Where(p => p.Problem.StartsWith(CustomCommandText.UnreadableFolderHead, StringComparison.Ordinal)).ToList();
        return (files, links, unreadable);
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

            // Case-blind, then ordinal (2026-10-31, review P2-12), so which of two case twins is the duplicate never depends on the listing.
            entries = info.EnumerateFileSystemInfos().OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Name, StringComparer.Ordinal).ToList();
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

            if (WorkingDirectory.IsLink(entry))
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

            // A FIFO, socket or device named *.md (2026-10-31, review P5-2) is a Skipped row, so no read, copy or move ever opens it. Asked
            // on a Mac only: off one, an enumerated file is a plain one already, and Windows pays no stat per file.
            if (OperatingSystem.IsMacOS() && !RegularFiles.IsPlain(entry.FullName))
            {
                problems.Add(new CustomCommandProblem(entry.FullName, scope, CustomCommandText.NotAFileProblem));
                continue;
            }

            files.Add((path, entry.FullName));
        }
    }

    // A file read and parsed, or its problem; a read that failed is not cached, so the next scan tries again. The read counts its bytes
    // (2026-10-31, review P5-1: a file grown past the cap since its stat was read whole) and opens only a plain file.
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

        string stampText = stamp.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/" + length.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Loaded result;
        if (length > CustomCommandFile.MaxBytes)
        {
            result = new Loaded(null, CustomCommandText.TooLargeProblem, stampText);
        }
        else
        {
            try
            {
                result = RegularFiles.ReadAtMost(file, CustomCommandFile.MaxBytes) is { } bytes
                    ? new Loaded(CustomCommandFile.Read(WorkingDirectory.Decode(bytes, out _)), null, stampText)
                    : new Loaded(null, CustomCommandText.TooLargeProblem, stampText);
            }
            catch (NotARegularFileException)
            {
                return new Loaded(null, CustomCommandText.NotAFileProblem);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new Loaded(null, ex.Message);
            }
        }

        _cache[file] = (stamp, length, result);
        return result;
    }
}
