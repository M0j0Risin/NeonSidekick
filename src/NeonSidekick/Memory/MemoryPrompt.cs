using System.Text;

namespace NeonSidekick.Memory;

/// <summary>
/// The memory section of the system prompt: pure statics with pinned wording. It sits between the
/// operating rules and the voice directive (the directive stays last so it still wins), and it exists
/// only while memory is on — off means neither the instruction nor the list.
///
/// <para>Since 2026-09-17 the list itself is in the prompt only for a turn without tools: with them
/// it rides the opening <c>recall_memory</c> pair (<see cref="Llm.Tools.RecallMemoryTool"/>), the
/// last thing before the model's first reply, where a first-turn question actually finds it.</para>
/// </summary>
public static class MemoryPrompt
{
    /// <summary>
    /// The directive for a turn that offers no tools (the setting <c>LLM offer tools</c> off): the memory
    /// exists and the list follows, but nothing to call — <c>/remember</c> is the user's way in. The
    /// first sentence of <see cref="Directive"/>. Pinned.
    /// </summary>
    public const string DirectiveWithoutTool = "You have a long-term memory that lasts across sessions.";

    /// <summary>
    /// Tells the model when to call the save tool and where the list is. Names the tools by
    /// <see cref="Llm.Tools.SaveMemoryTool.ToolName"/> and <see cref="Llm.Tools.RecallMemoryTool.ToolName"/>. Since 2026-10-04
    /// it is put together from <see cref="SaveSentences"/> and <see cref="RecallSentence"/>, byte for byte as before.
    /// </summary>
    public const string Directive = DirectiveWithoutTool + " " + SaveSentences + " " + RecallSentence;

    /// <summary>
    /// When to call the save tool (2026-10-04: split out of <see cref="Directive"/> for <see cref="ListedSection"/>). Pinned.
    /// </summary>
    public const string SaveSentences =
        "When the user tells you a lasting fact about themselves (their name, where they live, what they like, what they are working on) " +
        "or asks you to remember something, call " + Llm.Tools.SaveMemoryTool.ToolName + " with one short sentence in the third person, then continue your reply. " +
        "Do not save passing details, and do not save anything you already remember.";

    /// <summary>Where the list arrives in the main chat: the opening <c>recall_memory</c> pair. Pinned.</summary>
    public const string RecallSentence =
        "What you remember arrives as the result of " + Llm.Tools.RecallMemoryTool.ToolName + " at the start of the conversation; call it again when the list is no longer in view.";

    /// <summary>
    /// The directive under <c>Memory mode</c> read-only (2026-10-04, the user's ask): the memory exists and where the list arrives,
    /// but no save sentence, since <c>save_memory</c> is not offered. Pinned.
    /// </summary>
    public const string ReadOnlyDirective = DirectiveWithoutTool + " " + RecallSentence;

    /// <summary>Above the list, only when there is one.</summary>
    public const string Heading = "What you remember about the user, oldest first:";

    /// <summary>The recall result while nothing is stored. Pinned.</summary>
    public const string NothingRemembered = "Nothing remembered about the user yet.";

    /// <summary>
    /// The list as the model reads it: <see cref="Heading"/> and one <c>- </c> line per memory,
    /// oldest first; <see cref="NothingRemembered"/> when there is none. The recall tool's result and
    /// the tail of the tool-free <see cref="Section"/>.
    /// </summary>
    public static string Recalled(IReadOnlyList<string> memories)
    {
        ArgumentNullException.ThrowIfNull(memories);
        if (memories.Count == 0)
        {
            return NothingRemembered;
        }

        var sb = new StringBuilder(Heading.Length + memories.Count * 48);
        sb.Append(Heading);
        foreach (var memory in memories)
        {
            sb.Append("\n- ").Append(memory);
        }

        return sb.ToString();
    }

    /// <summary>
    /// The prompt's memory section. With <paramref name="tools"/> the <see cref="Directive"/> alone —
    /// the list rides the opening <c>recall_memory</c> pair — or, with <paramref name="save"/> false (<c>Memory mode</c>
    /// read-only, 2026-10-04), <see cref="ReadOnlyDirective"/>. Without them <see cref="DirectiveWithoutTool"/>
    /// and, when there are any, the list under it (<see cref="Recalled"/>): no tool can carry it, so
    /// the prompt does.
    /// </summary>
    public static string Section(IReadOnlyList<string> memories, bool tools = true, bool save = true)
    {
        ArgumentNullException.ThrowIfNull(memories);
        if (tools)
        {
            return save ? Directive : ReadOnlyDirective;
        }

        return memories.Count == 0 ? DirectiveWithoutTool : DirectiveWithoutTool + "\n\n" + Recalled(memories);
    }

    /// <summary>
    /// The memory section with the list in it whatever the tools (2026-10-04, the <c>/botchat</c> bots' memory): a bot's history
    /// is rebuilt every reply, so no opening <c>recall_memory</c> pair would carry the list, and the prompt, rebuilt with it,
    /// always holds the store as it is. <see cref="DirectiveWithoutTool"/>, <see cref="SaveSentences"/> with <paramref name="save"/>
    /// (<c>save_memory</c> offered), then <see cref="Recalled"/> — <see cref="NothingRemembered"/> for an empty store.
    /// </summary>
    public static string ListedSection(IReadOnlyList<string> memories, bool save)
    {
        ArgumentNullException.ThrowIfNull(memories);
        return (save ? DirectiveWithoutTool + " " + SaveSentences : DirectiveWithoutTool) + "\n\n" + Recalled(memories);
    }
}
