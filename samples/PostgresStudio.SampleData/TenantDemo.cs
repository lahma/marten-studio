using Microsoft.Extensions.Logging;

using Npgsql;

using NpgsqlTypes;

namespace PostgresStudio.SampleData;

/// <summary>What one <see cref="TenantDemo.ApplyAsync" /> found and did.</summary>
/// <param name="Tenants">The tenants whose databases are the demo's and are in the directory, in name order.</param>
/// <param name="Skipped">
/// The tenants left alone: a database of that name exists and the demo did not create it, or the role may not create
/// databases.
/// </param>
/// <param name="RowsWritten">
/// Rows the seeding inserted, as Postgres counted them. Zero when every table already had its rows, which is what a
/// second run against the same server reports.
/// </param>
public readonly record struct TenantDemoResult(IReadOnlyList<string> Tenants, IReadOnlyList<string> Skipped, int RowsWritten);

/// <summary>
/// The Postgres Studio sample's other half, the shape of a host that keeps one database per tenant beside an admin
/// database (issue #8's host B): three tenant databases on the admin database's server, each with a small order book,
/// and a directory of them in the admin database that the host lists them from.
/// </summary>
/// <remarks>
/// <para>
/// <b>Each tenant database.</b> <c>customers</c>, <c>orders</c> and <c>order_lines</c> in <c>public</c>, filled from
/// <c>generate_series</c>, so every value is a function of a row number and two runs describe the same data. Two
/// foreign keys: <c>order_lines</c> to <c>orders</c>, covered by its primary key, and <c>orders.customer_id</c> to
/// <c>customers</c> (<see cref="UnindexedForeignKey" />), which has <b>no index on purpose</b> - deleting a customer
/// scans every order - so the studio's health checks and lints have one real finding to show. The three tenants are
/// three sizes, so a list of them is not three identical rows.
/// </para>
/// <para>
/// <b>The directory.</b> <c>studio_sample.tenants</c> in the admin database: one row per tenant, naming its database.
/// The sample's <c>IPostgresDatabaseSource</c> reads it to list the tenants, as host B's own list does, so a tenant
/// added there appears in the studio after the picker's refresh. <c>studio_sample</c> is created here, which is also
/// what gives the relational demo's <c>app_settings</c> a schema to live in on a database with no Marten store.
/// </para>
/// <para>
/// <b>Never into a database it did not create.</b> <c>tenant_a</c> is an ordinary name, and a configured server may
/// already have one. So a database the demo creates is stamped with a comment beginning with
/// <see cref="DatabaseMarker" />, and one that exists without it is left alone - nothing created in it, nothing
/// written, not listed - and the skip is logged at Information, as the relational demo does for its schemas. A role
/// that may not create databases is a skip too, logged at Warning, so the sample still starts with what it has.
/// </para>
/// <para>
/// <b>Idempotent.</b> Every table is created only if it is missing and filled only while it is empty, the databases
/// are created under a session advisory lock (<c>CREATE DATABASE</c> cannot run in a transaction) and each one is
/// seeded in one transaction under a transaction-scoped one, so two sample hosts started at once on one server neither
/// race each other's <c>CREATE</c> nor fill a table twice.
/// </para>
/// </remarks>
public static class TenantDemo
{
    /// <summary>The tenants the demo creates a database for, in the order they are listed.</summary>
    public static IReadOnlyList<string> TenantNames { get; } = ["tenant_a", "tenant_b", "tenant_c"];

    /// <summary>
    /// What the comment on a tenant database the demo created begins with. The demo writes into a tenant database only
    /// when it created it, or its comment begins with this.
    /// </summary>
    public const string DatabaseMarker = "postgres-studio-sample:tenant";

    /// <summary>The admin database's directory of tenants, in <see cref="RelationalDemoSchema.SampleSchemaName" />.</summary>
    public const string DirectoryTable = "tenants";

    /// <summary>A tenant's customers.</summary>
    public const string CustomersTable = "customers";

    /// <summary>A tenant's orders, each pointing at a customer through <see cref="UnindexedForeignKey" />.</summary>
    public const string OrdersTable = "orders";

    /// <summary>A tenant's order lines, whose foreign key to their order is covered by their primary key.</summary>
    public const string OrderLinesTable = "order_lines";

    /// <summary>The one foreign key in a tenant database with no index to serve it, deliberately.</summary>
    public const string UnindexedForeignKey = "orders_customer_id_fkey";

    /// <summary>
    /// Serialises the databases' creation across hosts started at once on one server. A session lock, because
    /// <c>CREATE DATABASE</c> cannot run inside a transaction; a different number from the relational demo's.
    /// </summary>
    private const long CreateLockKey = 0x5053_5F54_454E_4344;

