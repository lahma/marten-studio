using Bunit;

using MartenStudio.Services;
using MartenStudio.Services.Configuration;
using PostgresStudio.Services.Database;
using MartenStudio.Services.Schema;
using MartenStudio.Tests.Components;
using MartenStudio.Tests.Health;

using Microsoft.Extensions.DependencyInjection;

using SchemaPage = MartenStudio.Components.Pages.Schema.Schema;
using PostgresStudio.Services;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Schema;

namespace MartenStudio.Tests.Schema;

/// <summary>
/// F6 on the Schema screen: the Tables tab's HOT %, dead %, last autovacuum, TOAST and storage-parameter drift,
/// and the Indexes tab's invalid, redundant and unused marks from the advisor's cached findings.
/// </summary>
public class SchemaHealthColumnsPageTests
{
    private static readonly DatabaseObjectOwnership Customer = new(DatabaseObjectOwner.MartenDocument, "default", "customer");

    private static TableStats Stats(Func<TableStats, TableStats>? shape = null)
    {
        TableStats table = new("studio", "mt_doc_customer", 2_000_000, 1_000_000, 500_000, 40_000, 40_000, 20_000, 4, 500,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "customer", "Customer", Customer)
        {
            Updates = 20_000,
            HotUpdates = 2_000,
            LastAutovacuum = new DateTimeOffset(2026, 10, 14, 3, 0, 0, TimeSpan.Zero),
            ToastBytes = 3 * 1024 * 1024,
            DeclaredParametersKnown = true,
            StorageParameters =
            [
                new StorageParameterState("fillfactor", "80", "100", StorageParameterStatus.Drift),
                new StorageParameterState("autovacuum_enabled", null, "false", StorageParameterStatus.SetOutsideMarten),
            ],
        };

        return shape is null ? table : shape(table);
    }

    [Fact]
    public void The_Tables_tab_shows_dead_and_HOT_shares_TOAST_last_autovacuum_and_marks_what_the_advisor_would()
    {
        using var context = NewContext(out FakeSchemaDataService schema, out _);
        schema.Tables = new SchemaTables([Stats()], ["studio"], 4_000_000, null);

        context.Navigate("/marten/schema?tab=tables");
        var page = context.Render<SchemaPage>();

        page.Find(".ms-schema-dead").TextContent.Trim().Should().Be("33%");
        page.Find(".ms-schema-dead").ClassList.Should().Contain("ms-schema-share-high", "past T07's 20% over 10,000 dead rows");
        page.Find(".ms-schema-hot").TextContent.Trim().Should().Be("10%");
        page.Find(".ms-schema-hot").ClassList.Should().Contain("ms-schema-share-high", "under T06's half over 10,000 updates");
        page.Find(".ms-schema-toast").TextContent.Trim().Should().Be("3.0 MiB");
        page.Find(".ms-schema-autovacuum").TextContent.Should().Contain("2026-10-14");
        page.TextOfAll("th").Should().Contain(["TOAST", "Dead %", "HOT %", "Last autovacuum", "Storage parameters"]);
    }

    [Fact]
    public void A_declared_parameter_the_table_differs_from_is_drift_and_one_set_outside_Marten_is_only_listed()
    {
        using var context = NewContext(out FakeSchemaDataService schema, out _);
        schema.Tables = new SchemaTables([Stats()], ["studio"], 4_000_000, null);

        context.Navigate("/marten/schema?tab=tables");
        var page = context.Render<SchemaPage>();

        var drift = page.Find(".ms-schema-parameter-drift");
        drift.TextContent.Trim().Should().Be("fillfactor: declared 80, table 100");
        drift.GetAttribute("title").Should().Contain("the next apply sets it");

        var outside = page.Find("[data-status='SetOutsideMarten']");
        outside.TextContent.Trim().Should().Be("autovacuum_enabled=false");
        outside.GetAttribute("title").Should().Contain("unless an apply rebuilds the table", "hard rule 14: no apply is promised to leave it");
    }

