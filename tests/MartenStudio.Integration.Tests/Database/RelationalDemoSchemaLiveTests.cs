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
    /// drops it nor empties it, nor the foreign key another schema holds into its customer table.
    /// </summary>
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
        (await ScalarAsync(
            connection,
            "select count(*) from pg_catalog.pg_constraint where conname = 'customer_credit_customer_id_fkey' and confrelid = 'studio_sample.mt_doc_customer'::regclass"))
            .Should().Be(1);
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

        return (string?) await command.ExecuteScalarAsync(Token);
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
/// the generated ones with a plain <c>DELETE</c>, which an inbound foreign key could refuse; the
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
