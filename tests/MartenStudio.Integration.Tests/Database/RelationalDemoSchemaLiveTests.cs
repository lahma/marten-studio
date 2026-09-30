using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

using Marten;

using MartenStudio.Integration.Tests.Browser;
using MartenStudio.Internal.Sql;
using MartenStudio.SampleDomain;
using MartenStudio.SampleDomain.Documents;
using MartenStudio.SampleDomain.Generation;
using MartenStudio.SampleDomain.Relational;

using Microsoft.Extensions.Logging;

using Npgsql;

namespace MartenStudio.Integration.Tests.Database;

/// <summary>
/// The sample's store, in a database of its own, seeded exactly as the sample host seeds it - which
/// includes the relational demo schemas.
/// </summary>
/// <remarks>
/// <para>
/// <b>A database, not a schema.</b> Every other fixture re-points the demo domain at a schema named after
/// its test class; this one cannot, because <see cref="RelationalDemoSchema" /> is anchored to the
/// sample's own <c>studio_sample</c> and creates the fixed schemas <c>quartz</c> and <c>legacy</c>.
/// Putting those in the assembly's shared database would hand every other class's catalog reads two
/// schemas they did not ask for, so each fixture here gets a database, dropped first, exactly as the
/// browser suite's hosts do.
/// </para>
/// <para>
/// <b>Through the sample's own seeder.</b> The store is built by <see cref="MartenFixture" /> with the
/// sample's own schema names, and seeded by <see cref="SampleDataSeeder" /> - so what is tested is the
/// wiring the sample host runs, not a direct call that the host might not make.
/// </para>
/// </remarks>
/// <param name="postgres">The assembly's Postgres.</param>
public abstract class RelationalDemoFixture(PostgresFixture postgres) : IAsyncLifetime
{
    private MartenFixture? marten;

    /// <summary>The database this fixture owns.</summary>
    protected abstract string DatabaseName { get; }

    /// <summary>The connection string for <see cref="DatabaseName" />, with its password intact.</summary>
    public string ConnectionString { get; private set; } = string.Empty;

    /// <summary>The store, once the fixture has run.</summary>
    public MartenFixture Marten => marten
        ?? throw new InvalidOperationException(
            "The relational demo fixture is not running. A test that needs it must be a [PostgresFact].");

    /// <summary>The assembly's Postgres, for a test that needs a database of its own.</summary>
    public PostgresFixture Postgres { get; } = postgres;

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        if (!DockerAvailability.IsAvailable)
        {
            return;
        }

        ConnectionString = await BrowserSuiteFixture.CreateDatabaseAsync(Postgres, DatabaseName);

        // SampleStore's own schema names, not a re-pointed copy: the seeder applies the relational demo
        // only to a store whose documents live in studio_sample, which is the sample host's.
        marten = await MartenFixture.CreateAsync(ConnectionString, SampleStore.DocumentSchema);

        await marten.SeedSampleDataAsync();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);

        if (marten is not null)
        {
            await marten.DisposeAsync();
        }
    }

    /// <summary>An open connection to <see cref="DatabaseName" />.</summary>
    public async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return connection;
    }
}

/// <summary>The fixture the read-mostly tests share.</summary>
/// <param name="postgres">The assembly's Postgres.</param>
public sealed class RelationalDemoSchemaFixture(PostgresFixture postgres) : RelationalDemoFixture(postgres)
{
    /// <inheritdoc />
    protected override string DatabaseName => "relational_demo";
}

/// <summary>The fixture the flow test writes to, so the read-mostly tests never see it mid-flight.</summary>
/// <param name="postgres">The assembly's Postgres.</param>
public sealed class RelationalDemoFlowsFixture(PostgresFixture postgres) : RelationalDemoFixture(postgres)
{
    /// <inheritdoc />
    protected override string DatabaseName => "relational_demo_flows";
}

