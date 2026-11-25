using NeonSidekick.Git;
using NeonSidekick.Llm.Tools;
using NeonSidekick.UI;

namespace NeonSidekick.Audits;

/// <summary>
/// The recent commits <c>/code-audit commit </c> offers (2026-11-25, the user's ask): the last <see cref="Count"/> on HEAD as short sha and
/// subject, read through <paramref name="log"/> and kept until the working folder or HEAD's tip (<paramref name="tip"/>, one cheap read) changes.
/// The log's topological sort can walk far more than <see cref="Count"/> commits before the first comes out, and completion asks on every key,
/// so the walk runs only when one of those changed. Called only on the screen's task (the PC's input, and the Web UI snapshot read at the idle
/// line's top), so it holds no lock. No repository, an unborn HEAD or a git error: an empty list.
/// </summary>
public sealed class AuditCommitList(Func<string?> tip, Func<int, GitLogReport> log)
{
    /// <summary>How many commits the list offers.</summary>
    public const int Count = 10;

    /// <summary>The most cells a subject's note takes.</summary>
    public const int NoteCells = 60;

    private (string Folder, string Tip)? _key;
    private IReadOnlyList<CompletionItem> _items = [];

    /// <summary>The list for the working folder <paramref name="folder"/>: read again only when it or HEAD's tip changed.</summary>
    public IReadOnlyList<CompletionItem> For(string folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        if (tip() is not { } head)
        {
            _key = null;
            _items = [];
            return _items;
        }

        if (_key is { } kept && kept.Folder == folder && kept.Tip == head)
        {
            return _items;
        }

        var report = log(Count);
        // A subject is the author's words: one scrubbed line, cut to fit beside the sha.
        _items = report.Outcome == GitOutcome.Ok
            ? report.Commits.Select(c => new CompletionItem(c.Short, TextCells.Cut(UntrustedText.OneLine(c.Subject), NoteCells))).ToList()
            : [];
        _key = (folder, head);
        return _items;
    }
}
