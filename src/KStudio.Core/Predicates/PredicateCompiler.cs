using System.Globalization;

using KStudio.Core.Decoding;
using KStudio.Core.Redaction;

namespace KStudio.Core.Predicates;

/// <summary>What a predicate is compiled against: one topic (key formats and redaction differ per topic) and one grant.</summary>
/// <param name="Topic">The topic.</param>
/// <param name="KeyFormat">How its keys are written.</param>
/// <param name="KeyFormatDeclared">Whether the host declared the format; a verdict says "assumed" when it did not.</param>
/// <param name="Redactor">Its redaction rules, deny-list included.</param>
/// <param name="Grant">What the visitor may read.</param>
/// <param name="DeclaresCloudEvents">Whether the topic declared <c>KafkaDecoders.CloudEvents</c> (every value is a structured event).</param>
internal sealed record PredicateCompileContext(
    string Topic,
    KafkaKeyFormat KeyFormat,
    bool KeyFormatDeclared,
    IKafkaRedactor Redactor,
    KafkaReadGrant Grant,
    bool DeclaresCloudEvents);

/// <summary>The outcome of compiling for one topic.</summary>
/// <param name="Predicate">The compiled predicate; <see langword="null" /> when there are errors.</param>
/// <param name="Errors">What is wrong with the text on this topic. No message contains a literal.</param>
internal sealed record PredicateCompilation(CompiledPredicate? Predicate, IReadOnlyList<PredicateError> Errors);

/// <summary>
/// Compiles a parsed search for one topic (decisions 7.4, 7.5, 10.5 and 10.6): key literals become byte forms through the topic's key
/// format, redaction decides which operators a field accepts, the clauses are ordered cheapest first, and each clause gets a verdict that
/// is shown before anything is read.
/// </summary>
internal static class PredicateCompiler
{
    /// <summary>Compiles <paramref name="tree" /> (null matches every record).</summary>
    /// <param name="tree">The parsed search.</param>
    /// <param name="context">The topic and the grant.</param>
    public static PredicateCompilation Compile(PredicateNode? tree, PredicateCompileContext context)
    {
        List<PredicateError> errors = [];
        List<ClauseVerdict> verdicts = [];
        CompiledNode? root = tree is null ? null : Build(tree, context, errors, verdicts);

        string audit = PredicateFormatter.Format(tree, clause => IsRedacted(clause.Field, context.Redactor));

        return errors.Count > 0
            ? new PredicateCompilation(null, errors)
            : new PredicateCompilation(new CompiledPredicate(context.Topic, root, verdicts, audit), []);
    }

    /// <summary>Whether a field is masked on a topic, which hides the literals of its clauses from every sink.</summary>
    /// <param name="field">The field.</param>
    /// <param name="redactor">The topic's rules.</param>
    public static bool IsRedacted(FieldNode field, IKafkaRedactor redactor) => RuleOf(field, redactor) is not null;

    private static RedactionRule? RuleOf(FieldNode field, IKafkaRedactor redactor) =>
        field switch
        {
            KeyField => redactor.ForKey(),
            HeaderField header => redactor.ForHeader(header.Name),
            CloudEventField ce => redactor.ForCloudEvent(ce.Attribute),
            JsonPathField path => redactor.ForJsonPath(path.Path),
            _ => null,
        };

    private static CompiledNode? Build(PredicateNode node, PredicateCompileContext context, List<PredicateError> errors, List<ClauseVerdict> verdicts)
    {
        switch (node)
        {
            case AndNode and:
                return Combine(and.Children, context, errors, verdicts, static c => new CompiledAnd(c));
            case OrNode or:
                return Combine(or.Children, context, errors, verdicts, static c => new CompiledOr(c));
            case NotNode not:
                CompiledNode? operand = Build(not.Operand, context, errors, verdicts);
                return operand is null ? null : new CompiledNot(operand);
            case ClauseNode clause:
                return BuildClause(clause, context, errors, verdicts);
            default:
                return null;
        }
    }

    private static CompiledNode? Combine(
        IReadOnlyList<PredicateNode> children,
        PredicateCompileContext context,
        List<PredicateError> errors,
        List<ClauseVerdict> verdicts,
        Func<IReadOnlyList<CompiledNode>, CompiledNode> make)
    {
        List<CompiledNode> built = [];

        foreach (PredicateNode child in children)
        {
            // Every clause is compiled, even after one failed, so that a visitor sees all of the problems at once.
            if (Build(child, context, errors, verdicts) is { } compiled)
            {
                built.Add(compiled);
            }
        }

        return built.Count == children.Count ? make(built) : null;
    }

