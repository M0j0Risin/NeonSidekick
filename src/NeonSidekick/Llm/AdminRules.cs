namespace NeonSidekick.Llm;

/// <summary>
/// Which of the admin families' sentences a turn's default rules carry (2026-10-15, the admin-tools plan: one flags value through
/// <c>Assistant.DefaultRules</c>, <see cref="TurnRules"/> and <c>SystemPromptFacts</c>, <see cref="ServerWrites"/>' shape, rather than
/// eight more booleans): the network tools and their certificate store, the services and their changes, the tasks and their changes, the
/// directory. Worked out from the tools a turn offers (<c>ChatScreen.ComposeTurnTools</c>). Pure.
/// </summary>
[Flags]
public enum AdminRules
{
    None = 0,
    Network = 1,
    NetworkCerts = 2,
    Services = 4,
    ServiceWrites = 8,
    Tasks = 16,
    TaskWrites = 32,
    Directory = 64,

    /// <summary>The monitor tools (2026-10-23, the monitors plan): <c>Assistant.MonitorRule</c>.</summary>
    Monitors = 128,
}
