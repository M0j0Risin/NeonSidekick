namespace NeonSidekick.Docker;

/// <summary>
/// What <c>/docker logs</c> hands the Docker log window (2026-10-23, the user's ask): the Docker door, the container as the
/// command resolved it (its id and its whole name) and how many earlier lines to show first. A public record so the app's
/// constructor can carry the window's hook; the window builds its own feed over it.
/// </summary>
public sealed record DockerLogSource(DockerSession Docker, string Id, string Name, int Tail);
