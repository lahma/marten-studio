using MartenStudio.Integration.Tests.Database;
using MartenStudio.Services;
using PostgresStudio.Services.Database;
using MartenStudio.Services.Schema;

using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

using Weasel.Postgresql;
using Weasel.Postgresql.Tables;

using static MartenStudio.Integration.Tests.Health.TableHealthLive;
using PostgresStudio.Internal.Sql;
using PostgresStudio.Services;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Schema;

namespace MartenStudio.Integration.Tests.Health;

/// <summary>
/// XSO: a table a host hands to <c>StoreOptions.Storage.ExtendedSchemaObjects</c> in a schema of its own is a
/// Marten table to the tables-and-indexes checks, as <c>docs/health/tables.md</c> promises - and its schema still
/// does not become one of the store's own.
/// </summary>
/// <remarks>
/// <para>
/// The checks' table set used to split each declaration key back against <c>SchemaDeclarations.Schemas</c>, which
/// leaves such a schema out on purpose (D27: it is the "store's own schemas" the database browser shows the
/// structure of without <c>BrowseDatabase</c>, and the Schema screen lists). The split found no schema, the table
/// was skipped, and every T-check ignored it without a word. The declaration now carries its schema and name.
/// </para>
/// <para>
/// A finding about such a table is shown only to a visitor the database browser's gate admits to its schema
/// (XSO-fix, D27), so every test that expects to see one opens that gate (<see cref="Admit" />).
/// </para>
/// <para>
/// Everything here is the test's own DDL and DML, in the test's own schemas; the studio runs none of it.
/// </para>
/// </remarks>
public class ExtendedTableHealthLiveTests(PostgresFixture postgres)
{
    /// <summary>The reason a report gives for a finding withheld for its schema, in MartenStudio's words.</summary>
    private static string WithheldSchemaReason => HealthService.WithheldSchemaReasonFor(MartenStudioSettings.Of(new MartenStudioOptions()));

    /// <summary>The extended table's name, in whichever schema a test gives it.</summary>
    private const string ThingsTable = "xso_things";

    private static readonly string[] TableCodes = ["T01", "T03", "T04", "T05", "T06", "T07", "T08", "T10", "T12"];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// Half the extended table dead with autovacuum switched off for it: T07 at once, about that table by its own
    /// schema - and no remedy line, because no <c>StoreOptions</c> line the studio could name sets its storage
    /// parameters.
    /// </summary>
    [PostgresFact]
    [HealthFires("T07")]
    public async Task An_extended_table_in_a_schema_of_its_own_half_dead_with_autovacuum_off_is_T07()
    {
        string extended = await postgres.CreateSchemaAsync("health_xso_fires_ext", Token);
        await using HealthLiveHost host = await HealthLiveHost.CreateAsync(
            postgres,
            "health_xso_fires",
            store: options => options.Storage.ExtendedSchemaObjects.Add(Things(extended)),
            studio: options => Admit(options, extended));
        await ApplyAsync(host.Store);

        await MakeHalfDeadAsync(extended);

        HealthReport report = await ReportAsync(host);

        string key = SchemaKey.For(extended, ThingsTable);
        // G2 (W5-V1): never vacuumed since its load, the table is behind on its inserts too; this is the dead-row finding.
        HealthReportFinding finding = Of(report, "T07").Should().ContainSingle(static x => x.Finding.Variant == VacuumBehindCheck.DeadRowsVariant).Subject;

        finding.Finding.Subject.Should().Be(key);
        finding.Evidence.Should().Contain(new HealthEvidence("Table", "the Marten-managed table " + key));
        finding.Finding.WhyItMatters.Should().Contain("switched off for this table (autovacuum_enabled = false)")
            .And.Contain("re-enable autovacuum on this table", "a table the host declares itself gets its way back in words, never as DDL");
        finding.Finding.GatedSchemas.Should().Equal(extended);
        finding.Finding.Remedy.Should().BeNull("a Schema.For<T>() line fits a document table only");
        finding.Finding.DocumentTypes.Should().BeEmpty();
        host.Logs.WarningsOrWorse.Should().BeEmpty();

        SchemaDeclarationReader.Read(host.Store.Options).Schemas.Should().NotContain(extended,
            "the checks read the declaration's own pair; the store's own schemas did not grow to hold it (D27)");
    }

