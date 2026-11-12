using System.Globalization;
using NeonSidekick.UI;

namespace NeonSidekick.Midi;

/// <summary>What a <c>/midi</c> line asks for.</summary>
public enum MidiVerb
{
    /// <summary>Bare: the pane.</summary>
    Pane,

    /// <summary><c>/midi learn [&lt;command&gt;]</c>: the learn, the command picked or given.</summary>
    Learn,

    /// <summary><c>/midi list</c>: the binding lines.</summary>
    List,

    /// <summary><c>/midi on</c>.</summary>
    On,

    /// <summary><c>/midi off</c>.</summary>
    Off,

    /// <summary><c>/midi forget &lt;n&gt;</c>.</summary>
    Forget,
}

/// <summary>A parsed <c>/midi</c> line: the verb, a learn's command as typed, a forget's number (1-based).</summary>
public sealed record MidiArgs(MidiVerb Verb, string? Command = null, int Number = 0);

/// <summary>
/// <c>/midi</c> (2026-11-12, the MIDI plan): bare the pane, <c>learn [&lt;command&gt;]</c> the learn (with a command, it binds that line
/// as typed, arguments and all), <c>list</c> the lines, <c>on</c>/<c>off</c> the switch, <c>forget &lt;n&gt;</c> a binding by its number in
/// the list. The words any case. Pure.
/// </summary>
public static class MidiCommand
{
    public const string Word = "/midi";
    public const string LearnWord = "learn";
    public const string ListWord = "list";
    public const string OnWord = "on";
    public const string OffWord = "off";
    public const string ForgetWord = "forget";

    /// <summary>The verbs completion offers.</summary>
    public static readonly IReadOnlyList<string> Verbs = [LearnWord, ListWord, OnWord, OffWord, ForgetWord];

    /// <summary>The line parsed, or null for a form <c>/midi</c> doesn't take (<see cref="MidiText.Usage"/>); a forget without a number is <c>Number</c> 0.</summary>
    public static MidiArgs? Parse(string args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string text = args.Trim();
        if (text.Length == 0)
        {
            return new MidiArgs(MidiVerb.Pane);
        }

        int space = text.IndexOfAny([' ', '\t']);
        string word = space < 0 ? text : text[..space];
        string rest = space < 0 ? "" : text[(space + 1)..].Trim();
        if (Is(word, LearnWord))
        {
            return new MidiArgs(MidiVerb.Learn, rest.Length == 0 ? null : rest);
        }

        if (Is(word, ForgetWord))
        {
            if (rest.Length == 0)
            {
                return new MidiArgs(MidiVerb.Forget);
            }

            return int.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n > 0 ? new MidiArgs(MidiVerb.Forget, Number: n) : null;
        }

        if (rest.Length > 0)
        {
            return null;
        }

        return Is(word, ListWord) ? new MidiArgs(MidiVerb.List)
            : Is(word, OnWord) ? new MidiArgs(MidiVerb.On)
            : Is(word, OffWord) ? new MidiArgs(MidiVerb.Off)
            : null;
    }

    /// <summary>
    /// <c>/midi</c>'s completion (2026-11-12): the verbs, then after <c>learn </c> the commands (<paramref name="commands"/>, the <c>/</c>
    /// list as the input line has it), then after <c>forget </c> each binding's number. Pure.
    /// </summary>
    public static IReadOnlyList<CompletionItem> Complete(string argText, IReadOnlyList<CompletionItem> commands, IReadOnlyList<MidiBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(argText);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(bindings);
        string learn = LearnWord + " ";
        if (argText.StartsWith(learn, StringComparison.OrdinalIgnoreCase))
        {
            string typed = argText[learn.Length..];
            if (typed.Contains(' ', StringComparison.Ordinal))
            {
                return [];
            }

            var items = commands.Where(c => !string.Equals(c.Text, Word, StringComparison.OrdinalIgnoreCase))
                .Select(c => new CompletionItem(learn + c.Text, c.Note)).ToList();
            return MentionCompleter.Matches(items, argText);
        }

        string forget = ForgetWord + " ";
        if (argText.StartsWith(forget, StringComparison.OrdinalIgnoreCase))
        {
            var items = bindings.Select((b, i) => new CompletionItem(forget + (i + 1).ToString(CultureInfo.InvariantCulture), MidiText.Describe(b.Trigger) + " → " + b.Command)).ToList();
            return MentionCompleter.Matches(items, argText);
        }

        if (argText.Contains(' ', StringComparison.Ordinal))
        {
            return [];
        }

        return MentionCompleter.Matches(Verbs.Select(v => new CompletionItem(v, MidiText.VerbHint(v))).ToList(), argText);
    }

    private static bool Is(string word, string verb) => string.Equals(word, verb, StringComparison.OrdinalIgnoreCase);
}
