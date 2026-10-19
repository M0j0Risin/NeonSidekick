namespace NeonSidekick.UI;

/// <summary><c>/mark</c>'s words (2026-10-18, <see cref="RuleMarks"/>): its usage, its error, its notices and what bare <c>/mark</c> says. Pinned.</summary>
public static class MarkText
{
    /// <summary>The command word.</summary>
    public const string Word = "/mark";

    /// <summary>The help line's summary.</summary>
    public const string HelpSummary = "color the rules around the draft line";

    /// <summary>The names <c>/mark</c> takes, comma-separated, then <c>default</c>.</summary>
    public static string Choices => string.Join(", ", RuleMarks.Names) + " or " + RuleMarks.Default;

    /// <summary>What bare <c>/mark</c> and an error teach.</summary>
    public static string Usage => "Usage: /mark <color>: " + Choices;

    /// <summary>A word that is no color. Pinned.</summary>
    public static string UnknownError(string word) => $"No color named '{word}': /mark takes {Choices}";

    /// <summary>The rules marked. Pinned.</summary>
    public static string SetNotice(string name) => $"The rules around the draft line are {name} now; /mark default puts the theme's back.";

    /// <summary>The mark taken off. Pinned.</summary>
    public const string DefaultNotice = "The rules around the draft line are the theme's again.";

    /// <summary>Bare <c>/mark</c>: the mark in force (or none) and the choices. Pinned.</summary>
    public static string ShowLine(string? name) =>
        (name is null ? "The rules are the theme's" : $"The rules are {name}") + "; /mark takes " + Choices + ".";
}
