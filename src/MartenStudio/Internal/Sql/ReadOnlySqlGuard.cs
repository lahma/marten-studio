namespace MartenStudio.Internal.Sql;

/// <summary>Why the guard refused a statement.</summary>
internal enum SqlRejectionReason
{
    /// <summary>It did not refuse.</summary>
    None,

    /// <summary>There was nothing to run but whitespace and comments.</summary>
    Empty,

    /// <summary>The statement does not start with one of the shapes the console runs.</summary>
    DisallowedStatement,

    /// <summary>There is more than one statement.</summary>
    MultipleStatements,

    /// <summary>
    /// The statement names a function the console refuses by policy — one that reaches outside the
    /// read-only transaction that would otherwise be the whole guarantee.
    /// </summary>
    DisallowedFunction,

    /// <summary>A quoted string is never closed.</summary>
    UnterminatedString,

    /// <summary>A block comment is never closed.</summary>
    UnterminatedComment,

    /// <summary>A dollar-quoted body is never closed.</summary>
    UnterminatedDollarQuote,
}

/// <summary>The guard's answer: allowed, or refused with a reason and a place to point at.</summary>
/// <param name="Allowed">Whether the statement may be sent.</param>
/// <param name="Reason">Why not.</param>
/// <param name="Message">What to tell the user.</param>
/// <param name="Token">The offending token, when there is one.</param>
/// <param name="Position">Where in the text the problem is, zero-based.</param>
internal readonly record struct SqlGuardResult(
    bool Allowed,
    SqlRejectionReason Reason,
    string Message,
    string? Token,
    int Position)
{
    /// <summary>The statement may be sent.</summary>
    public static SqlGuardResult Allow(string token, int position) =>
        new(true, SqlRejectionReason.None, string.Empty, token, position);

    /// <summary>The statement is refused.</summary>
    public static SqlGuardResult Reject(SqlRejectionReason reason, string message, string? token, int position) =>
        new(false, reason, message, token, position);
}

