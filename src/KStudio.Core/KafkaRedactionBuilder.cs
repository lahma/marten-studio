using KStudio.Core.Redaction;

namespace KStudio;

/// <summary>
/// The redaction rules of one topic (AGENTS.md D40). A masked field shows "••• (redacted by {rule})" and is never kept: not its text, not
/// even its length. A rule masks every case variant of its name, and a CloudEvents rule masks the attribute in the structured envelope and
/// in its binary-mode header (<c>ce_{attribute}</c>, and <c>content-type</c> for <c>datacontenttype</c>) alike. A JSON-path or pattern rule
/// masks parts of the value; a value it cannot be applied to is withheld, and searches compare what a visitor would see.
/// </summary>
public sealed class KafkaRedactionBuilder
{
    private readonly KafkaRedactionRules rules;

    internal KafkaRedactionBuilder(KafkaRedactionRules rules) => this.rules = rules;

    /// <summary>Masks a header's value. The name is compared ignoring case.</summary>
    /// <param name="name">The header's name.</param>
    /// <returns>The rule just declared, which can be made searchable.</returns>
    public KafkaRedactionRule Header(string name) => new(rules.Header(name), this);

    /// <summary>Masks the record's key.</summary>
    /// <returns>The rule just declared, which can be made searchable.</returns>
    public KafkaRedactionRule Key() => new(rules.Key(), this);

    /// <summary>Masks a CloudEvents attribute, in the structured envelope and in its binary-mode header.</summary>
    /// <param name="attribute">The attribute's name: 1 to 64 lower-case letters and digits, as CloudEvents names them.</param>
    /// <returns>The rule just declared, which can be made searchable.</returns>
    public KafkaRedactionRule CloudEvent(string attribute) => new(rules.CloudEvent(attribute), this);

    /// <summary>
    /// Masks the leaf, or the whole subtree, at a JSON path (<c>$.a.b</c>, <c>$.a[0]</c>, <c>$.a[*].b</c>) of the value; for a CloudEvent, of
    /// its data. Member names are compared ignoring case. A value this rule cannot be applied to (one that is not JSON) is withheld whole.
    /// </summary>
    /// <param name="path">The path: <c>$</c>, then up to 32 steps, each <c>.name</c>, <c>['name']</c>, <c>[n]</c> or <c>[*]</c>.</param>
    /// <returns>The rule just declared, which can be made searchable by an exact match.</returns>
    public KafkaRedactionRule JsonPath(string path) => new(rules.JsonPath(path), this);

    /// <summary>
    /// Masks every match of a regular expression in the value's text: each JSON string and number, or the whole text of a text value. It is
    /// never searchable. A value it cannot be applied to is withheld whole.
    /// </summary>
    /// <param name="regex">At most 500 characters; it runs without backtracking, so backreferences and lookarounds are refused.</param>
    /// <param name="name">What a visitor reads in "redacted by pattern {name}": 1 to 64 letters, digits, '-' or '_'; by default the rule's place among the topic's patterns.</param>
    /// <returns>This builder.</returns>
    public KafkaRedactionBuilder Pattern(string regex, string? name = null)
    {
        rules.Pattern(regex, name);
        return this;
    }

    /// <summary>
    /// Opts one of the built-in deny-list's headers (<c>authorization</c>, <c>proxy-authorization</c>, <c>cookie</c>, <c>set-cookie</c>,
    /// <c>x-api-key</c>) out of masking, by name, ignoring case.
    /// </summary>
    /// <param name="name">The header's name.</param>
    /// <returns>This builder.</returns>
    public KafkaRedactionBuilder AllowHeader(string name)
    {
        rules.AllowHeader(name);
        return this;
    }
}

/// <summary>One redaction rule, just declared.</summary>
public sealed class KafkaRedactionRule
{
    private readonly KafkaRedactionRules.RuleHandle handle;
    private readonly KafkaRedactionBuilder owner;

    internal KafkaRedactionRule(KafkaRedactionRules.RuleHandle handle, KafkaRedactionBuilder owner)
    {
        this.handle = handle;
        this.owner = owner;
    }

    /// <summary>
    /// Lets <c>=</c> and <c>in</c> search the masked field by an exact value. <c>^=</c>, <c>~</c> and <c>any</c> never can, and a header on the
    /// built-in deny-list never can: declaring it is refused when the options are validated.
    /// </summary>
    /// <returns>The builder, to go on declaring rules.</returns>
    public KafkaRedactionBuilder Searchable()
    {
        handle.Searchable();
        return owner;
    }
}
