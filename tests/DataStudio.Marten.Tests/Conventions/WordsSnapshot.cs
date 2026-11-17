using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace DataStudio.Marten.Tests.Conventions;

/// <summary>
/// Every sentence a module's words hold, as one text a Verify baseline pins, so that a reviewer reads a change of wording as a diff (v2 step 6).
/// </summary>
/// <remarks>
/// <para>
/// A member is written as <c>[Name]</c> and its value on the lines below, in alphabetical order. A sentence composed over a parameter - a refusal
/// that names the switch that is on - is written once for each sample the caller gives, and a method that takes only plain values and returns a
/// sentence is called with every value of an enum parameter, so that each arm of its <c>switch</c> is a line of the baseline. A member whose parameters
/// are objects the scan cannot make is listed by its signature at the end, so that it cannot be missed.
/// </para>
/// <para>
/// Linked into every fast test project that owns words, so each suite pins its own module and all of them read the same way.
/// </para>
/// </remarks>
internal static class WordsSnapshot
{
    /// <summary>The words held by an instance: the public string, bool and dictionary members, and the string methods the caller has samples for.</summary>
    /// <param name="words">The words.</param>
    /// <param name="samples">The arguments to call each string method with, by method name: one call per argument list.</param>
    /// <returns>The snapshot text, ending with a line feed.</returns>
    public static string OfInstance(object words, IReadOnlyDictionary<string, object?[][]> samples)
    {
        ArgumentNullException.ThrowIfNull(words);
        ArgumentNullException.ThrowIfNull(samples);

        List<(string Name, string Value)> entries = [];

        foreach (PropertyInfo property in words.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(static x => x.Name, StringComparer.Ordinal))
        {
            object? value = property.GetValue(words);

            if (property.PropertyType == typeof(string))
            {
                entries.Add((property.Name, (string?) value ?? "(null)"));
            }
            else if (property.PropertyType == typeof(bool))
            {
                entries.Add((property.Name, ((bool) value!) ? "true" : "false"));
            }
            else if (value is IReadOnlyDictionary<string, string> dictionary)
            {
                entries.Add((property.Name, dictionary.Count == 0
                    ? "(none)"
                    : string.Join('\n', dictionary.OrderBy(static x => x.Key, StringComparer.Ordinal).Select(static x => x.Key + " = " + x.Value))));
            }
        }

        foreach (MethodInfo method in words.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                     .Where(static x => !x.IsSpecialName && x.ReturnType == typeof(string))
                     .OrderBy(static x => x.Name, StringComparer.Ordinal))
        {
            if (!samples.TryGetValue(method.Name, out object?[][]? calls))
            {
                throw new InvalidOperationException(
                    "The words have a method, " + method.Name + ", the snapshot has no sample for: say what to call it with, so that its sentence is pinned.");
            }

            foreach (object?[] arguments in calls)
            {
                entries.Add((Signature(method.Name, arguments), (string) method.Invoke(words, arguments)!));
            }
        }

        return Write(entries, []);
    }

    /// <summary>The words of a <c>StudioText</c>: every member, and each sentence composed over a switch's name said for the module's own switch and the shell's.</summary>
    /// <param name="words">The module's <c>StudioText</c>.</param>
    /// <param name="ownReadOnlyOption">The module's own switch as a host spells it, <c>MartenOptions.ReadOnly</c>.</param>
    /// <returns>The snapshot text, ending with a line feed.</returns>
    public static string OfStudioText(object words, string ownReadOnlyOption)
    {
        object?[][] switches = [[ownReadOnlyOption], ["DataStudioOptions.ReadOnly"]];

        return OfInstance(words, new Dictionary<string, object?[][]>(StringComparer.Ordinal)
        {
            ["BrowseReadOnlyDenial"] = switches,
            ["DefinitionReadOnlyDenial"] = switches,
            ["SessionsReadOnlyDenial"] = switches,
            ["StatementsReadOnlyDenial"] = switches,
            ["DefinitionWritePolicyDenialFor"] = [["ViewDefinitions"], ["BrowseDatabase"], ["ViewDefinitions and BrowseDatabase"]],
            ["OptionName"] = [["Example"]],
            ["CapabilityOption"] = [["Example"]],
            ["CheckTitle"] = [["S02", "Title of the check"], ["T01", "Title of the check"]],
        });
    }

