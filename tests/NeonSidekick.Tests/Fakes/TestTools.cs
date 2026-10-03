using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Files;
using NeonSidekick.Llm.Tools;

namespace NeonSidekick.Tests.Fakes;

/// <summary>
/// The pattern every real tool follows: a hand-written schema parsed once, <c>Name</c>,
/// <c>Description</c> and <c>JsonSchema</c> overridden, arguments read by hand out of the
/// <see cref="JsonElement"/> values the adapter delivers. Never <c>AIFunctionFactory</c>.
/// </summary>
internal static class ToolSchema
{
    public static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    public static string ReadString(AIFunctionArguments arguments, string name)
    {
        if (!arguments.TryGetValue(name, out var value) || value is null)
        {
            return "";
        }

        return value switch
        {
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString() ?? "",
            JsonElement element => element.GetRawText(),
            string s => s,
            _ => value.ToString() ?? "",
        };
    }
}

/// <summary>Echoes its <c>text</c> argument and remembers what it saw.</summary>
public sealed class EchoTool : AIFunction
{
    private static readonly JsonElement Schema = ToolSchema.Parse(
        """{"type":"object","properties":{"text":{"type":"string","description":"What to echo."}},"required":["text"]}""");

    public List<string> Received { get; } = new();

    public override string Name => "echo";

    public override string Description => "Echoes the text back.";

    public override JsonElement JsonSchema => Schema;

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        string text = ToolSchema.ReadString(arguments, "text");
        Received.Add(text);
        return new ValueTask<object?>("echo: " + text);
    }
}

/// <summary>Always throws, so the exception branch of the loop can be pinned.</summary>
public sealed class ThrowingTool : AIFunction
{
    private static readonly JsonElement Schema = ToolSchema.Parse("""{"type":"object","properties":{}}""");

    public override string Name => "boom";

    public override string Description => "Throws.";

    public override JsonElement JsonSchema => Schema;

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        => throw new InvalidOperationException("kaboom");
}

/// <summary>Answers a fixed text: the shape of a no-argument lookup such as the working directory.</summary>
public sealed class FixedTool(string name, string text) : AIFunction
{
    private static readonly JsonElement Schema = ToolSchema.Parse("""{"type":"object","properties":{}}""");

    public override string Name => name;

    public override string Description => "Answers " + text + ".";

    public override JsonElement JsonSchema => Schema;

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        => new(text);
}

/// <summary>Answers a <see cref="ToolImageResult"/> for the pictures it was given: the shape of <c>view_image</c>, without a file.</summary>
public sealed class PictureTool(IReadOnlyList<ImageAttachment> images, string name = "picture") : AIFunction
{
    public PictureTool(ImageAttachment image, string name = "picture") : this([image], name)
    {
    }

    private static readonly JsonElement Schema = ToolSchema.Parse("""{"type":"object","properties":{}}""");

    public override string Name => name;

    public override string Description => "Fetches a picture.";

    public override JsonElement JsonSchema => Schema;

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        => new(new ToolImageResult("saw " + string.Join(", ", images.Select(i => i.Path)), images));
}

/// <summary>A tool's answer as the model reads it: the sentence of a <see cref="ToolDiffResult"/> (2026-10-03, the file-writing tools), else the string.</summary>
public static class ToolAnswers
{
    public static string Text(object? answer) => answer switch
    {
        ToolDiffResult changed => changed.Text,
        ToolShownResult shown => shown.Text,
        _ => (string)answer!,
    };
}
