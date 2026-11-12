namespace KStudio.Core.Correlation;

/// <summary>How a correlation value was written where it was read.</summary>
internal enum CorrelationValueForm
{
    /// <summary>Text: a header, an attribute, a key or a JSON string.</summary>
    Text,

    /// <summary>A JSON number, as its raw text.</summary>
    Number,

    /// <summary>A JSON <c>true</c> or <c>false</c>.</summary>
    Bool,
}

/// <summary>One value an anchor holds for a correlation kind.</summary>
/// <param name="Kind">What the value means.</param>
/// <param name="Value">The value's text (a trace id for the <c>trace</c> kind).</param>
/// <param name="Form">How it was written where it was read.</param>
/// <param name="From">The field of the anchor it came from, as the search grammar names it.</param>
internal sealed record CorrelationValue(string Kind, string Value, CorrelationValueForm Form, string From);

/// <summary>
/// The correlation values of one anchor, or of one seed: every value was already visible to the visitor on the anchor (a literal, unmasked
/// field), bounded, and with the reasons some were left out.
/// </summary>
internal sealed class CorrelationKeySet
{
    /// <summary>An empty set.</summary>
    public static CorrelationKeySet Empty { get; } = new([], []);

    /// <summary>Creates a key set.</summary>
    /// <param name="values">The values, in the order of the fields they were read from.</param>
    /// <param name="notes">What was left out, in words.</param>
    public CorrelationKeySet(IReadOnlyList<CorrelationValue> values, IReadOnlyList<string> notes)
    {
        Values = values;
        Notes = notes;
    }

    /// <summary>The values.</summary>
    public IReadOnlyList<CorrelationValue> Values { get; }

    /// <summary>What the bounds left out.</summary>
    public IReadOnlyList<string> Notes { get; }

    /// <summary>Whether there is nothing to search for.</summary>
    public bool IsEmpty => Values.Count == 0;

    /// <summary>The values of a kind, in order.</summary>
    /// <param name="kind">The kind.</param>
    public IEnumerable<CorrelationValue> Of(string kind) => Values.Where(x => string.Equals(x.Kind, kind, StringComparison.Ordinal));

    /// <summary>The set a link makes from one value of one kind (seed mode, decision 8 of D-CORR): there is no anchor.</summary>
    /// <param name="kind">The kind, for example <c>document:Order</c> or <c>stream</c>.</param>
    /// <param name="value">The value.</param>
    public static CorrelationKeySet Seed(string kind, string value)
    {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(value);

        return new CorrelationKeySet([new CorrelationValue(kind, value, CorrelationValueForm.Text, "link")], []);
    }
}