    private static CompiledNode? BuildClause(ClauseNode clause, PredicateCompileContext context, List<PredicateError> errors, List<ClauseVerdict> verdicts)
    {
        string field = PredicateFormatter.Field(clause.Field);
        int position = clause.Field.Start + 1;
        bool payload = context.Grant.HasFlag(KafkaReadGrant.Payload);
        bool isNull = clause.Operator is PredicateOperator.IsNull or PredicateOperator.IsNotNull;

        // `value is null` looks at the record and not at its content (a tombstone has no value), so it needs no payload grant.
        if (clause.Field is ValueField && isNull)
        {
            AddVerdict(verdicts, context, clause, field);
            return new CompiledValueNullClause(clause.Operator);
        }

        // The value and a JSON path need the payload grant, which KStudio decides before it compiles; an internal caller with the wrong
        // grant still gets a refusal and not a silent false.
        if (clause.Field is ValueField or JsonPathField && !payload)
        {
            errors.Add(new PredicateError(
                clause.Field.Start,
                clause.Field.Length,
                $"{field} reads the record's value, which needs ReadPayloads (KafkaStudioOptions.Capabilities.ReadPayloads)."));
            return null;
        }

        if (clause.Operator is PredicateOperator.Less or PredicateOperator.LessOrEqual or PredicateOperator.Greater or PredicateOperator.GreaterOrEqual
            && clause.Field is not JsonPathField)
        {
            errors.Add(Refuse(clause, field, position, "numbers compare only JSON paths"));
            return null;
        }

        if (clause.Field is AnyField && isNull)
        {
            errors.Add(Refuse(clause, field, position, "is null applies to key, header.<name>, ce.<attribute> and value"));
            return null;
        }

        // Redaction first: the operators a masked field accepts are fixed before anything else is asked of its literals.
        RedactionRule? rule = RuleOf(clause.Field, context.Redactor);

        if (rule is not null)
        {
            bool exact = clause.Operator is PredicateOperator.Equals or PredicateOperator.In;

            if (!(rule.Searchable && exact))
            {
                errors.Add(new PredicateError(
                    clause.Field.Start,
                    clause.Field.Length,
                    rule.Searchable
                        ? string.Create(CultureInfo.InvariantCulture, $"{field} is redacted by {rule.Name} (position {position}): only = and in can search it.")
                        : string.Create(CultureInfo.InvariantCulture, $"{field} is redacted by {rule.Name} (position {position}) and cannot be searched.")));
                return null;
            }
        }

        if (clause.Field is JsonPathField path)
        {
            return BuildPath(clause, path, field, position, rule is not null, context, errors, verdicts);
        }

        if (isNull)
        {
            AddVerdict(verdicts, context, clause, field);
            return clause.Field switch
            {
                KeyField => new CompiledKeyClause(clause.Operator, [], []),
                HeaderField header => new CompiledHeaderClause(header.Name, clause.Operator, [], []),
                CloudEventField ce => new CompiledCloudEventClause(ce.Attribute, clause.Operator, [], context.DeclaresCloudEvents),
                _ => null,
            };
        }

        // The literals must be quoted text on these fields.
        foreach (LiteralNode literal in clause.Literals)
        {
            if (literal is not StringLiteral)
            {
                errors.Add(new PredicateError(
                    literal.Start,
                    literal.Length,
                    string.Create(CultureInfo.InvariantCulture, $"Put the value at position {literal.Start + 1} in quotes: {field} compares as text.")));
                return null;
            }
        }

        string[] texts = [.. clause.Literals.Cast<StringLiteral>().Select(static x => x.Value)];

        CompiledNode? built = clause.Field switch
        {
            KeyField => BuildKey(clause, field, position, texts, context, errors),
            HeaderField header => new CompiledHeaderClause(
                header.Name,
                clause.Operator,
                clause.Operator == PredicateOperator.Contains ? [] : [.. texts.Select(TextMatch.Utf8)],
                texts),
            CloudEventField ce => new CompiledCloudEventClause(ce.Attribute, clause.Operator, texts, context.DeclaresCloudEvents),
            AnyField => new CompiledAnyClause(clause.Operator, texts, context.Redactor, payload),
            ValueField => new CompiledValueClause(clause.Operator, texts, context.Redactor),
            _ => null,
        };

        if (built is not null)
        {
            AddVerdict(verdicts, context, clause, field);
        }

        return built;
    }

    private static CompiledJsonPathClause? BuildPath(
        ClauseNode clause,
        JsonPathField path,
        string field,
        int position,
        bool leafRule,
        PredicateCompileContext context,
        List<PredicateError> errors,
        List<ClauseVerdict> verdicts)
    {
        if (clause.Operator is PredicateOperator.IsNull or PredicateOperator.IsNotNull)
        {
            errors.Add(Refuse(clause, field, position, string.Create(CultureInfo.InvariantCulture, $"write {field} = null for a JSON null")));
            return null;
        }

        bool compares = clause.Operator is PredicateOperator.Less or PredicateOperator.LessOrEqual or PredicateOperator.Greater or PredicateOperator.GreaterOrEqual;
        bool text = clause.Operator is PredicateOperator.Prefix or PredicateOperator.Contains;

        foreach (LiteralNode literal in clause.Literals)
        {
            if (compares && literal is not NumberLiteral)
            {
                errors.Add(Refuse(
                    clause,
                    field,
                    position,
                    string.Create(CultureInfo.InvariantCulture, $"it compares numbers, and the value at position {literal.Start + 1} is not one")));
                return null;
            }

            if (text && literal is not StringLiteral)
            {
                errors.Add(Refuse(
                    clause,
                    field,
                    position,
                    string.Create(CultureInfo.InvariantCulture, $"it compares text, and the value at position {literal.Start + 1} is not a string")));
                return null;
            }
        }

        AddVerdict(verdicts, context, clause, field, leafRule);
        return new CompiledJsonPathClause(path.Path, clause.Operator, clause.Literals, leafRule, context.Redactor);
    }