    /// <summary>
    /// The note says what the host's Marten can do, read off the probe - never off the rows, since a page of
    /// tables none of which is a document table says nothing about it. Each CI leg proves its own half: the floor
    /// (9.39) cannot declare parameters, the latest-tested (9.50) can.
    /// </summary>
    [Fact]
    public void The_storage_parameter_note_follows_the_host_s_Marten_not_the_rows()
    {
        using var context = NewContext(out FakeSchemaDataService schema, out _);
        schema.Tables = new SchemaTables(
            [Stats(static x => x with
            {
                DeclaredParametersKnown = false,
                StorageParameters = [new StorageParameterState("fillfactor", null, "90", StorageParameterStatus.SetOutsideMarten)],
            })],
            ["studio"],
            4_000_000,
            null);

        context.Navigate("/marten/schema?tab=tables");
        var page = context.Render<SchemaPage>();

        if (RuntimeFeature.DeclaredStorageParameters.IsAvailable(MartenRuntime.Current))
        {
            page.Markup.Should().Contain("each beside what a document mapping declares").And.NotContain("nothing to compare",
                "this Marten can declare them, whatever the rows on this page happen to be");
        }
        else
        {
            page.Markup.Should().Contain("from Marten 9.47").And.Contain("nothing to compare");
        }

        page.FindAll(".ms-schema-parameter-drift").Should().BeEmpty();
    }

    [Fact]
    public void A_parameter_the_partitions_disagree_on_says_so_and_names_no_partition()
    {
        using var context = NewContext(out FakeSchemaDataService schema, out _);
        schema.Tables = new SchemaTables(
            [Stats(static x => x with
            {
                StorageParameters = [new StorageParameterState("fillfactor", "80", null, StorageParameterStatus.PartitionsDiffer)],
            })],
            ["studio"],
            4_000_000,
            null);

        context.Navigate("/marten/schema?tab=tables");
        var page = context.Render<SchemaPage>();

        var differ = page.Find(".ms-schema-parameter-differ");
        differ.TextContent.Trim().Should().Be("fillfactor: partitions differ");
        differ.GetAttribute("data-status").Should().Be(nameof(StorageParameterStatus.PartitionsDiffer));
        differ.GetAttribute("title").Should().Contain("the mapping declares 80").And.Contain("partitions are not named");
        page.FindAll(".ms-schema-parameter-drift").Should().BeEmpty("a disagreement is not a drift the studio can state for the table");
    }

    [Fact]
    public void A_tenancy_registry_table_withholds_its_shares_with_its_other_figures()
    {
        using var context = NewContext(out FakeSchemaDataService schema, out _);
        schema.Tables = new SchemaTables(
            [Stats(static x => x with { FiguresWithheld = true, LiveRows = null, DeadRows = null, Updates = null, HotUpdates = null })],
            ["studio"],
            4_000_000,
            null);

        context.Navigate("/marten/schema?tab=tables");
        var page = context.Render<SchemaPage>();

        page.FindAll(".ms-schema-withheld").Should().HaveCount(7, "rows, live, dead, dead %, HOT % and both scan counters are counts of tenants here");
        page.FindAll(".ms-schema-dead").Should().BeEmpty();
    }

    // -----------------------------------------------------------------------------------------------
    // Indexes: the advisor's marks
    // -----------------------------------------------------------------------------------------------

    private static SchemaIndexes Indexes() => new(
        [
            new IndexInfo("studio", "mt_doc_customer", "mt_doc_customer_idx_name", "CREATE INDEX ...", 8192, 3, 3, false, false, true, "customer", null),
            new IndexInfo("studio", "mt_doc_customer", "mt_doc_customer_idx_email", "CREATE INDEX ...", 8192, 0, 0, false, false, true, "customer", null),
        ],
        [],
        [],
        null);

    [Fact]
    public void The_Indexes_tab_marks_an_invalid_and_a_redundant_index_from_the_cached_findings_and_links_each_to_its_finding()
    {
        using var context = NewContext(out FakeSchemaDataService schema, out MarksHealthService health);
        schema.Indexes = Indexes();
        health.Cached =
        [
            Cached("T03", HealthSeverity.Critical, "studio.mt_doc_customer.mt_doc_customer_idx_name"),
            Cached("T04", HealthSeverity.Warning, "studio.mt_doc_customer.mt_doc_customer_idx_email"),
            Cached("T03", HealthSeverity.Critical, "studio.mt_doc_customer#partitions"),
        ];

        context.Navigate("/marten/schema?tab=indexes");
        var page = context.Render<SchemaPage>();

        var invalid = page.Find(".ms-schema-health-mark[data-code='T03']");
        invalid.TextContent.Trim().Should().Be("invalid");
        invalid.ClassList.Should().Contain("ms-index-flag-danger");
        invalid.GetAttribute("href").Should().Contain("health").And.Contain("#finding-");

        page.Find(".ms-schema-health-mark[data-code='T04']").TextContent.Trim().Should().Be("redundant");
        page.FindAll(".ms-schema-health-mark").Should().HaveCount(2, "the partitions' finding is about no row on this tab");
        page.Find(".ms-schema-summary").TextContent.Should().Contain("1 invalid").And.Contain("1 redundant");
        page.Markup.Should().Contain("health advisor's");

        health.CachedCodes.Should().BeEquivalentTo(SchemaHealthMarks.IndexCodes);
        health.Reports.Should().Be(0, "a tab never starts a sweep");
    }

