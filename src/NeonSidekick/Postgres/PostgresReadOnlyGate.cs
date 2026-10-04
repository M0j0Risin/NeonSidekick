namespace NeonSidekick.Postgres;

/// <summary>
/// Whether <c>postgres_query</c> may run a text (2026-10-04), <c>MySqlReadOnlyGate</c>'s part for PostgreSQL: the first layer, ahead
/// of the session (<c>default_transaction_read_only</c> and <c>statement_timeout</c> set at startup), a <c>READ ONLY</c> transaction
/// always rolled back (<see cref="PostgresAccess"/>) and a SELECT-only account. A <b>lexer</b> for PostgreSQL's rules, never a
/// pattern: <c>--</c> comments and <b>nested</b> <c>/* */</c> ones; <c>'…'</c> strings with <c>''</c>, <c>E'…'</c> ones with backslash
/// escapes; dollar quoting (<c>$$…$$</c>, <c>$tag$…$tag$</c>); <c>"…"</c> names; <c>@name</c> placeholders (Npgsql's). <c>U&amp;</c>
/// strings and names are refused (their escapes hide what they spell), as are positional <c>$1</c> placeholders (<c>params</c> binds
/// names). Allowed: one statement (a trailing <c>;</c> dropped) that starts <c>SELECT</c>, <c>WITH</c>, <c>VALUES</c> or <c>TABLE</c>.
/// Refused wherever they stand: the data- and schema-changing words (a <c>WITH</c> may lead an <c>INSERT</c>), <c>INTO</c> (SELECT INTO
/// makes a table), the locking reads (<c>FOR UPDATE</c>, <c>FOR NO KEY UPDATE</c>, <c>FOR SHARE</c>, <c>FOR KEY SHARE</c>), the
/// statements that are not a query, and <see cref="DeniedFunctions"/> (a file on the server, a large object, a sequence moved, a lock
/// no rollback releases, a backend ended, a setting changed, a door to another database, a sleep).
/// </summary>
public static class PostgresReadOnlyGate
{
    public enum TokenKind
    {
        Word,
        Quoted,
        String,
        Number,
        Bind,

        /// <summary>
        /// An <c>@name</c> straight after an operator character (the second 2026-10-04 review): <c>id=@id</c> is a placeholder, but
        /// <c>tags &lt;@cols</c> and <c>v @@to_tsquery(…)</c> are an operator and a name. Npgsql rewrites either whenever the command
        /// carries a parameter of that name and leaves the text alone otherwise (checked live on Npgsql 10.0.3), so it is bound only
        /// when <c>params</c> names it; unbound, the server reads the name, so <see cref="Check"/> looks at it as a function's name.
        /// </summary>
        OperatorBind,
        Symbol,
    }

    public readonly record struct Token(TokenKind Kind, string Text, int Line, int Column);

    /// <summary>
    /// Functions a query may not call, by name; <see cref="DeniedPrefixes"/> covers the families. The ones that run a query handed
    /// to them as text are here because the gate sees that text as a string and nothing more: <c>query_to_xml</c> and its siblings,
    /// <c>ts_stat</c> and <c>ts_rewrite</c> (core), tablefunc's <c>connectby</c> and xml2's <c>xpath_table</c> (crosstab is a prefix)
    /// — <c>ts_stat('SELECT pg_terminate_backend(pid)::text::tsvector …')</c> walked past until the second 2026-10-04 review.
    /// </summary>
    public static readonly IReadOnlySet<string> DeniedFunctions = new HashSet<string>(StringComparer.Ordinal)
    {
        "PG_READ_FILE", "PG_READ_BINARY_FILE", "PG_STAT_FILE", "NEXTVAL", "SETVAL", "PG_TERMINATE_BACKEND", "PG_CANCEL_BACKEND",
        "PG_RELOAD_CONF", "SET_CONFIG", "PG_NOTIFY", "PG_SLEEP", "PG_SLEEP_FOR", "PG_SLEEP_UNTIL", "PG_LOGICAL_EMIT_MESSAGE", "PG_SWITCH_WAL",
        "PG_ROTATE_LOGFILE", "PG_START_BACKUP", "PG_STOP_BACKUP", "PG_IMPORT_SYSTEM_COLLATIONS",
        "QUERY_TO_XML", "QUERY_TO_XMLSCHEMA", "QUERY_TO_XML_AND_XMLSCHEMA", "CURSOR_TO_XML", "CURSOR_TO_XMLSCHEMA",
        "TS_STAT", "TS_REWRITE", "CONNECTBY", "XPATH_TABLE",
    };

