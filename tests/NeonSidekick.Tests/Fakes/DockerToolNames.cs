namespace NeonSidekick.Tests.Fakes;

/// <summary>The ten Docker tools' names in <c>ChatScreen.DockerTools</c> order (2026-10-02): the six reads, the four changes. Pinned.</summary>
internal static class DockerToolNames
{
    public static readonly string[] All =
        ["docker_containers", "docker_logs", "docker_inspect", "docker_stats", "docker_resources", "docker_compose", "docker_lifecycle", "docker_pull", "docker_remove", "docker_prune"];

    /// <summary>The reads: offered under Docker tools alone, kept by plan mode.</summary>
    public static readonly string[] Reads = ["docker_containers", "docker_logs", "docker_inspect", "docker_stats", "docker_resources", "docker_compose"];

    /// <summary>The changes, offered only under Docker writes, each asking.</summary>
    public static readonly string[] Writes = ["docker_lifecycle", "docker_pull", "docker_remove", "docker_prune"];
}
