using NeonSidekick.App;
using NeonSidekick.Prompts;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// The saved prompts on the screen (2026-10-13, the user's ask): <c>/prompts</c>' pane (Enter sends a prompt as if typed since 2026-10-16,
/// the user's ask, and <c>d</c> puts it on the draft line in place of what is there, as Enter did until then; <c>r</c> removes),
/// <c>/prompts &lt;n&gt;</c> (sends), <c>save</c>, <c>remove</c>, <c>list [--clear]</c>, the toolbar's 📣 and Ctrl+Alt+P (Ctrl+Alt+W until 2026-10-18; both keep the draft,
/// so the pane's save draft has it, and a send leaves it), the draft editor for a prompt of several lines, and the same under a reply, where
/// a send is queued and a to-draft pick must not be emptied by the live row.
/// </summary>
public sealed partial class ChatScreenTests
{
    private SavedPrompts SavedPromptsHere() => new(_settings.ProfileDirectory);

    private void SavePrompts(params string[] texts)
    {
        var prompts = SavedPromptsHere();
        foreach (string text in texts)
        {
            prompts.Add(text, out _);
        }
    }

    // Ctrl+Alt+P as the console delivers it: no character (Ctrl+Alt+W until 2026-10-18).
    private static readonly ConsoleKeyInfo CtrlAltP = new('\0', ConsoleKey.P, false, true, true);

    /// <summary>
    /// The pane, Enter on a prompt of two lines (2026-10-16, the user's ask): it is sent as it is, with no second Enter, and nothing is left
    /// on the draft (the next line typed, <c>/exit</c>, runs alone).
    /// </summary>
    [Fact]
    public async Task Prompts_ThePane_EnterSendsAPrompt_AsItIs()
    {
        _settings.Update(d => d.TtsOutput = false);
        _console.Profile.Height = 40;
        _console.Profile.Width = 160;
        _geometry = new ScreenGeometry(() => null);
        SavePrompts("Summarize this.", "Review the diff\nfor bugs");
        _chat.EnqueueText("Reviewed.");
        StepsWhenIdle(
            Line("/prompts"),
            Key(Keys.Down),
            Key(Keys.Enter),   // the second prompt sent
            Line("/exit"));

        string output = await RunAsync();

        Assert.Contains(SavedPromptText.PaneTitle, output);
        Assert.Contains(SavedPromptText.PaneCaption(2), output);
        Assert.Contains("2. Review the diff +1 line", output);
        Assert.Single(_chat.Requests);
        Assert.Equal("Review the diff\nfor bugs", UserText(_chat.Requests[0]));
    }

    /// <summary>The pane's <c>d</c> (2026-10-16): the old pick, the prompt on the draft (drawn on the input row), and Enter sends it as it is.</summary>
    [Fact]
    public async Task Prompts_ThePane_D_PutsAPromptOnTheDraft_WhichSendsAsItIs()
    {
        _settings.Update(d => d.TtsOutput = false);
        _console.Profile.Height = 40;
        _console.Profile.Width = 160;
        _geometry = new ScreenGeometry(() => null);
        SavePrompts("Summarize this.", "Review the diff\nfor bugs");
        _chat.EnqueueText("Reviewed.");
        StepsWhenIdle(
            Line("/prompts"),
            Key(Keys.Down),
            Key(Keys.Char(SavedPromptText.ToDraftKey)),   // the second prompt to the draft
            Key(Keys.Enter),                              // sent
            Line("/exit"));

        string output = await RunAsync();

        Assert.Contains(SavedPromptText.ToDraftButton, output);
        Assert.Contains(InputLine.PromptGlyph + "Review the diff", output);   // the draft on the input row before it was sent
        Assert.Single(_chat.Requests);
        Assert.Equal("Review the diff\nfor bugs", UserText(_chat.Requests[0]));
    }

