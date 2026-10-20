using NeonSidekick.Diagnostics;
using NeonSidekick.Sessions;
using NeonSidekick.UI;

namespace NeonSidekick.App;

// ── /mark (2026-10-18) ────────────────────────────────────────────────────

internal sealed partial class ChatScreen
{
    // The rules' mark in force (one of RuleMarks.Names), kept in the session row and back on resume; null for the theme's rules.
    // Cleared with a conversation left (ForgetSession's left part), kept by a purge of the row and by /rewind. The screen's task only.
    private string? _markName;

    /// <summary>
    /// <c>/mark &lt;color&gt;|default</c> (2026-10-18, the user's ask and picks: a window told apart at a glance): the rules around the draft line
    /// drawn in one of <see cref="RuleMarks.Names"/> (<see cref="ScreenPane.RuleMark"/>), or the theme's again; bare, the mark in force and the
    /// choices. At the idle line the session row is saved at once (when there is one); under a reply (<paramref name="midTurn"/>) it is set
    /// and redrawn only, and the turn's own save keeps it. The PC's alone: the page has no rules, and headless never touches it.
    /// </summary>
    private void HandleMark(string args, bool midTurn)
    {
        string word = args.Trim();
        if (word.Length == 0)
        {
            _transcript.Notice(MarkText.ShowLine(_markName));
            return;
        }

        string? name;
        if (string.Equals(word, RuleMarks.Default, StringComparison.OrdinalIgnoreCase))
        {
            name = null;
        }
        else if (RuleMarks.TryParse(word, out string parsed))
        {
            name = parsed;
        }
        else
        {
            _transcript.Error(MarkText.UnknownError(word));
            return;
        }

        SetMark(name);
        _transcript.Notice(name is null ? MarkText.DefaultNotice : MarkText.SetNotice(name));
        if (!midTurn && _session.Assistant is { } assistant)
        {
            SaveSessionHistory(assistant);
        }
    }

    /// <summary>The mark in force and on the rules (the next tick redraws them).</summary>
    private void SetMark(string? name)
    {
        _markName = name;
        _pane.RuleMark = RuleMarks.StyleOf(name);
    }

    /// <summary>A restored row's mark (2026-10-18): a name it no longer knows is dropped with a Debug line; none takes the mark off.</summary>
    private void RestoreMark(string? stored)
    {
        string? name = RuleMarks.TryParse(stored, out string known) ? known : null;
        if (stored is not null && name is null)
        {
            DiagnosticLog.Debug(SessionsCategory, $"session mark '{stored}' is no color /mark knows; dropped");
        }

        SetMark(name);
    }

    /// <summary>The <c>/mark</c> list: the colors, then <c>default</c>.</summary>
    private static IReadOnlyList<CompletionItem> MarkChoices() =>
        [.. RuleMarks.Names.Select(name => new CompletionItem(name, MarkText.ChoiceNote(name))), new(RuleMarks.Default, MarkText.DefaultChoiceNote)];
}
