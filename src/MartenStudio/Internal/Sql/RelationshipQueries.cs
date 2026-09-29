using Npgsql;

using NpgsqlTypes;

namespace MartenStudio.Internal.Sql;

/// <summary>
/// One foreign-key constraint as Postgres really has it.
/// </summary>
/// <remarks>
/// Read from <c>pg_constraint</c> rather than from Weasel's own table reader: the table reader is part of
/// the migration machinery and reaches Marten's feature schemas on the way (AGENTS.md hard rule 14), and
/// the graph needs nothing beyond the names.
/// </remarks>
/// <param name="Schema">The schema of the table the key is declared on.</param>
/// <param name="Table">The table the key is declared on — the one that points.</param>
/// <param name="Columns">The key's columns, in constraint order.</param>
/// <param name="LinkedSchema">The referenced table's schema.</param>
/// <param name="LinkedTable">The referenced table.</param>
/// <param name="LinkedColumns">The referenced columns, in constraint order.</param>
/// <param name="Name">The constraint name.</param>
/// <param name="OnDelete">The delete action, spelled the way <c>CascadeAction</c> spells it.</param>
/// <param name="OnUpdate">The update action, spelled the same way.</param>
/// <param name="Validated">
/// <c>convalidated</c>: <see langword="false" /> for a key added <c>NOT VALID</c>, whose existing rows
/// Postgres has never checked. Every row written since is checked all the same.
/// </param>
/// <param name="PrimaryKey">
/// The pointing table's primary-key columns in index order, or <see langword="null" /> when it has none.
/// What the relationship graph strips a composite key's shared leading columns against.
/// </param>
/// <param name="LinkedPrimaryKey">The referenced table's primary-key columns, likewise.</param>
internal sealed record PhysicalForeignKey(
    string Schema,
    string Table,
    IReadOnlyList<string> Columns,
    string LinkedSchema,
    string LinkedTable,
    IReadOnlyList<string> LinkedColumns,
    string Name,
    string OnDelete,
    string OnUpdate,
    bool Validated = true,
    IReadOnlyList<string>? PrimaryKey = null,
    IReadOnlyList<string>? LinkedPrimaryKey = null);

/// <summary>A bounded foreign-key read: the keys, and whether the cap stopped it.</summary>
/// <param name="Keys">The keys, at most the cap.</param>
/// <param name="Truncated">Whether the catalog held more than the cap.</param>
internal sealed record PhysicalForeignKeyRead(IReadOnlyList<PhysicalForeignKey> Keys, bool Truncated)
{
    /// <summary>Nothing, and nothing cut off.</summary>
    public static PhysicalForeignKeyRead Empty { get; } = new([], false);
}

/// <summary>
/// The reads behind the Relationships screen: every foreign key Postgres has in the store's schemas (or
/// touching any set of schemas), and the bounded "how many of these point at that one document" counts,
/// for a collection and for a table the studio does not map.
/// </summary>
/// <remarks>
/// <para>
/// Both are here rather than beside their service because this is the one place a Postgres identifier
/// becomes SQL text (AGENTS.md hard rule 4). The catalog read interpolates nothing at all — the schema
/// names travel as a single <c>text[]</c> parameter compared with <c>= any(@schemas)</c>, exactly as
/// <see cref="SchemaStatsQueries"/> does — and the inbound count quotes its table and its two columns
/// through <see cref="SqlIdentifier"/> and sends the id, the tenant and the cap as parameters.
/// </para>
/// <para>
/// Neither touches anything that builds schema. <c>IMartenDatabase.DocumentTables()</c>,
/// <c>AllObjects()</c> and <c>AllSchemaNames()</c> all run <c>Migrator.ApplyAllAsync</c> under the
/// database's own <c>AutoCreate</c> on the way to answering, which is how a read path creates
/// <c>mt_hilo</c>; the schema names come from <c>SchemaDeclarationReader</c>, which reads
/// <c>StoreOptions</c> and opens no connection.
/// </para>
/// </remarks>
internal static class RelationshipQueries
{
    /// <summary>
    /// How many matching rows the inbound count looks at before it stops. One more than the number it
    /// will report, so that "there are more" is distinguishable from "there are exactly this many".
    /// </summary>
    public const int DefaultInboundCap = 1001;

