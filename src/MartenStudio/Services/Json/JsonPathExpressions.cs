using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MartenStudio.Services.Json;

/// <summary>
/// One step of a path into a JSON document: either an object member or an array element.
/// </summary>
/// <param name="Key">The member name, or <see langword="null"/> when this step is an array index.</param>
/// <param name="Index">The array index, or <c>-1</c> when this step is a member name.</param>
internal readonly record struct JsonPathSegment(string? Key, int Index)
{
    /// <summary>A step into an object member.</summary>
    public static JsonPathSegment ForKey(string key) => new(key, -1);

    /// <summary>A step into an array element.</summary>
    public static JsonPathSegment ForIndex(int index) => new(null, index);

    /// <summary>Whether this step is an array index rather than a member name.</summary>
    public bool IsIndex => Key is null;
}

/// <summary>
/// How a node in a JSON document is written as a path, a Postgres expression, a containment filter and,
/// where the CLR type is known, as a Marten LINQ path.
/// </summary>
/// <remarks>
/// This is the differentiator the plan names in section 3.4: the studio knows the document's shape
/// <em>and</em> the store's schema, so it can answer "how do I query this" rather than only "what does it
/// say". Everything here is pure string construction over a segment list, which is what makes it
/// unit-testable without a database.
/// </remarks>
internal static class JsonPathExpressions
{
    /// <summary>The default jsonb column a Marten document lives in.</summary>
    public const string DefaultColumn = "data";

    /// <summary>
    /// Relaxed escaping, because these strings are copied into psql and into a LINQ expression, never
    /// interpolated into HTML - Blazor escapes on render, and <c>+</c> for a plus sign would only
    /// make a filter unreadable.
    /// </summary>
    private static readonly JsonSerializerOptions LiteralOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Whether <paramref name="key"/> can be written unquoted in a JSONPath or a dotted path.</summary>
    public static bool IsSimpleKey(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }

        if (!char.IsAsciiLetter(key[0]) && key[0] != '_')
        {
            return false;
        }

