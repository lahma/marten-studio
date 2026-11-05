using System.Collections;

namespace MartenStudio.Services.Events;

/// <summary>
/// The counts a page has loaded, by type name, that also carries the counted listing they came from (1.2-UX-FIX-FU3).
/// </summary>
/// <remarks>
/// A component parameter cannot be of an internal type (D12), so the Event types page hands the shape panel its counts as the public
/// <see cref="IReadOnlyDictionary{TKey, TValue}" /> it always did, and the panel recovers the listing from this subclass: the check
/// confirms a name against the listing object - never a bare name - and only where the listing was read for its own scope, recently.
/// </remarks>
internal sealed class CountedListingView : IReadOnlyDictionary<string, long>
{
    private readonly Dictionary<string, long> counts;

    /// <summary>Views <paramref name="listing" />'s counts.</summary>
    /// <param name="listing">The page's listing; empty counts, and no listing carried, when its counts are not loaded.</param>
    public CountedListingView(EventTypeList listing)
    {
        ArgumentNullException.ThrowIfNull(listing);

        counts = listing.CountsLoaded
            ? listing.Types.Where(static x => x.Count is not null).ToDictionary(static x => x.Name, static x => x.Count!.Value, StringComparer.Ordinal)
            : [];
        Listing = listing.CountsLoaded ? listing : null;
    }

    /// <summary>The counted listing the counts came from, or <see langword="null" /> when none was counted.</summary>
    public EventTypeList? Listing { get; }

    /// <inheritdoc />
    public long this[string key] => counts[key];

    /// <inheritdoc />
    public IEnumerable<string> Keys => counts.Keys;

    /// <inheritdoc />
    public IEnumerable<long> Values => counts.Values;

    /// <inheritdoc />
    public int Count => counts.Count;

    /// <inheritdoc />
    public bool ContainsKey(string key) => counts.ContainsKey(key);

    /// <inheritdoc />
    public bool TryGetValue(string key, out long value) => counts.TryGetValue(key, out value);

    /// <inheritdoc />
    public IEnumerator<KeyValuePair<string, long>> GetEnumerator() => counts.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
