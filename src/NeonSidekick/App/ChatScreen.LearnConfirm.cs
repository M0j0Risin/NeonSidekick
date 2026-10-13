using System.Collections.Concurrent;
using NeonSidekick.Diagnostics;
using NeonSidekick.Skills;
using NeonSidekick.UI;

namespace NeonSidekick.App;

/// <summary>
/// <c>Reflection confirm mode</c> on the screen (2026-10-12, the user's ask: whether a reflection asks before it adds or changes a skill).
/// A reflection runs on the pool, so its <c>skill_editor</c> cannot take the keys: its ask (<see cref="LearnConfirm"/>) goes into
/// <see cref="_learnAsks"/> with an alert, the idle read is cut short as for a learn notice (the draft kept), and the loop top shows each
/// ask on the pane (<see cref="AnswerLearnAsksAsync"/>) — Keep, View, Discard — while the reflection waits. Under a reply or a botchat the
/// ask waits for the idle line. A reflection cancelled while it waits (Ctrl+L, the 🧠's double-click, a pause for a turn, the exit) takes
/// its ask with it; one paused and run again asks again. Without the pane there is no one to ask: the write is declined at once.
/// </summary>
internal sealed partial class ChatScreen
{
    /// <summary>One reflection write waiting for the user: what it would write, and the answer the reflection awaits.</summary>
    private sealed record LearnAsk(SkillProposal Proposal, TaskCompletionSource<bool> Answer);

    private readonly ConcurrentQueue<LearnAsk> _learnAsks = new();

    /// <summary>The ask's three rows, Keep first. Pinned.</summary>
    public static readonly IReadOnlyList<string> LearnAskRows = ["Keep", "View", "Discard"];

    /// <summary>The ask's keys. Pinned.</summary>
    public const string LearnAskKeys = "Enter = choose · ESC = discard";

    /// <summary>The ask's title: <c>🧠 A reflection wants to create skill “haiku”</c>, <c>… update skill “haiku”</c>, <c>… change notes.md in skill “haiku”</c>. Pinned.</summary>
    public static string LearnAskTitle(SkillProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        string what = proposal.Kind == SkillChangeKind.New ? "create skill"
            : proposal.Path is { Length: > 0 } path ? "change " + path + " in skill"
            : "update skill";
        return LearnGlyph + "A reflection wants to " + what + " “" + proposal.Name + "”";
    }

    /// <summary>The ask's caption: the model's own summary, else the description it would write; null for neither.</summary>
    public static string? LearnAskCaption(SkillProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        return proposal.Summary.Length > 0 ? proposal.Summary : string.IsNullOrWhiteSpace(proposal.Description) ? null : proposal.Description.Trim();
    }

    /// <summary>
    /// The proposal whole, for View: a new skill's description and instructions; an update's new description and instructions (or
    /// <c>(unchanged)</c>) with the SKILL.md as it stands under them; a file written whole; an edit's old and new text. Pinned.
    /// </summary>
    public static string LearnAskDetail(SkillProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        const string Unchanged = "(unchanged)";
        var lines = new List<string>();
        if (proposal.Path is { Length: > 0 } path)
        {
            lines.Add("File: " + path);
            if (proposal.Content is { } content)
            {
                lines.Add("");
                lines.Add(content);
            }
            else
            {
                lines.Add("");
                lines.Add("Replace:");
                lines.Add(proposal.OldText ?? "");
                lines.Add("");
                lines.Add("With:");
                lines.Add(proposal.NewText ?? "");
            }
        }
        else
        {
            lines.Add("Description: " + (proposal.Description?.Trim() ?? Unchanged));
            lines.Add("");
            lines.Add("Instructions:");
            lines.Add(proposal.Instructions?.TrimEnd() ?? Unchanged);
            if (proposal.Before is { } before)
            {
                lines.Add("");
                lines.Add("The SKILL.md as it stands:");
                lines.Add(before.TrimEnd());
            }
        }

        return string.Join('\n', lines);
    }

