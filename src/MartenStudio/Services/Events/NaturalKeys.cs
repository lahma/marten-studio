using System.Globalization;

using JasperFx.Events.Aggregation;
using JasperFx.Events.Subscriptions;

using Marten;

using NpgsqlTypes;

namespace MartenStudio.Services.Events;

/// <summary>One aggregate with a natural key, as the studio models it from the store's configuration.</summary>
/// <param name="AggregateName">The aggregate's CLR simple name.</param>
/// <param name="MemberName">The <c>[NaturalKey]</c> member.</param>
/// <param name="InnerType">The primitive the key is stored as: <c>int</c>, <c>long</c> or <c>string</c>.</param>
/// <param name="Table">
/// The lookup table, <c>mt_natural_key_{AggregateType.Name.ToLowerInvariant()}</c> - Marten's own
/// <c>NaturalKeyTable</c> and <c>FetchNaturalKeyPlan</c> name it that way at 9.39.0 and 9.50.2.
/// </param>
internal sealed record NaturalKeyModel(string AggregateName, string MemberName, Type InnerType, string Table)
{
    /// <summary>What the Streams page calls the key: <c>Order.Number</c>.</summary>
    public string Label => AggregateName + "." + MemberName;
}

/// <summary>Reads the store's natural keys, and parses a typed value against one.</summary>
/// <remarks>
/// <para>
/// A natural key is declared on an aggregate (<c>[NaturalKey]</c> on a member, <c>[NaturalKeySource]</c> or
/// <c>NaturalKeyFor(...).SetBy&lt;TEvent&gt;</c> for the events that set it), and Marten registers an inline
/// projection that keeps <c>mt_natural_key_&lt;aggregate&gt;</c> in step with the stream. The definition is
/// on the aggregate's projection - <c>IAggregateProjection.NaturalKeyDefinition</c>, JasperFx.Events 2.74.0
/// - and Marten's own feature schema walks exactly <c>Projections.All.OfType&lt;IAggregateProjection&gt;()</c>
/// for it, which is what <c>IReadOnlyEventStoreOptions.Projections()</c> returns.
/// </para>
/// <para>
/// The table shape (9.39.0 and 9.50.2): <c>natural_key_value</c> (<c>integer</c>, <c>bigint</c> or
/// <c>varchar(200)</c>) leading the primary key, <c>stream_id uuid</c> or <c>stream_key varchar(250)</c>,
/// <c>tenant_id</c> in the key under conjoined tenancy, and <c>is_archived</c>. Archival is a property of
/// the <em>stream</em>: nothing on Marten's write side copies it onto the lookup row (#5344), so the lookup
/// reads it off <c>mt_streams</c>, as Marten's own <c>FetchNaturalKeyPlan</c> does.
/// </para>
/// </remarks>
internal static class NaturalKeyCatalog
{
    /// <summary>The lookup tables' common prefix.</summary>
    public const string TablePrefix = "mt_natural_key_";

    /// <summary>The aggregates with a usable natural key, one per lookup table.</summary>
    /// <param name="options">The store's own options.</param>
    /// <param name="isVisible">
    /// <c>MartenStudioOptions.IsDocumentTypeVisible</c>: an aggregate it hides has no natural key here - its key
    /// is not offered in the Streams box, not named in the placeholder, and not looked up.
    /// </param>
    public static IReadOnlyList<NaturalKeyModel> From(IReadOnlyStoreOptions options, Func<Type, bool>? isVisible = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<NaturalKeyModel> keys = [];
        HashSet<string> tables = new(StringComparer.Ordinal);

        foreach (ISubscriptionSource source in options.Events.Projections())
        {
            if (source is not IAggregateProjection { NaturalKeyDefinition: { } key } || !key.IsValid())
            {
                continue;
            }

            if (isVisible is not null && !isVisible(key.AggregateType))
            {
                continue;
            }

            string table = TablePrefix + key.AggregateType.Name.ToLowerInvariant();
            if (tables.Add(table))
            {
                keys.Add(new NaturalKeyModel(key.AggregateType.Name, key.Member.Name, key.InnerType, table));
            }
        }

        return keys;
    }

    /// <summary>
    /// The primitive a lookup table's <c>natural_key_value</c> holds, from its column type:
    /// <c>integer</c>, <c>bigint</c>, or text for <c>varchar(200)</c> and anything else.
    /// </summary>
    public static Type ClrTypeFor(NpgsqlDbType columnType) => columnType switch
    {
        NpgsqlDbType.Integer => typeof(int),
        NpgsqlDbType.Bigint => typeof(long),
        _ => typeof(string),
    };

    /// <summary>
    /// Parses a value typed into the Streams box against a key's primitive. A word is not a number, so an
    /// integer-keyed table is simply not asked about it.
    /// </summary>
    public static bool TryParse(Type innerType, string? text, out object? value)
    {
        ArgumentNullException.ThrowIfNull(innerType);

        string trimmed = (text ?? string.Empty).Trim();
        value = null;

        if (trimmed.Length == 0)
        {
            return false;
        }

        if (innerType == typeof(int))
        {
            if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
            {
                value = number;
                return true;
            }

            return false;
        }

        if (innerType == typeof(long))
        {
            if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out long number))
            {
                value = number;
                return true;
            }

            return false;
        }

        if (innerType == typeof(string))
        {
            value = trimmed;
            return true;
        }

        return false;
    }
}

/// <summary>One stream found by its natural key.</summary>
/// <param name="Key">Which natural key matched: <c>Order.Number</c>.</param>
/// <param name="Stream">The stream.</param>
internal sealed record NaturalKeyMatch(string Key, StreamRow Stream);

/// <summary>What a natural-key lookup from the Streams box found.</summary>
/// <param name="Matches">The streams, per key that matched.</param>
/// <param name="Searched">The keys the value was looked up under, for the line that says so.</param>
/// <param name="Error">What went wrong, when the lookup failed.</param>
internal sealed record NaturalKeyLookup(
    IReadOnlyList<NaturalKeyMatch> Matches,
    IReadOnlyList<string> Searched,
    EventDataError? Error = null)
{
    /// <summary>No natural key to look the value up under.</summary>
    public static NaturalKeyLookup None { get; } = new([], []);

    /// <summary>A lookup that failed.</summary>
    public static NaturalKeyLookup Failed(EventDataError error) => new([], [], error);

    /// <summary>Whether any key was searched at all.</summary>
    public bool Searchable => Searched.Count > 0;
}
