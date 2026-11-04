using System.Globalization;
using System.Text.RegularExpressions;

namespace PostgresStudio.Services.Find;

/// <summary>A table or view name as typed: <c>name</c> or <c>schema.name</c>.</summary>
/// <param name="Schema">The schema part, or <see langword="null" /> when none was typed.</param>
/// <param name="Name">The name.</param>
internal sealed record FindIdentifier(string? Schema, string Name);

/// <summary>
/// What a visitor typed into Find, read once and for good: which kinds of thing it could be (1.2). Pure - no clock, no service, no
/// database - so the table of shapes is the whole contract (D-SCOPE F3).
/// </summary>
/// <remarks>
/// <para>
/// <b>Every kind whose shape the value fits runs; it is not first-hit-wins.</b> A document and its stream share an id and both are
/// worth offering. The one exception is <c>#123</c>: that is how the studio prints an event sequence everywhere, so it is a sequence
/// and nothing else (<see cref="SequenceOnly" />).
/// </para>
/// <para>
/// A shape is a reason to ask, never an answer: a contributor still asks the read of the page its hit opens, under that page's gates.
/// </para>
/// </remarks>
internal sealed partial record FindQuery
{
    /// <summary>The longest value Find takes. An id is far shorter; more is refused before any read.</summary>
    public const int MaxLength = 512;

    /// <summary>What a refused over-long value says.</summary>
    public static readonly string TooLong = $"Too long to be an id ({MaxLength} characters at most).";

    private static readonly Regex IdentifierShape = IdentifierPattern();

    private FindQuery(string value)
    {
        Value = value;
    }

    /// <summary>What was typed, trimmed. For a refused value, the trimmed text up to the bound.</summary>
    public string Value { get; private init; }

    /// <summary>Whether nothing was typed.</summary>
    public bool IsEmpty => Value.Length == 0;

    /// <summary>Why the value is not searched at all, or <see langword="null" />: it is too long to be an id.</summary>
    public string? Refusal { get; private init; }

    /// <summary>Whether the value was typed as <c>#123</c>, which names an event sequence and nothing else.</summary>
    public bool SequenceOnly { get; private init; }

    /// <summary>The event sequence it could be: <c>#n</c> or all digits, positive; otherwise <see langword="null" />.</summary>
    public long? Sequence { get; private init; }

    /// <summary>The table or view name it could be, or <see langword="null" /> when it is not shaped like an identifier.</summary>
    public FindIdentifier? Identifier { get; private init; }

    /// <summary>The statement <c>queryid</c> it could be: a signed 64-bit number that is negative or has twelve digits or more.</summary>
    public long? QueryId { get; private init; }

    /// <summary>Whether it is worth asking at all: something was typed, it was not refused, and it is not a bare sequence.</summary>
    public bool CouldBeAnId => !IsEmpty && Refusal is null && !SequenceOnly;

    /// <summary>Reads what was typed.</summary>
    /// <param name="typed">The box's text, or <see langword="null" />.</param>
    public static FindQuery Parse(string? typed)
    {
        string value = (typed ?? string.Empty).Trim();

        if (value.Length == 0)
        {
            return new FindQuery(string.Empty);
        }

        if (value.Length > MaxLength)
        {
            return new FindQuery(value[..MaxLength]) { Refusal = TooLong };
        }

        if (value[0] == '#')
        {
            return new FindQuery(value) { SequenceOnly = true, Sequence = ParseSequence(value[1..]) };
        }

        return new FindQuery(value)
        {
            Sequence = ParseSequence(value),
            Identifier = ParseIdentifier(value),
            QueryId = ParseQueryId(value),
        };
    }

    private static long? ParseSequence(string digits)
    {
        if (digits.Length == 0 || digits.Length > 19)
        {
            return null;
        }

        foreach (char c in digits)
        {
            if (c is < '0' or > '9')
            {
                return null;
            }
        }

        return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out long sequence) && sequence > 0 ? sequence : null;
    }

    private static FindIdentifier? ParseIdentifier(string value)
    {
        if (!IdentifierShape.IsMatch(value))
        {
            return null;
        }

        int dot = value.IndexOf('.', StringComparison.Ordinal);
        return dot < 0 ? new FindIdentifier(null, value) : new FindIdentifier(value[..dot], value[(dot + 1)..]);
    }

    /// <summary>
    /// A <c>queryid</c> is a 64-bit hash, so a small positive number is far more likely an id of something else: one below 10^11
    /// is about one chance in 10^7 of being a statement, and is not offered as one.
    /// </summary>
    private static long? ParseQueryId(string value)
    {
        if (value.Length > 20 || !long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long id))
        {
            return null;
        }

        return id < 0 || id >= 100_000_000_000L ? id : null;
    }

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_$]{0,62}(\.[A-Za-z_][A-Za-z0-9_$]{0,62})?$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();
}