    /// <summary>The words held by a static class: its string constants and properties, and the string methods that take plain values.</summary>
    /// <param name="type">The class.</param>
    /// <param name="objects">The values to call a method with for a parameter that is an object, by its type, or <see langword="null" /> for one the caller cannot make.</param>
    /// <returns>The snapshot text, ending with a line feed.</returns>
    public static string OfStatic(Type type, Func<Type, object?[]?>? objects = null)
    {
        ArgumentNullException.ThrowIfNull(type);

        List<(string Name, string Value)> entries = [];
        List<string> skipped = [];

        foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Static).Where(static x => x.IsLiteral && x.FieldType == typeof(string)).OrderBy(static x => x.Name, StringComparer.Ordinal))
        {
            entries.Add((field.Name, (string) field.GetRawConstantValue()!));
        }

        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Static).Where(static x => x.PropertyType == typeof(string)).OrderBy(static x => x.Name, StringComparer.Ordinal))
        {
            entries.Add((property.Name, (string) property.GetValue(null)!));
        }

        foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                     .Where(static x => !x.IsSpecialName && x.ReturnType == typeof(string))
                     .OrderBy(static x => x.Name, StringComparer.Ordinal).ThenBy(static x => x.GetParameters().Length))
        {
            IReadOnlyList<object?[]>? calls = CallsFor(method, objects);

            if (calls is null)
            {
                skipped.Add(method.Name + "(" + string.Join(", ", method.GetParameters().Select(static x => x.ParameterType.Name + " " + x.Name)) + ")");
                continue;
            }

            foreach (object?[] arguments in calls)
            {
                string value;

                try
                {
                    value = (string) method.Invoke(null, arguments)!;
                }
                catch (TargetInvocationException exception)
                {
                    value = "(throws " + exception.InnerException?.GetType().Name + ")";
                }

                entries.Add((Signature(method.Name, arguments), value));
            }
        }

        return Write(entries, skipped);
    }

    private static string Signature(string name, object?[] arguments) =>
        arguments.Length == 0 ? name : name + "(" + string.Join(", ", arguments.Select(Show)) + ")";

    private static string Show(object? argument) => argument switch
    {
        null => "null",
        string text => "\"" + text + "\"",
        bool flag => flag ? "true" : "false",
        IEnumerable list when argument is not string => "[" + string.Join(", ", list.Cast<object?>().Select(Show)) + "]",
        IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
        _ => argument.ToString() ?? string.Empty,
    };

    /// <summary>
    /// What to call a method with, or <see langword="null" /> when a parameter is an object the snapshot cannot make: the plain values of each
    /// parameter, and for an enum each of its values, one call per combination of the enum's.
    /// </summary>
    private static List<object?[]>? CallsFor(MethodInfo method, Func<Type, object?[]?>? objects)
    {
        List<object?[]> calls = [[]];

        foreach (ParameterInfo parameter in method.GetParameters())
        {
            object?[]? values = ValuesOf(parameter.ParameterType, objects);

            if (values is null)
            {
                return null;
            }

            calls = [.. calls.SelectMany(prefix => values.Select(value => (object?[]) [.. prefix, value]))];
        }

        return calls;
    }

    private static object?[]? ValuesOf(Type type, Func<Type, object?[]?>? objects)
    {
        Type? underlying = Nullable.GetUnderlyingType(type);

        if (underlying is not null)
        {
            return ValuesOf(underlying, objects) is { } inner ? [null, .. inner] : null;
        }

        if (type.IsEnum)
        {
            return [.. Enum.GetValues(type).Cast<object?>()];
        }

        if (type == typeof(string))
        {
            return ["sample"];
        }

        if (type == typeof(int))
        {
            return [3];
        }

        if (type == typeof(long))
        {
            return [1234L];
        }

        if (type == typeof(bool))
        {
            return [false, true];
        }

        if (type == typeof(DateTimeOffset))
        {
            return [new DateTimeOffset(2026, 11, 2, 6, 0, 0, TimeSpan.Zero)];
        }

        if (type == typeof(TimeSpan))
        {
            return [TimeSpan.FromMinutes(90)];
        }

        if (type == typeof(IReadOnlyList<string>) || type == typeof(IEnumerable<string>))
        {
            return [new[] { "alpha", "beta" }];
        }

        return objects?.Invoke(type);
    }

    private static string Write(List<(string Name, string Value)> entries, List<string> skipped)
    {
        StringBuilder text = new();

        foreach ((string name, string value) in entries)
        {
            text.Append('[').Append(name).Append("]\n").Append(value.Replace("\r\n", "\n", StringComparison.Ordinal)).Append("\n\n");
        }

        if (skipped.Count > 0)
        {
            text.Append("[Not snapshotted: parameters that are objects]\n").Append(string.Join('\n', skipped)).Append("\n\n");
        }

        return text.ToString().TrimEnd('\n') + "\n";
    }
}
