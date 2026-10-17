using System.Net;
using NeonSidekick.App;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Skills;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// A reflection's ask on the Web UI's page (2026-10-17, the user's question whether <i>Reflection confirm mode</i> reaches the page; it has
/// since 2026-10-14, untested until now): the page gets a <c>learn</c> ask with the PC's title, the summary as its body and the whole proposal
/// folded behind View; Keep from the page writes the skill and closes the PC's pane, Discard writes nothing; a mode that leaves the kind be
/// asks on neither side.
/// </summary>
public partial class ChatScreenTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConfirmNew_ThePageGetsTheAsk_SummaryFirst_TheProposalFolded_AndItsAnswerDecides(bool keep)
    {
        _settings.Update(d => { d.TtsOutput = false; d.ReflectionConfirmMode = "new"; });
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null);   // the pane, so the reflection can ask
        _chat.EnqueueText("Hello.");
        _chat.Enqueue(FakeChatClient.Call("r1", SkillEditorTool.ToolName, CreateSkillArgs("greeting")));
        string title = ChatScreen.LearnAskTitle(new SkillProposal(SkillChangeKind.New, SkillEditorTool.CreateAction, "greeting"));
        string done = keep ? "(🧠 learned: created skill 'greeting'" : ChatScreen.LearnDeclinedNotice(new SkillEditResult(SkillEditOutcome.Declined, "greeting", SkillScope.Profile, Detail: SkillEditorTool.CreateAction));
        WebAskSpec? seen = null;
        var input = Scripted();
        int step = 0;
        input.OnWait = () =>
        {
            switch (step)
            {
                case 0: step++; PushLine(input, "hi"); break;
                case 1: step++; PushLine(input, "/learn keep the greeting"); break;
                case 2:
                    // The page answers as soon as its ask is open, whether or not the PC's pane is up yet.
                    if (Web.Asks.OpenAsks() is [var ask, ..] && ask.Spec.Kind == "learn")
                    {
                        step++;
                        seen = ask.Spec;
                        Web.Answer(new WebAnswerRequest { AskId = ask.Id, Choice = keep ? "keep" : "discard" }, IPAddress.Loopback);
                    }

                    break;
                case 3:
                    if (_session.Learning is { IsCompleted: true } && Output.Contains(done, StringComparison.Ordinal))
                    {
                        step++;
                        PushLine(input, "/exit");
                    }

                    break;
            }
        };

        string output = await RunAsync();

        Assert.NotNull(seen);
        Assert.Equal(title, seen!.Title);
        Assert.Equal(["keep", "discard"], seen.Choices.Select(c => c.Id));
        Assert.Equal(ChatScreen.LearnAskRows[1], seen.DetailLabel);
        Assert.StartsWith("Description: ", seen.Detail, StringComparison.Ordinal);
        Assert.Contains("Instructions:", seen.Detail, StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(seen.Body));
        Assert.NotEqual(seen.Detail, seen.Body);   // the summary, not the proposal whole
        Assert.Equal(1, CountOf(output, done));
        Assert.Equal(keep, File.Exists(Path.Combine(ProfileSkills, "greeting", SkillCatalog.FileName)));
        Assert.Empty(Web.Asks.OpenAsks());
    }

    [Fact]
    public async Task ConfirmEdit_ANewSkill_AsksOnNeitherSide()
    {
        _settings.Update(d => { d.TtsOutput = false; d.ReflectionConfirmMode = "edit"; });
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null);   // the pane, so the reflection can ask
        _chat.EnqueueText("Hello.");
        _chat.Enqueue(FakeChatClient.Call("r1", SkillEditorTool.ToolName, CreateSkillArgs("greeting")));
        bool asked = false;
        var input = Scripted();
        int step = 0;
        input.OnWait = () =>
        {
            asked |= Web.Asks.OpenAsks().Count > 0;
            switch (step)
            {
                case 0: step++; PushLine(input, "hi"); break;
                case 1: step++; PushLine(input, "/learn keep the greeting"); break;
                case 2:
                    if (_session.Learning is { IsCompleted: true } && Output.Contains("(🧠 learned: created skill 'greeting'", StringComparison.Ordinal))
                    {
                        step++;
                        PushLine(input, "/exit");
                    }

                    break;
            }
        };

        string output = await RunAsync();

        Assert.False(asked);
        Assert.DoesNotContain(ChatScreen.LearnAskTitle(new SkillProposal(SkillChangeKind.New, SkillEditorTool.CreateAction, "greeting")), output);
        Assert.True(File.Exists(Path.Combine(ProfileSkills, "greeting", SkillCatalog.FileName)));
    }
}
