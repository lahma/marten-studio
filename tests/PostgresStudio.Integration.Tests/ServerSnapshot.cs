using Npgsql;

using PostgresStudio.Internal.Sql;

namespace PostgresStudio.Integration.Tests;

/// <summary>
/// Everything a read path could change in databases a test owns: the databases themselves, every object in every
/// non-system schema of them, and every row of every table and every sequence's position.
/// </summary>
/// <remarks>
/// <para>
/// <b>Objects</b> are read as <c>MartenStudio.Integration.Tests</c>' <c>ServerSnapshot</c> reads them - each schema,
/// relation (with its oid, owner, grants, storage options, columns and index definition), constraint, trigger, routine (by
/// source digest), type, policy, extension and comment - so DDL of any kind, a table dropped and created again under its
/// own name included, reads as a change.
/// </para>
/// <para>
/// <b>Rows</b> are what the Marten suite's snapshot does not read, because PostgresStudio's hard-rule-14 analogue is "no
/// DDL and no rows". Every ordinary table and materialized view is read whole as a count and a digest of each row's
/// <c>xmin</c> and <c>ctid</c>: an insert, a delete, and an update that leaves every value as it was all move one or the
/// other. A sequence is read by its <c>last_value</c> and <c>is_called</c>, so a <c>nextval</c> is a change.
/// </para>
/// <para>
/// Read on unpooled connections of the test's own, so nothing of the studio's is in the measurement and no pooled
/// connection outlives a database the test drops. The databases are the test's own, so nothing else on the shared
/// container writes to them while it runs. <c>ServerSnapshotLiveTests</c> is this type's anti-vacuity: each part is seen
/// to move.
/// </para>
/// </remarks>
internal sealed record ServerSnapshot(IReadOnlyList<string> Databases, IReadOnlyList<string> Objects, IReadOnlyList<string> Rows)
{
    private const string ObjectsSql =
        "with schemas as (select oid, nspname from pg_namespace where nspname <> 'information_schema' and nspname !~ '^pg_') "
        + "select 'schema ' || n.nspname || ' ' || n.oid::text || ' owner=' || pg_get_userbyid(n.nspowner) "
        + "|| ' acl=' || coalesce(n.nspacl::text, '') "
        + "from pg_namespace n join schemas s on s.oid = n.oid "
        + "union all "
        + "select 'relation ' || s.nspname || '.' || c.relname || ' ' || c.relkind::text || ' ' || c.oid::text "
        + "|| ' owner=' || pg_get_userbyid(c.relowner) "
        + "|| ' acl=' || coalesce(c.relacl::text, '') "
        + "|| ' options=' || coalesce(array_to_string(c.reloptions, ','), '') "
        + "|| ' bound=' || coalesce(pg_get_expr(c.relpartbound, c.oid), '') "
        + "|| ' index=' || case when c.relkind in ('i', 'I') then pg_get_indexdef(c.oid) else '' end "
        + "|| ' columns=' || coalesce((select string_agg(a.attname || ':' || format_type(a.atttypid, a.atttypmod) "
        + "|| case when a.attnotnull then ' not null' else '' end "
        + "|| coalesce(' default ' || pg_get_expr(d.adbin, d.adrelid), ''), ',' order by a.attnum) "
        + "from pg_attribute a left join pg_attrdef d on d.adrelid = a.attrelid and d.adnum = a.attnum "
        + "where a.attrelid = c.oid and a.attnum > 0 and not a.attisdropped), '') "
        + "from pg_class c join schemas s on s.oid = c.relnamespace "
        + "union all "
        + "select 'constraint ' || s.nspname || '.' || coalesce(r.relname, '-') || '.' || k.conname || ' ' || k.oid::text "
        + "|| ' ' || pg_get_constraintdef(k.oid) "
        + "from pg_constraint k join schemas s on s.oid = k.connamespace left join pg_class r on r.oid = k.conrelid "
        + "union all "
        + "select 'trigger ' || s.nspname || '.' || r.relname || '.' || t.tgname || ' ' || t.oid::text || ' ' "
        + "|| t.tgenabled::text || ' ' || pg_get_triggerdef(t.oid) "
        + "from pg_trigger t join pg_class r on r.oid = t.tgrelid join schemas s on s.oid = r.relnamespace "
        + "where not t.tgisinternal "
        + "union all "
        + "select 'routine ' || s.nspname || '.' || p.proname || '(' || pg_get_function_identity_arguments(p.oid) || ') ' "
        + "|| p.oid::text || ' ' || md5(coalesce(p.prosrc, '')) || ' acl=' || coalesce(p.proacl::text, '') "
        + "from pg_proc p join schemas s on s.oid = p.pronamespace "
        + "union all "
        + "select 'type ' || s.nspname || '.' || t.typname || ' ' || t.typtype::text || ' ' || t.oid::text "
        + "from pg_type t join schemas s on s.oid = t.typnamespace where t.typtype in ('d', 'e', 'r', 'm') "
        + "union all "
        + "select 'policy ' || s.nspname || '.' || r.relname || '.' || p.polname || ' ' || p.oid::text "
        + "from pg_policy p join pg_class r on r.oid = p.polrelid join schemas s on s.oid = r.relnamespace "
        + "union all "
        + "select 'extension ' || e.extname || ' ' || e.extversion from pg_extension e "
        + "union all "
        + "select 'comment ' || d.classoid::regclass::text || ' ' || d.objoid::text || ' ' || d.objsubid::text "
        + "|| ' ' || md5(d.description) "
        + "from pg_description d where d.objoid >= 16384 "
        + "order by 1";

