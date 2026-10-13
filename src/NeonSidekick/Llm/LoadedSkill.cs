namespace NeonSidekick.Llm;

/// <summary>
/// A skill loaded into the conversation (2026-10-12, the user's ask: <c>/skills</c> › Offered › loaded): its name and how many of its
/// <c>load_skill</c> answers the history holds now (<see cref="ConversationHistory.LoadedSkills"/>). A load a compact or a rewind took
/// out is gone from the count; the skill is gone with its last one.
/// </summary>
public sealed record LoadedSkill(string Name, int Loads);
