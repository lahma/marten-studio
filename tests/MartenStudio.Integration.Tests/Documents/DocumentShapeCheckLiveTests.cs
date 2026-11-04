using System.Text.Json;

using Marten;
using Marten.Schema;

using MartenStudio.Services;
using MartenStudio.Services.Shapes;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Npgsql;

using PostgresStudio.Internal.Sql;

namespace MartenStudio.Integration.Tests.Documents;

/// <summary>
/// 1.2-SHAPE: the documents' shape check against a real Postgres, single tenancy. Rows are altered by raw SQL behind Marten's back
/// - a key it does not declare, a string where an int is declared, a discriminator no subclass claims - and the check is asked what
/// does not fit. It is a plain read: nothing is written and no DDL runs.
/// </summary>
public class DocumentShapeCheckLiveTests(DocumentShapeCheckLiveTests.Fixture fixture)
    : MartenTestBase(fixture), IClassFixture<DocumentShapeCheckLiveTests.Fixture>
{
    private readonly Fixture data = fixture;

    private const string Sentinel = "SENTINEL-7f3a";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>The store: one type of each shape the check meets.</summary>
    /// <param name="postgres">The assembly's container.</param>
    public sealed class Fixture(PostgresFixture postgres) : MartenClassFixture(postgres)
    {
        /// <summary>The ids of the corrupted rows, so a test names them.</summary>
        public Guid BadLegacy { get; } = Guid.NewGuid();

        /// <summary>Whether the lowest-keyed big document and the highest-keyed one are corrupted: their ids.</summary>
        public (Guid Lowest, Guid Highest) BigEdges { get; private set; }

        /// <inheritdoc />
        protected override bool SeedSampleData => false;

        /// <inheritdoc />
        protected override void ConfigureStore(StoreOptions options)
        {
            options.Schema.For<ShapeLiveOrder>();
            options.Schema.For<ShapeLiveSoft>().SoftDeleted();
            options.Schema.For<ShapeLiveBig>();
            options.Schema.For<ShapeLiveBigUnanalysed>();
            options.Schema.For<ShapeLiveBudget>();
            options.Schema.For<ShapeLiveHidden>();
            options.Schema.For<ShapeLiveVehicle>().AddSubClass<ShapeLiveCar>().AddSubClass<ShapeLiveTruck>();
        }

        /// <inheritdoc />
        protected override void ConfigureStudio(MartenStudioOptions options) =>
            options.IsDocumentTypeVisible = static type => type != typeof(ShapeLiveHidden);

        /// <inheritdoc />
        protected override async Task SeedAsync()
        {
            IDocumentStore store = Marten.Store;
            CancellationToken token = TestContext.Current.CancellationToken;

            await using NpgsqlConnection connection = await Postgres.OpenAsync(token);

            // No autovacuum on the two big tables, so "analysed" and "never analysed" are what the test says and not a race.
            foreach (string table in new[] { "shape_live_big", "shape_live_big_unanalysed" })
            {
                await ExecuteAsync(connection, $"alter table \"{Schema}\".\"mt_doc_{table}\" set (autovacuum_enabled = false)", token);
            }

            await using (IDocumentSession session = store.LightweightSession())
            {
                for (int i = 0; i < 5; i++)
                {
                    session.Store(new ShapeLiveOrder { Id = i == 0 ? BadLegacy : Guid.NewGuid(), Quantity = i, Name = "n" + i });
                }

                session.Store(new ShapeLiveOrder { Id = BadQuantity, Quantity = 1, Name = "bad" });

                await session.SaveChangesAsync(token);
            }

            await ExecuteAsync(
                connection,
                $"update \"{Schema}\".mt_doc_shape_live_order set data = jsonb_set(data, '{{Legacy}}', '\"{Sentinel}\"') where id = '{BadLegacy}'",
                token);
            await ExecuteAsync(
                connection,
                $"update \"{Schema}\".mt_doc_shape_live_order set data = jsonb_set(data, '{{Quantity}}', '\"{Sentinel}\"') where id = '{BadQuantity}'",
                token);

            // Soft deleted: two kept, one deleted - and the deleted one is the corrupt one.
            Guid deleted = Guid.NewGuid();

            await using (IDocumentSession session = store.LightweightSession())
            {
                session.Store(new ShapeLiveSoft { Id = Guid.NewGuid(), Quantity = 1 }, new ShapeLiveSoft { Id = Guid.NewGuid(), Quantity = 2 });
                session.Store(new ShapeLiveSoft { Id = deleted, Quantity = 3 });
                await session.SaveChangesAsync(token);
                session.Delete<ShapeLiveSoft>(deleted);
                await session.SaveChangesAsync(token);
            }

            await ExecuteAsync(
                connection,
                $"update \"{Schema}\".mt_doc_shape_live_soft set data = jsonb_set(data, '{{Quantity}}', '\"x\"') where id = '{deleted}'",
                token);

            await store.BulkInsertAsync(Enumerable.Range(0, 1000).Select(static i => new ShapeLiveBig { Id = Guid.NewGuid(), Quantity = i }).ToList(), cancellation: token);
            await store.BulkInsertAsync(Enumerable.Range(0, 300).Select(static i => new ShapeLiveBigUnanalysed { Id = Guid.NewGuid(), Quantity = i }).ToList(), cancellation: token);
            await ExecuteAsync(connection, $"analyze \"{Schema}\".\"mt_doc_shape_live_big\"", token);

            Guid lowest = (Guid) (await ScalarAsync(connection, $"select id from \"{Schema}\".mt_doc_shape_live_big order by id asc limit 1", token))!;
            Guid highest = (Guid) (await ScalarAsync(connection, $"select id from \"{Schema}\".mt_doc_shape_live_big order by id desc limit 1", token))!;
            BigEdges = (lowest, highest);

            await ExecuteAsync(
                connection,
                $"update \"{Schema}\".mt_doc_shape_live_big set data = jsonb_set(data, '{{Quantity}}', '\"x\"') where id in ('{lowest}', '{highest}')",
                token);

            await using (IDocumentSession session = store.LightweightSession())
            {
                session.Store(new ShapeLiveBudget { Id = Guid.NewGuid() }, new ShapeLiveBudget { Id = Guid.NewGuid() });
                session.Store(new ShapeLiveHidden { Id = Guid.NewGuid() });
                session.Store(new ShapeLiveCar { Id = BadCar, Doors = 4 }, new ShapeLiveCar { Id = Guid.NewGuid(), Doors = 2 });
                session.Store(new ShapeLiveTruck { Id = Guid.NewGuid(), Axles = 3 }, new ShapeLiveTruck { Id = StrayVehicle, Axles = 2 });
                await session.SaveChangesAsync(token);
            }

            await ExecuteAsync(
                connection,
                $"update \"{Schema}\".mt_doc_shape_live_vehicle set data = jsonb_set(data, '{{Doors}}', '\"many\"') where id = '{BadCar}'",
                token);
            await ExecuteAsync(
                connection,
                $"update \"{Schema}\".mt_doc_shape_live_vehicle set mt_doc_type = 'bicycle' where id = '{StrayVehicle}'",
                token);

            await ExecuteAsync(
                connection,
                $"create table \"{Schema}\".mt_doc_shape_discovered (id uuid primary key, data jsonb not null)",
                token);
        }

        /// <summary>The id of the order whose Quantity is a string.</summary>
        public Guid BadQuantity { get; } = Guid.NewGuid();

        /// <summary>The id of the car whose Doors is a string.</summary>
        public Guid BadCar { get; } = Guid.NewGuid();

        /// <summary>The id of the vehicle whose discriminator names no subclass.</summary>
        public Guid StrayVehicle { get; } = Guid.NewGuid();

        private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken token)
        {
            await using NpgsqlCommand command = new(sql, connection);
            await command.ExecuteNonQueryAsync(token);
        }

        private static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql, CancellationToken token)
        {
            await using NpgsqlCommand command = new(sql, connection);
            return await command.ExecuteScalarAsync(token);
        }
    }

    private string AliasOf<T>() => Store.Options.FindOrResolveDocumentType(typeof(T)).Alias;

    private async Task<ShapeCheckResult> CheckAsync(string alias, bool force = false)
    {
        using MartenFixture.ScopedService<IDocumentShapeCheck> check = Marten.Resolve<IDocumentShapeCheck>();
        return await check.Service.CheckAsync(Scope, alias, force, Token);
    }

    [PostgresFact]
    public async Task A_small_collection_is_read_whole_and_each_problem_is_found_without_a_value()
    {
        ShapeCheckResult result = await CheckAsync(AliasOf<ShapeLiveOrder>());

        result.Error.Should().BeNull();
        result.Coverage.Should().Be(ShapeCoverage.Complete);
        result.Examined.Should().Be(6);

        ShapeTypeResult order = result.Types.Should().ContainSingle().Subject;
        order.TypeName.Should().Be(nameof(ShapeLiveOrder));
        order.Examined.Should().Be(6);

        order.Members.Should().ContainSingle(x => x.Kind == ShapeMemberKind.Dropped && x.Path == "$.Legacy")
            .Which.Example.Id.Should().Be(data.BadLegacy.ToString());

        ShapeFailureGroup failure = order.Failures.Should().ContainSingle().Subject;
        failure.Path.Should().Be("$.Quantity");
        failure.StoredKind.Should().Be(JsonValueKind.String);
        failure.Example.Id.Should().Be(data.BadQuantity.ToString());

        // Value-free by construction, proved on the whole result: the planted value is nowhere in it.
        JsonSerializer.Serialize(result).Should().NotContain(Sentinel);
        result.Statements.Should().HaveCount(3).And.OnlyContain(x => !x.Sql.Contains(Sentinel, StringComparison.Ordinal));
    }

    [PostgresFact]
    public async Task A_soft_deleted_document_is_not_checked_and_the_result_says_so()
    {
        ShapeCheckResult result = await CheckAsync(AliasOf<ShapeLiveSoft>());

        result.Coverage.Should().Be(ShapeCoverage.Complete);
        result.Examined.Should().Be(2);
        result.LeftOut.Should().Contain(ShapeCheckText.SoftDeletedLeftOut);
        result.Types.Single().Failures.Should().BeEmpty("the corrupt document is the deleted one");
    }

    [PostgresFact]
    public async Task A_big_collection_after_analyze_is_sampled_from_both_edges_and_the_spread()
    {
        ShapeCheckResult result = await CheckAsync(AliasOf<ShapeLiveBig>());

        result.Error.Should().BeNull();
        result.Coverage.Should().Be(ShapeCoverage.Sampled);
        result.Examined.Should().BeInRange(100, ShapeCheckLimits.SampleSize);
        result.LowestPart.Should().Be(ShapeCheckLimits.Edge);
        result.HighestPart.Should().BeGreaterThan(10, "a few of the top keys are found by the spread first");
        result.SpreadPart.Should().BeGreaterThan(50);
        (result.LowestPart + result.HighestPart + result.SpreadPart).Should().Be(result.Examined);
        result.CoverageReason.Should().BeNull();

        ShapeTypeResult type = result.Types.Single();
        type.Failures.Should().ContainSingle().Which.Count.Should().Be(2, "the lowest and the highest id are the two corrupt rows");
        result.Statements.Select(static x => x.Sql).Should().Contain(x => x.Contains("histogram_bounds", StringComparison.Ordinal))
            .And.NotContain(x => x.Contains("random(", StringComparison.OrdinalIgnoreCase));
    }

    [PostgresFact]
    public async Task A_big_collection_never_analysed_reads_its_edges_and_says_why_nothing_else()
    {
        ShapeCheckResult result = await CheckAsync(AliasOf<ShapeLiveBigUnanalysed>());

        result.Coverage.Should().Be(ShapeCoverage.Sampled);
        result.Examined.Should().Be(2 * ShapeCheckLimits.Edge);
        result.SpreadPart.Should().Be(0);
        result.CoverageReason.Should().Be(ShapeCheckText.NotAnalysed);
    }

    [PostgresFact]
    public async Task A_hierarchy_reports_a_bad_car_under_car_only_and_an_unknown_discriminator_under_the_root()
    {
        ShapeCheckResult root = await CheckAsync(AliasOf<ShapeLiveVehicle>());

        root.Examined.Should().Be(4);
        root.Types.Single(x => x.TypeName == nameof(ShapeLiveCar)).Failures.Should().ContainSingle()
            .Which.Example.Id.Should().Be(data.BadCar.ToString());
        root.Types.Single(x => x.TypeName == nameof(ShapeLiveTruck)).Failures.Should().BeEmpty();

        ShapeTypeResult stray = root.Types.Single(x => x.TypeName == nameof(ShapeLiveVehicle));
        stray.Failures.Should().ContainSingle().Which.Should().Match<ShapeFailureGroup>(
            x => x.Kind == ShapeFailureKind.UnknownSubclass && x.Detail == "bicycle" && x.Example.Id == data.StrayVehicle.ToString());

        string carAlias = Store.Options.FindOrResolveDocumentType(typeof(ShapeLiveVehicle)).AliasFor(typeof(ShapeLiveCar));
        ShapeCheckResult cars = await CheckAsync(carAlias);

        cars.Examined.Should().Be(2, "the subclass alias reads only its own discriminator");
        cars.Types.Should().ContainSingle().Which.TypeName.Should().Be(nameof(ShapeLiveCar));
    }

    [PostgresFact]
    public async Task A_hidden_type_a_discovered_table_and_an_unknown_alias_get_the_same_sentence_and_no_read()
    {
        ShapeCheckResult hidden = await CheckAsync(AliasOf<ShapeLiveHidden>());
        ShapeCheckResult discovered = await CheckAsync("shape_discovered");
        ShapeCheckResult unknown = await CheckAsync("nothing_like_it");

        foreach (ShapeCheckResult result in new[] { hidden, discovered, unknown })
        {
            result.Coverage.Should().Be(ShapeCoverage.NotChecked);
            result.Statements.Should().BeEmpty("the refusal is before any database read");
            result.Examined.Should().Be(0);
        }

        hidden.CoverageReason.Should().Be(ShapeCheckText.NoSuchDocumentType(AliasOf<ShapeLiveHidden>()));
        discovered.CoverageReason.Should().Be(ShapeCheckText.NoSuchDocumentType("shape_discovered"));
        unknown.CoverageReason.Should().Be(ShapeCheckText.NoSuchDocumentType("nothing_like_it"));
    }

    [PostgresFact]
    public async Task A_check_creates_and_changes_no_object_in_the_database()
    {
        string before = await ObjectsAsync();

        await CheckAsync(AliasOf<ShapeLiveBudget>());
        await CheckAsync(AliasOf<ShapeLiveOrder>(), force: true);

        (await ObjectsAsync()).Should().Be(before);
    }

    [PostgresFact]
    public async Task A_budget_that_has_run_out_gives_a_partial_result_and_not_a_throw()
    {
        using IServiceScope scope = Services.CreateScope();
        IServiceProvider provider = scope.ServiceProvider;

        DocumentShapeCheckService service = new(
            provider.GetRequiredService<IOptions<MartenStudioOptions>>(),
            provider.GetRequiredService<StudioScopeResolver>(),
            provider.GetRequiredService<ColumnCatalog>(),
            new ShapeCheckCache(),
            NullLogger<DocumentShapeCheckService>.Instance)
        {
            Budget = TimeSpan.Zero,
        };

        ShapeCheckResult result = await service.CheckAsync(Scope, AliasOf<ShapeLiveBudget>(), false, Token);

        result.Coverage.Should().Be(ShapeCoverage.Partial);
        result.CoverageReason.Should().Contain("budget ran out");
        result.Error.Should().BeNull();
    }

    [PostgresFact]
    public async Task A_second_check_inside_five_minutes_is_the_same_result_and_check_again_inside_thirty_seconds_is_too()
    {
        string alias = AliasOf<ShapeLiveSoft>();

        ShapeCheckResult first = await CheckAsync(alias);
        ShapeCheckResult second = await CheckAsync(alias);
        ShapeCheckResult again = await CheckAsync(alias, force: true);

        second.Should().BeSameAs(first);
        again.Should().BeSameAs(first);
    }

    private async Task<string> ObjectsAsync()
    {
        await using NpgsqlConnection connection = await Postgres.OpenAsync(Token);
        await using NpgsqlCommand command = new(
            "select string_agg(n.nspname || '.' || c.relname || ':' || c.relkind::text || ':' || c.relnatts::text, ',' order by n.nspname, c.relname) "
            + "from pg_catalog.pg_class c join pg_catalog.pg_namespace n on n.oid = c.relnamespace where n.nspname like @schema",
            connection);
        command.Parameters.AddWithValue("schema", Schema + "%");

        return (string) (await command.ExecuteScalarAsync(Token))!;
    }
}

