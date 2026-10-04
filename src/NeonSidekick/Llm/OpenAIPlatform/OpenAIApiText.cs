namespace NeonSidekick.Llm.OpenAIPlatform;

/// <summary>The OpenAI API's user-visible words (2026-10-03), in one place so the tests can pin them.</summary>
public static class OpenAIApiText
{
    /// <summary>
    /// The saved LLM URL is the OpenAI API but it is not offered (the switch off, or no key that reads): the connect looks
    /// for a server as a blank URL would. Pinned.
    /// </summary>
    public const string NotOfferedWarning =
        "The LLM URL is the OpenAI API, but OpenAI API is off or has no key (/settings › OpenAI); looking for a server instead.";

    /// <summary><c>/server https://api.openai.com</c> while the OpenAI API is not offered. Pinned.</summary>
    public const string NotOfferedError =
        "The OpenAI API is not offered: turn on OpenAI API and set OpenAI API key in /settings › OpenAI.";

    /// <summary>What a 401/403 from the OpenAI API's model list adds to the probe's detail. Pinned.</summary>
    public const string KeyHint = "check OpenAI API key in /settings › OpenAI";

    /// <summary>The log line for a reply cut at the output cap. Pinned.</summary>
    public static string MaxTokensWarning(string model) =>
        $"The OpenAI API reply ({model}) stopped at its output cap; raise OpenAI API max tokens in /settings › OpenAI (0 = the model's own) if replies are cut short.";
}
