namespace NeonSidekick.Monitors;

/// <summary>What a <c>/monitor</c> line asks for.</summary>
public enum MonitorVerb
{
    /// <summary>Bare: the pane on its Layout tab.</summary>
    Pane,

    /// <summary><c>/monitor list</c>: the lines.</summary>
    List,

    /// <summary><c>/monitor &lt;n|name&gt;</c>: the pane on that monitor's tab.</summary>
    Show,

    /// <summary><c>/monitor &lt;n|name&gt; input &lt;input&gt;</c>.</summary>
    Input,

    /// <summary><c>/monitor &lt;n|name&gt; only</c>.</summary>
    Only,

    /// <summary><c>/monitor &lt;n|name&gt; primary</c>.</summary>
    Primary,

    /// <summary><c>/monitor extend</c>.</summary>
    Extend,

    /// <summary><c>/monitor duplicate</c>.</summary>
    Duplicate,
}

/// <summary>A parsed <c>/monitor</c> line: the verb, the monitor as typed (a number or a name), and the input as typed (the rest of the line after <c>input</c>).</summary>
public sealed record MonitorArgs(MonitorVerb Verb, string? Monitor = null, string? Input = null)
{
    /// <summary>Whether it changes something (an input or a layout), so a page's line is refused and the screen's asks where it must.</summary>
    public bool Changes => Verb is MonitorVerb.Input or MonitorVerb.Only or MonitorVerb.Primary or MonitorVerb.Extend or MonitorVerb.Duplicate;
}

/// <summary>
/// <c>/monitor</c> (2026-10-23, the monitors plan): bare the pane, <c>list</c> the lines, <c>&lt;n|name&gt;</c> the pane on that monitor's tab,
/// <c>&lt;n|name&gt; input &lt;input&gt;</c>, <c>&lt;n|name&gt; only|primary</c>, <c>extend</c>, <c>duplicate</c>. The verbs win over a monitor of
/// that name, which is reached by its number or quoted (<c>/monitor "list"</c>); after <c>input</c> the rest of the line is the input, so a given
/// name with spaces needs no quotes (<c>/monitor 2 input Work laptop</c>); a monitor name with spaces is quoted (<c>/monitor "LG ULTRAWIDE" only</c>)
/// or given by number. A name matches whole or by prefix, any case (<see cref="Find"/>). Pure.
/// </summary>
public static class MonitorCommand
{
    public const string Word = "/monitor";
    public const string ListWord = "list";
    public const string ExtendWord = "extend";
    public const string DuplicateWord = "duplicate";
    public const string InputWord = "input";
    public const string OnlyWord = "only";
    public const string PrimaryWord = "primary";

    /// <summary>The verbs completion offers first.</summary>
    public static readonly IReadOnlyList<string> Verbs = [ListWord, ExtendWord, DuplicateWord];

    /// <summary>The words after a monitor.</summary>
    public static readonly IReadOnlyList<string> MonitorVerbs = [InputWord, OnlyWord, PrimaryWord];

    /// <summary>The line parsed, or null for a form <c>/monitor</c> doesn't take (<see cref="MonitorText.Usage"/>).</summary>
    public static MonitorArgs? Parse(string args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string text = args.Trim();
        if (text.Length == 0)
        {
            return new MonitorArgs(MonitorVerb.Pane);
        }

        // A quoted first word is always a monitor (never a verb).
        string monitor;
        string rest;
        bool quoted = text[0] == '"';
        if (quoted)
        {
            int close = text.IndexOf('"', 1);
            if (close < 0)
            {
                return null;
            }

            monitor = text[1..close].Trim();
            rest = text[(close + 1)..].Trim();
            if (monitor.Length == 0)
            {
                return null;
            }
        }
        else
        {
            int space = text.IndexOf(' ', StringComparison.Ordinal);
            monitor = space < 0 ? text : text[..space];
            rest = space < 0 ? "" : text[(space + 1)..].Trim();
            if (rest.Length == 0)
            {
                if (Is(monitor, ListWord))
                {
                    return new MonitorArgs(MonitorVerb.List);
                }

                if (Is(monitor, ExtendWord))
                {
                    return new MonitorArgs(MonitorVerb.Extend);
                }

                if (Is(monitor, DuplicateWord))
                {
                    return new MonitorArgs(MonitorVerb.Duplicate);
                }
            }
            else if (Is(monitor, ListWord) || Is(monitor, ExtendWord) || Is(monitor, DuplicateWord))
            {
                return null;
            }
        }

        if (rest.Length == 0)
        {
            return new MonitorArgs(MonitorVerb.Show, monitor);
        }

        int gap = rest.IndexOf(' ', StringComparison.Ordinal);
        string verb = gap < 0 ? rest : rest[..gap];
        string after = gap < 0 ? "" : rest[(gap + 1)..].Trim();
        if (Is(verb, InputWord))
        {
            return new MonitorArgs(MonitorVerb.Input, monitor, after.Length > 0 ? after : null);
        }

        if (after.Length > 0)
        {
            return null;
        }

        return Is(verb, OnlyWord) ? new MonitorArgs(MonitorVerb.Only, monitor)
            : Is(verb, PrimaryWord) ? new MonitorArgs(MonitorVerb.Primary, monitor)
            : null;
    }

