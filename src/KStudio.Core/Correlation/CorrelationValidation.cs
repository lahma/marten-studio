using System.Globalization;
using System.Text.RegularExpressions;

using KStudio.Core.Predicates;
using KStudio.Core.Redaction;

namespace KStudio.Core.Correlation;

/// <summary>
/// The bounds of a topic's correlation declarations (D42, decision 1.2 of D-CORR), collected as problems like every other validation of
/// the engine's options. A name in a message is bounded, so a very long one cannot flood a log.
/// </summary>
internal static partial class CorrelationValidation
{
    private const int MaxDocumentTypeLength = 200;
    private const int MaxStoreLength = 100;

    /// <summary>Adds the problems with a topic's declarations to <paramref name="problems" />.</summary>
    /// <param name="problems">Where the problems go.</param>
    /// <param name="label">How the topic is named in a message: <c>Topic 'orders'</c>.</param>
    /// <param name="declared">What the topic declared; <see langword="null" /> for none.</param>
    public static void Validate(List<string> problems, string label, CorrelationDeclarations? declared)
    {
        ArgumentNullException.ThrowIfNull(problems);

        if (declared is null)
        {
            return;
        }

        string where = label + ": correlation";
        List<KafkaCorrelationField> seen = [];

        foreach (KafkaCorrelationField field in declared.Fields)
        {
            if (!ValidField(problems, where, field))
            {
                continue;
            }

            if (seen.Any(x => CorrelationDefaults.Same(x, field)))
            {
                problems.Add(where + " " + Describe(field) + " is declared twice (names are compared ignoring case).");
            }

            seen.Add(field);
        }

        int total = CorrelationDefaults.Resolve(declared).Count;

        if (total > CorrelationDefaults.MaxFields)
        {
            problems.Add(where + " has " + total.ToString(CultureInfo.InvariantCulture) + " fields, the defaults included; the most is " + CorrelationDefaults.MaxFields.ToString(CultureInfo.InvariantCulture) + ". Call WithoutDefaults() to drop the defaults.");
        }

        foreach (CorrelationField resolved in CorrelationDefaults.Resolve(declared))
        {
            if (resolved.Kind == CorrelationField.TraceKind && resolved.Source != CorrelationSource.Header)
            {
                problems.Add(where + " " + Display(resolved.Grammar) + ": the kind 'trace' is for headers that carry a W3C trace context.");
            }
        }
    }

    private static bool ValidField(List<string> problems, string where, KafkaCorrelationField field)
    {
        int before = problems.Count;
        string name = Describe(field);

        switch (field.Source)
        {
            case CorrelationSource.Header:
                if (!HeaderName().IsMatch(field.Name))
                {
                    problems.Add(where + " " + name + ": a header name is 1 to 256 letters, digits, '.', '_' and '-'.");
                }
                else if (HeaderDenyList.Contains(field.Name))
                {
                    // Its value is masked in every match and never searched: it can be no key, and a field on it would only look as if it were.
                    problems.Add(where + " " + name + " is on the header deny-list and is never a correlation field.");
                }

                break;

            case CorrelationSource.CloudEvent:
                if (!AttributeName().IsMatch(field.Name))
                {
                    problems.Add(where + " " + name + ": a CloudEvents attribute name is 1 to 20 lower-case letters and digits.");
                }

                break;

            case CorrelationSource.JsonPath:
                if (!JsonPathExpression.TryParse(field.Name, 0, out _, out _))
                {
                    problems.Add(where + " " + name + ": not a JSON path the search can read ($.name, $.name[0], $.name[*], at most " + JsonPathExpression.MaxSteps.ToString(CultureInfo.InvariantCulture) + " steps).");
                }

                break;
        }

        if (field.KindName is { } kind && !ValidKind(kind))
        {
            problems.Add(where + " " + name + ": a kind is a lower-case letter then up to 39 lower-case letters, digits and '-', or 'document:' and a document type.");
        }

        if (field.DocumentType is { } document && !ValidDocumentType(document))
        {
            problems.Add(where + " " + name + ": a document type is 1 to " + MaxDocumentTypeLength.ToString(CultureInfo.InvariantCulture) + " characters with no control character.");
        }

        if (field.StoreName is { } store && (store.Length is < 1 or > MaxStoreLength))
        {
            problems.Add(where + " " + name + ": a store is 1 to " + MaxStoreLength.ToString(CultureInfo.InvariantCulture) + " characters.");
        }

        if (field.DocumentType is not null && field.IsStream)
        {
            problems.Add(where + " " + name + ": Document and Stream cannot both be called on one field.");
        }

        return problems.Count == before;
    }

    private static bool ValidKind(string kind)
    {
        if (kind.StartsWith(CorrelationDefaults.DocumentPrefix, StringComparison.Ordinal))
        {
            return ValidDocumentType(kind[CorrelationDefaults.DocumentPrefix.Length..]);
        }

        return Kind().IsMatch(kind);
    }

    private static bool ValidDocumentType(string type) => type.Length is >= 1 and <= MaxDocumentTypeLength && !type.Any(char.IsControl);

    private static string Describe(KafkaCorrelationField field) =>
        field.Source switch
        {
            CorrelationSource.Header => "header '" + Display(field.Name) + "'",
            CorrelationSource.CloudEvent => "CloudEvents attribute '" + Display(field.Name) + "'",
            CorrelationSource.Key => "the key",
            _ => "path '" + Display(field.Name) + "'",
        };

    private static string Display(string? value) =>
        value is null ? string.Empty : value.Length > 64 ? value[..64] + "…" : value;

    [GeneratedRegex(@"\A[A-Za-z0-9_.-]{1,256}\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex HeaderName();

    [GeneratedRegex(@"\A[a-z0-9]{1,20}\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex AttributeName();

    [GeneratedRegex(@"\A[a-z][a-z0-9-]{0,39}\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Kind();
}