    /// <summary>
    /// The anti-cry-wolf half: the same table, healthy - two thousand live rows, analysed, autovacuum on - raises
    /// nothing in two sweeps an interval apart, and T07 and T08 still looked. The visitor is admitted to the schema
    /// (<see cref="Admit" />) and nothing is withheld from them, so "nothing shown" here is "nothing found", never a
    /// finding the browser's gate kept back.
    /// </summary>
    [PostgresFact]
    [HealthQuiet("T07")]
    [HealthQuiet("T08")]
    public async Task The_same_extended_table_healthy_raises_nothing()
    {
        ManualClock clock = new(Start);
        string extended = await postgres.CreateSchemaAsync("health_xso_quiet_ext", Token);
        await using HealthLiveHost host = await HostAsync(
            postgres,
            "health_xso_quiet",
            clock,
            options => options.Storage.ExtendedSchemaObjects.Add(Things(extended)),
            studio: options => Admit(options, extended));
        await ApplyAsync(host.Store);

        string table = SqlIdentifier.Qualify(extended, ThingsTable);
        await ExecuteAsync(
            postgres,
            "insert into " + table + " (id, rank) select g, g from generate_series(1, 2000) as g",
            "analyze " + table);

        HealthReport first = await ReportAsync(host);
        clock.Advance(TimeSpan.FromMinutes(2));
        HealthReport second = await ReportAsync(host);

        foreach (HealthReport report in new[] { first, second })
        {
            report.Findings.Should().NotContain(
                x => TableCodes.Contains(x.Code) && x.Finding.Subject != null && x.Finding.Subject.StartsWith(extended + ".", StringComparison.Ordinal),
                string.Join(" | ", report.Findings.Select(static x => x.Finding.Title)));
            // Quiet is not withheld. Asked of the table checks and of the browser's reasons rather than of WithheldCount as a
            // whole, which also counts an Activity-gated connections finding that another test's sessions may cause.
            // (T02, T22 and T23 are behind the activity gate, whose rows read "not shown to you" short of it, so they are left out.)
            report.Checks.Where(static x => TableCodes.Contains(x.Code)).Should().OnlyContain(static x => !x.FindingsWithheld,
                "a table check's silence is not a finding kept back: {0}", report.WithheldReason);
            (report.WithheldReason ?? string.Empty).Should().NotContain(WithheldSchemaReason)
                .And.NotContain(HealthService.UncheckedSchemaReason);
        }

        foreach (string code in (string[]) ["T07", "T08"])
        {
            HealthCheckStatus status = HealthLiveHost.Status(second, code);
            status.Outcome.Should().Be(HealthCheckOutcome.Checked, "{0} is quiet because it looked: {1}", code, status.Problem);
            status.Statements.Should().NotBeEmpty("{0} read the catalog", code);
            status.FindingsWithheld.Should().BeFalse("{0}'s silence is not a finding kept back", code);
        }

        host.Logs.WarningsOrWorse.Should().BeEmpty();
    }

