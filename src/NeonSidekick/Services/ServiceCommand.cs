using NeonSidekick.Computers;
using NeonSidekick.Llm.Tools;
using NeonSidekick.UI;

namespace NeonSidekick.Services;

/// <summary>What a <c>/services</c> line asks for: a computer, a state tab, and words to filter by.</summary>
public sealed record ServiceCommandArgs(string? Computer, string State, string Words);

/// <summary>What <c>/services</c> came to as lines (headless, the Web UI, a screen without the pane).</summary>
public sealed record ServiceCommandResult(IReadOnlyList<string> Lines, bool Failed);

/// <summary>
/// <c>/services [computer:x] [state:running|stopped|failed|all] [words]</c> (2026-10-15, the admin-tools plan): the user's own hand over a
/// computer's services. On the pane it opens <c>App/ServicesMenu</c>; elsewhere the matching services as lines. Never judged by
/// <c>Service tools</c>; the computer must be ticked, and its visible list applies. Full text, never fenced: it is the user's view.
/// </summary>
public static class ServiceCommand
{
    public const string Word = "/services";

    /// <summary>The state words, in the pane's tab order.</summary>
    public static readonly IReadOnlyList<string> States = ["running", "stopped", "failed", "all"];

    public static (ServiceCommandArgs? Args, string? Error) Parse(string args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? computer = null;
        string state = "running";
        var words = new List<string>();
        // Quoted words kept whole (2026-10-23, the admin review's P3: computer:"…" and a display name with spaces), as /eventlog reads them.
        foreach (string word in EventLog.EventLogCommand.Words(args))
        {
            if (word.StartsWith("computer:", StringComparison.OrdinalIgnoreCase))
            {
                computer = word[9..];
            }
            else if (word.StartsWith("state:", StringComparison.OrdinalIgnoreCase))
            {
                state = word[6..].ToLowerInvariant();
                if (!States.Contains(state))
                {
                    return (null, ServiceText.BadState(state).Replace("Error: ", "", StringComparison.Ordinal));
                }
            }
            else
            {
                words.Add(word);
            }
        }

        return (new ServiceCommandArgs(computer, state, string.Join(' ', words)), null);
    }

    public static async Task<ServiceCommandResult> RunAsync(ServiceAccess access, string args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        if (!OperatingSystem.IsWindows() || access.System is null)
        {
            return Failed(ServiceText.NoneHere);
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

        var filter = new ServiceFilter(line.State == "all" ? null : line.State, null, line.Words.Length > 0 ? line.Words : null);
        var result = await access.ReadAsync(source, null, session => Llm.Tools.ServiceListTool.Matches(session, source, filter), cancellationToken).ConfigureAwait(false);
        if (result.Error is { } failed)
        {
            return Failed(failed);
        }

        var lines = new List<string> { ServiceText.CommandHeader(source.Where, result.Value.Matched.Count, line.State) };
        lines.AddRange(result.Value.Matched.Select(PlainLine));
        return new ServiceCommandResult(lines, false);
    }

    /// <summary>A service as the user's view lists it: the name, the display name, the state.</summary>
    public static string PlainLine(ServiceEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return Llm.Tools.UntrustedText.Unfenced(ServiceRender.Line(entry));
    }

    public static IReadOnlyList<CompletionItem> Complete(string argText, IReadOnlyList<string> computers)
    {
        ArgumentNullException.ThrowIfNull(argText);
        int last = argText.LastIndexOf(' ');
        string head = argText[..(last + 1)];
        string current = argText[(last + 1)..];
        IEnumerable<string> pool = current.StartsWith("computer:", StringComparison.OrdinalIgnoreCase) ? computers.Select(c => "computer:" + c)
            : current.StartsWith("state:", StringComparison.OrdinalIgnoreCase) ? States.Select(s => "state:" + s)
            : ["computer:", "state:"];
        return MentionCompleter.Matches(pool.Select(p => new CompletionItem(head + p, "")).ToList(), argText);
    }

    private static ServiceCommandResult Failed(string error) => new([error.Replace("Error: ", "", StringComparison.Ordinal)], true);

    /// <summary>The draft a service's <c>ask</c> leaves. Pinned.</summary>
    /// <remarks>The computer's own name fenced (2026-10-23, the admin review's L9: pasted raw into the user's own message).</remarks>
    public static string AskDraft(string name, Computer<ServiceComputerConfig> computer) =>
        "Explain the service " + UntrustedText.Fenced(UntrustedText.Cut(UntrustedText.Name(name), 200)) + (computer.IsLocal ? "" : " on " + computer.Name) + " and whether it is healthy";
}
