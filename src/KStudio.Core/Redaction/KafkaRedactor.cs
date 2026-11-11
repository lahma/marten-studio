namespace KStudio.Core.Redaction;

/// <summary>What a rule masks (decision 10).</summary>
internal enum RedactionTarget
{
    /// <summary>A header's value, by name.</summary>
    Header,

    /// <summary>The record's key.</summary>
    Key,

    /// <summary>A CloudEvents attribute, by name.</summary>
    CloudEvent,
}

/// <summary>
/// One rule: what it masks, the name a visitor reads in "redacted by …", and whether the field can still be searched by an exact match
/// (OPEN-4, decided: a per-rule flag, off by default, never for the built-in deny-list).
/// </summary>
/// <param name="Target">What is masked.</param>
/// <param name="Field">The header's or attribute's name; empty for the key.</param>
/// <param name="Name">The rule's name, as shown.</param>
/// <param name="Searchable">Whether <c>=</c> and <c>in</c> may search the field.</param>
/// <param name="BuiltIn">Whether this is the built-in deny-list.</param>
internal sealed record RedactionRule(RedactionTarget Target, string Field, string Name, bool Searchable, bool BuiltIn);

/// <summary>
/// The headers that are masked in every match unless the host opts one out by name (decision 10.3). They ship in M1 because M1's matches
/// are what the first page shows; they are never searchable, and <c>any</c> never reads them.
/// </summary>
internal static class HeaderDenyList
{
    /// <summary>The five names, compared ignoring case.</summary>
    public static readonly IReadOnlyList<string> Names = ["authorization", "proxy-authorization", "cookie", "set-cookie", "x-api-key"];

    /// <summary>The name a visitor reads for these rules.</summary>
    public const string RuleName = "the built-in header deny-list";

