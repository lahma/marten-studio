using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;

using KStudio.Core.Predicates;

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

    /// <summary>A leaf or subtree of the value (a CloudEvent's data), by JSON path (decision 3 of D-PAYLOAD).</summary>
    JsonPath,

    /// <summary>Every match of a regular expression in the value's text; never searchable.</summary>
    Pattern,
}

/// <summary>
/// One rule: what it masks, the name a visitor reads in "redacted by …", and whether the field can still be searched by an exact match
/// (OPEN-4, decided: a per-rule flag, off by default, never for the built-in deny-list).
/// </summary>
/// <param name="Target">What is masked.</param>
/// <param name="Field">The header's or attribute's name; the path or the expression as declared for a payload rule; empty for the key.</param>
/// <param name="Name">The rule's name, as shown: <c>$.customer.email</c>, <c>pattern phone</c>.</param>
/// <param name="Searchable">Whether <c>=</c> and <c>in</c> may search the field.</param>
/// <param name="BuiltIn">Whether this is the built-in deny-list.</param>
/// <param name="Path">The parsed path of a <see cref="RedactionTarget.JsonPath" /> rule.</param>
/// <param name="Pattern">The compiled expression of a <see cref="RedactionTarget.Pattern" /> rule.</param>
internal sealed record RedactionRule(
    RedactionTarget Target,
    string Field,
    string Name,
    bool Searchable,
    bool BuiltIn,
    JsonPathExpression? Path = null,
    Regex? Pattern = null)
{
    // A payload rule's field is the host's expression and its compiled form: neither is printed.
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("Target = ").Append(Target).Append(", Name = ").Append(Name);
        return true;
    }
}

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

/// <summary>Why a pattern cannot be a redaction rule (decision 3.1 of D-PAYLOAD).</summary>
internal enum PayloadPatternProblem
{
    /// <summary>It can.</summary>
    None,

    /// <summary>It is longer than <see cref="PayloadPatterns.MaxLength" /> characters.</summary>
    TooLong,

    /// <summary>.NET cannot read it as a regular expression.</summary>
    NotARegularExpression,

    /// <summary>It uses a construct that cannot run without backtracking: backreferences, lookarounds, atomic groups, conditionals.</summary>
    NeedsBacktracking,

    /// <summary>It matches an empty text, so it would mask nothing and run on every value.</summary>
    MatchesEmpty,
}

/// <summary>
/// The patterns of redaction rules, compiled once for the process: each runs without backtracking (so a hostile value cannot make it
/// run for ever) and under a timeout, and the compiled expression is immutable and shared by every scan.
/// </summary>
internal static class PayloadPatterns
{
    /// <summary>The longest pattern, in characters.</summary>
    public const int MaxLength = 500;

    /// <summary>The longest one call of a pattern may run.</summary>
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    private static readonly ConcurrentDictionary<string, Regex> Compiled = new(StringComparer.Ordinal);

    /// <summary>Compiles a pattern, or says why it cannot be one.</summary>
    /// <param name="pattern">The expression.</param>
    /// <param name="regex">The compiled expression, when there is no problem.</param>
    public static PayloadPatternProblem TryCompile(string pattern, out Regex? regex)
    {
        regex = null;

        if (pattern.Length > MaxLength)
        {
            return PayloadPatternProblem.TooLong;
        }

        if (Compiled.TryGetValue(pattern, out Regex? known))
        {
            regex = known;
            return PayloadPatternProblem.None;
        }

        Regex made;

        try
        {
            made = new Regex(pattern, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, MatchTimeout);
        }
        catch (NotSupportedException)
        {
            return PayloadPatternProblem.NeedsBacktracking;
        }
        catch (ArgumentException)
        {
            return PayloadPatternProblem.NotARegularExpression;
        }

        try
        {
            if (made.IsMatch(string.Empty))
            {
                return PayloadPatternProblem.MatchesEmpty;
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return PayloadPatternProblem.NotARegularExpression;
        }

        regex = Compiled.GetOrAdd(pattern, made);
        return PayloadPatternProblem.None;
    }
}

/// <summary>
/// The rules a topic declares, before they become a redactor. M1 builds them internally (the deny-list's opt-out, header, key and
/// CloudEvents rules with their <c>Searchable</c> flag); M2's public builder wraps this and M3 adds the payload rules.
/// </summary>
internal sealed class KafkaRedactionRules
{
    private readonly List<(RedactionTarget Target, string Field, string Name, bool Searchable)> rules = [];
    private readonly HashSet<string> allowed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A copy that later declarations on this object do not reach: the registry's snapshot of what validation approved.</summary>
    internal KafkaRedactionRules Copy()
    {
        KafkaRedactionRules copy = new();
        copy.rules.AddRange(rules);

        foreach (string name in allowed)
        {
            copy.allowed.Add(name);
        }

        return copy;
    }

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

    /// <summary>Masks a leaf or subtree of the value, by JSON path.</summary>
    /// <param name="path">The path as the host wrote it; validation parses it.</param>
    public RuleHandle JsonPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // The rule is named by its canonical path; one that does not parse keeps what was written, and validation refuses it.
        string name = JsonPathExpression.TryParse(path, 0, out JsonPathExpression? parsed, out _) ? parsed!.Text : path;
        rules.Add((RedactionTarget.JsonPath, path, name, false));
        return new RuleHandle(this, rules.Count - 1);
    }