/// <summary>
/// The relational demo schemas exist, hold every object kind the database browser has to handle, are
/// seeded the way their owners would seed them, and can be applied any number of times.
/// </summary>
/// <remarks>
/// Every object is checked through <c>pg_catalog</c>, which is what the database browser reads. Counting
/// through <c>information_schema</c> would be counting something else: it hides what the role has no
/// privilege on, and it has no notion of a partition, a materialized view or an aggregate.
/// </remarks>
/// <param name="fixture">The seeded database.</param>
public class RelationalDemoSchemaLiveTests(RelationalDemoSchemaFixture fixture) : IClassFixture<RelationalDemoSchemaFixture>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // -------------------------------------------------------------------------------------------------
    // Idempotency
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// The sample host applies the demo on every start against a reused database, so a second and third
    /// apply must write nothing, create nothing and move no count.
    /// </summary>
    [PostgresFact]
    public async Task Applying_again_writes_nothing_and_changes_no_count()
    {
        await using NpgsqlConnection connection = await fixture.OpenAsync();

        IReadOnlyDictionary<string, long> rowsBefore = await RowCountsAsync(connection);
        IReadOnlyDictionary<string, long> objectsBefore = await ObjectCountsAsync(connection);

        rowsBefore.Should().NotBeEmpty();
        rowsBefore.Values.Should().Contain(x => x > 0, "a comparison of empty tables would pass for the wrong reason");

        RelationalDemoResult second = await RelationalDemoSchema.ApplyAsync(connection, Token);
        RelationalDemoResult third = await RelationalDemoSchema.ApplyAsync(connection, Token);

        second.RowsWritten.Should().Be(0, "every table already has its rows");
        third.RowsWritten.Should().Be(0);

        // And through the sample's own seeder, which is what a second `dotnet run` actually executes.
        await fixture.Marten.SeedSampleDataAsync(Token);

        (await RowCountsAsync(connection)).Should().BeEquivalentTo(rowsBefore);
        (await ObjectCountsAsync(connection)).Should().BeEquivalentTo(objectsBefore);
    }

    /// <summary>Both scripts on their own, run twice in a row outside the seeder: no error, no drop.</summary>
    [PostgresFact]
    public async Task The_scripts_run_twice_and_never_drop_anything()
    {
        DropStatements(RelationalDemoSchema.QuartzScript).Should().BeEmpty("the vendored script's DROP block is gone");
        DropStatements(RelationalDemoSchema.LegacyScript).Should().BeEmpty();

        await using NpgsqlConnection connection = await fixture.OpenAsync();

        IReadOnlyDictionary<string, long> before = await ObjectCountsAsync(connection);

        for (var run = 0; run < 2; run++)
        {
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(Token);

            foreach (string script in (string[]) [RelationalDemoSchema.QuartzScript, RelationalDemoSchema.LegacyScript])
            {
                await using var command = new NpgsqlCommand(script, connection, transaction);
                await command.ExecuteNonQueryAsync(Token);
            }

            await transaction.CommitAsync(Token);
        }

        (await ObjectCountsAsync(connection)).Should().BeEquivalentTo(before);
    }

    // -------------------------------------------------------------------------------------------------
    // Quartz
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Quartz.NET's fourteen tables, with its three-column foreign key from triggers to jobs and the
    /// cascading foreign keys from the four trigger-detail tables.
    /// </summary>
    [PostgresFact]
    public async Task Quartz_is_the_real_fourteen_table_schema()
    {
        await using NpgsqlConnection connection = await fixture.OpenAsync();

        List<string> tables = await StringsAsync(
            connection,
            """
            select c.relname::text from pg_catalog.pg_class c
            where c.relnamespace = 'quartz'::regnamespace and c.relkind = 'r'
            order by 1
            """);

        tables.Should().HaveCount(RelationalDemoSchema.QuartzTableCount);
        tables.Should().AllSatisfy(x => x.Should().StartWith("qrtz_"));
        tables.Should().Contain([
            "qrtz_job_details", "qrtz_triggers", "qrtz_simple_triggers", "qrtz_simprop_triggers",
            "qrtz_cron_triggers", "qrtz_blob_triggers", "qrtz_calendars", "qrtz_paused_trigger_grps",
            "qrtz_paused_job_grps", "qrtz_fired_triggers", "qrtz_scheduler_state", "qrtz_locks",
            "qrtz_execution_history", "qrtz_misfire_history",
        ]);

        List<string> jobKey = await StringsAsync(
            connection,
            """
            select a.attname::text
            from pg_catalog.pg_constraint con
            cross join lateral pg_catalog.unnest(con.conkey) with ordinality as k(attnum, position)
            join pg_catalog.pg_attribute a on a.attrelid = con.conrelid and a.attnum = k.attnum
            where con.contype = 'f'
              and con.conrelid = 'quartz.qrtz_triggers'::regclass
              and con.confrelid = 'quartz.qrtz_job_details'::regclass
            order by k.position
            """);

        jobKey.Should().Equal(["sched_name", "job_name", "job_group"], "the trigger -> job foreign key is on three columns");

        List<string> cascading = await StringsAsync(
            connection,
            """
            select con.conrelid::regclass::text
            from pg_catalog.pg_constraint con
            where con.contype = 'f'
              and con.confrelid = 'quartz.qrtz_triggers'::regclass
              and con.confdeltype = 'c'
              and pg_catalog.array_length(con.conkey, 1) = 3
            order by 1
            """);

        cascading.Should().Equal(
            "quartz.qrtz_blob_triggers",
            "quartz.qrtz_cron_triggers",
            "quartz.qrtz_simple_triggers",
            "quartz.qrtz_simprop_triggers");

        (await ScalarAsync(
            connection,
            """
            select count(*) from pg_catalog.pg_index i
            join pg_catalog.pg_class c on c.oid = i.indexrelid
            where c.relnamespace = 'quartz'::regnamespace and not i.indisprimary and c.relname like 'idx_qrtz_%'
            """)).Should().Be(12, "the twelve CREATE INDEX statements of the original script");
    }

    /// <summary>
    /// Two schedulers of a dozen jobs and triggers each, across cron, simple and simple-property
    /// triggers, with instants in .NET ticks and job data as UTF-8 JSON - which is how Quartz's ADO.NET
    /// job store writes them.
    /// </summary>
    [PostgresFact]
    public async Task Quartz_is_seeded_the_way_its_job_store_writes()
    {
        await using NpgsqlConnection connection = await fixture.OpenAsync();

        foreach (string scheduler in RelationalDemoSchema.QuartzSchedulerNames)
        {
            (await ScalarAsync(connection, "select count(*) from quartz.qrtz_job_details where sched_name = @p", scheduler))
                .Should().Be(12, scheduler + " has a dozen jobs");
            (await ScalarAsync(connection, "select count(*) from quartz.qrtz_triggers where sched_name = @p", scheduler))
                .Should().Be(12, scheduler + " has a dozen triggers");
            (await ScalarAsync(connection, "select count(*) from quartz.qrtz_locks where sched_name = @p", scheduler))
                .Should().Be(2, "TRIGGER_ACCESS and STATE_ACCESS");
            (await ScalarAsync(connection, "select count(*) from quartz.qrtz_scheduler_state where sched_name = @p", scheduler))
                .Should().BeGreaterThan(0);
        }

        (await ScalarAsync(connection, "select count(distinct sched_name) from quartz.qrtz_triggers")).Should().Be(2);
        (await ScalarAsync(connection, "select count(*) from quartz.qrtz_fired_triggers")).Should().BeGreaterThanOrEqualTo(2);

        // Every trigger type is there, and every trigger has exactly one row in the detail table its type
        // belongs in - calendar-interval and daily-time-interval triggers both in simprop.
        Dictionary<string, long> types = await PairsAsync(
            connection, "select trigger_type, count(*) from quartz.qrtz_triggers group by 1");

        types.Keys.Should().BeEquivalentTo(["CRON", "SIMPLE", "CAL_INT", "DAILY_I"]);

        (await ScalarAsync(connection, "select count(*) from quartz.qrtz_cron_triggers")).Should().Be(types["CRON"]);
        (await ScalarAsync(connection, "select count(*) from quartz.qrtz_simple_triggers")).Should().Be(types["SIMPLE"]);
        (await ScalarAsync(connection, "select count(*) from quartz.qrtz_simprop_triggers"))
            .Should().Be(types["CAL_INT"] + types["DAILY_I"]);

        (await ScalarAsync(
            connection,
            """
            select count(*) from quartz.qrtz_triggers t
            where 1 <> (select count(*) from quartz.qrtz_cron_triggers d
                        where (d.sched_name, d.trigger_name, d.trigger_group) = (t.sched_name, t.trigger_name, t.trigger_group))
                     + (select count(*) from quartz.qrtz_simple_triggers d
                        where (d.sched_name, d.trigger_name, d.trigger_group) = (t.sched_name, t.trigger_name, t.trigger_group))
                     + (select count(*) from quartz.qrtz_simprop_triggers d
                        where (d.sched_name, d.trigger_name, d.trigger_group) = (t.sched_name, t.trigger_name, t.trigger_group))
            """)).Should().Be(0, "every trigger has exactly one detail row");

        // .NET ticks, near now: not epoch milliseconds, not seconds, not a fixed date in the past.
        long now = DateTime.UtcNow.Ticks;
        List<long> nextFireTimes = await LongsAsync(
            connection, "select next_fire_time from quartz.qrtz_triggers where trigger_state = 'WAITING'");

        nextFireTimes.Should().NotBeEmpty();
        nextFireTimes.Should().AllSatisfy(x => x.Should().BeInRange(
            now - TimeSpan.FromDays(1).Ticks,
            now + TimeSpan.FromDays(400).Ticks,
            "next_fire_time is DateTimeOffset.UtcTicks, relative to when the demo was seeded"));

        (await ScalarAsync(
            connection,
            "select count(*) from quartz.qrtz_triggers where prev_fire_time is not null and prev_fire_time >= next_fire_time"))
            .Should().Be(0, "a trigger fired before it fires next");

        // job_data is the job data map as UTF-8 JSON bytes.
        List<byte[]> jobData = await BytesAsync(
            connection, "select job_data from quartz.qrtz_job_details where job_data is not null");

        jobData.Should().NotBeEmpty();
        foreach (byte[] bytes in jobData)
        {
            using JsonDocument document = JsonDocument.Parse(bytes);
            document.RootElement.ValueKind.Should().Be(JsonValueKind.Object);
        }
    }

    // -------------------------------------------------------------------------------------------------
    // Legacy
    // -------------------------------------------------------------------------------------------------

    /// <summary>Every kind of object the database browser lists, counted kind by kind.</summary>
    [PostgresFact]
    public async Task The_legacy_schema_has_every_kind_of_object()
    {
        await using NpgsqlConnection connection = await fixture.OpenAsync();

        Dictionary<string, long> relations = await PairsAsync(
            connection,
            """
            select c.relkind::text || case when c.relispartition then '/partition' else '' end, count(*)
            from pg_catalog.pg_class c
            where c.relnamespace = 'legacy'::regnamespace and c.relkind in ('r', 'p', 'v', 'm', 'S', 'c')
            group by 1
            """);

        relations.Should().BeEquivalentTo(new Dictionary<string, long>
        {
            ["r"] = 10,           // ordinary tables
            ["p"] = 1,            // the partitioned table ...
            ["r/partition"] = 2,  // ... and its two partitions
            ["v"] = 1,            // a plain view
            ["m"] = 2,            // two materialized views
            ["S"] = 3,            // three sequences
            ["c"] = 1,            // one standalone composite type
        });

        // Routines: an overloaded function, a trigger function, a procedure and an aggregate.
        (await PairsAsync(
            connection,
            "select p.prokind::text, count(*) from pg_catalog.pg_proc p where p.pronamespace = 'legacy'::regnamespace group by 1"))
            .Should().BeEquivalentTo(new Dictionary<string, long> { ["f"] = 3, ["p"] = 1, ["a"] = 1 });

        (await ScalarAsync(
            connection,
            "select count(*) from pg_catalog.pg_proc where pronamespace = 'legacy'::regnamespace and proname = 'format_employee_name'"))
            .Should().Be(2, "one name, two signatures");

        (await ScalarAsync(
            connection,
            """
            select count(*) from pg_catalog.pg_proc p join pg_catalog.pg_language l on l.oid = p.prolang
            where p.pronamespace = 'legacy'::regnamespace and p.prorettype = 'trigger'::regtype and l.lanname = 'plpgsql'
            """)).Should().Be(1);

        (await ScalarAsync(
            connection,
            """
            select count(*) from pg_catalog.pg_proc
            where pronamespace = 'legacy'::regnamespace and prokind = 'p' and prosecdef and proconfig is null
            """)).Should().Be(1, "the procedure is SECURITY DEFINER with no search_path, for the browser to flag");

        // Triggers: one enabled, one disabled.
        (await PairsAsync(
            connection,
            """
            select t.tgenabled::text, count(*) from pg_catalog.pg_trigger t
            join pg_catalog.pg_class c on c.oid = t.tgrelid
            where c.relnamespace = 'legacy'::regnamespace and not t.tgisinternal and t.tgparentid = 0
            group by 1
            """)).Should().BeEquivalentTo(new Dictionary<string, long> { ["O"] = 1, ["D"] = 1 });

        // Types: an enum a column uses, a composite a column uses, and a domain with a CHECK.
        (await PairsAsync(
            connection,
            """
            select t.typtype::text, count(*) from pg_catalog.pg_type t
            left join pg_catalog.pg_class c on c.oid = t.typrelid
            where t.typnamespace = 'legacy'::regnamespace
              and (t.typtype in ('e', 'd') or (t.typtype = 'c' and c.relkind = 'c'))
            group by 1
            """)).Should().BeEquivalentTo(new Dictionary<string, long> { ["e"] = 1, ["c"] = 1, ["d"] = 1 });

        (await ScalarAsync(connection, "select count(*) from pg_catalog.pg_attribute where atttypid = 'legacy.order_status'::regtype and attnum > 0"))
            .Should().BeGreaterThan(0, "the enum is used by a column");
        (await ScalarAsync(connection, "select count(*) from pg_catalog.pg_attribute where atttypid = 'legacy.postal_address'::regtype and attnum > 0"))
            .Should().BeGreaterThan(0, "the composite type is used by a column");
        (await ScalarAsync(connection, "select count(*) from pg_catalog.pg_constraint where contypid = 'legacy.email_address'::regtype and contype = 'c'"))
            .Should().Be(1, "the domain has a CHECK");

        // Sequences: two owned by a column (serial and identity), one owned by nothing.
        (await PairsAsync(
            connection,
            """
            select coalesce(d.deptype::text, 'standalone'), count(*)
            from pg_catalog.pg_class s
            left join pg_catalog.pg_depend d
              on d.classid = 'pg_catalog.pg_class'::regclass and d.objid = s.oid and d.deptype in ('a', 'i')
            where s.relnamespace = 'legacy'::regnamespace and s.relkind = 'S'
            group by 1
            """)).Should().BeEquivalentTo(new Dictionary<string, long> { ["a"] = 1, ["i"] = 1, ["standalone"] = 1 });

        // Keys: a composite primary key, a table with no key at all, a self-reference, a multi-column
        // foreign key, a NOT VALID one, and one into a Marten document table.
        (await ScalarAsync(
            connection,
            "select i.indnkeyatts from pg_catalog.pg_index i where i.indrelid = 'legacy.warehouses'::regclass and i.indisprimary"))
            .Should().Be(2);

        (await StringsAsync(
            connection,
            """
            select c.relname::text from pg_catalog.pg_class c
            where c.relnamespace = 'legacy'::regnamespace and c.relkind in ('r', 'p') and not c.relispartition
              and not exists (select 1 from pg_catalog.pg_index i where i.indrelid = c.oid and (i.indisprimary or i.indisunique))
            """)).Should().Equal(RelationalDemoSchema.AuditLogTable);

        List<string> foreignKeys = await StringsAsync(
            connection,
            """
            select con.conrelid::regclass::text || ' -> ' || con.confrelid::regclass::text
                   || ' (' || pg_catalog.array_length(con.conkey, 1) || ')'
                   || case when con.convalidated then '' else ' not valid' end
                   || case con.confdeltype when 'c' then ' cascade' else '' end
            from pg_catalog.pg_constraint con
            where con.contype = 'f' and con.connamespace = 'legacy'::regnamespace
            order by 1
            """);

        foreignKeys.Should().BeEquivalentTo([
            "legacy.customer_credit -> studio_sample.mt_doc_customer (1) cascade",
            "legacy.employees -> legacy.departments (1)",
            "legacy.employees -> legacy.employees (1)",
            "legacy.purchase_order_lines -> legacy.purchase_orders (1) not valid",
            "legacy.purchase_orders -> legacy.employees (1)",
            "legacy.stock_levels -> legacy.product_catalogue (1)",
            "legacy.stock_levels -> legacy.warehouses (2)",
        ]);

        // Wide: forty columns.
        (await ScalarAsync(
            connection,
            "select count(*) from pg_catalog.pg_attribute where attrelid = 'legacy.product_catalogue'::regclass and attnum > 0 and not attisdropped"))
            .Should().Be(40);

        // The column types a row grid has to render.
        (await PairsAsync(
            connection,
            """
            select a.attname::text, 1 from pg_catalog.pg_attribute a
            where a.attrelid = 'legacy.integration_messages'::regclass and a.attnum > 0 and not a.attisdropped
              and pg_catalog.format_type(a.atttypid, a.atttypmod) in ('jsonb', 'text', 'bytea', 'timestamp with time zone', 'timestamp without time zone')
            """)).Keys.Should().Contain(["payload", "raw_body", "attachment", "received_at", "sent_at_local"]);

        (await ScalarAsync(connection, "select count(*) from pg_catalog.pg_matviews where schemaname = 'legacy' and ispopulated"))
            .Should().Be(1);

        (await ScalarAsync(
            connection,
            """
            select count(*) from pg_catalog.pg_description d
            join pg_catalog.pg_class c on c.oid = d.objoid and d.classoid = 'pg_catalog.pg_class'::regclass
            where c.relnamespace = 'legacy'::regnamespace
            """)).Should().BeGreaterThanOrEqualTo(10, "several tables and columns carry a COMMENT");
    }

    /// <summary>Realistic rows everywhere, including the ones that are wrong on purpose.</summary>
    [PostgresFact]
    public async Task The_legacy_schema_is_seeded_with_its_edge_cases()
    {
        await using NpgsqlConnection connection = await fixture.OpenAsync();

        (await ScalarAsync(connection, "select count(*) from legacy.employees")).Should().Be(40);
        (await ScalarAsync(connection, "select count(*) from legacy.employees where manager_id is null")).Should().Be(1);
        (await ScalarAsync(
            connection,
            "select count(*) from legacy.employees e join legacy.employees m on m.employee_id = e.manager_id where m.manager_id is not null"))
            .Should().BeGreaterThan(0, "the self-reference goes three levels deep");

        (await ScalarAsync(connection, "select count(*) from legacy.audit_log")).Should().BeGreaterThanOrEqualTo(300);
        (await ScalarAsync(
            connection,
            """
            select count(*) from (
              select 1 from legacy.audit_log
              group by logged_at, actor, action, entity, entity_key, details having count(*) > 1) as duplicated
            """)).Should().Be(1, "one row is there twice, which only ctid can tell apart");

        (await ScalarAsync(
            connection,
            """
            select count(*) from legacy.purchase_order_lines l
            where not exists (select 1 from legacy.purchase_orders o where o.order_id = l.order_id)
            """)).Should().Be(1, "exactly one line violates the NOT VALID foreign key");

        (await ScalarAsync(connection, "select count(*) from legacy.integration_messages where pg_catalog.octet_length(raw_body) > 10 * 1024"))
            .Should().BeGreaterThanOrEqualTo(2);
        (await ScalarAsync(
            connection,
            "select pg_catalog.pg_relation_size(reltoastrelid) from pg_catalog.pg_class where oid = 'legacy.integration_messages'::regclass"))
            .Should().BeGreaterThan(0, "the long bodies live out of line in the TOAST table");
        (await ScalarAsync(connection, "select count(*) from legacy.integration_messages where attachment is not null")).Should().BeGreaterThan(0);
        (await ScalarAsync(connection, "select count(*) from legacy.integration_messages where sent_at_local is null")).Should().BeGreaterThan(0);

        (await PairsAsync(
            connection,
            "select c.relname::text, (select count(*) from legacy.sensor_readings r where r.tableoid = c.oid) from pg_catalog.pg_inherits i join pg_catalog.pg_class c on c.oid = i.inhrelid where i.inhparent = 'legacy.sensor_readings'::regclass"))
            .Values.Should().AllSatisfy(x => x.Should().BeGreaterThan(0, "both partitions hold rows"));

        (await ScalarAsync(connection, "select count(*) from legacy.stock_by_region")).Should().BeGreaterThan(0, "refreshed after the stock was written");

        await using (var unpopulated = new NpgsqlCommand("select count(*) from legacy.monthly_order_totals", connection))
        {
            Func<Task> read = () => unpopulated.ExecuteScalarAsync(Token);

            (await read.Should().ThrowAsync<PostgresException>())
                .Which.SqlState.Should().Be("55000", "a materialized view created WITH NO DATA cannot be read until refreshed");
        }

        (await ScalarAsync(
            connection,
            "select count(*) from legacy.customer_credit c join studio_sample.mt_doc_customer d on d.id = c.customer_id"))
            .Should().BeGreaterThan(0)
            .And.BeLessThan(SampleDataSeeder.CustomerCount, "most customers have a credit line, not all");
    }

    /// <summary>
    /// The citext column is the one conditional part: absent here, where nobody installed the extension,
    /// and present in a database where somebody did - which is also a database with no Marten store, so
    /// the Marten-anchored half has to be skipped rather than fail.
    /// </summary>
    [PostgresFact]
    public async Task The_citext_column_exists_only_where_the_extension_was_installed()
    {
        await using (NpgsqlConnection connection = await fixture.OpenAsync())
        {
            RelationalDemoResult sample = await RelationalDemoSchema.ApplyAsync(connection, Token);

            sample.HasCitextColumn.Should().BeFalse("postgres:17-alpine ships citext but nobody installed it here");
            sample.HasCustomerForeignKey.Should().BeTrue();
            sample.HasAppSettings.Should().BeTrue();
        }

        string connectionString = await BrowserSuiteFixture.CreateDatabaseAsync(fixture.Postgres, "relational_demo_citext");

        await using var bare = new NpgsqlConnection(connectionString);
        await bare.OpenAsync(Token);

        await using (var install = new NpgsqlCommand("create extension citext", bare))
        {
            await install.ExecuteNonQueryAsync(Token);
        }

        RelationalDemoResult first = await RelationalDemoSchema.ApplyAsync(bare, Token);

        first.HasCitextColumn.Should().BeTrue();
        first.HasCustomerForeignKey.Should().BeFalse("there is no studio_sample.mt_doc_customer in this database");
        first.HasAppSettings.Should().BeFalse("there is no studio_sample schema in this database");
        first.RowsWritten.Should().BeGreaterThan(0);

        (await ScalarTextAsync(
            bare,
            """
            select pg_catalog.format_type(atttypid, atttypmod) from pg_catalog.pg_attribute
            where attrelid = 'legacy.integration_messages'::regclass and attname = 'sender'
            """)).Should().EndWith("citext");

        (await ScalarAsync(bare, "select count(distinct sender) from legacy.integration_messages where sender is not null"))
            .Should().BeLessThan(
                await ScalarAsync(bare, "select count(distinct sender::text) from legacy.integration_messages where sender is not null"),
                "Orders@ and orders@ are one sender to citext and two to text");

        (await ScalarAsync(bare, "select count(*) from legacy.customer_credit")).Should().Be(0);
        (await ScalarAsync(bare, "select count(*) from quartz.qrtz_triggers")).Should().Be(24);

        (await RelationalDemoSchema.ApplyAsync(bare, Token)).RowsWritten.Should().Be(0);
    }

    // -------------------------------------------------------------------------------------------------
    // Inside the Marten schema
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// An ordinary table sits in <c>studio_sample</c> next to Marten's own, and Marten - applying every
    /// configured change under <c>AutoCreate.All</c>, from this store and from a brand-new one - neither
    /// drops it nor empties it: Marten's migrations diff only the tables Marten declares.
    /// </summary>
    /// <remarks>
    /// What Marten does <em>not</em> leave alone is the foreign key another schema holds into one of its
    /// tables; that is the next test.
    /// </remarks>
    [PostgresFact]
    public async Task An_ordinary_table_lives_in_the_marten_schema_and_marten_leaves_it_alone()
    {
        await using NpgsqlConnection connection = await fixture.OpenAsync();

        List<string> tables = await StringsAsync(
            connection,
            "select relname::text from pg_catalog.pg_class where relnamespace = 'studio_sample'::regnamespace and relkind = 'r' order by 1");

        tables.Should().Contain(RelationalDemoSchema.AppSettingsTable);
        tables.Should().Contain("mt_doc_customer");
        tables.Where(x => x.StartsWith("mt_doc_", StringComparison.Ordinal)).Should().HaveCountGreaterThan(5);

        long settings = await ScalarAsync(connection, "select count(*) from studio_sample.app_settings");
        settings.Should().BeGreaterThan(0);

        await fixture.Marten.Store.Storage.ApplyAllConfiguredChangesToDatabaseAsync();

        await using (DocumentStore fresh = DocumentStore.For(options => SampleStore.Configure(options, fixture.ConnectionString)))
        {
            await fresh.Storage.ApplyAllConfiguredChangesToDatabaseAsync();
        }

        (await ScalarAsync(connection, "select count(*) from studio_sample.app_settings")).Should().Be(settings);
        (await StringsAsync(
            connection,
            "select relname::text from pg_catalog.pg_class where relnamespace = 'studio_sample'::regnamespace and relkind = 'r' order by 1"))
            .Should().Equal(tables);
    }

    /// <summary>
    /// The foreign key from <c>legacy.customer_credit</c> into <c>mt_doc_customer</c> does not survive a
    /// Marten migration that rebuilds the customer table's primary key - and the next apply puts it back,
    /// validated and still cascading.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The real contract, not the hoped-for one.</b> When the key of <c>mt_doc_customer</c> changes,
    /// Weasel's <c>TableDelta</c> writes <c>alter table … drop constraint &lt;pk&gt; CASCADE</c>, adds the
    /// new key, and re-creates the foreign keys that point at the table - but only the ones Marten
    /// declared, from its own table deltas. A partition rebuild is <c>drop table … cascade</c> with the
    /// same result. Either way the demo's key is gone, and Marten will never bring it back; what does is
    /// <c>LegacySchema.sql</c>'s guarded <c>DO</c> block, which runs on every start of the sample.
    /// </para>
    /// <para>
    /// The rebuild is reproduced with Weasel's own two statements for a changed key - drop it with
    /// <c>CASCADE</c>, add it back - and Marten is then left to put back the foreign keys it owns, which
    /// is the part Weasel's <c>ReferencingForeignKeys</c> does inside the same migration. Not the drop
    /// alone and then Marten: a customer table with no key at all is a state the Weasel this repository
    /// resolves cannot migrate out of - it emitted <c>drop constraint pkey_mt_doc_customer_ CASCADE</c>
    /// and failed on 42704, measured. Its own database, because what it takes apart would otherwise be
    /// shared by every other test in the class.
    /// </para>
    /// </remarks>
    [PostgresFact]
    public async Task A_rebuild_of_the_customer_key_drops_the_foreign_key_and_the_next_start_restores_it()
    {
        string connectionString = await BrowserSuiteFixture.CreateDatabaseAsync(fixture.Postgres, "relational_demo_rebuild");

        await using MartenFixture marten = await MartenFixture.CreateAsync(connectionString, SampleStore.DocumentSchema);
        await marten.SeedSampleDataAsync(Token);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Token);

        long credit = await ScalarAsync(connection, "select count(*) from legacy.customer_credit");
        credit.Should().BeGreaterThan(0);

        (await ForeignKeysIntoCustomerAsync(connection)).Should().Equal(
            "legacy.customer_credit:customer_credit_customer_id_fkey:c:valid",
            "studio_sample.mt_doc_invoice:mt_doc_invoice_customer_id_fkey:a:valid",
            "studio_sample.mt_doc_order:mt_doc_order_customer_id_fkey:a:valid");

        string primaryKey = SqlIdentifier.Quote((await ScalarTextAsync(
            connection,
            "select conname::text from pg_catalog.pg_constraint where conrelid = 'studio_sample.mt_doc_customer'::regclass and contype = 'p'"))!);

        // TableDelta.writePrimaryKeyChanges, for a key whose columns changed.
        await ExecuteAsync(connection, "alter table studio_sample.mt_doc_customer drop constraint " + primaryKey + " cascade");
        await ExecuteAsync(connection, "alter table studio_sample.mt_doc_customer add constraint " + primaryKey + " primary key (id)");

        (await ForeignKeysIntoCustomerAsync(connection)).Should().BeEmpty("CASCADE takes every foreign key that depends on the key");

        // Marten puts back what it declares - its own two foreign keys - and nothing else.
        await using (DocumentStore fresh = DocumentStore.For(options => SampleStore.Configure(options, connectionString)))
        {
            await fresh.Storage.ApplyAllConfiguredChangesToDatabaseAsync();
        }

        (await ForeignKeysIntoCustomerAsync(connection)).Should().Equal(
            ["studio_sample.mt_doc_invoice:mt_doc_invoice_customer_id_fkey:a:valid",
             "studio_sample.mt_doc_order:mt_doc_order_customer_id_fkey:a:valid"],
            "Marten re-creates only the foreign keys it declared");

        // The next start of the sample host: its seeder, which applies the relational demo last.
        await marten.SeedSampleDataAsync(Token);

        (await ForeignKeysIntoCustomerAsync(connection)).Should().Equal(
            "legacy.customer_credit:customer_credit_customer_id_fkey:c:valid",
            "studio_sample.mt_doc_invoice:mt_doc_invoice_customer_id_fkey:a:valid",
            "studio_sample.mt_doc_order:mt_doc_order_customer_id_fkey:a:valid");
        (await ScalarAsync(connection, "select count(*) from legacy.customer_credit"))
            .Should().Be(credit, "no credit row lost its customer, so none was removed");
        (await RelationalDemoSchema.ApplyAsync(connection, Token)).HasCustomerForeignKey.Should().BeTrue();
    }

    // -------------------------------------------------------------------------------------------------
    // Only the demo's own schemas
    // -------------------------------------------------------------------------------------------------

    /// <summary>The two schemas the demo created carry the marker that lets a later start recognise them.</summary>
    [PostgresFact]
    public async Task The_schemas_the_demo_created_carry_its_marker()
    {
        await using NpgsqlConnection connection = await fixture.OpenAsync();

        (await SchemaCommentAsync(connection, RelationalDemoSchema.QuartzSchemaName))
            .Should().StartWith(RelationalDemoSchema.QuartzSchemaMarker + " ");
        (await SchemaCommentAsync(connection, RelationalDemoSchema.LegacySchemaName))
            .Should().StartWith(RelationalDemoSchema.LegacySchemaMarker + " ");

        RelationalDemoResult again = await RelationalDemoSchema.ApplyAsync(connection, Token);

        again.QuartzApplied.Should().BeTrue("the marker is what makes the schema the demo's own");
        again.LegacyApplied.Should().BeTrue();
    }

    /// <summary>
    /// A <c>legacy</c> schema somebody else made, with a function of the same name as one of the demo's,
    /// and a real, empty Quartz.NET job store in <c>quartz</c>: the sample host starts, seeds its Marten
    /// store, and touches neither - not a function replaced, not a table created, not a row inserted -
    /// and says so at Information.
    /// </summary>
    /// <remarks>
    /// An empty Quartz job store is the dangerous case, not a contrived one: "fill every empty table" is
    /// exactly the rule the demo seeds by, and it would have written two fake schedulers' jobs and
    /// triggers into a real scheduler's tables. Its own database, because the schemas are fixed names.
    /// </remarks>
    [PostgresFact]
    public async Task A_quartz_or_legacy_schema_the_demo_did_not_create_is_left_untouched()
    {
        string connectionString = await BrowserSuiteFixture.CreateDatabaseAsync(fixture.Postgres, "relational_demo_foreign");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Token);

        // The payroll team's schema, with its own format_employee_name(text, text).
        await ExecuteAsync(
            connection,
            """
            create schema legacy;
            comment on schema legacy is 'Payroll. Owned by the payroll team.';
            create function legacy.format_employee_name(first_name text, last_name text) returns text
              language sql immutable as $$ select upper(last_name) || ', ' || first_name || ' (payroll)' $$;
            """);

        // A scheduler's job store, made with Quartz.NET's own script: every table, no rows, no comment.
        await ExecuteAsync(connection, RelationalDemoSchema.QuartzScript);
        await ExecuteAsync(connection, "comment on schema quartz is null");

        Dictionary<string, long> objectsBefore = await ForeignSchemaObjectsAsync(connection);
        List<string> functionsBefore = await StringsAsync(
            connection,
            "select p.oid::regprocedure::text || ' = ' || p.prosrc from pg_catalog.pg_proc p where p.pronamespace = 'legacy'::regnamespace");

        functionsBefore.Should().ContainSingle();
        objectsBefore["quartz tables"].Should().Be(RelationalDemoSchema.QuartzTableCount);

        // The whole host path: a sample store in this database, seeded by the sample's own seeder.
        var log = new ListLogger<SampleDataSeeder>();

        await using (MartenFixture marten = await MartenFixture.CreateAsync(connectionString, SampleStore.DocumentSchema))
        {
            await new SampleDataSeeder(log).Populate(marten.Store, Token);
        }

        (await ForeignSchemaObjectsAsync(connection)).Should().BeEquivalentTo(objectsBefore, "no object was created, replaced or dropped");
        (await StringsAsync(
            connection,
            "select p.oid::regprocedure::text || ' = ' || p.prosrc from pg_catalog.pg_proc p where p.pronamespace = 'legacy'::regnamespace"))
            .Should().Equal(functionsBefore, "CREATE OR REPLACE never ran against the payroll team's function");
        (await ScalarAsync(connection, "select count(*) from quartz.qrtz_job_details")).Should().Be(0);
        (await ScalarAsync(connection, "select count(*) from quartz.qrtz_locks")).Should().Be(0);
        (await ScalarTextAsync(connection, "select pg_catalog.to_regclass('studio_sample.app_settings')::text"))
            .Should().BeNull("app_settings belongs to the legacy half, which was skipped entirely");
        (await SchemaCommentAsync(connection, RelationalDemoSchema.LegacySchemaName)).Should().Be("Payroll. Owned by the payroll team.");
        (await SchemaCommentAsync(connection, RelationalDemoSchema.QuartzSchemaName)).Should().BeNull();

        // Said once per schema, at Information: a database with its own `legacy` schema is not a fault.
        log.Entries.Should().HaveCount(2);
        log.Entries.Should().AllSatisfy(x => x.Level.Should().Be(LogLevel.Information));
        log.Entries.Select(x => x.Message).Should().Satisfy(
            x => x.Contains("'quartz'", StringComparison.Ordinal) && x.Contains(RelationalDemoSchema.QuartzSchemaMarker, StringComparison.Ordinal),
            x => x.Contains("'legacy'", StringComparison.Ordinal) && x.Contains("Payroll. Owned by the payroll team.", StringComparison.Ordinal));

        // And directly, which is also what reports the two halves as not applied.
        RelationalDemoResult direct = await RelationalDemoSchema.ApplyAsync(connection, Token);

        direct.Should().Be(new RelationalDemoResult(0, false, false, false, QuartzApplied: false, LegacyApplied: false));
        (await ForeignSchemaObjectsAsync(connection)).Should().BeEquivalentTo(objectsBefore);
    }

    /// <summary>
    /// A schema the first version of the demo created - before there was a marker, so carrying that
    /// version's exact comment - is still the demo's: it is applied to and stamped with the marker, rather
    /// than left alone forever on a developer's reused container.
    /// </summary>
    [PostgresFact]
    public async Task A_schema_the_unmarked_first_version_created_is_recognised_and_marked()
    {
        string connectionString = await BrowserSuiteFixture.CreateDatabaseAsync(fixture.Postgres, "relational_demo_unmarked");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Token);

        // Verbatim from DB-2's QuartzSchema.sql and LegacySchema.sql.
        await ExecuteAsync(
            connection,
            """
            create schema quartz;
            comment on schema quartz is 'Quartz.NET job store (database/tables/tables_postgres.sql, Apache-2.0), vendored by the Marten Studio sample. Not a Marten schema.';
            create schema legacy;
            comment on schema legacy is 'A hand-made relational schema beside the Marten store, covering every catalog edge case the database browser has to handle.';
            """);

        RelationalDemoResult applied = await RelationalDemoSchema.ApplyAsync(connection, Token);

        applied.QuartzApplied.Should().BeTrue();
        applied.LegacyApplied.Should().BeTrue();
        applied.RowsWritten.Should().BeGreaterThan(0);

        (await SchemaCommentAsync(connection, RelationalDemoSchema.QuartzSchemaName)).Should().StartWith(RelationalDemoSchema.QuartzSchemaMarker);
        (await SchemaCommentAsync(connection, RelationalDemoSchema.LegacySchemaName)).Should().StartWith(RelationalDemoSchema.LegacySchemaMarker);
    }

    // -------------------------------------------------------------------------------------------------
    // Quartz, re-applied
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Emptying the jobs - and with them, by foreign key, the triggers - leaves the locks, calendars,
    /// scheduler state, fired triggers and history behind. The next apply puts the jobs and triggers back
    /// and keeps the rest, rather than failing on the first leftover's primary key and taking the host's
    /// start down with it.
    /// </summary>
    [PostgresFact]
    public async Task Quartz_leftovers_without_their_jobs_do_not_stop_the_next_apply()
    {
        await using NpgsqlConnection connection = await fixture.OpenAsync();

        IReadOnlyDictionary<string, long> before = await QuartzRowCountsAsync(connection);

        (string Table, bool Emptied)[] expected =
        [
            ("qrtz_job_details", true), ("qrtz_triggers", true), ("qrtz_cron_triggers", true),
            ("qrtz_simple_triggers", true), ("qrtz_simprop_triggers", true),
            ("qrtz_calendars", false), ("qrtz_locks", false), ("qrtz_scheduler_state", false),
            ("qrtz_fired_triggers", false), ("qrtz_execution_history", false), ("qrtz_misfire_history", false),
        ];

        expected.Should().AllSatisfy(x => before[x.Table].Should().BeGreaterThan(0, x.Table + " is seeded"));

        // Everything that hangs off the jobs by foreign key goes with them; nothing else does.
        await ExecuteAsync(connection, "truncate quartz.qrtz_job_details cascade");

        IReadOnlyDictionary<string, long> partial = await QuartzRowCountsAsync(connection);
        expected.Should().AllSatisfy(x => (partial[x.Table] == 0).Should().Be(x.Emptied, x.Table));

        RelationalDemoResult applied = await RelationalDemoSchema.ApplyAsync(connection, Token);

        applied.RowsWritten.Should().Be(
            (int) expected.Where(x => x.Emptied).Sum(x => before[x.Table]),
            "exactly the rows that were missing were written, and every leftover was recognised as already there");

        (await QuartzRowCountsAsync(connection)).Should().BeEquivalentTo(before);
        (await RelationalDemoSchema.ApplyAsync(connection, Token)).RowsWritten.Should().Be(0);
    }

    /// <summary>
    /// The two daily-time-interval triggers run on weekdays as Quartz.NET reads them back: each number in
    /// <c>str_prop_2</c> is cast straight to <see cref="DayOfWeek" />, where Sunday is zero.
    /// </summary>
    [PostgresFact]
    public async Task The_daily_interval_triggers_run_monday_to_friday()
    {
        await using NpgsqlConnection connection = await fixture.OpenAsync();

        List<string> days = await StringsAsync(
            connection,
            """
            select distinct s.str_prop_2 from quartz.qrtz_simprop_triggers s
            join quartz.qrtz_triggers t using (sched_name, trigger_name, trigger_group)
            where t.trigger_type = 'DAILY_I'
            """);

        // DailyTimeIntervalTriggerPersistenceDelegate.GetTriggerPropertyBundle: (DayOfWeek) int.Parse(num).
        days.Should().ContainSingle().Which.Split(',')
            .Select(x => (DayOfWeek) int.Parse(x, CultureInfo.InvariantCulture))
            .Should().Equal(DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday);
    }

    // -------------------------------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------------------------------

    private static readonly Regex DropStatement = new(
        @"\bdrop\s+(table|index|schema|view|materialized|function|procedure|aggregate|type|domain|sequence|trigger|constraint|column|extension)\b",
        RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(5));

    /// <summary>
    /// Every DROP statement or clause in a script, outside its <c>--</c> comments - which do talk about
    /// the DROP block that was removed, and are allowed to.
    /// </summary>
    private static List<string> DropStatements(string script) =>
    [
        .. script.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Select(line => line.Split("--", 2)[0])
            .Where(line => DropStatement.IsMatch(line)),
    ];

    /// <summary>Row counts of every readable relation the demo owns.</summary>
    internal static async Task<IReadOnlyDictionary<string, long>> RowCountsAsync(NpgsqlConnection connection)
    {
        List<(string Schema, string Name)> relations = [];

        await using (var list = new NpgsqlCommand(
            """
            select n.nspname::text, c.relname::text
            from pg_catalog.pg_class c join pg_catalog.pg_namespace n on n.oid = c.relnamespace
            where (n.nspname in ('quartz', 'legacy') or (n.nspname = 'studio_sample' and c.relname = 'app_settings'))
              and ((c.relkind in ('r', 'p') and not c.relispartition) or (c.relkind = 'm' and c.relispopulated))
            order by 1, 2
            """,
            connection))
        await using (NpgsqlDataReader reader = await list.ExecuteReaderAsync(Token))
        {
            while (await reader.ReadAsync(Token))
            {
                relations.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach ((string schema, string name) in relations)
        {
            counts[schema + "." + name] = await ScalarAsync(connection, "select count(*) from " + SqlIdentifier.Qualify(schema, name));
        }

        return counts;
    }

    /// <summary>Row counts of the Quartz tables, keyed by bare table name.</summary>
    private static async Task<IReadOnlyDictionary<string, long>> QuartzRowCountsAsync(NpgsqlConnection connection) =>
        (await RowCountsAsync(connection))
            .Where(static x => x.Key.StartsWith("quartz.", StringComparison.Ordinal))
            .ToDictionary(static x => x.Key["quartz.".Length..], static x => x.Value, StringComparer.Ordinal);

    /// <summary>
    /// Every foreign key into <c>mt_doc_customer</c>, as <c>table:name:on-delete:validity</c> - where
    /// on-delete is <c>pg_constraint.confdeltype</c>, <c>a</c> for NO ACTION and <c>c</c> for CASCADE.
    /// </summary>
    private static Task<List<string>> ForeignKeysIntoCustomerAsync(NpgsqlConnection connection) =>
        StringsAsync(
            connection,
            """
            select con.conrelid::regclass::text || ':' || con.conname::text || ':' || con.confdeltype::text || ':'
                   || case when con.convalidated then 'valid' else 'not valid' end
            from pg_catalog.pg_constraint con
            where con.contype = 'f' and con.confrelid = 'studio_sample.mt_doc_customer'::regclass
            order by 1
            """);

    /// <summary>What a schema the demo must not touch holds, so that "untouched" can be asserted as a whole.</summary>
    private static Task<Dictionary<string, long>> ForeignSchemaObjectsAsync(NpgsqlConnection connection) =>
        PairsAsync(
            connection,
            """
            select 'quartz tables', count(*) from pg_catalog.pg_class where relnamespace = 'quartz'::regnamespace and relkind = 'r'
            union all
            select 'quartz relations', count(*) from pg_catalog.pg_class where relnamespace = 'quartz'::regnamespace
            union all
            select 'quartz constraints', count(*) from pg_catalog.pg_constraint where connamespace = 'quartz'::regnamespace
            union all
            select 'quartz rows', (select count(*) from quartz.qrtz_job_details) + (select count(*) from quartz.qrtz_triggers)
                                  + (select count(*) from quartz.qrtz_locks) + (select count(*) from quartz.qrtz_calendars)
                                  + (select count(*) from quartz.qrtz_scheduler_state) + (select count(*) from quartz.qrtz_fired_triggers)
            union all
            select 'legacy relations', count(*) from pg_catalog.pg_class where relnamespace = 'legacy'::regnamespace
            union all
            select 'legacy types', count(*) from pg_catalog.pg_type where typnamespace = 'legacy'::regnamespace
            union all
            select 'legacy routines', count(*) from pg_catalog.pg_proc where pronamespace = 'legacy'::regnamespace
            """);

    /// <summary>A schema's comment, or <see langword="null" /> when it has none.</summary>
    private static async Task<string?> SchemaCommentAsync(NpgsqlConnection connection, string schema)
    {
        await using var command = new NpgsqlCommand(
            "select pg_catalog.obj_description(n.oid, 'pg_namespace') from pg_catalog.pg_namespace n where n.nspname = @p",
            connection);

        command.Parameters.AddWithValue("p", schema);

        return await command.ExecuteScalarAsync(Token) as string;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);

        await command.ExecuteNonQueryAsync(Token);
    }

    /// <summary>How many of each catalog object the demo's schemas hold, so a re-apply can be shown to add none.</summary>
    private static async Task<IReadOnlyDictionary<string, long>> ObjectCountsAsync(NpgsqlConnection connection) =>
        await PairsAsync(
            connection,
            """
            select 'class ' || c.relkind::text, count(*) from pg_catalog.pg_class c
            where c.relnamespace in ('quartz'::regnamespace, 'legacy'::regnamespace, 'studio_sample'::regnamespace)
            group by c.relkind
            union all
            select 'constraint ' || con.contype::text, count(*) from pg_catalog.pg_constraint con
            where con.connamespace in ('quartz'::regnamespace, 'legacy'::regnamespace, 'studio_sample'::regnamespace)
            group by con.contype
            union all
            select 'proc', count(*) from pg_catalog.pg_proc where pronamespace = 'legacy'::regnamespace
            union all
            select 'trigger ' || t.tgenabled::text, count(*) from pg_catalog.pg_trigger t
            join pg_catalog.pg_class c on c.oid = t.tgrelid
            where c.relnamespace = 'legacy'::regnamespace and not t.tgisinternal
            group by t.tgenabled
            union all
            select 'type', count(*) from pg_catalog.pg_type where typnamespace = 'legacy'::regnamespace
            """);

    internal static async Task<long> ScalarAsync(NpgsqlConnection connection, string sql, string? parameter = null)
    {
        await using var command = new NpgsqlCommand(sql, connection);

        if (parameter is not null)
        {
            command.Parameters.AddWithValue("p", parameter);
        }

        return Convert.ToInt64(await command.ExecuteScalarAsync(Token), CultureInfo.InvariantCulture);
    }

    private static async Task<string?> ScalarTextAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);

        // `as`, not a cast: a SQL null comes back as DBNull, and that is an answer here, not an error.
        return await command.ExecuteScalarAsync(Token) as string;
    }

    private static async Task<List<string>> StringsAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(Token);

        List<string> values = [];
        while (await reader.ReadAsync(Token))
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private static async Task<List<long>> LongsAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(Token);

        List<long> values = [];
        while (await reader.ReadAsync(Token))
        {
            values.Add(reader.GetInt64(0));
        }

        return values;
    }

    private static async Task<List<byte[]>> BytesAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(Token);

        List<byte[]> values = [];
        while (await reader.ReadAsync(Token))
        {
            values.Add(reader.GetFieldValue<byte[]>(0));
        }

        return values;
    }

    internal static async Task<Dictionary<string, long>> PairsAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(Token);

        var values = new Dictionary<string, long>(StringComparer.Ordinal);
        while (await reader.ReadAsync(Token))
        {
            values[reader.GetString(0)] = Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture);
        }

        return values;
    }
}