    [Fact]
    public void With_nothing_cached_the_Indexes_tab_draws_no_mark_and_claims_nothing()
    {
        using var context = NewContext(out FakeSchemaDataService schema, out MarksHealthService health);
        schema.Indexes = Indexes();

        context.Navigate("/marten/schema?tab=indexes");
        var page = context.Render<SchemaPage>();

        page.FindAll(".ms-schema-health-mark").Should().BeEmpty();
        page.Markup.Should().NotContain("health advisor's").And.NotContain("healthy");
        health.Reports.Should().Be(0);
    }

    [Fact]
    public void A_failing_health_read_leaves_the_index_list_standing()
    {
        using var context = NewContext(out FakeSchemaDataService schema, out MarksHealthService health);
        schema.Indexes = Indexes();
        health.Failure = new InvalidOperationException("the cache is gone");

        context.Navigate("/marten/schema?tab=indexes");
        var page = context.Render<SchemaPage>();

        page.Markup.Should().Contain("mt_doc_customer_idx_name");
        page.FindAll(".ms-schema-health-mark").Should().BeEmpty();
    }

    private static HealthReportFinding Cached(string code, HealthSeverity severity, string subject)
    {
        HealthFinding finding = FakeHealthService.Finding(code, severity, code + " about " + subject, HealthArea.Tables, subject);

        return new HealthReportFinding(
            finding,
            finding.Evidence,
            HealthService.MuteKeyOf("default", "localhost.marten", finding),
            "https://example.invalid/docs/health/tables.md#" + code.ToLowerInvariant(),
            RemedyAvailable: true,
            HostRuns: null);
    }

    private static StudioComponentContext NewContext(out FakeSchemaDataService schema, out MarksHealthService health)
    {
        var fake = new FakeSchemaDataService();
        var marks = new MarksHealthService();
        var context = new StudioComponentContext(services => services.AddSingleton<ISchemaDataService>(fake));
        context.WithStores("default");

        // Registered after the context's own fake, so this one is what the tab resolves.
        context.Services.AddSingleton<IHealthService>(marks);

        schema = fake;
        health = marks;
        return context;
    }

    /// <summary>
    /// What the Indexes tab is told: the cached findings, said outright, and a count of anything that would
    /// start a sweep.
    /// </summary>
    internal sealed class MarksHealthService : IHealthService
    {
        public IReadOnlyList<HealthReportFinding> Cached { get; set; } = [];

        public Exception? Failure { get; set; }

        public IReadOnlyCollection<string> CachedCodes { get; private set; } = [];

        public int Reports { get; private set; }

        public Task<HealthReport> GetReportAsync(StudioScope scope, HealthReportRequest request, CancellationToken cancellationToken = default)
        {
            Reports++;
            return Task.FromResult(new HealthReport());
        }

        public Task<HealthGlance?> GlanceAsync(StudioScope scope, CancellationToken cancellationToken = default) =>
            Task.FromResult<HealthGlance?>(null);

        public Task<IReadOnlyList<HealthReportFinding>> CachedFindingsAsync(
            StudioScope scope,
            IReadOnlyCollection<string> codes,
            CancellationToken cancellationToken = default)
        {
            CachedCodes = codes;

            return Failure is not null
                ? Task.FromException<IReadOnlyList<HealthReportFinding>>(Failure)
                : Task.FromResult<IReadOnlyList<HealthReportFinding>>([.. Cached.Where(x => codes.Contains(x.Code))]);
        }
    }
}
