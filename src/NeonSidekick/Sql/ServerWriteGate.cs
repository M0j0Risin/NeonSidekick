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
/// statement are counted (a routine's body holds its own; a negative count is a body the gate cannot read for sure, refused).
/// Since 2026-10-24 (the database review's Phase 8) two more: <see cref="Also"/>, the kinds a statement needs beyond its verb's (MySQL's
/// REPLACE deletes the row it replaces, PostgreSQL's SELECT … INTO makes a table), and <see cref="Body"/>, a routine's or block's body
/// read with the top level's refusals (DB3).
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
    Func<IReadOnlyList<GateToken>, int, int>? Separators = null,
    Func<IReadOnlyList<GateToken>, int, IEnumerable<ServerStatementKind>>? Also = null,
    Func<IReadOnlyList<GateToken>, int, (string What, string Why)?>? Body = null);

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
        if (separators < 0)
        {
            return ServerWriteText.UnclearBody(family);
        }

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

        if (rules.Body?.Invoke(body, lead) is { } inBody)
        {
            return ServerWriteText.Forbidden(family, inBody.What, inBody.Why);
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
        if (OwnKinds(rules, body, lead, out refused, out what) is not { } own)
        {
            return null;
        }

        var found = new HashSet<ServerStatementKind>(own);
        if (rules.Also is { } also)
        {
            found.UnionWith(also(body, lead));
        }

        // A read that also does something else is that something (2026-10-24: PostgreSQL's SELECT … INTO makes a table, review T7), so it
        // never takes the read-only path, where it fails, and asks as a change.
        if (found.Count > 1)
        {
            found.Remove(ServerStatementKind.Read);
        }

        return Enum.GetValues<ServerStatementKind>().Where(found.Contains).ToList();
    }

    /// <summary>
    /// What an <c>ALTER</c> of a table does beyond changing its structure (2026-10-24, the database review's B4, T6): a <c>DROP</c> of a
    /// column, constraint or partition loses what it held (dropping; a partition's rows deleting too), <c>TRUNCATE PARTITION</c> deletes,
    /// <c>EXCHANGE</c>/<c>SWITCH PARTITION</c> moves rows out (deleting and changing data), and enabling or disabling a trigger or rule
    /// changes which code runs (procedures and triggers). A <c>DROP DEFAULT</c>, <c>NOT NULL</c>, <c>IDENTITY</c> or <c>EXPRESSION</c> loses
    /// nothing.
    /// </summary>
    private static IEnumerable<ServerStatementKind> AlterAlso(List<GateToken> body, int from)
    {
        for (int i = from; i < body.Count; i++)
        {
            var t = body[i];
            if (t.Kind != GateTokenKind.Word || (i > 0 && body[i - 1].IsSymbol(".")))
            {
                continue;
            }

            var next = i + 1 < body.Count ? body[i + 1] : default;
            var after = i + 2 < body.Count ? body[i + 2] : default;
            switch (t.Text)
            {
                case "DROP" when !(next.Kind == GateTokenKind.Word && next.Text is "DEFAULT" or "NOT" or "IDENTITY" or "EXPRESSION"):
                    yield return ServerStatementKind.Drop;
                    if (next.Kind == GateTokenKind.Word && next.Text is "PARTITION" or "SUBPARTITION" or "PARTITIONS")
                    {
                        yield return ServerStatementKind.Delete;
                    }

                    break;
                case "TRUNCATE":
                    yield return ServerStatementKind.Delete;
                    break;
                case "EXCHANGE" or "SWITCH":
                    yield return ServerStatementKind.Delete;
                    yield return ServerStatementKind.Data;
                    break;
                case "ENABLE" or "DISABLE" when next.Kind == GateTokenKind.Word
                    && (next.Text is "TRIGGER" or "TRIGGERS" or "RULE" || (next.Text is "ALL" or "REPLICA" or "ALWAYS" && after.Kind == GateTokenKind.Word && after.Text is "TRIGGER" or "TRIGGERS" or "RULE")):
                    yield return ServerStatementKind.Procedures;
                    break;
            }
        }
    }

    private static IReadOnlyList<ServerStatementKind>? OwnKinds(WriteGateRules rules, List<GateToken> body, int lead, out string? refused, out string what)
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

                // A MERGE branch's INSERT (cols) is the insert itself, not a function (2026-10-24, the database review's R9: WHEN NOT
                // MATCHED THEN INSERT (…) classed a MERGE as its DELETE branch alone, inserting with changing data unticked).
                bool call = i + 1 < body.Count && body[i + 1].IsSymbol("(") && !body[i - 1].IsWord("THEN");
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
        bool replace = false;
        while (at < body.Count && body[at].Kind == GateTokenKind.Word && rules.Modifiers.Contains(body[at].Text))
        {
            replace |= body[at].Text == "OR" && at + 1 < body.Count && body[at + 1].IsWord("REPLACE");
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

        if (!verb.Kinds.TryGetValue(target, out var objectKind))
        {
            return null;
        }

        // CREATE OR REPLACE (the review, 2026-10-05) changes what is there as well as making it: a view's or a synonym's definition
        // (changing structure), and MariaDB's table or sequence, which goes with its rows or its place (dropping). A routine's OR
        // REPLACE is the procedures kind either way.
        var kinds = new List<ServerStatementKind> { objectKind };
        if (replace && objectKind == ServerStatementKind.Create)
        {
            kinds.Add(target is "TABLE" or "SEQUENCE" ? ServerStatementKind.Drop : ServerStatementKind.Alter);
        }

        if (word == "ALTER" && target is "TABLE" or "MATERIALIZED")
        {
            kinds.AddRange(AlterAlso(body, at + 1));
        }

        // A CREATE that holds others (2026-10-24, the database review's C2: PostgreSQL's CREATE SCHEMA s CREATE TABLE … CREATE TRIGGER …)
        // needs each one's kind, and one the family refuses refuses it.
        if (word == "CREATE")
        {
            for (int i = at + 1; i < body.Count; i++)
            {
                if (!body[i].IsWord("CREATE") || body[i - 1].IsSymbol("."))
                {
                    continue;
                }

                int inner = i + 1;
                while (inner < body.Count && body[inner].Kind == GateTokenKind.Word && rules.Modifiers.Contains(body[inner].Text))
                {
                    inner++;
                }

                if (inner >= body.Count || body[inner].Kind != GateTokenKind.Word)
                {
                    what = "CREATE";
                    return null;
                }

                if (verb.Refused.TryGetValue(body[inner].Text, out var innerWhy))
                {
                    what = "CREATE " + body[inner].Text;
                    refused = innerWhy;
                    return null;
                }

                if (!verb.Kinds.TryGetValue(body[inner].Text, out var innerKind))
                {
                    what = "CREATE " + body[inner].Text;
                    return null;
                }

                kinds.Add(innerKind);
            }
        }

        return kinds;
    }

    /// <summary>
    /// Every qualifier in a statement, in order: the heads of its <c>x.y</c> chains, as written (<see cref="QualifierTokens"/>).
    /// <c>NamesAny</c>, which matched these against a set case-blind, went on 2026-10-24 (the database review's R15: no caller, a wrong doc).
    /// </summary>
    public static IReadOnlyList<string> Qualifiers(IReadOnlyList<GateToken> tokens, IReadOnlySet<string>? members = null) =>
        QualifierTokens(tokens, members).Select(t => t.Text).ToList();

    /// <summary>
    /// Every qualifier in a statement, in order, as tokens (so a caller can tell a bare word, which the lexer folded, from a quoted name):
    /// the heads of its <c>x.y</c> chains. A head after another dot is no qualifier; <paramref name="members"/> are the words after a dot
    /// that name no object (Oracle's <c>NEXTVAL</c>). A head after a <c>:</c> is one since 2026-10-24 (the database review's F3, R7: the skip
    /// meant for a bind dropped PostgreSQL's <c>x::payroll.t</c> cast and array slice and Oracle's JSON <c>'k' : payroll.f()</c>; a bind is
    /// a token of its own, never a word, so the skip guarded nothing).
    /// </summary>
    public static IReadOnlyList<GateToken> QualifierTokens(IReadOnlyList<GateToken> tokens, IReadOnlySet<string>? members = null)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        var heads = new List<GateToken>();
        for (int i = 0; i + 1 < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Kind is not (GateTokenKind.Word or GateTokenKind.Name) || !tokens[i + 1].IsSymbol(".") || (i > 0 && tokens[i - 1].IsSymbol(".")))
            {
                continue;
            }

            if (members is not null && i + 2 < tokens.Count && tokens[i + 2].Kind == GateTokenKind.Word && members.Contains(tokens[i + 2].Text))
            {
                continue;
            }

            heads.Add(t);
        }

        return heads;
    }

    /// <summary>
    /// Every part of every <c>x.y.z</c> chain but its last, in order, as tokens (so a caller can fold a bare word and keep a quoted name
    /// as written): <c>shop.payroll.salaries</c> gives <c>shop</c> and <c>payroll</c> (the review, 2026-10-09: PostgreSQL's three-part
    /// name kept its schema in the middle, which <see cref="Qualifiers"/> never looked at); a chain ending in <c>.*</c> gives all of it.
    /// A chain after <c>:</c> is one since 2026-10-24 (<see cref="QualifierTokens"/>), and a chain ending in a dot and an operator
    /// (<c>OPERATOR(payroll.+)</c>, the review's F4) gives all of it.
    /// </summary>
    public static IReadOnlyList<GateToken> Prefixes(IReadOnlyList<GateToken> tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        var prefixes = new List<GateToken>();
        for (int i = 0; i + 1 < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Kind is not (GateTokenKind.Word or GateTokenKind.Name) || !tokens[i + 1].IsSymbol(".") || (i > 0 && tokens[i - 1].IsSymbol(".")))
            {
                continue;
            }

            var chain = new List<GateToken> { t };
            int j = i;
            while (j + 2 < tokens.Count && tokens[j + 1].IsSymbol(".") && tokens[j + 2].Kind is GateTokenKind.Word or GateTokenKind.Name)
            {
                chain.Add(tokens[j + 2]);
                j += 2;
            }

            bool star = j + 2 < tokens.Count && tokens[j + 1].IsSymbol(".") && tokens[j + 2].Kind is GateTokenKind.Symbol or GateTokenKind.Other;
            prefixes.AddRange(star ? chain : chain.Take(chain.Count - 1));
            i = j;
        }

        return prefixes;
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
