using NeonSidekick.Files;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// What a tool returns when it has pictures for the model — <see cref="ViewImageTool"/> when at least one loaded,
/// <see cref="GenerateImageTool"/> when ComfyUI made some (2026-09-24, when the record left <c>ViewImageTool.cs</c>
/// for a file of its own): the text that goes back as the tool result and the pictures <see cref="Assistant"/> puts in
/// the carrier message after it (<see cref="ConversationHistory.AddToolImages"/>). Two parts because an OpenAI-format
/// tool message is text only: the pictures cannot ride the result.
/// </summary>
public sealed record ToolImageResult(string Text, IReadOnlyList<ImageAttachment> Images);
