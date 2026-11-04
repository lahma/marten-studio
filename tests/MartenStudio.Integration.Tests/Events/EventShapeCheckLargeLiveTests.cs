using JasperFx.Events;

using Marten;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Shapes;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using PostgresStudio.Internal.Sql;

using static MartenStudio.Integration.Tests.Events.EventShapeLiveSupport;

namespace MartenStudio.Integration.Tests.Events;

/// <summary>
/// 1.2-SHAPE-B: the events' shape check on event tables big enough to be sampled - tens of thousands of events after <c>ANALYZE</c> -
/// at every tenancy the events table has, with and without the index on <c>type</c>. The lesson of the documents' check is that an
/// ordering which is not an index order (a conjoined primary key read by <c>id</c> alone) is a sequential scan and a sort of the whole
/// table: here the claim is that every statement of the check reads <c>mt_events</c> in an index order, which <c>EXPLAIN (VERBOSE,
/// COSTS OFF)</c> shows, and that the check finishes in seconds.
/// </summary>
public abstract class EventShapeCheckLargeLiveTests(EventShapeCheckLargeLiveTests.LargeFixture fixture) : MartenTestBase(fixture)
{
    private readonly LargeFixture data = fixture;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>The store: 15,000 events a tenant, of one common type and one rare one, with a corrupt event at each end.</summary>
    /// <param name="postgres">The assembly's container.</param>
    public abstract class LargeFixture(PostgresFixture postgres) : MartenClassFixture(postgres)
    {
        /// <summary>Events per tenant (45,000 on a single-tenanted store).</summary>
        public const int PerTenant = 15_000;

        /// <summary>Whether <c>EnableEventTypeIndex</c> is on.</summary>
        public abstract bool TypeIndex { get; }

        /// <summary>Whether events are conjoined-tenanted.</summary>
        public virtual bool Conjoined => false;

        /// <summary>Whether <c>UseTenantPartitionedEvents</c> is on: each tenant numbers its own events.</summary>
        public virtual bool Partitioned => false;

        /// <summary>The tenants; a single-tenanted store has the default one.</summary>
        public string[] Tenants => Conjoined ? ["acme", "globex", "initech"] : ["DEFAULT"];

        /// <summary>How many events each tenant holds.</summary>
        public int Count => Conjoined ? PerTenant : 3 * PerTenant;

        /// <inheritdoc />
        protected override bool SeedSampleData => false;

        /// <inheritdoc />
        protected override void ConfigureStore(StoreOptions options)
        {
            options.Events.MapEventType<ShapeCommon>("shape_common");
            options.Events.MapEventType<ShapeRare>("shape_rare");
            options.Events.EnableEventTypeIndex = TypeIndex;

            if (Conjoined)
            {
                options.Events.TenancyStyle = JasperFx.MultiTenancy.TenancyStyle.Conjoined;
                options.Policies.AllDocumentsAreMultiTenanted();
            }

            if (Partitioned)
            {
                options.Events.UseTenantPartitionedEvents = true;
                options.Events.AppendMode = EventAppendMode.QuickWithServerTimestamps;
            }
        }

        /// <inheritdoc />
        protected override async Task SeedAsync()
        {
            CancellationToken token = TestContext.Current.CancellationToken;

            if (Partitioned)
            {
                await Marten.Store.Advanced.AddMartenManagedTenantsAsync(token, Tenants);
            }

            await using NpgsqlConnection connection = await Postgres.OpenAsync(token);
            string events = EventsTable(Schema);
            string streams = $"\"{Schema}_events\".mt_streams";

            for (int index = 0; index < Tenants.Length; index++)
            {
                string tenant = Tenants[index];
                // Interleaved, as a conjoined store's global sequence is: tenant k of K owns every K-th number.
                string sequence = Partitioned ? "n" : $"n * {Tenants.Length} + {index}";

                await ExecuteAsync(
                    connection,
                    $"insert into {streams} (id, type, version, tenant_id) select md5('{tenant}:' || s)::uuid, 'shape', 0, '{tenant}' from generate_series(0, 149) s",
                    token);

                await ExecuteAsync(
                    connection,
                    $"insert into {events} (seq_id, id, stream_id, version, data, type, timestamp, mt_dotnet_type, is_archived, tenant_id) "
                    + $"select {sequence}, gen_random_uuid(), md5('{tenant}:' || (n % 150))::uuid, n / 150 + 1, jsonb_build_object('N', n), "
                    + "case when n % 60 = 0 then 'shape_rare' else 'shape_common' end, now(), "
                    + "case when n % 60 = 0 then '" + typeof(ShapeRare).FullName + ", " + typeof(ShapeRare).Assembly.GetName().Name + "' "
                    + "else '" + typeof(ShapeCommon).FullName + ", " + typeof(ShapeCommon).Assembly.GetName().Name + "' end, "
                    + $"false, '{tenant}' from generate_series(1, {Count}) n",
                    token);
            }

            // One corrupt common event at each end of the key order.
            await Corrupt(connection, events, Tenants[0], 1, token);
            await Corrupt(connection, events, Tenants[^1], Count - 1, token);

            await ExecuteAsync(connection, $"analyze {events}", token);
        }

