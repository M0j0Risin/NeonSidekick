using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using NeonSidekick.Llm;

namespace NeonSidekick.Sessions;

/// <summary>One message of a stored history: the role's wire word, the carrier mark and its parts.</summary>
public sealed class StoredMessage
{
    public string Role { get; set; } = "";

    /// <summary>The <see cref="ConversationHistory.CarrierKey"/> tag (a <c>view_image</c> carrier), kept off the wire and off the turn count.</summary>
    public bool Carrier { get; set; }

    public List<StoredPart> Parts { get; set; } = new();
}

/// <summary>
/// One content part: <see cref="Kind"/> picks which fields are read — <c>text</c> (<see cref="Text"/>),
/// <c>image</c> (<see cref="Bytes"/> base64 + <see cref="MediaType"/>), <c>call</c> (<see cref="CallId"/>,
/// <see cref="Name"/>, <see cref="Arguments"/> as the arguments' JSON object) or <c>result</c>
/// (<see cref="CallId"/>, <see cref="Text"/>, <see cref="SkillResult"/> for the compactor's tag).
/// </summary>
public sealed class StoredPart
{
    public const string TextKind = "text";
    public const string ImageKind = "image";
    public const string CallKind = "call";
    public const string ResultKind = "result";

    public string Kind { get; set; } = "";
    public string? Text { get; set; }
    public string? Bytes { get; set; }
    public string? MediaType { get; set; }
    public string? CallId { get; set; }
    public string? Name { get; set; }
    public string? Arguments { get; set; }
    public bool SkillResult { get; set; }
}

/// <summary>The document a session row's <c>history_json</c> holds.</summary>
public sealed class StoredHistory
{
    public int SchemaVersion { get; set; } = 1;
    public List<StoredMessage> Messages { get; set; } = new();

    /// <summary>Plan mode as the session left it (2026-09-26, <see cref="Plans.PlanSession"/>); absent while it was off, so a row written without plan mode reads the same as ever.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StoredPlan? Plan { get; set; }

    /// <summary>The approved plan being carried out (2026-09-26, round two): its path and requirement, until every step is ticked; absent otherwise.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StoredPlan? Executing { get; set; }
}

/// <summary>A session's plan mode (2026-09-26): the requirement, and the plan's file, title, revision and first save once presented.</summary>
public sealed class StoredPlan
{
    public string Requirement { get; set; } = "";
    public string? Path { get; set; }
    public string? Title { get; set; }
    public int Revision { get; set; }
    public DateTimeOffset? Created { get; set; }
}

/// <summary>
/// The AOT-safe round trip between <see cref="ConversationHistory.Messages"/> and the JSON the
/// session store keeps. <see cref="ChatMessage"/> itself cannot be serialised here: reflection
/// serialisation is off, and the two marks the app keeps in <c>AdditionalProperties</c>
/// (<see cref="ConversationHistory.CarrierKey"/>, <see cref="ConversationHistory.SkillResultKey"/>)
/// would come back as <see cref="JsonElement"/>s, which <see cref="ConversationHistory.IsImageCarrier"/>
/// does not read. So each message becomes a <see cref="StoredMessage"/> of typed parts — text, an
/// image's bytes, a tool call's id + name + arguments, a tool result's id + text + skill tag — and
/// nothing else (a usage or reasoning part is not conversation). Call arguments go out through
/// <see cref="Assistant.SerializeArguments"/> and come back as one <see cref="JsonElement"/> per
/// property, the shape the server hands the adapter. Pure; the store owns the I/O.
/// </summary>
public static class SessionHistory
{
    /// <summary>The stored form of <paramref name="messages"/>, compact JSON.</summary>
    public static string ToJson(IReadOnlyList<ChatMessage> messages, StoredPlan? plan = null, StoredPlan? executing = null)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var document = new StoredHistory { Plan = plan, Executing = executing };
        foreach (var message in messages)
        {
            document.Messages.Add(Store(message));
        }

