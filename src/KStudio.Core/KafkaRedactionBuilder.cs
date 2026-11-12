using KStudio.Core.Redaction;

namespace KStudio;

/// <summary>
/// The redaction rules of one topic (AGENTS.md D40). A masked field shows "••• (redacted by {rule})" and is never kept: not its text, not
/// even its length. A rule masks every case variant of its name, and a CloudEvents rule masks the attribute in the structured envelope and
/// in its binary-mode header (<c>ce_{attribute}</c>, and <c>content-type</c> for <c>datacontenttype</c>) alike.
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
