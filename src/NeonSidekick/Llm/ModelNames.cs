namespace NeonSidekick.Llm;

/// <summary>
/// A model id as the status bar names it (2026-11-22, shared that day with the advisor lines, from the user's screenshot of
/// <c>gittensor-model-hub/Qwen3.8-27B-NVFP4-RTX5090</c> shown whole in the Advisor list while the status bar said
/// <c>Qwen3.8-27B-NVFP4-RTX5090</c>). Pure.
/// </summary>
public static class ModelNames
{
    /// <summary>
    /// The id's last path segment (LM Studio's <c>lyf/Qwen…</c>, llama.cpp's file path): <c>Qwen3-30B</c>; an id with no <c>/</c> or
    /// <c>\</c>, or one ending in one, as it is; empty for none.
    /// </summary>
    public static string Short(string? modelId)
    {
        if (string.IsNullOrEmpty(modelId))
        {
            return "";
        }

        int cut = modelId.LastIndexOfAny(['/', '\\']);
        return cut >= 0 && cut < modelId.Length - 1 ? modelId[(cut + 1)..] : modelId;
    }
}