    private static CompiledKeyClause? BuildKey(ClauseNode clause, string field, int position, string[] texts, PredicateCompileContext context, List<PredicateError> errors)
    {
        KafkaKeyFormat format = context.KeyFormat;

        if (clause.Operator == PredicateOperator.Contains)
        {
            return new CompiledKeyClause(clause.Operator, [], texts);
        }

        bool prefix = clause.Operator == PredicateOperator.Prefix;

        if (prefix && !format.SupportsPrefix)
        {
            // The format words its own reason.
            _ = format.TryEncodePrefix(texts[0], out _, out string? reason);
            errors.Add(Refuse(clause, field, position, reason ?? "a prefix needs a text key format"));
            return null;
        }

        List<byte[]> forms = [];

        foreach (StringLiteral literal in clause.Literals.Cast<StringLiteral>())
        {
            bool ok = prefix
                ? format.TryEncodePrefix(literal.Value, out IReadOnlyList<byte[]> encoded, out string? problem)
                : format.TryEncode(literal.Value, out encoded, out problem);

            if (!ok)
            {
                errors.Add(new PredicateError(
                    literal.Start,
                    literal.Length,
                    string.Create(CultureInfo.InvariantCulture, $"The value at position {literal.Start + 1} is not a valid {format.Name} key: {problem}.")));
                return null;
            }

            forms.AddRange(encoded);
        }

        return new CompiledKeyClause(clause.Operator, [.. forms], texts, ReferenceEquals(format, KafkaKeyFormat.JsonString));
    }

    private static PredicateError Refuse(ClauseNode clause, string field, int position, string reason) =>
        new(
            clause.Start,
            clause.Length,
            string.Create(
                CultureInfo.InvariantCulture,
                $"'{PredicateFormatter.OperatorText(clause.Operator)}' cannot be used on {field} (position {position}): {reason}."));

    private static void AddVerdict(List<ClauseVerdict> verdicts, PredicateCompileContext context, ClauseNode clause, string field, bool leafRule = false)
    {
        string assumed = context.KeyFormatDeclared ? string.Empty : ", assumed";
        string rules = context.Redactor.HasPayloadRules ? "; the topic's redaction rules run on each value" : string.Empty;
        string events = context.DeclaresCloudEvents ? "each event's data" : "each value";

        (ClauseVerdictKind kind, string text) = clause.Field switch
        {
            KeyField when clause.Operator == PredicateOperator.Contains =>
                (ClauseVerdictKind.SlowScan, $"slow: each key is formatted as text ({context.KeyFormat.Name}{assumed})"),
            KeyField when ReferenceEquals(context.KeyFormat, KafkaKeyFormat.JsonString) && clause.Operator is not (PredicateOperator.IsNull or PredicateOperator.IsNotNull) =>
                (ClauseVerdictKind.Scan, $"compared as bytes; a key with escapes is decoded ({context.KeyFormat.Name}{assumed})"),
            KeyField => (ClauseVerdictKind.Scan, $"compared as bytes ({context.KeyFormat.Name}{assumed})"),
            HeaderField when clause.Operator == PredicateOperator.Contains =>
                (ClauseVerdictKind.SlowScan, "slow: each header value is decoded as text"),
            HeaderField => (ClauseVerdictKind.Scan, "compared as bytes"),
            CloudEventField when context.DeclaresCloudEvents =>
                (ClauseVerdictKind.SlowScan, "slow: each value is parsed as a structured CloudEvent"),
            CloudEventField =>
                (ClauseVerdictKind.Scan, "read from the ce_ headers; a structured event's value is parsed (slow) only on a record that carries one"),
            ValueField when clause.Operator is PredicateOperator.IsNull or PredicateOperator.IsNotNull =>
                (ClauseVerdictKind.Scan, "read from the record: a tombstone has no value, and nothing is decoded"),
            ValueField => (ClauseVerdictKind.SlowScan, $"slow: {events} is decoded as text{rules}"),
            JsonPathField when leafRule =>
                (ClauseVerdictKind.SlowScan, "slow: each value is parsed as JSON; the masked leaf is compared exactly, and never shown"),
            JsonPathField => (ClauseVerdictKind.SlowScan, $"slow: {events} is parsed as JSON{rules}"),
            AnyField when context.Grant.HasFlag(KafkaReadGrant.Payload) =>
                (ClauseVerdictKind.SlowScan, $"slow: it reads the key, every header, every CloudEvents attribute and every text in the value{rules}"),
            _ => (ClauseVerdictKind.SlowScan, "slow: it reads the key, every header and every CloudEvents attribute as text"),
        };

        verdicts.Add(new ClauseVerdict(context.Topic, field, kind, text, clause.Operator));
    }
}
