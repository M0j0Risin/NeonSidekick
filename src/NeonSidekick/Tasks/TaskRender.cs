using System.Globalization;
using System.Text;
using NeonSidekick.Llm.Tools;

namespace NeonSidekick.Tasks;

/// <summary>A task list's filters.</summary>
public sealed record TaskFilter(string Folder = "\\", bool Recursive = true, string? State = null, string? Words = null, bool IncludeMicrosoft = false)
{
    public static readonly IReadOnlyList<string> States = ["ready", "running", "disabled", "queued"];

    /// <summary>
    /// Whether Windows' own tasks are listed: asked for, or the folder is under <c>\Microsoft\</c> (2026-10-23, the admin review's I10: a list of
    /// <c>\Microsoft\Windows\Defrag</c> dropped every task in it and answered "0 of 0 matching" with no hint).
    /// </summary>
    public bool ShowsMicrosoft => IncludeMicrosoft || TaskAccess.IsMicrosoft(Folder.TrimEnd('\\') + "\\");

    public bool Passes(TaskEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!ShowsMicrosoft && TaskAccess.IsMicrosoft(entry.Path))
        {
            return false;
        }

        if (State is { } state && !string.Equals(TaskText.StateWord(entry.State), state, StringComparison.Ordinal))
        {
            return false;
        }