    /// <summary>
    /// Function families a query may not call: large objects, dblink, advisory locks, the replication and file admin functions,
    /// the server's directory listers (<c>PG_LS_</c>: <c>pg_ls_dir</c> and, since the 2026-10-04 review, <c>pg_ls_logdir</c>,
    /// <c>pg_ls_waldir</c>, <c>pg_ls_tmpdir</c>, <c>pg_ls_archive_statusdir</c> and whatever later versions add), and, since the
    /// second review that day, what a rollback does not undo: replication slots made, copied, dropped or read on
    /// (<c>PG_CREATE_</c> — <c>pg_create_restore_point</c> too — <c>PG_COPY_</c>, <c>PG_DROP_</c>, <c>PG_LOGICAL_SLOT_</c>,
    /// <c>PG_SYNC_REPLICATION_SLOTS</c>), statistics wiped (<c>PG_STAT_RESET</c>, <c>PG_STAT_STATEMENTS_RESET</c>) or written
    /// (<c>PG_RESTORE_</c>, <c>PG_CLEAR_</c>: PG 18's relation and attribute stats), a backup begun or ended (<c>PG_BACKUP_</c>), a
    /// standby's replay paused (<c>PG_WAL_REPLAY_</c>), and tablefunc's <c>crosstab</c> family, which runs its text argument.
    /// </summary>
    public static readonly IReadOnlyList<string> DeniedPrefixes =
    [
        "LO_", "DBLINK", "PG_ADVISORY", "PG_TRY_ADVISORY", "PG_FILE_", "PG_REPLICATION_", "PG_PROMOTE", "PG_LS_",
        "PG_CREATE_", "PG_COPY_", "PG_DROP_", "PG_LOGICAL_SLOT_", "PG_SYNC_REPLICATION_SLOTS", "PG_STAT_RESET", "PG_STAT_STATEMENTS_RESET",
        "PG_RESTORE_", "PG_CLEAR_", "PG_BACKUP_", "PG_WAL_REPLAY_", "CROSSTAB",
    ];

    /// <summary>The words that change data or the schema, or are not a query; refused wherever they stand.</summary>
    public static readonly IReadOnlySet<string> ChangingWords = new HashSet<string>(StringComparer.Ordinal)
    {
        "INSERT", "UPDATE", "DELETE", "MERGE", "TRUNCATE", "CREATE", "DROP", "ALTER", "GRANT", "REVOKE", "COPY", "CALL", "DO", "LISTEN",
        "NOTIFY", "UNLISTEN", "LOCK", "VACUUM", "CLUSTER", "REINDEX", "REFRESH", "PREPARE", "EXECUTE", "DEALLOCATE", "DISCARD", "IMPORT",
        "SECURITY", "CHECKPOINT", "LOAD", "INTO",
    };

    public static string? Check(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        if (string.IsNullOrWhiteSpace(sql))
        {
            return PostgresText.NoSql;
        }

        if (Tokenize(sql, out var lexError) is not { } all)
        {
            return lexError;
        }

        var tokens = all.Count > 0 && IsSymbol(all[^1], ";") ? all[..^1] : all;
        int lead = tokens.FindIndex(t => !IsSymbol(t, "("));
        if (lead < 0)
        {
            return PostgresText.NoSql;
        }

        var first = tokens[lead];
        if (first.Kind != TokenKind.Word || first.Text is not ("SELECT" or "WITH" or "VALUES" or "TABLE"))
        {
            return PostgresText.NotASelect(first.Kind == TokenKind.Word ? first.Text : "'" + first.Text + "'");
        }

        int separators = tokens.Count(t => IsSymbol(t, ";"));
        if (separators > 0)
        {
            return PostgresText.NotOneStatement(separators + 1);
        }

        for (int i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            var next = i + 1 < tokens.Count ? tokens[i + 1] : default;

            // A quoted name is a function's name too: "pg_read_file"('/etc/passwd') calls pg_read_file (the 2026-10-04 review found
            // it past the gate). Upper-cased to meet the lists; a quoted "PG_READ_FILE" is another name to the server, refused anyway.
            if (t.Kind is TokenKind.Quoted or TokenKind.OperatorBind)
            {
                if (IsSymbol(next, "(") && IsDeniedFunction(t.Text.ToUpperInvariant()))
                {
                    return PostgresText.Forbidden(t.Text.ToLowerInvariant() + "()");
                }

                continue;
            }

            if (t.Kind != TokenKind.Word)
            {
                continue;
            }

            if (ChangingWords.Contains(t.Text))
            {
                return t.Text == "INTO" ? PostgresText.SelectInto : PostgresText.Forbidden(t.Text);
            }

            if (t.Text == "FOR" && next.Kind == TokenKind.Word && next.Text is "SHARE" or "KEY" or "NO")
            {
                return PostgresText.Forbidden("FOR " + next.Text + " … (it locks rows)");
            }

            if (IsSymbol(next, "(") && IsDeniedFunction(t.Text))
            {
                return PostgresText.Forbidden(t.Text.ToLowerInvariant() + "()");
            }
        }

        return null;
    }

