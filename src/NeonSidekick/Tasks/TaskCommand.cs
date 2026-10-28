using NeonSidekick.Computers;
using NeonSidekick.Llm.Tools;
using NeonSidekick.UI;

namespace NeonSidekick.Tasks;

/// <summary>What a <c>/tasks</c> line asks for.</summary>
public sealed record TaskCommandArgs(string? Computer, string Folder, string State, string Words, bool Microsoft);

public sealed record TaskCommandResult(IReadOnlyList<string> Lines, bool Failed);

/// <summary>
/// <c>/tasks [computer:x] [folder:\x] [state:ready|running|disabled|all] [microsoft] [words]</c> (2026-10-15, the admin-tools plan): the user's
/// own hand over a computer's scheduled tasks. On the pane <c>App/TasksMenu</c>; elsewhere the matching tasks as lines.
/// </summary>
public static class TaskCommand
{
    public const string Word = "/tasks";

    public static readonly IReadOnlyList<string> States = ["ready", "running", "disabled", "all"];

    public static (TaskCommandArgs? Args, string? Error) Parse(string args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? computer = null;
        string folder = "\\";
        string state = "all";
        bool microsoft = false;
        var words = new List<string>();
        // Quoted words kept whole (2026-10-23, the admin review's P3: folder:"\Microsoft\Windows\Windows Defender" could not be named), as
        // /eventlog reads them.
        foreach (string word in EventLog.EventLogCommand.Words(args))
        {
            if (word.StartsWith("computer:", StringComparison.OrdinalIgnoreCase))
            {
                computer = word[9..];
            }
            else if (word.StartsWith("folder:", StringComparison.OrdinalIgnoreCase))
            {
                var (read, bad) = TaskAccess.Folder(word[7..]);
                if (read is null)
                {
                    return (null, bad!.Replace("Error: ", "", StringComparison.Ordinal));
                }

                folder = read;
            }
            else if (word.StartsWith("state:", StringComparison.OrdinalIgnoreCase))
            {
                state = word[6..].ToLowerInvariant();
                if (!States.Contains(state))
                {
                    return (null, TaskText.BadState(state).Replace("Error: ", "", StringComparison.Ordinal));
                }
            }
            else if (string.Equals(word, "microsoft", StringComparison.OrdinalIgnoreCase))
            {
                microsoft = true;
            }
            else
            {
                words.Add(word);
            }
        }

        return (new TaskCommandArgs(computer, folder, state, string.Join(' ', words), microsoft), null);
    }

    public static async Task<TaskCommandResult> RunAsync(TaskAccess access, string args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        if (!OperatingSystem.IsWindows() || access.System is null)
        {
            return Failed(TaskText.NoneHere);
        }

        var (line, error) = Parse(args);
        if (line is null)
        {
            return Failed(error!);
        }

        var (source, refused) = access.Computer(line.Computer, forceRedacted: false);
        if (source is null)
        {
            return Failed(refused!);
        }

        var filter = new TaskFilter(line.Folder, true, line.State == "all" ? null : line.State, line.Words.Length > 0 ? line.Words : null, line.Microsoft);
        var result = await access.ReadAsync(source, line.Folder, session => Llm.Tools.TaskListTool.Matches(session, source, filter), cancellationToken).ConfigureAwait(false);
        if (result.Error is { } failed)
        {
            return Failed(failed);
        }

        access.Saw(source, result.Value.All);

        var lines = new List<string> { TaskText.CommandHeader(source.Where, line.Folder, result.Value.Matched.Count) };
        lines.AddRange(result.Value.Matched.Select(t => Llm.Tools.UntrustedText.Unfenced(TaskRender.Line(t))));
        return new TaskCommandResult(lines, false);
    }

    /// <summary>
    /// <c>/tasks</c>' argument list: the filter words; after <c>computer:</c> the <paramref name="computers"/>, after <c>state:</c> the states,
    /// and after <c>folder:</c> the folders the last list read on the computer typed (this PC without one) found (<paramref name="folders"/>,
    /// 2026-10-27; only <c>folder:\</c> before a read). A folder with a blank is offered quoted, as <see cref="Parse"/> reads it, and the word
    /// being typed runs past a blank inside an open quote, matched with the quotes left out (2026-10-27, the review). Pure.
    /// </summary>
    public static IReadOnlyList<CompletionItem> Complete(string argText, IReadOnlyList<string> computers, Func<string?, IReadOnlyList<string>>? folders = null)
    {
        ArgumentNullException.ThrowIfNull(argText);
        int start = WordStart(argText);
        string head = argText[..start];
        string current = argText[start..];
        string? computer = head.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault(w => w.StartsWith("computer:", StringComparison.OrdinalIgnoreCase))?["computer:".Length..];
        if (current.StartsWith("folder:", StringComparison.OrdinalIgnoreCase))
        {
            string[] offered = ["folder:\\", .. (folders?.Invoke(computer) ?? []).Select(f => "folder:" + CompletionWord.Quoted(f))];
            string typed = current.Replace("\"", "", StringComparison.Ordinal);
            return offered.Any(f => string.Equals(f.Replace("\"", "", StringComparison.Ordinal), typed, StringComparison.OrdinalIgnoreCase))
                ? []
                : [.. offered.Where(f => CompletionWord.StartsQuoteBlind(f, current)).Select(f => new CompletionItem(head + f, ""))];
        }

        IEnumerable<string> pool = current.StartsWith("computer:", StringComparison.OrdinalIgnoreCase) ? computers.Select(c => "computer:" + c)
            : current.StartsWith("state:", StringComparison.OrdinalIgnoreCase) ? States.Select(s => "state:" + s)
            : ["computer:", "folder:\\", "state:", "microsoft"];
        return MentionCompleter.Matches(pool.Select(p => new CompletionItem(head + p, "")).ToList(), argText);
    }

    // Where the word being typed starts: after the last blank outside a double quote.
    private static int WordStart(string text)
    {
        bool quoted = false;
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '"')
            {
                quoted = !quoted;
            }
            else if (text[i] == ' ' && !quoted)
            {
                start = i + 1;
            }
        }

        return start;
    }

    private static TaskCommandResult Failed(string error) => new([error.Replace("Error: ", "", StringComparison.Ordinal)], true);

    /// <summary>The draft a task's <c>ask</c> leaves. Pinned.</summary>
    /// <remarks>The computer's own path fenced (2026-10-23, the admin review's L9).</remarks>
    public static string AskDraft(string path, Computer<TaskComputerConfig> computer) =>
        "Explain the scheduled task " + UntrustedText.Fenced(UntrustedText.Cut(UntrustedText.Name(path), 300)) + (computer.IsLocal ? "" : " on " + computer.Name) + ": what it runs, when, and how its last run went";

    /// <summary>
    /// The <c>/eventlog</c> line a task's History button opens: the Task Scheduler's log, filtered to the task's path. The path and the computer
    /// quoted (2026-10-23, the admin review's E8: a remote task named <c>\Ops\Sync computer:dc02</c> read its history from dc02, and
    /// <c>since:soon</c> in a name broke the line); a double quote, which no task name holds, is dropped.
    /// </summary>
    public static string HistoryLine(string path, string? eventLogComputer) =>
        TaskText.HistoryLog + (eventLogComputer is { } c ? " computer:" + Quoted(c) : "") + " level:all " + Quoted(path);

    private static string Quoted(string word) => "\"" + word.Replace("\"", "", StringComparison.Ordinal) + "\"";
}
