using Microsoft.Extensions.DependencyInjection;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Sources;

namespace MartenStudio.Tests.Health;

/// <summary>
/// What a capped table set left out is said by a real sweep (design R5, W4-polish): the sweeper reads the set's
/// <see cref="IStudioTableSet.Coverage" /> from its memo once its gatherers ran and adds "Judged: …" to every Tables-area
/// check's status note and to every caveat of its findings - and to nothing else, and to nothing when the set is whole.
/// </summary>
/// <remarks>
/// The set comes from a stand-in <see cref="IHealthTableSource" /> registered before <c>AddMartenStudio()</c>, whose
/// <c>TryAdd</c> then keeps it. It lists no table, so T07 - a real check, gathering through <see cref="HealthTables" /> -
/// opens no connection against the harness's refused one: what is under test is the sweep carrying the set's coverage,
/// not the cap, which <c>PostgresSeamTests</c> covers.
/// </remarks>
public class HealthSweeperCoverageTests
{
    private const string Coverage = "the 2 largest of 3,412 tables by estimated size (a sweep judges at most 2)";

    private const string Judged = "Judged: " + Coverage + ".";

    private static readonly Type[] Checks = [typeof(VacuumBehindCheck), typeof(ScriptedCatalogCheck), typeof(ScriptedConfigurationCheck)];

    [Fact]
    public async Task A_capped_sets_coverage_is_said_in_a_real_sweeps_tables_checks_and_findings()
    {
        await using HealthHarness harness = Harness(Coverage);

        HealthSweep sweep = await harness.SweepAsync();

        HealthCheckResult vacuum = sweep.Results.Single(static x => x.Check.Code == "T07");
        vacuum.Outcome.Should().Be(HealthCheckOutcome.Checked, "a set with no table opens no connection");
        vacuum.Findings.Should().BeEmpty();
        vacuum.Note.Should().Be(Judged, "a capped set's quiet is never a clean bill for the tables it did not judge");

        HealthCheckResult catalog = sweep.Results.Single(static x => x.Check.Code == "T90");
        catalog.Findings.Should().HaveCount(2);
        catalog.Findings.Single(static x => x.Subject == "with-caveat").Caveat.Should().Be("Read in pg_stat_user_tables. " + Judged);
        catalog.Findings.Single(static x => x.Subject == "without-caveat").Caveat.Should().Be(Judged);
        catalog.Note.Should().Be(Judged, "the status row of a check that found something says it too");

        HealthCheckResult configuration = sweep.Results.Single(static x => x.Check.Code == "K90");
        configuration.Findings.Single().Caveat.Should().Be("Read from the configuration.", "only a Tables-area check judges the table set");
        configuration.Note.Should().BeNull();

        sweep.Results.Should().OnlyContain(static x => x.WithinOwnSchemas == null,
            "a set that reads no schema beyond the source's own has one reading for every visitor");
    }

    /// <summary>
    /// G1M F6, H1: a set that read schemas only <c>BrowsableSchemas</c> admits counts what they hold, and that count is the
    /// database browser's to show. So each Tables-area result carries a second reading - the same findings, the coverage
    /// counted over the source's own schemas alone - and the report picks one per visitor: past the browser's gate the
    /// whole count, short of it (or with no gate to ask) the own schemas' alone.
    /// </summary>
    [Fact]
    public async Task A_set_that_reads_beyond_the_sources_own_schemas_is_read_per_visitor()
    {
        const string Own = "the 1 largest of 4 tables in the data source's own schemas by estimated size (a sweep judges at most 2)";
        const string JudgedOwn = "Judged: " + Own + ".";

        await using HealthHarness harness = Harness(Coverage, Own);

        HealthSweep sweep = await harness.SweepAsync();

        HealthCheckResult catalog = sweep.Results.Single(static x => x.Check.Code == "T90");
        catalog.Note.Should().Be(Judged, "the sweep's own reading is the whole count - the host's diagnostics API reads it (D31)");

        catalog.WithinOwnSchemas.Should().NotBeNull();
        HealthCheckResult own = catalog.WithinOwnSchemas!;
        own.Note.Should().Be(JudgedOwn);
        own.Findings.Select(static x => x.Key).Should().Equal(catalog.Findings.Select(static x => x.Key), "the same findings, only counted differently");
        own.Findings.Single(static x => x.Subject == "with-caveat").Caveat.Should().Be("Read in pg_stat_user_tables. " + JudgedOwn);
        own.Findings.Single(static x => x.Subject == "without-caveat").Caveat.Should().Be(JudgedOwn);
        own.WithinOwnSchemas.Should().BeNull();

        sweep.Results.Single(static x => x.Check.Code == "K90").WithinOwnSchemas.Should().BeNull("only a Tables-area check judges the set");

        static string? NoteOf(IReadOnlyList<HealthCheckResult> results) => results.Single(static x => x.Check.Code == "T90").Note;

        NoteOf(await HealthService.AsVisitorReadsAsync(sweep.Results, browserGate: null)).Should().Be(JudgedOwn, "no gate to ask is the own reading");
        NoteOf(await HealthService.AsVisitorReadsAsync(sweep.Results, () => Task.FromResult<DatabaseGateRead?>(Gate(open: false))))
            .Should().Be(JudgedOwn, "short of the browser's gate, the count of what the other schemas hold is withheld");
        NoteOf(await HealthService.AsVisitorReadsAsync(sweep.Results, () => Task.FromResult<DatabaseGateRead?>(new DatabaseGateRead(null, DatabaseRefusal.Unavailable, "x"))))
            .Should().Be(JudgedOwn, "a gate that cannot be read is short of it (fail closed)");
        NoteOf(await HealthService.AsVisitorReadsAsync(sweep.Results, () => Task.FromResult<DatabaseGateRead?>(Gate(open: true))))
            .Should().Be(Judged, "past it, the whole count");
    }