/// <summary>
/// The demo-data panel's two flows and the seeder's re-seed still work with a foreign key from
/// <c>legacy.customer_credit</c> into <c>mt_doc_customer</c> in place.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is the risky part.</b> Three things delete customers. The generator's truncation deletes
/// the generated ones with plain, batched <c>DELETE</c>s, which an inbound foreign key could refuse; the
/// credit rows point only at seeded customers and cascade anyway, so it must not. Marten's
/// <c>DeleteDocumentsByTypeAsync</c> - the seeder's re-seed path - runs <c>truncate … cascade</c>, which
/// empties every table holding a foreign key into <c>mt_doc_customer</c>, <c>legacy.customer_credit</c>
/// included; the relational seeding runs after it and must refill it. And the event-data truncation
/// touches none of this at all, which is worth proving rather than assuming.
/// </para>
/// <para>
/// One test, because it is a sequence of one-way doors on one database: a second test in this class
/// would be asserting about state the first had already changed.
/// </para>
/// </remarks>
/// <param name="fixture">The seeded database this class alone writes to.</param>
public class RelationalDemoFlowsLiveTests(RelationalDemoFlowsFixture fixture) : IClassFixture<RelationalDemoFlowsFixture>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [PostgresFact]
    public async Task Generate_truncate_and_re_seed_all_work_with_the_customer_foreign_key_in_place()
    {
        IDocumentStore store = fixture.Marten.Store;

        await using NpgsqlConnection connection = await fixture.OpenAsync();

        IReadOnlyDictionary<string, long> before = await RelationalDemoSchemaLiveTests.RowCountsAsync(connection);
        long credit = before["legacy.customer_credit"];
        credit.Should().BeGreaterThan(0);

        string creditVersion = await CreditRowVersionAsync(connection);

        DemoDataPlan plan = DemoDataPlan.For(DemoDataSize.Small) with { AnalyzeWhenDone = false };

        // Generate, then truncate the ordinary way: generated documents deleted, generated streams archived.
        await new DemoDataGenerator(store, plan).GenerateAsync(progress: null, Token);
        (await CustomersAsync(store)).Should().Be(SampleDataSeeder.CustomerCount + plan.Customers);

        DemoDataTruncation archived = await new DemoDataTruncator(store).TruncateAsync(deleteAllEventData: false, progress: null, Token);
        archived.Runs.Should().Be(1);
        archived.DocumentsDeleted.Should().Be(plan.DocumentTarget);

        // Generate again, and truncate the destructive way: every event in the store goes.
        await new DemoDataGenerator(store, plan).GenerateAsync(progress: null, Token);

        DemoDataTruncation deleted = await new DemoDataTruncator(store).TruncateAsync(deleteAllEventData: true, progress: null, Token);
        deleted.EventDataDeleted.Should().BeTrue();

        (await CustomersAsync(store)).Should().Be(SampleDataSeeder.CustomerCount, "only the generated customers went");
        (await RelationalDemoSchemaLiveTests.RowCountsAsync(connection)).Should().BeEquivalentTo(
            before, "nothing the generator or the truncation does reaches the relational demo");
        (await CreditRowVersionAsync(connection)).Should().Be(creditVersion, "the credit rows were never touched");

        // The re-seed path: an old marker makes the seeder clear the demo collections - Marten truncates
        // mt_doc_customer with CASCADE, which empties legacy.customer_credit - and write them again, after
        // which the relational seeding refills the credit rows.
        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Store(new SeedMarker { Id = SeedMarker.WellKnownId, Version = 1, SeededAt = DateTimeOffset.UtcNow });
            await session.SaveChangesAsync(Token);
        }

        await new SampleDataSeeder().Populate(store, Token);

        (await CreditRowVersionAsync(connection)).Should().NotBe(
            creditVersion, "the credit rows were cascaded away by Marten's truncate and written again");
        (await RelationalDemoSchemaLiveTests.ScalarAsync(connection, "select count(*) from legacy.customer_credit")).Should().Be(credit);
        (await RelationalDemoSchemaLiveTests.ScalarAsync(
            connection,
            "select count(*) from pg_catalog.pg_constraint where conname = 'customer_credit_customer_id_fkey' and convalidated"))
            .Should().Be(1);

        (await CustomersAsync(store)).Should().Be(SampleDataSeeder.CustomerCount);
        (await RelationalDemoSchemaLiveTests.RowCountsAsync(connection)).Should().BeEquivalentTo(before);

        // And deleting a customer through Marten, as the studio's delete does, is not refused because of
        // the credit row: the foreign key cascades.
        Guid withCredit = SampleDataSeeder.CustomerId(1);
        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Delete<Customer>(withCredit);
            await session.SaveChangesAsync(Token);
        }

        (await RelationalDemoSchemaLiveTests.ScalarAsync(
            connection, "select count(*) from legacy.customer_credit where customer_id = @p::uuid", withCredit.ToString("D")))
            .Should().Be(0);
    }

    private static async Task<int> CustomersAsync(IDocumentStore store)
    {
        await using IQuerySession session = store.QuerySession();

        return await session.Query<Customer>().CountAsync(Token);
    }

    /// <summary>
    /// The transaction ids that wrote the credit rows. They change only if the rows were deleted and
    /// written again, which is how "refilled" is told apart from "never emptied".
    /// </summary>
    private static async Task<string> CreditRowVersionAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand(
            "select pg_catalog.string_agg(distinct xmin::text, ',') from legacy.customer_credit",
            connection);

        return (string) (await command.ExecuteScalarAsync(Token))!;
    }
}

/// <summary>One line a <see cref="ListLogger{T}" /> was given.</summary>
/// <param name="Level">The level it was written at.</param>
/// <param name="Message">The rendered message.</param>
internal sealed record LoggedLine(LogLevel Level, string Message);

/// <summary>
/// A logger that keeps what it is given, for the one test that has to see what the sample's seeder says.
/// Hand-written: the package budget has no mocking library (AGENTS.md hard rule 1).
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

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (gate)
        {
            entries.Add(new LoggedLine(logLevel, formatter(state, exception)));
        }
    }
}
