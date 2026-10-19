using System.Globalization;
using System.Text.RegularExpressions;
using NeonSidekick.Plans;
using NeonSidekick.UI;

namespace NeonSidekick.Audits;

/// <summary>What a <c>/code-audit</c> line asks for.</summary>
public enum AuditVerb
{
    /// <summary>A new audit: the scope, focus, reviewer, count and brief.</summary>
    Start,

    /// <summary><c>open [&lt;name&gt;]</c>: the list, or one report.</summary>
    Open,

    /// <summary><c>fix [&lt;name&gt;]</c>: plan mode on a report's open findings.</summary>
    Fix,

    /// <summary><c>resume [&lt;name&gt;] [reviewer] [passes]</c>: more passes on a kept report.</summary>
    Resume,
}

/// <summary>
/// A <c>/code-audit</c> line taken apart (2026-10-18): the verb, a report's name, the reviewer and pass count when named, the scope (a
/// start's; null for the other verbs), the focus when named (null for the setting's), the brief as typed, or the error that refuses it.
/// </summary>
public sealed record AuditArguments(
    AuditVerb Verb,
    string? Name,
    PlanReviewer? Reviewer,
    int? Passes,
    AuditScopeRequest? Scope,
    IReadOnlyList<string>? Focus,
    string Brief,
    string? Error = null);

/// <summary>
/// <c>/code-audit</c>'s grammar (2026-10-18, <see cref="Pdf.PdfCommand"/>'s shape):
/// <code>
/// /code-audit [model|advisor|any|confirm] [&lt;passes&gt;] [diff|staged|branch [&lt;base&gt;]|&lt;path&gt; ...] [&lt;focus&gt; ...] [--] [&lt;brief&gt;]
/// /code-audit open [&lt;name&gt;]
/// /code-audit fix [&lt;name&gt;]
/// /code-audit resume [&lt;name&gt;] [model|advisor|any|confirm] [&lt;passes&gt;]
/// </code>
/// The line is split as <c>/pdf</c> splits its paths (<see cref="Printing.PrintCommand.Words"/>: blanks, a double-quoted stretch kept whole);
/// words come in any order; the first word that is none of reviewer, count, scope, path or focus starts the brief, and <c>--</c> makes the
/// rest the brief; the brief is the raw text from there, quotes and all. A focus word is a focus word even when a folder has its name
/// (<c>tests/</c> or <c>./tests</c> is the folder). A word that looks like a path and names nothing is refused, so a typo never becomes a
/// brief; a path in <c>.neon/</c> is refused by name. Pure over <c>existing</c>.
/// </summary>
public static partial class AuditCommand
{
    /// <summary>
    /// The line's meaning. <paramref name="existing"/> answers a word's <see cref="AuditPath"/> when it names a file or folder in the working
    /// directory, else null.
    /// </summary>
    public static AuditArguments Parse(string args, Func<string, string?> existing)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(existing);
        var words = Split(args);
        if (words.Count > 0)
        {
            string first = words[0].Word.ToLowerInvariant();
            if (first is AuditText.OpenWord or AuditText.FixWord)
            {
                return new AuditArguments(first == AuditText.OpenWord ? AuditVerb.Open : AuditVerb.Fix, words.Count > 1 ? words[1].Word : null, null, null, null, null, "");
            }

            if (first == AuditText.ResumeWord)
            {
                return ParseResume(words.Skip(1).Select(w => w.Word).ToList());
            }
        }

