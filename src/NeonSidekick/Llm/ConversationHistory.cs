using System.Globalization;
using Microsoft.Extensions.AI;
using NeonSidekick.Diagnostics;
using NeonSidekick.Files;

namespace NeonSidekick.Llm;

/// <summary>
/// The in-memory transcript the model sees: a system prompt followed by user, assistant and tool
/// messages in wire order. Pure; no I/O, no persistence.
///
/// <para>Trimming happens on write, at <em>user-message boundaries</em>: a turn is everything from
/// one user message up to the next, so dropping whole turns can never separate an assistant's
/// <see cref="FunctionCallContent"/> from the <see cref="FunctionResultContent"/> that answers it.
/// A tool result whose call is missing makes most servers reject the whole request.</para>
///
/// <para>One user-role message is not a turn: the <em>carrier</em> a <c>view_image</c> result is
/// followed by (<see cref="AddToolImages"/>), a user message only because that is the one role an
/// OpenAI-format request lets a picture ride in. It is tagged with <see cref="CarrierKey"/> in
/// <see cref="ChatMessage.AdditionalProperties"/> — never on the wire; the adapter reads the role
/// and the contents — and <see cref="IsTurnStart"/> is the one boundary rule, so a carrier ages out
/// with the turn it belongs to and never counts in <see cref="TurnCount"/>.</para>
/// </summary>
public sealed class ConversationHistory
{
    /// <summary>The cap a fresh history starts with, and <c>LLM max turns</c>' fallback when auto compact cannot run.</summary>
    public const int DefaultMaxTurns = 24;

    /// <summary>
    /// How many user turns are kept; older turns fall off the front on the next <see cref="AddUser(string)"/>.
    /// Null keeps every turn. A fixed 24 until 2026-09-27, when it became <c>LLM max turns</c> (the user's
    /// call): a turn count is the wrong unit for the context — two dozen short voice turns are a few
    /// thousand tokens, and every turn past the cap dropped the oldest for good (the opening pairs with
    /// turn 1, and from the saved session too) and changed the prompt's prefix, so a local server
    /// reprocessed the whole prompt each message. <c>auto</c> (<see cref="App.ChatScreen.TurnCapFor"/>)
    /// sets null while the token-based auto compact can act and <see cref="DefaultMaxTurns"/> when it cannot.
    /// </summary>
    public int? MaxTurns { get; set; } = DefaultMaxTurns;

    /// <summary>The <see cref="ChatMessage.AdditionalProperties"/> key that marks a carrier message (value <c>true</c>).</summary>
    public const string CarrierKey = "neon.imageCarrier";

    /// <summary>
    /// The <see cref="AIContent.AdditionalProperties"/> key that marks a <see cref="FunctionResultContent"/>
    /// holding a loaded skill's instructions (value <c>true</c>; <see cref="Assistant.ResultContent"/>),
    /// what the compactor's protection keeps (<c>Skill compact mode</c>). Never on the wire.
    /// </summary>
    public const string SkillResultKey = "neon.skillResult";

    private const string Category = "Llm";

    private readonly List<ChatMessage> _messages = new();
    // Kept by Recount after every write that can change it, so a reader on another task (the
    // /sys pane opened mid-turn) gets a number and never walks the list the turn appends to.
    private volatile int _turnCount;

    public ConversationHistory(string systemPrompt)
    {
        SystemPrompt = systemPrompt ?? "";
    }

    /// <summary>Sent first on every request when non-blank. Changing it takes effect on the next request.</summary>
    public string SystemPrompt { get; set; }

    /// <summary>Everything after the system prompt, in order.</summary>
    public IReadOnlyList<ChatMessage> Messages => _messages;

    /// <summary>Number of user turns currently held (a carrier, <see cref="IsImageCarrier"/>, is none). A plain read, safe from any task.</summary>
    public int TurnCount => _turnCount;

    /// <summary>Whether <paramref name="message"/> is a carrier (<see cref="AddToolImages"/>): user-role, tagged <see cref="CarrierKey"/>.</summary>
    public static bool IsImageCarrier(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message.Role == ChatRole.User
            && message.AdditionalProperties is { } properties
            && properties.TryGetValue(CarrierKey, out object? tag)
            && tag is true;
    }

    /// <summary>Whether <paramref name="result"/> is a loaded skill's instructions: tagged <see cref="SkillResultKey"/>.</summary>
    public static bool IsSkillResult(FunctionResultContent result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.AdditionalProperties is { } properties
            && properties.TryGetValue(SkillResultKey, out object? tag)
            && tag is true;
    }

    /// <summary>The one boundary rule: a user message that is not a carrier starts a turn. <see cref="ConversationCompactor.Split"/> cuts by it too.</summary>
    public static bool IsTurnStart(ChatMessage message) => message.Role == ChatRole.User && !IsImageCarrier(message);