        return Words is not { Length: > 0 } words || entry.Path.Contains(words, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// The task tools' answers (2026-10-15, the admin-tools plan): a header, a line per task, a page's next call; one task whole with its triggers in
/// words, its actions, its principal and its results. A stranger's text — the description, the author, an action's command and arguments — is
/// scrubbed and fenced; under <c>redacted</c> detail the command lines go through <see cref="ToolRedaction.Text"/> first. Times are the computer's
/// own clock (the list's from COM, the definition's its ISO stamps). Held to <see cref="MaxChars"/>; a single value to 1,000. Pure.
/// </summary>
public static class TaskRender
{
    public const int MaxChars = 16_000;

    public static string List(string where, IReadOnlyList<TaskEntry> matched, int total, int offset, int limit, TaskFilter filter, string? computerArgument)
    {
        ArgumentNullException.ThrowIfNull(matched);
        // The header counts the lines that fit, not the page (2026-10-23, the admin review's S9).
        var page = matched.Skip(offset).Take(limit).ToList();
        var lines = new StringBuilder();
        int shown = 0;
        foreach (var entry in page)
        {
            string line = "\n  " + Line(entry);
            if (lines.Length + line.Length > MaxChars - 1200)
            {
                break;
            }

            lines.Append(line);
            shown++;
        }

        var text = new StringBuilder($"Tasks on {where} under {filter.Folder}{(filter.Recursive ? "" : " (not its subfolders)")}: {shown.ToString(CultureInfo.InvariantCulture)} of {matched.Count.ToString(CultureInfo.InvariantCulture)} matching ({total.ToString(CultureInfo.InvariantCulture)} in all)"
            + (filter.State is { } s ? ", state " + s : "") + (filter.Words is { Length: > 0 } w ? ", filter \"" + UntrustedText.OneLine(w) + "\"" : "") + (filter.IncludeMicrosoft ? ", Windows' own included" : "") + "; times are the computer's own clock");
        text.Append(lines);

        int left = matched.Count - offset - shown;
        if (left > 0)
        {
            text.Append('\n').Append(left.ToString(CultureInfo.InvariantCulture)).Append(" more tasks; narrow with folder, filter or state, or next: ")
                .Append(NextCall(offset + shown, limit, filter, computerArgument));
        }

        if (shown > 0)
        {
            text.Append('\n').Append(UntrustedText.QuoteNote);
        }

        return text.ToString();
    }

    /// <summary>
    /// The next page's call with every filter and the limit restated (2026-10-23, the admin review's E4: it kept only the offset and the
    /// computer, so the copied call paged the whole root's list).
    /// </summary>
    public static string NextCall(int offset, int limit, TaskFilter filter, string? computerArgument)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var parts = new List<string> { "offset " + offset.ToString(CultureInfo.InvariantCulture) };
        if (filter.Folder != "\\")
        {
            parts.Add("folder \"" + UntrustedText.OneLine(filter.Folder) + "\"");
        }

        if (!filter.Recursive)
        {
            parts.Add("recursive false");
        }

        if (filter.State is { } state)
        {
            parts.Add("state " + state);
        }

        if (filter.Words is { Length: > 0 } words)
        {
            parts.Add("filter \"" + UntrustedText.OneLine(words).Replace("\"", "'", StringComparison.Ordinal) + "\"");
        }

        if (filter.IncludeMicrosoft)
        {
            parts.Add("include_microsoft true");
        }

        parts.Add("limit " + limit.ToString(CultureInfo.InvariantCulture));
        if (computerArgument is not null)
        {
            parts.Add("computer " + UntrustedText.OneLine(computerArgument));
        }

        return "task_list(" + string.Join(", ", parts) + ")";
    }

    /// <summary>One task's line: its path fenced (anyone may name a task), its state, last run and result, next run.</summary>
    public static string Line(TaskEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return UntrustedText.Fenced(UntrustedText.Cut(entry.Path, 300)) + "  " + TaskText.StateWord(entry.State)
            + "  last " + TaskText.When(entry.LastRun) + (entry.LastRun is null ? "" : " " + TaskWords.Result(entry.LastResult))
            + "  next " + TaskText.When(entry.NextRun);
    }

    /// <summary>One task whole; "(privileged)" judged for a session connected as <paramref name="self"/> (null: unknown, every account another's).</summary>
    public static string Detail(string where, TaskDetail detail, TaskDefinition? definition, string? definitionError, bool redacted, IReadOnlyCollection<string>? self = null)
    {
        ArgumentNullException.ThrowIfNull(detail);
        var e = detail.Entry;
        var text = new StringBuilder($"Task {UntrustedText.Fenced(UntrustedText.Cut(e.Path, 300))} on {where}: {TaskText.StateWord(e.State)}{(e.Enabled ? "" : " (disabled)")}");
        Add(text, "last run", TaskText.When(e.LastRun) + (e.LastRun is null ? "" : ": " + TaskWords.Result(e.LastResult)) + " (the computer's own clock)");
        Add(text, "next run", TaskText.When(e.NextRun));
        if (e.MissedRuns > 0)
        {
            Add(text, "missed runs", e.MissedRuns.ToString(CultureInfo.InvariantCulture));
        }

        if (definition is null)
        {
            Add(text, "definition", definitionError ?? "not read");
        }
        else
        {
            // The remote's own values fenced and cut (2026-10-23, the admin review's B6): the principal, the triggers' values, a time limit the
            // parser could not read.
            Add(text, "runs as", UntrustedText.Fenced(UntrustedText.Cut(UntrustedText.OneLine(PrincipalWords(definition.Principal)), 200)) + (TaskPrincipal.IsPrivileged(definition.Principal, self) ? " (privileged)" : ""));
            if (definition.Description is { } description)
            {
                // Redacted like the command lines (2026-10-23, the admin review's C3).
                Add(text, "description", UntrustedText.Fenced(UntrustedText.Cut(redacted ? ToolRedaction.Text(description) : description)));
            }

            if (definition.Author is { } author)
            {
                Add(text, "author", UntrustedText.Fenced(UntrustedText.Cut(author, 200)));
            }

            if (definition.Triggers.Count == 0)
            {
                Add(text, "triggers", "none (run on demand only)");
            }

            foreach (var trigger in definition.Triggers.Take(50))
            {
                Add(text, "trigger", UntrustedText.Fenced(UntrustedText.Cut(UntrustedText.OneLine(TaskWords.Trigger(trigger)), 300)));
            }

            foreach (var action in definition.Actions.Take(32))
            {
                Add(text, "action", ActionWords(action, redacted));
            }

            if (definition.ExecutionTimeLimit is { } limit && limit != "PT0S")
            {
                string words = TaskWords.Duration(limit);
                Add(text, "time limit", words == limit ? UntrustedText.Fenced(UntrustedText.Cut(UntrustedText.OneLine(limit), 100)) : words);
            }

            if (definition.Hidden)
            {
                Add(text, "hidden", "yes");
            }
        }

        foreach (var instance in detail.Running.Take(16))
        {
            // Redacted like the definition's command lines (2026-10-23, the admin review's A11).
            string current = redacted ? ToolRedaction.Text(instance.CurrentAction) : instance.CurrentAction;
            Add(text, "running", "pid " + instance.EnginePid.ToString(CultureInfo.InvariantCulture) + (current.Length > 0 ? ", " + UntrustedText.Fenced(UntrustedText.Cut(current, 300)) : ""));
        }

        // The notes kept whole past a cut, the cut never inside a fence (2026-10-23, the admin review's C6).
        string tail = (redacted ? "\nsecret-like values hidden as " + ToolRedaction.Placeholder : "") + "\n" + UntrustedText.QuoteNote;
        return UntrustedText.Capped(text.ToString(), tail, MaxChars, "\n(cut at " + MaxChars.ToString("N0", CultureInfo.InvariantCulture) + " characters)");
    }

    /// <summary>An action in words: an Exec's command line fenced (redacted first under <c>redacted</c>), a COM handler's class.</summary>
    public static string ActionWords(TaskAction action, bool redacted)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (action.Kind == "Exec")
        {
            string line = (action.Command ?? "") + (action.Arguments is { } arguments ? " " + arguments : "");
            string shown = UntrustedText.Fenced(UntrustedText.Cut(redacted ? ToolRedaction.Text(line) : line));
            return "run " + shown + (action.WorkingDirectory is { } folder ? " in " + UntrustedText.Fenced(UntrustedText.Cut(folder, 300)) : "");
        }

        return action.Kind == "ComHandler" ? "COM handler " + UntrustedText.Scrub(action.ClassId ?? "?") : action.Kind + " (deprecated action)";
    }

