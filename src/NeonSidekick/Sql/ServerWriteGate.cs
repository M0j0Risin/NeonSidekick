namespace NeonSidekick.Sql;

/// <summary>A token as <see cref="ServerWriteGate"/> reads it, mapped from a family's own lexer.</summary>
public enum GateTokenKind
{
    /// <summary>A bare word, upper-cased (the families' lexers fold them).</summary>
    Word,

    /// <summary>A quoted name (or PostgreSQL's <c>@name</c> after an operator): a name all the same, a function's when <c>(</c> follows.</summary>
    Name,

    /// <summary>A symbol: <c>(</c>, <c>)</c>, <c>;</c>, <c>/</c>, <c>@</c>, ….</summary>
    Symbol,

    /// <summary>A string, a number, a placeholder: never a statement's word.</summary>
    Other,
}

/// <summary>One token: its kind and its text (a word upper-cased).</summary>
public readonly record struct GateToken(GateTokenKind Kind, string Text)
{
    public bool IsWord(string word) => Kind == GateTokenKind.Word && Text == word;

    public bool IsSymbol(string symbol) => Kind == GateTokenKind.Symbol && Text == symbol;
}

/// <summary>What <c>CREATE</c>, <c>ALTER</c> or <c>DROP</c> may act on: the object words the gate runs, by kind, and those it refuses, with why.</summary>
public sealed record ObjectVerb(IReadOnlyDictionary<string, ServerStatementKind> Kinds, IReadOnlyDictionary<string, string> Refused);

/// <summary>
/// A family's rules for <see cref="ServerWriteGate"/> (2026-10-05): the first words it refuses and why (a transaction's own words, the
/// account and permission statements, the server-wide ones, those that reach files or other servers, dynamic SQL, a session's
/// settings, locks), the first words with one kind (<c>TRUNCATE</c>, <c>VACUUM</c>, <c>CALL</c>…), the data-changing verbs (a statement
/// led by one, or by <c>WITH</c>, needs the kind of every such verb in it — a <c>WITH … DELETE … RETURNING</c> feeding an <c>INSERT</c>
/// deletes too), the reading first words, the object verbs and the modifiers between a verb and its object (<c>CREATE OR REPLACE
/// TEMP</c>), a scan for what is refused wherever it stands (a denied function, a database link), and how the <c>;</c>s that end a
/// statement are counted (a routine's body holds its own).
/// </summary>
public sealed record WriteGateRules(
    ServerWriteFamily Family,
    IReadOnlyDictionary<string, string> Refused,
    IReadOnlyDictionary<string, ServerStatementKind> Verbs,
    IReadOnlyDictionary<string, ServerStatementKind> Dml,
    IReadOnlySet<string> Reads,
    IReadOnlyDictionary<string, ObjectVerb> Objects,
    IReadOnlySet<string> Modifiers,
    Func<IReadOnlyList<GateToken>, int, (string What, string Why)?> Scan,
    Func<IReadOnlyList<GateToken>, int, int>? Separators = null);

/// <summary>
/// The write gates' common engine (2026-10-05, the user's ask: <c>SqliteWriteGate</c> mirrored for PostgreSQL, MySQL and Oracle; SQL
/// Server's reads ScriptDom's tree instead). One statement per call; its first word decides — refused outright, or a kind the user must
/// have ticked (<c>… statements allowed</c>), or none the tool knows. An allow-list, never a deny-list: a <c>CREATE</c> of something
/// the family's <see cref="ObjectVerb"/> does not name is refused as unknown. Routines, triggers and rules are code, so they are the
/// procedures kind whatever verb makes them (a function made under creating could be called by the next INSERT). A read here is
/// what the family's read gate also passes: the tools check it there too before it runs on the read-only path. Pure.
/// </summary>
public static class ServerWriteGate
{
    /// <summary>Null when <paramref name="tokens"/> (one statement's, lexed) may run under <paramref name="allowed"/> (null allows every kind); else the <c>Error:</c> sentence.</summary>
    public static string? Check(WriteGateRules rules, List<GateToken> tokens, IReadOnlyList<ServerStatementKind>? allowed)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(tokens);
        var family = rules.Family;
        var body = tokens.Count > 0 && tokens[^1].IsSymbol(";") ? tokens[..^1] : tokens;
        int lead = body.FindIndex(t => !t.IsSymbol("("));
        if (lead < 0)
        {
            return ServerWriteText.NoStatement;
        }

        var first = body[lead];
        if (first.Kind != GateTokenKind.Word)
        {
            return ServerWriteText.UnknownStatement(family, "'" + first.Text + "'");
        }

        if (rules.Refused.TryGetValue(first.Text, out var why))
        {
            return ServerWriteText.Forbidden(family, first.Text, why);
        }

