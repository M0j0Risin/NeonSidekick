namespace NeonSidekick.Llm;

/// <summary>
/// The editable operating rules: <c>operata.md</c> in the profile directory. When the file exists
/// and has text, that text replaces <see cref="Assistant.OperatingRules"/> — the whole rules block
/// that follows the persona, so the operator can rewrite how the sidekick answers and uses its
/// tools. Absent or blank, the default rules apply. <c>/operata</c> seeds the file with the default
/// so the edit starts from the sentences being replaced. The mechanics are <see cref="PromptFile"/>'s.
/// </summary>
public sealed class OperataFile : PromptFile
{
    public const string FileName = "operata.md";

    /// <summary>Characters kept. Rules can run longer than a persona (the default is about 1,150); the prompt goes out on every request.</summary>
    public const int MaxLength = 8000;

    public const string Category = "Operata";

    /// <param name="directory">The profile directory; the file is <see cref="FileName"/> under it.</param>
    public OperataFile(string directory)
        : base(directory, FileName, Assistant.OperatingRules, Category, "Operating rules", MaxLength)
    {
    }

    /// <summary>The rules text as the prompt carries it: CRLF folded to LF, trimmed, cut at <see cref="MaxLength"/> with an ellipsis. Pure; pinned.</summary>
    public static string Normalize(string raw) => Normalize(raw, out _);

    public static string Normalize(string raw, out bool truncated) => Normalize(raw, MaxLength, out truncated);

    public static string TruncatedWarning(int length) => TruncatedWarning(FileName, MaxLength, length);

    public static string UnreadableWarning(string detail) => UnreadableWarning(FileName, "operating rules", detail);
}
