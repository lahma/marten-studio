using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace PostgresStudio.Services;

/// <summary>
/// A value a visitor could have typed, made safe to write into the application's log (wave 6): every control, format and
/// line or paragraph separator character escaped, and the whole bounded - as the Activity ring's target is
/// (<c>SqlConsoleService.Target</c>), but escaping rather than dropping what it cannot print.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> A refused scope is what the visitor asked for, not what exists: <c>?store=</c> and <c>?db=</c> are the
/// address's, and a refusal (9203, 9205, the 9201 that records it) logs them before anything has resolved them. Uncapped,
/// one request writes a log line of any length; with a line break in it, it writes a second line that reads as the
/// application's own (log forging); with a bidirectional override, a line that does not read as it is. So the audit and
/// refusal events - 9200 to 9205, 9235, 9236, 9245 and 9452 through the per-product dispatchers every call goes through
/// (<c>StudioLog.PostgresStudio.cs</c>, <c>StudioLog.StatementsPage.cs</c>), 9206, 9207 and 9209 through MartenLog's
/// wrappers, and LINQ mode's 9260 to 9263 through its run's <c>Logged*</c> values - take each value a visitor could have
/// shaped through here, and so does every Warning that names a value from the address: a document id, a collection alias,
/// the scope's store, database or tenant (<c>RefusalLogValuesTests</c>).
/// </para>
/// <para>
/// <b>Escaped, never dropped</b>, so an operator reading the line sees that the value had a line break in it, as
/// <c>\n</c>, rather than a value that silently differs from what was sent - and a backslash is doubled, so a value that
/// held a backslash and an <c>n</c> is not read as one that held a line break. A value cut at the bound ends with
/// <c>…</c> and says how long it was.
/// </para>
/// <para>
/// <b>Except the statement that ran.</b> 9204 records what the studio executed, so its statement is kept whole up to the
/// circuit's own receive bound and, past it, by its head and its tail (<see cref="CapStatement" />): cut at the front alone,
/// a statement padded with comment would leave what it really did out of every record. A refused statement (9205) never
/// ran, and is bounded like any other text.
/// </para>
/// </remarks>
internal static class StudioLogValue
{
    /// <summary>
    /// The most characters a logged name keeps - a user, a store key, a database id, a tenant, a target: past the Activity
    /// ring's target (two hundred characters and an ellipsis), so a target it already cut goes through as it is.
    /// </summary>
    public const int MaxLength = 256;

    /// <summary>
    /// The most characters a logged sentence keeps - a statement, a reason, an outcome, a filter: longer, because the
    /// statement is what 9204 and 9205 are about and a refusal's reason is a studio sentence that can run past a name's bound.
    /// </summary>
    public const int MaxTextLength = 4_000;

    /// <summary>
    /// The most characters 9204 keeps of the statement it says was executed: SignalR's default
    /// <c>HubOptions.MaximumReceiveMessageSize</c>, 32 KB, the most one circuit message - and so one statement - carries.
    /// </summary>
    public const int MaxStatementLength = 32_768;

    /// <summary><paramref name="value" />, escaped and bounded at <see cref="MaxLength" />; <see langword="null" /> stays so.</summary>
    /// <param name="value">A name a visitor could have typed.</param>
    [return: NotNullIfNotNull(nameof(value))]
    public static string? Cap(string? value) => Bounded(value, MaxLength);

    /// <summary><paramref name="text" />, escaped and bounded at <see cref="MaxTextLength" />; <see langword="null" /> stays so.</summary>
    /// <param name="text">A statement a visitor typed, or a sentence carrying what they typed.</param>
    [return: NotNullIfNotNull(nameof(text))]
    public static string? CapText(string? text) => Bounded(text, MaxTextLength);

    /// <summary>Whether <paramref name="c" /> is escaped: a control, format or line or paragraph separator character.</summary>
    internal static bool IsEscaped(char c) => char.GetUnicodeCategory(c) is UnicodeCategory.Control
        or UnicodeCategory.Format
        or UnicodeCategory.LineSeparator
        or UnicodeCategory.ParagraphSeparator;