        int separators = rules.Separators is { } count ? count(body, lead) : body.Count(t => t.IsSymbol(";"));
        if (separators > 0)
        {
            return ServerWriteText.NotOneStatement(family, separators + 1);
        }

        for (int i = 0; i < body.Count; i++)
        {
            if (rules.Scan(body, i) is { } refused)
            {
                return ServerWriteText.Forbidden(family, refused.What, refused.Why);
            }
        }

        var kinds = Kinds(rules, body, lead, out string? objectRefused, out string unknown);
        if (objectRefused is not null)
        {
            return ServerWriteText.Forbidden(family, unknown, objectRefused);
        }

        if (kinds is null)
        {
            return ServerWriteText.UnknownStatement(family, unknown);
        }

        if (allowed is null)
        {
            return null;
        }

        foreach (var kind in kinds)
        {
            if (!allowed.Contains(kind))
            {
                return ServerWriteText.KindNotAllowed(family, kind, allowed);
            }
        }

        return null;
    }

    /// <summary>The kinds the one statement in <paramref name="tokens"/> needs, in menu order; null when it is none the tool runs (or one it refuses).</summary>
    public static IReadOnlyList<ServerStatementKind>? Kinds(WriteGateRules rules, List<GateToken> tokens)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(tokens);
        var body = tokens.Count > 0 && tokens[^1].IsSymbol(";") ? tokens[..^1] : tokens;
        int lead = body.FindIndex(t => !t.IsSymbol("("));
        if (lead < 0 || body[lead].Kind != GateTokenKind.Word || rules.Refused.ContainsKey(body[lead].Text))
        {
            return null;
        }

        var kinds = Kinds(rules, body, lead, out var refused, out _);
        return refused is null ? kinds : null;
    }

    /// <summary>Whether <paramref name="kinds"/> is a read alone: what runs on the read-only path without asking.</summary>
    public static bool IsRead(IReadOnlyList<ServerStatementKind>? kinds) => kinds is [ServerStatementKind.Read];

    private static IReadOnlyList<ServerStatementKind>? Kinds(WriteGateRules rules, List<GateToken> body, int lead, out string? refused, out string what)
    {
        refused = null;
        string word = body[lead].Text;
        what = word;
        if (rules.Reads.Contains(word))
        {
            return [ServerStatementKind.Read];
        }

        if (word == "WITH" || rules.Dml.ContainsKey(word))
        {
            // Its own verb, and every data-changing verb in it at any depth: a WITH's own statements, a MERGE's branches (a MERGE is
            // only what its branches do), an upsert's UPDATE. A verb with ( after it is a function (MySQL's REPLACE(), INSERT()).
            var found = new HashSet<ServerStatementKind>();
            if (word is not ("WITH" or "MERGE"))
            {
                found.Add(rules.Dml[word]);
            }

            for (int i = lead + 1; i < body.Count; i++)
            {
                var t = body[i];
                bool call = i + 1 < body.Count && body[i + 1].IsSymbol("(");
                if (t.Kind == GateTokenKind.Word && !call && rules.Dml.TryGetValue(t.Text, out var kind))
                {
                    found.Add(kind);
                }
            }

            if (found.Count == 0)
            {
                return word == "WITH" ? [ServerStatementKind.Read] : [rules.Dml[word]];
            }

            return Enum.GetValues<ServerStatementKind>().Where(found.Contains).ToList();
        }

        if (rules.Verbs.TryGetValue(word, out var single))
        {
            return [single];
        }

        if (!rules.Objects.TryGetValue(word, out var verb))
        {
            return null;
        }

        int at = lead + 1;
        while (at < body.Count && body[at].Kind == GateTokenKind.Word && rules.Modifiers.Contains(body[at].Text))
        {
            // A modifier may carry a value (MySQL's ALGORITHM = MERGE): the = and the value are skipped with it.
            at += at + 2 < body.Count && body[at + 1].IsSymbol("=") ? 3 : 1;
        }

        if (at >= body.Count || body[at].Kind != GateTokenKind.Word)
        {
            return null;
        }

        string target = body[at].Text;
        what = word + " " + target;
        if (verb.Refused.TryGetValue(target, out var why))
        {
            refused = why;
            return null;
        }

        return verb.Kinds.TryGetValue(target, out var objectKind) ? [objectKind] : null;
    }

    /// <summary>A word list as a set, ordinal (the lexers upper-case words).</summary>
    public static IReadOnlySet<string> Words(params string[] words) => new HashSet<string>(words, StringComparer.Ordinal);

    /// <summary>A word table from groups: each group's value for each of its words (blank-separated), ordinal.</summary>
    public static Dictionary<string, T> Table<T>(params (T Value, string Words)[] groups)
    {
        var table = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var (value, words) in groups)
        {
            foreach (string w in words.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                table[w] = value;
            }
        }

        return table;
    }
}