    /// <summary>
    /// A finding about the extended table needs what one about a document table needs first: the store policy for the
    /// database as a whole. A visitor it refuses - here one it passes only for a tenant - is shown no T07 at all and
    /// a check row that says findings were not shown, and nothing on the report names the table; the same visitor,
    /// once the policy passes the database, is shown it (the browser's gate admits the schema throughout).
    /// </summary>
    [PostgresFact]
    public async Task A_visitor_the_store_policy_refuses_the_database_as_a_whole_is_not_shown_the_finding()
    {
        bool wholeDatabase = false;
        ResourcePolicy policy = new(resource => wholeDatabase || resource.TenantId == "acme");

        string extended = await postgres.CreateSchemaAsync("health_xso_gated_ext", Token);
        await using HealthLiveHost host = await HealthLiveHost.CreateAsync(
            postgres,
            "health_xso_gated",
            store: options => options.Storage.ExtendedSchemaObjects.Add(Things(extended)),
            studio: options =>
            {
                options.StoreAuthorizationPolicy = "store";
                options.KnownTenantIds.Add("acme");
                Admit(options, extended);
            },
            services: services => services.AddSingleton<IAuthorizationService>(policy));
        await ApplyAsync(host.Store);

        await MakeHalfDeadAsync(extended);

        HealthReport refused = await ReportForAsync(host, host.Scope with { TenantId = "acme" });

        refused.Findings.Should().NotContain(static x => x.Code == "T07");
        HealthLiveHost.Status(refused, "T07").FindingsWithheld.Should().BeTrue("a withheld finding is never a \"nothing found\"");

        foreach (HealthReportFinding finding in refused.Findings)
        {
            string text = finding.Finding.Title + " " + finding.Finding.WhyItMatters + " " + finding.Finding.Subject + " "
                + string.Join(" ", finding.Evidence.Select(static x => x.Value));
            text.Should().NotContain(ThingsTable, "{0} names nothing a visitor short of the database-wide gate may see", finding.Code);
        }

        foreach (HealthCheckStatus status in refused.Checks)
        {
            (status.Note ?? string.Empty).Should().NotContain(ThingsTable);
        }

        wholeDatabase = true;
        HealthReport passed = await ReportForAsync(host, host.Scope);

        Of(passed, "T07").Should().Contain(x => x.Finding.Subject == SchemaKey.For(extended, ThingsTable),
            "the condition is real: the refused visitor's report was withheld, not quiet");
    }

    /// <summary>
    /// XSO-fix: past the store policy, a finding naming the extended table needs the database browser's gate for its
    /// schema too, because the browser withholds that schema - and masks its name - from anybody short of it (D27). A
    /// visitor the store policy passes for the whole database, with <c>BrowseDatabase</c> off, or on with
    /// <c>BrowsableSchemas</c> naming only another schema, gets no T07 for the table, a check row marked withheld, the
    /// browser's reason on the report's notice, and nothing anywhere that names the table; with the schema admitted, the
    /// finding is shown as it is to a document table's visitor.
    /// </summary>
    [PostgresTheory]
    [InlineData("closed", false, false)]
    [InlineData("other", true, false)]
    [InlineData("open", true, true)]
    public async Task A_finding_about_the_table_is_shown_only_past_the_database_browsers_gate_for_its_schema(string name, bool browse, bool admitted)
    {
        string extended = await postgres.CreateSchemaAsync("health_xso_browse_" + name + "_ext", Token);
        await using HealthLiveHost host = await HealthLiveHost.CreateAsync(
            postgres,
            "health_xso_browse_" + name,
            store: options => options.Storage.ExtendedSchemaObjects.Add(Things(extended)),
            studio: options =>
            {
                options.Capabilities.BrowseDatabase = browse;
                options.BrowsableSchemas.Add(admitted ? extended : "public");
            });
        await ApplyAsync(host.Store);

        await MakeHalfDeadAsync(extended);

        HealthReport report = await ReportAsync(host);
        string key = SchemaKey.For(extended, ThingsTable);
        HealthCheckStatus row = HealthLiveHost.Status(report, "T07");

        if (admitted)
        {
            Of(report, "T07").Should().ContainSingle(x => x.Finding.Subject == key && x.Finding.Variant == VacuumBehindCheck.DeadRowsVariant);
            row.FindingsWithheld.Should().BeFalse();
            return;
        }

        Of(report, "T07").Should().BeEmpty();
        row.FindingCount.Should().Be(0);
        row.FindingsWithheld.Should().BeTrue("a withheld finding is never a \"nothing found\"");
        report.WithheldCount.Should().BeGreaterThan(0);
        report.WithheldReason.Should().Contain(WithheldSchemaReason);

        foreach (HealthReportFinding finding in report.Findings)
        {
            string text = finding.Finding.Title + " " + finding.Finding.WhyItMatters + " " + finding.Finding.Subject + " "
                + string.Join(" ", finding.Evidence.Select(static x => x.Value));
            text.Should().NotContain(extended, "{0} names nothing in a schema the browser withholds", finding.Code);
        }

        foreach (HealthCheckStatus status in report.Checks)
        {
            (status.Note ?? string.Empty).Should().NotContain(extended).And.NotContain(ThingsTable);
        }
    }

