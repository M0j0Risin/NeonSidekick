using System.Globalization;
using NeonSidekick.UI;

namespace NeonSidekick.EventLog;

/// <summary>
/// What a <c>/eventlog</c> line asks for on a Mac (2026-10-18, the Mac event-log plan's phase 4): the log word (<c>system</c>, <c>crashes</c>,
/// <c>install</c>, a <c>.logarchive</c>), the terms (each null when not given), and the free text.
/// </summary>
public sealed record MacLogCommandArgs(
    string Log,
    bool LogGiven,
    string? Levels = null,
    string? Process = null,
    int? Pid = null,
    string? Subsystem = null,
    string? Category = null,
    DateTimeOffset? Since = null,
    DateTimeOffset? Until = null,
    string? Text = null,
    IReadOnlyList<string>? Exclude = null)
{
    /// <summary>
    /// The query for <paramref name="source"/>'s kind and the terms its log lacks, for the note: the tools' own builder
    /// (<see cref="MacLogTerms.QueryFor"/>, 2026-10-20, the code review: this re-made it, and an archive's window drifted from theirs).
    /// </summary>
    public (MacLogQuery Query, IReadOnlyList<string> Ignored) QueryFor(MacLogSource source, DateTimeOffset now, int limit)
    {
        ArgumentNullException.ThrowIfNull(source);
        var terms = new MacLogTerms(Levels, Since, Until, Process, Pid, Subsystem, Category, Text, Exclude);
        var (query, ignored, error) = terms.QueryFor(source, now, limit);
        return query is not null ? (query, ignored) : throw new ArgumentException(error, nameof(source));   // Parse refuses a bad level first
    }
}

/// <summary>
/// <c>/eventlog</c> on a Mac (2026-10-18, the Mac event-log plan's phase 4; <see cref="EventLogCommand"/>'s shape): <c>/eventlog
/// [system|crashes|install|x.logarchive] [level:error,fault] [process:loginwindow] [subsystem:com.apple.x] [category:X] [pid:395]
/// [exclude:mediaanalysisd] [since:2h] [until:…] [text…]</c>, every term landing in a <see cref="MacLogQuery"/> as the tools' arguments
/// do. The user's own hand: it works with <c>Event log tools</c> off and shows full text whatever <c>local</c>'s detail. The screen shows
/// it on a pane (<c>App/MacLogMenu</c>); headless, the Web UI and the screen without a pane print <see cref="RunAsync"/>'s lines.
/// </summary>
public static class MacLogCommand
{
    /// <summary>The log a line with none opens on.</summary>
    public const string DefaultLog = "system";

    /// <summary>The term keys, in completion's order. Pinned.</summary>
    public static readonly IReadOnlyList<string> Keys = ["level:", "process:", "subsystem:", "category:", "pid:", "exclude:", "since:", "until:"];

    /// <summary>The three logs the pane's tabs show, in order. Pinned.</summary>
    public static readonly IReadOnlyList<string> TabLogs = ["system", "crashes", "install"];