    /// <summary>Whether a header name is on the list.</summary>
    /// <param name="name">The header's name.</param>
    public static bool Contains(string name)
    {
        foreach (string listed in Names)
        {
            if (string.Equals(listed, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// The rules a topic declares, before they become a redactor. M1 builds them internally (the deny-list's opt-out, header, key and
/// CloudEvents rules with their <c>Searchable</c> flag); M2's public builder wraps this and adds nothing the engine does not already do.
/// </summary>
internal sealed class KafkaRedactionRules
{
    private readonly List<(RedactionTarget Target, string Field, string Name, bool Searchable)> rules = [];
    private readonly HashSet<string> allowed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The rules in the order they were declared.</summary>
    internal IReadOnlyList<(RedactionTarget Target, string Field, string Name, bool Searchable)> Declared => rules;

    /// <summary>The deny-listed headers the host opted out of, ignoring case.</summary>
    internal IReadOnlyCollection<string> AllowedHeaders => allowed;

    /// <summary>Masks a header's value.</summary>
    /// <param name="name">The header's name (case-sensitive, as the predicate's is).</param>
    public RuleHandle Header(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        rules.Add((RedactionTarget.Header, name, "header " + name, false));
        return new RuleHandle(this, rules.Count - 1);
    }

    /// <summary>Masks the record's key.</summary>
    public RuleHandle Key()
    {
        rules.Add((RedactionTarget.Key, string.Empty, "key", false));
        return new RuleHandle(this, rules.Count - 1);
    }

    /// <summary>Masks a CloudEvents attribute.</summary>
    /// <param name="attribute">The attribute's name.</param>
    public RuleHandle CloudEvent(string attribute)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attribute);
        rules.Add((RedactionTarget.CloudEvent, attribute, "ce." + attribute, false));
        return new RuleHandle(this, rules.Count - 1);
    }

    /// <summary>Opts a deny-listed header out of masking, by name.</summary>
    /// <param name="name">The header's name, ignoring case.</param>
    public KafkaRedactionRules AllowHeader(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        allowed.Add(name);
        return this;
    }

    /// <summary>A rule just declared, so that it can be made searchable.</summary>
    public readonly struct RuleHandle
    {
        private readonly KafkaRedactionRules owner;
        private readonly int index;

        internal RuleHandle(KafkaRedactionRules owner, int index)
        {
            this.owner = owner;
            this.index = index;
        }

        /// <summary>Lets <c>=</c> and <c>in</c> search the field. Exact matches only: <c>^=</c>, <c>~</c> and <c>any</c> would read it one character at a time.</summary>
        public KafkaRedactionRules Searchable()
        {
            (RedactionTarget target, string field, string name, _) = owner.rules[index];
            owner.rules[index] = (target, field, name, true);
            return owner;
        }
    }
}

/// <summary>
/// The rules of one topic as the match factory and the predicate compiler ask them (decision 10.2): is this field masked, and by what.
/// A redactor always exists, with at least the deny-list, so there is no match without one.
/// </summary>
internal interface IKafkaRedactor
{
    /// <summary>The rule masking a header's value, or <see langword="null" />.</summary>
    /// <param name="name">The header's name.</param>
    RedactionRule? ForHeader(string name);

    /// <summary>The rule masking the key, or <see langword="null" />.</summary>
    RedactionRule? ForKey();

    /// <summary>The rule masking a CloudEvents attribute, or <see langword="null" />.</summary>
    /// <param name="attribute">The attribute's name.</param>
    RedactionRule? ForCloudEvent(string attribute);

    /// <summary>The rule that makes a structured CloudEvent's value unsafe to keep whole (its envelope holds the masked attribute), or <see langword="null" />.</summary>
    RedactionRule? AnyCloudEventRule();
}

/// <summary>The redactor built from a topic's rules and the deny-list.</summary>
internal sealed class KafkaRedactor : IKafkaRedactor
{
    private readonly Dictionary<string, RedactionRule> headers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RedactionRule> denyListed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RedactionRule> attributes = new(StringComparer.Ordinal);
    private readonly RedactionRule? key;

    /// <summary>The redactor of a topic that declared no rules: the deny-list only.</summary>
    public static KafkaRedactor DenyListOnly { get; } = new(null);

    /// <summary>Builds a redactor.</summary>
    /// <param name="rules">What the topic declared; <see langword="null" /> for nothing.</param>
    public KafkaRedactor(KafkaRedactionRules? rules)
    {
        HashSet<string> allowed = new(rules?.AllowedHeaders ?? [], StringComparer.OrdinalIgnoreCase);

        foreach (string name in HeaderDenyList.Names)
        {
            if (!allowed.Contains(name))
            {
                denyListed[name] = new RedactionRule(RedactionTarget.Header, name, HeaderDenyList.RuleName, Searchable: false, BuiltIn: true);
            }
        }

        foreach ((RedactionTarget target, string field, string name, bool searchable) in rules?.Declared ?? [])
        {
            RedactionRule rule = new(target, field, name, searchable, BuiltIn: false);

            switch (target)
            {
                case RedactionTarget.Header:
                    headers[field] = rule;
                    break;
                case RedactionTarget.Key:
                    key = rule;
                    break;
                default:
                    attributes[field] = rule;
                    break;
            }
        }
    }

    /// <inheritdoc />
    public RedactionRule? ForHeader(string name) =>
        headers.TryGetValue(name, out RedactionRule? declared) ? declared
        : denyListed.TryGetValue(name, out RedactionRule? builtIn) ? builtIn

        // A binary-mode CloudEvent carries its attribute in a ce_ header: a rule on the attribute masks the header too.
        : name.StartsWith("ce_", StringComparison.Ordinal) && attributes.TryGetValue(name[3..], out RedactionRule? attribute) ? attribute
        : null;

    /// <inheritdoc />
    public RedactionRule? ForKey() => key;

    /// <inheritdoc />
    public RedactionRule? ForCloudEvent(string attribute) => attributes.GetValueOrDefault(attribute);

    /// <inheritdoc />
    public RedactionRule? AnyCloudEventRule() => attributes.Values.FirstOrDefault();
}
