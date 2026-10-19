using System.Data;

using MartenStudio.Internal.Sql;

using Npgsql;

using NpgsqlTypes;

namespace MartenStudio.Services.Events;

// F5: finding a stream by its natural key. In a file of its own; the class's documentation is
// EventDataService.cs's.
internal sealed partial class EventDataService
{
    /// <inheritdoc />
    public async Task<NaturalKeyLookup> FindStreamsByNaturalKeyAsync(
        StudioScope scope,
        string value,
        bool includeArchived,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        if (string.IsNullOrWhiteSpace(value))
        {
            return NaturalKeyLookup.None;
        }

        try
        {
            ResolvedScope resolved = await resolver.ResolveAsync(scope, null, cancellationToken).ConfigureAwait(false);

            IReadOnlyList<NaturalKeyModel> keys = NaturalKeyCatalog.From(resolved.Store.Options, options.Value.IsDocumentTypeVisible);
            if (keys.Count == 0)
            {
                return NaturalKeyLookup.None;
            }

            await using NpgsqlConnection connection = await OpenAsync(resolved, cancellationToken).ConfigureAwait(false);
            EventTableInfo table = await DescribeTablesAsync(resolved, connection, cancellationToken).ConfigureAwait(false);

            if (table.StreamColumns.Count == 0)
            {
                return NaturalKeyLookup.None;
            }

            string streamColumn = NaturalKeyQueries.StreamColumnFor(table.StreamIdentity);
            List<NaturalKeyMatch> matches = [];
            List<string> searched = [];

            foreach (NaturalKeyModel key in keys)
            {
                TableColumns lookup = await catalog
                    .GetAsync(connection, table.Schema, key.Table, cancellationToken).ConfigureAwait(false);

                // Declared and never created - Marten makes it with the event store's schema, or the first
                // time FetchForWriting by natural key runs - so there is nothing to look up, and nothing here
                // will create it (hard rule 14).
                if (lookup.Find("natural_key_value") is not { } keyColumn || !lookup.Has(streamColumn))
                {
                    continue;
                }

                // Parsed against the column the database actually has rather than the member's CLR type,
                // so the parameter is bound as exactly that type. A word is not a number: an integer-keyed
                // lookup table is simply not asked about it.
                NpgsqlDbType keyType = PostgresColumn.ToNpgsqlDbType(keyColumn.UdtName);
                if (!NaturalKeyCatalog.TryParse(NaturalKeyCatalog.ClrTypeFor(keyType), value, out object? parsed))
                {
                    continue;
                }

                searched.Add(key.Label);

                await using NpgsqlCommand command = NaturalKeyQueries.BuildLookup(table, new NaturalKeyLookupQuery
                {
                    Table = key.Table,
                    Key = parsed!,
                    KeyType = keyType,
                    TableHasTenant = lookup.Has("tenant_id"),
                    TenantId = resolved.TenantId,
                    IncludeArchived = includeArchived,
                });

                command.Connection = connection;
                command.CommandTimeout = CommandTimeoutSeconds;

                await using NpgsqlDataReader reader = await command
                    .ExecuteReaderAsync(CommandBehavior.SingleResult, cancellationToken).ConfigureAwait(false);

                ColumnMap columns = ColumnMap.From(reader);

                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    matches.Add(new NaturalKeyMatch(key.Label, ReadStreamRow(reader, columns)));
                }
            }

            return new NaturalKeyLookup(matches, searched);
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            return NaturalKeyLookup.Failed(Describe(scope, exception, "look the stream up by its natural key"));
        }
    }
}
