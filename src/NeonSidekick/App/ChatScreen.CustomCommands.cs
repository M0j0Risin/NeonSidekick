using Microsoft.Extensions.AI;
using NeonSidekick.CustomCommands;
using NeonSidekick.Diagnostics;
using NeonSidekick.Files;
using NeonSidekick.UI;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace NeonSidekick.App;

// ── Custom commands (2026-10-30, plan i-want-to-talk-golden-sunbeam) ──────

/// <summary>
/// The user's own slash commands, Claude Code's way: a Markdown file per command in the profile's or the home's <c>commands</c> folder
/// (<see cref="CustomCommandCatalog"/>), its body the prompt, filled from what is typed after the word (<see cref="CustomCommandTemplate"/>).
/// A built-in always wins. <c>/commands</c> is the pane (<see cref="CustomCommandsMenu"/>) and <c>new</c>/<c>save</c> typed; <c>/help</c> has a
/// Custom tab and a card for one command.
/// </summary>
internal sealed partial class ChatScreen
{
    // The two folders' commands, read at each scan (a profile switch needs no rebuild); Kestrel threads read it too, behind its own lock.
    private readonly CustomCommandCatalog _customCommands;

    // The /commands pane, built once beside /skills'; its lines go through the flow sink.
    private readonly CustomCommandsMenu _commandsMenu;

    // A use picked on /commands' pane run on the watcher (under a reply): the word put on the row after the pane phase
    // (TakeCommandsDraft, beside TakePromptPick). Watcher-only.
    private string? _commandsDraft;

    // The typed line a running custom command titles a new session with (LogTurn), set for its RunMessageAsync alone.
    private string? _customCommandTitle;

    // Whether the idle line's top names the files a built-in hides again (2026-10-31, review P1-8/P1-9: it scanned at every pass): set at
    // start and by CommandsChanged, read by the screen's task; the pane may set it from the watcher.
    private volatile bool _commandsAnnounceDue = true;

    // The folders the last announcement read, so a profile switch announces its own folders with no site of its own. Screen task only.
    private CustomCommandRoots? _announcedRoots;

    /// <summary><see cref="SlashCommands.Parse(string, Func{string, bool}?)"/> with this run's custom commands (the catalog's recent scan).</summary>
    private (SlashCommand Command, string Args) ParseWithCustom(string text) => SlashCommands.Parse(text, _customCommands.Knows);

    /// <summary>
    /// A custom command at the idle line: found and expanded (<see cref="CustomCommandsCommand.Resolve"/>: a file gone since the parse is
    /// <see cref="CustomCommandText.NotFound"/>, 2026-10-31, review P1-11; a blank prompt its error), then, with a model connected, the 🪄
    /// notice naming where the prompt came from and the prompt sent as a message with the line's pictures (<see cref="RunMessageAsync"/>), the
    /// typed line already the user row and on the message (<see cref="Llm.TurnMark"/>, review P1-5). With no model
    /// <see cref="NoAssistantError"/> alone, never a notice for a prompt that went nowhere. One log line, never the prompt.
    /// </summary>
    private async Task<bool> RunCustomCommandAsync(string text, IReadOnlyList<ImageAttachment> images, CancellationToken cancellationToken)
    {
        var run = CustomCommandsCommand.Resolve(_customCommands, text);
        if (run is not { Command: { } command, Prompt: { } prompt })
        {
            _transcript.Error(run.Error!);
            return false;
        }

        if (_session.Assistant is null)
        {
            _transcript.Error(NoAssistantError);
            return false;
        }

        _transcript.Notice(CustomCommandText.SentNotice(command));
        DiagnosticLog.Debug(CustomCommandCatalog.Category, CustomCommandText.RunLogLine(command, prompt.Length));
        _customCommandTitle = text.Trim();
        try
        {
            return await RunMessageAsync(prompt, images, cancellationToken, Llm.TurnMark.TypedAs(_customCommandTitle, prompt)).ConfigureAwait(false);
        }
        finally
        {
            _customCommandTitle = null;
        }
    }

    /// <summary>The typed line a custom command's first turn titles its session with, taken once (<see cref="LogTurn"/>); null for any other turn.</summary>
    private string? TakeCustomCommandTitle()
    {
        string? title = _customCommandTitle;
        _customCommandTitle = null;
        return title;
    }

