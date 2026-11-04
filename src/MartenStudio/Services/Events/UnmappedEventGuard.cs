using System.Data.Common;

using Marten.Events;

using MartenStudio.Services.Health.Checks;

using PostgresStudio.Services;

namespace MartenStudio.Services.Events;

/// <summary>
/// The replay's and the stepper's check, before a row reaches Marten's event selector: is the alias the row was stored with one this
/// process maps?
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> For an alias with no mapping, <c>IEventStorage</c>'s resolver loads the stored <c>mt_dotnet_type</c> and registers that type on
/// the host's store, as any event read in the host would. A visitor pressing a version step must not do that: it changes
/// <c>AllKnownEventTypes()</c> for the rest of the process and silences E05 (unregistered event types) until a restart, the same
/// reason the events' shape check classifies an unmapped alias's rows without the resolver (D-SHAPE). The decision is the shape
/// check's: <see cref="EventHealth.KnownAliases" />, the registered mappings and an upcaster's old names, and where the upcasters'
/// aliases cannot be read on this Marten the registered names alone.
/// </para>
/// <para>
/// <b>What an unmapped row does.</b> It is not folded, and nothing after it is read: a fold cannot skip an event and still say what the
/// aggregate is. <see cref="RequireMappedAsync" /> throws <see cref="UnmappedEventException" />, which the callers turn into the value each already
/// returns for an event it cannot read. It runs no statement and loads nothing (hard rule 14).
/// </para>
/// </remarks>
internal sealed class UnmappedEventGuard
{
    private readonly Func<string, bool> isMapped;

    private UnmappedEventGuard(Func<string, bool> isMapped) => this.isMapped = isMapped;

    /// <summary>The guard for a store's events: built once per read, from <c>StoreOptions</c> and without a statement.</summary>
    /// <param name="events">The store's event options.</param>
    public static UnmappedEventGuard For(IReadOnlyEventStoreOptions events)
    {
        ArgumentNullException.ThrowIfNull(events);

        Func<string, bool>? known = EventHealth.KnownAliases(events);

        if (known is null)
        {
            HashSet<string> registered = new(events.AllKnownEventTypes().Select(static x => x.EventTypeName), StringComparer.Ordinal);
            known = registered.Contains;
        }

        return new UnmappedEventGuard(known);
    }

    /// <summary>
    /// Throws <see cref="UnmappedEventException" /> when the reader's current row carries an alias this process does not map. Reads two
    /// columns of the row by name (<c>type</c> and <c>version</c>, both always in Marten's event select list) and consumes nothing:
    /// Marten's selector reads the same row afterwards.
    /// </summary>
    /// <param name="reader">A reader over the replay's select list, positioned on a row.</param>
    /// <param name="cancellationToken">The read's token.</param>
    /// <exception cref="UnmappedEventException">The row's alias is not mapped.</exception>
    public async Task RequireMappedAsync(DbDataReader reader, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);

        int typeOrdinal = reader.GetOrdinal("type");

        // A row with no alias is not one Marten can map either; the selector says what it makes of it.
        if (await reader.IsDBNullAsync(typeOrdinal, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        string alias = await reader.GetFieldValueAsync<string>(typeOrdinal, cancellationToken).ConfigureAwait(false);

        if (isMapped(alias))
        {
            return;
        }

        long version = await reader.GetFieldValueAsync<long>(reader.GetOrdinal("version"), cancellationToken).ConfigureAwait(false);
        throw new UnmappedEventException(alias, version);
    }
}

/// <summary>An event the replay or the stepper met whose alias this process does not map: it is not folded.</summary>
internal sealed class UnmappedEventException : Exception
{
    /// <summary>The event the message names.</summary>
    /// <param name="alias">The alias the row was stored with (bounded and escaped in the message).</param>
    /// <param name="version">The event's version in its stream.</param>
    public UnmappedEventException(string alias, long version)
        : base(
            "Event '" + StudioLogValue.Cap(alias) + "' at version "
            + version.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + " is not mapped in this process; not folded.")
    {
    }
}