    /// <summary>
    /// Every table, materialized view and sequence whose contents the snapshot reads: schema, name, kind, and whether it
    /// can be read - a materialized view created <c>with no data</c> cannot, and a refresh that populated it is a change.
    /// </summary>
    private const string ContentsSql =
        "select n.nspname, c.relname, c.relkind::text, c.relispopulated from pg_class c join pg_namespace n on n.oid = c.relnamespace "
        + "where c.relkind in ('r', 'm', 'S') and n.nspname <> 'information_schema' and n.nspname !~ '^pg_' "
        + "and c.relpersistence <> 't' order by 1, 2";

    /// <summary>Reads the snapshot.</summary>
    /// <param name="databases">The connection string of each database the test owns; every non-system schema is read.</param>
    /// <param name="databasePrefix">The prefix of the server's databases to list, so a created database is a change.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    public static async Task<ServerSnapshot> ReadAsync(
        IReadOnlyList<string> databases,
        string databasePrefix,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(databases);

        List<string> objects = [];
        List<string> rows = [];
        IReadOnlyList<string> databaseNames = [];

        for (int i = 0; i < databases.Count; i++)
        {
            await using var connection = new NpgsqlConnection(Unpooled(databases[i]));
            await connection.OpenAsync(cancellationToken);

            string name = connection.Database;

            foreach (string line in await LinesAsync(connection, ObjectsSql, cancellationToken))
            {
                objects.Add(name + ": " + line);
            }

            List<(string Schema, string Relation, string Kind, bool Populated)> relations = [];
            await using (var command = new NpgsqlCommand(ContentsSql, connection))
            await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    relations.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3)));
                }
            }

            foreach ((string schema, string relation, string kind, bool populated) in relations)
            {
                if (!populated)
                {
                    rows.Add(name + ": " + schema + "." + relation + " not populated");
                    continue;
                }

                string qualified = SqlIdentifier.Qualify(schema, relation);
                string sql = kind == "S"
                    ? "select 'sequence last_value=' || last_value::text || ' is_called=' || is_called::text from " + qualified
                    : "select 'rows=' || count(*)::text || ' ' || coalesce(md5(string_agg(xmin::text || ctid::text, ',' order by ctid)), '-') from " + qualified;

                foreach (string line in await LinesAsync(connection, sql, cancellationToken))
                {
                    rows.Add(name + ": " + schema + "." + relation + " " + line);
                }
            }

            if (i == 0)
            {
                await using var list = new NpgsqlCommand(
                    "select datname from pg_database where starts_with(datname, @prefix) order by datname", connection);
                list.Parameters.AddWithValue("prefix", databasePrefix);

                List<string> names = [];
                await using NpgsqlDataReader reader = await list.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    names.Add(reader.GetString(0));
                }

                databaseNames = names;
            }
        }

        return new ServerSnapshot(databaseNames, objects, rows);
    }

    /// <summary>Asserts nothing in <paramref name="after" /> differs from this one, part by part, so a failure says which.</summary>
    public void ShouldBeUnchangedIn(ServerSnapshot after)
    {
        ArgumentNullException.ThrowIfNull(after);

        after.Databases.Should().Equal(Databases, "a read path creates no database");
        after.Objects.Should().Equal(Objects, "a read path creates, alters, recreates or drops no schema object");
        after.Rows.Should().Equal(Rows, "a read path writes no row and moves no sequence");
    }

    /// <summary>An unpooled copy of a connection string, so no idle connection outlives a database a test drops.</summary>
    public static string Unpooled(string connectionString) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString;

    private static async Task<List<string>> LinesAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);

        List<string> lines = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            lines.Add(reader.GetString(0));
        }

        return lines;
    }
}

/// <summary>Databases a test owns on the shared container, created empty and dropped when it is done.</summary>
internal sealed class OwnedDatabases : IAsyncDisposable
{
    private readonly PostgresFixture postgres;
    private readonly IReadOnlyList<string> names;

    private OwnedDatabases(PostgresFixture postgres, IReadOnlyList<string> names, IReadOnlyList<string> connectionStrings)
    {
        this.postgres = postgres;
        this.names = names;
        ConnectionStrings = connectionStrings;
    }

    /// <summary>Each database's connection string, in the order the names were given.</summary>
    public IReadOnlyList<string> ConnectionStrings { get; }

    /// <summary>The connection string of the <paramref name="index" />th database.</summary>
    public string this[int index] => ConnectionStrings[index];

    /// <summary>Creates the databases, dropping any a previous run left behind first.</summary>
    public static async Task<OwnedDatabases> CreateAsync(PostgresFixture postgres, IReadOnlyList<string> names, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(postgres);
        ArgumentNullException.ThrowIfNull(names);

        List<string> connectionStrings = [];
        foreach (string name in names)
        {
            connectionStrings.Add(await postgres.CreateDatabaseAsync(name, cancellationToken));
        }

        return new OwnedDatabases(postgres, names, connectionStrings);
    }

    /// <summary>Runs statements on one of the databases, over an unpooled connection of the test's own.</summary>
    public static async Task ExecuteAsync(string connectionString, string sql, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(ServerSnapshot.Unpooled(connectionString));
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 300 };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Drops every database, ending whatever is still attached to it.</summary>
    public async ValueTask DisposeAsync()
    {
        foreach (string name in names)
        {
            await postgres.DropDatabaseAsync(name, CancellationToken.None);
        }
    }
}