[DocumentAlias("shape_live_order")]
public class ShapeLiveOrder
{
    public Guid Id { get; set; }

    public int Quantity { get; set; }

    public string Name { get; set; } = string.Empty;
}

[DocumentAlias("shape_live_soft")]
public class ShapeLiveSoft
{
    public Guid Id { get; set; }

    public int Quantity { get; set; }
}

[DocumentAlias("shape_live_big")]
public class ShapeLiveBig
{
    public Guid Id { get; set; }

    public int Quantity { get; set; }
}

[DocumentAlias("shape_live_big_unanalysed")]
public class ShapeLiveBigUnanalysed
{
    public Guid Id { get; set; }

    public int Quantity { get; set; }
}

[DocumentAlias("shape_live_budget")]
public class ShapeLiveBudget
{
    public Guid Id { get; set; }
}

[DocumentAlias("shape_live_hidden")]
public class ShapeLiveHidden
{
    public Guid Id { get; set; }
}

[DocumentAlias("shape_live_vehicle")]
public class ShapeLiveVehicle
{
    public Guid Id { get; set; }
}

public class ShapeLiveCar : ShapeLiveVehicle
{
    public int Doors { get; set; }
}

public class ShapeLiveTruck : ShapeLiveVehicle
{
    public int Axles { get; set; }
}
