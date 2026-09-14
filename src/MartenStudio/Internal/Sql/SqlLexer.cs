namespace MartenStudio.Internal.Sql;

/// <summary>What one thing <see cref="SqlLexer" /> found is.</summary>
internal enum SqlTokenKind
{
    /// <summary>A bare word: an unquoted identifier or a keyword, outside every literal and comment.</summary>
    Word,

    /// <summary>One character of anything else - <c>;</c>, <c>(</c>, <c>)</c>, <c>,</c>, an operator.</summary>
    Punctuation,

    /// <summary>
    /// The text does not close: an unterminated string, block comment or dollar-quoted body. The walk
    /// cannot go on, and every caller refuses rather than guessing.
    /// </summary>
    Unreadable,
}

/// <summary>Which thing never closed, when a walk hit <see cref="SqlTokenKind.Unreadable" />.</summary>
internal enum SqlLexFault
{
    /// <summary>Nothing went wrong.</summary>
    None,

    /// <summary>A <c>'…'</c> literal or a <c>"…"</c> identifier is never closed.</summary>
    UnterminatedString,

    /// <summary>A <c>/* … */</c> comment is never closed.</summary>
    UnterminatedComment,

    /// <summary>A <c>$tag$ … $tag$</c> body is never closed.</summary>
    UnterminatedDollarQuote,
}

