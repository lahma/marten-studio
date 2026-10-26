using Microsoft.Extensions.DependencyInjection;

using PostgresStudio.Internal.Sql;
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
    }

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

    private static HealthHarness Harness(string? coverage)
    {
        CappedTableSource source = new(coverage);
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

    /// <summary>A table source whose set is capped - or whole, with no coverage - and lists no table.</summary>
    private sealed class CappedTableSource(string? coverage) : IHealthTableSource
    {
        public int Calls { get; private set; }

        public Task<IStudioTableSet> TablesAsync(HealthCheckContext context)
        {
            Calls++;
            return Task.FromResult<IStudioTableSet>(new Set(coverage));
        }

        private sealed class Set(string? coverage) : IStudioTableSet
        {
            public IReadOnlyList<StudioTable> Tables => [];

            public string? Coverage => coverage;

            public StudioTable? Find(string schema, string table) => null;

            public IndexOrigin OriginOf(HealthIndexRow index, StudioTable table) => IndexOrigin.Undeclared;
        }
    }
}
