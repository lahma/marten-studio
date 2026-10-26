using Bunit;

using MartenStudio.Services;
using MartenStudio.Tests.Components;
using MartenStudio.Tests.Database;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Sources;
using PostgresStudio.Services.Storage;

namespace MartenStudio.Tests.Storage;

/// <summary>
/// The Storage page in MartenStudio (W5-CAP), over views built by the real per-visitor step (<c>StorageService.ShowAsync</c>)
/// from stated facts: its tiles, a table in a withheld schema listed without its name (D27), a hidden document type's table
/// absent, the "needs a day" text before the window has counted a day, the store policy's refusal, and nothing read while
/// prerendered.
/// </summary>
public class StoragePageTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A document type the host hides from the studio.</summary>
    private sealed class HiddenDocument;

    /// <summary>What the page is handed, and what it asked.</summary>
    private sealed class FakeStorageService : IStorageService
    {
        public StorageView View { get; set; } = StorageView.Refused(DatabaseRefusal.Unavailable, "no view set");

        public List<StudioScope> Requests { get; } = [];

        public Task<StorageView> GetAsync(StudioScope scope, CancellationToken cancellationToken = default)
        {
            Requests.Add(scope);
            return Task.FromResult(View);
        }
    }

    private static StatisticsWindow Window(TimeSpan length) => new()
    {
        MeasuredAt = Now,
        Since = Now - length,
        Basis = StatisticsBasis.ServerReset,
        Reason = StatisticsResetReason.ServerStarted,
        SharedView = "pg_stat_checkpointer",
    };

    private static readonly StudioTable Orders = new("public", "orders", StudioTableKind.Relational, StudioTableOwnership.Owned);

    private static readonly StudioTable Salaries = new("hr", "salaries", StudioTableKind.Relational, StudioTableOwnership.Other) { GatedSchemas = ["hr"] };

    private static readonly StudioTable Triggers = new("quartz", "qrtz_triggers", StudioTableKind.Relational, StudioTableOwnership.Library)
    {
        GatedSchemas = ["quartz"],
        Library = "Quartz.NET",
    };

    private static readonly StudioTable Hidden = new("public", "mt_doc_hiddendocument", StudioTableKind.DocumentTable, StudioTableOwnership.Owned)
    {
        DocumentTypes = [typeof(HiddenDocument)],
        TypeName = "HiddenDocument",
    };

    private static StorageFacts Facts(TimeSpan window) => StorageFacts.Build(
        "default",
        "localhost.marten",
        Window(window),
        new StorageDatabaseRow(DatabaseBytes: 400L << 20, EstimatedBytes: 360L << 20, BlockSize: 8192, ServerVersion: 170_011),
        new Set([Orders, Salaries, Triggers, Hidden]),
        [
            Row("public", "orders", heap: 120L << 20, index: 40L << 20, toast: 0, rows: 900_000, inserts: 120_000, deletes: 20_000),
            Row("hr", "salaries", heap: 60L << 20, index: 6L << 20, toast: 0, rows: 50_000),
            Row("quartz", "qrtz_triggers", heap: 10L << 20, index: 2L << 20, toast: 0, rows: 4_000),
            Row("public", "mt_doc_hiddendocument", heap: 200L << 20, index: 10L << 20, toast: 30L << 20, rows: 2_000_000),
        ]);

    private static StorageTableRow Row(string schema, string table, long heap, long index, long toast, long rows, long? inserts = null, long? deletes = null) => new()
    {
        Schema = schema,
        Table = table,
        HeapBytes = heap,
        IndexBytes = index,
        ToastBytes = toast,
        EstimatedRows = rows,
        Inserts = inserts,
        Deletes = deletes,
        Updates = 1_000,
        HotUpdates = 400,
    };

    /// <summary>The visitor's database-browser gate: <c>public</c> is the store's, <c>quartz</c> is browsable, <c>hr</c> is withheld.</summary>
    private static DatabaseGate Gate() => new(
        ["public"],
        [new CatalogSchema("public", true, false), new CatalogSchema("quartz", true, false), new CatalogSchema("hr", true, false)],
        ["quartz"],
        capabilityEnabled: true,
        readOnly: false,
        authorized: true,
        new DatabaseObjectClassifier([]));

    private static StorageService Service(MartenStudioOptions options) => new(
        MartenStudioSettings.Of(options),
        MartenStudioDescriptor.Instance,
        authorization: null!,
        resolver: null!,
        facts: null!,
        new StudioLogThrottle(TimeProvider.System),
        NullLogger<StorageService>.Instance);

    private static MartenStudioOptions Hiding() => new() { IsDocumentTypeVisible = static type => type != typeof(HiddenDocument) };

    private static async Task<StudioComponentContext> ContextAsync(FakeStorageService storage) =>
        await new StudioComponentContext(services => services.AddSingleton<IStorageService>(storage)).ReadyAsync();

    [Fact]
    public async Task The_view_names_own_and_browsable_tables_withholds_the_rest_and_drops_a_hidden_type()
    {
        int gateReads = 0;
        StorageView view = await Service(Hiding()).ShowAsync(Facts(TimeSpan.FromDays(3)), new StudioScope("default", "localhost.marten", null), () =>
        {
            gateReads++;
            return Task.FromResult<DatabaseGate?>(Gate());
        });

        view.Tables.Select(static x => x.Name).Should().Equal(["orders", null, "qrtz_triggers"], "largest first, the hidden type's table absent");
        view.Tables[1].Should().Match<StorageTableView>(static x => x.Schema == null && x.Link == null && x.Owner == null && x.TotalBytes == 66L << 20);
        view.Tables[2].Owner.Should().Be("Quartz.NET");
        view.WithheldReason.Should().Be(
            "1 table is shown without its name: it sits in a schema the database browser does not show you (it needs "
            + "MartenStudioOptions.Capabilities.BrowseDatabase, the write policy and a BrowsableSchemas entry for that schema).");
        gateReads.Should().Be(1, "the browser's gate is read once per view");
        view.Tables[0].ShareOfDatabase.Should().BeApproximately(0.4, 0.0001, "160 MiB of 400 MiB measured");
    }

    [Fact]
    public async Task With_no_table_outside_the_stores_schemas_the_gate_is_never_read()
    {
        StorageFacts facts = StorageFacts.Build("default", "db", Window(TimeSpan.FromHours(1)),
            new StorageDatabaseRow(1L << 30, 1L << 30, 8192, 170_011), new Set([Orders]), [Row("public", "orders", 1, 0, 0, 1)]);

        StorageView view = await Service(new MartenStudioOptions()).ShowAsync(facts, new StudioScope("default", "db", null),
            static () => throw new InvalidOperationException("the gate was read for no reason"));

        view.Tables.Should().ContainSingle().Which.Name.Should().Be("orders");
    }

    [Fact]
    public async Task A_gate_that_cannot_be_read_withholds_every_name_outside_the_stores_schemas()
    {
        StorageView view = await Service(Hiding()).ShowAsync(Facts(TimeSpan.FromDays(3)), new StudioScope("default", "localhost.marten", null),
            static () => Task.FromResult<DatabaseGate?>(null));

        view.Tables.Select(static x => x.Name).Should().Equal(["orders", null, null]);
        view.WithheldReason.Should().StartWith("2 tables are shown without their names: they sit in a schema outside the store's own")
            .And.EndWith("could not be checked against the database browser's gate for you.");
    }

    [Fact]
    public async Task The_page_draws_the_tiles_the_withheld_row_and_no_hidden_type()
    {
        FakeStorageService storage = new()
        {
            View = await Service(Hiding()).ShowAsync(Facts(TimeSpan.FromDays(3)), new StudioScope("default", "localhost.marten", null),
                static () => Task.FromResult<DatabaseGate?>(Gate())),
        };
        await using StudioComponentContext context = await ContextAsync(storage);

        var page = context.Render<PostgresStudio.Components.Pages.Storage.Storage>();
        page.WaitForAssertion(() => page.FindAll(".ms-storage-tiles").Should().ContainSingle());

        page.TextOfAll(".ms-storage-tile dt").Should().Equal("Database size", "Growth a day", "Growth a month", "Statistics since", "Tables");
        page.Find(".ms-storage-tile-value").TextContent.Should().Be("400 MiB");
        page.TextOfAll(".ms-storage-tile-value")[1].Should().StartWith("+", "100 000 net rows over three days");
        page.Markup.Should().Contain("at the current daily rate").And.Contain("From pg_stat_checkpointer.stats_reset");

        string[] firstCells = [.. page.FindAll(".ms-storage-table tbody tr td:first-child").Select(static x => x.TextContent.Trim())];
        firstCells.Should().Equal("public.orders", WithheldNames.Token, "quartz.qrtz_triggers");
        page.Markup.Should().NotContain("salaries").And.NotContain("mt_doc_hiddendocument").And.NotContain("HiddenDocument");
        page.Find(".ms-storage-withheld").TextContent.Should().Contain("MartenStudioOptions.Capabilities.BrowseDatabase");
        page.Find(".ms-storage-table tbody tr td:first-child a").GetAttribute("href").Should().Contain("database/object").And.Contain("name=orders");
        page.Markup.Should().Contain("ms-storage-bar");
    }

    [Fact]
    public async Task Before_a_day_of_statistics_the_growth_tiles_say_how_long_so_far()
    {
        FakeStorageService storage = new()
        {
            View = await Service(Hiding()).ShowAsync(Facts(TimeSpan.FromHours(5)), new StudioScope("default", "localhost.marten", null),
                static () => Task.FromResult<DatabaseGate?>(Gate())),
        };
        await using StudioComponentContext context = await ContextAsync(storage);

        var page = context.Render<PostgresStudio.Components.Pages.Storage.Storage>();
        page.WaitForAssertion(() => page.FindAll(".ms-storage-tiles").Should().ContainSingle());

        string[] values = [.. page.TextOfAll(".ms-storage-tile-value")];
        values[1].Should().Be("–");
        values[2].Should().Be("–");
        page.TextOfAll(".ms-storage-tile-note")[1].Should().Be("needs a day of statistics: 5 h so far");
    }

    [Fact]
    public async Task A_store_policy_refusal_shows_its_sentence_and_nothing_else()
    {
        FakeStorageService storage = new()
        {
            View = StorageView.Refused(DatabaseRefusal.StorePolicy, PostgresStudio.Services.Settings.StudioText.Marten.StorageStorePolicyDenial),
        };
        await using StudioComponentContext context = await ContextAsync(storage);

        var page = context.Render<PostgresStudio.Components.Pages.Storage.Storage>();
        page.WaitForAssertion(() => page.FindAll(".ms-storage-refusal").Should().ContainSingle());

        page.Find(".ms-storage-refusal").TextContent.Should().Contain("MartenStudioOptions.StoreAuthorizationPolicy");
        page.FindAll(".ms-storage-table").Should().BeEmpty();
    }

    [Fact]
    public async Task Nothing_is_read_while_prerendered_and_the_circuit_reads_once()
    {
        FakeStorageService storage = new() { View = StorageView.Refused(DatabaseRefusal.Unavailable, "unread") };
        await using StudioComponentContext context = await ContextAsync(storage);

        string html = await DatabasePagePrerenderTests.StaticAsync<PostgresStudio.Components.Pages.Storage.Storage>(context);

        html.Should().Contain("ms-loading-spinner");
        storage.Requests.Should().BeEmpty("a static render reads nothing");

        var page = context.Render<PostgresStudio.Components.Pages.Storage.Storage>();
        page.WaitForAssertion(() => page.FindAll(".ms-storage-refusal").Should().ContainSingle());
        storage.Requests.Should().ContainSingle().Which.Should().Be(context.State.ActiveScope);
    }

    private sealed class Set(IReadOnlyList<StudioTable> tables) : IStudioTableSet
    {
        public IReadOnlyList<StudioTable> Tables { get; } = tables;

        public string? Coverage => null;

        public StudioTable? Find(string schema, string table) => Tables.FirstOrDefault(x => x.Schema == schema && x.Table == table);

        public IndexOrigin OriginOf(HealthIndexRow index, StudioTable table) => IndexOrigin.Host;
    }
}