    /// <summary><paramref name="args"/> read into terms (times from <paramref name="now"/> in <paramref name="zone"/>), or the first refusal.</summary>
    public static (MacLogCommandArgs? Args, string? Error) Parse(string args, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(zone);
        string? log = null;
        var parsed = new MacLogCommandArgs(DefaultLog, false);
        var text = new List<string>();
        var exclude = new List<string>();
        foreach (string word in EventLogCommand.Words(args))
        {
            int colon = word.IndexOf(':', StringComparison.Ordinal);
            string key = colon > 0 ? word[..(colon + 1)].ToLowerInvariant() : "";
            string value = colon > 0 ? word[(colon + 1)..].Trim() : "";
            if (!Keys.Contains(key))
            {
                if (log is null && text.Count == 0)
                {
                    log = word;
                }
                else
                {
                    text.Add(word);
                }

                continue;
            }

            switch (key)
            {
                case "level:":
                    if (MacLogQuery.ParseLevels(value).Error is { } levelError)
                    {
                        return (null, levelError);
                    }

                    parsed = parsed with { Levels = value };
                    break;
                case "process:":
                    parsed = parsed with { Process = Blank(value) };
                    break;
                case "subsystem:":
                    parsed = parsed with { Subsystem = Blank(value) };
                    break;
                case "category:":
                    parsed = parsed with { Category = Blank(value) };
                    break;
                case "pid:":
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int pid))
                    {
                        return (null, MacLogText.BadPid(value));
                    }

                    parsed = parsed with { Pid = pid };
                    break;
                case "exclude:":
                    exclude.AddRange(MacLogQuery.ParseList(value));
                    break;
                default:
                {
                    if (Llm.Tools.TimeArgument.Parse(value, now, zone) is not { } moment)
                    {
                        return (null, EventLogText.BadTime(key.TrimEnd(':'), value));
                    }

                    parsed = key == "since:" ? parsed with { Since = moment } : parsed with { Until = moment };
                    break;
                }
            }
        }

        return (parsed with
        {
            Log = log ?? DefaultLog,
            LogGiven = log is not null,
            Text = text.Count == 0 ? null : string.Join(" ", text),
            Exclude = exclude.Distinct(StringComparer.Ordinal).ToList(),
        }, null);
    }

    /// <summary>
    /// <c>/eventlog</c>'s argument list on a Mac: the logs and the working directory's <c>.logarchive</c> folders (quoted when they hold a
    /// space) and the term keys; after <c>level:</c> the levels. Never reads a log.
    /// </summary>
    public static IReadOnlyList<CompletionItem> Complete(string argText, IReadOnlyList<string> archives)
    {
        ArgumentNullException.ThrowIfNull(argText);
        ArgumentNullException.ThrowIfNull(archives);
        int start = argText.LastIndexOf(' ') + 1;
        string head = argText[..start];
        string last = argText[start..];
        IEnumerable<CompletionItem> pool;
        if (last.StartsWith("level:", StringComparison.OrdinalIgnoreCase))
        {
            pool = new[] { "fault", "error", "default", "info", "debug", "all" }.Select(l => new CompletionItem("level:" + l, MacLogText.LevelNote));
        }
        else
        {
            var items = new List<CompletionItem>();
            if (head.Trim().Length == 0)
            {
                items.Add(new CompletionItem("system", MacLogText.SystemReadable));
                items.Add(new CompletionItem("crashes", MacLogText.CrashesNote));
                items.Add(new CompletionItem("install", MacLogText.InstallNote));
                items.AddRange(archives.Select(a => new CompletionItem(a.Contains(' ', StringComparison.Ordinal) ? "\"" + a + "\"" : a, MacLogText.ArchiveLine)));
            }

            items.AddRange(Keys.Select(k => new CompletionItem(k, MacLogText.KeyNote(k))));
            pool = items;
        }

        return MentionCompleter.Matches(pool.Select(i => i with { Text = head + i.Text }).ToList(), argText);
    }

    /// <summary>
    /// Runs one <c>/eventlog</c> line as lines (headless, the Web UI, the screen without a pane): the header, any notes, then a row per entry.
    /// The user's own hand: full text, the switch not asked.
    /// </summary>
    public static async Task<EventLogCommandResult> RunAsync(MacLogAccess access, string args, int limit, int scanLimit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(args);
        if (access.Log is null)
        {
            return EventLogCommandResult.Error(EventLogText.NoneHere);
        }

        var now = access.Time.GetUtcNow();
        var zone = access.Time.LocalTimeZone;
        var (parsed, error) = Parse(args, now, zone);
        if (parsed is null)
        {
            return EventLogCommandResult.Error(error!);
        }

        var (source, refused) = access.Resolve(parsed.Log, forceRedacted: false);
        if (source is null)
        {
            return EventLogCommandResult.Error(refused!);
        }

        source = source with { Redacted = false };
        var (query, ignored) = parsed.QueryFor(source, now, limit);
        var result = await MacLogReads.SearchAsync(access, source, query, scanLimit, null, cancellationToken).ConfigureAwait(false);
        if (result.Error is { } failed)
        {
            return EventLogCommandResult.Error(failed);
        }

        var page = result.Value!;
        var lines = new List<string> { Header(source, page.Query ?? query, page, now, zone) };
        if (ignored.Count > 0)
        {
            lines.Add(MacLogText.NotApplied(source.Log, ignored));
        }

        lines.AddRange(page.Rows.Select(r => PlainLine(r, zone)));
        if (page.BudgetStop)
        {
            lines.Add(MacLogText.ScanStopped(page.Read, page.Reached is { } reached ? EventLogText.Time(reached, zone) : null, source.Kind));
        }

        if (page.Loudest is { } loud)
        {
            lines.Add(MacLogText.Loudest(EventLogText.Scrub(loud.Process), loud.Count, loud.Of));
        }

        return new EventLogCommandResult(lines, false);
    }

    /// <summary>The lines' header: <c>system on this Mac: 20 entries, newest first</c>, or the empty view's sentence. Pinned.</summary>
    public static string Header(MacLogSource source, MacLogQuery query, MacRowsPage page, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(page);
        string log = EventLogText.Scrub(source.Log);
        if (page.Rows.Count == 0)
        {
            return source.Kind == MacLogKind.Unified
                ? MacLogText.NoEntries(log, query.Levels, query.Since, now, zone)
                : MacLogText.NoFileEntries(source.Log, query.Since, now, zone);
        }

        string count = source.Kind == MacLogKind.Crashes ? EventLogText.Count(page.Rows.Count, "report") : EventLogText.Count(page.Rows.Count, "entry", "entries");
        return log + " " + MacLogText.OnThisMac + ": " + count + ", newest first";
    }

    /// <summary>
    /// One row as a printed line: <c>u…  2026-10-18 01:09:34 -05:00  error  loginwindow[395]  com.apple.x/Cat  message</c> (a report's kind
    /// in the level's place), scrubbed. Pinned.
    /// </summary>
    public static string PlainLine(MacLogRow row, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(row);
        string what = row.Level is { } level ? MacLogRender.LevelWord(level) : row.Kind ?? "";
        string who = (row.Process.Length > 0 ? row.Process : "?") + (row.Pid is { } pid ? "[" + pid.ToString(CultureInfo.InvariantCulture) + "]" : "");
        string message = (row.FirstLine ?? MacLogText.NoMessage).Replace('\t', ' ');
        return EventLogText.Scrub(row.Handle + "  " + EventLogText.Time(row.Time, zone) + (what.Length > 0 ? "  " + what : "") + "  " + who
            + (row.Where is { Length: > 0 } where ? "  " + where : "") + "  " + message);
    }

    private static string? Blank(string value) => value.Length == 0 ? null : value;
}
