using System.Globalization;
using System.Text;
using NeonSidekick.Llm.Tools;

namespace NeonSidekick.Tasks;

/// <summary>A task list's filters.</summary>
public sealed record TaskFilter(string Folder = "\\", bool Recursive = true, string? State = null, string? Words = null, bool IncludeMicrosoft = false)
{
    public static readonly IReadOnlyList<string> States = ["ready", "running", "disabled", "queued"];

    public bool Passes(TaskEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!IncludeMicrosoft && TaskAccess.IsMicrosoft(entry.Path))
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
        var page = matched.Skip(offset).Take(limit).ToList();
        var text = new StringBuilder($"Tasks on {where} under {filter.Folder}{(filter.Recursive ? "" : " (not its subfolders)")}: {page.Count.ToString(CultureInfo.InvariantCulture)} of {matched.Count.ToString(CultureInfo.InvariantCulture)} matching ({total.ToString(CultureInfo.InvariantCulture)} in all)"
            + (filter.State is { } s ? ", state " + s : "") + (filter.Words is { Length: > 0 } w ? ", filter \"" + w + "\"" : "") + (filter.IncludeMicrosoft ? ", Windows' own included" : "") + "; times are the computer's own clock");
        int shown = 0;
        foreach (var entry in page)
        {
            string line = "\n  " + Line(entry);
            if (text.Length + line.Length > MaxChars - 600)
            {
                break;
            }

            text.Append(line);
            shown++;
        }

        int left = matched.Count - offset - shown;
        if (left > 0)
        {
            text.Append('\n').Append(left.ToString(CultureInfo.InvariantCulture)).Append(" more tasks; pass folder, filter or state, or next: task_list(offset ")
                .Append((offset + shown).ToString(CultureInfo.InvariantCulture)).Append(computerArgument is null ? "" : ", computer " + computerArgument).Append(')');
        }

        if (shown > 0)
        {
            text.Append('\n').Append(UntrustedText.QuoteNote);
        }

        return text.ToString();
    }

    /// <summary>One task's line: its path fenced (anyone may name a task), its state, last run and result, next run.</summary>
    public static string Line(TaskEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return UntrustedText.Fenced(UntrustedText.Cut(entry.Path, 300)) + "  " + TaskText.StateWord(entry.State)
            + "  last " + TaskText.When(entry.LastRun) + (entry.LastRun is null ? "" : " " + TaskWords.Result(entry.LastResult))
            + "  next " + TaskText.When(entry.NextRun);
    }

    /// <summary>One task whole.</summary>
    public static string Detail(string where, TaskDetail detail, TaskDefinition? definition, string? definitionError, bool redacted)
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
            Add(text, "runs as", PrincipalWords(definition.Principal) + (TaskPrincipal.IsPrivileged(definition.Principal) ? " (privileged)" : ""));
            if (definition.Description is { } description)
            {
                Add(text, "description", UntrustedText.Fenced(UntrustedText.Cut(description)));
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
                Add(text, "trigger", UntrustedText.Scrub(TaskWords.Trigger(trigger)));
            }

            foreach (var action in definition.Actions.Take(32))
            {
                Add(text, "action", ActionWords(action, redacted));
            }

            if (definition.ExecutionTimeLimit is { } limit && limit != "PT0S")
            {
                Add(text, "time limit", TaskWords.Duration(limit));
            }

            if (definition.Hidden)
            {
                Add(text, "hidden", "yes");
            }
        }

        foreach (var instance in detail.Running.Take(16))
        {
            Add(text, "running", "pid " + instance.EnginePid.ToString(CultureInfo.InvariantCulture) + (instance.CurrentAction.Length > 0 ? ", " + UntrustedText.Fenced(UntrustedText.Cut(instance.CurrentAction, 300)) : ""));
        }

        if (redacted)
        {
            text.Append("\nsecret-like values hidden as ").Append(ToolRedaction.Placeholder);
        }

        text.Append('\n').Append(UntrustedText.QuoteNote);
        return text.Length > MaxChars ? text.ToString(0, MaxChars - 40) + "\n(cut at " + MaxChars.ToString("N0", CultureInfo.InvariantCulture) + " characters)" : text.ToString();
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

    /// <summary>The command lines a run's question shows: every Exec action whole, never redacted (the user decides on them).</summary>
    public static string Commands(TaskDefinition? definition) =>
        definition is null || definition.Actions.Count == 0 ? "(no actions)"
        : string.Join("; ", definition.Actions.Select(a => a.Kind == "Exec" ? (a.Command ?? "") + (a.Arguments is { } args ? " " + args : "") : a.Kind + (a.ClassId is { } id ? " " + id : "")));

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
