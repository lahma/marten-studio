using System.Globalization;

namespace PostgresStudio.Services.Schema;

/// <summary>How a table's storage parameter stands against what the configuration declares.</summary>
internal enum StorageParameterStatus
{
    /// <summary>Declared, and the table has it at that value.</summary>
    Matches,

    /// <summary>Declared, and the table has another value or none: the next apply sets it.</summary>
    Drift,

    /// <summary>
    /// On the table and not declared. Marten's delta leaves such a parameter alone - "only the parameters declared
    /// here are managed" (<c>DocumentMapping.StorageParameters</c>, V9.50.2) - unless an apply rebuilds the table
    /// (a changed partition scheme copies it out and back), so this is information, not drift.
    /// </summary>
    SetOutsideMarten,

    /// <summary>
    /// A partitioned table whose leaf partitions do not all have the parameter at one value. Postgres keeps no
    /// storage parameters on a partitioned parent, and Weasel writes and compares them per partition, so this is
    /// the only honest answer short of naming a partition - which the studio never does.
    /// </summary>
    PartitionsDiffer,
}

/// <summary>One storage parameter of one table.</summary>
/// <param name="Name">The parameter, as Postgres spells it.</param>
/// <param name="Declared">What the configuration declares, or <see langword="null" />.</param>
/// <param name="Actual">What <c>reloptions</c> holds, or <see langword="null" />.</param>
/// <param name="Status">How the two stand.</param>
internal sealed record StorageParameterState(string Name, string? Declared, string? Actual, StorageParameterStatus Status);

/// <summary>
/// A table's storage parameters (<c>reloptions</c>) read, and compared with the ones a configuration declares -
/// where the configuration can declare them.
/// </summary>
/// <remarks>
/// <para>
/// What is declared is the source's to say: in MartenStudio, <c>Schema.For&lt;T&gt;().FillFactor(n)</c> and
/// <c>.StorageParameter(name, value)</c> from Marten 9.47, read by <c>DeclaredStorageParameters</c>. Where nothing can be
/// declared there is nothing to compare against, and the table's own parameters are shown as they are.
/// </para>
/// <para>
/// <b>Values are compared as Postgres would.</b> <c>reloptions</c> keeps the text it was given, and a declared
/// value is a .NET object, so <c>80</c> and <c>"80"</c>, <c>0.02</c> and <c>"0.020"</c>, <c>true</c> and
/// <c>"on"</c> are the same value.
/// </para>
/// </remarks>
internal static class StorageParameters
{
    /// <summary><c>reloptions</c> as name and value, the name lower case. An entry without <c>=</c> is skipped.</summary>
    public static IReadOnlyDictionary<string, string> Parse(IReadOnlyList<string>? reloptions)
    {
        Dictionary<string, string> parsed = new(StringComparer.OrdinalIgnoreCase);

        foreach (string option in reloptions ?? [])
        {
            int equals = option.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
            {
                continue;
            }

            parsed[option[..equals].Trim().ToLowerInvariant()] = option[(equals + 1)..].Trim();
        }

        return parsed;
    }

    /// <summary>Whether a storage parameter's text is Postgres' false: <c>false</c>, <c>off</c>, <c>no</c>, <c>0</c>, <c>f</c>.</summary>
    public static bool IsFalse(string? value) => TryParseBoolean(value, out bool parsed) && !parsed;

    /// <summary>Whether two parameter values are the same value, the way Postgres would read them.</summary>
    public static bool SameValue(string? left, string? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        string a = left.Trim().Trim('\'');
        string b = right.Trim().Trim('\'');

        if (decimal.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal x)
            && decimal.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal y))
        {
            return x == y;
        }

        if (TryParseBoolean(a, out bool p) && TryParseBoolean(b, out bool q))
        {
            return p == q;
        }

        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A declared value as text: invariant numbers, <c>true</c>/<c>false</c>.</summary>
    public static string Format(object? value) => value switch
    {
        null => string.Empty,
        bool flag => flag ? "true" : "false",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    /// <summary>
    /// Every parameter the table has or the configuration declares, and how each stands - declared ones first,
    /// in declaration order, then the rest by name. With <paramref name="declared" /> unknown, every parameter
    /// on the table is simply listed as set outside Marten's declarations.
    /// </summary>
    /// <param name="declared">What the configuration declares, or <see langword="null" /> when that is unknown.</param>
    /// <param name="reloptions">
    /// The table's parameters - for a partitioned table, the ones every leaf partition has at one value.
    /// </param>
    /// <param name="differing">For a partitioned table, the parameters its leaves disagree on, by name.</param>
    public static IReadOnlyList<StorageParameterState> Compare(
        IReadOnlyList<KeyValuePair<string, string>>? declared,
        IReadOnlyList<string>? reloptions,
        IReadOnlyList<string>? differing = null)
    {
        IReadOnlyDictionary<string, string> actual = Parse(reloptions);
        HashSet<string> disagreed = new((differing ?? []).Select(static x => x.Trim().ToLowerInvariant()), StringComparer.OrdinalIgnoreCase);
        List<StorageParameterState> states = [];
        HashSet<string> named = new(StringComparer.OrdinalIgnoreCase);

        foreach ((string name, string value) in declared ?? [])
        {
            if (!named.Add(name))
            {
                continue;
            }

            if (disagreed.Contains(name))
            {
                states.Add(new StorageParameterState(name, value, null, StorageParameterStatus.PartitionsDiffer));
                continue;
            }

            string? has = actual.GetValueOrDefault(name);
            states.Add(new StorageParameterState(
                name,
                value,
                has,
                has is not null && SameValue(value, has) ? StorageParameterStatus.Matches : StorageParameterStatus.Drift));
        }

        foreach ((string name, string value) in actual.OrderBy(static x => x.Key, StringComparer.Ordinal))
        {
            if (named.Add(name))
            {
                states.Add(new StorageParameterState(name, null, value, StorageParameterStatus.SetOutsideMarten));
            }
        }

        foreach (string name in disagreed.Order(StringComparer.Ordinal))
        {
            if (named.Add(name))
            {
                states.Add(new StorageParameterState(name, null, null, StorageParameterStatus.PartitionsDiffer));
            }
        }

        return states;
    }

    private static bool TryParseBoolean(string? value, out bool parsed)
    {
        switch (value?.Trim().Trim('\'').ToLowerInvariant())
        {
            case "true" or "on" or "yes" or "1" or "t" or "y":
                parsed = true;
                return true;

            case "false" or "off" or "no" or "0" or "f" or "n":
                parsed = false;
                return true;

            default:
                parsed = false;
                return false;
        }
    }
}