    /// <summary>
    /// The other half of D27's boundary, read through the studio's own screens: the extended table's schema is not
    /// one of the store's own for the database browser (whose structure any visitor sees) nor for the Schema
    /// screen's Tables tab - the known limit <c>README.md</c>'s Schema section states.
    /// </summary>
    [PostgresFact]
    public async Task The_extended_tables_schema_stays_outside_the_store_schemas_of_the_browser_and_the_Schema_screen()
    {
        string extended = await postgres.CreateSchemaAsync("health_xso_scope_ext", Token);
        await using HealthLiveHost host = await HealthLiveHost.CreateAsync(
            postgres,
            "health_xso_scope",
            store: options => options.Storage.ExtendedSchemaObjects.Add(Things(extended)));
        await ApplyAsync(host.Store);

        await using AsyncServiceScope circuit = host.Services.CreateAsyncScope();

        DatabaseBrowserOverview overview = await circuit.ServiceProvider.GetRequiredService<IDatabaseObjectService>()
            .GetOverviewAsync(host.Scope, Token);

        overview.Access.StoreSchemas.Should().Contain(host.Schema).And.NotContain(extended);
        overview.Schemas.Should().NotContain(x => x.Name == extended, "BrowseDatabase is off, so a schema not the store's own is withheld");
        overview.Access.WithheldSchemaCount.Should().BeGreaterThan(0);

        SchemaTables tables = await circuit.ServiceProvider.GetRequiredService<ISchemaDataService>()
            .TablesAsync(host.Scope, Token);

        tables.Schemas.Should().Contain(host.Schema).And.NotContain(extended);
        tables.Tables.Should().NotContain(x => x.Schema == extended);
    }

    /// <summary>Opens the database browser's gate for <paramref name="schema" />, so its findings are this visitor's to see.</summary>
    private static void Admit(MartenStudioOptions options, string schema)
    {
        options.Capabilities.BrowseDatabase = true;
        options.BrowsableSchemas.Add(schema);
    }

    /// <summary>An <c>ExtendedSchemaObjects</c> table: an <c>int</c> key and one column.</summary>
    private static Table Things(string schema)
    {
        Table table = new(new PostgresqlObjectName(schema, ThingsTable, SchemaUtils.IdentifierUsage.General));
        table.AddColumn<int>("id").AsPrimaryKey();
        table.AddColumn<int>("rank");
        return table;
    }

    /// <summary>T07's condition: autovacuum off for the table, 24,000 rows, half of them deleted.</summary>
    private Task MakeHalfDeadAsync(string schema)
    {
        string table = SqlIdentifier.Qualify(schema, ThingsTable);

        return ExecuteAsync(
            postgres,
            "alter table " + table + " set (autovacuum_enabled = false)",
            "insert into " + table + " (id, rank) select g, g from generate_series(1, 24000) as g",
            "delete from " + table + " where rank % 2 = 0");
    }

    /// <summary>A report for <paramref name="scope" />, through a circuit-shaped scope of the host's own container.</summary>
    private static async Task<HealthReport> ReportForAsync(HealthLiveHost host, StudioScope scope)
    {
        await using AsyncServiceScope circuit = host.Services.CreateAsyncScope();

        return await circuit.ServiceProvider.GetRequiredService<IHealthService>()
            .GetReportAsync(scope, HealthReportRequest.Regular, Token);
    }
}