        return JsonSerializer.Serialize(document, SessionJsonContext.Default.StoredHistory);
    }

    /// <summary>The messages of <paramref name="json"/>; a message with no readable part is dropped. Throws <see cref="JsonException"/> on a document that is not one.</summary>
    public static List<ChatMessage> FromJson(string json) => FromJson(json, out _);

    /// <summary><see cref="FromJson(string)"/>, with the plan mode the session left (null when it was off, 2026-09-26).</summary>
    public static List<ChatMessage> FromJson(string json, out StoredPlan? plan) => FromJson(json, out plan, out _);

    /// <summary><see cref="FromJson(string, out StoredPlan)"/>, with the approved plan still being carried out (2026-09-26).</summary>
    public static List<ChatMessage> FromJson(string json, out StoredPlan? plan, out StoredPlan? executing)
    {
        ArgumentNullException.ThrowIfNull(json);
        var document = JsonSerializer.Deserialize(json, SessionJsonContext.Default.StoredHistory) ?? throw new JsonException("The stored history is empty.");
        plan = document.Plan;
        executing = document.Executing;
        var messages = new List<ChatMessage>(document.Messages.Count);
        foreach (var stored in document.Messages)
        {
            if (Restore(stored) is { } message)
            {
                messages.Add(message);
            }
        }

        return messages;
    }


    internal static StoredMessage Store(ChatMessage message)
    {
        var stored = new StoredMessage { Role = message.Role.Value, Carrier = ConversationHistory.IsImageCarrier(message) };
        foreach (var content in message.Contents)
        {
            switch (content)
            {
                case TextContent text:
                    stored.Parts.Add(new StoredPart { Kind = StoredPart.TextKind, Text = text.Text });
                    break;
                case DataContent data when data.HasTopLevelMediaType("image"):
                    stored.Parts.Add(new StoredPart { Kind = StoredPart.ImageKind, Bytes = Convert.ToBase64String(data.Data.Span), MediaType = data.MediaType });
                    break;
                case FunctionCallContent call:
                    stored.Parts.Add(new StoredPart { Kind = StoredPart.CallKind, CallId = call.CallId, Name = call.Name, Arguments = Assistant.SerializeArguments(call.Arguments) });
                    break;
                case FunctionResultContent result:
                    stored.Parts.Add(new StoredPart { Kind = StoredPart.ResultKind, CallId = result.CallId, Text = result.Result as string ?? result.Result?.ToString() ?? "", SkillResult = ConversationHistory.IsSkillResult(result) });
                    break;
            }
        }

        return stored;
    }

    internal static ChatMessage? Restore(StoredMessage stored)
    {
        var contents = new List<AIContent>(stored.Parts.Count);
        foreach (var part in stored.Parts)
        {
            switch (part.Kind)
            {
                case StoredPart.TextKind:
                    contents.Add(new TextContent(part.Text ?? ""));
                    break;
                case StoredPart.ImageKind when part.Bytes is not null && !string.IsNullOrEmpty(part.MediaType):
                    contents.Add(new DataContent(Convert.FromBase64String(part.Bytes), part.MediaType));
                    break;
                case StoredPart.CallKind when part.CallId is not null && part.Name is not null:
                    contents.Add(new FunctionCallContent(part.CallId, part.Name, ParseArguments(part.Arguments)));
                    break;
                case StoredPart.ResultKind when part.CallId is not null:
                    var result = new FunctionResultContent(part.CallId, part.Text ?? "");
                    if (part.SkillResult)
                    {
                        result.AdditionalProperties = new AdditionalPropertiesDictionary { [ConversationHistory.SkillResultKey] = true };
                    }

                    contents.Add(result);
                    break;
            }
        }

        if (contents.Count == 0)
        {
            return null;
        }

        var message = new ChatMessage(new ChatRole(stored.Role), contents);
        if (stored.Carrier)
        {
            message.AdditionalProperties = new AdditionalPropertiesDictionary { [ConversationHistory.CarrierKey] = true };
        }

        return message;
    }

    /// <summary>The call's arguments back as the adapter shapes them: one detached <see cref="JsonElement"/> per property; null for no object.</summary>
    private static Dictionary<string, object?>? ParseArguments(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            arguments[property.Name] = property.Value.Clone();
        }

        return arguments;
    }
}
