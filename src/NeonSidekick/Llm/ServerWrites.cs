namespace NeonSidekick.Llm;

/// <summary>
/// Which server families' <c>_execute</c> tools a turn offers (2026-10-05, the families' read-write modes): one value through the rules'
/// plumbing (<see cref="TurnRules"/>, <c>Assistant.DefaultRules</c>, <c>SystemPromptFacts</c>) in place of a flag per family, so each
/// family's rule is followed by its write sentence while its tool is offered.
/// </summary>
[Flags]
public enum ServerWrites
{
    None = 0,
    Sql = 1,
    Oracle = 2,
    MySql = 4,
    Postgres = 8,
}