    /// <summary>
    /// The carrier's text: names the source and disclaims the user, so a small model answers the
    /// question it was asked rather than this line. <c>(attached by view_image, not typed by the
    /// user: ladybug.png)</c>; several names joined by <c>, </c> after <c>the pictures:</c>. Since 2026-09-24
    /// <paramref name="source"/> names the tool (<c>generate_image</c>'s pictures ride the same carrier; several tools
    /// in one iteration are joined by <c> and </c>), <c>view_image</c> when none is given. Pinned.
    /// </summary>
    public static string ImageCarrierText(IReadOnlyList<string> names, string? source = null)
    {
        ArgumentNullException.ThrowIfNull(names);
        return "(attached by " + (string.IsNullOrWhiteSpace(source) ? Tools.ViewImageTool.ToolName : source) + ", not typed by the user" + (names.Count == 1 ? ": " : " — the pictures: ") + string.Join(", ", names) + ")";
    }

    /// <summary>
    /// The carrier for a <c>view_image</c> or <c>generate_image</c> result (or several in one iteration): one user-role
    /// message, <see cref="ImageCarrierText"/> then one image part per attachment, tagged
    /// <see cref="CarrierKey"/>. Appended right after the iteration's tool results, so the next
    /// request shows the model the picture its call fetched. Nothing for none; no trim, since it is no turn.
    /// <paramref name="source"/> is the tool name (or names) the carrier text credits.
    /// </summary>
    public void AddToolImages(IReadOnlyList<ImageAttachment> images, string? source = null)
    {
        ArgumentNullException.ThrowIfNull(images);
        if (images.Count == 0)
        {
            return;
        }

        var contents = new List<AIContent>(images.Count + 1) { new TextContent(ImageCarrierText(images.Select(i => i.Path).ToList(), source)) };
        foreach (var image in images)
        {
            contents.Add(new DataContent(image.Bytes, image.MediaType));
        }

        _messages.Add(new ChatMessage(ChatRole.User, contents) { AdditionalProperties = new AdditionalPropertiesDictionary { [CarrierKey] = true } });
    }

    public void AddUser(string text) => AddUser(text, []);

    /// <summary>
    /// A user message with pictures: the text first (it names them by their <c>[Image #n]</c>
    /// labels), then one image part per attachment in the order given. The OpenAI adapter sends a
    /// <see cref="DataContent"/> with an <c>image/*</c> media type as an <c>image_url</c> data URL.
    /// Without images the message is the plain text one.
    /// </summary>
    public void AddUser(string text, IReadOnlyList<ImageAttachment> images)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(images);
        if (images.Count == 0)
        {
            _messages.Add(new ChatMessage(ChatRole.User, text));
        }
        else
        {
            var contents = new List<AIContent>(images.Count + 1) { new TextContent(text) };
            foreach (var image in images)
            {
                contents.Add(new DataContent(image.Bytes, image.MediaType));
            }

            _messages.Add(new ChatMessage(ChatRole.User, contents));
        }