    /// <summary>
    /// Every foreign key declared on a table in the store's schemas, with both ends resolved to names.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>conkey</c> and <c>confkey</c> are <c>smallint[]</c> of attribute numbers, so the column names
    /// come from a correlated <c>unnest(...) with ordinality</c> join against <c>pg_attribute</c> —
    /// <c>with ordinality</c> because <c>array_agg</c> over a bare <c>unnest</c> has no defined order and
    /// a composite key whose columns came back the other way round would not match the one the
    /// configuration declares.
    /// </para>
    /// <para>
    /// Both <c>attname</c> and the two action codes are cast to <c>text</c>: they are Postgres's internal
    /// <c>name</c> and <c>"char"</c> types, and reading them as text is what keeps the mapping from
    /// depending on how Npgsql happens to handle those two.
    /// </para>
    /// <para>
    /// <b><c>conparentid = 0</c> keeps the clones out.</b> Postgres copies a foreign key declared on a
    /// partitioned table onto every partition, and onto the parent once per referenced partition, so one
    /// declared key on a tenant-partitioned collection (<c>MartenManagedTenantListPartitions</c>) becomes
    /// as many rows as there are tenants, on tables named <c>mt_doc_&lt;alias&gt;_&lt;tenant&gt;</c>. Those names are
    /// not in the store's mappings, so every one of them would be reported as a key "on a table no
    /// document type maps" - printing the tenant list on a page a visitor may be scoped to one tenant of,
    /// and walking straight past <c>IsDocumentTypeVisible</c>, which can only recognise the parent.
    /// A clone has a non-zero <c>conparentid</c>; a key declared directly on a partition has zero and is
    /// still reported, which is right.
    /// </para>
    /// <para>
    /// Both ends' primary keys come back with the key - the first <c>indnkeyatts</c> entries of the
    /// primary index's <c>indkey</c>, so an <c>INCLUDE</c> column is never mistaken for part of it - because
    /// that is what a composite key's label is stripped against (<c>RelationshipGraphBuilder.LabelFor</c>):
    /// Quartz.NET's <c>sched_name</c> leads every key of every table, and a label that repeated it would
    /// say the same thing on every arrow.
    /// </para>
    /// </remarks>
    internal const string ForeignKeysSql =
        ForeignKeysSelect +
        """

        where con.contype = 'f' and con.conparentid = 0 and ns.nspname = any(@schemas)
        order by ns.nspname, cl.relname, con.conname
        """;

    /// <summary>
    /// Every foreign key with <em>either</em> end in the schema set, bounded - what the relationship graph
    /// reads once per database and then filters per visitor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Either end, because the far end is exactly what a visitor must not be told about.</b>
    /// <c>legacy.customer_credit</c> points into the document schema and cascades when a customer is
    /// deleted; a read that only looked at keys <em>declared in</em> the store's schemas would never see it,
    /// and neither the "referenced by" panel nor the delete dialog could say it exists. So the read takes
    /// every key that touches the set, and the per-visitor filter decides which ends may be named - an end
    /// in a schema the visitor is not shown is counted, never named.
    /// </para>
    /// <para>
    /// <b>No partition at either end</b>, as in the database browser's own read: a partition is rolled up
    /// under its parent and never listed, because on a tenant-partitioned store its name is a tenant id.
    /// <c>conparentid = 0</c> already drops the clones; this drops the keys somebody declared on one
    /// partition by hand too, which the browser does not show either.
    /// </para>
    /// <para>
    /// <b>Capped</b> at <c>@cap</c> rows, one more than the caller will keep, so that a database with more
    /// keys than anybody could draw says so rather than timing out or quietly drawing some of them.
    /// </para>
    /// </remarks>
    internal const string ForeignKeysTouchingSql =
        ForeignKeysSelect +
        """

        where con.contype = 'f'
          and con.conparentid = 0
          and not cl.relispartition
          and not fcl.relispartition
          and (ns.nspname = any(@schemas) or fns.nspname = any(@schemas))
        order by ns.nspname, cl.relname, con.conname
        limit @cap
        """;