    /// <summary>Serialises the seeding of one tenant database, inside its transaction.</summary>
    private const long SeedLockKey = 0x5053_5F54_454E_5344;

    /// <summary>The comment the demo puts on a tenant database it created.</summary>
    private const string DatabaseComment =
        DatabaseMarker + " - A tenant database the Postgres Studio sample created beside its admin database. Its tables are demo data.";

    /// <summary>
    /// Creates the directory in the admin database, the tenant databases on its server, and their tables and rows -
    /// leaving any database the demo did not create alone.
    /// </summary>
    /// <param name="adminConnectionString">
    /// The admin database, with the credentials the tenant databases are created and seeded with: the same server, the
    /// same role.
    /// </param>
    /// <param name="logger">Where a skipped tenant is reported.</param>
    /// <param name="cancellationToken">Cancels the apply. A tenant already seeded stays seeded; the rest roll back.</param>
    /// <returns>Which tenants are the demo's, which were left alone, and how many rows were written.</returns>
    public static async Task<TenantDemoResult> ApplyAsync(
        string adminConnectionString,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adminConnectionString);
        ArgumentNullException.ThrowIfNull(logger);

        // Unpooled: a seeding run is a one-off, and an idle pooled connection left in a tenant database would be one
        // more session for the studio to show, and would hold up a `drop database` of it.
        await using var admin = new NpgsqlConnection(Unpooled(adminConnectionString, database: null));
        await admin.OpenAsync(cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(admin, null, DirectoryScript, cancellationToken).ConfigureAwait(false);

        List<string> owned = [];
        List<string> skipped = [];

        await using (var take = new NpgsqlCommand("select pg_catalog.pg_advisory_lock(@key)", admin))
        {
            take.Parameters.Add(new NpgsqlParameter("key", NpgsqlDbType.Bigint) { Value = CreateLockKey });
            await take.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            foreach (string tenant in TenantNames)
            {
                if (await EnsureDatabaseAsync(admin, tenant, logger, cancellationToken).ConfigureAwait(false))
                {
                    owned.Add(tenant);
                }
                else
                {
                    skipped.Add(tenant);
                }
            }
        }
        finally
        {
            // Not under the caller's token: a lock left behind would outlive this apply on a pooled connection.
            await using var release = new NpgsqlCommand("select pg_catalog.pg_advisory_unlock(@key)", admin);
            release.Parameters.Add(new NpgsqlParameter("key", NpgsqlDbType.Bigint) { Value = CreateLockKey });
            await release.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }

        int rows = 0;

        for (int i = 0; i < owned.Count; i++)
        {
            string tenant = owned[i];

            await using var connection = new NpgsqlConnection(Unpooled(adminConnectionString, tenant));
            rows += await SeedTenantAsync(connection, tenant, SizeOf(tenant), cancellationToken).ConfigureAwait(false);
        }

        await using (var directory = new NpgsqlCommand(
            $"""
            insert into {RelationalDemoSchema.SampleSchemaName}.{DirectoryTable} (name, database_name, display_name, created_at)
            select x.name, x.name, x.display_name, timestamptz '2026-01-05 09:00:00+00' + make_interval(days => x.n::int)
            from unnest(@names, @displays) with ordinality as x(name, display_name, n)
            on conflict (name) do nothing
            """,
            admin))
        {
            directory.Parameters.Add(new NpgsqlParameter("names", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = owned.ToArray() });
            directory.Parameters.Add(new NpgsqlParameter("displays", NpgsqlDbType.Array | NpgsqlDbType.Text)
            {
                Value = owned.Select(DisplayNameOf).ToArray(),
            });

            rows += await directory.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return new TenantDemoResult(owned, skipped, rows);
    }

    /// <summary>
    /// Creates a tenant's tables if they are missing and fills each that is empty, in one transaction. Public so that a
    /// test can seed a database of its own naming the way the sample seeds a tenant.
    /// </summary>
    /// <param name="connection">
    /// A connection to the tenant database. Opened if it is not open yet; never closed or disposed here.
    /// </param>
    /// <param name="tenant">The tenant's name, which its order references and e-mail addresses carry.</param>
    /// <param name="size">How many customers and orders to write into empty tables.</param>
    /// <param name="cancellationToken">Cancels the seeding, which then rolls back as a whole.</param>
    /// <returns>How many rows were written: zero when every table already had rows.</returns>
    public static async Task<int> SeedTenantAsync(
        NpgsqlConnection connection,
        string tenant,
        TenantDemoSize size,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);

        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        int rows = 0;

        await using (NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            await using (var take = new NpgsqlCommand("select pg_catalog.pg_advisory_xact_lock(@key)", connection, transaction))
            {
                take.Parameters.Add(new NpgsqlParameter("key", NpgsqlDbType.Bigint) { Value = SeedLockKey });
                await take.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await ExecuteAsync(connection, transaction, TenantScript, cancellationToken).ConfigureAwait(false);

            if (!await DemoTable.HasRowsAsync(connection, transaction, "public", CustomersTable, cancellationToken).ConfigureAwait(false))
            {
                rows += await FillAsync(connection, transaction, CustomersFill, tenant, size, cancellationToken).ConfigureAwait(false);
            }

            if (!await DemoTable.HasRowsAsync(connection, transaction, "public", OrdersTable, cancellationToken).ConfigureAwait(false))
            {
                rows += await FillAsync(connection, transaction, OrdersFill, tenant, size, cancellationToken).ConfigureAwait(false);
            }

            if (!await DemoTable.HasRowsAsync(connection, transaction, "public", OrderLinesTable, cancellationToken).ConfigureAwait(false))
            {
                rows += await FillAsync(connection, transaction, OrderLinesFill, tenant, size, cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (rows > 0)
        {
            // Planner statistics for tables nobody has let autovacuum reach yet, so the studio shows row estimates rather
            // than "not analyzed" on a demo that was seeded a second ago.
            foreach (string table in (string[]) [CustomersTable, OrdersTable, OrderLinesTable])
            {
                await using var analyze = new NpgsqlCommand("analyze " + DemoDataSql.QuoteQualified("public", table), connection);
                await analyze.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        return rows;
    }

    /// <summary>How big a tenant's order book is: the three are three sizes, so a list of them shows a difference.</summary>
    /// <param name="tenant">One of <see cref="TenantNames" />.</param>
    public static TenantDemoSize SizeOf(string tenant) => tenant switch
    {
        "tenant_a" => new TenantDemoSize(40, 160),
        "tenant_b" => new TenantDemoSize(20, 60),
        _ => new TenantDemoSize(6, 12),
    };

    private static string DisplayNameOf(string tenant) => tenant switch
    {
        "tenant_a" => "Tenant A (Helsinki)",
        "tenant_b" => "Tenant B (Tampere)",
        "tenant_c" => "Tenant C (Oulu)",
        _ => tenant,
    };

    /// <summary>
    /// Whether <paramref name="tenant" />'s database is the demo's to write into: created now, or created earlier and
    /// carrying <see cref="DatabaseMarker" />.
    /// </summary>
    private static async Task<bool> EnsureDatabaseAsync(
        NpgsqlConnection admin,
        string tenant,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        string? comment;
        bool exists;

        await using (var find = new NpgsqlCommand(
            "select pg_catalog.shobj_description(d.oid, 'pg_database') from pg_catalog.pg_database d where d.datname = @name",
            admin))
        {
            find.Parameters.Add(new NpgsqlParameter("name", NpgsqlDbType.Text) { Value = tenant });

            await using NpgsqlDataReader reader = await find.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            exists = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            comment = exists && !await reader.IsDBNullAsync(0, cancellationToken).ConfigureAwait(false) ? reader.GetString(0) : null;
        }

        if (exists)
        {
            if (comment is not null && comment.StartsWith(DatabaseMarker, StringComparison.Ordinal))
            {
                return true;
            }

            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation(
                    "The Postgres Studio sample left the existing database '{Database}' alone: it was not created by the sample - "
                    + "its comment does not begin with '{Marker}' - so nothing was written to it and it is not listed as a tenant. "
                    + "Comment found: {Comment}",
                    tenant,
                    DatabaseMarker,
                    comment ?? "(none)");
            }

            return false;
        }

        try
        {
            await using var create = new NpgsqlCommand("create database " + DemoDataSql.Quote(tenant), admin);
            await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        {
            if (logger.IsEnabled(LogLevel.Warning))
            {
                logger.LogWarning(
                    "The Postgres Studio sample could not create the tenant database '{Database}': the role it connects as may "
                    + "not create databases (CREATEDB). The tenants source lists only the tenant databases that exist. {Reason}",
                    tenant,
                    exception.MessageText);
            }

            return false;
        }

        await using (var stamp = new NpgsqlCommand(
            "comment on database " + DemoDataSql.Quote(tenant) + " is " + Literal(DatabaseComment),
            admin))
        {
            await stamp.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    private static async Task<int> FillAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        string tenant,
        TenantDemoSize size,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter("tenant", NpgsqlDbType.Text) { Value = tenant });
        command.Parameters.Add(new NpgsqlParameter("customers", NpgsqlDbType.Integer) { Value = size.Customers });
        command.Parameters.Add(new NpgsqlParameter("orders", NpgsqlDbType.Integer) { Value = size.Orders });

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string script,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(script, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The admin connection string without pooling, pointed at <paramref name="database" /> when one is named.</summary>
    private static string Unpooled(string adminConnectionString, string? database)
    {
        var builder = new NpgsqlConnectionStringBuilder(adminConnectionString) { Pooling = false };

        if (database is not null)
        {
            builder.Database = database;
        }

        return builder.ConnectionString;
    }

    /// <summary>
    /// A SQL string literal of one of this class's own constants. <c>COMMENT ON</c> takes no parameter, so the text is
    /// written in; it is never anything typed, and its quotes are doubled anyway.
    /// </summary>
    private static string Literal(string text) => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";

    /// <summary>The admin database's directory of tenants, and the schema it lives in.</summary>
    private const string DirectoryScript =
        """
        CREATE SCHEMA IF NOT EXISTS studio_sample;

        CREATE TABLE IF NOT EXISTS studio_sample.tenants
        (
            name          text        PRIMARY KEY CHECK (name ~ '^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$'),
            database_name text        NOT NULL UNIQUE,
            display_name  text        NOT NULL,
            created_at    timestamptz NOT NULL
        );

        COMMENT ON TABLE studio_sample.tenants IS
            'The tenant directory: one row per tenant database. The Postgres Studio sample''s IPostgresDatabaseSource lists the tenants from it.';
        """;

    /// <summary>A tenant database's tables. The foreign key from orders to customers has no index, on purpose.</summary>
    private const string TenantScript =
        """
        CREATE TABLE IF NOT EXISTS public.customers
        (
            id         bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            name       text        NOT NULL,
            email      text        NOT NULL UNIQUE,
            country    char(2)     NOT NULL,
            created_at timestamptz NOT NULL
        );

        COMMENT ON TABLE public.customers IS 'A tenant''s customers. Postgres Studio sample data.';

        CREATE TABLE IF NOT EXISTS public.orders
        (
            id          bigint        GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            customer_id bigint        NOT NULL REFERENCES public.customers (id),
            reference   text          NOT NULL UNIQUE,
            status      text          NOT NULL CHECK (status IN ('placed', 'shipped', 'cancelled')),
            total       numeric(12,2) NOT NULL,
            placed_at   timestamptz   NOT NULL
        );

        CREATE INDEX IF NOT EXISTS orders_placed_at_idx ON public.orders (placed_at);

        COMMENT ON TABLE public.orders IS 'A tenant''s orders. Postgres Studio sample data.';
        COMMENT ON COLUMN public.orders.customer_id IS
            'Deliberately not indexed: deleting or re-keying a customer scans every order. The one finding the sample leaves for the studio''s checks.';

        CREATE TABLE IF NOT EXISTS public.order_lines
        (
            order_id   bigint        NOT NULL REFERENCES public.orders (id) ON DELETE CASCADE,
            line_no    integer       NOT NULL,
            sku        text          NOT NULL,
            quantity   integer       NOT NULL CHECK (quantity > 0),
            unit_price numeric(12,2) NOT NULL,
            PRIMARY KEY (order_id, line_no)
        );

        COMMENT ON TABLE public.order_lines IS 'An order''s lines. Their foreign key is served by the primary key. Postgres Studio sample data.';
        """;

    private const string CustomersFill =
        """
        insert into public.customers (name, email, country, created_at)
        select 'Customer ' || lpad(g::text, 3, '0'),
               @tenant || '.customer' || lpad(g::text, 3, '0') || '@example.com',
               (array['FI', 'SE', 'EE', 'DE'])[1 + g % 4],
               timestamptz '2026-01-01 00:00:00+00' + make_interval(days => g)
        from generate_series(1, @customers) as g
        """;

    private const string OrdersFill =
        """
        insert into public.orders (customer_id, reference, status, total, placed_at)
        select c.id,
               upper(@tenant) || '-' || lpad(g::text, 5, '0'),
               (array['placed', 'shipped', 'cancelled'])[1 + g % 3],
               round(19.90 + g * 3.15, 2),
               timestamptz '2026-02-01 08:00:00+00' + make_interval(hours => g * 5)
        from generate_series(1, @orders) as g
        join public.customers c on c.email = @tenant || '.customer' || lpad((1 + (g * 7) % @customers)::text, 3, '0') || '@example.com'
        """;

    private const string OrderLinesFill =
        """
        insert into public.order_lines (order_id, line_no, sku, quantity, unit_price)
        select o.id,
               l,
               'SKU-' || lpad(((o.id * 3 + l) % 50 + 1)::text, 3, '0'),
               1 + (o.id + l) % 4,
               round(4.50 + ((o.id + l) % 20) * 1.25, 2)
        from public.orders o
        cross join lateral generate_series(1, 1 + (o.id % 3)::int) as l
        """;
}

/// <summary>How many customers and orders one tenant database is seeded with.</summary>
/// <param name="Customers">Customers, numbered from one.</param>
/// <param name="Orders">Orders, each for one of the customers.</param>
public readonly record struct TenantDemoSize(int Customers, int Orders);
