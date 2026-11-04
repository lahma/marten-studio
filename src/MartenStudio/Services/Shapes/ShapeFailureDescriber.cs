using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MartenStudio.Services.Shapes;

/// <summary>
/// How one item failed, as a value: a kind, the exception's type name, the path it failed at and the JSON kind stored there
/// (D-SHAPE S6). <b>Never the exception's message</b> - a converter's, or Newtonsoft's, quotes the value it could not read.
/// </summary>
/// <param name="Kind">How it failed.</param>
/// <param name="ExceptionType">The exception type's short name.</param>
/// <param name="Path">The normalized path, array indices written <c>[*]</c>.</param>
/// <param name="StoredKind">The JSON kind found in the stored item at that path; omitted when the walk fails.</param>
/// <param name="Detail">The subclass alias, or "alias / mt_dotnet_type": a type name, never a message.</param>
internal sealed record ShapeFailure(
    ShapeFailureKind Kind,
    string ExceptionType,
    string? Path,
    JsonValueKind? StoredKind,
    string? Detail);

/// <summary>Turns an exception from a store's reader or writer into a <see cref="ShapeFailure" />.</summary>
/// <remarks>
/// The path comes from <see cref="JsonException.Path" />, otherwise from a public instance <see cref="string" /> property named
/// <c>Path</c> on the exception or one inside it - which is how Newtonsoft's <c>JsonReaderException</c> and
/// <c>JsonSerializationException</c> are found, by reflection, because the studio does not reference Newtonsoft.
/// </remarks>
internal static class ShapeFailureDescriber
{
    /// <summary>Describes <paramref name="exception" />.</summary>
    /// <param name="kind">How the item failed.</param>
    /// <param name="exception">What the reader or writer threw.</param>
    /// <param name="storedJson">The stored item, to find the JSON kind at the failing path; may be <see langword="null" />.</param>
    /// <param name="detail">A type name to carry along, or <see langword="null" />.</param>
    public static ShapeFailure Describe(ShapeFailureKind kind, Exception exception, string? storedJson, string? detail = null)
    {
        ArgumentNullException.ThrowIfNull(exception);

        (string? rawPath, Exception source) = FindPath(exception);
        string? path = rawPath is null ? null : Normalize(rawPath);

        JsonValueKind? storedKind = rawPath is not null && storedJson is not null ? StoredKindAt(storedJson, rawPath) : null;

        return new ShapeFailure(kind, source.GetType().Name, path, storedKind, detail);
    }

    /// <summary>A failure that is not an exception: a subclass alias the store no longer registers, a reader that returned nothing.</summary>
    /// <param name="kind">How the item failed.</param>
    /// <param name="exceptionType">The exception type's name, or an empty string for a failure that threw nothing.</param>
    /// <param name="detail">A type name to carry along, or <see langword="null" />.</param>
    public static ShapeFailure Of(ShapeFailureKind kind, string exceptionType, string? detail = null) =>
        new(kind, exceptionType, null, null, detail);

    /// <summary>
    /// The path in the usual spelling: it starts with <c>$</c>, and an array index is <c>[*]</c>. Cut at
    /// <see cref="ShapeCheckLimits.MaxPathLength" />.
    /// </summary>
    /// <param name="path">A path as an exception spelled it: <c>$.items[0].sku</c> or Newtonsoft's <c>items[0].sku</c>.</param>
    internal static string Normalize(string path)
    {
        StringBuilder builder = new(Math.Min(path.Length + 2, ShapeCheckLimits.MaxPathLength + 2));

        if (path.Length == 0 || path[0] != '$')
        {
            builder.Append('$');

            if (path.Length > 0 && path[0] != '[')
            {
                builder.Append('.');
            }
        }

        for (int i = 0; i < path.Length; i++)
        {
            if (path[i] == '[' && i + 1 < path.Length && char.IsAsciiDigit(path[i + 1]))
            {
                int end = path.IndexOf(']', i);

                if (end > i && IsDigits(path.AsSpan(i + 1, end - i - 1)))
                {
                    builder.Append("[*]");
                    i = end;
                    continue;
                }
            }

            builder.Append(path[i]);
        }

        return builder.Length <= ShapeCheckLimits.MaxPathLength ? builder.ToString() : builder.ToString(0, ShapeCheckLimits.MaxPathLength);
    }

