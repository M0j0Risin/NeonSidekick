using NeonSidekick.Docker;

namespace NeonSidekick.Tests;

/// <summary>
/// A fact whose Docker tools need a transport on this OS (2026-11-20, the mcp-ask review's #81): skipped where
/// <see cref="DockerSocket.Here"/> is <see cref="DockerTransport.None"/>, so the run says so instead of passing an early return.
/// No engine has to be running: the tests only reach the confirm.
/// </summary>
public sealed class DockerHereFactAttribute : FactAttribute
{
    public DockerHereFactAttribute()
    {
        if (DockerSocket.Here == DockerTransport.None)
        {
            Skip = "Docker has no transport on this OS.";
        }
    }
}