        foreach (var c in key)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Appends one step to a JSONPath expression.</summary>
    public static string AppendJsonPath(string parent, JsonPathSegment segment)
    {
        if (segment.IsIndex)
        {
            return parent + "[" + segment.Index.ToString(CultureInfo.InvariantCulture) + "]";
        }

        var key = segment.Key ?? string.Empty;
        return IsSimpleKey(key)
            ? parent + "." + key
            : parent + "['" + key.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal) + "']";
    }

    /// <summary>Appends one step to a dotted path such as <c>address.city</c> or <c>items[0].sku</c>.</summary>
    public static string AppendDotPath(string parent, JsonPathSegment segment)
    {
        if (segment.IsIndex)
        {
            return parent + "[" + segment.Index.ToString(CultureInfo.InvariantCulture) + "]";
        }

        var key = segment.Key ?? string.Empty;
        if (!IsSimpleKey(key))
        {
            return parent + "[\"" + key.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"]";
        }

        return parent.Length == 0 ? key : parent + "." + key;
    }

    /// <summary>
    /// Appends one step to a Postgres jsonb expression, using <c>-&gt;&gt;</c> for a value read as text
    /// and <c>-&gt;</c> for one that stays jsonb.
    /// </summary>
    public static string AppendPostgres(string parent, JsonPathSegment segment, bool asText)
    {
        var arrow = asText ? " ->> " : " -> ";
        return segment.IsIndex
            ? parent + arrow + segment.Index.ToString(CultureInfo.InvariantCulture)
            : parent + arrow + QuoteLiteral(segment.Key ?? string.Empty);
    }

    /// <summary>The JSONPath expression for a whole segment list, for example <c>$.items[0].sku</c>.</summary>
    public static string ToJsonPath(IReadOnlyList<JsonPathSegment> segments)
    {
        var result = "$";
        for (var i = 0; i < segments.Count; i++)
        {
            result = AppendJsonPath(result, segments[i]);
        }

        return result;
    }

    /// <summary>The dotted path for a whole segment list, for example <c>items[0].sku</c>.</summary>
    public static string ToDotPath(IReadOnlyList<JsonPathSegment> segments)
    {
        var result = string.Empty;
        for (var i = 0; i < segments.Count; i++)
        {
            result = AppendDotPath(result, segments[i]);
        }

        return result;
    }

    /// <summary>
    /// The Postgres expression for a whole segment list; <paramref name="asText"/> makes the last step
    /// <c>-&gt;&gt;</c> so the result is <c>text</c> rather than <c>jsonb</c>.
    /// </summary>
    public static string ToPostgresExpression(IReadOnlyList<JsonPathSegment> segments, bool asText, string column = DefaultColumn)
    {
        var result = column;
        for (var i = 0; i < segments.Count; i++)
        {
            result = AppendPostgres(result, segments[i], asText && i == segments.Count - 1);
        }

        return result;
    }

    /// <summary>
    /// The <c>#&gt;&gt;</c> alternative, for example <c>data #&gt;&gt; '{address,city}'</c> - one operator
    /// and one path array instead of a chain of arrows, which is what an index definition wants.
    /// </summary>
    public static string ToPostgresTextPath(IReadOnlyList<JsonPathSegment> segments, string column = DefaultColumn)
    {
        if (segments.Count == 0)
        {
            return column;
        }

        var builder = new StringBuilder();
        builder.Append('{');
        for (var i = 0; i < segments.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            var segment = segments[i];
            builder.Append(QuoteArrayElement(segment.IsIndex
                ? segment.Index.ToString(CultureInfo.InvariantCulture)
                : segment.Key ?? string.Empty));
        }

        builder.Append('}');
        return column + " #>> " + QuoteLiteral(builder.ToString());
    }

    /// <summary>
    /// The jsonb containment filter that matches this node's value, for example
    /// <c>{"address":{"city":"Helsinki"}}</c>.
    /// </summary>
    /// <remarks>
    /// An array step becomes a one-element array rather than an index, because <c>@&gt;</c> asks whether
    /// the array <em>contains</em> the element and has no notion of position. Writing the index in would
    /// produce a filter that silently matches nothing.
    /// </remarks>
    public static string ToContainmentFilter(IReadOnlyList<JsonPathSegment> segments, string valueJson)
    {
        var result = string.IsNullOrEmpty(valueJson) ? "null" : valueJson;
        for (var i = segments.Count - 1; i >= 0; i--)
        {
            var segment = segments[i];
            result = segment.IsIndex
                ? "[" + result + "]"
                : "{" + JsonSerializer.Serialize(segment.Key ?? string.Empty, LiteralOptions) + ":" + result + "}";
        }

        return result;
    }

    /// <summary>The JSON literal for a string value, escaped the way it would appear in a document.</summary>
    public static string ToJsonStringLiteral(string value) => JsonSerializer.Serialize(value, LiteralOptions);

    /// <summary>Wraps <paramref name="value"/> in single quotes as a Postgres string literal.</summary>
    public static string QuoteLiteral(string value) =>
        "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    /// <summary>
    /// Resolves a JSON path against a CLR type, producing the Marten LINQ path a query would use.
    /// </summary>
    /// <param name="documentType">The document's CLR type, or <see langword="null"/> when it is not known.</param>
    /// <param name="segments">The path into the document.</param>
    /// <param name="namingPolicy">The serializer's naming policy, so <c>first_name</c> finds <c>FirstName</c>.</param>
    /// <returns>The resolution, which carries a usable path even when a step could not be matched.</returns>
    /// <remarks>
    /// Resolution is deliberately forgiving: a document in the store can carry members the CLR type has
    /// dropped, and refusing to produce a path at all in that case would take the feature away exactly
    /// when it is most interesting. The unresolved step is written through verbatim and named in
    /// <see cref="ClrPathResolution.Note"/>, so the copy is still useful and the user is told why it may
    /// not compile.
    /// </remarks>
    public static ClrPathResolution ResolveClrPath(Type? documentType, IReadOnlyList<JsonPathSegment> segments, JsonNamingPolicy? namingPolicy)
    {
        var builder = new StringBuilder("x");
        var current = documentType;
        string? note = documentType is null ? "the document's CLR type is not known here" : null;

        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            if (segment.IsIndex)
            {
                builder.Append('[').Append(segment.Index.ToString(CultureInfo.InvariantCulture)).Append(']');
                current = current is null ? null : ElementTypeOf(current);
                continue;
            }

            var key = segment.Key ?? string.Empty;
            var member = current is null ? null : FindMember(current, key, namingPolicy);
            if (member is null)
            {
                note ??= current is null
                    ? "could not resolve '" + key + "'"
                    : "could not resolve '" + key + "' on " + current.Name;
                builder.Append('.').Append(key);
                current = null;
                continue;
            }

            builder.Append('.').Append(member.Name);
            current = MemberTypeOf(member);
        }

        return new ClrPathResolution(builder.ToString(), note is null, note);
    }

    /// <summary>Resolves a dotted path string, the form <see cref="ToDotPath"/> produces.</summary>
    public static ClrPathResolution ResolveClrPath(Type? documentType, string path, JsonNamingPolicy? namingPolicy) =>
        ResolveClrPath(documentType, ParseDotPath(path), namingPolicy);

    /// <summary>Parses a dotted path such as <c>items[0].sku</c> back into segments.</summary>
    public static ImmutableArray<JsonPathSegment> ParseDotPath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return ImmutableArray<JsonPathSegment>.Empty;
        }

        var segments = ImmutableArray.CreateBuilder<JsonPathSegment>();
        var current = new StringBuilder();
        var index = 0;

        while (index < path.Length)
        {
            var c = path[index];
            if (c == '.')
            {
                FlushKey();
                index++;
                continue;
            }

            if (c == '[')
            {
                FlushKey();
                var close = path.IndexOf(']', index);
                if (close < 0)
                {
                    index = path.Length;
                    break;
                }

                var inner = path.Substring(index + 1, close - index - 1);
                if (inner.Length >= 2 && inner[0] == '"' && inner[^1] == '"')
                {
                    var key = inner[1..^1]
                        .Replace("\\\"", "\"", StringComparison.Ordinal)
                        .Replace("\\\\", "\\", StringComparison.Ordinal);
                    segments.Add(JsonPathSegment.ForKey(key));
                }
                else if (int.TryParse(inner, NumberStyles.Integer, CultureInfo.InvariantCulture, out var arrayIndex))
                {
                    segments.Add(JsonPathSegment.ForIndex(arrayIndex));
                }

                index = close + 1;
                continue;
            }

            current.Append(c);
            index++;
        }

        FlushKey();
        return segments.ToImmutable();

        void FlushKey()
        {
            if (current.Length == 0)
            {
                return;
            }

            segments.Add(JsonPathSegment.ForKey(current.ToString()));
            current.Clear();
        }
    }

    /// <summary>Quotes one element of a Postgres text array literal when it would otherwise be ambiguous.</summary>
    private static string QuoteArrayElement(string value)
    {
        var needsQuotes = value.Length == 0;
        foreach (var c in value)
        {
            if (c is '{' or '}' or ',' or '"' or '\\' or ' ' || char.IsWhiteSpace(c))
            {
                needsQuotes = true;
                break;
            }
        }

        if (!needsQuotes)
        {
            return value;
        }

        return "\"" + value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    /// <summary>
    /// Finds the property or field a JSON member name came from, honouring an explicit
    /// <see cref="JsonPropertyNameAttribute"/> first, then the naming policy, then the plain name.
    /// </summary>
    private static MemberInfo? FindMember(Type type, string key, JsonNamingPolicy? namingPolicy)
    {
        var members = type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Cast<MemberInfo>()
            .Concat(type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            .ToArray();

        foreach (var member in members)
        {
            var attribute = member.GetCustomAttribute<JsonPropertyNameAttribute>();
            if (attribute is not null && string.Equals(attribute.Name, key, StringComparison.OrdinalIgnoreCase))
            {
                return member;
            }
        }

        if (namingPolicy is not null)
        {
            foreach (var member in members)
            {
                if (string.Equals(namingPolicy.ConvertName(member.Name), key, StringComparison.OrdinalIgnoreCase))
                {
                    return member;
                }
            }
        }

        foreach (var member in members)
        {
            if (string.Equals(member.Name, key, StringComparison.OrdinalIgnoreCase))
            {
                return member;
            }
        }

        return null;
    }

    private static Type? MemberTypeOf(MemberInfo member) => member switch
    {
        PropertyInfo property => property.PropertyType,
        FieldInfo field => field.FieldType,
        _ => null,
    };

    private static Type? ElementTypeOf(Type type)
    {
        if (type.IsArray)
        {
            return type.GetElementType();
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
        {
            return type.GetGenericArguments()[0];
        }

        foreach (var contract in type.GetInterfaces())
        {
            if (contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            {
                return contract.GetGenericArguments()[0];
            }
        }

        return null;
    }
}

/// <summary>
/// A Marten LINQ path and whether every step of it was matched against the CLR type.
/// </summary>
/// <param name="Path">The path, always usable, for example <c>x.Address.City</c>.</param>
/// <param name="Resolved">Whether every step matched a member of the CLR type.</param>
/// <param name="Note">Why it did not, when it did not.</param>
internal readonly record struct ClrPathResolution(string Path, bool Resolved, string? Note);