        PlanReviewer? reviewer = null;
        int? passes = null;
        var kind = AuditScopeKind.Tree;
        string? @base = null;
        var paths = new List<string>();
        var focus = new List<string>();
        bool all = false;
        string brief = "";
        for (int i = 0; i < words.Count; i++)
        {
            var (word, start, end) = words[i];
            string lower = word.ToLowerInvariant();
            if (word == AuditText.BriefSwitch)
            {
                brief = args[end..].Trim();
                break;
            }

            if (PlanText.ParseReviewer(word) is { } named)
            {
                if (reviewer is not null)
                {
                    return Refused(AuditText.TwoReviewers);
                }

                reviewer = named;
                continue;
            }

            if (IsNumber(word))
            {
                if (passes is not null)
                {
                    return Refused(AuditText.TwoCounts);
                }

                if (!int.TryParse(word, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count)
                    || count < Settings.AppSettingsData.MinCodeAuditPasses || count > Settings.AppSettingsData.MaxCodeAuditPasses)
                {
                    return Refused(AuditText.BadPasses(word));
                }

                passes = count;
                continue;
            }

            if (kind is AuditScopeKind.Tree && lower is AuditText.DiffWord or AuditText.StagedWord)
            {
                kind = lower == AuditText.DiffWord ? AuditScopeKind.Diff : AuditScopeKind.Staged;
                continue;
            }

            if (kind is AuditScopeKind.Tree && lower == AuditText.BranchWord)
            {
                kind = AuditScopeKind.Branch;
                if (i + 1 < words.Count && IsBase(words[i + 1].Word, existing))
                {
                    @base = words[++i].Word;
                }

                continue;
            }

            if (lower == AuditFocus.AllWord)
            {
                all = true;
                continue;
            }

            if (!LooksLikePath(word) && AuditFocus.Parse(word) is { } dimension)
            {
                if (!focus.Contains(dimension, StringComparer.Ordinal))
                {
                    focus.Add(dimension);
                }

                continue;
            }

            if (AppFolderWord(word))
            {
                return Refused(AuditText.AppFolderPath);
            }

            if (existing(word) is { } path)
            {
                if (AuditPath.InAppFolder(path))
                {
                    return Refused(AuditText.AppFolderPath);
                }

                if (!paths.Contains(path, AuditPath.Comparer))
                {
                    paths.Add(path);
                }

                continue;
            }

            if (LooksLikePath(word))
            {
                return Refused(AuditText.NoSuchPath(word));
            }

            // The first other word starts the brief (/botchat's convention): the raw text from it, quotes and all.
            brief = args[start..].Trim();
            break;
        }