    /// <summary>
    /// The <c>@name</c> placeholders the text uses, each once, without the <c>@</c>; empty when the text does not lex. One straight
    /// after an operator character (<see cref="TokenKind.OperatorBind"/>) counts only when <paramref name="named"/> holds it.
    /// </summary>
    public static IReadOnlyList<string> Binds(string sql, IEnumerable<string>? named = null)
    {
        if (Tokenize(sql ?? "", out _) is not { } tokens)
        {
            return [];
        }

        var given = new HashSet<string>(named ?? [], StringComparer.OrdinalIgnoreCase);
        return tokens.Where(t => t.Kind == TokenKind.Bind || (t.Kind == TokenKind.OperatorBind && given.Contains(t.Text)))
            .Select(t => t.Text).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// The <c>@name</c>s straight after an operator character that <paramref name="named"/> does not hold, each once, without the
    /// <c>@</c>; empty when the text does not lex. Sent as written, the server reads such an @ as the operator's tail, so a
    /// placeholder written <c>id=@id</c> with no params is the operator <c>=@</c>: <see cref="PostgresAccess"/> adds a hint to the
    /// error that follows (the third 2026-10-04 review: the spaced <c>id = @id</c> binds NULL, the unspaced one failed with
    /// <c>operator does not exist: integer =@ integer</c> and nothing more).
    /// </summary>
    public static IReadOnlyList<string> UnboundOperatorBinds(string sql, IEnumerable<string>? named = null)
    {
        if (Tokenize(sql ?? "", out _) is not { } tokens)
        {
            return [];
        }

        var given = new HashSet<string>(named ?? [], StringComparer.OrdinalIgnoreCase);
        return tokens.Where(t => t.Kind == TokenKind.OperatorBind && !given.Contains(t.Text))
            .Select(t => t.Text).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The text without a trailing <c>;</c>: what runs.</summary>
    public static string Body(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        string trimmed = sql.TrimEnd();
        return trimmed.EndsWith(';') ? trimmed[..^1].TrimEnd() : trimmed;
    }

    private static bool IsDeniedFunction(string upper) =>
        DeniedFunctions.Contains(upper) || DeniedPrefixes.Any(p => upper.StartsWith(p, StringComparison.Ordinal));

    private static bool IsSymbol(Token token, string symbol) => token.Kind == TokenKind.Symbol && token.Text == symbol;

    /// <summary>PostgreSQL's operator characters (CREATE OPERATOR's list).</summary>
    private static bool IsOperatorChar(char c) => c is '+' or '-' or '*' or '/' or '<' or '>' or '=' or '~' or '!' or '@' or '#' or '%' or '^' or '&' or '|' or '`' or '?';

    private static bool IsNameStart(char c) => char.IsLetter(c) || c == '_' || c > 127;

    private static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '$' || c > 127;

    /// <summary>The text as tokens, comments dropped; null with the sentence for something that never ends, a <c>U&amp;</c> escape or a positional placeholder.</summary>
    public static List<Token>? Tokenize(string sql, out string? error)
    {
        ArgumentNullException.ThrowIfNull(sql);
        error = null;
        var tokens = new List<Token>();
        int line = 1, lineStart = 0, i = 0;

        void Advance(int to)
        {
            for (int k = i; k < to && k < sql.Length; k++)
            {
                if (sql[k] == '\n')
                {
                    line++;
                    lineStart = k + 1;
                }
            }

            i = to;
        }

        while (i < sql.Length)
        {
            char c = sql[i];
            int column = i - lineStart + 1;
            if (char.IsWhiteSpace(c))
            {
                Advance(i + 1);
                continue;
            }

            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                int end = sql.IndexOf('\n', i);
                Advance(end < 0 ? sql.Length : end);
                continue;
            }

            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                int depth = 0, k = i;
                while (k < sql.Length)
                {
                    if (k + 1 < sql.Length && sql[k] == '/' && sql[k + 1] == '*')
                    {
                        depth++;
                        k += 2;
                    }
                    else if (k + 1 < sql.Length && sql[k] == '*' && sql[k + 1] == '/')
                    {
                        depth--;
                        k += 2;
                        if (depth == 0)
                        {
                            break;
                        }
                    }
                    else
                    {
                        k++;
                    }
                }

                if (depth != 0)
                {
                    error = PostgresText.Unterminated("comment", line, column);
                    return null;
                }

                Advance(k);
                continue;
            }

            if ((c is 'U' or 'u') && i + 2 < sql.Length && sql[i + 1] == '&' && sql[i + 2] is '\'' or '"')
            {
                error = PostgresText.UnicodeEscapes(line, column);
                return null;
            }

            bool escapes = (c is 'E' or 'e') && i + 1 < sql.Length && sql[i + 1] == '\'' && (i == 0 || !IsNameChar(sql[i - 1]));
            if (c == '\'' || escapes)
            {
                int k = i + (escapes ? 2 : 1);
                bool closed = false;
                while (k < sql.Length)
                {
                    if (escapes && sql[k] == '\\')
                    {
                        k += 2;
                        continue;
                    }

                    if (sql[k] == '\'')
                    {
                        if (k + 1 < sql.Length && sql[k + 1] == '\'')
                        {
                            k += 2;
                            continue;
                        }

                        closed = true;
                        break;
                    }

                    k++;
                }

                if (!closed)
                {
                    error = PostgresText.Unterminated("string", line, column);
                    return null;
                }

                tokens.Add(new Token(TokenKind.String, sql[i..(k + 1)], line, column));
                Advance(k + 1);
                continue;
            }

            if (c == '"')
            {
                int k = i + 1;
                var name = new System.Text.StringBuilder();
                bool closed = false;
                while (k < sql.Length)
                {
                    if (sql[k] == '"')
                    {
                        if (k + 1 < sql.Length && sql[k + 1] == '"')
                        {
                            name.Append('"');
                            k += 2;
                            continue;
                        }

                        closed = true;
                        break;
                    }

                    name.Append(sql[k]);
                    k++;
                }

                if (!closed)
                {
                    error = PostgresText.Unterminated("quoted name", line, column);
                    return null;
                }

                tokens.Add(new Token(TokenKind.Quoted, name.ToString(), line, column));
                Advance(k + 1);
                continue;
            }

            if (c == '$')
            {
                if (i + 1 < sql.Length && char.IsDigit(sql[i + 1]))
                {
                    error = PostgresText.Positional;
                    return null;
                }

                int k = i + 1;
                while (k < sql.Length && sql[k] != '$' && (IsNameStart(sql[k]) || (k > i + 1 && char.IsDigit(sql[k]))))
                {
                    k++;
                }

                if (k < sql.Length && sql[k] == '$')
                {
                    string tag = sql[i..(k + 1)];
                    int end = sql.IndexOf(tag, k + 1, StringComparison.Ordinal);
                    if (end < 0)
                    {
                        error = PostgresText.Unterminated("dollar-quoted string", line, column);
                        return null;
                    }

                    tokens.Add(new Token(TokenKind.String, sql[i..(end + tag.Length)], line, column));
                    Advance(end + tag.Length);
                    continue;
                }

                tokens.Add(new Token(TokenKind.Symbol, "$", line, column));
                Advance(i + 1);
                continue;
            }

            if (c == '@' && i + 1 < sql.Length && IsNameStart(sql[i + 1]))
            {
                int k = i + 1;
                while (k < sql.Length && (char.IsLetterOrDigit(sql[k]) || sql[k] == '_'))
                {
                    k++;
                }

                // After an operator character the @ may be the operator's tail (<@tags, @@to_tsquery): ReadAsync bound every one to
                // NULL, and ARRAY['x'] <@tags compared with NULL in place of the column (the second 2026-10-04 review).
                var kind = i > 0 && IsOperatorChar(sql[i - 1]) ? TokenKind.OperatorBind : TokenKind.Bind;
                tokens.Add(new Token(kind, sql[(i + 1)..k], line, column));
                Advance(k);
                continue;
            }

            if (char.IsDigit(c) || (c == '.' && i + 1 < sql.Length && char.IsDigit(sql[i + 1])))
            {
                int k = i + 1;
                while (k < sql.Length && (char.IsLetterOrDigit(sql[k]) || sql[k] == '.'))
                {
                    k++;
                }

                tokens.Add(new Token(TokenKind.Number, sql[i..k], line, column));
                Advance(k);
                continue;
            }

            if (IsNameStart(c))
            {
                int k = i + 1;
                while (k < sql.Length && IsNameChar(sql[k]))
                {
                    k++;
                }

                tokens.Add(new Token(TokenKind.Word, sql[i..k].ToUpperInvariant(), line, column));
                Advance(k);
                continue;
            }

            tokens.Add(new Token(TokenKind.Symbol, c.ToString(), line, column));
            Advance(i + 1);
        }

        return tokens;
    }
}
