using System.Text.RegularExpressions;

namespace MartenStudio.Services.Events;

/// <summary>
/// A dead letter's message with its variable parts replaced by placeholders, so that letters which failed for the same
/// reason fall into one group (M3): quoted strings, GUIDs, timestamps, hex runs and numbers.
/// </summary>
/// <remarks>
/// <para>
/// <b>One rule, two engines.</b> The grouped view groups in PostgreSQL (<c>DeadLetterGroupQueries</c>), so that the
/// counts are exact and only groups cross the wire, and the same groups have to be found again when a group is opened in
/// the list or skipped - which is SQL too. The rule is therefore written once, as <see cref="Rules" />: each pattern is a
/// string both .NET's regex engine and PostgreSQL's ARE read the same way, and the SQL builder binds it as a parameter
/// to nested <c>regexp_replace</c> calls. <see cref="Of" /> applies the same table in C#, which is what the unit tests
/// read; <c>DeadLetterGroupsLiveTests</c> holds the two equal over the same corpus, as <c>TriggerParityLiveTests</c>
/// holds <c>Autovacuum.Triggers</c> to its SQL.
/// </para>
/// <para>
/// <b>The common subset.</b> No <c>\b</c> (a backspace in ARE, a word boundary in .NET), no <c>\d</c>, <c>\s</c> or
/// <c>\w</c> (whose classes differ beyond ASCII), no case-insensitive flag and no lazy quantifier: explicit ASCII
/// classes, greedy quantifiers and positive lookahead, which both engines have. A word boundary is spelled as a
/// captured leading character, <c>(^|[^0-9A-Za-z_])</c>, put back by the replacement (<c>${1}</c> in .NET, <c>\1</c> in
/// PostgreSQL), and a lookahead at the end. Each pattern is greedy throughout and has one way to match at a position -
/// an alternation's branches never both match there - so .NET's leftmost-first and PostgreSQL's leftmost-longest agree:
/// the one place they could part, a quote at the start of <c>''</c>, is closed by keeping the quote out of the single-quote
/// rule's boundary. Whitespace is collapsed first, so <c>$</c> is the end of the text in both.
/// </para>
/// <para>
/// <b>What it is for, in practice.</b> Marten's daemon writes a dead letter's message from the daemon's own
/// <c>ApplyEventException</c> - "Failure to apply event #42 Id(…)" - and its type from the exception the projection
/// threw, at JasperFx 2.74.0 and 2.95.1 alike; an event Marten could not read is "Event deserialization error on
/// sequence = 42 for event type …" (<c>EventDeserializationFailureException.ToDeadLetterEvent</c>). So for Marten's own
/// letters the fingerprint is close to constant per exception type, and what separates causes is the exception type
/// and the event type. The fingerprint is what keeps a group honest when a message does carry its cause - a host's own
/// letters, or a Marten that one day stores the inner message.
/// </para>
/// </remarks>
internal static class DeadLetterFingerprint
{
    /// <summary>How much of a message is fingerprinted, in characters (code points, as PostgreSQL's <c>left</c> counts).</summary>
    public const int MaxInputLength = 1_000;

    /// <summary>What a number becomes.</summary>
    public const string NumberPlaceholder = "{n}";

    /// <summary>What a GUID becomes.</summary>
    public const string GuidPlaceholder = "{guid}";

    /// <summary>What a timestamp, a date or a time of day becomes.</summary>
    public const string TimestampPlaceholder = "{timestamp}";

    /// <summary>What a hex literal or a run of hex digits becomes.</summary>
    public const string HexPlaceholder = "{hex}";

    /// <summary>What a quoted string's contents become.</summary>
    public const string StringPlaceholder = "{string}";

    private const string WordCharacters = "0-9A-Za-z_";

    /// <summary>A word boundary before a token: the start, or a character that is not a word character - captured.</summary>
    private const string Before = "(^|[^" + WordCharacters + "])";

    /// <summary>A word boundary after a token: a character that is not a word character, or the end - not consumed.</summary>
    private const string After = "(?=[^" + WordCharacters + "]|$)";