        var scopeKind = kind == AuditScopeKind.Tree && paths.Count > 0 ? AuditScopeKind.Paths : kind;
        IReadOnlyList<string>? chosen = all ? AuditFocus.Dimensions : focus.Count > 0 ? AuditFocus.Normalize(focus) : null;
        return new AuditArguments(AuditVerb.Start, null, reviewer, passes, new AuditScopeRequest(scopeKind, paths, @base), chosen, brief);
    }

    /// <summary>
    /// The scope a report's header kept (its <c>scope:</c> words, <see cref="AuditScope.Words"/>), read back through the same grammar; the
    /// error names a path that is gone (renamed, deleted), so <c>resume</c> never audits a narrower scope in silence.
    /// </summary>
    public static (AuditScopeRequest? Scope, string? Error) ScopeFromHeader(string words, Func<string, string?> existing)
    {
        ArgumentNullException.ThrowIfNull(words);
        ArgumentNullException.ThrowIfNull(existing);
        var split = Split(words).Select(w => w.Word).ToList();
        if (split.Count == 0)
        {
            return (new AuditScopeRequest(AuditScopeKind.Tree, []), null);
        }

        var kind = AuditScopeKind.Paths;
        string? @base = null;
        int at = 0;
        switch (split[0].ToLowerInvariant())
        {
            case AuditText.DiffWord:
                kind = AuditScopeKind.Diff;
                at = 1;
                break;
            case AuditText.StagedWord:
                kind = AuditScopeKind.Staged;
                at = 1;
                break;
            case AuditText.BranchWord:
                kind = AuditScopeKind.Branch;
                @base = split.Count > 1 ? split[1] : null;
                at = 2;
                break;
        }

        var paths = new List<string>();
        foreach (string word in split.Skip(at))
        {
            if (existing(word) is not { } path)
            {
                return (null, AuditText.NoSuchPath(word));
            }

            paths.Add(path);
        }

        return (new AuditScopeRequest(kind, paths, @base), null);
    }

    /// <summary>
    /// Whether the line would start passes (2026-10-18, the <c>/botchat</c> refusal): every form but <c>open</c>, a line the parser would
    /// refuse included, so the botchat's refusal comes first.
    /// </summary>
    public static bool StartsPasses(string args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var words = Split(args);
        return words.Count == 0 || !string.Equals(words[0].Word, AuditText.OpenWord, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The completion list for the argument typed so far: the verbs as the first word, a report's name after <c>open</c>, <c>fix</c> or
    /// <c>resume</c>, and otherwise the reviewer, scope and focus words and the working directory's folders (a blank-holding folder in
    /// double quotes, so the line parses back to the same path).
    /// </summary>
    public static IReadOnlyList<CompletionItem> Complete(string argText, Func<IReadOnlyList<CompletionItem>>? reports, Func<string, IReadOnlyList<string>> folders)
    {
        ArgumentNullException.ThrowIfNull(argText);
        ArgumentNullException.ThrowIfNull(folders);
        int space = argText.LastIndexOf(' ');
        string head = space < 0 ? "" : argText[..(space + 1)];
        string last = space < 0 ? argText : argText[(space + 1)..];
        var typed = Split(head).Select(w => w.Word.ToLowerInvariant()).ToList();
        var items = new List<CompletionItem>();
        string first = typed.Count > 0 ? typed[0] : "";
        if (typed.Count == 0)
        {
            items.Add(new(AuditText.OpenWord, "list the reports, or show one"));
            items.Add(new(AuditText.FixWord, "plan the fixes for a report's open findings"));
            items.Add(new(AuditText.ResumeWord, "more passes on a kept report"));
        }

        if (typed.Count == 1 && first is AuditText.OpenWord or AuditText.FixWord or AuditText.ResumeWord)
        {
            items.AddRange(reports?.Invoke() ?? []);
        }

        if (first is AuditText.OpenWord or AuditText.FixWord)
        {
            return MentionCompleter.Matches(items.Select(i => new CompletionItem(head + i.Text, i.Note)).ToList(), argText);
        }

        items.AddRange(Reviewers);
        if (first != AuditText.ResumeWord)
        {
            items.Add(new(AuditText.DiffWord, "the working changes: unstaged, staged and untracked"));
            items.Add(new(AuditText.StagedWord, "the staged changes"));
            items.Add(new(AuditText.BranchWord, "the branch's commits since its base (main, else master)"));
            items.AddRange(AuditFocus.Dimensions.Select(d => new CompletionItem(d, "focus")));
            items.Add(new(AuditFocus.AllWord, "every focus"));
            items.AddRange(folders(last.TrimStart('"')).Select(f => new CompletionItem(f.Any(char.IsWhiteSpace) ? "\"" + f + "\"" : f, "")));
        }

        return MentionCompleter.Matches(items.Select(i => new CompletionItem(head + i.Text, i.Note)).ToList(), argText);
    }

    /// <summary>The reviewer words with their notes, for the completion list.</summary>
    public static readonly IReadOnlyList<CompletionItem> Reviewers =
    [
        new(PlanText.ReviewerWord(PlanReviewer.Model), "the model audits alone"),
        new(PlanText.ReviewerWord(PlanReviewer.Advisor), "an advisor finds, the model verifies"),
        new(PlanText.ReviewerWord(PlanReviewer.Any), "the model, asking advisors where a role fits"),
        new(PlanText.ReviewerWord(PlanReviewer.Confirm), "the model audits; an advisor checks once it finds nothing new"),
    ];

    private static AuditArguments ParseResume(List<string> words)
    {
        PlanReviewer? reviewer = null;
        int? passes = null;
        string? name = null;
        for (int i = 0; i < words.Count; i++)
        {
            string word = words[i];
            bool reviewerWord = PlanText.ParseReviewer(word) is not null;
            bool number = IsNumber(word);
            // A reviewer word or a number is never a report's name, except first with another of its kind after it: resume model model.
            bool nameFirst = i == 0 && name is null
                && ((reviewerWord && words.Skip(1).Any(w => PlanText.ParseReviewer(w) is not null)) || (number && words.Skip(1).Any(IsNumber)));
            if (nameFirst || (!reviewerWord && !number))
            {
                if (name is not null)
                {
                    return Refused(AuditText.NoSuchReport(string.Join(' ', words)), AuditVerb.Resume);
                }

                name = word;
                continue;
            }

            if (reviewerWord)
            {
                if (reviewer is not null)
                {
                    return Refused(AuditText.TwoReviewers, AuditVerb.Resume);
                }

                reviewer = PlanText.ParseReviewer(word);
                continue;
            }

            if (passes is not null)
            {
                return Refused(AuditText.TwoCounts, AuditVerb.Resume);
            }

            if (!int.TryParse(word, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count)
                || count < Settings.AppSettingsData.MinCodeAuditPasses || count > Settings.AppSettingsData.MaxCodeAuditPasses)
            {
                return Refused(AuditText.BadPasses(word), AuditVerb.Resume);
            }

            passes = count;
        }

        return new AuditArguments(AuditVerb.Resume, name, reviewer, passes, null, null, "");
    }

    /// <summary>
    /// A path as a scope word that reads back as that path (2026-10-19, the review: a folder named <c>diff</c>, <c>staged</c> or <c>branch</c> was
    /// kept bare in the header's <c>scope:</c>, and <c>resume</c> read it as a git scope): a path the grammar would read as another kind of word
    /// — a scope word, a verb, a reviewer, a count, a focus, <c>all</c>, <c>--</c> — gains <c>./</c>; a blank-holding one is quoted
    /// (<see cref="AuditPath.Word"/>).
    /// </summary>
    public static string PathWord(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path.Length > 0 && !path.Any(char.IsWhiteSpace) && !LooksLikePath(path) && Reserved(path) ? "./" + path : AuditPath.Word(path);
    }

    private static bool Reserved(string word) =>
        word == AuditText.BriefSwitch || PlanText.ParseReviewer(word) is not null || IsNumber(word) || AuditFocus.Parse(word) is not null
        || word.ToLowerInvariant() is AuditText.DiffWord or AuditText.StagedWord or AuditText.BranchWord or AuditFocus.AllWord
            or AuditText.OpenWord or AuditText.FixWord or AuditText.ResumeWord;

    /// <summary>A word after <c>branch</c> that names its base: anything the grammar would read as another kind of word is not.</summary>
    private static bool IsBase(string word, Func<string, string?> existing) =>
        word != AuditText.BriefSwitch && PlanText.ParseReviewer(word) is null && !IsNumber(word)
        && word.ToLowerInvariant() is not (AuditText.DiffWord or AuditText.StagedWord or AuditText.BranchWord or AuditFocus.AllWord)
        && AuditFocus.Parse(word) is null && existing(word) is null;

    private static AuditArguments Refused(string error, AuditVerb verb = AuditVerb.Start) => new(verb, null, null, null, null, null, "", error);

    /// <summary>A whole number as typed, a sign allowed, so <c>-1</c> and an overflow read as a count out of range rather than a brief.</summary>
    private static bool IsNumber(string word)
    {
        string digits = word.Length > 1 && word[0] is '-' or '+' ? word[1..] : word;
        return digits.Length > 0 && digits.All(char.IsAsciiDigit);
    }

    /// <summary>A word that looks like a path: it holds a separator, or ends in a dot and an extension.</summary>
    public static bool LooksLikePath(string word)
    {
        ArgumentNullException.ThrowIfNull(word);
        return word.Contains('/', StringComparison.Ordinal) || word.Contains('\\', StringComparison.Ordinal) || Extension().IsMatch(word);
    }

    /// <summary>A word naming the app's own folder, whether it exists or not: <c>.neon</c>, <c>.neon/…</c>, <c>./.neon\…</c>.</summary>
    private static bool AppFolderWord(string word) => AuditPath.InAppFolder(AuditPath.FromRelative(word));

    /// <summary>
    /// The words of <paramref name="text"/> with where each begins and ends in it: <see cref="Printing.PrintCommand.Words"/>' rule (blanks, a
    /// double-quoted stretch kept whole and unquoted), so the brief can be cut from the raw text.
    /// </summary>
    public static List<(string Word, int Start, int End)> Split(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var words = new List<(string, int, int)>();
        var word = new System.Text.StringBuilder();
        bool quoted = false;
        bool any = false;
        int start = -1;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '"')
            {
                quoted = !quoted;
                any = true;
                start = start < 0 ? i : start;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (any || word.Length > 0)
                {
                    words.Add((word.ToString(), start, i));
                    word.Clear();
                    any = false;
                    start = -1;
                }
            }
            else
            {
                start = start < 0 ? i : start;
                word.Append(c);
            }
        }

        if (any || word.Length > 0)
        {
            words.Add((word.ToString(), start, text.Length));
        }

        return words;
    }

    [GeneratedRegex(@"^[^.\s][^\s]*\.[A-Za-z0-9]{1,8}$", RegexOptions.CultureInvariant)]
    private static partial Regex Extension();
}
