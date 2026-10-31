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

    /// <summary><see cref="SlashCommands.Parse(string, Func{string, bool}?)"/> with this run's custom commands (the catalog's recent scan).</summary>
    private (SlashCommand Command, string Args) ParseWithCustom(string text) => SlashCommands.Parse(text, _customCommands.Knows);

    /// <summary>
    /// A custom command at the idle line: found afresh (a file gone since the parse is <see cref="UnknownCommandError"/>), expanded
    /// (<see cref="CustomCommandsCommand.Prompt"/>; blank is its error), then, with a model connected, the 🪄 notice naming where the prompt came
    /// from and the prompt sent as a message with the line's pictures (<see cref="RunMessageAsync"/>), the typed line already the user row.
    /// With no model <see cref="NoAssistantError"/> alone, never a notice for a prompt that went nowhere. One log line, never the prompt.
    /// </summary>
    private async Task<bool> RunCustomCommandAsync(string text, string args, IReadOnlyList<ImageAttachment> images, CancellationToken cancellationToken)
    {
        string word = SlashCommands.CustomWord(text, out _);
        if (_customCommands.Scan().Find(word) is not { } command)
        {
            _transcript.Error(UnknownCommandError(word));
            return false;
        }

        var (prompt, error) = CustomCommandsCommand.Prompt(command, args);
        if (prompt is null)
        {
            _transcript.Error(error!);
            return false;
        }

        if (_session.Assistant is null)
        {
            _transcript.Error(NoAssistantError);
            return false;
        }

        _transcript.Notice(CustomCommandText.SentNotice(command));
        DiagnosticLog.Debug(AppCategory, CustomCommandText.RunLogLine(command, prompt.Length));
        _customCommandTitle = text.Trim();
        try
        {
            return await RunMessageAsync(prompt, images, cancellationToken).ConfigureAwait(false);
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
                if (await _commandsMenu.ShowAsync(cancellationToken).ConfigureAwait(false) is { } use)
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

                sink.Notice(answer);
                try
                {
                    _openFile(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    sink.Error(CustomCommandText.OpenFailedError(ex.Message));
                }

                return;
            }

            case CustomCommandsVerb.Save:
            {
                var (answer, failed) = CustomCommandsCommand.Save(CustomCommandRoots.For(_settings), line, _session.History.Messages);
                if (failed)
                {
                    sink.Error(answer);
                }
                else
                {
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
    /// (<see cref="CustomCommandEditor.Save"/>), the answer naming the new word and the file, with the override and <c>\$</c> notes; a
    /// refusal as <c>Error: </c> (the pane's own prefix).
    /// </summary>
    private string SavePromptAsCommand(CustomCommandScope scope, string name, string text)
    {
        var roots = CustomCommandRoots.For(_settings);
        var result = CustomCommandEditor.Save(roots, scope, name, text);
        if (result.Outcome != CustomCommandEditOutcome.Saved)
        {
            return "Error: " + CustomCommandsMenu.Refusal(result);
        }

        return CustomCommandText.SavedNotice(name, result.Path, CustomCommandText.FirstLine(text))
            + CustomCommandsMenu.OverrideNote(roots, scope, name)
            + (result.MayReadDifferently ? " " + CustomCommandText.SavedMayReadDifferently : "");
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

    /// <summary>The custom command <c>/help &lt;words&gt;</c> names, from a fresh scan (<see cref="CustomCommandsCommand.HelpCard"/>); null otherwise.</summary>
    private CustomCommand? CustomHelpCard(string args) => CustomCommandsCommand.HelpCard(_customCommands.Scan(), args);

    /// <summary>
    /// A custom command's <c>/help</c> card drawn as the manual's own answer (<see cref="HelpLookupAsync"/> with no model): a reply under
    /// <see cref="TranscriptRenderer.HelpGlyph"/>, never in the history, so the model never reads it and <c>/rewind</c> doesn't offer it.
    /// </summary>
    private void ShowCustomHelpCard(CustomCommand command)
    {
        _transcript.BeginAssistant(StyledReply(_effective().TranscriptMarkdown, _pane.Enabled), TranscriptRenderer.HelpGlyph);
        _transcript.AppendDelta(CustomCommandText.Card(command));
        _transcript.EndAssistant();
    }

    /// <summary>
    /// Files a built-in hides, named once a run (and again after one changes), at the idle line's top: startup, after a profile switch,
    /// after <c>/commands</c> closes. From a recent scan.
    /// </summary>
    private void AnnounceShadowedCommands()
    {
        _customCommands.Recent();
        if (_customCommands.TakeNewlyShadowedByBuiltIn() is { Count: > 0 } shadows)
        {
            _transcript.Notice(CustomCommandText.ShadowedByBuiltInNotice(shadows));
        }
    }

    /// <summary>
    /// <c>/help</c>'s Custom tab (2026-10-30): each command's word, description and form (the word and its hint, in the Commands tabs' two
    /// tones), then the Shadowed and Skipped blocks with their reasons; an empty list a line pointing at <c>/commands</c>. Text cells, never
    /// Markup: a hint is <c>[issue]</c>. Pure over <paramref name="scan"/>.
    /// </summary>
    internal static IRenderable CustomCommandsTab(CustomCommandScan scan)
    {
        ArgumentNullException.ThrowIfNull(scan);
        if (scan.Commands.Count == 0 && scan.Shadowed.Count == 0 && scan.Problems.Count == 0)
        {
            return new Text(CustomCommandText.HelpEmptyRow, Theme.DimText);
        }

        var grid = new Grid()
            .AddColumn(new GridColumn().NoWrap().Width(SlashCommands.LabelWidth).PadRight(SlashCommands.HelpColumnGap))
            .AddColumn(new GridColumn().Width(SlashCommands.DescriptionWidth).PadRight(SlashCommands.HelpColumnGap))
            .AddColumn(new GridColumn().PadRight(0));
        foreach (var command in scan.Commands)
        {
            string scope = CustomCommandText.ScopeName(command.Scope);
            string description = command.Description is { Length: > 0 } said ? said + " · " + scope : scope;
            string form = command.ArgumentHint is { Length: > 0 } hint ? command.Word + " " + hint : command.Word;
            grid.AddRow(new Text(command.Word, Theme.AccentSecondary), new Text(description, Theme.Body), FormsCell([form]));
        }

        if (scan.Shadowed.Count > 0)
        {
            grid.AddRow(new Text(CustomCommandText.ShadowedHeading, Theme.SectionHeading), Text.Empty, Text.Empty);
            foreach (var shadow in scan.Shadowed)
            {
                grid.AddRow(new Text(shadow.Command.Word, Theme.DimText), new Text(CustomCommandText.ShadowReason(shadow), Theme.DimText), new Text(CustomCommandText.ScopeName(shadow.Command.Scope), Theme.DimText));
            }
        }

        if (scan.Problems.Count > 0)
        {
            grid.AddRow(new Text(CustomCommandText.SkippedHeading, Theme.SectionHeading), Text.Empty, Text.Empty);
            foreach (var problem in scan.Problems)
            {
                grid.AddRow(new Text(Path.GetFileName(problem.FilePath), Theme.DimText), new Text(problem.Problem, Theme.DimText), new Text(CustomCommandText.ScopeName(problem.Scope), Theme.DimText));
            }
        }

        return grid;
    }
}
