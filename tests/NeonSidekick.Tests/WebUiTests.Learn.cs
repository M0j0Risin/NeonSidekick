using System.Net;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// The status row's 🧠 on the session (2026-10-17, the user's ask): the cancel reaches the screen's hook with the address, and with no hook
/// is not found; the status carries the flag, and the words go with every snapshot.
/// </summary>
public sealed partial class WebUiTests
{
    [Fact]
    public void TheLearnCancel_ReachesItsHook_AndWithNoneIsNotFound()
    {
        Assert.Equal(WebOutcome.NotFound, _session.CancelLearning(null).Outcome);

        IPAddress? seen = null;
        _session.LearnCancel = remote => { seen = remote; return new WebReply(WebOutcome.Conflict, WebUiText.NoReflection); };
        var reply = _session.CancelLearning(IPAddress.Loopback);

        Assert.Equal(IPAddress.Loopback, seen);
        Assert.Equal((WebOutcome.Conflict, WebUiText.NoReflection), (reply.Outcome, reply.Message));
    }

    [Fact]
    public void TheLearnWords_AreSentWithTheSnapshot_AndTheStatusDefaultsToNoReflection()
    {
        Assert.Equal(WebUiText.LearnCancel, WebUiText.Words.LearnCancel);
        Assert.Equal(WebUiText.LearnCancelArmed, WebUiText.Words.LearnCancelArmed);
        Assert.False(new WebStatus(null, null, null, null).Learning);
        Assert.Contains("\"learning\":true", System.Text.Json.JsonSerializer.Serialize(new WebStatus(null, null, null, null, Learning: true), WebUiJsonContext.Default.WebStatus), StringComparison.Ordinal);
    }

    private const string Proposal = "Description: x\n\nInstructions:\nDo x.";

    /// <summary>An ask's folded detail and its label (2026-10-17, the reflection's proposal behind View) reach the page's view as given.</summary>
    [Fact]
    public void AnAsksDetail_ReachesThePagesView()
    {
        var handle = _session.Asks.Open(new WebAskSpec("learn", "🧠 A reflection wants to create skill “x”", [new WebChoice("keep", "Keep"), new WebChoice("discard", "Discard")],
            Body: "The summary.", Detail: Proposal, DetailLabel: "View"));
        var view = Assert.Single(_session.Snapshot().Asks);
        Assert.Equal(handle.Id, view.Id);
        Assert.Equal(("The summary.", Proposal, "View"), (view.Body, view.Detail, view.DetailLabel));
        _session.Asks.Close(handle);
    }
}