    /// <summary>
    /// The monitor <paramref name="text"/> names in <paramref name="snapshot"/>: a number, else a name (with its <c> #2</c>) whole, else by
    /// prefix, any case; null with the refusal's sentence in <paramref name="error"/> (no such number, no such name, two matches).
    /// </summary>
    public static MonitorInfo? Find(MonitorSnapshot snapshot, string text, out string? error)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(text);
        error = null;
        string t = text.Trim();
        var all = snapshot.Monitors;
        if (int.TryParse(t, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int number))
        {
            if (snapshot.ByNumber(number) is { } byNumber)
            {
                return byNumber;
            }

            error = MonitorText.NoSuchMonitor(t, all.Select(m => m.Number).ToList());
            return null;
        }

        var whole = all.Where(m => string.Equals(m.Name, t, StringComparison.OrdinalIgnoreCase)).ToList();
        var matches = whole.Count > 0 ? whole : all.Where(m => m.Name.StartsWith(t, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 1)
        {
            return matches[0];
        }

        error = matches.Count > 1 ? MonitorText.Ambiguous(t, matches.Select(m => m.Label).ToList()) : MonitorText.NoSuchMonitorName(t, all.Select(m => m.Label).ToList());
        return null;
    }

    /// <summary>A monitor as completion inserts it: its name quoted when it holds a space.</summary>
    public static string Spelled(MonitorInfo monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        return monitor.Name.Contains(' ', StringComparison.Ordinal) ? "\"" + monitor.Name + "\"" : monitor.Name;
    }

    /// <summary>
    /// <c>/monitor</c>'s completion (2026-10-23): whole lines, one word further than <paramref name="argText"/> — the verbs, each monitor's
    /// number and name (quoted when it holds a space), then a monitor's <c>input</c>, <c>only</c> and <c>primary</c>, then its inputs as the
    /// tab shows them — all from <paramref name="state"/>, the board's snapshot, never a DDC/CI read while typing. Pure.
    /// </summary>
    public static IReadOnlyList<UI.CompletionItem> Complete(string argText, MonitorState state, MonitorWords words)
    {
        ArgumentNullException.ThrowIfNull(argText);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(words);
        var all = new List<UI.CompletionItem>();
        all.AddRange(Verbs.Select(v => new UI.CompletionItem(v, MonitorText.VerbHint(v))));
        foreach (var m in state.Topology.Monitors)
        {
            var inputs = state.InputsOf(m);
            var rows = m.Active && !m.Shared ? words.Rows(m, inputs.Offered, inputs.Current, state.AwayOf(m)) : [];
            foreach (string spelled in new[] { m.Number.ToString(System.Globalization.CultureInfo.InvariantCulture), Spelled(m) }.Distinct(StringComparer.Ordinal))
            {
                all.Add(new UI.CompletionItem(spelled, m.Label));
                all.AddRange(MonitorVerbs.Select(v => new UI.CompletionItem(spelled + " " + v, MonitorText.VerbHint(v))));
                all.AddRange(rows.Select(r => new UI.CompletionItem(spelled + " " + InputWord + " " + (r.Given ?? r.Standard ?? Mccs.Hex(r.Code)), r.Words)));
            }
        }

        var next = all.Where(item => item.Text.StartsWith(argText, StringComparison.OrdinalIgnoreCase) && OneWordOn(item.Text, argText)).ToList();
        return UI.MentionCompleter.Matches(next, argText);
    }

    // Whether the candidate is at most one word past what is typed (a space inside quotes, or in an input's given name, not counted).
    private static bool OneWordOn(string candidate, string typed)
    {
        // After "input " the rest of the line is the input, spaces and all.
        if (typed.Contains(" " + InputWord + " ", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        bool quoted = typed.Count(c => c == '"') % 2 == 1;
        string rest = candidate[typed.Length..];
        if (!quoted && rest.StartsWith(' '))
        {
            rest = rest[1..];
        }

        foreach (char c in rest)
        {
            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (c == ' ' && !quoted)
            {
                return false;
            }
        }

        return true;
    }

    private static bool Is(string word, string verb) => string.Equals(word, verb, StringComparison.OrdinalIgnoreCase);
}
