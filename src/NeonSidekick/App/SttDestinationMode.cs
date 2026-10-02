using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;

namespace NeonSidekick.App;

/// <summary>Where a spoken request goes (<see cref="SttDestinationMode"/>).</summary>
public enum SttTarget
{
    /// <summary>Sent to the model at once, as a typed line would be (what voice input always did).</summary>
    Chat,

    /// <summary>Added to the end of the input line's draft; Enter sends it.</summary>
    Draft,
}

/// <summary>
/// The setting <c>STT destination</c> (2026-10-02, the user's ask): what a transcript does — <c>chat</c> (the default, what voice
/// input always did: shown as a <c>›</c> row, remembered and sent to the model at once) or <c>draft</c> (appended to the end of
/// the input line's draft, a space between when the draft does not already end in whitespace, nothing sent, nothing remembered —
/// it is a typed line once Enter sends it). In <c>draft</c> mode the push-to-talk key and the idle wake word listen with text on
/// the row too (dictating onto a draft is the point), except a push-to-talk key the editor uses with text on the row
/// (<see cref="PushToTalkOverDraft"/>). The <see cref="BotChatLlmMode"/> shape: <see cref="Resolve"/> is the one place the
/// saved string becomes the enum.
/// </summary>
public static class SttDestinationMode
{
    /// <summary>Chat: the compiled default, what voice input did before the setting. Pinned.</summary>
    public const string Default = "chat";

    /// <summary>The modes in menu order.</summary>
    public static readonly string[] Names = { "chat", "draft" };

    private const string Category = "Voice";

    /// <summary>Trims and ignores case; false (and <see cref="SttTarget.Chat"/>, the default) for anything that is not one of <see cref="Names"/>.</summary>
    public static bool TryParse(string? text, out SttTarget target)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "chat": target = SttTarget.Chat; return true;
            case "draft": target = SttTarget.Draft; return true;
            default: target = SttTarget.Chat; return false;
        }
    }

    /// <summary>The menu hint next to a mode. Pinned.</summary>
    public static string Describe(string name) => name switch
    {
        "chat" => "a spoken request is sent to the model at once",
        "draft" => "a spoken request is added to the end of the draft, sent with Enter",
        _ => "",
    };

    /// <summary>The mode in force for <paramref name="effective"/>; an unknown saved value warns and uses <see cref="Default"/>.</summary>
    public static SttTarget Resolve(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        if (TryParse(effective.SttDestination, out var target))
        {
            return target;
        }

        DiagnosticLog.Warn(Category,
            $"{nameof(AppSettingsData.SttDestination)}='{effective.SttDestination}' is not one of {string.Join(", ", Names)}. Using {Default}.");
        return SttTarget.Chat;
    }

    /// <summary>
    /// <paramref name="spoken"/> at the end of <paramref name="draft"/> (the draft in its token form): joined as is when the
    /// draft is empty or already ends in whitespace (a space, a line break), else with one space between. Pure.
    /// </summary>
    public static string AppendToDraft(string draft, string spoken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(spoken);
        return draft.Length == 0 || char.IsWhiteSpace(draft[^1]) ? draft + spoken : draft + " " + spoken;
    }

    /// <summary>
    /// Whether the push-to-talk <paramref name="key"/> may listen with text on the row in <c>draft</c> mode: not Home, End,
    /// PageUp or PageDown, which the editor moves the cursor (or the transcript) with while there is a draft — those keep
    /// listening from an empty line only. Pure.
    /// </summary>
    public static bool PushToTalkOverDraft(ConsoleKey key) =>
        key is not (ConsoleKey.Home or ConsoleKey.End or ConsoleKey.PageUp or ConsoleKey.PageDown);
}