    /// <summary>
    /// The column list every foreign-key read shares: both ends by name, the columns in constraint order,
    /// the two referential actions, <c>convalidated</c>, and both ends' primary keys.
    /// </summary>
    private const string ForeignKeysSelect =
        """
        select ns.nspname::text,
               cl.relname::text,
               (select pg_catalog.array_agg(a.attname::text order by u.ord)
                  from pg_catalog.unnest(con.conkey) with ordinality as u(attnum, ord)
                  join pg_catalog.pg_attribute a on a.attrelid = con.conrelid and a.attnum = u.attnum),
               fns.nspname::text,
               fcl.relname::text,
               (select pg_catalog.array_agg(a.attname::text order by u.ord)
                  from pg_catalog.unnest(con.confkey) with ordinality as u(attnum, ord)
                  join pg_catalog.pg_attribute a on a.attrelid = con.confrelid and a.attnum = u.attnum),
               con.conname::text,
               con.confdeltype::text,
               con.confupdtype::text,
               con.convalidated,
               (select pg_catalog.array_agg(a.attname::text order by u.ord)
                  from pg_catalog.pg_index i
                  cross join lateral pg_catalog.unnest(i.indkey::pg_catalog.int2[]) with ordinality as u(attnum, ord)
                  join pg_catalog.pg_attribute a on a.attrelid = i.indrelid and a.attnum = u.attnum
                  where i.indrelid = con.conrelid and i.indisprimary and u.ord <= i.indnkeyatts),
               (select pg_catalog.array_agg(a.attname::text order by u.ord)
                  from pg_catalog.pg_index i
                  cross join lateral pg_catalog.unnest(i.indkey::pg_catalog.int2[]) with ordinality as u(attnum, ord)
                  join pg_catalog.pg_attribute a on a.attrelid = i.indrelid and a.attnum = u.attnum
                  where i.indrelid = con.confrelid and i.indisprimary and u.ord <= i.indnkeyatts)
        from pg_catalog.pg_constraint con
        join pg_catalog.pg_class cl on cl.oid = con.conrelid
        join pg_catalog.pg_namespace ns on ns.oid = cl.relnamespace
        join pg_catalog.pg_class fcl on fcl.oid = con.confrelid
        join pg_catalog.pg_namespace fns on fns.oid = fcl.relnamespace
        """;

    /// <summary>How many keys <see cref="ReadForeignKeysTouchingAsync" /> keeps before it says there were more.</summary>
    public const int DefaultForeignKeyCap = 20_000;

    /// <summary>Reads every physical foreign key in the store's schemas.</summary>
    /// <param name="connection">An open connection to the database in scope.</param>
    /// <param name="schemas">The schemas the store owns, from <c>SchemaDeclarationReader</c>.</param>
    /// <param name="commandTimeoutSeconds">The command timeout, from <c>MartenStudioOptions.QueryTimeout</c>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static async Task<IReadOnlyList<PhysicalForeignKey>> ReadForeignKeysAsync(
        NpgsqlConnection connection,
        IReadOnlyList<string> schemas,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(schemas);

        if (schemas.Count == 0)
        {
            return [];
        }

        await using var command = new NpgsqlCommand(ForeignKeysSql, connection)
        {
            CommandTimeout = commandTimeoutSeconds,
        };

        BindSchemas(command, schemas);

