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
internal sealed record PhysicalForeignKey(
    string Schema,
    string Table,
    IReadOnlyList<string> Columns,
    string LinkedSchema,
    string LinkedTable,
    IReadOnlyList<string> LinkedColumns,
    string Name,
    string OnDelete,
    string OnUpdate);

/// <summary>
/// The two reads behind the Relationships screen: every foreign key Postgres has in the store's schemas,
/// and the bounded "how many of these point at that one document" count.
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
    /// </remarks>
    internal const string ForeignKeysSql =
        """
        select ns.nspname::text,
               cl.relname::text,
               (select array_agg(a.attname::text order by u.ord)
                  from unnest(con.conkey) with ordinality as u(attnum, ord)
                  join pg_catalog.pg_attribute a on a.attrelid = con.conrelid and a.attnum = u.attnum),
               fns.nspname::text,
               fcl.relname::text,
               (select array_agg(a.attname::text order by u.ord)
                  from unnest(con.confkey) with ordinality as u(attnum, ord)
                  join pg_catalog.pg_attribute a on a.attrelid = con.confrelid and a.attnum = u.attnum),
               con.conname::text,
               con.confdeltype::text,
               con.confupdtype::text
        from pg_catalog.pg_constraint con
        join pg_catalog.pg_class cl on cl.oid = con.conrelid
        join pg_catalog.pg_namespace ns on ns.oid = cl.relnamespace
        join pg_catalog.pg_class fcl on fcl.oid = con.confrelid
        join pg_catalog.pg_namespace fns on fns.oid = fcl.relnamespace
        where con.contype = 'f' and con.conparentid = 0 and ns.nspname = any(@schemas)
        order by ns.nspname, cl.relname, con.conname
        """;

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

        List<PhysicalForeignKey> keys = [];

        if (schemas.Count == 0)
        {
            return keys;
        }

        await using var command = new NpgsqlCommand(ForeignKeysSql, connection)
        {
            CommandTimeout = commandTimeoutSeconds,
        };

        command.Parameters.Add(new NpgsqlParameter("schemas", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            Value = schemas as string[] ?? [.. schemas],
        });

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            keys.Add(new PhysicalForeignKey(
                reader.GetString(0),
                reader.GetString(1),
                Names(reader, 2),
                reader.GetString(3),
                reader.GetString(4),
                Names(reader, 5),
                reader.GetString(6),
                CascadeAction(reader.IsDBNull(7) ? null : reader.GetString(7)),
                CascadeAction(reader.IsDBNull(8) ? null : reader.GetString(8))));
        }

        return keys;
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

    private static string[] Names(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? [] : reader.GetFieldValue<string[]>(ordinal);
}