        private async Task Corrupt(NpgsqlConnection connection, string events, string tenant, int n, CancellationToken token)
        {
            long sequence = Partitioned ? n : ((long) n * Tenants.Length) + Array.IndexOf(Tenants, tenant);

            await ExecuteAsync(
                connection,
                $"update {events} set data = '{{\"N\":\"x\"}}'::jsonb where tenant_id = '{tenant}' and seq_id = {sequence}",
                token);
        }
    }

    private async Task<ShapeCheckResult> CheckAsync(string eventType, string? tenant)
    {
        using MartenFixture.ScopedService<IEventShapeCheck> check = Marten.Resolve<IEventShapeCheck>();
        return await check.Service.CheckAsync(MartenFixture.ScopeFor(tenant), eventType, false, Token);
    }

    private bool SpreadAllowed(string? tenant) => data.TypeIndex && !(data.Partitioned && tenant is null);

    [PostgresFact]
    public async Task The_common_type_with_every_tenant_in_scope_is_sampled_from_both_ends_in_seconds_and_finds_both_corrupt_events()
    {
        ShapeCheckResult result = await CheckAsync("shape_common", null);

        result.Error.Should().BeNull();
        result.Coverage.Should().Be(ShapeCoverage.Sampled, "not Partial: every statement is an index order and finishes");
        result.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3));

        if (SpreadAllowed(null))
        {
            result.SpreadPart.Should().BeGreaterThan(50);
            result.CoverageReason.Should().BeNull();
        }
        else
        {
            result.SpreadPart.Should().Be(0);
            result.Examined.Should().Be(2 * ShapeCheckLimits.EdgeWithoutTypeIndex);
            result.CoverageReason.Should().Be(data.TypeIndex ? ShapeCheckText.EventsAllTenantsEdgesOnly : ShapeCheckText.NoTypeIndex);
            result.Remedy.Should().Be(data.TypeIndex ? ShapeCheckText.UpcasterRemedy : ShapeCheckText.UpcasterRemedy + " " + ShapeCheckText.TypeIndexRemedy);
        }

        result.Types.Single().Failures.Sum(static x => x.Count).Should().Be(2);
    }

    [PostgresFact]
    public async Task One_tenants_sample_holds_only_that_tenants_events_and_is_sampled_in_seconds()
    {
        if (data.Tenants.Length == 1)
        {
            return;
        }

        string tenant = data.Tenants[0];
        ShapeCheckResult result = await CheckAsync("shape_common", tenant);

        result.Error.Should().BeNull();
        result.Coverage.Should().Be(ShapeCoverage.Sampled);
        result.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3));
        result.Types.Single().Failures.Should().ContainSingle().Which.Example.TenantId.Should().Be(tenant);
        result.Types.Single().Failures.Sum(static x => x.Count).Should().Be(1, "the other end's corrupt event is another tenant's");

        if (data.TypeIndex)
        {
            result.SpreadPart.Should().BeGreaterThan(50, "with a tenant in scope the spread probes that tenant's own sequence line");
        }
    }

    [PostgresFact]
    public async Task The_rare_type_is_checked_in_seconds_or_stops_at_the_statement_timeout_and_says_so()
    {
        ShapeCheckResult result = await CheckAsync("shape_rare", null);

        // 250 events a tenant of a rare type: more than a check reads whole, and found in seconds - by the index on type where there is one, and
        // by walking the primary key where there is not (its plan is the test below). That the walk can run to the statement timeout on a
        // table big enough to need it, and says so, is EventShapeCheckLiveTests' forced-timeout test.
        result.Error.Should().BeNull();
        result.Coverage.Should().Be(ShapeCoverage.Sampled);
        result.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3));
        result.CoverageReason.Should().Be(
            data.TypeIndex ? (data.Partitioned ? ShapeCheckText.EventsAllTenantsEdgesOnly : null) : ShapeCheckText.NoTypeIndex);
    }

    [PostgresFact]
    public async Task Every_statement_reads_mt_events_in_an_index_order_not_a_sequential_scan_and_a_sort()
    {
        await using NpgsqlConnection connection = await Postgres.OpenAsync(Token);
        ColumnCatalog catalog = Services.GetRequiredService<ColumnCatalog>();

        EventTableInfo table = EventTableInfo.FromColumns(
            Schema + "_events",
            StreamIdentity.AsGuid,
            await catalog.GetAsync(connection, Schema + "_events", "mt_events", Token),
            await catalog.GetAsync(connection, Schema + "_events", "mt_streams", Token));

        string[] fields = ["data", "type", "mt_dotnet_type", "bdata", "seq_id", "id", "stream_id", "version", "timestamp", "tenant_id", "is_archived"];
        List<string?> scopes = [null];

        if (data.Tenants.Length > 1)
        {
            scopes.Add(data.Tenants[0]);
        }

        foreach (string type in new[] { "shape_common", "shape_rare" })
        {
            foreach (string? tenant in scopes)
            {
                EventShapeQueries.EventScope scope = new(tenant, false);
                using NpgsqlCommand edges = EventShapeQueries.BuildEdgeKeys(table, type, scope, ShapeCheckLimits.Edge, 30);
                using NpgsqlCommand spread = EventShapeQueries.BuildSpreadKeys(table, type, scope, [5_000, 9_000], 30);
                using NpgsqlCommand rows = EventShapeQueries.BuildRows(
                    table, type, scope, fields, data.Partitioned, [1, 2, 3], [tenant ?? data.Tenants[0], tenant ?? data.Tenants[0], tenant ?? data.Tenants[0]], ShapeCheckLimits.MaxItemBytes, 30);

                string edgePlan = await ExplainAsync(edges);
                string spreadPlan = await ExplainAsync(spread);
                string rowPlan = await ExplainAsync(rows);

                string where = $"{type} for {tenant ?? "all tenants"}, type index {data.TypeIndex}, partitioned {data.Partitioned}";

                // Even a rare type with no index on type is walked in primary-key order here; at a size where the planner prefers
                // otherwise the statement is bounded by statement_timeout, and the check says so (Partial, with the remedy).
                edgePlan.Should().NotContain("Seq Scan", where).And.Contain("Index Scan");
                HasSortNode(edgePlan).Should().BeFalse($"the edges of {where} are an index order:\n{edgePlan}");
                spreadPlan.Should().NotContain("Seq Scan", where);
                HasSortNode(spreadPlan).Should().BeFalse($"the spread of {where} is an index order:\n{spreadPlan}");
                rowPlan.Should().NotContain("Seq Scan", where);

                // The text-size guard and the shape_json column read the lateral's one rendering of data::text (the double parentheses
                // EXPLAIN prints for a reference to a child's column), and do not render the jsonb again.
                System.Text.RegularExpressions.Regex.Count(rowPlan, @"(?<!\()\(e(_\d+)?\.data\)::text")
                    .Should().Be(1, $"the jsonb is rendered as text once a row, not once for the guard and once for the column:\n{rowPlan}");
            }
        }
    }

    /// <summary>Whether the plan has a Sort node: not the "Sort Key:" line of a Merge Append, which merges index-ordered children.</summary>
    private static bool HasSortNode(string plan) =>
        System.Text.RegularExpressions.Regex.IsMatch(plan, @"(^|->\s+)(Incremental )?Sort\s*$", System.Text.RegularExpressions.RegexOptions.Multiline);

    private async Task<string> ExplainAsync(NpgsqlCommand source)
    {
        await using NpgsqlConnection connection = await Postgres.OpenAsync(Token);
        await using NpgsqlCommand command = new("explain (verbose, costs off) " + source.CommandText, connection);

        foreach (NpgsqlParameter parameter in source.Parameters)
        {
            command.Parameters.Add(new NpgsqlParameter(parameter.ParameterName, parameter.NpgsqlDbType) { Value = parameter.Value });
        }

        List<string> lines = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(Token);

        while (await reader.ReadAsync(Token))
        {
            lines.Add(reader.GetString(0));
        }

        return string.Join('\n', lines);
    }
}

