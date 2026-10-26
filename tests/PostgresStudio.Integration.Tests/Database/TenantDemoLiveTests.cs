using Microsoft.Extensions.Logging;

using Npgsql;

using PostgresStudio.Internal.Sql;
using PostgresStudio.SampleData;

namespace PostgresStudio.Integration.Tests.Database;

/// <summary>
/// The Postgres Studio sample's demo data, applied the way its host applies it on a server with no Marten: the tenant
/// databases with their one unindexed foreign key, the admin database's directory of them, and the relational demo
/// beside it.
/// </summary>
/// <remarks>
/// <para>
/// The tenant databases have fixed names (<see cref="TenantDemo.TenantNames" />) because the sample's do, so every test
/// here drops them first and the class's tests run one after another, as xunit runs a class. The one other user of those
/// names in this suite is the browser suite's sample host, which creates them beside its own admin database - so both
/// are in collections xunit runs apart from everything else and one after the other (<see cref="TenantDatabases" />).
/// The admin database is each test's own.
/// </para>
/// <para>Moved here from <c>tests/MartenStudio.Integration.Tests</c> (PS-5).</para>
/// </remarks>
/// <param name="postgres">The assembly's Postgres.</param>
[Collection(TenantDatabases.Name)]
public class TenantDemoLiveTests(PostgresFixture postgres)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [PostgresFact]
    public async Task The_first_apply_creates_three_marked_tenant_databases_and_their_directory_and_a_second_writes_nothing()
    {
        string admin = await ResetAsync("tenant_demo_admin_first");
        var log = new ListLogger<TenantDemoLiveTests>();

        TenantDemoResult first = await TenantDemo.ApplyAsync(admin, log, Token);

        first.Tenants.Should().Equal(TenantDemo.TenantNames);
        first.Skipped.Should().BeEmpty();
        first.RowsWritten.Should().BePositive();

        (await DirectoryAsync(admin)).Should().Equal(TenantDemo.TenantNames, "the host lists its tenants from this table");

        foreach (string tenant in TenantDemo.TenantNames)
        {
            (await CommentAsync(tenant)).Should().StartWith(TenantDemo.DatabaseMarker, "{0} is stamped as the demo's", tenant);

            string connection = TenantConnection(admin, tenant);
            TenantDemoSize size = TenantDemo.SizeOf(tenant);

            (await CountAsync(connection, TenantDemo.CustomersTable)).Should().Be(size.Customers);
            (await CountAsync(connection, TenantDemo.OrdersTable)).Should().Be(size.Orders);
            (await CountAsync(connection, TenantDemo.OrderLinesTable)).Should().BeGreaterThanOrEqualTo(size.Orders);
        }

        TenantDemoSize a = TenantDemo.SizeOf("tenant_a");
        TenantDemoSize c = TenantDemo.SizeOf("tenant_c");
        a.Should().NotBe(c, "three sizes, so a list of the tenants is not three identical rows");

        TenantDemoResult second = await TenantDemo.ApplyAsync(admin, log, Token);

        second.RowsWritten.Should().Be(0, "a second start against the same server writes nothing");
        second.Tenants.Should().Equal(TenantDemo.TenantNames);
        log.Entries.Should().NotContain(static x => x.Level >= LogLevel.Information, "the demo's own databases are not skipped");
    }

    [PostgresFact]
    public async Task Exactly_one_foreign_key_in_a_tenant_database_has_no_index_and_it_is_the_one_the_demo_left_on_purpose()
    {
        string admin = await ResetAsync("tenant_demo_admin_fk");

        await TenantDemo.ApplyAsync(admin, new ListLogger<TenantDemoLiveTests>(), Token);

        await using var connection = new NpgsqlConnection(
            new NpgsqlConnectionStringBuilder(TenantConnection(admin, "tenant_a")) { Pooling = false }.ConnectionString);
        await connection.OpenAsync(Token);

        // A foreign key is served by an index whose leading columns are the key's columns, in any order.
        await using var command = new NpgsqlCommand(
            """
            select c.conname
            from pg_catalog.pg_constraint c
            where c.contype = 'f' and c.connamespace = 'public'::regnamespace
              and not exists (
                  select 1 from pg_catalog.pg_index i
                  where i.indrelid = c.conrelid
                    and (string_to_array(i.indkey::text, ' ')::smallint[])[1:cardinality(c.conkey)] @> c.conkey
                    and (string_to_array(i.indkey::text, ' ')::smallint[])[1:cardinality(c.conkey)] <@ c.conkey)
            order by 1
            """,
            connection);

        List<string> unindexed = [];
        await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(Token))
        {
            while (await reader.ReadAsync(Token))
            {
                unindexed.Add(reader.GetString(0));
            }
        }

        unindexed.Should().Equal([TenantDemo.UnindexedForeignKey],
            "order_lines' foreign key is served by its primary key, and orders.customer_id is the deliberate finding");
    }

    [PostgresFact]
    public async Task A_tenant_database_the_demo_did_not_create_is_left_alone_and_not_listed()
    {
        string admin = await ResetAsync("tenant_demo_admin_foreign");

        await ExecuteAsync(postgres.ConnectionString, "create database tenant_b");
        await ExecuteAsync(TenantConnection(admin, "tenant_b"), "create table public.keep_me (id int primary key); insert into public.keep_me values (1)");

        var log = new ListLogger<TenantDemoLiveTests>();
        TenantDemoResult result = await TenantDemo.ApplyAsync(admin, log, Token);

        result.Tenants.Should().Equal("tenant_a", "tenant_c");
        result.Skipped.Should().Equal("tenant_b");
        (await DirectoryAsync(admin)).Should().Equal("tenant_a", "tenant_c");

        string foreign = TenantConnection(admin, "tenant_b");
        (await ScalarAsync<bool>(foreign, "select pg_catalog.to_regclass('public.customers') is null"))
            .Should().BeTrue("nothing is created in a database the demo did not create");
        (await CountAsync(foreign, "keep_me")).Should().Be(1);
        (await CommentAsync("tenant_b")).Should().BeNull("and it is not stamped either");

        log.Entries.Should().ContainSingle(static x => x.Level == LogLevel.Information && x.Message.Contains("'tenant_b' alone", StringComparison.Ordinal));
    }

    [PostgresFact]
    public async Task The_admin_database_gets_quartz_legacy_and_app_settings_and_no_foreign_key_into_a_marten_table_it_does_not_have()
    {
        string admin = await ResetAsync("tenant_demo_admin_relational");

        // The sample's order: the tenant half creates studio_sample, which is what app_settings needs.
        await TenantDemo.ApplyAsync(admin, new ListLogger<TenantDemoLiveTests>(), Token);

        await using var connection = new NpgsqlConnection(admin);
        RelationalDemoResult result = await RelationalDemoSchema.ApplyAsync(connection, Token);

        result.QuartzApplied.Should().BeTrue();
        result.LegacyApplied.Should().BeTrue();
        result.HasAppSettings.Should().BeTrue("studio_sample exists, so the app-settings table is created and seeded in it");
        result.HasCustomerForeignKey.Should().BeFalse("there is no Marten here, so no mt_doc_customer to point at");
        result.RowsWritten.Should().BePositive();

        (await CountAsync(admin, RelationalDemoSchema.AppSettingsTable, RelationalDemoSchema.SampleSchemaName)).Should().BePositive();
        (await ScalarAsync<bool>(admin,
                "select not exists (select 1 from pg_catalog.pg_class where relname like 'mt\\_%')"))
            .Should().BeTrue("the Postgres Studio sample's admin database holds nothing of Marten's");

        (await RelationalDemoSchema.ApplyAsync(connection, Token)).RowsWritten.Should().Be(0, "and applying it again writes nothing");
    }

    /// <summary>Drops the tenant databases and returns a fresh admin database of the test's own.</summary>
    private async Task<string> ResetAsync(string adminDatabase)
    {
        await TenantDatabases.DropAsync(postgres, Token);

        return await postgres.CreateDatabaseAsync(adminDatabase, Token);
    }

    private static string TenantConnection(string admin, string tenant) =>
        new NpgsqlConnectionStringBuilder(admin) { Database = tenant }.ConnectionString;

    private async Task<string?> CommentAsync(string database) =>
        await ScalarAsync<string>(postgres.ConnectionString,
            "select pg_catalog.shobj_description(d.oid, 'pg_database') from pg_catalog.pg_database d where d.datname = @name",
            ("name", database));

    private static async Task<List<string>> DirectoryAsync(string admin)
    {
        await using var connection = new NpgsqlConnection(admin);
        await connection.OpenAsync(Token);
        await using var command = new NpgsqlCommand("select name from studio_sample.tenants order by name", connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(Token);

        List<string> names = [];
        while (await reader.ReadAsync(Token))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static async Task<long> CountAsync(string connectionString, string table, string schema = "public") =>
        await ScalarAsync<long>(connectionString, "select count(*) from " + SqlIdentifier.Qualify(schema, table));

    private static async Task<T?> ScalarAsync<T>(string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        // Unpooled: a pooled connection left in a tenant database would stop the next test's drop.
        await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
        await connection.OpenAsync(Token);
        await using var command = new NpgsqlCommand(sql, connection);

        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        object? result = await command.ExecuteScalarAsync(Token);
        return result is null or DBNull ? default : (T) result;
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
        await connection.OpenAsync(Token);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Token);
    }
}

