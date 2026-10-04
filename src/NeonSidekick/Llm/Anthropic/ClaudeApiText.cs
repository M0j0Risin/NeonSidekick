namespace NeonSidekick.Llm.Anthropic;

/// <summary>The Anthropic API's user-visible words (2026-09-27), in one place so the tests can pin them.</summary>
public static class ClaudeApiText
{
    /// <summary>
    /// The saved LLM URL is the Anthropic API but it is not offered (the switch off, or no key that reads): the connect
    /// looks for a server as a blank URL would. Pinned.
    /// </summary>
    public const string NotOfferedWarning =
        "The LLM URL is the Anthropic API, but Anthropic API is off or has no key (/settings › Anthropic); looking for a server instead.";

    /// <summary><c>/server https://api.anthropic.com</c> while the Anthropic API is not offered. Pinned.</summary>
    public const string NotOfferedError =
        "The Anthropic API is not offered: turn on Anthropic API and set Anthropic API key in /settings › Anthropic.";
}