/// <summary>Single tenancy, no index on type.</summary>
public class EventShapeCheckLargeSingleLiveTests(EventShapeCheckLargeSingleLiveTests.Fixture fixture)
    : EventShapeCheckLargeLiveTests(fixture), IClassFixture<EventShapeCheckLargeSingleLiveTests.Fixture>
{
    /// <summary>The store.</summary>
    /// <param name="postgres">The assembly's container.</param>
    public sealed class Fixture(PostgresFixture postgres) : LargeFixture(postgres)
    {
        /// <inheritdoc />
        public override bool TypeIndex => false;
    }
}

/// <summary>Single tenancy, with the index on type.</summary>
public class EventShapeCheckLargeSingleIndexedLiveTests(EventShapeCheckLargeSingleIndexedLiveTests.Fixture fixture)
    : EventShapeCheckLargeLiveTests(fixture), IClassFixture<EventShapeCheckLargeSingleIndexedLiveTests.Fixture>
{
    /// <summary>The store.</summary>
    /// <param name="postgres">The assembly's container.</param>
    public sealed class Fixture(PostgresFixture postgres) : LargeFixture(postgres)
    {
        /// <inheritdoc />
        public override bool TypeIndex => true;
    }
}

/// <summary>Conjoined events, three tenants, no index on type.</summary>
public class EventShapeCheckLargeConjoinedLiveTests(EventShapeCheckLargeConjoinedLiveTests.Fixture fixture)
    : EventShapeCheckLargeLiveTests(fixture), IClassFixture<EventShapeCheckLargeConjoinedLiveTests.Fixture>
{
    /// <summary>The store.</summary>
    /// <param name="postgres">The assembly's container.</param>
    public sealed class Fixture(PostgresFixture postgres) : LargeFixture(postgres)
    {
        /// <inheritdoc />
        public override bool TypeIndex => false;

        /// <inheritdoc />
        public override bool Conjoined => true;
    }
}