    /// <summary>
    /// <c>/commands</c> (<see cref="CustomCommandsCommand.Parse"/>): bare, the pane (its lines without one), a <c>use</c> putting the word on the
    /// draft — at once at the idle line, kept for the hook on the watcher (<paramref name="onWatcher"/>); <c>new</c> the template written and
    /// opened in the editor; <c>save</c> the last message sent saved as the command's body. <paramref name="sink"/> is the flow sink under a
    /// reply, the transcript at the idle line.
    /// </summary>
    private async Task HandleCommandsAsync(string args, INoticeSink sink, CancellationToken cancellationToken, bool onWatcher = false)
    {
        var line = CustomCommandsCommand.Parse(args);
        switch (line.Verb)
        {
            case CustomCommandsVerb.Pane:
                var use = await _commandsMenu.ShowAsync(cancellationToken).ConfigureAwait(false);
                CommandsChanged();
                if (use is not null)
                {
                    if (onWatcher)
                    {
                        _commandsDraft = use;
                    }
                    else
                    {
                        FillDraft(use, keepOld: true);
                    }
                }

                return;
            case CustomCommandsVerb.New:
            {
                var (answer, path) = CustomCommandsCommand.Create(CustomCommandRoots.For(_settings), line);
                if (path is null)
                {
                    sink.Error(answer);
                    return;
                }

                CommandsChanged();
                sink.Notice(answer);
                CustomCommandsMenu.OpenInEditor(sink, _openFile, path, opened: null);
                return;
            }

            case CustomCommandsVerb.Save:
            {
                // Under a reply the turn is appending to the history: the message it published, never a walk (2026-11-02, review P13-1).
                IReadOnlyList<ChatMessage> messages = _replyRunning
                    ? _session.Assistant?.LastUserMessage is { } sent ? [sent] : []
                    : _session.History.Messages;
                var (answer, failed) = CustomCommandsCommand.Save(CustomCommandRoots.For(_settings), line, messages);
                if (failed)
                {
                    sink.Error(answer);
                }
                else
                {
                    CommandsChanged();
                    sink.Notice(answer);
                }

                return;
            }

            default:
                sink.Error(line.Error ?? CustomCommandText.Usage);
                return;
        }
    }

    /// <summary>
    /// <c>/prompts</c>' as-command button (2026-10-30): the prompt's text saved as <paramref name="name"/>'s body
    /// (<see cref="CustomCommandEditor.Save"/>), the answer <c>/commands save</c>'s (<see cref="CustomCommandsCommand.SavedText"/>, 2026-10-31,
    /// review P7-5); a refusal behind <see cref="Prompts.SavedPromptText.ErrorPrefix"/> (the pane's own prefix, review P3-10).
    /// </summary>
    private string SavePromptAsCommand(CustomCommandScope scope, string name, string text)
    {
        var roots = CustomCommandRoots.For(_settings);
        var result = CustomCommandEditor.Save(roots, scope, name, text);
        if (result.Outcome != CustomCommandEditOutcome.Saved)
        {
            return Prompts.SavedPromptText.ErrorPrefix + CustomCommandsMenu.Refusal(result);
        }

        CommandsChanged();
        return CustomCommandsCommand.SavedText(roots, result, text, picturesDropped: false);
    }

    /// <summary>
    /// The command files changed by the app's hand (2026-10-31, review P1-6/P3-8, P1-8/P1-9): the catalog reads afresh at its next look, so
    /// <c>/commands save sum</c> then <c>/sum</c> runs (the Web UI's names too), and the idle line's top names any file a built-in now hides.
    /// Called after the pane, <c>new</c>, <c>save</c>, <c>/prompts</c>' m and <c>/profile push|pull commands</c>.
    /// </summary>
    private void CommandsChanged()
    {
        _customCommands.Invalidate();
        _commandsAnnounceDue = true;
    }

    /// <summary>
    /// After a pane phase under a reply: a <c>use</c> picked on <c>/commands</c>' pane goes on the draft, the hook answering "kept" for a typed
    /// line so the live row does not empty itself over it (<see cref="TakePromptPick"/>'s to draft). True when the line is taken.
    /// </summary>
    private bool TakeCommandsDraft(bool typed)
    {
        if (_commandsDraft is not { } draft)
        {
            return true;
        }

        _commandsDraft = null;
        FillDraft(draft, keepOld: !typed);
        return !typed;
    }

    /// <summary>
    /// The card <c>/help &lt;words&gt;</c> names (<see cref="CustomCommandsCommand.HelpCard"/>): a custom command's, or a Skipped file's (2026-10-31,
    /// review P4-6); null otherwise. From a recent scan (review P1-8).
    /// </summary>
    private CustomHelpCard? FindCustomHelpCard(string args) => CustomCommandsCommand.HelpCard(_customCommands.Recent(), args);

