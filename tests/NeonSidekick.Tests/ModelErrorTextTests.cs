using System.Net.Http;
using System.Net.Sockets;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Anthropic;
using NeonSidekick.Llm.OpenAIPlatform;

namespace NeonSidekick.Tests;

/// <summary>A failed model request said in one line with the way on (2026-10-04, the UI review), the chain left for what this does not know.</summary>
public class ModelErrorTextTests
{
    private static HttpRequestException Refused(string message, SocketError code) =>
        new(message, new SocketException((int)code));

    [Fact]
    public void AnUnreachableServer_NamesWhereAndWhy_AndTheWayOn()
    {
        Assert.Equal(
            "Could not reach the LLM server at 127.0.0.1:9 (connection refused). /server picks another; /settings › Servers › Common › LLM URL changes it.",
            ModelErrorText.Readable(Refused("No connection could be made because the target machine actively refused it. (127.0.0.1:9)", SocketError.ConnectionRefused)));
        Assert.Equal(
            ModelErrorText.Unreachable("nowhere.invalid:80", "the name did not resolve"),
            ModelErrorText.Readable(new InvalidOperationException("wrapped", Refused("No such host is known. (nowhere.invalid:80)", SocketError.HostNotFound))));
        Assert.Equal(ModelErrorText.Unreachable(null, "no answer"), ModelErrorText.Readable(Refused("timed out", SocketError.TimedOut)));
        Assert.Equal(ModelErrorText.Unreachable(null, "connection refused"), ModelErrorText.Readable(new AggregateException(Refused("x", SocketError.ConnectionRefused))));
    }

    [Fact]
    public void WhatItDoesNotKnow_IsLeftToTheChain()
    {
        Assert.Null(ModelErrorText.Readable(new HttpRequestException("refused")));
        Assert.Null(ModelErrorText.Readable(new InvalidOperationException("Only user, system and assistant roles are supported!")));
        Assert.Null(ModelErrorText.Readable(Refused("x", SocketError.AccessDenied)));
    }

    /// <summary>A too-long request (2026-11-09) reads as the context window, from every client's exception, with only the figures the server gave.</summary>
    [Fact]
    public void ATooLongRequest_ReadsAsTheContextWindow()
    {
        string anthropic = ModelErrorText.Readable(new AnthropicApiException(400, "invalid_request_error", "prompt is too long: 210000 tokens > 200000 maximum"))!;
        Assert.StartsWith("The request was too long for the model's context window (210,000 of 200,000 tokens). ", anthropic, StringComparison.Ordinal);
        Assert.EndsWith(ModelErrorText.TooLongNextStep, anthropic, StringComparison.Ordinal);

        string openAI = ModelErrorText.Readable(new InvalidOperationException("wrapped", new OpenAIApiException(400, ContextOverflowMatch.OpenAICode, "Your input exceeds the context window of this model.")))!;
        Assert.Equal("The request was too long for the model's context window. " + ModelErrorText.TooLongNextStep, openAI);

        string streamed = ModelErrorText.Readable(new System.ClientModel.ClientResultException("request (518 tokens) exceeds the available context size (512 tokens), try increasing it"))!;
        Assert.Contains("(518 of 512 tokens)", streamed, StringComparison.Ordinal);
    }

    /// <summary>The reply clause appears only when the server's reservation is what blocked the retry: half the limit or more.</summary>
    [Fact]
    public void TheReplyClause_NamesALargeReservationOnly()
    {
        Assert.Contains("(4,500 of 4,096 tokens); the request asks for 3,000 tokens of reply. ", ModelErrorText.TooLong(new ContextOverflow("vLLM text", 1500, 4096, 3000)), StringComparison.Ordinal);
        Assert.DoesNotContain("of reply", ModelErrorText.TooLong(new ContextOverflow("vLLM text", 4000, 4096, 1000)), StringComparison.Ordinal);
        Assert.DoesNotContain("of reply", ModelErrorText.TooLong(new ContextOverflow("Anthropic text", null, null, 90_000)), StringComparison.Ordinal);
        Assert.DoesNotContain(" of ", ModelErrorText.TooLong(new ContextOverflow("vLLM text", null, 4096, null)), StringComparison.Ordinal);
    }

    /// <summary>Each ending has its own next step (Stage 2.2), and the shrinking modes aren't told to turn themselves on (Stage 1.9).</summary>
    [Fact]
    public void TooLong_NamesTheNextStepThatFits()
    {
        var overflow = new ContextOverflow("x", 9_000, 8_192, null);
        Assert.EndsWith(ModelErrorText.MessageNextStep, ModelErrorText.TooLong(overflow, ToolCompactMode.Prune, TooLongReason.Message), StringComparison.Ordinal);
        Assert.EndsWith(ModelErrorText.FixedNextStep, ModelErrorText.TooLong(overflow, null, TooLongReason.Fixed), StringComparison.Ordinal);
        Assert.EndsWith(ModelErrorText.TooLongShrinkingNextStep, ModelErrorText.TooLong(overflow, ToolCompactMode.Compact), StringComparison.Ordinal);
        Assert.EndsWith(ModelErrorText.TooLongNextStep, ModelErrorText.TooLong(overflow, ToolCompactMode.Stop), StringComparison.Ordinal);
        Assert.DoesNotContain("LLM tool compact mode", ModelErrorText.TooLongShrinkingNextStep, StringComparison.Ordinal);
        Assert.EndsWith(ModelErrorText.WindowNextStep, ModelErrorText.TooLongNextStep, StringComparison.Ordinal);
    }
}