    /// <summary>
    /// <paramref name="statement" />, escaped, and whole up to <see cref="MaxStatementLength" /> - the statement 9204 says was
    /// executed. Past it, its head and its tail, each half the bound, around a line saying how many characters were cut.
    /// </summary>
    /// <remarks>
    /// Not <see cref="CapText" />: cutting the statement that ran at 4,000 characters let a visitor pad its front with 4,000
    /// characters of comment or whitespace and keep the statement itself out of every record. The bound is the circuit's own:
    /// a statement reaches the server in one SignalR message, which <c>HubOptions.MaximumReceiveMessageSize</c> bounds at
    /// 32 KB by default, so under that default nothing that ran is cut. A host that raised the bound gets both ends, padding
    /// at the front and at the back alike, and the count of what is missing between them.
    /// </remarks>
    /// <param name="statement">The statement the studio executed.</param>
    [return: NotNullIfNotNull(nameof(statement))]
    public static string? CapStatement(string? statement)
    {
        if (statement is null)
        {
            return null;
        }

        if (statement.Length <= MaxStatementLength && !statement.Any(NeedsEscape))
        {
            return statement;
        }

        int budget = MaxStatementLength / 2;
        (string head, int headEnd) = Forward(statement, 0, budget);

        if (headEnd == statement.Length)
        {
            return head;
        }

        (string tail, int tailStart) = Backward(statement, headEnd, budget);

        if (tailStart == headEnd)
        {
            return head + tail;
        }

        return head
            + " … (" + (tailStart - headEnd).ToString("N0", CultureInfo.InvariantCulture)
            + " of " + statement.Length.ToString("N0", CultureInfo.InvariantCulture) + " characters cut) … "
            + tail;
    }

    /// <summary>Whether <paramref name="c" /> is written as an escape: <see cref="IsEscaped" />, or the escape character itself.</summary>
    private static bool NeedsEscape(char c) => c == '\\' || IsEscaped(c);

    /// <summary>
    /// The escaped form of the character at <paramref name="i" /> of <paramref name="value" /> - a whole surrogate pair when it
    /// starts one - and how many characters of <paramref name="value" /> it stands for.
    /// </summary>
    /// <remarks>
    /// A backslash is doubled (wave 6), so a value that held a backslash and an <c>n</c> reads as <c>\\n</c> and never as the
    /// escaped line break <c>\n</c>.
    /// </remarks>
    private static (string Piece, int Width) Piece(string value, int i)
    {
        char c = value[i];

        if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
        {
            return (value.Substring(i, 2), 2);
        }

        if (!NeedsEscape(c))
        {
            return (c.ToString(), 1);
        }

        string escaped = c switch
        {
            '\\' => "\\\\",
            '\r' => "\\r",
            '\n' => "\\n",
            '\t' => "\\t",
            _ => "\\u" + ((int) c).ToString("X4", CultureInfo.InvariantCulture),
        };

        return (escaped, 1);
    }

    /// <summary>
    /// The escaped characters of <paramref name="value" /> from <paramref name="start" /> on while they fit in
    /// <paramref name="budget" />, and the index of the first character left out.
    /// </summary>
    private static (string Escaped, int End) Forward(string value, int start, int budget)
    {
        StringBuilder safe = new(Math.Min(value.Length - start, budget) + 16);
        int i = start;

        while (i < value.Length)
        {
            (string piece, int width) = Piece(value, i);

            if (safe.Length + piece.Length > budget)
            {
                break;
            }

            safe.Append(piece);
            i += width;
        }

        return (safe.ToString(), i);
    }

    /// <summary>
    /// The escaped characters at the end of <paramref name="value" />, back to no earlier than <paramref name="floor" />, while
    /// they fit in <paramref name="budget" />, and the index of the first character kept.
    /// </summary>
    private static (string Escaped, int Start) Backward(string value, int floor, int budget)
    {
        List<string> pieces = [];
        int length = 0;
        int i = value.Length;

        while (i > floor)
        {
            // A low surrogate whose high surrogate is also past the floor is one character with it, never half of one.
            int at = i - 1;
            if (char.IsLowSurrogate(value[at]) && at - 1 >= floor && char.IsHighSurrogate(value[at - 1]))
            {
                at--;
            }

            (string piece, _) = Piece(value, at);

            if (length + piece.Length > budget)
            {
                break;
            }

            pieces.Add(piece);
            length += piece.Length;
            i = at;
        }

        pieces.Reverse();
        return (string.Concat(pieces), i);
    }

    private static string? Bounded(string? value, int max)
    {
        if (value is null)
        {
            return null;
        }

        if (value.Length <= max && !value.Any(NeedsEscape))
        {
            return value;
        }

        (string kept, int end) = Forward(value, 0, max);

        return end == value.Length
            ? kept
            : kept + "… (" + value.Length.ToString("N0", CultureInfo.InvariantCulture) + " characters)";
    }
}
