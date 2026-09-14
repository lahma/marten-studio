using System.Reflection;
using System.Text.Json;

namespace MartenStudio.Services.Documents;

/// <summary>
/// Finds the JSON property behind a duplicated column, so the metadata pane can put the column's value
/// beside the document's and say whether they still agree.
/// </summary>
/// <remarks>
/// <para>
/// This exists because Marten reports a nested duplicated field's member name with the names run
/// together: <c>Duplicate(x =&gt; x.Address.City)</c> gives <c>MemberName = "AddressCity"</c> and
/// <c>ColumnName = "address_city"</c>, with no separator anywhere (Appendix B addendum, verified against
/// Marten 9.35). <c>"AddressCity"</c> alone is ambiguous — it could be one property or two — so the split
/// is resolved against the CLR type rather than guessed from the string: walk the type, take the longest
/// property name that is a prefix of what is left, and recurse.
/// </para>
/// <para>
/// When the type is not known (a discovered table) or no split fits, the answer is
/// <see langword="null"/> and the pane says <see cref="AgreementState.Unknown"/> rather than claiming a
/// disagreement it cannot substantiate.
/// </para>
/// </remarks>
internal static class DuplicatedFieldPaths
{
    private const int MaxDepth = 8;

    /// <summary>
    /// The JSON path for a duplicated field's member name, with each segment already put through the
    /// serializer's naming policy.
    /// </summary>
    public static IReadOnlyList<string>? Resolve(Type? documentType, string memberName, JsonNamingPolicy? namingPolicy)
    {
        if (documentType is null || string.IsNullOrEmpty(memberName))
        {
            return null;
        }

        List<string> path = [];

        return Walk(documentType, memberName, namingPolicy, path, 0) ? path : null;
    }

    private static bool Walk(Type type, string remaining, JsonNamingPolicy? namingPolicy, List<string> path, int depth)
    {
        if (remaining.Length == 0)
        {
            return path.Count > 0;
        }

        if (depth >= MaxDepth)
        {
            return false;
        }

        MemberInfo? best = null;
        var bestLength = 0;

        foreach (var member in Members(type))
        {
            var name = member.Name;

            if (name.Length > bestLength &&
                remaining.StartsWith(name, StringComparison.Ordinal))
            {
                best = member;
                bestLength = name.Length;
            }
        }

        if (best is null)
        {
            return false;
        }

        path.Add(namingPolicy?.ConvertName(best.Name) ?? best.Name);

        return Walk(MemberType(best), remaining[bestLength..], namingPolicy, path, depth + 1);
    }

    private static IEnumerable<MemberInfo> Members(Type type)
    {
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length == 0)
            {
                yield return property;
            }
        }

        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            yield return field;
        }
    }

    private static Type MemberType(MemberInfo member) => member switch
    {
        PropertyInfo property => property.PropertyType,
        FieldInfo field => field.FieldType,
        _ => typeof(object),
    };

    /// <summary>
    /// The value at a JSON path, as text, or <see langword="null"/> when the path is not there.
    /// </summary>
    /// <remarks>
    /// Comparison is by text because that is what the column holds: the whole question is whether the
    /// duplicated column and the document still say the same thing, and both sides of that comparison are
    /// rendered the same way before they are compared.
    /// </remarks>
    public static string? ReadValue(string? json, IReadOnlyList<string>? path)
    {
        if (string.IsNullOrEmpty(json) || path is null || path.Count == 0)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });

            var element = document.RootElement;

            foreach (var segment in path)
            {
                if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(segment, out element))
                {
                    return null;
                }
            }

            return element.ValueKind switch
            {
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                JsonValueKind.String => element.GetString(),
                _ => element.GetRawText(),
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether a duplicated column and the JSON behind it agree, tolerant of the ways Postgres and
    /// <c>System.Text.Json</c> render the same value differently.
    /// </summary>
    public static AgreementState Compare(string? columnValue, string? jsonValue, bool pathResolved)
    {
        if (!pathResolved)
        {
            return AgreementState.Unknown;
        }

        if (columnValue is null && jsonValue is null)
        {
            return AgreementState.Agrees;
        }

        if (columnValue is null || jsonValue is null)
        {
            return AgreementState.Differs;
        }

        if (string.Equals(columnValue, jsonValue, StringComparison.Ordinal))
        {
            return AgreementState.Agrees;
        }

        // `1000.00` from a numeric column and `1000` from the JSON are the same number; `True` from
        // Npgsql and `true` from the document are the same boolean. Neither is a drift worth a red dot.
        if (decimal.TryParse(columnValue, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var left) &&
            decimal.TryParse(jsonValue, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var right))
        {
            return left == right ? AgreementState.Agrees : AgreementState.Differs;
        }

        return string.Equals(columnValue, jsonValue, StringComparison.OrdinalIgnoreCase)
            ? AgreementState.Agrees
            : AgreementState.Differs;
    }
}
