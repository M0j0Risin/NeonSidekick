using NeonSidekick.Llm.Tools;

namespace NeonSidekick.Llm;

/// <summary>
/// Which tool rules a turn's default operating rules carry (2026-10-04, the <c>/botchat</c> bots' tools): exactly the flags
/// <c>Assistant.DefaultRules</c> takes, worked out once from the tools a turn offers (<c>ChatScreen.ComposeTurnTools</c>) so the main chat and a bot read
/// them the same way. Memory, skills, the project notes and plan mode's directive are not here: each prompt decides those itself.
/// </summary>
public sealed record TurnRules(
    bool Web = false,
    bool Files = false,
    AskLimits? Ask = null,
    bool Sessions = false,
    bool Download = false,
    bool Delete = false,
    bool Mcp = false,
    bool Timers = false,
    bool Git = false,
    bool Shell = false,
    bool Bridge = false,
    bool Police = true,
    bool Obsidian = false,
    bool ObsidianDelete = false,
    bool Sql = false,
    bool Native = false,
    bool Advisor = false,
    bool HomeAssistant = false,
    bool Oracle = false,
    bool MySql = false,
    bool Unc = false,
    bool UncFetch = false,
    bool UncWrite = false,
    bool Docker = false,
    bool DockerWrite = false,
    bool Help = false,
    bool Sqlite = false,
    bool Postgres = false,
    bool SqliteWrite = false,
    ServerWrites ServerWrites = ServerWrites.None)
{
    /// <summary>The default operating rules for <paramref name="markdown"/>: <c>Assistant.DefaultRules</c> with these flags and tools on. Pure.</summary>
    public string DefaultRules(bool markdown) =>
        Assistant.DefaultRules(markdown, tools: true, Files, Web, Ask, Sessions, Download, Delete, Mcp, Timers, Git, Shell, Bridge, Police, Obsidian, ObsidianDelete, Sql, Native, Advisor, HomeAssistant, Oracle, MySql, Unc, UncFetch, UncWrite, Docker, DockerWrite, Help, Sqlite, Postgres, Sqlite && SqliteWrite, ServerWrites);
}
