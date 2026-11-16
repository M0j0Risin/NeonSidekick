using NeonSidekick.App;
using NeonSidekick.Sessions;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// The 🗂️ sheet's Sessions tab on the screen (2026-11-16, plan lucid-roaming-lantern #40): a resume queued as the page's chord replays the
/// session at the idle line and marks it current; a resume is refused for the session on screen, a missing one, a stale profile, under a
/// reply and while another page chord waits; a title loses its saved paths and a model path shows as its label; a search lists what was
/// said in other sessions, never the current one nor a match only inside a path; with logging off the view says so.
/// </summary>
public partial class ChatScreenTests
{
    /// <summary>A stored session as an older build left it: <paramref name="title"/> and <paramref name="model"/> as given, one turn.</summary>
    private long SeedRawSession(string title, string model, string user, string reply)
    {
        using var store = OpenSessions();
        long id = store.Begin(title, model)!.Value;
        store.AppendTurn(id, user, reply, 0, [], [], 0, 3, 4, false);
        return id;
    }

    [Fact]
    public async Task AResumeFromTheSessionsTab_ReplaysTheSession_AndMarksItCurrent()
    {
        _settings.Update(d => { d.TtsOutput = false; d.WebUiCommands = true; });
        long id = SeedSession();
        WebReply resumed = default, again = default;
        WebSessionsView? after = null;
        StepsWhenIdle(
            _ => resumed = Web.ResumeSession(new WebSessionResume(id, _settings.ProfileName), null),
            i =>
            {
                after = Web.Sessions(null);
                again = Web.ResumeSession(new WebSessionResume(id, _settings.ProfileName), null);
                PushLine(i, "/exit");
            });

        await RunAsync();

        Assert.Equal(WebOutcome.Ok, resumed.Outcome);
        var feed = Web.Feed.Snapshot(m => m).SkipWhile(e => e.Kind != "user").ToList();
        Assert.Equal("How do I wire the Vosk model?", feed[0].Text);
        Assert.True(Assert.Single(after!.Rows).Current);
        Assert.Equal((WebOutcome.Conflict, SessionsMenu.CurrentNotice), (again.Outcome, again.Message));
        Assert.Empty(_chat.Requests);
    }

    [Fact]
    public async Task AResume_IsRefused_ForAMissingSession_AStaleProfile_AndWhileAChordWaits()
    {
        _settings.Update(d => { d.TtsOutput = false; d.WebUiCommands = true; });
        long id = SeedSession();
        WebReply missing = default, stale = default, first = default, waiting = default;
        StepsWhenIdle(
            _ =>
            {
                missing = Web.ResumeSession(new WebSessionResume(id + 50, _settings.ProfileName), null);
                stale = Web.ResumeSession(new WebSessionResume(id, "another"), null);
                first = Web.ResumeSession(new WebSessionResume(id, _settings.ProfileName), null);
                waiting = Web.ResumeSession(new WebSessionResume(id, _settings.ProfileName), null);
            },
            Line("/exit"));

        await RunAsync();

        Assert.Equal((WebOutcome.Conflict, ChatScreen.SessionMissingError(id + 50)), (missing.Outcome, missing.Message));
        Assert.Equal((WebOutcome.Conflict, WebUiText.ProfileSwitched), (stale.Outcome, stale.Message));
        Assert.Equal(WebOutcome.Ok, first.Outcome);
        Assert.Equal((WebOutcome.Conflict, WebUiText.ChordWaiting), (waiting.Outcome, waiting.Message));
    }

    [Fact]
    public async Task AResume_IsRefused_WhileAReplyRuns()
    {
        _settings.Update(d => { d.TtsOutput = false; d.WebUiCommands = true; });
        long id = SeedSession();
        _chat.EnqueueText("Hello.");
        WebReply busy = default;
        _chat.BeforeUpdate = (i, ct) =>
        {
            if (i == 0)
            {
                busy = Web.ResumeSession(new WebSessionResume(id, _settings.ProfileName), null);
            }

            return Task.CompletedTask;
        };
        LinesWhenIdle("hi", "/exit");

        await RunAsync();

        Assert.Equal((WebOutcome.Conflict, SettingsMenu.NotWhileReplyRunsNotice), (busy.Outcome, busy.Message));
    }

    [Fact]
    public async Task TheSessionsView_ShowsNoSavedPath_AndAModelPathAsItsLabel()
    {
        _settings.Update(d => { d.TtsOutput = false; d.WebUiCommands = true; });
        SeedRawSession(@"[Image #1, saved as C:\Users\someone\hidden\pasted-1.png] what is this", @"C:\models\hidden\gemma-3-12b.gguf, qwen3", "look", "A cat.");
        WebSessionsView? all = null, byFolder = null;
        StepsWhenIdle(
            i =>
            {
                all = Web.Sessions(null);
                byFolder = Web.Sessions("hidden");
                PushLine(i, "/exit");
            });

        await RunAsync();

        var row = Assert.Single(all!.Rows);
        Assert.Equal("[Image #1] what is this", row.Title);
        Assert.Equal("gemma-3-12b.gguf, qwen3", row.Model);
        Assert.False(row.Current);
        Assert.Empty(byFolder!.Rows);   // the folder's name neither shows nor matches
        Assert.Empty(byFolder.Found);
    }

    [Fact]
    public async Task ASearch_ListsWhatWasSaidElsewhere_ButNeverTheCurrentSession_NorAMatchInsideAPath()
    {
        _settings.Update(d => { d.TtsOutput = false; d.WebUiCommands = true; });
        long current = SeedSession("Plan the trip", "The tax office closes at five.");
        long other = SeedSession("Something else", "File the tax forms by Friday.");
        SeedRawSession("A picture", "llama", @"[Image #1, saved as C:\taxes\pasted-1.png] look", "A receipt.");
        WebSessionsView? found = null, shortQuery = null;
        StepsWhenIdle(
            Line("/sessions " + current),
            i =>
            {
                found = Web.Sessions("tax");
                shortQuery = Web.Sessions("ta");
                PushLine(i, "/exit");
            });

        await RunAsync();

        Assert.Empty(found!.Rows);   // no title says it
        var hit = Assert.Single(found.Found);
        Assert.Equal(other, hit.Id);
        Assert.Contains(hit.Snippet!, p => p.Match && p.Text == "tax");
        Assert.Empty(shortQuery!.Found);   // under three letters: titles only
        Assert.True(found.ResumeOffered);
        Assert.False(found.NotSaved);
    }

    [Fact]
    public async Task TheSessionsView_SaysWhenLoggingIsOff_AndWhenCommandsAreOff()
    {
        _settings.Update(d => { d.TtsOutput = false; d.SessionLogging = false; d.WebUiCommands = false; });
        WebSessionsView? view = null;
        StepsWhenIdle(
            i =>
            {
                view = Web.Sessions(null);
                PushLine(i, "/exit");
            });

        await RunAsync();

        Assert.True(view!.NotSaved);
        Assert.False(view.ResumeOffered);
        Assert.Equal(WebUiText.CommandsOff, view.Note);
    }
}
