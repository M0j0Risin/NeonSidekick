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
    /// The <see cref="AIContent.AdditionalProperties"/> key that marks a camera picture's part (2026-10-02; the value is the
    /// photo's path): <c>SessionHistory</c> stores a line naming it instead, unless <c>Camera keep in sessions</c> is on.
    /// </summary>
    public const string CameraKey = "neon.camera";

    /// <summary>
    /// The <see cref="AIContent.AdditionalProperties"/> key that holds a picture part's path or name, a <see cref="string"/>
    /// (2026-10-03): what <see cref="PictureBudget.LeftOut"/> names when the picture is taken out of a request, so the model
    /// can look again. Kept in a stored session (<c>StoredPart.Path</c>). Never on the wire.
    /// </summary>
    public const string PathKey = "neon.path";

    /// <summary>
    /// The <see cref="ChatMessage.AdditionalProperties"/> key on a carrier that holds the tool (or tools) its pictures came
    /// from, a <see cref="string"/> (2026-10-03): what a prune's stub credits (<see cref="ConversationCompactor.PrunedImageStub"/>),
    /// once always <c>view_image</c>. Kept in a stored session (<c>StoredMessage.Source</c>). Never on the wire.
    /// </summary>
    public const string SourceKey = "neon.imageSource";

    /// <summary>An attachment's image part: its bytes and type, its path under <see cref="PathKey"/>, marked <see cref="CameraKey"/> when it came off the camera.</summary>
    public static DataContent ImagePart(ImageAttachment image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var part = new DataContent(image.Bytes, image.MediaType);
        if (!string.IsNullOrWhiteSpace(image.Path))
        {
            part.AdditionalProperties = new AdditionalPropertiesDictionary { [PathKey] = image.Path };
        }

        if (image.Camera)
        {
            (part.AdditionalProperties ??= new AdditionalPropertiesDictionary())[CameraKey] = image.Path;
        }

        return part;
    }

    /// <summary>The photo's path when <paramref name="content"/> is a camera picture's part (<see cref="CameraKey"/>); null otherwise.</summary>
    public static string? CameraPath(AIContent content) =>
        content is DataContent && content.AdditionalProperties?.TryGetValue(CameraKey, out var path) == true ? path as string ?? "" : null;

    /// <summary>A picture part's path or name (<see cref="PathKey"/>); null when it carries none.</summary>
    public static string? PicturePath(AIContent content) =>
        content.AdditionalProperties?.TryGetValue(PathKey, out var path) == true && path is string { Length: > 0 } text ? text : null;

    /// <summary>The tool a carrier's pictures came from (<see cref="SourceKey"/>); null when it names none (a session stored before it did).</summary>
    public static string? CarrierSource(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message.AdditionalProperties?.TryGetValue(SourceKey, out var source) == true && source is string { Length: > 0 } text ? text : null;
    }

    /// <summary>
    /// The <see cref="AIContent.AdditionalProperties"/> key that marks a <see cref="FunctionResultContent"/>
    /// holding a loaded skill's instructions (value <c>true</c>; <see cref="Assistant.ResultContent"/>),
    /// what the compactor's protection keeps (<c>Skill compact mode</c>). Never on the wire.
    /// </summary>
    public const string SkillResultKey = "neon.skillResult";

    /// <summary>
    /// The <see cref="ChatMessage.AdditionalProperties"/> key on a turn's user message that holds the session store's
    /// ordinal for that turn, an <see cref="int"/> (2026-09-30, <c>/rewind</c>). The store's <c>turns</c> rows and the
    /// history's turns do not line up one for one: a compact's summary turn has no row, <see cref="Trim"/> drops turns
    /// whose rows stay, and logging can start part way through a conversation. So the row a turn wrote is stamped on the
    /// turn itself (<see cref="SetTurnOrdinal"/>), kept in the stored history (<c>StoredMessage.Ordinal</c>), and a rewind
    /// cuts the store where the history is cut. Never sent.
    /// </summary>
    public const string TurnOrdinalKey = "neon.turnOrdinal";

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

    /// <summary>The store ordinal stamped on <paramref name="message"/> (<see cref="TurnOrdinalKey"/>); null when none is.</summary>
    public static int? TurnOrdinal(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message.AdditionalProperties is { } properties && properties.TryGetValue(TurnOrdinalKey, out object? value) && value is int ordinal ? ordinal : null;
    }

    /// <summary>Stamps <paramref name="ordinal"/> on <paramref name="message"/> (<see cref="TurnOrdinalKey"/>), replacing any earlier stamp.</summary>
    public static void SetTurnOrdinal(ChatMessage message, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(message);
        (message.AdditionalProperties ??= new AdditionalPropertiesDictionary())[TurnOrdinalKey] = ordinal;
    }

    /// <summary>
    /// The index of the first message of the turn in flight: the last user message that is neither a tool result nor an
    /// image carrier — what the user typed — or 0 when there is none. Thinking is sent back after it: the Anthropic API's
    /// signed blocks (<see cref="Anthropic.AnthropicRequest"/>) and, since 2026-09-28, a local server's
    /// <c>reasoning_content</c> (<see cref="OpenAICompatibleChatClient.WithReasoningBack"/>).
    /// </summary>
    public static int InFlightStart(IReadOnlyList<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        for (int i = messages.Count - 1; i >= 0; i--)
        {
            var message = messages[i];
            if (message.Role == ChatRole.User && !IsImageCarrier(message) && !message.Contents.Any(c => c is FunctionResultContent))
            {
                return i;
            }
        }

        return 0;
    }

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
            contents.Add(ImagePart(image));
        }

        var properties = new AdditionalPropertiesDictionary { [CarrierKey] = true };
        if (!string.IsNullOrWhiteSpace(source))
        {
            properties[SourceKey] = source;
        }

        _messages.Add(new ChatMessage(ChatRole.User, contents) { AdditionalProperties = properties });
    }

    /// <summary>
    /// Takes pictures out of the transcript itself to keep it within <paramref name="budget"/> (2026-10-03,
    /// <see cref="PictureBudget.Apply"/>): in history, not just on the wire, so the stored session and the process's memory
    /// shrink with the request. Returns what was taken out; message order and count never change.
    /// </summary>
    public PictureTrim ApplyPictureBudget(PictureBudget budget) => Swap(budget.Apply(_messages));

    /// <summary>
    /// <see cref="ApplyPictureBudget"/> to explicit limits (<see cref="PictureBudget.TakeOut"/>): the retry after a
    /// request the server dropped as it was sent keeps half the pictures that request carried.
    /// </summary>
    public PictureTrim TakePicturesOut(int keepPictures, long keepBytes) => Swap(PictureBudget.TakeOut(_messages, keepPictures, keepBytes));

    private PictureTrim Swap(PictureTrim trim)
    {
        if (trim.Pictures > 0)
        {
            for (int i = 0; i < _messages.Count; i++)
            {
                _messages[i] = trim.Messages[i];
            }
        }

        return trim;
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
                contents.Add(ImagePart(image));
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

        TruncateAt(start);
        return true;
    }

    /// <summary>The last <see cref="IsTurnStart"/> message: the user's message of the turn held last; null with no turn held.</summary>
    public ChatMessage? LastTurnStart()
    {
        int start = _messages.FindLastIndex(IsTurnStart);
        return start < 0 ? null : _messages[start];
    }

    /// <summary>The index into <see cref="Messages"/> of each <see cref="IsTurnStart"/> message, oldest first.</summary>
    public List<int> TurnStarts()
    {
        var starts = new List<int>();
        for (int i = 0; i < _messages.Count; i++)
        {
            if (IsTurnStart(_messages[i]))
            {
                starts.Add(i);
            }
        }

        return starts;
    }

    /// <summary>
    /// Drops every message from <paramref name="index"/> to the end and returns them in order (2026-09-30, <c>/rewind</c>).
    /// Give it a turn start (<see cref="TurnStarts"/>) so the cut falls between turns and never splits a call from its
    /// result. A cut at the first turn takes the opening pairs with it, and the next turn seeds them again because the
    /// count is 0. <see cref="RemoveLastTurn"/> is this cut at the last turn.
    /// </summary>
    public List<ChatMessage> TruncateAt(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, _messages.Count);
        var removed = _messages.GetRange(index, _messages.Count - index);
        _messages.RemoveRange(index, _messages.Count - index);
        Recount();
        return removed;
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