    /// <summary>The pane's <c>r</c> (2026-10-16; <c>d</c> until then): the highlighted prompt removed after a yes.</summary>
    [Fact]
    public async Task Prompts_ThePane_R_RemovesAfterAYes()
    {
        _console.Profile.Height = 40;
        _console.Profile.Width = 160;
        _geometry = new ScreenGeometry(() => null);
        SavePrompts("One", "Two");
        StepsWhenIdle(
            Line("/prompts"),
            Key(Keys.Char(SavedPromptText.RemoveKey)),   // remove One
            Key(Keys.Down),                              // Yes
            Key(Keys.Enter),
            Key(Keys.Escape),                            // the pane closed
            Line("/exit"));

        string output = await RunAsync();

        Assert.Contains(SavedPromptText.RemovePrompt(new SavedPrompt("x", "One", default)), output);
        Assert.Equal(["Two"], SavedPromptsHere().List().Select(p => p.Text));
        Assert.Empty(_chat.Requests);
    }

    /// <summary>Delete with a filter typed removes the filtered prompt after a yes, and the filter stays (2026-10-16, the user's ask).</summary>
    [Fact]
    public async Task Prompts_ThePane_DeleteWithAFilter_RemovesTheFilteredPrompt_AndKeepsTheFilter()
    {
        _console.Profile.Height = 40;
        _console.Profile.Width = 160;
        _geometry = new ScreenGeometry(() => null);
        SavePrompts("One", "Two");
        StepsWhenIdle(
            Line("/prompts"),
            Key(new ConsoleKeyInfo('T', ConsoleKey.T, true, false, false)),   // the filter: Two alone
            Key(Keys.Delete),
            Key(Keys.Down),                                                   // Yes
            Key(Keys.Enter),
            Key(Keys.Escape),                                                 // the filter cleared
            Key(Keys.Escape),                                                 // the pane closed
            Line("/exit"));

        string output = await RunAsync();

        Assert.Equal(["One"], SavedPromptsHere().List().Select(p => p.Text));
        Assert.Contains(SavedPromptText.RemovePrompt(new SavedPrompt("x", "Two", default)), output);
        Assert.Contains(Spectre.Console.Markup.Remove(MenuFilter.NoMatchRow("T")), output);
        Assert.Empty(_chat.Requests);
    }

    /// <summary>A prompt that starts with <c>/</c> runs as the command it reads as (2026-10-16, the user's pick), never sent to the model.</summary>
    [Fact]
    public async Task Prompts_ASlashPrompt_RunsAsACommand()
    {
        SavePrompts("/prompts save Second.");
        StepsWhenIdle(
            Line("/prompts 1"),
            Line("/exit"));

        string output = await RunAsync();

        Assert.Equal(["/prompts save Second.", "Second."], SavedPromptsHere().List().Select(p => p.Text));
        Assert.Contains(SavedPromptText.Saved(new SavedPrompt("x", "Second.", default), 2), output);
        Assert.Empty(_chat.Requests);
    }

    /// <summary>Under Queue mode <c>manual</c> a sent prompt joins the queue, as a typed message does, for the <c>/queue</c> send.</summary>
    [Fact]
    public async Task Prompts_UnderManualQueueing_ASentPromptIsQueued()
    {
        _settings.Update(d => d.QueueMode = "manual");
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null);
        SavePrompts("Summarize this.");
        StepsWhenIdle(
            Line("/prompts 1"),
            Line("/exit"));

        string output = await RunAsync();