        List<PhysicalForeignKey> keys = [];

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            keys.Add(Read(reader));
        }

        return keys;
    }

    /// <summary>
    /// Reads every foreign key with either end in <paramref name="schemas" />, up to <paramref name="cap" />.
    /// </summary>
    /// <param name="connection">An open connection to the database in scope.</param>
    /// <param name="transaction">
    /// The read-only transaction to run in (<c>ReadOnlySqlSession.InTransactionAsync</c>), so the read has
    /// its <c>lock_timeout</c> and runs as <c>SqlConsoleRole</c> like every other catalog read of objects
    /// Marten does not own; <see langword="null" /> only for a caller that has none.
    /// </param>
    /// <param name="schemas">The schemas to read around.</param>
    /// <param name="cap">How many keys to keep.</param>
    /// <param name="commandTimeoutSeconds">The command timeout.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static async Task<PhysicalForeignKeyRead> ReadForeignKeysTouchingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        IReadOnlyList<string> schemas,
        int cap,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(schemas);

        if (schemas.Count == 0)
        {
            return PhysicalForeignKeyRead.Empty;
        }

        int kept = Math.Max(cap, 1);

        await using var command = new NpgsqlCommand(ForeignKeysTouchingSql, connection, transaction)
        {
            CommandTimeout = commandTimeoutSeconds,
        };

        BindSchemas(command, schemas);
        command.Parameters.Add(new NpgsqlParameter("cap", NpgsqlDbType.Integer) { Value = kept + 1 });

        List<PhysicalForeignKey> keys = [];
        bool truncated = false;

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (keys.Count == kept)
            {
                truncated = true;
                break;
            }

            keys.Add(Read(reader));
        }

        return new PhysicalForeignKeyRead(keys, truncated);
    }

    /// <summary>
    /// The bounded inbound count for a pointing table the studio does not map: how many of its rows carry
    /// the given values in the given columns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same bounded shape as <see cref="BuildInboundCount" />, over a relation that is not a Marten
    /// collection - so there is no tenant predicate and no soft-delete predicate to add, because neither
    /// exists: a raw count of a non-Marten table is a count for every tenant at once, which is why only a
    /// visitor the <c>BrowseDatabase</c> row gate admits ever gets one.
    /// </para>
    /// <para>
    /// The schema, table and column names are the catalog's own (the foreign-key read), quoted through
    /// <see cref="SqlIdentifier" />. Each value is bound as text with <see cref="NpgsqlDbType.Unknown" />,
    /// so Postgres applies the column's own input function - a uuid, a bigint, a varchar or a domain are all
    /// compared correctly without a type name ever being written into the statement, and a value the column
    /// cannot hold is a 22P02 for this one count rather than a wrong answer.
    /// </para>
    /// </remarks>
    /// <param name="schema">The pointing table's schema.</param>
    /// <param name="table">The pointing table.</param>
    /// <param name="columns">The columns to match, in the order of <paramref name="values" />.</param>
    /// <param name="values">The values, as text.</param>
    /// <param name="cap">How many rows to look at before stopping.</param>
    /// <param name="commandTimeoutSeconds">The command timeout.</param>
    public static NpgsqlCommand BuildTableInboundCount(
        string schema,
        string table,
        IReadOnlyList<string> columns,
        IReadOnlyList<string> values,
        int cap,
        int commandTimeoutSeconds)
    {
        ArgumentException.ThrowIfNullOrEmpty(schema);
        ArgumentException.ThrowIfNullOrEmpty(table);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(values);

        if (columns.Count == 0 || columns.Count != values.Count)
        {
            throw new ArgumentException("Every column needs exactly one value.", nameof(values));
        }

        var command = new NpgsqlCommand { CommandTimeout = commandTimeoutSeconds };

        try
        {
            command.CommandText = TableInboundCountSql(schema, table, columns);

            for (int i = 0; i < values.Count; i++)
            {
                command.Parameters.Add(new NpgsqlParameter("k" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), NpgsqlDbType.Unknown)
                {
                    Value = values[i],
                });
            }

            command.Parameters.Add(new NpgsqlParameter("cap", NpgsqlDbType.Integer) { Value = Math.Max(cap, 1) });

            return command;
        }
        catch
        {
            command.Dispose();
            throw;
        }
    }

    /// <summary>The text of <see cref="BuildTableInboundCount" />: quoted identifiers, numbered parameters.</summary>
    /// <param name="schema">The pointing table's schema.</param>
    /// <param name="table">The pointing table.</param>
    /// <param name="columns">The columns matched.</param>
    internal static string TableInboundCountSql(string schema, string table, IReadOnlyList<string> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);

        var sql = new System.Text.StringBuilder("select count(*) from (select 1 from ")
            .Append(SqlIdentifier.Quote(schema))
            .Append('.')
            .Append(SqlIdentifier.Quote(table))
            .Append(" where ");

        for (int i = 0; i < columns.Count; i++)
        {
            if (i > 0)
            {
                sql.Append(" and ");
            }

            sql.Append(SqlIdentifier.Quote(columns[i]))
                .Append(" = @k")
                .Append(i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return sql.Append(" limit @cap) as bounded").ToString();
    }

    /// <summary>
    /// The bounded inbound count: how many rows of one collection point at one document.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>select count(*) from (select 1 … limit @cap) x</c> rather than a plain <c>count(*)</c>. The
    /// number is exact where it matters — a person wants to know whether a customer has three orders or
    /// three hundred before they delete it — but the studio must not pay for a sequential scan to say
    /// "lots" about a table with ten million rows, and the foreign key's column is not necessarily
    /// indexed. The cap is what bounds it; above the cap the screen says "1000+".
    /// </para>
    /// <para>
    /// The predicates are the ones the source collection is actually read with elsewhere: the tenant when
    /// the <em>source</em> is conjoined (its tenancy, not the target's — the target may well be a
    /// single-tenanted type, and the rows being counted are the source's), and live rows only when the
    /// source is soft-deleted, because a count that included deleted rows would contradict the list the
    /// row links to.
    /// </para>
    /// </remarks>
    /// <param name="table">The pointing collection, with its physical columns already settled.</param>
    /// <param name="column">The foreign-key column on that collection.</param>
    /// <param name="idDbType">The Postgres type of <paramref name="column"/>, for the parameter.</param>
    /// <param name="id">The already-parsed id value, as <c>DocumentQueryBuilder.ParseId</c> produced it.</param>
    /// <param name="tenantId">The tenant in scope, or <see langword="null"/>.</param>
    /// <param name="cap">How many rows to look at before stopping.</param>
    /// <param name="commandTimeoutSeconds">The command timeout.</param>
    public static NpgsqlCommand BuildInboundCount(
        DocumentTableInfo table,
        string column,
        NpgsqlDbType idDbType,
        object id,
        string? tenantId,
        int cap,
        int commandTimeoutSeconds)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentException.ThrowIfNullOrWhiteSpace(column);
        ArgumentNullException.ThrowIfNull(id);

        var tenantColumn = TenantColumn(table, tenantId);
        var command = DocumentQueryBuilder.NewCommand(commandTimeoutSeconds);

        try
        {
            command.CommandText = InboundCountSql(table, column, tenantColumn);

            command.Parameters.Add(new NpgsqlParameter("id", idDbType) { Value = id });
            command.Parameters.Add(new NpgsqlParameter("cap", NpgsqlDbType.Integer) { Value = Math.Max(cap, 1) });

            if (tenantColumn is not null)
            {
                command.Parameters.Add(new NpgsqlParameter("tenant", NpgsqlDbType.Varchar) { Value = tenantId! });
            }

            return command;
        }
        catch
        {
            command.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The inbound count's text. Every identifier is quoted and comes from Marten's mapping or from
    /// <c>information_schema</c>; the id, the tenant and the cap are parameters.
    /// </summary>
    /// <param name="table">The pointing collection.</param>
    /// <param name="column">The foreign-key column.</param>
    /// <param name="tenantColumn">The tenant column to filter on, or <see langword="null"/>.</param>
    internal static string InboundCountSql(DocumentTableInfo table, string column, string? tenantColumn)
    {
        ArgumentNullException.ThrowIfNull(table);

        var sql = new System.Text.StringBuilder("select count(*) from (select 1 from ")
            .Append(table.QualifiedName)
            .Append(" where ")
            .Append(SqlIdentifier.Quote(column))
            .Append(" = @id");

        if (tenantColumn is not null)
        {
            sql.Append(" and ").Append(SqlIdentifier.Quote(tenantColumn)).Append(" = @tenant");
        }

        if (table.SoftDeleteEnabled &&
            table.MetadataColumnName(DocumentMetadataColumn.IsSoftDeleted) is { } deletedColumn)
        {
            sql.Append(" and ").Append(SqlIdentifier.Quote(deletedColumn)).Append(" = false");
        }

        return sql.Append(" limit @cap) as bounded").ToString();
    }

    /// <summary>The tenant column to filter the inbound count by, or <see langword="null"/>.</summary>
    internal static string? TenantColumn(DocumentTableInfo table, string? tenantId)
    {
        ArgumentNullException.ThrowIfNull(table);

        return tenantId is not null && table.TenancyStyle == JasperFx.MultiTenancy.TenancyStyle.Conjoined
            ? table.MetadataColumnName(DocumentMetadataColumn.TenantId)
            : null;
    }

    /// <summary>
    /// Postgres's one-letter referential action, as <c>Weasel.Postgresql.Tables.CascadeAction</c> spells
    /// it — so a physical-only edge and a declared one read the same on the screen.
    /// </summary>
    internal static string CascadeAction(string? code) => code switch
    {
        "r" => "Restrict",
        "c" => "Cascade",
        "n" => "SetNull",
        "d" => "SetDefault",
        _ => "NoAction",
    };

    private static void BindSchemas(NpgsqlCommand command, IReadOnlyList<string> schemas) =>
        command.Parameters.Add(new NpgsqlParameter("schemas", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            Value = schemas as string[] ?? [.. schemas],
        });

    /// <summary>One row of <see cref="ForeignKeysSelect" />.</summary>
    private static PhysicalForeignKey Read(NpgsqlDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            Names(reader, 2),
            reader.GetString(3),
            reader.GetString(4),
            Names(reader, 5),
            reader.GetString(6),
            CascadeAction(reader.IsDBNull(7) ? null : reader.GetString(7)),
            CascadeAction(reader.IsDBNull(8) ? null : reader.GetString(8)),
            reader.IsDBNull(9) || reader.GetBoolean(9),
            reader.IsDBNull(10) ? null : reader.GetFieldValue<string[]>(10),
            reader.IsDBNull(11) ? null : reader.GetFieldValue<string[]>(11));

    private static string[] Names(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? [] : reader.GetFieldValue<string[]>(ordinal);
}