/// <summary>
/// The collection of every class that uses the sample's fixed tenant database names (<see cref="TenantDemo.TenantNames" />)
/// on the shared container, and the one helper they share.
/// </summary>
/// <remarks>
/// Kept out of the parallel run (<c>DisableParallelization</c>), as the browser suite is: xunit runs such collections after
/// every parallel one, one after the other, so a class here that drops <c>tenant_b</c> never does it under the browser
/// suite's running sample - whose own tenant databases have the same three names.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TenantDatabases
{
    /// <summary>The collection name.</summary>
    public const string Name = "tenant-databases";

    /// <summary>Drops the three tenant databases, ending whatever is attached to them.</summary>
    internal static async Task DropAsync(PostgresFixture postgres, CancellationToken cancellationToken)
    {
        foreach (string tenant in TenantDemo.TenantNames)
        {
            await postgres.DropDatabaseAsync(tenant, cancellationToken);
        }
    }
}

/// <summary>One line a <see cref="ListLogger{T}" /> was given.</summary>
/// <param name="Level">The level it was written at.</param>
/// <param name="Message">The rendered message.</param>
internal sealed record LoggedLine(LogLevel Level, string Message);

/// <summary>
/// A logger that keeps what it is given, for the tests that have to see what the sample's seeder says. Hand-written: the
/// package budget has no mocking library (AGENTS.md hard rule 1).
/// </summary>
/// <typeparam name="T">The category.</typeparam>
internal sealed class ListLogger<T> : ILogger<T>
{
    private readonly List<LoggedLine> entries = [];
    private readonly Lock gate = new();

    /// <summary>Everything logged so far, in order.</summary>
    public IReadOnlyList<LoggedLine> Entries
    {
        get
        {
            lock (gate)
            {
                return [.. entries];
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (gate)
        {
            entries.Add(new LoggedLine(logLevel, formatter(state, exception)));
        }
    }
}