        Assert.Contains(ChatScreen.QueuedHintPart(1), output);
        Assert.Empty(_chat.Requests);
    }

    /// <summary>A sent prompt joins the message history as a typed line does: Up brings it back.</summary>
    [Fact]
    public async Task Prompts_ASentPrompt_IsInTheMessageHistory()
    {
        _settings.Update(d => d.TtsOutput = false);
        SavePrompts("Summarize this.");
        _chat.EnqueueText("Summary.").EnqueueText("Again.");
        StepsWhenIdle(
            Line("/prompts 1"),
            Key(Keys.Up),      // the prompt, the history's newest line
            Key(Keys.Enter),
            Line("/exit"));

        await RunAsync();

        Assert.Equal(2, _chat.Requests.Count);
        Assert.Equal("Summarize this.", UserText(_chat.Requests[0]));
        Assert.Equal("Summarize this.", LastUserText(_chat.Requests[1]));
    }

    /// <summary><c>/prompts &lt;n&gt;</c> sends the prompt without the pane (2026-10-16; it filled the draft until then); a number past the list is its error.</summary>
    [Fact]
    public async Task Prompts_ANumber_SendsIt_ANumberPastTheListSaysSo()
    {
        _settings.Update(d => d.TtsOutput = false);
        SavePrompts("Summarize this.", "Translate to French.");
        _chat.EnqueueText("Voilà.");
        StepsWhenIdle(
            Line("/prompts 9"),
            Line("/prompts 2"),
            Line("/exit"));

        string output = await RunAsync();

        Assert.Contains(SavedPromptText.NotSaved("9", 2)["Error: ".Length..], output);
        Assert.Single(_chat.Requests);
        Assert.Equal("Translate to French.", UserText(_chat.Requests[0]));
    }

    /// <summary>save, list, remove and list --clear from the command line; none of them reaches the model.</summary>
    [Fact]
    public async Task Prompts_SaveListRemoveAndClear_FromTheCommandLine()
    {
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null);
        PushLine("/prompts save Summarize this.");
        PushLine("/prompts save Review the diff");
        PushLine("/prompts save Summarize this.");
        PushLine("/prompts list");
        PushLine("/prompts remove 1");
        PushLine("/prompts remove 7");
        PushLine("/prompts list --clear");
        _console.Input.PushKey(Keys.Down);                 // Yes
        _console.Input.PushKey(Keys.Enter);
        PushLine("/prompts list");
        PushLine("/exit");

        string output = await RunAsync();

        var first = new SavedPrompt("x", "Summarize this.", default);
        Assert.Contains(SavedPromptText.Saved(first, 1), output);
        Assert.Contains(SavedPromptText.AlreadySaved(first, 1), output);
        Assert.Contains("  · " + SavedPromptText.Caption(2), output);
        Assert.Contains("  · 2. Review the diff", output);
        Assert.Contains(SavedPromptText.Removed(first), output);
        Assert.Contains("There is no saved prompt 7", output);
        Assert.Contains(SavedPromptText.ClearPrompt(1), output);
        Assert.Contains(SavedPromptText.Cleared(1), output);
        Assert.Contains("  · " + SavedPromptText.NoneSaved, output);
        Assert.Empty(SavedPromptsHere().List());
        Assert.Empty(_chat.Requests);
    }

    /// <summary>
    /// The toolbar's 📣 (the user's ask): a double-click keeps the typed draft; a pick sends the prompt (2026-10-16) and leaves the draft on
    /// the row, where the next Enter sends it.
    /// </summary>
    [Fact]
    public async Task Prompts_TheToolbarsItem_APickSends_TheTypedDraftStays()
    {
        _settings.Update(d => { d.TtsOutput = false; d.ToolbarItems = [ToolbarItems.Prompts]; });
        _console.Profile.Height = 40;
        _console.Profile.Width = 240;
        _geometry = new ScreenGeometry(() => null, () => 100);   // the line: row 100, the rule 101, the hint row 102, the toolbar 103
        SavePrompts("Summarize this.");
        _chat.EnqueueText("Summary.").EnqueueText("Drafted.");
        StepsWhenIdle(
        [
            .. Typed("my draft"),
            input => { input.PushClick(0, 103); input.PushClick(0, 103); },   // 📣
            Key(Keys.Enter),                                                   // the prompt sent
            Key(Keys.Enter),                                                   // the draft, still on the row, sent
            Line("/exit"),
        ]);

        string output = await RunAsync();

        Assert.Contains(SavedPromptText.PaneTitle, output);
        Assert.Equal(2, _chat.Requests.Count);
        Assert.Equal("Summarize this.", UserText(_chat.Requests[0]));
        Assert.Equal("my draft", LastUserText(_chat.Requests[1]));
    }

    /// <summary>Ctrl+Alt+P keeps the draft, so the pane's save draft saves it; the draft stays where it was.</summary>
    [Fact]
    public async Task Prompts_TheChord_SaveDraft_SavesWhatIsTyped()
    {
        _console.Profile.Height = 40;
        _console.Profile.Width = 240;
        _geometry = new ScreenGeometry(() => null);
        StepsWhenIdle(
        [
            .. Typed("Line one"),
            Key(CtrlAltP),
            Key(Keys.Char('s')),
            Key(Keys.Escape),   // the pane closed
            Key(Keys.Escape),   // the draft cleared
            Line("/exit"),
        ]);

        string output = await RunAsync();

        Assert.Equal(["Line one"], SavedPromptsHere().List().Select(p => p.Text));
        Assert.Contains(SavedPromptText.Saved(SavedPromptsHere().List()[0], 1), output);
        Assert.Empty(_chat.Requests);
    }

    /// <summary>Typed <c>/prompts</c> took the draft's place, so save draft finds none and says how to keep one.</summary>
    [Fact]
    public async Task Prompts_SaveDraft_AfterATypedPrompts_FindsNone_AndSaysHowToKeepOne()
    {
        _console.Profile.Height = 40;
        _console.Profile.Width = 240;
        _geometry = new ScreenGeometry(() => null);
        StepsWhenIdle(
            Line("/prompts"),
            Key(Keys.Char('s')),
            Key(Keys.Escape),
            Line("/exit"));

        string output = await RunAsync();

        Assert.Contains(SavedPromptText.EmptyRow, output);
        Assert.Contains(SavedPromptText.DraftEmpty, output);
        Assert.Empty(SavedPromptsHere().List());
    }

    /// <summary>On the pane: add (typed), edit (a one-line prompt on the input row), remove after a yes, the filter, Delete.</summary>
    [Fact]
    public async Task Prompts_ThePane_AddsEditsFiltersAndRemoves()
    {
        _console.Profile.Height = 40;
        _console.Profile.Width = 160;
        _geometry = new ScreenGeometry(() => null);
        SavePrompts("One");
        PushLine("/prompts");
        _console.Input.PushKey(Keys.Char('a'));                 // add Two
        foreach (char c in "Two")
        {
            _console.Input.PushKey(Keys.Char(c));
        }

        _console.Input.PushKey(Keys.Enter);
        _console.Input.PushKey(Keys.Char('e'));                 // the cursor on Two: edit it to Three
        for (int i = 0; i < 3; i++)
        {
            _console.Input.PushKey(Keys.Backspace);
        }

        foreach (char c in "Three")
        {
            _console.Input.PushKey(Keys.Char(c));
        }

        _console.Input.PushKey(Keys.Enter);
        _console.Input.PushKey(Keys.Char('h'));                 // the filter: Three alone
        _console.Input.PushKey(Keys.Escape);                    // the filter cleared (no moves while it shows part of the list), the cursor on the first row
        _console.Input.PushKey(Keys.Down);
        _console.Input.PushKey(Keys.Delete);                    // Delete removes Three, after a yes
        _console.Input.PushKey(Keys.Down);
        _console.Input.PushKey(Keys.Enter);
        _console.Input.PushKey(Keys.Escape);                    // the pane closed
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(SavedPromptText.Saved(new SavedPrompt("x", "Two", default), 2), output);
        Assert.Contains(SavedPromptText.Edited(new SavedPrompt("x", "Three", default)), output);
        Assert.Contains(MenuFilter.CaptionOrNull("h", 1, 2)!, output);
        Assert.Contains(SavedPromptText.RemovePrompt(new SavedPrompt("x", "Three", default)), output);
        Assert.Equal(["One"], SavedPromptsHere().List().Select(p => p.Text));
    }

    /// <summary>A prompt of several lines is edited in the draft editor, the prompt in its file; the pane opens again after it.</summary>
    [Fact]
    public async Task Prompts_AMultiLinePrompt_IsEditedInTheDraftEditor()
    {
        _console.Profile.Height = 40;
        _console.Profile.Width = 160;
        _geometry = new ScreenGeometry(() => null);
        SavePrompts("A\nB");
        string? handed = null;
        _editDraft = (path, _, _) =>
        {
            handed = File.ReadAllText(path);
            File.WriteAllText(path, "A\r\nB\r\nC\r\n");
            return Task.CompletedTask;
        };
        StepsWhenIdle(
            Line("/prompts"),
            Key(Keys.Char('e')),
            Key(Keys.Escape),
            Line("/exit"));

        string output = await RunAsync();

        Assert.Equal("A" + Environment.NewLine + "B", handed);
        Assert.Equal(["A\nB\nC"], SavedPromptsHere().List().Select(p => p.Text));
        Assert.Contains(SavedPromptText.Edited(new SavedPrompt("x", "A\nB\nC", default)), output);
        Assert.Contains("1. A +2 lines", output);   // the pane open again on it
    }

    /// <summary>With no draft editor a prompt of several lines is not flattened: the edit says why.</summary>
    [Fact]
    public async Task Prompts_AMultiLinePrompt_WithNoEditor_IsLeftAlone()
    {
        _console.Profile.Height = 40;
        _console.Profile.Width = 240;
        _geometry = new ScreenGeometry(() => null);
        _editDraft = null;
        SavePrompts("A\nB");
        StepsWhenIdle(
            Line("/prompts"),
            Key(Keys.Char('e')),
            Key(Keys.Escape),
            Line("/exit"));

        string output = await RunAsync();

        Assert.Contains(SavedPromptText.EditorUnavailable, output);
        Assert.Equal(["A\nB"], SavedPromptsHere().List().Select(p => p.Text));
    }

    /// <summary>
    /// Under a reply, a typed <c>/prompts</c> and Enter on a prompt (2026-10-16): the prompt is queued and sent as the reply ends, with no
    /// Enter at the idle line (<see cref="MidTurnSend"/> types none), and the live row is left empty (the next line typed runs alone).
    /// </summary>
    [Fact]
    public async Task MidTurn_TypedPrompts_APickIsQueued_AndSentAsTheReplyEnds()
    {
        SavePrompts("Summarize this.");
        MidTurnSend(i =>
        {
            if (i == 1)
            {
                PushLine("/prompts");
            }
            else if (i == 2)
            {
                Scripted().Push(Keys.Enter);
            }
        });

        string output = await RunAsync();

        Assert.Equal(2, _chat.Requests.Count);
        Assert.Equal("Summarize this.", LastUserText(_chat.Requests[1]));
        Assert.DoesNotContain(ChatScreen.CancelledNotice, output);
    }

    /// <summary>Under a reply, the pane's <c>d</c>: the prompt stays on the live row (the hook answers "kept"), and once the reply ends Enter sends it.</summary>
    [Fact]
    public async Task MidTurn_TypedPrompts_D_PutsThePromptOnTheLiveRow()
    {
        SavePrompts("Summarize this.");
        MidTurnPick(i =>
        {
            if (i == 1)
            {
                PushLine("/prompts");
            }
            else if (i == 2)
            {
                Scripted().Push(Keys.Char(SavedPromptText.ToDraftKey));
            }
        });

        await RunAsync();

        Assert.Equal(2, _chat.Requests.Count);
        Assert.Equal("Summarize this.", LastUserText(_chat.Requests[1]));
    }

    /// <summary>Under a reply, a typed <c>/prompts &lt;n&gt;</c> queues the prompt the same way, with no pane, sent as the reply ends.</summary>
    [Fact]
    public async Task MidTurn_TypedPromptsNumber_QueuesThePrompt()
    {
        SavePrompts("Summarize this.", "Translate to French.");
        MidTurnSend(i =>
        {
            if (i == 1)
            {
                PushLine("/prompts 2");
            }
        });

        await RunAsync();

        Assert.Equal(2, _chat.Requests.Count);
        Assert.Equal("Translate to French.", LastUserText(_chat.Requests[1]));
    }

    /// <summary>
    /// Under a reply, a typed <c>/memory</c> and a double-click on 📣 in its pane switches to the prompts; a pick there is queued as well,
    /// the check standing where every mid-turn pane returns.
    /// </summary>
    [Fact]
    public async Task MidTurn_ASwitchFromAnotherPane_APickIsQueued()
    {
        SavePrompts("Summarize this.");
        _settings.Update(d => d.ToolbarItems = [ToolbarItems.Prompts]);
        MidTurnSend(i =>
        {
            if (i == 1)
            {
                PushLine("/memory");
            }
            else if (i == 2)
            {
                PushDoubleClick(0, ToolbarUnder(MemoryMenu.Title));   // 📣 under it: the memory closed, the prompts opened
            }
            else if (i == 3)
            {
                Scripted().Push(Keys.Enter);
            }
        }, "One ", "two ", "three ", "four ", "five.");
        _console.Profile.Width = 240;
        _geometry = new ScreenGeometry(() => null, () => 100);

        await RunAsync();

        Assert.Equal(2, _chat.Requests.Count);
        Assert.Equal("Summarize this.", LastUserText(_chat.Requests[1]));
    }

    /// <summary>Under a reply, a typed <c>/prompts</c> then save draft: the row holds the command, not a draft, so nothing is saved.</summary>
    [Fact]
    public async Task MidTurn_TypedPrompts_SaveDraft_SavesNotTheCommand()
    {
        MidTurnFixture(i =>
        {
            if (i == 1)
            {
                PushLine("/prompts");
            }
            else if (i == 2)
            {
                Scripted().Push(Keys.Char('s'));
                Scripted().Push(Keys.Escape);
            }
        });
        _console.Profile.Width = 240;

        string output = await RunAsync();

        Assert.Empty(SavedPromptsHere().List());
        Assert.Contains(SavedPromptText.DraftEmpty, output);
    }

    /// <summary>Under a reply, <c>/prompts save</c> and a long pasted block: saved whole, never queued or sent to the model.</summary>
    [Fact]
    public async Task MidTurn_PromptsSave_WithAPastedBlock_SavesIt()
    {
        string block = string.Join("\n", Enumerable.Range(1, 6).Select(i => $"row {i}"));
        MidTurnFixture(i =>
        {
            if (i == 1)
            {
                foreach (char c in "/prompts save ")
                {
                    Scripted().Push(Keys.Char(c));
                }

                Scripted().PushPaste(block);
                Scripted().Push(Keys.Enter);
            }
        });

        await RunAsync();

        Assert.Equal([block], SavedPromptsHere().List().Select(p => p.Text));
        Assert.Single(_chat.Requests);
    }

    /// <summary>At the idle line, <c>/prompts save</c> and a long pasted block: saved whole, the model asked nothing.</summary>
    [Fact]
    public async Task Prompts_Save_WithAPastedBlock_AtTheIdleLine_SavesIt()
    {
        string block = string.Join("\n", Enumerable.Range(1, 6).Select(i => $"row {i}"));
        StepsWhenIdle(
            input =>
            {
                foreach (char c in "/prompts save ")
                {
                    input.Push(Keys.Char(c));
                }

                input.PushPaste(block);
                input.Push(Keys.Enter);
            },
            Line("/exit"));

        await RunAsync();

        Assert.Equal([block], SavedPromptsHere().List().Select(p => p.Text));
        Assert.Empty(_chat.Requests);
    }

    /// <summary>Under Queue mode <c>manual</c>, which queues every message at the idle line too: <c>/prompts save</c> with a paste is saved, never queued.</summary>
    [Fact]
    public async Task Prompts_Save_WithAPastedBlock_UnderManualQueueing_IsSavedNotQueued()
    {
        _settings.Update(d => d.QueueMode = "manual");
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null);
        string block = string.Join("\n", Enumerable.Range(1, 6).Select(i => $"row {i}"));
        StepsWhenIdle(
            input =>
            {
                foreach (char c in "/prompts save ")
                {
                    input.Push(Keys.Char(c));
                }

                input.PushPaste(block);
                input.Push(Keys.Enter);
            },
            Line("/exit"));

        string output = await RunAsync();

        Assert.Equal([block], SavedPromptsHere().List().Select(p => p.Text));
        Assert.DoesNotContain(ChatScreen.QueuedHintPart(1), output);
        Assert.Empty(_chat.Requests);
    }

    /// <summary>
    /// Under a reply (2026-10-13): the pane and <c>list --clear</c>'s question on the watcher, the rest at once on the turn task. A number
    /// never reaches the policy (the hook queues it ahead of it), but the policy still has an answer for it.
    /// </summary>
    [Fact]
    public void Prompts_MidTurnPolicy()
    {
        Assert.Equal(
            [MidTurnClass.Pane, MidTurnClass.Pane, MidTurnClass.Quick, MidTurnClass.Quick, MidTurnClass.Quick, MidTurnClass.Quick],
            new[] { "", "list --clear", "list", "save x", "remove 1", "3" }.Select(a => ChatScreen.MidTurnPolicy(SlashCommand.Prompts, a)));
    }

    /// <summary><c>/profile push prompts</c>: after a yes, the saved prompts added to the other profile's, a text it holds skipped.</summary>
    [Fact]
    public async Task ProfilePushPrompts_Yes_AddsIntoTheOtherProfile_SkippingWhatItHolds()
    {
        WorkProfile();
        new SavedPrompts(ProfileDir("work")).Add("Translate to French.", out _);
        SavePrompts("Summarize this.", "Translate to French.");
        PushLine("/profile push prompts work");
        PickYes();
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(SavedPromptText.ProfilePrompt(2, "work", overwrite: false, pull: false), output);
        Assert.Contains(SavedPromptText.ProfileCopied(new SavedPromptImport(SavedPromptOutcome.Done, 1, 1), "work", overwrite: false, pull: false), output);
        Assert.Equal(["Translate to French.", "Summarize this."], new SavedPrompts(ProfileDir("work")).List().Select(p => p.Text));
        Assert.Equal(["Summarize this.", "Translate to French."], SavedPromptsHere().List().Select(p => p.Text));   // the source untouched
    }

    /// <summary><c>/profile pull prompts work overwrite</c>: this profile's list replaced with the other's.</summary>
    [Fact]
    public async Task ProfilePullPrompts_Overwrite_ReplacesThisProfilesList()
    {
        WorkProfile();
        new SavedPrompts(ProfileDir("work")).Add("Theirs.", out _);
        SavePrompts("Mine.");
        PushLine("/profile pull prompts work overwrite");
        PickYes();
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(SavedPromptText.ProfilePrompt(1, "work", overwrite: true, pull: true), output);
        Assert.Equal(["Theirs."], SavedPromptsHere().List().Select(p => p.Text));
    }

    /// <summary><c>/profile pull prompts</c> from a profile with none: said, nothing asked or changed.</summary>
    [Fact]
    public async Task ProfilePullPrompts_FromAProfileWithNone_SaysSo()
    {
        WorkProfile();
        SavePrompts("Mine.");
        PushLine("/profile pull prompts work");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(SavedPromptText.ProfileNothing("work", pull: true), output);
        Assert.Equal(["Mine."], SavedPromptsHere().List().Select(p => p.Text));
    }

    /// <summary>
    /// <see cref="MidTurnFixture"/> with a second reply, so the draft a pick left on the live row is sent once the first one ends: the
    /// lines at the idle line are <c>hi</c>, then Enter (the draft), then <c>/exit</c>.
    /// </summary>
    private void MidTurnPick(Action<int> duringTheReply, params string[] deltas) => MidTurnPrompts(duringTheReply, deltas, enter: true);

    /// <summary>
    /// <see cref="MidTurnPick"/> with no Enter at the idle line (2026-10-16): the lines are <c>hi</c> and <c>/exit</c>, so a prompt sent under the
    /// reply reaches the model only by being queued, and one left on the live row would join <c>/exit</c> and the script would run dry.
    /// </summary>
    private void MidTurnSend(Action<int> duringTheReply, params string[] deltas) => MidTurnPrompts(duringTheReply, deltas, enter: false);

    private void MidTurnPrompts(Action<int> duringTheReply, string[] deltas, bool enter)
    {
        _settings.Update(d => d.TtsOutput = false);
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null);
        _chat.EnqueueText(deltas.Length == 0 ? ["One ", "two ", "three."] : deltas).EnqueueText("Done.");
        _chat.BeforeUpdate = async (i, _) =>
        {
            if (_chat.Requests.Count == 1)
            {
                duringTheReply(i);
                await Task.Delay(40, CancellationToken.None);
            }
        };
        StepsWhenIdle(enter ? [Line("hi"), Key(Keys.Enter), Line("/exit")] : [Line("hi"), Line("/exit")]);
    }
}