/// <summary>Conjoined events, three tenants, with the index on type.</summary>
public class EventShapeCheckLargeConjoinedIndexedLiveTests(EventShapeCheckLargeConjoinedIndexedLiveTests.Fixture fixture)
    : EventShapeCheckLargeLiveTests(fixture), IClassFixture<EventShapeCheckLargeConjoinedIndexedLiveTests.Fixture>
{
    /// <summary>The store.</summary>
    /// <param name="postgres">The assembly's container.</param>
    public sealed class Fixture(PostgresFixture postgres) : LargeFixture(postgres)
    {
        /// <inheritdoc />
        public override bool TypeIndex => true;

        /// <inheritdoc />
        public override bool Conjoined => true;
    }
}

/// <summary>Tenant-partitioned events, three tenants numbering their own, no index on type.</summary>
public class EventShapeCheckLargePartitionedLiveTests(EventShapeCheckLargePartitionedLiveTests.Fixture fixture)
    : EventShapeCheckLargeLiveTests(fixture), IClassFixture<EventShapeCheckLargePartitionedLiveTests.Fixture>
{
    /// <summary>The store.</summary>
    /// <param name="postgres">The assembly's container.</param>
    public sealed class Fixture(PostgresFixture postgres) : LargeFixture(postgres)
    {
        /// <inheritdoc />
        public override bool TypeIndex => false;

        /// <inheritdoc />
        public override bool Conjoined => true;

        /// <inheritdoc />
        public override bool Partitioned => true;
    }
}

/// <summary>Tenant-partitioned events, three tenants numbering their own, with the index on type.</summary>
public class EventShapeCheckLargePartitionedIndexedLiveTests(EventShapeCheckLargePartitionedIndexedLiveTests.Fixture fixture)
    : EventShapeCheckLargeLiveTests(fixture), IClassFixture<EventShapeCheckLargePartitionedIndexedLiveTests.Fixture>
{
    /// <summary>The store.</summary>
    /// <param name="postgres">The assembly's container.</param>
    public sealed class Fixture(PostgresFixture postgres) : LargeFixture(postgres)
    {
        /// <inheritdoc />
        public override bool TypeIndex => true;

        /// <inheritdoc />
        public override bool Conjoined => true;

        /// <inheritdoc />
        public override bool Partitioned => true;
    }
}

/// <summary>An event type that fits.</summary>
public sealed class ShapeCommon
{
    public int N { get; set; }
}

/// <summary>A rare event type that fits.</summary>
public sealed class ShapeRare
{
    public int N { get; set; }
}
