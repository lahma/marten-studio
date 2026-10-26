using Marten;

using MartenStudio.Integration.Tests.Browser;
using MartenStudio.Integration.Tests.Health;
using MartenStudio.SampleDomain;

using Npgsql;

namespace MartenStudio.Integration.Tests;

/// <summary>
/// The database a reused container hands the Marten sample: judged before Marten touches it, and dropped so it can be
/// seeded again only when asked.
/// </summary>
/// <remarks>
/// <para>
/// <b>The stale shape is the one that was hit.</b> <see cref="StaleSampleShape" /> rebuilds, in a database of its own,
/// what a container seeded on 2026-09-14 still held on 2026-10-20: a seed marker at version 2, invoices from before
/// <c>Invoice</c> had a foreign key to its customer - so no <c>customer_id</c> column and no constraint - and no
/// customers left. The first test is the anti-vacuity half: that shape still stops today's seeder with
/// <c>23503</c> on the invoice table, exactly as a plain <c>dotnet run</c> did, so the rest prove a fix for the real
/// failure rather than for a shape nobody meets.
/// </para>
/// <para>
/// <b>Databases, not schemas</b>, because the sample's schema names are constants, as
/// <see cref="Database.RelationalDemoFixture" /> explains; every test makes its own and drops it first.
/// </para>
/// </remarks>
/// <param name="postgres">The assembly's Postgres.</param>
public class SampleDatabaseLiveTests(PostgresFixture postgres)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [PostgresFact]
    public async Task The_stale_shape_still_stops_the_seeder_with_the_23503_a_reused_container_was_reported_with()
    {
        string connectionString = await StaleSampleShape.CreateAsync(postgres, "sample_db_stale_fails");

        using DocumentStore store = DocumentStore.For(options => SampleStore.Configure(options, connectionString));

        Func<Task> seed = () => new SampleDataSeeder().Populate(store, Token);

        Exception thrown = (await seed.Should().ThrowAsync<Exception>()).Which;
        PostgresException? cause = Unwrap(thrown);

        cause.Should().NotBeNull("Marten wraps the server's error, and the server's error is the point: " + thrown);
        cause!.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation);
        cause.TableName.Should().Be("mt_doc_invoice",
            "adding mt_doc_invoice.customer_id backfills it from the JSON, and the foreign key then finds invoices whose customers are gone");
    }

    [PostgresFact]
    public async Task A_database_nothing_seeded_is_empty_and_one_this_build_seeded_is_current()
    {
        string connectionString = await BrowserSuiteFixture.CreateDatabaseAsync(postgres, "sample_db_current");

        (await SampleDatabase.InspectAsync(connectionString, Token))
            .Should().Be(new SampleDatabaseInspection(SampleDatabaseState.Empty, null));

        await using (MartenFixture marten = await MartenFixture.CreateAsync(connectionString, SampleStore.DocumentSchema))
        {
            await marten.SeedSampleDataAsync(Token);
        }

        SampleDatabaseInspection seeded = await SampleDatabase.InspectAsync(connectionString, Token);

        seeded.Should().Be(new SampleDatabaseInspection(SampleDatabaseState.Current, SampleDataSeeder.SeedVersion),
            "the marker Marten wrote is read back from " + SampleDatabase.MarkerTable + " without Marten");
        seeded.NeedsReseed.Should().BeFalse();

        // Nothing is dropped from a database that does not need it, even when asked.
        (await SampleDatabase.ReseedAsync(connectionString, Token)).NeedsReseed.Should().BeFalse();
        (await CustomersAsync(connectionString)).Should().Be(SampleDataSeeder.CustomerCount, "the drop is judged again under its lock");
    }

    [PostgresFact]
    public async Task A_stale_database_is_dropped_and_then_seeded_from_nothing_in_this_builds_shape()
    {
        string connectionString = await StaleSampleShape.CreateAsync(postgres, "sample_db_stale_reseeded");

        SampleDatabaseInspection found = await SampleDatabase.InspectAsync(connectionString, Token);

        found.Should().Be(new SampleDatabaseInspection(SampleDatabaseState.Stale, StaleSampleShape.SeedVersion));
        found.NeedsReseed.Should().BeTrue();

        (await SampleDatabase.ReseedAsync(connectionString, Token)).Should().Be(found, "what was dropped is what was found");

        foreach (string schema in SampleDatabase.Schemas)
        {
            (await LiveWait.ScalarAsync<bool>(connectionString, "select pg_catalog.to_regnamespace(@schema) is null", ("schema", schema)))
                .Should().BeTrue("{0} is dropped with everything in it", schema);
        }

        (await LiveWait.ScalarAsync<bool>(connectionString, "select pg_catalog.to_regnamespace('quartz') is not null"))
            .Should().BeTrue("the relational demo's schemas are idempotent and are left alone");

        (await SampleDatabase.InspectAsync(connectionString, Token)).State.Should().Be(SampleDatabaseState.Empty);

        // A store of this build's, as a restarted host builds one: it migrates nothing, because there is nothing left.
        using (DocumentStore store = DocumentStore.For(options => SampleStore.Configure(options, connectionString)))
        {
            await new SampleDataSeeder().Populate(store, Token);
        }

        (await SampleDatabase.InspectAsync(connectionString, Token)).State.Should().Be(SampleDatabaseState.Current);
        (await CustomersAsync(connectionString)).Should().Be(SampleDataSeeder.CustomerCount);
        (await ForeignKeysOnAsync(connectionString, "studio_sample.mt_doc_invoice")).Should().Be(1, "this build's invoice table has its foreign key");
        (await ForeignKeysOnAsync(connectionString, "legacy.customer_credit")).Should().Be(1,
            "the drop took customer_credit's foreign key with mt_doc_customer, and the relational demo put it back");
    }

    [PostgresFact]
    public async Task Rows_no_seed_marker_accounts_for_are_unmarked_and_need_a_reseed()
    {
        string connectionString = await BrowserSuiteFixture.CreateDatabaseAsync(postgres, "sample_db_unmarked");

        await using (MartenFixture marten = await MartenFixture.CreateAsync(connectionString, SampleStore.DocumentSchema))
        {
            await marten.SeedSampleDataAsync(Token);
        }

        await LiveWait.ScalarAsync<int>(connectionString, "delete from studio_sample.mt_doc_seedmarker");

        SampleDatabaseInspection found = await SampleDatabase.InspectAsync(connectionString, Token);

        found.Should().Be(new SampleDatabaseInspection(SampleDatabaseState.Unmarked, null));
        found.NeedsReseed.Should().BeTrue("documents nobody can vouch for are what a migration fails over");
    }

    /// <summary>The server's error under Marten's and Npgsql's wrappers, if there is one.</summary>
    private static PostgresException? Unwrap(Exception? exception)
    {
        for (; exception is not null; exception = exception.InnerException)
        {
            if (exception is PostgresException postgresException)
            {
                return postgresException;
            }

            if (exception is AggregateException { InnerExceptions.Count: > 0 } aggregate)
            {
                return aggregate.InnerExceptions.Select(Unwrap).FirstOrDefault(static x => x is not null);
            }
        }

        return null;
    }

    private static async Task<long> CustomersAsync(string connectionString) =>
        await LiveWait.ScalarAsync<long>(connectionString, "select count(*) from studio_sample.mt_doc_customer");

    private static async Task<long> ForeignKeysOnAsync(string connectionString, string table) =>
        await LiveWait.ScalarAsync<long>(connectionString,
            "select count(*) from pg_catalog.pg_constraint where conrelid = pg_catalog.to_regclass(@table) and contype = 'f'",
            ("table", table));
}