    /// <summary>The notice for a reflection whose write the user discarded: <c>(🧠 discarded: the new skill 'haiku')</c>. Pinned.</summary>
    public static string LearnDeclinedNotice(SkillEditResult declined)
    {
        ArgumentNullException.ThrowIfNull(declined);
        bool created = string.Equals(declined.Detail, Llm.Tools.SkillEditorTool.CreateAction, StringComparison.Ordinal);
        return "(" + LearnGlyph + "discarded: " + (created ? "the new skill '" : "the change to skill '") + declined.Name + "')";
    }

    /// <summary>The log line when a reflection's write had no one to confirm it (no pane). Pinned.</summary>
    public static string LearnNoOneToAskLogLine(SkillProposal proposal) =>
        "Reflection confirm mode: no pane to ask on, so the " + (proposal.Kind == SkillChangeKind.New ? "new skill '" : "change to skill '") + proposal.Name + "' is declined.";

    /// <summary>
    /// The editor's ask for a reflection decided under <paramref name="scope"/>: null for <see cref="ReflectionConfirmScope.None"/> (no ask
    /// at all), else yes at once for a kind the scope leaves be, no at once without the pane, and otherwise the user's answer at the idle
    /// line, awaited under the reflection's token (its cancel withdraws the ask).
    /// </summary>
    private Func<SkillProposal, CancellationToken, Task<bool>>? LearnConfirm(ReflectionConfirmScope scope) =>
        scope == ReflectionConfirmScope.None ? null : async (proposal, cancellationToken) =>
        {
            if (!ReflectionConfirmMode.Asks(scope, proposal.Kind))
            {
                return true;
            }

            if (!_pane.Enabled)
            {
                DiagnosticLog.Info(SkillCatalog.Category, LearnNoOneToAskLogLine(proposal));
                return false;
            }

            var ask = new LearnAsk(proposal, new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
            using var withdraw = cancellationToken.Register(() => ask.Answer.TrySetCanceled(cancellationToken));
            _learnAsks.Enqueue(ask);
            SignalAlert();
            return await ask.Answer.Task.ConfigureAwait(false);
        };

    /// <summary>The ask's pane closed under the user because the reflection was cancelled meanwhile (2026-10-13, the code review). Pinned.</summary>
    public const string LearnAskWithdrawnNotice = "(" + LearnGlyph + "the reflection was cancelled: nothing was written)";

    /// <summary>
    /// The loop top's turn for the asks (2026-10-12): each waiting one on the pane — its title, the summary as caption, Keep / View / Discard;
    /// View shows the proposal whole on the info pane and comes back; ESC is Discard. One the reflection withdrew meanwhile is skipped.
    /// </summary>
    private async Task AnswerLearnAsksAsync(CancellationToken cancellationToken)
    {
        while (_learnAsks.TryDequeue(out var ask))
        {
            if (ask.Answer.Task.IsCompleted)
            {
                continue;   // withdrawn: the reflection was cancelled while it waited
            }

            bool keep = false;
            // The pane goes with the ask (2026-10-13, the code review): a reflection cancelled while it waits (a reconnect, Ctrl+L) closes it
            // and says nothing was written, rather than leave a Keep that would be dropped.
            using var withdrawn = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _ = ask.Answer.Task.ContinueWith(static (_, state) =>
            {
                try
                {
                    ((CancellationTokenSource)state!).Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // answered here: the source is gone with the pane
                }
            }, withdrawn, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            try
            {
                while (!ask.Answer.Task.IsCompleted)
                {
                    var page = new MenuPage(LearnAskTitle(ask.Proposal), LearnAskRows, LearnAskKeys) { Caption = LearnAskCaption(ask.Proposal) };
                    int? picked = await _menu.ChooseAsync(page, withdrawn.Token).ConfigureAwait(false);
                    if (picked == 1)
                    {
                        string detail = LearnAskDetail(ask.Proposal);
                        await _info.ShowAsync(LearnAskTitle(ask.Proposal), [new InfoTab(LearnAskRows[1], () => new Spectre.Console.Text(detail))], 0, withdrawn.Token).ConfigureAwait(false);
                        continue;
                    }

                    keep = picked == 0;
                    break;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Withdrawn while it was shown.
            }
            finally
            {
                _menuPane.Close();
                if (!ask.Answer.TrySetResult(keep) && ask.Answer.Task.IsCanceled && !cancellationToken.IsCancellationRequested)
                {
                    _transcript.Notice(LearnAskWithdrawnNotice);
                }
            }
        }
    }
}
