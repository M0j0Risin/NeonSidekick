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

    /// <summary>Ping and trace offered with the network tools (2026-10-23, the admin review's R4: the rule named them where no pinger is).</summary>
    NetworkEcho = 256,

    /// <summary>
    /// <c>service_start_type</c> offered (2026-10-23, the admin review's I4): <see cref="ServiceWrites"/> is <c>service_control</c> alone now, so a
    /// computer that allows only start types is never told of a tool it lacks.
    /// </summary>
    ServiceStartTypes = 512,

    /// <summary>
    /// The directory's 4740 pointer may be followed (2026-10-23, the admin review's N3): Windows' event logs with a ticked computer beyond this
    /// one, where a PDC's Security log can be. A Mac's logs have no Security log and no remote computer.
    /// </summary>
    DirectoryLockout = 1024,
}
