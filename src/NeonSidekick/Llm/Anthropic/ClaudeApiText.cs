namespace NeonSidekick.Llm.Anthropic;

/// <summary>The Claude API's user-visible words (2026-09-27), in one place so the tests can pin them.</summary>
public static class ClaudeApiText
{
    /// <summary>
    /// The saved LLM URL is the Claude API but it is not offered (the switch off, or no key that reads): the connect
    /// looks for a server as a blank URL would. Pinned.
    /// </summary>
    public const string NotOfferedWarning =
        "The LLM URL is the Claude API, but Claude API is off or has no key (/settings › Claude (API)); looking for a server instead.";

    /// <summary><c>/server https://api.anthropic.com</c> while the Claude API is not offered. Pinned.</summary>
    public const string NotOfferedError =
        "The Claude API is not offered: turn on Claude API and set Claude API key in /settings › Claude (API).";
}