    /// <summary>The rule, in the order it is applied.</summary>
    public static IReadOnlyList<DeadLetterFingerprintRule> Rules { get; } =
    [
        // Literal control characters, not escapes: ARE and .NET read a bracket's escapes differently.
        new("whitespace", "[ \t\n\r\f\v]+", " ", keepsLeadingCharacter: false),
        new("double-quoted string", "\"[^\"]*\"", "\"" + StringPlaceholder + "\"", keepsLeadingCharacter: false),

        // Only after a non-word character, so the apostrophes of "can't" and "doesn't" never open a string - and never after
        // a quote, so at the start of '' the two alternatives cannot both match: .NET takes the first alternative that
        // matches and PostgreSQL the longest match, and with a quote allowed before a quote they parted (the parity test).
        new("single-quoted string", "(^|[^" + WordCharacters + "'])'[^']*'", "'" + StringPlaceholder + "'", keepsLeadingCharacter: true),
        new(
            "guid",
            "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
            GuidPlaceholder,
            keepsLeadingCharacter: false),
        new(
            "iso timestamp",
            "[0-9]{4}-[0-9]{2}-[0-9]{2}([T ][0-9]{2}:[0-9]{2}(:[0-9]{2}([.,][0-9]+)?)?)?(Z|[+-][0-9]{2}(:?[0-9]{2})?)?",
            TimestampPlaceholder,
            keepsLeadingCharacter: false),
        new(
            "local timestamp",
            "[0-9]{1,2}[/.][0-9]{1,2}[/.][0-9]{4}( [0-9]{1,2}:[0-9]{2}(:[0-9]{2})?( ?[AaPp][Mm])?)?",
            TimestampPlaceholder,
            keepsLeadingCharacter: false),
        new("time of day", "[0-9]{1,2}:[0-9]{2}:[0-9]{2}([.,][0-9]+)?", TimestampPlaceholder, keepsLeadingCharacter: false),
        new("hex literal", Before + "0[xX][0-9a-fA-F]+" + After, HexPlaceholder, keepsLeadingCharacter: true),

        // Eight or more hex digits with at least one letter and one digit: a hash, a token, a GUID without its dashes. A
        // run of digits alone is a number, and a run of a-f alone is a word ("deadbeef" stays as written).
        new(
            "hex run",
            Before + "(?=[0-9]*[a-fA-F])(?=[a-fA-F]*[0-9])[0-9a-fA-F]{8,}" + After,
            HexPlaceholder,
            keepsLeadingCharacter: true),

        // A whole token only: "partition_2", "V2" and "42P01" are names, not quantities.
        new("number", Before + "[0-9]+([.,][0-9]+)*" + After, NumberPlaceholder, keepsLeadingCharacter: true),
    ];

    /// <summary>The fingerprint of <paramref name="message" />, or <see langword="null" /> for none.</summary>
    /// <param name="message">A dead letter's <c>ExceptionMessage</c>.</param>
    public static string? Of(string? message)
    {
        if (message is null)
        {
            return null;
        }

        string text = Truncate(message, MaxInputLength);

        foreach (DeadLetterFingerprintRule rule in Rules)
        {
            text = rule.Replace(text);
        }

        return text.Trim(' ');
    }

    /// <summary>The first <paramref name="length" /> code points of <paramref name="text" />, as PostgreSQL's <c>left</c> counts them.</summary>
    internal static string Truncate(string text, int length)
    {
        ArgumentNullException.ThrowIfNull(text);

        int index = 0;
        int counted = 0;

        while (index < text.Length)
        {
            if (counted == length)
            {
                return text[..index];
            }

            index += char.IsSurrogatePair(text, index) ? 2 : 1;
            counted++;
        }

        return text;
    }
}

/// <summary>One step of <see cref="DeadLetterFingerprint" />: a pattern both engines read alike, and what replaces it.</summary>
internal sealed class DeadLetterFingerprintRule
{
    private readonly Regex regex;

    /// <summary>A step.</summary>
    /// <param name="name">What the step replaces, for a test's message.</param>
    /// <param name="pattern">The pattern, in the subset .NET and PostgreSQL's ARE share.</param>
    /// <param name="placeholder">The text a match becomes.</param>
    /// <param name="keepsLeadingCharacter">
    /// Whether the pattern's first group is the boundary character before the token, which the replacement puts back.
    /// </param>
    public DeadLetterFingerprintRule(string name, string pattern, string placeholder, bool keepsLeadingCharacter)
    {
        Name = name;
        Pattern = pattern;
        Placeholder = placeholder;
        KeepsLeadingCharacter = keepsLeadingCharacter;
        regex = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }

    /// <summary>What the step replaces.</summary>
    public string Name { get; }

    /// <summary>The pattern, bound as a parameter in SQL.</summary>
    public string Pattern { get; }

    /// <summary>The text a match becomes.</summary>
    public string Placeholder { get; }

    /// <summary>Whether the replacement puts back the boundary character the pattern's first group captured.</summary>
    public bool KeepsLeadingCharacter { get; }

    /// <summary>The replacement as .NET's <c>Regex.Replace</c> reads it.</summary>
    public string DotNetReplacement => KeepsLeadingCharacter ? "${1}" + Placeholder : Placeholder;

    /// <summary>The replacement as PostgreSQL's <c>regexp_replace</c> reads it.</summary>
    public string PostgresReplacement => KeepsLeadingCharacter ? "\\1" + Placeholder : Placeholder;

    /// <summary>Every match in <paramref name="text" /> replaced, as <c>regexp_replace(…, 'g')</c> replaces them.</summary>
    public string Replace(string text) => regex.Replace(text, DotNetReplacement);

    /// <inheritdoc />
    public override string ToString() => Name + ": " + Pattern;
}
