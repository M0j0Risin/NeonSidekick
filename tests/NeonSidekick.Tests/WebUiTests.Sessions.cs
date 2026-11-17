using System.Net;
using System.Text.Json;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// The 🗂️ sheet's Sessions tab at the session (2026-11-16, plan lucid-roaming-lantern #40): the screen's hooks (none: no view and NotFound),
/// a resume's chord kept with the profile it was asked under, the JSON names the page reads, and the words.
/// </summary>
public sealed partial class WebUiTests
{
    [Fact]
    public void TheSessionsTabsHooks_AreReached_AndNotFoundWithoutThem()
    {
        Assert.Null(_session.Sessions("x"));
        Assert.Equal(WebOutcome.NotFound, _session.ResumeSession(new WebSessionResume(3, "default"), null).Outcome);

        var view = new WebSessionsView("default", [], [], true);
        List<string> calls = [];
        _session.SessionsNow = q => { calls.Add("list " + q); return view; };
        _session.SessionResume = (request, _) => { calls.Add("resume " + request.Id + " " + request.Profile); return WebReply.Ok; };

        Assert.Same(view, _session.Sessions("tax"));
        Assert.Equal(WebOutcome.Ok, _session.ResumeSession(new WebSessionResume(3, "work"), IPAddress.Loopback).Outcome);
        Assert.Equal(["list tax", "resume 3 work"], calls);
    }

    [Fact]
    public void AResumesChord_KeepsTheProfileItWasAskedUnder()
    {
        Assert.Equal(WebOutcome.Ok, _session.QueueChord("/sessions 3", null, null, null, "work").Outcome);
        Assert.True(_session.TryTakeLine(out var line));
        Assert.Equal(("/sessions 3", "work", true), (line.Text, line.Profile, line.Chord));
    }

    [Fact]
    public void TheSessionsView_IsSentInTheNamesThePageReads()
    {
        var view = new WebSessionsView(
            "default",
            [new WebSessionRow(4, "Trip", "today 09:00", "3 turns", "gemma", true, [new WebSnippetPart("the "), new WebSnippetPart("tax", true)])],
            [new WebSessionRow(2, "Older", "Mon", "1 turn", "qwen", false)],
            false,
            "Commands are off.",
            true);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(view, WebUiJsonContext.Default.WebSessionsView));
        var root = json.RootElement;
        Assert.Equal(["profile", "rows", "found", "resumeOffered", "note", "notSaved"], root.EnumerateObject().Select(p => p.Name));
        var row = root.GetProperty("rows")[0];
        Assert.Equal(["id", "title", "when", "turns", "model", "current", "snippet"], row.EnumerateObject().Select(p => p.Name));
        Assert.Equal(["text", "match"], row.GetProperty("snippet")[1].EnumerateObject().Select(p => p.Name));
        Assert.False(root.GetProperty("found")[0].TryGetProperty("snippet", out _));   // nulls left out

        var resume = JsonSerializer.Deserialize("{\"id\":7,\"profile\":\"work\"}", WebUiJsonContext.Default.WebSessionResume)!;
        Assert.Equal((7L, "work"), (resume.Id, resume.Profile));
    }

    [Fact]
    public void TheQueueTabsBodies_AreSentInTheNamesThePageReads()
    {
        var view = new WebQueueView([new WebQueuePcLine(0, "a", 1)], [new WebQueueLine(2, "b", true, 1)], "automatic", "empty", true, false, ["automatic"], ["empty"]);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(view, WebUiJsonContext.Default.WebQueueView));
        Assert.Equal(["pc", "page", "mode", "cancelMode", "held", "sendOffered", "modes", "cancelModes"], json.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(["index", "label", "pictures", "removable"], json.RootElement.GetProperty("pc")[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal(["key", "text", "chord", "pictures"], json.RootElement.GetProperty("page")[0].EnumerateObject().Select(p => p.Name));

        var remove = JsonSerializer.Deserialize("{\"index\":1,\"label\":\"a\"}", WebUiJsonContext.Default.WebQueueRemoveRequest)!;
        Assert.Equal((1, "a", (long?)null), (remove.Index, remove.Label, remove.Key));
        var mode = JsonSerializer.Deserialize("{\"mode\":\"manual\",\"cancel\":\"hold\"}", WebUiJsonContext.Default.WebQueueModeRequest)!;
        Assert.Equal(("manual", "hold"), (mode.Mode, mode.Cancel));
    }

    [Fact]
    public void TheSessionsTabsWords_AreSentWithTheSnapshot()
    {
        Assert.Equal(WebUiText.SessionsArmed, WebUiText.Words.SessionsArmed);
        Assert.All(
            new[]
            {
                WebUiText.Words.SessionsNone, WebUiText.Words.SessionsNoMatch, WebUiText.Words.SessionsFilter, WebUiText.Words.SessionsFound,
                WebUiText.Words.SessionsCurrent, WebUiText.Words.SessionsBusy, WebUiText.Words.SessionsLoading, WebUiText.Words.SessionsNotSaved,
            },
            word => Assert.False(string.IsNullOrEmpty(word)));
    }
}