    /// <summary>A visitor's browser gate over <c>"*"</c>: open, or shut by the write policy.</summary>
    private static DatabaseGateRead Gate(bool open) => new(
        new DatabaseGate(
            ["public"],
            [new CatalogSchema("public", true, false), new CatalogSchema("legacy", true, false)],
            ["*"],
            capabilityEnabled: true,
            readOnly: false,
            authorized: open,
            new DatabaseObjectClassifier([])),
        DatabaseRefusal.None,
        null);

    [Fact]
    public async Task A_whole_set_adds_nothing_to_any_check()
    {
        await using HealthHarness harness = Harness(coverage: null);

        HealthSweep sweep = await harness.SweepAsync();

        sweep.Results.Single(static x => x.Check.Code == "T07").Note.Should().BeNull();

        HealthCheckResult catalog = sweep.Results.Single(static x => x.Check.Code == "T90");
        catalog.Note.Should().BeNull();
        catalog.Findings.Single(static x => x.Subject == "with-caveat").Caveat.Should().Be("Read in pg_stat_user_tables.");
        catalog.Findings.Single(static x => x.Subject == "without-caveat").Caveat.Should().BeNull();
        sweep.Findings.Should().HaveCount(3)
            .And.OnlyContain(static x => x.Caveat == null || !x.Caveat.Contains("Judged", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_set_no_check_asked_for_says_nothing()
    {
        // No Tables-area check that reads the set: the memo never holds one, so there is no coverage to say.
        await using HealthHarness harness = HealthHarness.Create(
            checks: [typeof(ScriptedCatalogCheck)],
            before: static services => services.AddSingleton<IHealthTableSource>(new CappedTableSource(Coverage)));
        harness.Script.Finds("T90", HealthSeverity.Warning, shape: static x => x.Subject = "unread");

        HealthSweep sweep = await harness.SweepAsync();

        sweep.Results.Single().Findings.Single().Caveat.Should().BeNull();
        harness.Services.GetRequiredService<IHealthTableSource>().Should().BeOfType<CappedTableSource>()
            .Which.Calls.Should().Be(0, "the set is read only by a check that asks for it");
    }

    private static HealthHarness Harness(string? coverage, string? ownSchemasCoverage = null)
    {
        CappedTableSource source = new(coverage, ownSchemasCoverage ?? coverage);
        HealthHarness harness = HealthHarness.Create(
            checks: Checks,
            before: services => services.AddSingleton<IHealthTableSource>(source));

        harness.Script.Judge["T90"] = static (_, _) => new HealthVerdict(
        [
            Finding("T90", "with-caveat", "Read in pg_stat_user_tables."),
            Finding("T90", "without-caveat", null),
        ]);
        harness.Script.Judge["K90"] = static (_, _) => HealthVerdict.Of(Finding("K90", "configured", "Read from the configuration."));

        return harness;
    }

    private static HealthFinding Finding(string code, string subject, string? caveat) => new()
    {
        Code = code,
        Severity = HealthSeverity.Warning,
        Subject = subject,
        Title = "Scripted " + subject,
        WhyItMatters = "Scripted.",
        Caveat = caveat,
    };

    /// <summary>
    /// A table source whose set is capped - or whole, with no coverage - and lists no table; its coverage over the source's
    /// own schemas alone is <paramref name="coverage" /> unless the test says otherwise.
    /// </summary>
    private sealed class CappedTableSource(string? coverage, string? ownSchemasCoverage) : IHealthTableSource
    {
        public CappedTableSource(string? coverage)
            : this(coverage, coverage)
        {
        }

        public int Calls { get; private set; }

        public Task<IStudioTableSet> TablesAsync(HealthCheckContext context)
        {
            Calls++;
            return Task.FromResult<IStudioTableSet>(new Set(coverage, ownSchemasCoverage));
        }

        private sealed class Set(string? coverage, string? ownSchemasCoverage) : IStudioTableSet
        {
            public IReadOnlyList<StudioTable> Tables => [];

            public string? Coverage => coverage;

            public string? OwnSchemasCoverage => ownSchemasCoverage;

            public StudioTable? Find(string schema, string table) => null;

            public IndexOrigin OriginOf(HealthIndexRow index, StudioTable table) => IndexOrigin.Undeclared;
        }
    }
}