        Trim();
    }

    /// <summary>Appends assistant text produced without tool calls (or a partial reply cut short).</summary>
    public void AddAssistant(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        _messages.Add(new ChatMessage(ChatRole.Assistant, text));
    }

    /// <summary>
    /// Appends a message as the adapter shaped it — text plus <see cref="FunctionCallContent"/> —
    /// so the next request re-serialises the <c>tool_calls</c> the results refer back to.
    /// Messages with no content are ignored.
    /// </summary>
    public void AddMessage(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Contents.Count == 0)
        {
            return;
        }

        _messages.Add(message);
        Recount();
    }

    /// <summary>One <see cref="ChatRole.Tool"/> message carrying every result of an iteration.</summary>
    public void AddToolResults(IEnumerable<FunctionResultContent> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        var contents = new List<AIContent>(results);
        if (contents.Count == 0)
        {
            return;
        }

        _messages.Add(new ChatMessage(ChatRole.Tool, contents));
    }

    /// <summary>
    /// The one in-place edit of the transcript: replaces the text of the tool result carrying
    /// <paramref name="callId"/>, for a seeded result that must stay current (the working
    /// directory after <c>/cwd</c>). False when no such result is held — a pair <see cref="Trim"/>
    /// dropped is simply not found — or when the text already matches, so a caller can tell a
    /// change from a no-op. Message order and count never change.
    /// </summary>
    public bool TryReplaceToolResult(string callId, string result)
    {
        ArgumentNullException.ThrowIfNull(callId);
        ArgumentNullException.ThrowIfNull(result);
        foreach (var message in _messages)
        {
            if (message.Role != ChatRole.Tool)
            {
                continue;
            }

            foreach (var content in message.Contents)
            {
                if (content is FunctionResultContent found && string.Equals(found.CallId, callId, StringComparison.Ordinal))
                {
                    if (found.Result is string text && string.Equals(text, result, StringComparison.Ordinal))
                    {
                        return false;
                    }

                    found.Result = result;
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Forgets every message; the system prompt stays.</summary>
    public void Clear()
    {
        _messages.Clear();
        Recount();
    }

    /// <summary>
    /// The transcript becomes <paramref name="messages"/> (a compact's new list, <see cref="ConversationCompactor"/>);
    /// empty messages are skipped and the result trimmed like any write. The system prompt stays.
    /// A compact's summary turn is the oldest afterwards and ages out at <see cref="MaxTurns"/> like any other.
    /// </summary>
    public void Replace(IReadOnlyList<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        _messages.Clear();
        foreach (var message in messages)
        {
            AddMessage(message);
        }

        Trim();
    }

    /// <summary>
    /// A stored session back as the transcript (<c>/sessions</c>, 2026-09-18): <see cref="Replace"/>
    /// with <paramref name="messages"/>. The opening pairs are in the list; the next turn keeps them
    /// current rather than seeding them again. (Until later on 2026-09-18 it also continued the
    /// <c>/skill</c> activation counter; the name form went with the counter.)
    /// </summary>
    public void Restore(IReadOnlyList<ChatMessage> messages) => Replace(messages);

    /// <summary>
    /// A copy of the last turn: from the last <see cref="IsTurnStart"/> message to the end (the
    /// user's message, the model's calls and results, the reply). Empty with no turn held. The
    /// snapshot a side job takes (<c>SkillLearner</c>) before the next turn appends to the list.
    /// </summary>
    public List<ChatMessage> LastTurn() => LastTurns(1);

    /// <summary>
    /// A copy of the last <paramref name="count"/> turns: from the <paramref name="count"/>-th-last
    /// <see cref="IsTurnStart"/> message to the end, fewer when the list holds fewer (a compact's
    /// summary message is user-role, so it is a turn here). Empty with no turn held. The
    /// <c>Reflection window</c> a reflection reads (<c>SkillLearner</c>).
    /// </summary>
    public List<ChatMessage> LastTurns(int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        int start = -1;
        int seen = 0;
        for (int i = _messages.Count - 1; i >= 0; i--)
        {
            if (IsTurnStart(_messages[i]))
            {
                start = i;
                if (++seen == count)
                {
                    break;
                }
            }
        }

        return start < 0 ? [] : _messages.GetRange(start, _messages.Count - start);
    }

    /// <summary>
    /// Withdraws the last turn: everything from the last <see cref="IsTurnStart"/> message to the
    /// end is dropped (the user's message and, on a first turn, the opening call/result pairs
    /// after it, so the next turn opens as the first one again). What an ESC before the model's
    /// first event does, the line going back to the user. False with no turn held. A turn
    /// <see cref="Trim"/> dropped off the front when this one landed is not brought back.
    /// </summary>
    public bool RemoveLastTurn()
    {
        int start = _messages.FindLastIndex(IsTurnStart);
        if (start < 0)
        {
            return false;
        }

        _messages.RemoveRange(start, _messages.Count - start);
        Recount();
        return true;
    }

    /// <summary>A fresh list for one request: system prompt first, then a copy of the transcript.</summary>
    public List<ChatMessage> BuildRequest()
    {
        var request = new List<ChatMessage>(_messages.Count + 1);
        if (!string.IsNullOrWhiteSpace(SystemPrompt))
        {
            request.Add(new ChatMessage(ChatRole.System, SystemPrompt));
        }

        request.AddRange(_messages);
        return request;
    }

    private void Recount() => _turnCount = _messages.Count(IsTurnStart);

    private void Trim()
    {
        Recount();
        if (MaxTurns is not { } cap)
        {
            return;
        }

        int turns = TurnCount;
        int before = turns;
        while (turns > cap)
        {
            int firstUser = _messages.FindIndex(IsTurnStart);
            int nextUser = _messages.FindIndex(firstUser + 1, IsTurnStart);
            if (nextUser < 0)
            {
                break;
            }

            // Everything before the next user message belongs to the oldest turn (or to a
            // pre-turn prefix that has no user message at all). Drop it whole.
            _messages.RemoveRange(0, nextUser);
            turns--;
        }

        Recount();
        // One turn per message is the steady state at the cap; more at once means the cap just
        // came down (the window lost on a reconnect, the setting lowered), worth a line in the log.
        if (before - TurnCount > 1)
        {
            DiagnosticLog.Info(Category, "trimmed " + (before - TurnCount).ToString(CultureInfo.InvariantCulture) + " turns to the cap of " + cap.ToString(CultureInfo.InvariantCulture));
        }
    }
}