    /// <summary>
    /// The command lines a run's question shows: every Exec action whole, never redacted (the user decides on them), each on one line
    /// (<see cref="UntrustedText.OneLine"/>, 2026-10-20, a review: a run of spaces or a newline hid a command's tail). Null
    /// <paramref name="definition"/> is one that could not be read: <see cref="TaskText.CommandsUnknown"/>, never "(no actions)". Since
    /// 2026-10-23 (the admin review): each escaped as a name is (A4), an Exec's with where it starts (C8: a relative command resolves there,
    /// <c>\\fs02\drop</c>), a COM handler's class with its data (T11: only the CLSID was shown).
    /// </summary>
    public static string Commands(TaskDefinition? definition) =>
        definition is null ? TaskText.CommandsUnknown
        : definition.Actions.Count == 0 ? "(no actions)"
        : string.Join("; ", definition.Actions.Select(a => UntrustedText.Name(a.Kind == "Exec"
            ? (a.Command ?? "") + (a.Arguments is { } args ? " " + args : "") + (a.WorkingDirectory is { } dir ? " (in " + dir + ")" : "")
            : a.Kind + (a.ClassId is { } id ? " " + id : "") + (a.Data is { } data ? " with data " + data : ""))));

    /// <summary>A principal as people say it: <c>SYSTEM</c>, <c>CORP\ops</c>, <c>the group Administrators</c>.</summary>
    public static string PrincipalWords(TaskPrincipalInfo principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        string who = principal.UserId switch
        {
            "S-1-5-18" => "SYSTEM",
            "S-1-5-19" => "LOCAL SERVICE",
            "S-1-5-20" => "NETWORK SERVICE",
            { Length: > 0 } user => user,
            _ => principal.GroupId is { Length: > 0 } group ? "the group " + group : "the registering user",
        };
        return UntrustedText.Scrub(who) + (string.Equals(principal.RunLevel, "HighestAvailable", StringComparison.OrdinalIgnoreCase) ? " with highest privileges" : "");
    }

    /// <summary>The run level as the question words it.</summary>
    public static string RunLevel(TaskPrincipalInfo principal) =>
        string.Equals(principal.RunLevel, "HighestAvailable", StringComparison.OrdinalIgnoreCase) ? "highest privileges" : "least privilege";

    private static void Add(StringBuilder text, string label, string value) => text.Append("\n  ").Append(label).Append(": ").Append(value);
}