    /// <summary>Masks every match of a regular expression in the value's text.</summary>
    /// <param name="regex">The expression.</param>
    /// <param name="name">What a visitor reads in "redacted by pattern {name}"; the pattern's place among the topic's patterns when <see langword="null" />.</param>
    public void Pattern(string regex, string? name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(regex);

        int ordinal = rules.Count(static x => x.Target == RedactionTarget.Pattern) + 1;
        rules.Add((RedactionTarget.Pattern, regex, "pattern " + (name ?? ordinal.ToString(CultureInfo.InvariantCulture)), false));
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

    /// <summary>The JSON-path and pattern rules, in the order they were declared.</summary>
    IReadOnlyList<RedactionRule> PayloadRules { get; }

    /// <summary>Whether the topic declared a JSON-path or a pattern rule: its values go through <c>PayloadRedactor</c>.</summary>
    bool HasPayloadRules { get; }

    /// <summary>
    /// The rule that covers every node a clause's path can select, or <see langword="null" />. Where several cover it, a rule that cannot
    /// be searched comes first: a clause on a field any rule hides for good must not be searchable because another rule allows it.
    /// </summary>
    /// <param name="clause">The clause's path.</param>
    RedactionRule? ForJsonPath(JsonPathExpression clause);
}

/// <summary>
/// The redactor built from a topic's rules and the deny-list. Redaction fails closed, so a rule's name is compared ignoring case (a rule on
/// <c>Customer-Email</c> masks <c>customer-email</c> too), and the two ways a CloudEvent travels are one field: the binary mode of the
/// CloudEvents Kafka binding carries attribute <c>a</c> as header <c>ce_a</c> and <c>datacontenttype</c> as <c>content-type</c>, and a
/// rule on either spelling masks both. The predicate's <c>header.&lt;name&gt;</c> keeps matching the record's header names case-sensitively.
/// </summary>
internal sealed class KafkaRedactor : IKafkaRedactor
{
    private const string BinaryPrefix = "ce_";
    private const string ContentTypeHeader = "content-type";
    private const string ContentTypeAttribute = "datacontenttype";

    private readonly Dictionary<string, RedactionRule> headers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RedactionRule> denyListed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RedactionRule> attributes = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<RedactionRule> payload = [];
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
            // A deny-listed header is never searchable, whatever was declared (validation refuses the declaration; this keeps the rule even
            // where validation did not run).
            bool canSearch = searchable && !(target == RedactionTarget.Header && HeaderDenyList.Contains(field));
            RedactionRule rule = new(target, field, name, canSearch, BuiltIn: false);

            switch (target)
            {
                case RedactionTarget.Header:
                    headers[field] = rule;
                    break;
                case RedactionTarget.Key:
                    key = rule;
                    break;
                case RedactionTarget.JsonPath:
                    // A path that does not parse is a declaration validation refuses; without validation it masks nothing it could name.
                    if (JsonPathExpression.TryParse(field, 0, out JsonPathExpression? path, out _))
                    {
                        payload.Add(rule with { Path = path });
                    }

                    break;
                case RedactionTarget.Pattern:
                    if (PayloadPatterns.TryCompile(field, out Regex? regex) == PayloadPatternProblem.None)
                    {
                        payload.Add(rule with { Pattern = regex, Searchable = false });
                    }

                    break;
                default:
                    attributes[field] = rule;
                    break;
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<RedactionRule> PayloadRules => payload;

    /// <inheritdoc />
    public bool HasPayloadRules => payload.Count > 0;

    /// <inheritdoc />
    public RedactionRule? ForHeader(string name)
    {
        if (headers.TryGetValue(name, out RedactionRule? declared))
        {
            return declared;
        }

        if (denyListed.TryGetValue(name, out RedactionRule? builtIn))
        {
            return builtIn;
        }

        // A binary-mode CloudEvent carries its attribute in a ce_ header, and its content type in content-type: a rule on the attribute
        // masks the header too.
        if (name.StartsWith(BinaryPrefix, StringComparison.OrdinalIgnoreCase) && attributes.TryGetValue(name[BinaryPrefix.Length..], out RedactionRule? attribute))
        {
            return attribute;
        }

        return string.Equals(name, ContentTypeHeader, StringComparison.OrdinalIgnoreCase) && attributes.TryGetValue(ContentTypeAttribute, out RedactionRule? contentType)
            ? contentType
            : null;
    }

    /// <inheritdoc />
    public RedactionRule? ForKey() => key;

    /// <inheritdoc />
    public RedactionRule? ForCloudEvent(string attribute)
    {
        if (attributes.TryGetValue(attribute, out RedactionRule? declared))
        {
            return declared;
        }

        // The other direction: a header rule on the binary-mode carrier of this attribute masks the attribute as well.
        if (headers.TryGetValue(BinaryPrefix + attribute, out RedactionRule? carrier))
        {
            return carrier;
        }

        return string.Equals(attribute, ContentTypeAttribute, StringComparison.OrdinalIgnoreCase) && headers.TryGetValue(ContentTypeHeader, out RedactionRule? contentType)
            ? contentType
            : null;
    }

    /// <inheritdoc />
    public RedactionRule? ForJsonPath(JsonPathExpression clause)
    {
        ArgumentNullException.ThrowIfNull(clause);
        RedactionRule? first = null;

        foreach (RedactionRule rule in payload)
        {
            if (rule.Path is null || !rule.Path.CoversClause(clause))
            {
                continue;
            }

            if (!rule.Searchable)
            {
                return rule;
            }

            first ??= rule;
        }

        return first;
    }
}