/// <summary>One thing <see cref="SqlLexer" /> found, and where.</summary>
/// <param name="Kind">Word, punctuation, or "this text cannot be read".</param>
/// <param name="Text">
/// The word as typed, the single punctuation character, or - for
/// <see cref="SqlTokenKind.Unreadable" /> - the thing that never closed (<c>/*</c>, the quote character,
/// or the dollar tag), which is what a refusal points the user at.
/// </param>
/// <param name="Start">Where it starts, zero-based.</param>
/// <param name="End">One past its last character. Equal to <paramref name="Start" /> for a fault.</param>
/// <param name="Depth">
/// The parenthesis depth at <paramref name="Start" />, counted the way Postgres would: a <c>(</c> carries
/// the depth <em>before</em> it opened, and a <c>)</c> that closes nothing leaves the depth at zero rather
/// than going negative.
/// </param>
/// <param name="Fault">Which thing never closed, for <see cref="SqlTokenKind.Unreadable" />.</param>
internal readonly record struct SqlToken(
    SqlTokenKind Kind,
    string Text,
    int Start,
    int End,
    int Depth,
    SqlLexFault Fault)
{
    /// <summary>Whether this is the single punctuation character <paramref name="character" />.</summary>
    public bool Is(char character) =>
        Kind == SqlTokenKind.Punctuation && Text.Length == 1 && Text[0] == character;

    /// <summary>Whether this is the bare word <paramref name="word" />, whatever its case.</summary>
    public bool IsWord(string word) =>
        Kind == SqlTokenKind.Word && string.Equals(Text, word, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The one lexical walk over Postgres text this assembly has, shared by every guard that needs to know
/// where a string, a comment or a dollar-quoted body is.
/// </summary>
/// <remarks>
/// <para>
/// <b>One scanner, because the divergences are invisible.</b> <see cref="ReadOnlySqlGuard" /> and
/// <see cref="QuerySqlComposer" /> ask different questions of different text and produce different refusal
/// shapes, but they need the <em>same</em> answer to "is this character inside a literal?". They used to
/// carry a private copy of the walk each, and three separate reviews found the same defect twice over: a
/// dollar tag beginning with a digit had to be fixed in both, and a <c>--</c> comment that ended at LF but
/// not at CR had to be fixed in both. A scanner that disagrees with Postgres about where a string ends is
/// the one thing a guard may never do, and two scanners that <em>nearly</em> agree is how that keeps
/// happening. So there is one, and the guards share only the lexing: their verdicts, messages and result
/// shapes stay their own.
/// </para>
/// <para>
/// It agrees with Postgres about five things that are easy to get wrong, each of which was a live-proven
/// escape before it was a rule:
/// </para>
/// <list type="bullet">
/// <item><description>
/// a <c>--</c> comment ends at a carriage return as well as at a line feed - Postgres' own lexer spells
/// <c>non_newline</c> as <c>[^\n\r]</c>, so a scanner that read on to the LF hid the rest of that line from
/// every rule while Postgres ran it;
/// </description></item>
/// <item><description>
/// block comments <b>nest</b> (<c>/* /* */ */</c> is one comment), unlike C's;
/// </description></item>
/// <item><description>
/// a doubled quote inside a literal is an escaped quote and does not end it (<c>'it''s'</c>);
/// </description></item>
/// <item><description>
/// a backslash escapes only inside an <c>E'…'</c> literal - with the default
/// <c>standard_conforming_strings = on</c> the quote after <c>'a\</c> <em>closes</em> the string; and
/// </description></item>
/// <item><description>
/// a dollar-quote tag follows the rules of an unquoted identifier, so <c>$1$</c> is a parameter
/// placeholder and not the start of a body - reading it as one let a <c>;</c> hide between two of them.
/// </description></item>
/// </list>
/// <para>
/// It is a lexer and not a parser: it knows where the literals are and nothing about what the statement
/// means. Anything it cannot read comes back as <see cref="SqlTokenKind.Unreadable" /> rather than as a
/// guess, because "I cannot tell what this would run" and "this is safe" are not the same answer.
/// </para>
/// <para>
/// It is a mutable <see langword="struct" /> on purpose: a caller that needs to look ahead and put the
/// text back copies it (<c>SqlLexer before = lexer;</c> … <c>lexer = before;</c>), which is cheaper and far
/// harder to get wrong than a rewind method.
/// </para>
/// </remarks>
/// <param name="text">The text to walk. Never null.</param>
internal struct SqlLexer(string text)
{
    private int position;
    private int depth;

    /// <summary>Where the walk has got to, zero-based.</summary>
    public readonly int Position => position;

    /// <summary>The parenthesis depth at <see cref="Position" />. Zero for balanced text.</summary>
    public readonly int Depth => depth;

    /// <summary>
    /// Reads the next thing: a bare word, one character of punctuation, or a fault. Whitespace, comments,
    /// string literals, quoted identifiers and dollar-quoted bodies are walked over rather than returned.
    /// </summary>
    /// <param name="token">What was found, when something was.</param>
    /// <returns>
    /// <see langword="false" /> at the end of the text. A fault is a token like any other and comes back
    /// <see langword="true" /> with <see cref="SqlTokenKind.Unreadable" /> - callers must look at
    /// <see cref="SqlToken.Kind" /> rather than only at the return value. After a fault the walk is parked
    /// at the end of the text, so a caller that carries on regardless stops rather than spinning.
    /// </returns>
    public bool TryRead(out SqlToken token)
    {
        while (position < text.Length)
        {
            char c = text[position];

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
                int start = position;

                if (!TrySkipBlockComment())
                {
                    token = Fault(SqlLexFault.UnterminatedComment, "/*", start);
                    return true;
                }

                continue;
            }

            if (c is '\'' or '"')
            {
                int start = position;

                if (!TrySkipQuoted())
                {
                    token = Fault(SqlLexFault.UnterminatedString, c.ToString(), start);
                    return true;
                }

                continue;
            }

            if (c == '$' && TryReadDollarTag(out string? tag))
            {
                int start = position;

                if (!TrySkipDollarQuoted(tag!))
                {
                    token = Fault(SqlLexFault.UnterminatedDollarQuote, tag!, start);
                    return true;
                }

                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                int wordStart = position;

                while (position < text.Length && (char.IsLetterOrDigit(text[position]) || text[position] == '_'))
                {
                    position++;
                }

                token = new SqlToken(
                    SqlTokenKind.Word, text[wordStart..position], wordStart, position, depth, SqlLexFault.None);
                return true;
            }

            // Everything else is one character, and the parentheses among them move the depth. A ')' that
            // closes nothing leaves the depth at zero instead of going negative, so the depth a later word
            // carries is the depth Postgres would give it; the caller that cares about the stray bracket
            // sees it as a ')' token at depth zero.
            int at = position;
            int before = depth;

            position++;

            if (c == '(')
            {
                depth++;
            }
            else if (c == ')' && depth > 0)
            {
                depth--;
            }

            token = new SqlToken(SqlTokenKind.Punctuation, c.ToString(), at, at + 1, before, SqlLexFault.None);
            return true;
        }

        token = default;
        return false;
    }

    private readonly char Peek(int offset) => position + offset < text.Length ? text[position + offset] : '\0';

    private SqlToken Fault(SqlLexFault fault, string token, int start)
    {
        // Parked at the end: a caller that keeps reading after a fault gets "no more tokens" rather than
        // the same fault for ever. Every caller in this assembly refuses on the first one.
        position = text.Length;

        return new SqlToken(SqlTokenKind.Unreadable, token, start, start, depth, fault);
    }

    /// <summary>
    /// Walks over a <c>--</c> comment, which ends at a carriage return as well as at a line feed.
    /// </summary>
    /// <remarks>
    /// Postgres' lexer spells <c>non_newline</c> as <c>[^\n\r]</c>. A scanner that ran on to the LF hid
    /// everything after a lone CR from the statement-separator rule, the bracket balance, the word
    /// denylists and the sort-list check while Postgres ran it - measured, as a cross-tenant read from the
    /// mode that needs no capability at all. This is the single line that was wrong twice, in two copies,
    /// which is why there is now one copy.
    /// </remarks>
    private void SkipLineComment()
    {
        while (position < text.Length && text[position] is not ('\n' or '\r'))
        {
            position++;
        }
    }

    /// <summary>Walks over a <c>/* … */</c> comment, counting nesting the way Postgres does.</summary>
    private bool TrySkipBlockComment()
    {
        var nesting = 0;

        while (position < text.Length)
        {
            if (text[position] == '/' && Peek(1) == '*')
            {
                nesting++;
                position += 2;
                continue;
            }

            if (text[position] == '*' && Peek(1) == '/')
            {
                nesting--;
                position += 2;

                if (nesting == 0)
                {
                    return true;
                }

                continue;
            }

            position++;
        }

        return false;
    }

    /// <summary>Walks over a <c>'…'</c> literal or a <c>"…"</c> identifier, doubling and escapes included.</summary>
    private bool TrySkipQuoted()
    {
        int start = position;
        char quote = text[start];

        // Backslash escapes exist in an E'' literal and nowhere else. With Postgres' default
        // standard_conforming_strings = on, the backslash in 'a\' is an ordinary character and the quote
        // after it CLOSES the string - so treating it as an escape made `select 'a\'; drop table t --'`
        // look like one statement while Npgsql split it into two and ran both. Verified against Postgres
        // 17. The prefix has to be a standalone e/E: `date'2026-01-01'` also ends in an 'e' and is not an
        // escape string.
        bool escapes = quote == '\'' && IsEscapeStringPrefix(start);

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
                return true;
            }

            position++;
        }

        return false;
    }

    /// <summary>Whether the quote at <paramref name="quotePosition" /> opens an <c>E'…'</c> literal.</summary>
    private readonly bool IsEscapeStringPrefix(int quotePosition)
    {
        if (quotePosition == 0 || text[quotePosition - 1] is not ('e' or 'E'))
        {
            return false;
        }

        // The e has to be a token of its own; `date'…'`, `alue'…'` and anything else that merely ends in an
        // e is a typed literal or a syntax error, not an escape string.
        int before = quotePosition - 2;

        return before < 0 || !(char.IsLetterOrDigit(text[before]) || text[before] is '_' or '$');
    }

    /// <summary>
    /// Reads the <c>$tag$</c> that opens a dollar-quoted body at the current position, if one does.
    /// </summary>
    /// <remarks>
    /// <b>The first character of a tag has to be a letter or an underscore</b>, because a dollar-quote tag
    /// follows the rules of an unquoted identifier and Postgres reads <c>$1</c> as a parameter placeholder.
    /// Accepting a digit made <c>'x' = $1$;drop table x;--$1$</c> look like a quoted body, and the scanner
    /// then walked straight past both semicolons - a scanner disagreeing with Postgres about where a string
    /// is, which is the one thing it may never do. <c>$$</c> with no tag at all is still a body.
    /// </remarks>
    private readonly bool TryReadDollarTag(out string? tag)
    {
        int probe = position + 1;

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

    /// <summary>Walks over the body <paramref name="tag" /> opened, up to and including its closing tag.</summary>
    private bool TrySkipDollarQuoted(string tag)
    {
        int closing = text.IndexOf(tag, position + tag.Length, StringComparison.Ordinal);

        if (closing < 0)
        {
            return false;
        }

        position = closing + tag.Length;
        return true;
    }
}
