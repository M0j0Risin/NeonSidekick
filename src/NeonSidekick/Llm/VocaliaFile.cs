namespace NeonSidekick.Llm;

/// <summary>
/// The editable voice directive: <c>vocalia.md</c> in the profile directory. When the file exists
/// and has text, that text is the block appended last while a turn speaks. A silent turn carries
/// no directive whatever the file says, and headless never speaks.
///
/// <para>No default since 2026-10-03 (the user's call): absent or blank, a spoken turn carries no
/// directive at all, and <c>/vocalia</c> creates the file empty. The built-in directive it replaced
/// ("shown on screen and also read aloud … keep it short and in plain spoken language") opened with
/// a sentence exempting the tool channel, and that lesson is worth keeping for whoever writes one: a
/// directive that restricts what the model says reads, to a small model, as a rule a tool call could
/// break, and the failure is a spoken answer <em>invented</em> instead of looked up — so a directive
/// that restricts should say it applies to what is said to the user, never to tool calls. Nor need
/// one ban formatting (since 2026-09-26): the voice skips fenced code (<see cref="Speech.CodeBlockFilter"/>)
/// and tables (<see cref="Speech.TableFilter"/>) and strips emoji and markdown
/// (<see cref="Speech.SpeakableText"/>) on its own. The mechanics are <see cref="PromptFile"/>'s.</para>
/// </summary>
public sealed class VocaliaFile : PromptFile
{
    public const string FileName = "vocalia.md";

    /// <summary>Characters kept. A directive is a paragraph; the prompt goes out on every spoken request.</summary>
    public const int MaxLength = 4000;

    public const string Category = "Vocalia";

    /// <param name="directory">The profile directory; the file is <see cref="FileName"/> under it.</param>
    public VocaliaFile(string directory)
        : base(directory, FileName, "", Category, "Voice directive", MaxLength)
    {
    }

    /// <summary><c>no voice directive</c>: without the file a spoken turn carries none (2026-10-03).</summary>
    public override string DefaultInUse => "no voice directive";

    /// <summary>The directive text as the prompt carries it: CRLF folded to LF, trimmed, cut at <see cref="MaxLength"/> with an ellipsis. Pure; pinned.</summary>
    public static string Normalize(string raw) => Normalize(raw, out _);

    public static string Normalize(string raw, out bool truncated) => Normalize(raw, MaxLength, out truncated);

    public static string TruncatedWarning(int length) => TruncatedWarning(FileName, MaxLength, length);

    public static string UnreadableWarning(string detail) => UnreadableWarningUsing(FileName, "no voice directive", detail);
}