/// <summary>
/// An <em>advisory</em> check on the shape of a statement before the SQL console sends it.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is not the security boundary, and must never be treated as one</b> (D13). What makes the
/// console safe is the transaction: <c>SET TRANSACTION READ ONLY</c> plus <c>statement_timeout</c> and an
/// optional <c>SET LOCAL ROLE</c>, which is the database's own refusal and not a parser's. This guard
/// exists so that <c>delete from mt_doc_customer</c> produces "the SQL console only runs select, with,
/// explain, table and values statements" instead of Postgres' <c>25006: cannot execute DELETE in a
/// read-only transaction</c> — a better message for the overwhelmingly common case of somebody pasting the
/// wrong thing. Every statement the parser lets through and the database refuses is working as designed;
/// <c>WITH x AS (DELETE … RETURNING *) SELECT * FROM x</c> is exactly that statement, and the integration
/// tests assert it comes back as 25006 with the table unchanged.
/// </para>
/// <para>
/// Two rules about the shape of the statement. The first significant token - after leading whitespace,
/// <c>--</c> line comments and nestable
/// <c>/* */</c> block comments - must be one of <c>select</c>, <c>with</c>, <c>explain</c>, <c>table</c>,
/// <c>values</c>. And there must be exactly one statement: a <c>;</c> anywhere except inside a string, a
/// dollar-quoted body or a comment ends the statement, and anything but whitespace and comments after it is
/// a second statement. That second rule is what keeps <c>select 1; drop table x</c> from arriving as one
/// batch, which is the shape of every SQL-injection demonstration ever written.
/// </para>
/// <para>
/// <c>explain</c> gets one extra check: <c>EXPLAIN ANALYZE</c> <em>runs</em> the statement it is given, so
/// the guard looks past the explain options and holds whatever follows to the same allow-list.
/// </para>
/// <para>
/// <b>Three guarantees, and only one of them is the database's.</b> The read-only transaction is the
/// guarantee <em>for writes</em>: no <c>INSERT</c>, <c>UPDATE</c>, <c>DELETE</c>, <c>TRUNCATE</c> or DDL
/// survives it, whatever this parser thinks. <see cref="DisallowedFunctions"/> is a guarantee
/// <em>by policy</em> for everything else, because a read-only transaction is not a sandbox: verified
/// against Postgres 17, <c>select pg_terminate_backend(pg_backend_pid())</c> runs happily inside one and
/// kills the backend, and <c>pg_read_file</c>, <c>lo_export</c>, <c>pg_ls_dir</c>, <c>dblink</c> and the
/// advisory-lock family are all "reads" as far as SQLSTATE 25006 is concerned. Those are refused here, by
/// name, and that refusal is worth exactly what a parser's refusal is worth — a determined caller can
/// reach the same function through a view, a <c>SECURITY DEFINER</c> wrapper or an alias, and this list
/// will not see it. <b>The only true narrowing is <c>MartenStudioOptions.SqlConsoleRole</c></b>: a Postgres
/// role that was never granted <c>EXECUTE</c> on these functions cannot call them however they are
/// spelled, and that is what a host that cares should configure. The list is here because the host that
/// did not configure one should still not be one paste away from terminating its own connections.
/// </para>
/// </remarks>
internal static class ReadOnlySqlGuard
{
    /// <summary>
    /// Functions the console refuses by name, each with the reason a user is told.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Matched against every identifier token in the statement, case-insensitively — which also catches
    /// the schema-qualified spelling, because <c>pg_catalog.pg_terminate_backend</c> arrives at the scanner
    /// as <c>pg_catalog</c>, <c>.</c> and <c>pg_terminate_backend</c>, and it is the bare name that is
    /// looked up. Quoted strings, quoted identifiers, dollar-quoted bodies and comments are skipped by the
    /// scanner and so cannot trip it.
    /// </para>
    /// <para>
    /// <c>pg_sleep</c> is deliberately <em>not</em> here: <c>statement_timeout</c> bounds it, and it is how
    /// the tests prove the timeout fires. <c>pg_sleep_for</c> and <c>pg_sleep_until</c> are, because
    /// <c>pg_sleep_until</c> in particular parks until a wall-clock time the timeout does not describe.
    /// <c>txid_current</c> is not here either; it assigns a transaction id and nothing more.
    /// </para>
    /// </remarks>
    internal static readonly IReadOnlyDictionary<string, string> DisallowedFunctions =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["pg_terminate_backend"] = "terminates other sessions",
            ["pg_cancel_backend"] = "cancels other sessions' running statements",
            ["pg_log_backend_memory_contexts"] = "makes another session dump its memory contexts to the server log",
            ["pg_reload_conf"] = "makes the server re-read its configuration files",
            ["pg_rotate_logfile"] = "rotates the server's log file",
            ["pg_stat_reset"] = "throws away the statistics the planner and everyone else depends on",
            ["pg_switch_wal"] = "forces a write-ahead-log switch",
            ["pg_create_restore_point"] = "writes a named restore point into the write-ahead log",
            ["pg_backup_start"] = "puts the server into backup mode",
            ["pg_backup_stop"] = "ends the server's backup mode",
            ["pg_promote"] = "promotes a standby to be a primary",
            ["pg_read_file"] = "reads files from the server's filesystem",
            ["pg_read_binary_file"] = "reads files from the server's filesystem",
            ["pg_stat_file"] = "reads file metadata from the server's filesystem",
            ["pg_ls_dir"] = "lists the server's filesystem",
            ["pg_ls_logdir"] = "lists the server's log directory",
            ["pg_ls_waldir"] = "lists the server's write-ahead-log directory",
            ["lo_export"] = "writes a file to the server's filesystem",
            ["lo_import"] = "reads a file from the server's filesystem into a large object",
            ["lo_unlink"] = "deletes a large object, which no read-only transaction prevents",
            ["dblink"] = "opens a connection to another database, outside this transaction and its read-only flag",
            ["dblink_exec"] = "runs a statement on another database, outside this transaction and its read-only flag",
            ["dblink_connect"] = "opens a connection to another database, outside this transaction and its read-only flag",
            ["pg_advisory_lock"] = "takes a session-level advisory lock that outlives this transaction",
            ["pg_advisory_lock_shared"] = "takes a session-level advisory lock that outlives this transaction",
            ["pg_advisory_xact_lock"] = "takes an advisory lock Marten's own daemon contends for",
            ["pg_advisory_xact_lock_shared"] = "takes an advisory lock Marten's own daemon contends for",
            ["pg_sleep_for"] = "parks the session for an interval rather than doing work",
            ["pg_sleep_until"] = "parks the session until a wall-clock time the statement timeout does not describe",
            ["set_config"] = "changes a server setting, which is how every other limit here would be undone",
            ["pg_notify"] = "sends a notification to other sessions listening on this database",
        };

    /// <summary>The statement shapes the console runs.</summary>
    internal static readonly string[] AllowedStatements = ["select", "with", "explain", "table", "values"];

    /// <summary>Keywords that may sit between <c>explain</c> and the statement it explains.</summary>
    private static readonly HashSet<string> ExplainOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "analyze", "analyse", "verbose", "costs", "settings", "generic_plan", "buffers", "serialize", "wal",
        "timing", "summary", "memory", "format", "text", "xml", "json", "yaml", "on", "off", "true", "false",
    };

    /// <summary>Checks a statement's shape.</summary>
    public static SqlGuardResult Check(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return SqlGuardResult.Reject(SqlRejectionReason.Empty, "There is no statement to run.", null, 0);
        }

        var scanner = new Scanner(sql);

        if (!scanner.TryReadToken(out var first, out var firstPosition, out var scanError))
        {
            return scanError ?? SqlGuardResult.Reject(
                SqlRejectionReason.Empty, "There is no statement to run, only comments.", null, 0);
        }

        if (!IsAllowed(first!))
        {
            return SqlGuardResult.Reject(
                SqlRejectionReason.DisallowedStatement,
                $"The SQL console only runs {string.Join(", ", AllowedStatements)} statements; this one starts with " +
                $"'{first}'. Everything it runs is inside a read-only transaction, so a write would be refused by " +
                "Postgres anyway.",
                first,
                firstPosition);
        }

        if (string.Equals(first, "explain", StringComparison.OrdinalIgnoreCase))
        {
            var explained = CheckExplainTarget(ref scanner);

            if (!explained.Allowed)
            {
                return explained;
            }
        }

        return ScanForSecondStatement(ref scanner, first!, firstPosition);
    }

    private static SqlGuardResult CheckExplainTarget(ref Scanner scanner)
    {
        // EXPLAIN ANALYZE executes what it is given, so whatever the options end at has to be allow-listed
        // too. Unrecognised words are left alone: they will be the statement, or Postgres' own syntax error.
        while (true)
        {
            var before = scanner;

            if (!scanner.TryReadToken(out var token, out var position, out var error))
            {
                return error ?? SqlGuardResult.Reject(
                    SqlRejectionReason.DisallowedStatement,
                    "EXPLAIN needs a statement after it.",
                    "explain",
                    position);
            }

            if (token is "(" or ")" or "," || ExplainOptions.Contains(token!))
            {
                continue;
            }

            if (IsAllowed(token!))
            {
                scanner = before;
                return SqlGuardResult.Allow(token!, position);
            }

            return SqlGuardResult.Reject(
                SqlRejectionReason.DisallowedStatement,
                $"EXPLAIN runs the statement it is given when ANALYZE is on, and '{token}' is not one of " +
                $"{string.Join(", ", AllowedStatements)}.",
                token,
                position);
        }
    }

    private static SqlGuardResult ScanForSecondStatement(ref Scanner scanner, string first, int firstPosition)
    {
        while (true)
        {
            if (!scanner.TryReadToken(out var token, out var position, out var error))
            {
                return error ?? SqlGuardResult.Allow(first, firstPosition);
            }

            if (token != ";")
            {
                if (DisallowedFunctions.TryGetValue(token!, out var why))
                {
                    return SqlGuardResult.Reject(
                        SqlRejectionReason.DisallowedFunction,
                        $"'{token}' is not available in the SQL console: it {why}. The read-only transaction " +
                        "refuses writes, but this is not a write, so the console refuses it by name instead. " +
                        "Configure a restricted role for the console if you need the refusal to be the " +
                        "database's rather than the studio's.",
                        token,
                        position);
                }

                continue;
            }

            // A trailing semicolon is fine, and so is a run of them; anything else after one is a second
            // statement.
            string? next;
            int nextPosition;
            SqlGuardResult? trailingError;

            do
            {
                if (!scanner.TryReadToken(out next, out nextPosition, out trailingError))
                {
                    return trailingError ?? SqlGuardResult.Allow(first, firstPosition);
                }
            }
            while (next == ";");

            return SqlGuardResult.Reject(
                SqlRejectionReason.MultipleStatements,
                "The SQL console runs one statement at a time, and this text holds more than one. Run " +
                $"'{next}' separately.",
                next,
                nextPosition);
        }
    }

    private static bool IsAllowed(string token)
    {
        foreach (var allowed in AllowedStatements)
        {
            if (string.Equals(token, allowed, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A minimal Postgres lexer: enough to know where a token is and what is inside a string, a comment or
    /// a dollar-quoted body, and nothing more.
    /// </summary>
    private struct Scanner(string text)
    {
        private int position;

        /// <summary>
        /// Reads the next significant token. Punctuation comes back one character at a time; a word comes
        /// back whole; strings and comments are skipped over rather than returned.
        /// </summary>
        public bool TryReadToken(out string? token, out int tokenPosition, out SqlGuardResult? error)
        {
            error = null;

            while (position < text.Length)
            {
                var c = text[position];

                if (char.IsWhiteSpace(c))
                {
                    position++;
                    continue;
                }

                if (c == '-' && Peek(1) == '-')
                {
                    SkipLineComment();
                    continue;
                }

                if (c == '/' && Peek(1) == '*')
                {
                    if (!SkipBlockComment(out error))
                    {
                        token = null;
                        tokenPosition = position;
                        return false;
                    }

                    continue;
                }

                if (c is '\'' or '"')
                {
                    if (!SkipQuoted(c, out error))
                    {
                        token = null;
                        tokenPosition = position;
                        return false;
                    }

                    continue;
                }

                if (c == '$' && TryReadDollarTag(out var tag))
                {
                    if (!SkipDollarQuoted(tag!, out error))
                    {
                        token = null;
                        tokenPosition = position;
                        return false;
                    }

                    continue;
                }

                tokenPosition = position;

                if (char.IsLetter(c) || c == '_')
                {
                    var start = position;

                    while (position < text.Length && (char.IsLetterOrDigit(text[position]) || text[position] == '_'))
                    {
                        position++;
                    }

                    token = text[start..position];
                    return true;
                }

                position++;
                token = c.ToString();
                return true;
            }

            token = null;
            tokenPosition = position;
            return false;
        }

        private char Peek(int offset) => position + offset < text.Length ? text[position + offset] : '\0';

        private void SkipLineComment()
        {
            while (position < text.Length && text[position] != '\n')
            {
                position++;
            }
        }

        private bool SkipBlockComment(out SqlGuardResult? error)
        {
            // Postgres block comments nest, unlike C's.
            var start = position;
            var depth = 0;

            while (position < text.Length)
            {
                if (text[position] == '/' && Peek(1) == '*')
                {
                    depth++;
                    position += 2;
                    continue;
                }

                if (text[position] == '*' && Peek(1) == '/')
                {
                    depth--;
                    position += 2;

                    if (depth == 0)
                    {
                        error = null;
                        return true;
                    }

                    continue;
                }

                position++;
            }

            error = SqlGuardResult.Reject(
                SqlRejectionReason.UnterminatedComment, "A /* comment is never closed.", "/*", start);
            return false;
        }

        private bool SkipQuoted(char quote, out SqlGuardResult? error)
        {
            var start = position;

            // Backslash escapes exist in an E'' literal and nowhere else. With Postgres' default
            // standard_conforming_strings = on, the backslash in 'a\' is an ordinary character and the
            // quote after it CLOSES the string - so treating it as an escape made
            // `select 'a\'; drop table t --'` look like one statement to this guard while Npgsql split it
            // into two and ran both. Verified against Postgres 17. The prefix has to be a standalone e/E:
            // `date'2026-01-01'` also ends in an 'e' and is not an escape string.
            var escapes = quote == '\'' && IsEscapeStringPrefix(start);

            position++;

            while (position < text.Length)
            {
                if (escapes && text[position] == '\\' && position + 1 < text.Length)
                {
                    position += 2;
                    continue;
                }

                if (text[position] == quote)
                {
                    if (Peek(1) == quote)
                    {
                        position += 2;
                        continue;
                    }

                    position++;
                    error = null;
                    return true;
                }

                position++;
            }

            error = SqlGuardResult.Reject(
                SqlRejectionReason.UnterminatedString,
                $"A {quote} quoted string is never closed.",
                quote.ToString(),
                start);
            return false;
        }

        /// <summary>Whether the quote at <paramref name="quotePosition"/> opens an <c>E'…'</c> literal.</summary>
        private bool IsEscapeStringPrefix(int quotePosition)
        {
            if (quotePosition == 0 || text[quotePosition - 1] is not ('e' or 'E'))
            {
                return false;
            }

            // The e has to be a token of its own; `date'…'`, `alue'…'` and anything else that merely ends
            // in an e is a typed literal or a syntax error, not an escape string.
            var before = quotePosition - 2;

            return before < 0 || !(char.IsLetterOrDigit(text[before]) || text[before] is '_' or '$');
        }

        private bool TryReadDollarTag(out string? tag)
        {
            // $tag$ or $$; a bare $1 parameter placeholder is not a dollar quote. A tag follows the rules
            // of an unquoted identifier, so it cannot begin with a digit: Postgres reads `$1$…$1$` as two
            // parameter placeholders around text, not as a quoted body, and a scanner that read it as a
            // body would skip the `;` inside it - the one thing this scanner must never do.
            var probe = position + 1;

            if (probe < text.Length && text[probe] != '$')
            {
                if (!char.IsLetter(text[probe]) && text[probe] != '_')
                {
                    tag = null;
                    return false;
                }

                probe++;

                while (probe < text.Length && (char.IsLetterOrDigit(text[probe]) || text[probe] == '_'))
                {
                    probe++;
                }
            }

            if (probe < text.Length && text[probe] == '$')
            {
                tag = text[position..(probe + 1)];
                return true;
            }

            tag = null;
            return false;
        }

        private bool SkipDollarQuoted(string tag, out SqlGuardResult? error)
        {
            var start = position;
            var closing = text.IndexOf(tag, position + tag.Length, StringComparison.Ordinal);

            if (closing < 0)
            {
                error = SqlGuardResult.Reject(
                    SqlRejectionReason.UnterminatedDollarQuote,
                    $"A {tag} quoted body is never closed.",
                    tag,
                    start);
                return false;
            }

            position = closing + tag.Length;
            error = null;
            return true;
        }
    }
}