/// <summary>
/// What a container seeded by the 2026-09-14 sample held on 2026-10-20, rebuilt on demand: read off the reused
/// container a plain <c>dotnet run</c> crashed on, and reproduced here from today's seed rather than from an old build.
/// </summary>
internal static class StaleSampleShape
{
    /// <summary>The seed version the old build wrote.</summary>
    public const int SeedVersion = 2;

    /// <summary>
    /// A database of its own, seeded by this build and then wound back: the invoices lose the column and the foreign
    /// key P10 gave them, the marker goes back to version 2, and the customers are emptied - Marten's own
    /// <c>truncate … cascade</c>, which takes the orders with them and, with no foreign key in the way, leaves the
    /// invoices behind.
    /// </summary>
    /// <param name="postgres">The assembly's Postgres.</param>
    /// <param name="database">The database name; a literal of the calling test's own.</param>
    /// <returns>Its connection string.</returns>
    public static async Task<string> CreateAsync(PostgresFixture postgres, string database)
    {
        string connectionString = await BrowserSuiteFixture.CreateDatabaseAsync(postgres, database);

        await using (MartenFixture marten = await MartenFixture.CreateAsync(connectionString, SampleStore.DocumentSchema))
        {
            await marten.SeedSampleDataAsync(TestContext.Current.CancellationToken);
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = new NpgsqlCommand(WindBack, connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        (await LiveWait.ScalarAsync<long>(connectionString, "select count(*) from studio_sample.mt_doc_invoice"))
            .Should().BePositive("the stale shape is invoices whose customers are gone");

        return connectionString;
    }

    /// <summary>
    /// Every foreign key the invoice table has, by whatever name this Marten gave it (9.50 hashes long ones), then its
    /// column, with whatever index Marten put on it.
    /// </summary>
    private const string WindBack =
        """
        do $$
        declare
            fk text;
        begin
            for fk in select conname from pg_catalog.pg_constraint
                      where conrelid = 'studio_sample.mt_doc_invoice'::regclass and contype = 'f'
            loop
                execute pg_catalog.format('alter table studio_sample.mt_doc_invoice drop constraint %I', fk);
            end loop;
        end
        $$;

        alter table studio_sample.mt_doc_invoice drop column customer_id cascade;

        update studio_sample.mt_doc_seedmarker set data = jsonb_set(data, '{Version}', '2');

        truncate studio_sample.mt_doc_customer cascade;
        """;
}
