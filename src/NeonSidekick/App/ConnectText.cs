namespace NeonSidekick.App;

/// <summary>
/// The Connect a model page's rows (2026-10-04, <see cref="ChatScreen.ConnectRows"/>; here since 2026-10-22, a review: the scan row's note changed in
/// place, out of reach of a test and of the help that names the same rows). Each row's label and its dim note.
/// </summary>
public static class ConnectText
{
    public const string ScanLabel = "Scan for local servers";

    /// <summary>The scan row's note (2026-10-21: the scan mode is asked first while it is off).</summary>
    public const string ScanNote = "LM Studio, Ollama, llama.cpp, vLLM… choose where to look";

    public const string UrlLabel = "Enter a server's URL";

    public const string UrlNote = "any OpenAI-compatible server";

    public const string EmbeddedLabel = "Download an embedded model";

    public const string EmbeddedNote = "selected GGUF models from Hugging Face";

    public const string DockerLabel = "Connect Docker container";

    public const string DockerNote = "a vLLM or SGLang container in Docker Desktop";

    public const string AnthropicLabel = "Use the Anthropic API";

    public const string OpenAILabel = "Use the OpenAI API";

    /// <summary>The two hosted APIs' note.</summary>
    public const string ApiNote = "your API key, billed per message";

    public const string ClaudeCliLabel = "Use Claude Code";

    public const string ClaudeCliNote = "the claude CLI on this machine, with your Claude subscription";

    public const string NotNowLabel = "Not now";

    public const string NotNowNote = "/server connects later";
}
