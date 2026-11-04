using System.Collections;
using System.Reflection;

namespace MartenStudio.Tests.Shapes;

/// <summary>
/// Collects every string reachable from an object by public properties and collections, so a test can prove that a value it
/// planted in the data appears in none of them (D-SHAPE S6: value-free by construction).
/// </summary>
internal static class ShapeSentinel
{
    /// <summary>Every string in the graph of <paramref name="value" />.</summary>
    public static List<string> Strings(object? value)
    {
        List<string> found = [];
        Walk(value, found, new HashSet<object>(ReferenceEqualityComparer.Instance), 0);
        return found;
    }

    private static void Walk(object? value, List<string> found, HashSet<object> seen, int depth)
    {
        if (value is null || depth > 12)
        {
            return;
        }

        if (value is string text)
        {
            found.Add(text);
            return;
        }

        Type type = value.GetType();

        if (type.IsPrimitive || type.IsEnum || value is DateTimeOffset or TimeSpan or Guid or decimal)
        {
            return;
        }

        if (!type.IsValueType && !seen.Add(value))
        {
            return;
        }

        if (value is IEnumerable sequence)
        {
            foreach (object? item in sequence)
            {
                Walk(item, found, seen, depth + 1);
            }

            return;
        }

        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length == 0)
            {
                Walk(property.GetValue(value), found, seen, depth + 1);
            }
        }
    }
}