    /// <summary>The JSON kind of the value at <paramref name="path" /> in <paramref name="storedJson" />, or <see langword="null" /> when the walk fails.</summary>
    /// <param name="storedJson">The stored item.</param>
    /// <param name="path">A path as an exception spelled it, indices included.</param>
    internal static JsonValueKind? StoredKindAt(string storedJson, string path)
    {
        try
        {
            JsonNode? node = JsonNode.Parse(storedJson, documentOptions: new JsonDocumentOptions { MaxDepth = 128 });

            foreach (object segment in Segments(path))
            {
                node = segment switch
                {
                    int index when node is JsonArray array && index >= 0 && index < array.Count => array[index],
                    string key when node is JsonObject obj && obj.TryGetPropertyValue(key, out JsonNode? child) => child,
                    _ => throw new KeyNotFoundException(),
                };
            }

            return node is null ? JsonValueKind.Null : ShapeDiff.KindOf(node);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    private static (string? Path, Exception Source) FindPath(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is JsonException { Path: { Length: > 0 } jsonPath })
            {
                return (jsonPath, current);
            }

            if (PathProperty(current) is { Length: > 0 } reflected)
            {
                return (reflected, current);
            }
        }

        return (null, exception is TargetInvocationException { InnerException: { } inner } ? inner : exception);
    }

    private static string? PathProperty(Exception exception)
    {
        try
        {
            PropertyInfo? property = exception.GetType().GetProperty("Path", BindingFlags.Public | BindingFlags.Instance);

            return property is { PropertyType: var type, CanRead: true } && type == typeof(string) && property.GetIndexParameters().Length == 0
                ? (string?) property.GetValue(exception)
                : null;
        }
        catch (Exception reflection) when (reflection is TargetInvocationException or AmbiguousMatchException or MethodAccessException)
        {
            return null;
        }
    }

    private static bool IsDigits(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
        {
            return false;
        }

        foreach (char c in text)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The segments of a path: a <see cref="string" /> for a member, an <see cref="int" /> for an index.</summary>
    private static IEnumerable<object> Segments(string path)
    {
        int i = path.Length > 0 && path[0] == '$' ? 1 : 0;

        while (i < path.Length)
        {
            char c = path[i];

            if (c == '.')
            {
                i++;
                continue;
            }

            if (c == '[')
            {
                int close = IndexOfClose(path, i);

                if (close < 0)
                {
                    yield break;
                }

                string inside = path.Substring(i + 1, close - i - 1);

                if (inside.Length >= 2 && inside[0] is '\'' or '"' && inside[^1] == inside[0])
                {
                    yield return inside[1..^1];
                }
                else if (int.TryParse(inside, NumberStyles.None, CultureInfo.InvariantCulture, out int index))
                {
                    yield return index;
                }
                else
                {
                    yield return inside;
                }

                i = close + 1;
                continue;
            }

            int end = i;

            while (end < path.Length && path[end] is not '.' and not '[')
            {
                end++;
            }

            yield return path[i..end];
            i = end;
        }
    }

    private static int IndexOfClose(string path, int open)
    {
        char quote = open + 1 < path.Length && path[open + 1] is '\'' or '"' ? path[open + 1] : '\0';

        for (int i = open + 1; i < path.Length; i++)
        {
            if (quote == '\0' ? path[i] == ']' : path[i] == ']' && path[i - 1] == quote)
            {
                return i;
            }
        }

        return -1;
    }
}