    /// <summary>
    /// A custom command's <c>/help</c> card drawn as the manual's own answer (<see cref="HelpLookupAsync"/> with no model): a reply under
    /// <see cref="TranscriptRenderer.HelpGlyph"/>, never in the history, so the model never reads it and <c>/rewind</c> doesn't offer it. The
    /// card is Markdown where the reply is drawn as Markdown (review P3-6, <see cref="StyledReply"/>), so <c>__init__</c> reads as written; plain
    /// text where it isn't, as headless's, so no stray backslash shows. The Web UI gets the same text.
    /// </summary>
    private void ShowCustomHelpCard(CustomHelpCard card)
    {
        bool styled = StyledReply(_effective().TranscriptMarkdown, _pane.Enabled);
        _transcript.BeginAssistant(styled, TranscriptRenderer.HelpGlyph);
        _transcript.AppendDelta(CustomCommandText.Card(card, markdown: styled));
        _transcript.EndAssistant();
    }

    /// <summary>
    /// Files a built-in hides, named once a run (and again after one changes), at the idle line's top. Since 2026-10-31 (review P1-8/P1-9:
    /// it scanned at every pass) only when due: at start, after <see cref="CommandsChanged"/>, and when the folders differ from the last
    /// announcement's (a profile switch). A shadowed file made outside the app while it idles is named at the next of those, not at once.
    /// </summary>
    private void AnnounceShadowedCommands()
    {
        var roots = _customCommands.Roots;
        if (!_commandsAnnounceDue && roots == _announcedRoots)
        {
            return;
        }

        _commandsAnnounceDue = false;
        _announcedRoots = roots;
        _customCommands.Recent();
        if (_customCommands.TakeNewlyShadowedByBuiltIn() is { Count: > 0 } shadows)
        {
            _transcript.Notice(CustomCommandText.ShadowedByBuiltInNotice(shadows));
        }
    }

    /// <summary>
    /// <c>/help</c>'s Custom tab (2026-10-30): each command's word, description and form (the word and its hint, in the Commands tabs' two
    /// tones), then the Shadowed and Skipped blocks with their reasons (a Skipped file by its path under its folder, 2026-10-31, review P4-8),
    /// then the scan's note; an empty list a line pointing at <c>/commands</c>. From the lists' shared rows (<see cref="CustomCommandText.Rows"/>,
    /// review P6-7), cleaned. The word column is as wide as the longest word, wrapping when the window is narrower (review P2-4: a long
    /// <c>a:b</c> word was cut). Text cells, never Markup: a hint is <c>[issue]</c>. Pure over <paramref name="scan"/>.
    /// </summary>
    internal static IRenderable CustomCommandsTab(CustomCommandScan scan)
    {
        ArgumentNullException.ThrowIfNull(scan);
        var rows = CustomCommandText.Rows(scan);
        if (rows.Count == 0)
        {
            return scan.Note is { } alone
                ? new Rows(new Text(CustomCommandText.HelpEmptyRow, Theme.DimText), new Text(CustomCommandText.NotePrefix + alone, Theme.DimText))
                : new Text(CustomCommandText.HelpEmptyRow, Theme.DimText);
        }

        int longest = rows.Max(r => TextCells.Width(r.Word));
        var grid = new Grid()
            .AddColumn(new GridColumn().Width(Math.Max(SlashCommands.LabelWidth, longest)).PadRight(SlashCommands.HelpColumnGap))
            .AddColumn(new GridColumn().Width(SlashCommands.DescriptionWidth).PadRight(SlashCommands.HelpColumnGap))
            .AddColumn(new GridColumn().PadRight(0));
        foreach (var row in rows.Where(r => r.Kind == CustomCommandRowKind.Command))
        {
            string scope = CustomCommandText.ScopeName(row.Scope);
            string description = row.Detail.Length > 0 ? row.Detail + " · " + scope : scope;
            string form = row.Hint is { } hint ? row.Word + " " + hint : row.Word;
            grid.AddRow(new Text(row.Word, Theme.AccentSecondary), new Text(description, Theme.Body), FormsCell([form]));
        }

        foreach (var (kind, heading) in new[] { (CustomCommandRowKind.Shadow, CustomCommandText.ShadowedHeading), (CustomCommandRowKind.Problem, CustomCommandText.SkippedHeading) })
        {
            var block = rows.Where(r => r.Kind == kind).ToList();
            if (block.Count == 0)
            {
                continue;
            }

            grid.AddRow(new Text(heading, Theme.SectionHeading), Text.Empty, Text.Empty);
            foreach (var row in block)
            {
                grid.AddRow(new Text(row.Word, Theme.DimText), new Text(row.Detail, Theme.DimText), new Text(CustomCommandText.ScopeName(row.Scope), Theme.DimText));
            }
        }

        return scan.Note is { } note ? new Rows(grid, new Text(CustomCommandText.NotePrefix + note, Theme.DimText)) : grid;
    }
}
