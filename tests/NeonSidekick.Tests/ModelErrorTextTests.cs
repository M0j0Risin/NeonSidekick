using System.Net.Http;
using System.Net.Sockets;
using NeonSidekick.Llm;

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
            "Could not reach the LLM server at 127.0.0.1:9 (connection refused). /server picks another; /settings › LLM › URL changes it.",
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
}
