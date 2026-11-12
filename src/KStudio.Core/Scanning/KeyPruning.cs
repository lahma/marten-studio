using KStudio.Core.Predicates;

namespace KStudio.Core.Scanning;

/// <summary>
/// What a search says about its key, for partition pruning (AGENTS.md D39, D-PAYLOAD decision 7.3): whether the predicate fixes the key to
/// a few literals, and whether those literals' byte forms are complete for the topic's key format.
/// </summary>
internal static class KeyPruning
{
    /// <summary>The characters a JSON-string key literal may hold for its byte forms to be known: other characters are escaped differently by different serializers.</summary>
    private const string JsonSafePunctuation = " -_.:@";

    /// <summary>
    /// The literals the predicate fixes the key to, or <see langword="null" /> when it does not: a key <c>=</c> or <c>in</c> clause gives its
    /// literals, an <c>and</c> gives the smallest set among its children that have one, an <c>or</c> gives the union only when every child has
    /// one, and <c>not</c> and every other clause give none.
    /// </summary>
    /// <param name="node">The parsed search; <see langword="null" /> for an empty text.</param>
    public static IReadOnlyList<string>? KeySet(PredicateNode? node)
    {
        switch (node)
        {
            case ClauseNode { Field: KeyField, Operator: PredicateOperator.Equals or PredicateOperator.In } clause:
                List<string> literals = [];

                foreach (LiteralNode literal in clause.Literals)
                {
                    // A key clause compiles only with text literals; anything else is a compile error before pruning is asked.
                    if (literal is not StringLiteral text)
                    {
                        return null;
                    }

                    if (!literals.Contains(text.Value, StringComparer.Ordinal))
                    {
                        literals.Add(text.Value);
                    }
                }

                return literals.Count == 0 ? null : literals;

            case AndNode and:
                IReadOnlyList<string>? smallest = null;

                foreach (PredicateNode child in and.Children)
                {
                    if (KeySet(child) is { } set && (smallest is null || set.Count < smallest.Count))
                    {
                        smallest = set;
                    }
                }

                return smallest;

            case OrNode or:
                List<string> union = [];

                foreach (PredicateNode child in or.Children)
                {
                    if (KeySet(child) is not { } set)
                    {
                        return null;
                    }

                    foreach (string literal in set)
                    {
                        if (!union.Contains(literal, StringComparer.Ordinal))
                        {
                            union.Add(literal);
                        }
                    }
                }

                return union.Count == 0 ? null : union;

            default:
                return null;
        }
    }

    /// <summary>
    /// Whether the byte forms of <paramref name="literals" /> are every form a producer could have written, for <paramref name="format" />:
    /// the built-in formats write exactly the forms they return, except a JSON-string key, whose escapes differ between serializers unless
    /// every character is a letter, a digit, a space or one of <c>-_.:@</c>. A host's format claims its own forms.
    /// </summary>
    /// <param name="format">The topic's key format.</param>
    /// <param name="literals">The key set.</param>
    public static bool FormsAreComplete(KafkaKeyFormat format, IReadOnlyList<string> literals)
    {
        if (!ReferenceEquals(format, KafkaKeyFormat.JsonString))
        {
            return true;
        }

        foreach (string literal in literals)
        {
            foreach (char c in literal)
            {
                bool safe = c < 128 && (char.IsAsciiLetterOrDigit(c) || JsonSafePunctuation.Contains(c, StringComparison.Ordinal));

                if (!safe)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>The distinct byte forms of the key set through the topic's format; <see langword="null" /> when a literal is not valid for it.</summary>
    /// <param name="format">The topic's key format.</param>
    /// <param name="literals">The key set.</param>
    public static IReadOnlyList<byte[]>? Forms(KafkaKeyFormat format, IReadOnlyList<string> literals)
    {
        List<byte[]> forms = [];

        foreach (string literal in literals)
        {
            if (!format.TryEncode(literal, out IReadOnlyList<byte[]> encoded, out _))
            {
                return null;
            }

            foreach (byte[] form in encoded)
            {
                if (!forms.Any(x => x.AsSpan().SequenceEqual(form)))
                {
                    forms.Add(form);
                }
            }
        }

        return forms;
    }
}
